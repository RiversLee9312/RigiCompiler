using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware
{
    /// <summary>
    /// Middleware 会话中枢：每个已过门禁的 BilModule 一个实例，贯穿
    /// Symbols → Binding → MIR → Passes → Layout → Emit 各层，逐层挂载各自
    /// 产物（参照 VM 侧 VmContext 模式），也是流水线阶段（IMwStage）之间
    /// 唯一的产物交换物。当前挂载：符号表（构造时）+ 布局（LayoutStage）+
    /// MIR（MirBuildStage 挂载，随后 Passes/ 原地改写指令列表）；Binding
    /// 缓存等随后续阶段在此扩展。
    /// </summary>
    public sealed class MwContext
    {
        // 已过 Gate 门禁的 BIL 模块（只读消费；各层不得回写）
        public BilModule Module { get; }

        // MW1 驻留符号表
        public MwSymbolTable Symbols { get; }

        // MW3 产物：由 MirBuilder.Build 挂载（构建前为 null）
        public MirModule? Mir { get; internal set; }

        // MW4 产物：布局计划表（LayoutStage 挂载；只依赖符号表，不依赖 MIR）
        public LayoutPlanTable? Layout { get; internal set; }

        // 派发闭包查询（Layout 实现、Mir 消费；Layout 未挂载时为 null）
        public IMwDispatchQuery? DispatchQuery => Layout;

        public MwContext(BilModule module)
        {
            Module = module;
            Symbols = MwSymbolTable.Build(module);
        }
    }
}
