namespace RigiCompiler
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
                var ctx = new LowerContext(body.Method);
                // 闭包存储计划（SYNTAX §5.2）：在体降级前构建——被捕获参数的
                // cell 构造 prologue 前插到体首；计划随 ctx 供全部 rewriter 查询
                ctx.Closure = ClosureStoragePlan.Build(body, ctx, env);
                var lowered = LowerBody(body, ctx, env);
                if (lowered == null) continue;
                if (ctx.Closure.Prologue.Count > 0)
                {
                    lowered = new LoweredBlock(body.Body,
                        ctx.Closure.Prologue.Concat(lowered.Statements).ToList());
                }
                result.Add(new LoweredFunctionBody(body.Method,
                    body.Locals.Concat(ctx.Synth.SynthLocals).ToList(), lowered));
            }
            return result;
        }

        // 逐体降级入口：值块 lambda 的 $$call 体走值块协议（结果局部 +
        // 目标映射注册 + LoweredSeqBlock 包装承载 region breakId，
        // ValueBlockRewriter 唯一入口），产物末尾补 ret 结果局部；其余体
        // （含 init 体/void lambda/单表达式 lambda）按普通函数体块降级。
        // 两路径产物统一过 StructuredExitRouting normalization pass
        // （Stage B：return@ 标记展开为 route/break 形态）
        private static LoweredBlock? LowerBody(BoundFunctionBody body, LowerContext ctx,
            LowerEnvironment env)
        {
            if (body.Method.Owner?.LambdaClosure is { } closure
                && ReferenceEquals(body.Method, closure.Call)
                && closure.ValueBlock != null)
            {
                var result = ctx.Synth.NewSynthLocal(closure.Call.ReturnType!);
                var bodyBreakId = ctx.Synth.NewBreakIdLocal();
                ctx.ExitTargets.Register(closure.ValueBlock, result, bodyBreakId);
                var valueBlock = ValueBlockRewriter.Visit(closure.ValueBlock,
                    new ValueBlockContext(ctx, result), env);
                if (valueBlock == null) return null;
                var statements = new List<LoweredStatement>
                {
                    new LoweredSeqBlock(closure.ValueBlock, valueBlock, isVolatile: false,
                        bodyBreakId),
                    new LoweredReturnStatement(
                        new BoundReturnStatement(closure.ValueBlock.Syntax, null),
                        SynthLocalFactory.ReferenceTo(closure.ValueBlock, result)),
                };
                return StructuredExitRouting.Run(
                    new LoweredBlock(body.Body, statements), ctx, env);
            }
            var lowered = LowerBlockVisitor.Visit(body.Body, ctx, env);
            if (lowered == null) return null;
            return StructuredExitRouting.Run(lowered, ctx, env);
        }
    }
}
