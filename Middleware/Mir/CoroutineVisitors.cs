using RigiCompiler.Bil;

namespace RigiCompiler.Middleware.Mir
{
    // BIL→MIR 协程簇（MW11a 棒2）：await / 裸 yield 直译为 MirAwait /
    // MirYieldBare（split 前形态，状态机改造归 CoroutineSplitPass）。
    // MW11b 棒3：带 Alarm 的 yield 直译 MirYieldAlarm（split 改写为
    // MirYieldAlarmCall + probe 合成 fn，运行时分类归面内 is 链）。

    internal sealed class AwaitLowering : MirLowerVisitor<AwaitLowering, AwaitInstruction>
    {
        protected override void VisitCore(AwaitInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            // await 可重抛 Task 失败异常：异常边按 MirCall 同口径取本词法
            // 上下文的落点（try 派发垫/逃逸垫；null 留 RcInjection 进传播垫）
            flow.Add(new MirAwait(inst.Task.Name, inst.Result?.Name,
                flow.Tries.CurrentExcTarget()));
        }
    }

    internal sealed class YieldLowering : MirLowerVisitor<YieldLowering, YieldInstruction>
    {
        protected override void VisitCore(YieldInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            if (inst.Alarm != null)
            {
                flow.Add(new MirYieldAlarm(inst.Alarm.Name));
                return;
            }
            flow.Add(new MirYieldBare());
        }
    }
}
