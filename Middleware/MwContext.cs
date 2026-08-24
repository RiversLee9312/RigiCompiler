using RigiCompiler.Bil;

namespace RigiCompiler.Middleware
{
    /// <summary>
    /// Middleware 会话中枢：每个已过门禁的 BilModule 一个实例，贯穿
    /// Symbols → Binding → MIR → Passes → Layout → Emit 各层，逐层挂载各自
    /// 产物（参照 VM 侧 VmContext 模式）。当前挂载：符号表（构造时）+ MIR
    /// （MirBuilder.Build）；Binding 缓存、布局计划等随后续阶段在此扩展。
    /// </summary>
    public sealed class MwContext
    {
        // 已过 Gate 门禁的 BIL 模块（只读消费；各层不得回写）
        public BilModule Module { get; }

        // MW1 驻留符号表
        public MwSymbolTable Symbols { get; }

        // MW3 产物：由 MirBuilder.Build 挂载（构建前为 null）
        public MirModule? Mir { get; internal set; }

        public MwContext(BilModule module)
        {
            Module = module;
            Symbols = MwSymbolTable.Build(module);
        }
    }
}
