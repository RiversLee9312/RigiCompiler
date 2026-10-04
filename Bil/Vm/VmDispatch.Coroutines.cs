using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace RigiCompiler.Bil.Vm
{
    internal sealed partial class VmDispatch
    {
        // Coroutines 职责；与主文件共享同一类型、字段及生命周期。

        private static void Enqueue(VmWorker worker, long task)
        {
            lock (worker.Queue)
            {
                worker.Queue.Enqueue(task);
            }
            worker.Semaphore.Release();
        }

        internal VmValue CoroutineCreate(IReadOnlyList<VmValue> args)
        {
            // resumeFn token 在 VM 侧不承载语义（VM 直接解释 BIL，无需
            // 状态机 fn 指针）；frame token = 协程规格表键
            var frame = RequireI64("rigi_coroutine_create", args, 1);
            if (!_specs.TryRemove(frame, out var spec))
            {
                throw new VmException("rigi_coroutine_create：未知 frame token " + frame);
            }
            var coroutine = new VmCoroutine(this);
            coroutine.PushFrame(spec.Fn, spec.Args, null, _context);
            // 对齐 native cohandle 语义（句柄无内部状态机，create 后即可
            // resume）：VM 侧直接置 Runnable；后续若经 Dispatcher.publish
            // 发布，其三连 CAS 对 Runnable 幂等 benign
            coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Runnable);
            return new VmI64(RegisterCoroutine(coroutine));
        }

        // 在当前线程内嵌套解释目标协程的一个执行段（复用 Step 机制）；
        // 用户协程抛出的 C# 异常在此捕获并转任务失败路径，不逃逸进
        // 调用方解释帧
        internal VmValue CoroutineResume(IReadOnlyList<VmValue> args)
        {
            var handle = RequireI64("rigi_coroutine_resume", args, 0);
            return new VmI64(ResumeSegment(RequireCoroutine(handle)));
        }

        internal long ResumeSegment(VmCoroutine coroutine)
        {
            Interlocked.Increment(ref _activeSegments);
            Interlocked.Increment(ref _activityEpoch);
            try
            {
            // 协程锁串行化（单所有者不变量：同一协程任意时刻至多一个
            // Worker）：转换 + settle + Step 循环整体在锁内（旧
            // VmExecutor.Execute 同口径；C# lock 同线程可重入，Step 内的
            // 嵌套 Step 循环自然安全）
            lock (coroutine.SyncRoot)
            {
                if (!coroutine.TryTransition(
                        VmCoroutineState.Runnable, VmCoroutineState.Running))
                {
                    return ResumeSkipped;
                }
                var previous = s_currentCoroutine;
                s_currentCoroutine = coroutine;
                NoteResume(coroutine);
                try
                {
                    if (!coroutine.SettleAfterResume(_context))
                    {
                        return ResumeSuspended;
                    }
                    while (coroutine.State == VmCoroutineState.Running)
                    {
                        coroutine.Step(_context);
                    }
                    return coroutine.State switch
                    {
                        VmCoroutineState.Suspended => ResumeSuspended,
                        VmCoroutineState.Runnable => ResumeYielded,
                        _ => ResumeDone,
                    };
                }
                catch (Exception exception)
                {
                    coroutine.Fail(exception);
                    return ResumeDone;
                }
                finally
                {
                    s_currentCoroutine = previous;
                }
            }

            }
            finally
            {
                Interlocked.Increment(ref _activityEpoch);
                Interlocked.Decrement(ref _activeSegments);
            }
        }

        internal VmValue CoroutineDestroy(IReadOnlyList<VmValue> args)
        {
            var handle = RequireI64("rigi_coroutine_destroy", args, 0);
            var coroutine = RequireCoroutine(handle);
            if (coroutine.State is not (VmCoroutineState.Completed
                or VmCoroutineState.Failed or VmCoroutineState.Cancelled))
            {
                throw new VmException("rigi_coroutine_destroy：协程未终态（状态 "
                    + coroutine.State + "）");
            }
            NativeRcReleaseCore(handle);
            // 摘终态强登记册条目（review-20260910 #AOT句柄）：destroy 释放
            // 引擎初始强引用（_nativeRcStrong 归零）即 native cohandle 销毁
            // 点，登记册条目生命周期 = [终态, destroy]，强持有窗口在此闭合。
            // 摘除后的迟到查询安全：resume/lane/二次 destroy 走 Rigi 侧
            // retainCoroutine 先行（token 已摘 → benign null）；await 结算
            // 的 target 由 Task.TaskRuntimeState 双向链或 waiter 的
            // _awaitTarget 根住，弱引用不失效
            _completedStrong.TryRemove(handle, out _);
            return VmVoid.Instance;
        }

    }
}
