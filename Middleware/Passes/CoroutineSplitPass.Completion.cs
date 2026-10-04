using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Passes
{
    public sealed partial class CoroutineSplitPass : IMwStage
    {
        // Completion 职责；与主文件共享同一类型、字段及生命周期。

        // 失败终态序列（DONE 垫尾/探测失败尾共用，棒5a；对齐 VM
        // OnTerminal 的 fail 通道）：take pending（.any，+1 move）→
        // native 失败注册表登记（+1 拷贝持有，节点 id）→ 写
        // task.failureNodeId → fail()（终态迁移 + waiter 排空）→
        // publishAll → noteTerminal(failed=true) → MirCoroutineDone →
        // ret DONE。RcInjection 追加托管槽 release + frame 最终 release
        //（DONE 出口）。padManagedOut 非空时收集本序列新建的托管局部
        //（RcInjection 传播垫场景：垫在 releaseOrder 冻结后合成，自带
        // 局部须显式释放）；正常 split 期调用传 null（局部在
        // RcInjection 分类前已入 fn.Locals，统一配平）。返回终结符
        internal static MirTerminator EmitFailTerminal(MwContext context, MirModule mir,
            MirFunction fn, List<MirInst> insts, string taskFieldSymbol,
            string taskTypeRef, RuntimeSyms syms, List<string>? padManagedOut = null)
        {
            string Fresh(string prefix, MirType type) =>
                ProxyWildcardAbi.FreshLocal(fn, prefix, type);
            string FreshManaged(string prefix, MirType type)
            {
                var name = Fresh(prefix, type);
                padManagedOut?.Add(name);
                return name;
            }
            var exc = FreshManaged("$mw.exc.", Any);
            insts.Add(new MirTakePending(exc));
            var nodeId = Fresh("$mw.nid.", I64);
            insts.Add(new MirCall(syms.FailureRecord,
                new List<MirOperand> { new MirLocalOperand(exc) }, nodeId));
            var task = FreshManaged("$mw.task.", MirType.Of(
                FieldTypeOfTaskField(taskFieldSymbol)));
            insts.Add(new MirGetField(new MirLocalOperand(FrameParamName),
                taskFieldSymbol, task));
            insts.Add(new MirSetField(new MirLocalOperand(nodeId),
                new MirLocalOperand(task),
                TaskField(context, taskTypeRef, "failureNodeId", ".i64")));
            EmitTerminalPublish(context, fn, insts, task, taskTypeRef,
                failed: true, syms, Fresh, FreshManaged);
            return ResumeDoneTerminator(context, fn, insts, Fresh);
        }

        // frame.task 字段符号的槽类型段（$mw.task@ 之后）
        private static string FieldTypeOfTaskField(string taskFieldSymbol)
        {
            var at = taskFieldSymbol.LastIndexOf('@');
            return taskFieldSymbol.Substring(at + 1);
        }

        // TaskState 投影（§18.2）：挂起点 markSuspended / 恢复点
        // markRunnable。frame.$mw.task 即当前协程的 Task 对象
        private static void EmitTaskMark(MwContext context,
            List<MirInst> insts, MirLocalOperand frameOp, string taskFieldSymbol,
            string taskTypeRef, string name, System.Func<string, MirType, string> fresh)
        {
            var task = fresh("$mw.ts.", MirType.Of(FieldTypeOfTaskField(taskFieldSymbol)));
            insts.Add(new MirGetField(frameOp, taskFieldSymbol, task));
            insts.Add(new MirCall(TaskFn(context, taskTypeRef, name),
                new List<MirOperand> { new MirLocalOperand(task) }, null));
        }

        private static void EmitRestoreMark(MwContext context,
            List<MirInst> insts, MirLocalOperand frameOp, string taskFieldSymbol,
            string taskTypeRef, System.Func<string, MirType, string> fresh) =>
            EmitTaskMark(context, insts, frameOp, taskFieldSymbol,
                taskTypeRef, "markRunnable", fresh);

        // 终态与 await 登记共用 Task.gate：状态迁移和 waiter 排空必须
        // 原子完成，否则其他 Worker 可在排空后才把 waiter 写入旧队列。
        // publishAll 留在锁外，避免恢复者等待同一 gate 时阻塞发布路径。
        private static void EmitTerminalPublish(MwContext context, MirFunction fn,
            List<MirInst> insts, string task, string taskTypeRef, bool failed,
            RuntimeSyms syms, System.Func<string, MirType, string> fresh,
            System.Func<string, MirType, string> freshManaged)
        {
            var gate = fresh("$mw.done.gate.", I64);
            insts.Add(new MirGetField(new MirLocalOperand(task),
                TaskField(context, taskTypeRef, "gate", ".i64"), gate));
            insts.Add(new MirCall(syms.MutexAcquire,
                new List<MirOperand> { new MirLocalOperand(gate) }, null));
            var drained = freshManaged("$mw.drained.",
                MirType.Of(".array<core.coroutine::CoroutineCarriage>"));
            insts.Add(new MirCall(TaskFn(context, taskTypeRef, failed ? "fail" : "complete"),
                new List<MirOperand> { new MirLocalOperand(task) }, drained));
            insts.Add(new MirCall(syms.MutexRelease,
                new List<MirOperand> { new MirLocalOperand(gate) }, null));
            var disp = freshManaged("$mw.disp.", MirType.Of(BilCompilerSymbols.Resolve(context.Module, DispatcherCanonical)));
            insts.Add(new MirCall(syms.DispatcherGet, new List<MirOperand>(), disp));
            insts.Add(new MirCall(syms.PublishAll,
                new List<MirOperand> { new MirLocalOperand(disp),
                    new MirLocalOperand(drained) }, null));
            var cur = fresh("$mw.done.cur.", I64);
            var failedConst = fresh("$mw.done.fl.", Bool);
            insts.Add(new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), cur));
            insts.Add(new MirLoadResource(
                ProxyWildcardAbi.AddBoolResource(context, failed), failedConst));
            insts.Add(new MirCall(syms.NoteTerminal,
                new List<MirOperand> { new MirLocalOperand(disp),
                    new MirLocalOperand(cur), new MirLocalOperand(failedConst) }, null));
            insts.Add(new MirCoroutineDone());
        }

        private static MirTerminator ResumeDoneTerminator(MwContext context, MirFunction fn,
            List<MirInst> insts, System.Func<string, MirType, string> fresh)
        {
            var doneCode = fresh("$mw.code.", I32);
            insts.Add(new MirLoadResource(
                ProxyWildcardAbi.AddI32Resource(context, ResumeDone), doneCode));
            return new MirRet(new MirLocalOperand(doneCode));
        }

        // $mw.poll_probe 模块级合成（懒建一次，$mw.named.lookup 先例）：
        // 普通 MIR fn（alarm 胖引用 → i32）：isReady 虚派发 + bool 双
        // 分支转 i32（1 ready / 0 not）；isReady 抛异常走 ExcTarget 进
        // 函数级传播垫，RcInjection 第二垫尾分叉（IsPollProbe 标记，仿
        // IsCoroutineResume）= release 配平 + ret -1 + pending 保持置位
        //（恢复块 poll 失败尾取走走 yield 点失败路径）。参数借用约定
        //（frame 恢复的 alarm 槽持有 +1，probe 不动计数）
        private static MwMemberSymbol EnsurePollProbe(MwContext context, MirModule mir)
        {
            foreach (var existing in mir.Functions)
            {
                if (existing.Symbol.Canonical == PollProbeCanonical)
                {
                    return existing.Symbol;
                }
            }
            var isReady = context.Symbols.FindMember(PollProbeIsReadyCanonical)
                ?? throw new CompilerInternalException(
                    "stdlib PollingAlarm.isReady 符号缺失: " + PollProbeIsReadyCanonical);
            var symbol = ProxyBakeSupport.SyntheticMember(PollProbeCanonical, owner: null);
            var alarmParam = new MirLocal(ProbeParamName, MirType.Of(PollingAlarmCanonical));
            var fn = new MirFunction(symbol, I32, new List<MirLocal> { alarmParam },
                new List<MirLocal> { alarmParam }, new List<MirBlock>(), false,
                isPollProbe: true);
            var ready = ProxyWildcardAbi.FreshLocal(fn, "$mw.pp.rdy.", MirType.Of(".bool"));
            fn.AddBlock(new MirBlock("entry", new List<MirInst>
            {
                new MirCall(isReady,
                    new List<MirOperand> { new MirLocalOperand(ProbeParamName) }, ready),
            }, new MirCondBranch(new MirLocalOperand(ready), "mw.pp.ready", "mw.pp.wait")));
            var one = ProxyWildcardAbi.FreshLocal(fn, "$mw.pp.one.", I32);
            fn.AddBlock(new MirBlock("mw.pp.ready", new List<MirInst>
            {
                new MirLoadResource(ProxyWildcardAbi.AddI32Resource(context, 1), one),
            }, new MirRet(new MirLocalOperand(one))));
            var zero = ProxyWildcardAbi.FreshLocal(fn, "$mw.pp.zero.", I32);
            fn.AddBlock(new MirBlock("mw.pp.wait", new List<MirInst>
            {
                new MirLoadResource(ProxyWildcardAbi.AddI32Resource(context, 0), zero),
            }, new MirRet(new MirLocalOperand(zero))));
            mir.AddFunction(fn);
            return symbol;
        }

        // DONE 尾（棒5a；resume fn 的 MirRet 出口改写，对齐 VM
        // OnTerminal 的 complete 通道）：Task<T> 先把结果写 result
        // 字段（nullable 包装——终态保存与 waiter 读取以 Task.gate
        // 临界区建立 happens-before，§18.3）→ complete() → publishAll
        // → noteTerminal → MirCoroutineDone → ret DONE。
        // B-1 Plain 分叉：裸 frame 无 Task——非 void 写 $mw.result
        // 字段（调用方 DONE 臂读取）→ 直 ret DONE（无终态序列/无
        // MirCoroutineDone——frame 所有权归调用方，本出口不做最终
        // release）
        private void RewriteReturn(MwContext context, MirModule mir, MirFunction resumeFn,
            MirBlock block, SplitPlan plan, MirLocalOperand frameOp, RuntimeSyms syms)
        {
            if (block.Terminator is not MirRet ret)
            {
                return;
            }
            var insts = block.InstructionList;
            string Fresh(string prefix, MirType type) =>
                ProxyWildcardAbi.FreshLocal(resumeFn, prefix, type);
            if (plan.Mode == SplitMode.Plain)
            {
                if (ret.Value != null && plan.ResultFieldSymbol != null)
                {
                    insts.Add(new MirSetField(ret.Value, frameOp,
                        plan.ResultFieldSymbol));
                }
                block.Terminator = ResumeDoneTerminator(context, resumeFn, insts, Fresh);
                return;
            }
            var taskFieldSymbol = plan.TaskFieldSymbol!;
            var taskTypeRef = plan.TaskTypeRef!;
            var original = plan.Fn;
            var task = Fresh("$mw.task.", MirType.Of(FieldTypeOfTaskField(taskFieldSymbol)));
            insts.Add(new MirGetField(frameOp, taskFieldSymbol, task));
            if (ret.Value != null && !original.ReturnType.IsVoid)
            {
                // 终态结果写 Task<T>.result（nullable<T> 包装）
                var rn = Fresh("$mw.done.rn.",
                    MirType.Of(".nullable<" + original.ReturnType.Canonical + ">"));
                insts.Add(new MirWrapNullable(ret.Value, original.ReturnType, rn));
                insts.Add(new MirSetField(new MirLocalOperand(rn),
                    new MirLocalOperand(task),
                    TaskField(context, taskTypeRef, "result",
                        ".nullable<" + original.ReturnType.Canonical + ">")));
            }
            EmitTerminalPublish(context, resumeFn, insts, task, taskTypeRef,
                failed: false, syms, Fresh, Fresh);
            block.Terminator = ResumeDoneTerminator(context, resumeFn, insts, Fresh);
        }

        // ===== ③ spawn stub 改写（棒5a 新交互点，对齐 VM
        // VmDispatch.Spawn 序：建 Task + attachRuntime → noteSpawn →
        // publish；lane = 继承调用方（laneOfCurrent——协程只在绑定
        // Executor 的 Worker 上运行，§17.1））=====

        private void ReplaceWithStub(MwContext context, MirModule mir, MirFunction fn,
            MwTypeSymbol frameType, MirType frameMirType, MwMemberSymbol initSymbol,
            MwMemberSymbol resumeSymbol, string taskTypeRef,
            string taskFieldSymbol, System.Func<MirLocal, string> fieldOf)
        {
            var syms = Syms(context, mir);
            var taskType = MirType.Of(taskTypeRef);
            var stubLocals = new List<MirLocal>(fn.Parameters);
            var stub = new MirFunction(fn.Symbol, taskType, fn.Parameters, stubLocals,
                new List<MirBlock>(), fn.IsEntrypoint, isAsync: true);
            string Fresh(string prefix, MirType type) =>
                ProxyWildcardAbi.FreshLocal(stub, prefix, type);
            var frame = Fresh("$mw.frame.", frameMirType);
            var outTask = Fresh("$mw.task.", taskType);

            var insts = new List<MirInst>
            {
                // frame = new 合成类型（合成空 init；字段随后逐槽落）
                new MirNewObject(frameType, null, initSymbol,
                    new List<MirOperand>(), frame),
            };
            // 参数/类级 typeid 落 frame 字段（stub 是原实例方法，Emit
            // prologue 正常——类级 .generic.* 局部由 prologue 从 .this 装入）
            foreach (var parameter in fn.Parameters)
            {
                insts.Add(new MirSetField(new MirLocalOperand(parameter.Name),
                    new MirLocalOperand(frame), fieldOf(parameter)));
            }
            var frameOp = new MirLocalOperand(frame);
            // Task 对象：合成空 init（无用户 init——gate/body 冷通道用不
            // 到，attachRuntime 懒建 gate 兜底；对齐 VM AllocateObject）
            // NewObject 用原 taskTypeRef（占位构造 Task<.generic<T>>），
            // 不得用声明形/裸模板——后者 TypeSheetFor 命中 void Task
            insts.Add(new MirNewObject(TaskTypeSymbolOf(context, taskTypeRef),
                null, TaskEmptyInit(context, mir, taskTypeRef),
                new List<MirOperand>(), outTask));
            // frame.task = task（DONE 尾/恢复块取回自身 Task 的通道）
            insts.Add(new MirSetField(new MirLocalOperand(outTask), frameOp,
                taskFieldSymbol));
            // 协程句柄创建（frame +1 move 进续体——RcInjection 免配平）
            var handle = Fresh("$mw.handle.", I64);
            insts.Add(new MirCoroutineCreate(frame, resumeSymbol, handle));
            // CoroutineLocal：从启动方当前协程拷有效顶（eager spawn）
            insts.Add(new MirCall(syms.CoroLocalInherit,
                new List<MirOperand> { new MirLocalOperand(handle) }, null));
            // lane 继承（§18.1 第 3 步）→ attachRuntime → noteSpawn → publish
            var disp = Fresh("$mw.disp.", MirType.Of(BilCompilerSymbols.Resolve(context.Module, DispatcherCanonical)));
            var lane = Fresh("$mw.lane.", I32);
            insts.Add(new MirCall(syms.DispatcherGet, new List<MirOperand>(), disp));
            insts.Add(new MirCall(syms.LaneOfCurrent,
                new List<MirOperand> { new MirLocalOperand(disp) }, lane));
            insts.Add(new MirCall(syms.CoroutineSetLane,
                new List<MirOperand> { new MirLocalOperand(handle),
                    new MirLocalOperand(lane) }, null));
            insts.Add(new MirCall(TaskFn(context, taskTypeRef, "attachRuntimeNative"),
                new List<MirOperand> { new MirLocalOperand(outTask),
                    new MirLocalOperand(handle) }, null));
            insts.Add(new MirCall(syms.NoteSpawn,
                new List<MirOperand> { new MirLocalOperand(disp) }, null));
            insts.Add(new MirCall(syms.Publish,
                new List<MirOperand> { new MirLocalOperand(disp),
                    new MirLocalOperand(handle) }, null));
            stub.AddBlock(new MirBlock("entry", insts,
                new MirRet(new MirLocalOperand(outTask))));

            var index = mir.FunctionList.IndexOf(fn);
            if (index < 0)
            {
                throw new CompilerInternalException(
                    "CoroutineSplit：原 fn 不在模块函数表: " + fn.Symbol.Canonical);
            }
            mir.FunctionList[index] = stub;
        }

    }
}
