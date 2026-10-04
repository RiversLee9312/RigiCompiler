using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace RigiCompiler.Bil.Vm
{
    internal sealed partial class VmDispatch
    {
        // TaskBridge 职责；与主文件共享同一类型、字段及生命周期。

        private VmValue DispatcherInstance()
        {
            return _context.GetSingleton(_context.RuntimeSymbol("core.coroutine::Dispatcher"))
                ?? throw new VmException("core.coroutine::Dispatcher singleton 未初始化");
        }

        // Task 运行时 fn/字段的符号前缀：非泛型 Task 与 Task<TReturn>
        // 两套同构声明（BIL 泛型以声明形态 TReturn 落盘）
        private static string TaskPrefixOf(string typeRef)
        {
            if (typeRef.StartsWith("core.coroutine::Task<", StringComparison.Ordinal))
            {
                return "core.coroutine::Task<TReturn>";
            }
            return "core.coroutine::Task";
        }

        private BilFunction TaskFn(string prefix, string name)
        {
            return _context.FindRuntimeFunction(prefix + "$" + name + "(")
                ?? throw new VmException("stdlib 缺少 " + prefix + "$" + name);
        }

        private long ReadI64Field(IVmFieldHost host, string symbol)
        {
            return host.TryReadField(_context.RuntimeField(symbol), out var value) && value is VmI64 number
                ? number.Value
                : 0;
        }

        private long CoroutineTokenOf(VmValue value)
        {
            if (value is not VmObject carriage
                || carriage.TypeRef != _context.RuntimeSymbol("core.coroutine::CoroutineCarriage")
                || !carriage.TryReadField(
                    _context.RuntimeField("core.coroutine::CoroutineCarriage#token@.i64"), out var token)
                || token is not VmI64 number)
            {
                return 0;
            }
            return number.Value;
        }

        private static long TaskCoroutineToken(VmObject taskObject)
        {
            return taskObject.TryReadHidden(TaskCoroutineHiddenKey, out var value)
                && value is VmI64 number ? number.Value : 0;
        }

        private static void AttachTaskCoroutineToken(VmObject taskObject, long token)
        {
            taskObject.WriteHidden(TaskCoroutineHiddenKey, new VmI64(token));
        }

        private bool ReadBoolField(IVmFieldHost host, string symbol)
        {
            return host.TryReadField(_context.RuntimeField(symbol), out var value) && value is VmBool flag
                && flag.Value;
        }

        // ===== 同步解释桥（ConstructSingleton/ProbePolling 先例）=====

        // ad-hoc 协程同步跑完一个 Rigi fn（不得挂起；用于无当前协程的
        // 线程：主线程启动序列、timer 线程、worker 线程入口）
        internal VmValue? InvokeIsolated(BilFunction function, VmValue[] args)
        {
            var coroutine = new VmCoroutine(this);
            coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Running);
            coroutine.PushFrame(function, args, null, _context);
            var previous = s_currentCoroutine;
            s_currentCoroutine = coroutine;
            try
            {
                RunToReturn(coroutine);
            }
            finally
            {
                s_currentCoroutine = previous;
            }
            if (coroutine.Failure != null)
            {
                throw coroutine.Failure;
            }
            return coroutine.Result;
        }

        private void RunToReturn(VmCoroutine coroutine)
        {
            while (coroutine.CallStack.Count > 0
                && coroutine.State == VmCoroutineState.Running)
            {
                coroutine.Step(_context);
            }
            if (coroutine.State == VmCoroutineState.Suspended)
            {
                throw new VmException("运行时 fn 意外挂起（调度原语不得 await/yield）");
            }
        }

        // 在当前运行中的协程上同步嵌套解释一个 Rigi fn（ProbePolling
        // 同形态：压帧 → Step 到原深度 → 从结果槽取值）
        internal VmValue? InvokeOn(VmCoroutine coroutine, BilFunction function,
            VmValue[] args, string resultSlot)
        {
            var depth = coroutine.CallStack.Count;
            coroutine.PushFrame(function, args, resultSlot, _context);
            while (coroutine.CallStack.Count > depth
                && coroutine.State == VmCoroutineState.Running)
            {
                coroutine.Step(_context);
            }
            if (coroutine.State != VmCoroutineState.Running)
            {
                throw new VmException("运行时 fn 导致协程意外离开 Running");
            }
            return coroutine.CurrentFrame.Slots.TryGetValue(resultSlot, out var value)
                ? value
                : VmVoid.Instance;
        }

        private void Invoke(BilFunction function, VmValue[] args, VmCoroutine? caller,
            string resultSlot = "$.dispatch.ret")
        {
            if (caller != null)
            {
                InvokeOn(caller, function, args, resultSlot);
                return;
            }
            InvokeIsolated(function, args);
        }

        // ===== 调度桥：spawn / publish / await / 终态 =====

        // §18.1 eager spawn：建协程 + Rigi Task 对象（attachRuntime 装
        // 句柄/gate/状态）→ noteSpawn（live 先增）→ 发布
        internal VmCoroutine Spawn(BilFunction function, IReadOnlyList<VmValue> args,
            VmCoroutine? caller)
        {
            EnsureFnsResolved();
            if (_noteSpawnFn == null || _publishFn == null)
            {
                throw new VmException("stdlib 缺少 core.coroutine::Dispatcher 运行时通道");
            }
            var coroutine = new VmCoroutine(this);
            coroutine.PushFrame(function, args, null, _context);
            // §18.1 第 3 步：未显式指定时继承调用方 Coroutine 的 Executor
            coroutine.BoundExecutor = caller == null ? null : EffectiveExecutorOf(caller);
            var handle = RegisterCoroutine(coroutine);
            var typeRef = VmContext.TaskTypeRef(VmContext.FunctionResultType(function));
            var taskObject = _context.AllocateObject(typeRef);
            coroutine.AttachTaskObject(taskObject);
            AttachTaskCoroutineToken(taskObject, handle);
            var prefix = TaskPrefixOf(typeRef);
            Invoke(TaskFn(prefix, "attachRuntimeNative"),
                new VmValue[] { taskObject, new VmI64(handle) }, caller);
            InheritLocals(coroutine, caller);
            Invoke(_noteSpawnFn!, new VmValue[] { DispatcherInstance() }, caller);
            Publish(coroutine, "eager spawn");
            return coroutine;
        }

        // ===== 棒4b：冷 Task 启动通道（§18.4）=====

        // spawn-into：复用既有 Task 对象建协程（Task↔协程 1:1，不另建
        // 句柄）。前置：调用方已持 task gate 临界区且 tryStart 判定本次
        // 启动成立（state 已投影 Runnable）。body 闭包经 callable 协议
        // 虚派发解析 $$call 实现 fn（与 invoke.indirect 同通道），直接
        // 压帧运行其 async 体（不再经 EagerSpawn——那会另建 Task）
        internal VmCoroutine SpawnInto(VmObject taskObject, string prefix, VmCoroutine? caller)
        {
            EnsureFnsResolved();
            if (_noteSpawnFn == null || _publishFn == null)
            {
                throw new VmException("stdlib 缺少 core.coroutine::Dispatcher 运行时通道");
            }
            var bodySymbol = FieldSymbolOf(taskObject.TypeRef, "body");
            if (!taskObject.TryReadField(bodySymbol, out var body) || body is VmNull)
            {
                throw new VmException("冷 Task 缺少 body：" + taskObject.TypeRef);
            }
            var callSymbol = _context.FindCallTarget(VmTypeOps.ActualType(body),
                Array.Empty<VmValue>())
                ?? throw new VmException("冷 Task body 没有 $$call 实现：" + body.TypeRef);
            var function = _context.FindFunction(callSymbol)
                ?? throw new VmException("冷 Task body 实现缺少 fn 定义：" + callSymbol);
            var coroutine = new VmCoroutine(this);
            coroutine.PushFrame(function, new VmValue[] { body }, null, _context);
            var handle = RegisterCoroutine(coroutine);
            coroutine.AttachTaskObject(taskObject);
            AttachTaskCoroutineToken(taskObject, handle);
            // 目标 Executor：预设 ?? 当前 Executor（§18.4）
            coroutine.BoundExecutor = ReadExecutorField(taskObject, prefix)
                ?? (caller == null ? null : EffectiveExecutorOf(caller));
            Invoke(TaskFn(prefix, "attachRuntimeNative"),
                new VmValue[] { taskObject, new VmI64(handle) }, caller);
            InheritLocals(coroutine, caller);
            Invoke(_noteSpawnFn!, new VmValue[] { DispatcherInstance() }, caller);
            Publish(coroutine, "spawn-into");
            return coroutine;
        }

        // run()/run(executor:) 的方法 hook（Task$startCold）：gate 临界区内
        // tryStart 一次性判定——已启动（含热 Task）抛 IllegalStateException
        // （走真 init 通道，可 catch）；成立则 spawn-into
        internal VmValue StartCold(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 1 || args[0] is not VmObject taskObject)
            {
                throw new VmException("startCold 需要 Task receiver");
            }
            var prefix = TaskPrefixOf(taskObject.TypeRef);
            var caller = s_currentCoroutine;
            var gate = EnsureGate(taskObject, prefix);
            var mutex = _mutexes[gate];
            mutex.Wait();
            try
            {
                var tryStart = TaskFn(prefix, "tryStart");
                var code = ReadDecision(caller != null
                    ? InvokeOn(caller, tryStart, new VmValue[] { taskObject },
                        "$.dispatch.ret")
                    : InvokeIsolated(tryStart, new VmValue[] { taskObject }));
                if (code != 0)
                {
                    throw IllegalState(caller,
                        "Task 只允许启动一次：对已完成启动的 Task 调用 run");
                }
                SpawnInto(taskObject, prefix, caller);
            }
            finally
            {
                mutex.Release();
            }
            return VmVoid.Instance;
        }

        private static int ReadDecision(VmValue? value)
        {
            return value is VmI32 number
                ? number.Value
                : throw new VmException("运行时判定 fn 未返回 i32");
        }

        // 语言级 IllegalStateException（stdlib 真 init 通道；无协程上下文
        // 时回落直写字段路径，与 NoSuchMethod 两变体同形态）
        private VmException IllegalState(VmCoroutine? coroutine, string message)
        {
            return coroutine != null
                ? _context.LanguageException(coroutine, "core::IllegalStateException",
                    new VmValue[] { new VmString(message) }, new[] { ".string" }, message)
                : _context.LanguageException("core::IllegalStateException", message);
        }

        // 按字段简单名查字段符号（泛型承载形态如 AsyncFunc<TReturn> 的
        // 拼写随声明漂移，运行时桥只锁简单名）
        private string FieldSymbolOf(string typeRef, string simpleName)
        {
            var prefix = TaskPrefixOf(typeRef) + "#" + simpleName + "@";
            var canonical = BilCompilerSymbols.ResolvePrefix(_context.Module, prefix);
            foreach (var field in _context.CollectInstanceFields(typeRef))
            {
                if (canonical != null ? field.Symbol == canonical
                    : field.Symbol.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return field.Symbol;
                }
            }
            throw new VmException("类型缺少字段 " + simpleName + "：" + typeRef);
        }

        // 有效 Executor（§17.1/§18.4）：Task.executor 预设/换绑字段 ??
        // 创建时继承的默认绑定 ?? MainExecutor（null 表示）
        private VmObject? EffectiveExecutorOf(VmCoroutine coroutine)
        {
            var taskObject = coroutine.TaskObject;
            if (taskObject != null)
            {
                var preset = ReadExecutorField(taskObject, TaskPrefixOf(taskObject.TypeRef));
                if (preset != null)
                {
                    return preset;
                }
            }
            return coroutine.BoundExecutor;
        }

        private VmObject? ReadExecutorField(VmObject taskObject, string prefix)
        {
            return taskObject.TryReadField(
                    _context.RuntimeField(prefix + "#executor@.nullable<core.coroutine::Executor>"), out var value)
                && value is VmObject executor
                ? executor
                : null;
        }

        // Executor → Dispatcher lane（0=Main / 1=Compute / 2=IO；未知
        // 用户派生 Executor 落 Main——§20.1 只有三个内置 Executor 有
        // 独立调度域）
        internal int LaneOf(VmCoroutine coroutine)
        {
            var executor = EffectiveExecutorOf(coroutine);
            if (executor == null)
            {
                return 0;
            }
            return executor.TypeRef switch
            {
                "core.coroutine::ComputeExecutor" => 1,
                "core.coroutine::IOExecutor" => 2,
                _ => 0,
            };
        }

        // 统一发布入口（旧 VmExecutor.Publish/PublishWakeup 合并形态）：
        // 引擎侧先 CAS 进 Runnable；三连失败 = 已被并发发布或已终态
        // ⇒ benign 跳过（对齐旧 benign 竞态口径，不再配纪元判定——
        // 「入队即唤醒」由队列 transport 保证不丢，残留的丢失通道只剩
        // Rigi 侧发布抛错，转为 Fail 留证，对齐旧 HandleWakeupPublishFailure
        // 的留证语义）
        internal void Publish(VmCoroutine coroutine, string source)
        {
            if (coroutine.Handle == 0)
            {
                return;  // ad-hoc 协程不进调度（防御）
            }
            if (!coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Runnable)
                && !coroutine.TryTransition(VmCoroutineState.Suspended, VmCoroutineState.Runnable)
                && !coroutine.TryTransition(VmCoroutineState.Running, VmCoroutineState.Runnable))
            {
                return;  // benign：已在 Runnable / 已终态
            }
            try
            {
                if (FailPublishesForTest)
                {
                    throw new VmException("注入的发布失败（测试缝线）");
                }
                if (!HasDispatcher)
                {
                    return;  // 空模块单元测试形态：纯状态机
                }
                // TaskState 投影（§18.2）：spawn/publish → Runnable
                NoteRunnable(coroutine);
                // lane 在发布时读取协程最新绑定（§18.4 换绑于下一恢复点
                // 生效）。棒5a：lane 换算移入 Rigi Dispatcher.publish
                //（经 coroutine_get_lane hook 回调 LaneOf），本桥只传
                // 句柄——双端同一换算点
                InvokeIsolated(_publishFn!,
                    new VmValue[] { DispatcherInstance(), new VmI64(coroutine.Handle) });
            }
            catch (Exception exception)
            {
                try
                {
                    coroutine.Fail(new VmException(
                        "调度器丢失唤醒（" + source + "）：协程已 Runnable 但发布失败："
                        + exception.Message));
                }
                catch
                {
                    // 留证路径自身失败（Dispatcher 已坏）：不再升级，
                    // 协程状态机已脱离 Runnable 由终态兜底
                }
            }
        }

        // ===== TaskState 投影桥（§18.2，棒4b）=====

        // 挂起点投影：协程已 CAS 进 Suspended 后调用（Await 的 gate 临界
        // 区内、EventAlarm/Timer 的 TryAwait 锁内、轮询挂起、Mutex 竞争
        // 挂起）。终态三通道（complete/fail/cancel）不经此路径
        internal void NoteSuspended(VmCoroutine coroutine)
        {
            NoteState(coroutine, "markSuspended");
        }

        // 发布点投影：协程已 CAS 进 Runnable 后调用（Publish 统一入口）
        internal void NoteRunnable(VmCoroutine coroutine)
        {
            NoteState(coroutine, "markRunnable");
        }

        private void NoteState(VmCoroutine coroutine, string name)
        {
            var taskObject = coroutine.TaskObject;
            if (taskObject == null || !HasDispatcher)
            {
                return;  // ad-hoc 协程 / 空模块单元测试：无投影对象
            }
            InvokeIsolated(TaskFn(TaskPrefixOf(taskObject.TypeRef), name),
                new VmValue[] { taskObject });
        }

    }
}
