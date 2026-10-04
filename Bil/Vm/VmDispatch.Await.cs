using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace RigiCompiler.Bil.Vm
{
    internal sealed partial class VmDispatch
    {
        // Await 职责；与主文件共享同一类型、字段及生命周期。

        // §18.3 await：Rigi Task.registerWaiter 决策 + 同一 gate 临界区
        // 内挂起（对齐旧 VmTask._gate 不变量）；终态快读不挂起
        internal void Await(VmCoroutine coroutine, VmValue taskValue, string? resultSlot)
        {
            if (taskValue is not VmObject taskObject
                || !(taskObject.TypeRef == "core.coroutine::Task"
                    || taskObject.TypeRef.StartsWith("core.coroutine::Task<",
                        StringComparison.Ordinal)))
            {
                throw new VmException("await 操作数不是 Task：" + taskValue.TypeRef);
            }
            var prefix = TaskPrefixOf(taskObject.TypeRef);
            var gate = EnsureGate(taskObject, prefix);
            var mutex = _mutexes[gate];
            int code;
            mutex.Wait();
            try
            {
                var handle = TaskCoroutineToken(taskObject);
                if (handle == 0)
                {
                    // 冷 Task 首次 await（§18.3/§18.4）：同一临界区内做
                    // 一次性启动判定——赢家 spawn-into（当前 Executor 启动），
                    // 输家不抛、按普通 waiter 登记等待
                    var started = ReadDecision(InvokeOn(coroutine,
                        TaskFn(prefix, "tryStart"), new VmValue[] { taskObject },
                        "$.dispatch.ret"));
                    if (started == 0)
                    {
                        SpawnInto(taskObject, prefix, coroutine);
                        handle = TaskCoroutineToken(taskObject);
                        if (handle == 0)
                        {
                            throw new VmException("spawn-into 后 Task 仍无协程句柄");
                        }
                    }
                    else
                    {
                        // 并发竞争窗口：tryStart 判定已启动但 attachRuntime
                        // 尚未写句柄不可能发生（同一 gate 临界区串行），
                        // 防御性重读
                        handle = TaskCoroutineToken(taskObject);
                    }
                }
                code = (int)((VmI32)(InvokeOn(coroutine,
                    TaskFn(prefix, "registerWaiter"),
                    new VmValue[] { taskObject, new VmI64(coroutine.Handle) },
                    "$.dispatch.ret") ?? throw new VmException(
                        "registerWaiter 未返回判定码"))).Value;
                if (code == 0)
                {
                    coroutine.MarkAwaiting(handle, resultSlot, RequireCoroutine(handle));
                    if (!coroutine.TrySuspend())
                    {
                        throw new VmException("await 时协程不在 Running");
                    }
                    NoteSuspended(coroutine);
                }
                else
                {
                    _ = handle;  // 终态快读路径在临界区外结算
                }
            }
            finally
            {
                mutex.Release();
            }
            if (code != 0)
            {
                ApplyTaskOutcome(coroutine,
                    RequireCoroutine(TaskCoroutineToken(taskObject)),
                    code, resultSlot);
            }
        }

        // 冷 Task 的 gate 懒建（棒4b 前仅防御；热 Task 的 gate 由
        // attachRuntime 在发布前建好）
        private long EnsureGate(VmObject taskObject, string prefix)
        {
            var gate = ReadI64Field(taskObject, prefix + "#gate@.i64");
            if (gate != 0)
            {
                return gate;
            }
            var created = SyncMutexCreate();
            taskObject.WriteField(_context.RuntimeField(prefix + "#gate@.i64"), new VmI64(created));
            return created;
        }

        private VmCoroutine RequireCoroutine(long handle)
        {
            return (_coroutines.TryGetValue(handle, out var coroutine)
                || _completedStrong.TryGetValue(handle, out coroutine)
                || (_completedCoroutines.TryGetValue(handle, out var weak)
                    && weak.TryGetTarget(out coroutine)))
                ? coroutine
                : throw new VmException("协程句柄失效：" + handle);
        }

        // 恢复点结算：被等待协程已终态（completer 先写终态再发布），
        // 按旧 ApplyTaskOutcome 语义取结果/重抛/传播取消
        internal bool SettleAwait(VmCoroutine coroutine, long handle, string? resultSlot)
        {
            var target = RequireCoroutine(handle);
            var code = target.State switch
            {
                VmCoroutineState.Completed => 1,
                VmCoroutineState.Failed => 2,
                VmCoroutineState.Cancelled => 3,
                _ => throw new VmException("恢复时 Task 仍未终态"),
            };
            ApplyTaskOutcome(coroutine, target, code, resultSlot);
            return coroutine.State == VmCoroutineState.Running;
        }

        private void ApplyTaskOutcome(VmCoroutine coroutine, VmCoroutine target,
            int code, string? resultSlot)
        {
            _failed.TryRemove(target.Handle, out _);
            switch (code)
            {
                case 1:
                    if (resultSlot != null)
                    {
                        coroutine.WriteVar(resultSlot,
                            (target.Result ?? VmVoid.Instance).Copy());
                    }
                    break;
                case 2:
                    coroutine.Complete(VmCompletion.Throw(
                        target.Failure ?? new VmException("Task 失败")));
                    break;
                case 3:
                    coroutine.Complete(VmCompletion.Throw(new VmException("Task 已取消")));
                    break;
                default:
                    throw new VmException("未知 registerWaiter 返回码：" + code);
            }
        }

        // 协程终态统一 choke point（旧 NotifyTerminal + VmTask.Complete/
        // Fail 合并形态）：释放轮询 timer → Rigi Task 终态迁移 + waiter
        // 排空（gate 临界区）→ 逐个发布 waiter → Dispatcher noteTerminal
        internal void OnTerminal(VmCoroutine coroutine)
        {
            coroutine.DisposePollTimer();
            if (coroutine.Handle == 0)
            {
                return;  // ad-hoc 协程不进调度台账
            }
            var failed = coroutine.Failure != null;
            if (failed)
            {
                _failed[coroutine.Handle] = coroutine;
            }
            var taskObject = coroutine.TaskObject;
            if (taskObject != null && HasDispatcher)
            {
                var prefix = TaskPrefixOf(taskObject.TypeRef);
                var gate = ReadI64Field(taskObject, prefix + "#gate@.i64");
                var name = coroutine.State == VmCoroutineState.Completed ? "complete"
                    : coroutine.State == VmCoroutineState.Cancelled ? "cancel" : "fail";
                VmValue? drained;
                var mutex = _mutexes[gate];
                mutex.Wait();
                try
                {
                    drained = InvokeIsolated(TaskFn(prefix, name),
                        new VmValue[] { taskObject });
                }
                finally
                {
                    mutex.Release();
                }
                if (drained is VmArray waiters)
                {
                    for (var i = 0; i < waiters.Length; i++)
                    {
                        var waiterToken = CoroutineTokenOf(waiters.GetAt(i));
                        if (waiterToken != 0
                            && _coroutines.TryGetValue(waiterToken, out var waiter))
                        {
                            Publish(waiter, "Task.ResumeWaiters");
                        }
                    }
                }
            }
            if (HasDispatcher)
            {
                InvokeIsolated(_noteTerminalFn!, new VmValue[]
                {
                    DispatcherInstance(), new VmI64(coroutine.Handle),
                    new VmBool(failed),
                });
            }
            // 活动/排队/挂起协程仍在强登记册；终态结果由 Task 保活。
            // 先发布弱项再摘强项，迟到 await 不存在查找空窗。
            // 终态协程一律进强登记册（review-20260910 #AOT句柄）：不单是
            // 裸协程——Task-backed 协程在 fire-and-forget（async 直调不
            // await 不存 Task）下「Task 保活」前提不成立：Task 对象与协程
            // 同簇即刻不可达，AOT GC 在 OnTerminal→retire 窗口内回收簇后，
            // workerLoop 的 retire/迟到 await 走 RequireCoroutine 只剩死弱
            // 项，误报「协程句柄失效」（AOT 实测触发；CoreCLR GC 时点晚
            // 不暴露）。摘除配对：destroy（CoroutineDestroy）释放引擎初始
            // 强引用时闭合 [终态, destroy] 窗口；Task 对象回收时的
            // NativeResourceRelease 终结器兜底摘残留条目，与 GC 时点解耦
            _completedStrong[coroutine.Handle] = coroutine;
            _completedCoroutines[coroutine.Handle] = new WeakReference<VmCoroutine>(coroutine);
            _coroutines.TryRemove(coroutine.Handle, out _);
            if (taskObject != null && ReadBoolField(taskObject,
                TaskPrefixOf(taskObject.TypeRef) + "#observed@.bool"))
                _failed.TryRemove(coroutine.Handle, out _);
        }

        // 未被任何 await 观察的失败（fire-and-forget；对齐旧
        // VmExecutor.UnobservedFailure 语义）
        internal VmException? UnobservedFailure
        {
            get
            {
                foreach (var pair in _failed)
                {
                    var task = pair.Value.TaskObject;
                    if (task == null)
                    {
                        continue;
                    }
                    var prefix = TaskPrefixOf(task.TypeRef);
                    if (!ReadBoolField(task, prefix + "#observed@.bool"))
                    {
                        return pair.Value.Failure;
                    }
                }
                return null;
            }
        }

    }
}
