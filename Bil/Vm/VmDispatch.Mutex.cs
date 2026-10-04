using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace RigiCompiler.Bil.Vm
{
    internal sealed partial class VmDispatch
    {
        // Mutex 职责；与主文件共享同一类型、字段及生命周期。

        // ===== 棒4b：Mutex 异步互斥锁桥（§19.6）=====

        private BilFunction MutexFn(string name)
        {
            return _context.FindRuntimeFunction("core.coroutine::Mutex$" + name + "(")
                ?? throw new VmException("stdlib 缺少 core.coroutine::Mutex$" + name);
        }

        private long ReadMutexGate(VmObject mutexObject)
        {
            return ReadI64Field(mutexObject, "core.coroutine::Mutex#gate@.i64");
        }

        // acquire 的竞争挂起（Mutex$enter 方法 hook）：tryEnter 判定与
        // TrySuspend 在同一 gate 临界区内原子完成（对齐 await 纪律）
        internal VmValue MutexEnter(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 1 || args[0] is not VmObject mutexObject)
            {
                throw new VmException("Mutex.enter 需要 Mutex receiver");
            }
            var coroutine = s_currentCoroutine
                ?? throw new VmException("Mutex.acquire 需在协程上下文调用");
            if (coroutine.Handle == 0)
            {
                throw new VmException("Mutex.acquire 不支持在运行时初始化上下文"
                    + "（ad-hoc 协程无调度句柄）");
            }
            var mutex = _mutexes[ReadMutexGate(mutexObject)];
            mutex.Wait();
            try
            {
                var code = ReadDecision(InvokeOn(coroutine, MutexFn("tryEnter"),
                    new VmValue[] { mutexObject, new VmI64(coroutine.Handle) },
                    "$.dispatch.ret"));
                if (code != 0)
                {
                    if (!coroutine.TrySuspend())
                    {
                        throw new VmException("Mutex.acquire 竞争挂起时协程不在 Running");
                    }
                    NoteSuspended(coroutine);
                }
            }
            finally
            {
                mutex.Release();
            }
            return VmVoid.Instance;
        }

        // release（Mutex$release 方法 hook）：临界区内 releaseNext 判定
        // （令牌校验失败由 Rigi 侧抛 IllegalStateException），临界区外
        // 发布被唤醒的队首 waiter（FIFO handoff：锁所有权已随判定移交）
        internal VmValue MutexRelease(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 2 || args[0] is not VmObject mutexObject)
            {
                throw new VmException("Mutex.release 需要 (Mutex, Lock)");
            }
            var caller = s_currentCoroutine;
            var mutex = _mutexes[ReadMutexGate(mutexObject)];
            long next;
            mutex.Wait();
            try
            {
                var releaseNext = MutexFn("releaseNext");
                var value = caller != null
                    ? InvokeOn(caller, releaseNext, args.ToArray(), "$.dispatch.ret")
                    : InvokeIsolated(releaseNext, args.ToArray());
                // Rigi 侧令牌校验抛 IllegalStateException 时结果槽为空：
                // 异常在 InvokeOn 的 Step 循环内已按 caller 的 try/catch
                // 展开（或置 pending 待展开）——直接返回，不覆盖其传播
                next = value == null ? 0 : CoroutineTokenOf(value);
                if (next == 0)
                {
                    return VmVoid.Instance;
                }
            }
            finally
            {
                mutex.Release();
            }
            if (next != 0 && _coroutines.TryGetValue(next, out var waiter))
            {
                Publish(waiter, "Mutex.release");
            }
            return VmVoid.Instance;
        }

    }
}
