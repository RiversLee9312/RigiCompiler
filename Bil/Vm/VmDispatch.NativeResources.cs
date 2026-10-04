using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace RigiCompiler.Bil.Vm
{
    internal sealed partial class VmDispatch
    {
        // NativeResources 职责；与主文件共享同一类型、字段及生命周期。

        internal VmValue NativeRcRetain(IReadOnlyList<VmValue> args)
        {
            var token = RequireI64("rigi_native_rc_retain", args, 0);
            lock (_nativeRcGate)
            {
                if (!_nativeRcStrong.TryGetValue(token, out var strong))
                {
                    return new VmI32(0);
                }
                if (strong == ulong.MaxValue)
                {
                    throw new VmException("NativeRc 强引用计数溢出");
                }
                _nativeRcStrong[token] = strong + 1;
                return new VmI32(1);
            }
        }

        internal VmValue NativeRcRelease(IReadOnlyList<VmValue> args)
        {
            NativeRcReleaseCore(RequireI64("rigi_native_rc_release", args, 0));
            return VmVoid.Instance;
        }

        private void NativeRcReleaseCore(long token)
        {
            lock (_nativeRcGate)
            {
                if (!_nativeRcStrong.TryGetValue(token, out var strong) || strong == 0)
                {
                    throw new VmException("NativeRc release 收到无效或已释放 token");
                }
                if (strong > 1)
                {
                    _nativeRcStrong[token] = strong - 1;
                    return;
                }
                _nativeRcStrong.Remove(token);
                // 施工块 7-2：fs 句柄的归零析构（关闭流/目录枚举器 + 摘
                // 除记录；锁序 _nativeRcGate → _fsGate 与 FsOpen 一致）。
                // close 错误不上报——持久化错误归 flush 面（§4.5.6），与
                // native 侧 NativeRc 析构回调同口径
                lock (_fsGate)
                {
                    if (_fsFiles.TryGetValue(token, out var file))
                    {
                        _fsFiles.Remove(token);
                        if (file.Resource is FileStream stream)
                        {
                            stream.Dispose();
                        }
                        else if (file.Resource is DirState dir)
                        {
                            dir.Enumerator.Dispose();
                        }
                    }
                }
            }
        }

        internal long SyncMutexCreate()
        {
            var handle = NewHandle();
            // SemaphoreSlim(1,1)：非重入、跨线程，对齐 rigi_rt 同步
            // Mutex 原语（不得跨挂起点持有）
            _mutexes[handle] = new SemaphoreSlim(1, 1);
            return handle;
        }

        // CLR 终结器线程仅操作并发登记册和独占资源，不进入解释器。
        internal void ReleaseOwnedResources(long gate, long coroutine)
        {
            if (gate != 0 && _mutexes.TryRemove(gate, out var mutex)) mutex.Dispose();
            if (coroutine != 0) _completedCoroutines.TryRemove(coroutine, out _);
            if (coroutine != 0) _completedStrong.TryRemove(coroutine, out _);
        }

        internal VmValue SyncMutexCreate(IReadOnlyList<VmValue> args)
        {
            return new VmI64(SyncMutexCreate());
        }

        internal VmValue SyncMutexAcquire(IReadOnlyList<VmValue> args)
        {
            var handle = RequireI64("rigi_sync_mutex_acquire", args, 0);
            while (!_mutexes[handle].Wait(100)) _context.CheckStepLimit();
            return VmVoid.Instance;
        }

        // 预算耗尽是解释器终止，不再执行 Rigi 终态回调（它们也需要步数）。
        // 阻塞 Worker 通过 CheckStepLimit 自行退出，再解除计时器对上下文的根。
        internal void StopAfterStepLimit()
        {
            foreach (var worker in _workers.Values)
                if (worker.Thread != null && worker.Thread != Thread.CurrentThread)
                    worker.Thread.Join();
            foreach (var coroutine in _coroutines.Values) coroutine.DisposePollTimer();
            foreach (var record in _timers.Values)
                lock (record.Gate) record.DotNetTimer?.Dispose();
        }

        internal VmValue SyncMutexRelease(IReadOnlyList<VmValue> args)
        {
            var handle = RequireI64("rigi_sync_mutex_release", args, 0);
            _mutexes[handle].Release();
            return VmVoid.Instance;
        }

        internal VmValue TlsCurrentContext(IReadOnlyList<VmValue> args)
        {
            return new VmI64(s_currentWorker);
        }

        // ===== 棒5a：协程句柄 lane/当前协程三面（stdlib Rigi 体调用）=====

        // 当前协程句柄（native 半场 TLS 槽的 VM 对偶；无当前协程返 0）
        internal VmValue CoroutineCurrent(IReadOnlyList<VmValue> args)
        {
            return new VmI64(s_currentCoroutine?.Handle ?? 0);
        }

        // cohandle lane 槽的 VM 对偶：lane 恒由 executor 字段/继承绑定
        // 现算（LaneOf），不另存槽——get hook 直读，set hook 为空操作
        //（stdlib executor setter 写 executor 字段即完成 VM 侧换绑）
        internal VmValue CoroutineGetLane(IReadOnlyList<VmValue> args)
        {
            var handle = RequireI64("rigi_coroutine_get_lane", args, 0);
            // 声明返回 i32（stdlib coroutine.rg）：必须返 VmI32——返
            // VmI64 会让 publish 体的 lane == 1 比较抛类型错，异常穿越
            // 未释放的 gate 临界区后 OnTerminal 重入同一闸造成死锁
            return new VmI32(LaneOf(RequireCoroutine(handle)));
        }

        internal VmValue CoroutineSetLane(IReadOnlyList<VmValue> args)
        {
            _ = RequireI64("rigi_coroutine_set_lane", args, 0);
            if (args.Count <= 1 || args[1] is not VmI32)
            {
                throw new VmException("rigi_coroutine_set_lane：参数 1 需要 i32");
            }
            return VmVoid.Instance;
        }

        // CoroutineLocal（§20.2）：绑定栈在 VmCoroutine 上；无当前协程时
        // get 返 null（与 native 零值同口径），push/pop 属 withValue 内
        // 部通道，无当前协程即编译器/标准库 bug。
        internal VmValue CoroLocalPush(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 2)
            {
                throw new VmException("coro_local_push 需要 key 与 value");
            }
            var current = s_currentCoroutine
                ?? throw new VmException("coro_local_push 无当前协程");
            current.LocalPush(args[0], args[1]);
            return VmVoid.Instance;
        }

        internal VmValue CoroLocalPop(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 1)
            {
                throw new VmException("coro_local_pop 需要 key");
            }
            var current = s_currentCoroutine
                ?? throw new VmException("coro_local_pop 无当前协程");
            current.LocalPop(args[0]);
            return VmVoid.Instance;
        }

        internal VmValue CoroLocalGet(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 1)
            {
                throw new VmException("coro_local_get 需要 key");
            }
            var current = s_currentCoroutine;
            if (current == null)
            {
                return VmNull.Instance;
            }
            return current.LocalGet(args[0]);
        }

        internal VmValue CoroLocalInherit(IReadOnlyList<VmValue> args)
        {
            var child = RequireI64("rigi_coro_local_inherit", args, 0);
            var current = s_currentCoroutine;
            if (current == null)
            {
                return VmVoid.Instance;
            }
            RequireCoroutine(child).InheritLocalsFrom(current);
            return VmVoid.Instance;
        }

        private static void InheritLocals(VmCoroutine child, VmCoroutine? caller)
        {
            if (caller != null)
            {
                child.InheritLocalsFrom(caller);
            }
        }

    }
}
