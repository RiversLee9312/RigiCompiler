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
        // Execution 职责；与主文件共享同一类型、字段及生命周期。

        private void ExecuteSplit(MwContext context, MirModule mir, SplitPlan plan)
        {
            var fn = plan.Fn;
            string FieldOf(MirLocal local) => SyntheticTypePlanner.FrameFieldSymbol(
                plan.FrameCanonical, local.Name, local.Type.Canonical);

            var resumeFn = BuildResumeFunction(context, mir, plan, FieldOf);
            mir.AddFunction(resumeFn);
            if (plan.Mode == SplitMode.Tasked)
            {
                if (IsZeroArgAsyncCall(fn))
                {
                    var thisLocal = fn.Parameters.FirstOrDefault(p => p.Name == ".this")
                        ?? throw new CompilerInternalException(
                            "0 参 $$call 缺 .this: " + fn.Symbol.Canonical);
                    _coldBinds.Add(new ColdBindEntry
                    {
                        OwnerCanonical = fn.Symbol.Owner!.Canonical,
                        CallCanonical = fn.Symbol.Canonical,
                        ResumeSymbol = plan.ResumeSymbol,
                        FrameCanonical = plan.FrameCanonical,
                        FrameType = plan.FrameType,
                        FrameInit = plan.FrameInit,
                        TaskTypeRef = plan.TaskTypeRef!,
                        TaskConstructionRef = plan.TaskConstructionRef!,
                        ThisType = thisLocal.Type,
                    });
                }
                ReplaceWithStub(context, mir, fn, plan.FrameType, plan.FrameMirType,
                    plan.FrameInit, plan.ResumeSymbol, plan.TaskTypeRef!,
                    plan.TaskFieldSymbol!, FieldOf);
            }
            else
            {
                ReplaceWithTrap(mir, fn);
            }

            // 自检②③：state 分发表项恰覆盖 入口+挂起点；frame 字段 =
            // state + 保存槽 + $mw.task（Tasked）/ $mw.result?（Plain）
            var layoutPlan = context.Layout!.Find(plan.FrameCanonical)
                ?? throw new CompilerInternalException("frame 布局计划缺失: " + plan.FrameCanonical);
            var expected = plan.SavedSlots.Count + 1
                + (plan.Mode == SplitMode.Tasked ? 1 : 0)
                + (plan.ResultFieldSymbol != null ? 1 : 0);
            if (layoutPlan.Fields.Count != expected)
            {
                throw new CompilerInternalException(
                    $"CoroutineSplit 自检失败：{fn.Symbol.Canonical} frame 字段数 {layoutPlan.Fields.Count} ≠ 保存槽 {plan.SavedSlots.Count} + state + task/result");
            }
        }

        // plain tainted fn 的原符号陷阱 stub：全部调用点已协议化
        //（taint 闭包 + 调用点改写），直调残留属内部错误——陷阱体
        // 仅作防御（模块内符号保留，Emit 零特例）
        private static void ReplaceWithTrap(MirModule mir, MirFunction fn)
        {
            var trap = new MirFunction(fn.Symbol, fn.ReturnType, fn.Parameters,
                new List<MirLocal>(fn.Parameters),
                new List<MirBlock>
                {
                    new MirBlock("entry", new List<MirInst>(), new MirUnreachable()),
                }, fn.IsEntrypoint);
            var index = mir.FunctionList.IndexOf(fn);
            if (index < 0)
            {
                throw new CompilerInternalException(
                    "CoroutineSplit：tainted fn 不在模块函数表: " + fn.Symbol.Canonical);
            }
            mir.FunctionList[index] = trap;
        }

        // ===== B-1 根驱动：$mw.main.settle 合成 =====
        // tainted main 的 Task 包装 split 后，rigi_entry 在 drain 至
        // quiescence 之后调本 fn 取 main 结果/重抛 main 失败（对齐 VM
        // BilVm.Run 的 main.Failure 优先汇总）：failureNodeId != 0 →
        // MirFailureLoad 取异常 + MirThrow（pending 置位，rigi_entry
        // 收进 entry.exc 进 reporter）；否则解包 result 字段返回
        //（void main 恒 0）
        public static string MainSettleCanonicalOf(string taskTypeRef) =>
            "$mw.main.settle(task:" + taskTypeRef + ")@.i32";

        private void SynthesizeMainSettle(MwContext context, MirModule mir,
            MirFunction mainFn)
        {
            var taskTypeRef = TaskTypeRefOf(mainFn.ReturnType);
            var symbol = ProxyBakeSupport.SyntheticMember(
                MainSettleCanonicalOf(taskTypeRef), owner: null);
            var taskParam = new MirLocal("task", MirType.Of(taskTypeRef));
            var settle = new MirFunction(symbol, I32,
                new List<MirLocal> { taskParam }, new List<MirLocal> { taskParam },
                new List<MirBlock>(), false);
            string Fresh(string prefix, MirType type) =>
                ProxyWildcardAbi.FreshLocal(settle, prefix, type);
            var taskOp = new MirLocalOperand("task");
            var nodeId = Fresh("$mw.settle.nid.", I64);
            var zero = Fresh("$mw.settle.z.", I64);
            var isFail = Fresh("$mw.settle.isf.", Bool);
            settle.AddBlock(new MirBlock("entry", new List<MirInst>
            {
                new MirGetField(taskOp, TaskField(context, taskTypeRef, "failureNodeId", ".i64"),
                    nodeId),
                new MirLoadResource(ProxyWildcardAbi.AddI64Resource(context, 0), zero),
                new MirBinaryIntrinsic(BilBinaryOp.CmpNe, new MirLocalOperand(nodeId),
                    new MirLocalOperand(zero), I64, I64, Bool, isFail),
            }, new MirCondBranch(new MirLocalOperand(isFail), "mw.settle.fail",
                "mw.settle.ok")));

            var okInsts = new List<MirInst>();
            var okResult = Fresh("$mw.settle.r.",
                mainFn.ReturnType.IsVoid ? I32 : mainFn.ReturnType);
            if (!mainFn.ReturnType.IsVoid)
            {
                var rn = Fresh("$mw.settle.rn.",
                    MirType.Of(".nullable<" + mainFn.ReturnType.Canonical + ">"));
                okInsts.Add(new MirGetField(taskOp,
                    TaskField(context, taskTypeRef, "result",
                        ".nullable<" + mainFn.ReturnType.Canonical + ">"), rn));
                okInsts.Add(new MirUnwrapNullable(new MirLocalOperand(rn),
                    mainFn.ReturnType, okResult));
            }
            else
            {
                okInsts.Add(new MirLoadResource(
                    ProxyWildcardAbi.AddI32Resource(context, 0), okResult));
            }
            settle.AddBlock(new MirBlock("mw.settle.ok", okInsts,
                new MirRet(new MirLocalOperand(okResult))));

            var exc = Fresh("$mw.settle.exc.", Any);
            settle.AddBlock(new MirBlock("mw.settle.fail", new List<MirInst>
            {
                new MirFailureLoad(nodeId, exc),
                new MirThrow(new MirLocalOperand(exc), null),
            }, new MirRetThrow()));
            mir.AddFunction(settle);
        }

        private static string TaskTypeRefOf(MirType returnType) =>
            returnType.IsVoid
                ? "core.coroutine::Task"
                : "core.coroutine::Task<" + returnType.Canonical + ">";

        private MwMemberSymbol TaskEmptyInit(MwContext context, MirModule mir,
            string taskTypeRef)
        {
            var declarationRef = TaskPrefixOf(taskTypeRef);
            if (_taskEmptyInits.TryGetValue(declarationRef, out var cached))
            {
                return cached;
            }
            var type = RequireTaskType(context, taskTypeRef);
            var symbol = ProxyBakeSupport.SyntheticMember(
                declarationRef + "$init()@.void", type);
            var thisParam = new MirLocal(".this", MirType.Of(declarationRef));
            mir.AddFunction(new MirFunction(symbol, MirType.Of(".void"),
                new List<MirLocal> { thisParam }, new List<MirLocal> { thisParam },
                new List<MirBlock>
                {
                    new MirBlock("entry", new List<MirInst>(), new MirRet(null)),
                }, false));
            _taskEmptyInits.Add(declarationRef, symbol);
            return symbol;
        }

        // Task 构造类型符号：闭合构造经 MwTypeSymbol 包装（CallVisitors
        // 同口径）。模板 Canonical 是裸 `core.coroutine::Task`（同名不同
        // 元数共用声明符号）——TypeSheetFor 精确命中 arity-0 的 void Task
        // sheet 后，对象头/vtable/gate 全错（void gate@72 vs Task<T>
        // gate@88）。泛型 Task 必须包装带实参的 identity。
        private static MwTypeSymbol TaskTypeSymbolOf(MwContext context,
            string taskTypeRef)
        {
            var template = RequireTaskType(context, taskTypeRef);
            if (template.Declaration.GenericParameters.Count == 0)
            {
                return template;
            }
            var identity = GenericTaskSheetIdentity(taskTypeRef, template);
            // 状态机切分会引入 BIL 中尚未出现的 Task<R>（例如被污染的
            // 同步返回函数）。为该实际构造补布局，不能依赖发射器回退模板。
            if (GenericAbi.IsClosedConstructed(identity) && context.Layout != null)
                ConstructedLayout.ResolveConstructed(identity, context.Symbols, context.Layout,
                    new HashSet<string>(System.StringComparer.Ordinal),
                    new HashSet<string>(context.Module.Functions.Select(f => f.Symbol),
                        System.StringComparer.Ordinal));
            return new MwTypeSymbol(identity, template);
        }

        // 声明形 Task<TReturn> 会让 Layout.Find 命中模板后 WriteHiddenTypeIds
        // 对实参名取 sheet 崩；改写为占位构造。已是占位/闭合构造则原样。
        private static string GenericTaskSheetIdentity(string taskTypeRef,
            MwTypeSymbol template)
        {
            var normalized = MwTypeKey.Normalize(taskTypeRef);
            if (normalized == template.Canonical
                || normalized == TaskPrefixOf(taskTypeRef))
            {
                var param = template.Declaration.GenericParameters[0];
                return template.Canonical + "<.generic<$.generic." + param + ">>";
            }
            return normalized;
        }

    }
}
