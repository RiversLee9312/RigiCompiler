using System.Collections.Generic;
using RigiCompiler.Middleware.Passes;

namespace RigiCompiler.Middleware.Pipeline
{
    /// <summary>
    /// 流水线驱动器（单一调度器）：持线性阶段序，依次把 MwContext 交给各
    /// 阶段。MW4 起语言语义 pass 群（IndexOperatorLowering/AccessorLowering
    /// 及随后 WrapperBaking/RcInjection/CoroutineSplit/CellElim/Devirt）以
    /// IMwStage 登记在 MIR 构建之后——pass 之间以 MIR 为唯一交换物
    /// （MIDDLEWARE_ARCHITECTURE §3 MW4）。Emit/链接持有外部资源（LLVM
    /// 模块/工具链进程），属驱动尾，不进流水线，留 Cli 编排。
    /// </summary>
    public sealed class MwPipeline
    {
        // 标准编译管线：BIL（已过门禁）→ 布局（只依赖符号表，先行——
        // MirReachability 的派发闭包要查 vtable 计划）→ MIR 构建 →
        // IndexOperatorLowering → AccessorLowering
        public static MwPipeline CreateDefault()
        {
            return new MwPipeline()
                .Add(new LayoutStage())
                .Add(new MirBuildStage())
                .Add(new IndexOperatorLoweringPass())
                .Add(new AccessorLoweringPass());
        }

        private readonly List<IMwStage> _stages = new();

        public MwPipeline Add(IMwStage stage)
        {
            _stages.Add(stage);
            return this;
        }

        public void Run(MwContext context)
        {
            foreach (var stage in _stages)
            {
                Logger.Verbose("Middleware", $"阶段 {stage.Name}");
                stage.Run(context);
            }
        }
    }
}
