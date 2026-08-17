using System.Collections.Generic;

namespace RigiCompiler
{
    // StructuredExitRouting normalization pass（Stage B，return@/Value-Block
    // Structured Exit 重构）：P4a 末尾跑在 LoweringDriver.LowerBody 产出的
    // LoweredTree 上（两路径：值块 lambda $$call 体与普通体），把
    // LoweredStructuredExit 标记展开为「写结果局部（如有）+ 写 route
    // 局部（仅跨 region 时）+ break 当前 region」，并在需要 multiplex
    // 的 region 后生成 dispatcher（读 route → 写 parent route +
    // break parent）。同 region 尾位 exit（落尾与 break 同落点）省略
    // 冗余 break——isTail 随递归下传，进 FinallyBlock 强制 false
    // （finally 内 exit 必须以 abrupt completion 覆盖 SavedCompletion）。
    // pass 后树中不得残留 LoweredStructuredExit。
    //
    // region = BIL 结构化指令对应的 Lowered 节点（各自携带 .breakid
    // region-exit capability，§16.5）：LoweredIfStatement（True/False
    // 同 region）、LoweredLoop（Judge/Body 同 region）、LoweredSwitch
    // （全部 case/default 同 region）、LoweredTryStatement
    // （TryBlock/全部 Catches/FinallyBlock 同 region）、LoweredSeqBlock
    // （Body 同 region）。LoweredBlock 透明不压栈。
    //
    // route local：每 region 独立、普通 i32 合成局部（.sN），首个跨
    // region exit 时懒建；进入 region 前初始化 0，退出 dispatcher 后
    // 即死。exit 的目标解析查 StructuredExitTargetTable（ordinary
    // lowering 期注册）；P3 已拦截隔循环/隔值块的 return@，故 region
    // 链上必能走到目标 region，走不到即内部不变量破坏。
    internal static class StructuredExitRouting
    {
        // region 帧（pass 期压栈）：BreakId = region 节点的 .breakid
        // 局部；RouteMap = 本 region 内 exit 的目标 breakId → tag
        // （引用相等键，保序——dispatcher 分支序）；RouteLocal 懒建
        private sealed class ExitRoutingRegion
        {
            public ExitRoutingRegion(BoundNode origin, LocalSymbol breakId,
                ExitRoutingRegion? parent)
            {
                Origin = origin;
                BreakId = breakId;
                Parent = parent;
            }

            public BoundNode Origin { get; }

            public LocalSymbol BreakId { get; }

            public ExitRoutingRegion? Parent { get; }

            public LocalSymbol? RouteLocal { get; private set; }

            public List<(LocalSymbol Target, int Tag)> Routes { get; } =
                new List<(LocalSymbol, int)>();

            public int NextTag = 1;

            // 目标 breakId 的 route tag（命中直返；未命中懒建 RouteLocal
            // 并登记，再沿 region 链递归保证父链各 region 都有 relay 条目——
            // 父链走不到目标即内部不变量破坏）
            public int EnsureRoute(LocalSymbol targetBreakId, LowerContext ctx,
                LowerEnvironment env)
            {
                foreach (var (target, tag) in Routes)
                {
                    if (ReferenceEquals(target, targetBreakId)) return tag;
                }
                RouteLocal ??= ctx.Synth.NewSynthLocal(env.Unit.Symbols.Bootstrap.Int32);
                var newTag = NextTag++;
                Routes.Add((targetBreakId, newTag));
                if (Parent == null)
                {
                    throw new CompilerInternalException(
                        "return@ 目标 region 不在 region 链上（P3 不变量破坏）");
                }
                if (!ReferenceEquals(Parent.BreakId, targetBreakId))
                {
                    Parent.EnsureRoute(targetBreakId, ctx, env);
                }
                return newTag;
            }
        }

        // pass 入口：body 为函数体根块（根块透明，region 栈空起步——
        // 根块内 exit 即不变量破坏，isTail 无意义传 false）
        public static LoweredBlock? Run(LoweredBlock body, LowerContext ctx,
            LowerEnvironment env)
        {
            return new LoweredBlock(body.Origin,
                ProcessStatements(body.Statements, null, ctx, env, false));
        }

        // 语句序列递归处理（region 压栈点见类注释）；遇
        // LoweredStructuredExit 展开并截断同块内其后语句（静死——break
        // 当前 region 后本序列不可达）。isTail = 本序列是否处于所属
        // region 末尾位置；块内仅末条语句以 isTail 继续下传（透明
        // LoweredBlock 透传；region 节点子块对子 region 而言尾位重新
        // 从 true 起步——try 的 FinallyBlock 例外强制 false：finally
        // 内 exit 必须以 abrupt completion（break）覆盖
        // SavedCompletion，落尾 Normal 会让 VM 恢复 body 原 completion）
        private static List<LoweredStatement> ProcessStatements(
            IReadOnlyList<LoweredStatement> statements, ExitRoutingRegion? current,
            LowerContext ctx, LowerEnvironment env, bool isTail)
        {
            var output = new List<LoweredStatement>();
            for (var i = 0; i < statements.Count; i++)
            {
                var statement = statements[i];
                var atTail = isTail && i == statements.Count - 1;
                switch (statement)
                {
                    case LoweredStructuredExit exit:
                        ExpandExit(exit, current, output, ctx, env, atTail);
                        return output;
                    case LoweredThrowStatement:
                        // throw 是无条件终止：同块其后语句静态不可达，截断
                        // 不发射（与旧 weaving 的 BlockTerminates 对齐；
                        // 主流编译器同策略——Roslyn 连 Debug 也删、GCC 的
                        // cleanup_cfg 在 -O0 也删、Clang IR gen 在
                        // terminator 后根本不发射；Rigi 无 goto/label，
                        // 死代码不存在被跳入复活的可能）
                        output.Add(statement);
                        return output;
                    case LoweredBlock block:
                        // LoweredBlock 透明不压栈（尾位透传）
                        output.Add(new LoweredBlock(block.Origin,
                            ProcessStatements(block.Statements, current, ctx, env, atTail)));
                        break;
                    case LoweredIfStatement ifStatement:
                    {
                        var region = new ExitRoutingRegion(ifStatement.Origin,
                            ifStatement.BreakId, current);
                        var node = new LoweredIfStatement(ifStatement.Origin,
                            ifStatement.Condition,
                            ProcessBlock(ifStatement.TrueBlock, region, ctx, env, true),
                            ifStatement.FalseBlock == null ? null
                                : ProcessBlock(ifStatement.FalseBlock, region, ctx, env, true),
                            ifStatement.BreakId);
                        output.AddRange(FinishRegion(region, node, ctx, env));
                        // 逃逸型 if 表达式（全分支向外逃逸）：同逃逸型 seq
                        // 的截断哲学（详见下方 EscapesOnAllPaths 注释）
                        if (ifStatement.Origin is BoundIfExpression ifExpression
                            && EscapesOnAllPaths(ifExpression))
                        {
                            return output;
                        }
                        break;
                    }
                    case LoweredLoop loop:
                    {
                        var region = new ExitRoutingRegion(loop.Origin, loop.BreakId, current);
                        var node = new LoweredLoop((BoundLoop)loop.Origin, loop.IsRev,
                            ProcessBlock(loop.Judge, region, ctx, env, true), loop.Condition,
                            ProcessBlock(loop.Body, region, ctx, env, true), loop.BreakId);
                        output.AddRange(FinishRegion(region, node, ctx, env));
                        break;
                    }
                    case LoweredSwitch switchStatement:
                    {
                        var region = new ExitRoutingRegion(switchStatement.Origin,
                            switchStatement.BreakId, current);
                        var cases = new List<LoweredSwitchCase>();
                        foreach (var switchCase in switchStatement.Cases)
                        {
                            cases.Add(new LoweredSwitchCase(switchCase.Origin, switchCase.Value,
                                ProcessBlock(switchCase.Body, region, ctx, env, true)));
                        }
                        var node = new LoweredSwitch(switchStatement.Origin,
                            switchStatement.Selector, cases,
                            ProcessBlock(switchStatement.DefaultBody, region, ctx, env, true),
                            switchStatement.BreakId);
                        output.AddRange(FinishRegion(region, node, ctx, env));
                        // 逃逸型 switch 表达式（全值路径产物）：同逃逸型
                        // seq 的截断哲学
                        if (switchStatement.Origin is BoundSwitchExpression switchExpression
                            && EscapesOnAllPaths(switchExpression))
                        {
                            return output;
                        }
                        break;
                    }
                    case LoweredTryStatement tryStatement:
                    {
                        var region = new ExitRoutingRegion(tryStatement.Origin,
                            tryStatement.BreakId, current);
                        var catches = new List<LoweredTryCatch>();
                        foreach (var tryCatch in tryStatement.Catches)
                        {
                            catches.Add(new LoweredTryCatch(tryCatch.Origin, tryCatch.Variable,
                                tryCatch.ExceptionType,
                                ProcessBlock(tryCatch.Body, region, ctx, env, true)));
                        }
                        var node = new LoweredTryStatement(tryStatement.Origin,
                            ProcessBlock(tryStatement.TryBlock, region, ctx, env, true), catches,
                            tryStatement.FinallyBlock == null ? null
                                // finally 例外：块内 exit 永不省略 break
                                : ProcessBlock(tryStatement.FinallyBlock, region, ctx, env, false),
                            tryStatement.ExceptionSlot, tryStatement.BreakId);
                        output.AddRange(FinishRegion(region, node, ctx, env));
                        break;
                    }
                    case LoweredSeqBlock seqBlock:
                    {
                        var region = new ExitRoutingRegion(seqBlock.Origin, seqBlock.BreakId,
                            current);
                        var node = new LoweredSeqBlock(seqBlock.Origin,
                            ProcessBlock(seqBlock.Body, region, ctx, env, true), seqBlock.IsVolatile,
                            seqBlock.BreakId);
                        output.AddRange(FinishRegion(region, node, ctx, env));
                        // 逃逸型值块表达式（seq 表达式体全路径向外逃逸，
                        // 或 pattern 路径的 switch 表达式——其 if 链外包的
                        // seq region Origin 即 BoundSwitchExpression）：
                        // P3 已证全路径向外逃逸，本 region 不存在「正常
                        // 完成」，结果局部在任何路径上都不会被写，其后同块
                        // 语句（含消费者对该局部的读取）动态不可达——按
                        // throw 同款哲学截断不发射，消除验证器 §21.4 静态
                        // 可见的死读（route==0 的 fall-through 臂保留但
                        // 只落到块尾，不再触及任何读）
                        if (seqBlock.Origin is BoundSeqExpression { Body.ValueType: null }
                            || (seqBlock.Origin is BoundSwitchExpression patternSwitch
                                && EscapesOnAllPaths(patternSwitch)))
                        {
                            return output;
                        }
                        break;
                    }
                    default:
                        output.Add(statement);
                        break;
                }
            }
            return output;
        }

        private static LoweredBlock ProcessBlock(LoweredBlock block, ExitRoutingRegion region,
            LowerContext ctx, LowerEnvironment env, bool isTail)
        {
            return new LoweredBlock(block.Origin,
                ProcessStatements(block.Statements, region, ctx, env, isTail));
        }

        // exit 标记展开：写结果局部（return@语句seq 无值跳过）→
        // 同 region 直 break 目标 breakId（尾位 exit 省略——落尾与
        // break 落点相同；finally 块经 isTail=false 强制保留 break，
        // 必须以 abrupt completion 覆盖 SavedCompletion）；跨 region
        // 写本 region 的 route 局部后 break 本 region（dispatcher 链
        // 向上 relay）
        private static void ExpandExit(LoweredStructuredExit exit, ExitRoutingRegion? current,
            List<LoweredStatement> output, LowerContext ctx, LowerEnvironment env, bool isTail)
        {
            var (resultLocal, targetBreakId) = ctx.ExitTargets.Find(exit.Target);
            if (exit.Value != null)
            {
                output.Add(new LoweredAssignmentStatement(exit.Origin,
                    SynthLocalFactory.ReferenceTo(exit.Origin,
                        resultLocal ?? throw new CompilerInternalException(
                            "return@值块目标缺结果局部（注册不变量破坏）")),
                    exit.Value));
            }
            if (current == null)
            {
                throw new CompilerInternalException(
                    "return@ 标记不在任何 region 内（P3/P4a 不变量破坏）");
            }
            if (ReferenceEquals(current.BreakId, targetBreakId))
            {
                // 同 region 尾位 exit：落尾与 break 落点完全相同，
                // 省略冗余 break（finally 块经 isTail=false 强制保留）
                if (!isTail)
                {
                    output.Add(new LoweredBreakStatement(exit.Origin, targetBreakId));
                }
                return;
            }
            var tag = current.EnsureRoute(targetBreakId, ctx, env);
            output.Add(new LoweredAssignmentStatement(exit.Origin,
                SynthLocalFactory.ReferenceTo(exit.Origin, current.RouteLocal!),
                IntConstant(exit.Origin, tag, env)));
            output.Add(new LoweredBreakStatement(exit.Origin, current.BreakId));
        }

        // region 收尾（children 处理完）：无 route 直通；有 route 把
        // region 节点原位展开为三条——route = 0、原 region 节点、
        // dispatcher（else-if 链：route == tag → relay；无 else，
        // 0/未匹配 fallthrough）
        private static List<LoweredStatement> FinishRegion(ExitRoutingRegion region,
            LoweredStatement node, LowerContext ctx, LowerEnvironment env)
        {
            if (region.RouteLocal == null)
            {
                return new List<LoweredStatement> { node };
            }
            var output = new List<LoweredStatement>
            {
                new LoweredAssignmentStatement(region.Origin,
                    SynthLocalFactory.ReferenceTo(region.Origin, region.RouteLocal),
                    IntConstant(region.Origin, 0, env)),
                node,
            };
            // dispatcher else-if 链：自末条 route 向内包（最内层无 else）
            LoweredStatement? chain = null;
            for (var i = region.Routes.Count - 1; i >= 0; i--)
            {
                var (target, tag) = region.Routes[i];
                var condition = new LoweredBinaryExpression(region.Origin, BilIntrinsicOp.CmpEq,
                    SynthLocalFactory.ReferenceTo(region.Origin, region.RouteLocal),
                    IntConstant(region.Origin, tag, env), env.Unit.Symbols.Bootstrap.Bool);
                chain = new LoweredIfStatement(region.Origin, condition,
                    new LoweredBlock(region.Origin, BuildRelay(region, target, ctx, env)),
                    chain == null ? null
                        : new LoweredBlock(region.Origin,
                            new List<LoweredStatement> { chain }),
                    ctx.Synth.NewBreakIdLocal());
            }
            output.Add(chain!);
            return output;
        }

        // dispatcher 分支体（relay）：目标是父 region 直 break 父
        // breakId；否则写父 route 局部（父链 tag 经 EnsureRoute 登记）
        // 再 break 父 breakId
        private static List<LoweredStatement> BuildRelay(ExitRoutingRegion region,
            LocalSymbol targetBreakId, LowerContext ctx, LowerEnvironment env)
        {
            var parent = region.Parent ?? throw new CompilerInternalException(
                "route region 缺父 region（region 链不变量破坏）");
            var relay = new List<LoweredStatement>();
            if (!ReferenceEquals(parent.BreakId, targetBreakId))
            {
                var parentTag = parent.EnsureRoute(targetBreakId, ctx, env);
                relay.Add(new LoweredAssignmentStatement(region.Origin,
                    SynthLocalFactory.ReferenceTo(region.Origin, parent.RouteLocal!),
                    IntConstant(region.Origin, parentTag, env)));
            }
            relay.Add(new LoweredBreakStatement(region.Origin, parent.BreakId));
            return relay;
        }

        // 逃逸型 if/switch 表达式判定：全部分支值块 ValueType == null
        //（P3 已证各自全路径向外逃逸、表达式已按 ExpectedType 定型）——
        // 结果局部任何路径都不写，其后同块语句静态死，按 throw 同款
        // 哲学截断（与逃逸型 seq 表达式同口径）
        private static bool EscapesOnAllPaths(BoundIfExpression expression)
        {
            return expression.TrueBranch.ValueType == null
                && expression.FalseBranch.ValueType == null;
        }

        private static bool EscapesOnAllPaths(BoundSwitchExpression expression)
        {
            return expression.Cases.All(c => c.Body.ValueType == null)
                && expression.DefaultBody.ValueType == null;
        }

        // i32 合成常量（route tag / 初始化 0；LoweredConstantExpression
        // Stage B 起支持 int——ConstantEmitter 同步扩展）
        private static LoweredConstantExpression IntConstant(BoundNode origin, int value,
            LowerEnvironment env)
        {
            return new LoweredConstantExpression(origin, value, env.Unit.Symbols.Bootstrap.Int32);
        }
    }
}
