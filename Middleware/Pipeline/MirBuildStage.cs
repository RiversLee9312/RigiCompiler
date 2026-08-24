namespace RigiCompiler.Middleware
{
    /// <summary>
    /// MIR 构建阶段（MW3）。读：Module + Symbols（可达序经 MirReachability）；
    /// 写：Mir（MirBuilder.Build 挂载）。
    /// </summary>
    public sealed class MirBuildStage : IMwStage
    {
        public string Name => "MirBuild";

        public void Run(MwContext context)
        {
            MirBuilder.Build(context);
        }
    }
}
