namespace LatteCompiler
{
    // P4a 降级驱动器：逐函数体创建独立 LowerContext 降级；合成局部
    // （.s/.b 前缀，脱糖产物）跟在源码局部之后收尾。遇失败（null）跳过
    // 该函数体——函数体之间诊断互不阻断（同 P3 原则）。
    internal sealed class LoweringDriver
    {
        private readonly LowerEnvironment env;
        private readonly IReadOnlyList<BoundFunctionBody> bodies;

        public LoweringDriver(LowerEnvironment env, IReadOnlyList<BoundFunctionBody> bodies)
        {
            this.env = env;
            this.bodies = bodies;
        }

        public IReadOnlyList<LoweredFunctionBody> Run()
        {
            var result = new List<LoweredFunctionBody>();
            foreach (var body in bodies)
            {
                // S11 烘焙合成 fn（S11a 特化/原始体/shim 的 "." 前缀保留名）
                // 与 S11b 转发壳（WrapperChain 非空）：发射统一归 S11d，
                // lowering 无消费者（且 proxy 体内 wrapper place 成员访问
                // 的降级归 S11c）——跳过，不落归口噪音
                if (body.Method.Name.StartsWith('.')) continue;
                if (body.Method.WrapperChain != null) continue;
                var ctx = new LowerContext(body.Method);
                var lowered = LowerBlockVisitor.Visit(body.Body, ctx, env);
                if (lowered == null) continue;
                result.Add(new LoweredFunctionBody(body.Method,
                    body.Locals.Concat(ctx.Synth.SynthLocals).ToList(), lowered));
            }
            return result;
        }
    }
}
