namespace RigiCompiler
{
    // 表达式降级（S5–S8c；前置语句追加到当前块输出列表）。
    // 自旧 LowerSession.LowerExpression 各分支迁移，行为不变。

    // 表达式设施：独立块上下文降级（judge/分支块合成用）
    internal static class ExpressionFacility
    {
        // 在独立块上下文里降级表达式并写目标局部（前置语句随块走）；
        // 产物是单语句合成块（Origin 指最近语法来源）。值按目标局部类型
        // cast 物化（BIL §6.5——#28④ if? 右操作数降级 Any→T 等同路径）
        public static LoweredBlock? LowerAssignInNewBlock(BoundNode origin, BoundExpression value,
            LocalSymbol target, LowerContext ctx, LowerEnvironment env)
        {
            var statements = new List<LoweredStatement>();
            ctx.Output.Push(statements);
            try
            {
                var lowered = LowerExpressionDispatcher.Visit(value, ctx, env);
                if (lowered == null) return null;
                lowered = LoweringFacility.EnsureDeclaredType(origin, lowered, target.Type);
                statements.Add(new LoweredAssignmentStatement(origin,
                    SynthLocalFactory.ReferenceTo(origin, target), lowered));
                return new LoweredBlock(origin, statements);
            }
            finally
            {
                ctx.Output.Pop();
            }
        }
    }

    internal sealed class LiteralRewriter : LoweredVisitor<LiteralRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            return new LoweredLiteralExpression((BoundLiteralExpression)node);
        }
    }

    // 值引用（局部/参数）：闭包存储计划（SYNTAX §5.2）命中时被捕获符号
    // 改写为 cell getValue 调用 / 捕获字段访问；未命中为普通变量引用
    internal sealed class ValueReferenceRewriter
        : LoweredVisitor<ValueReferenceRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var reference = (BoundValueReferenceExpression)node;
            return ctx.Closure.TryRewriteValueRead(reference)
                ?? new LoweredValueReferenceExpression(reference, reference.Symbol);
        }
    }

    // 静态/全局字段引用：统一 cell 存储（SYNTAX §14.3）命中时 cell 化
    // 字段的值读取改写为 cell getValue 调用；未命中为普通字段引用
    internal sealed class FieldReferenceRewriter
        : LoweredVisitor<FieldReferenceRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var reference = (BoundFieldReferenceExpression)node;
            return CellStorageLowering.TryRewriteStaticRead(reference, env)
                ?? new LoweredFieldReferenceExpression(reference, reference.Field);
        }
    }

    // 二元运算：P3 仅对内建 bool 定型 and/or（SYNTAX §13.2：未被重载
    // 才短路），Op=And/Or 即短路展开（BIL §11.3）；其余恒等
    internal sealed class BinaryRewriter : LoweredVisitor<BinaryRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var binary = (BoundBinaryExpression)node;
            // 短路仅内建 bool（SYNTAX §13.2：用户类型 and/or 两侧求值后
            // 调 operator，发普通 and/or 指令由 VM 派发）
            if (binary.Op is BilIntrinsicOp.And or BilIntrinsicOp.Or
                && binary.Left.Type is TypeSymbol leftLogic
                && leftLogic.IntrinsicOps.Contains(binary.Op))
            {
                return ShortCircuitRewriter.Lower(binary, ctx, env);
            }
            // 兄弟求值序保护：右操作数产前置语句时左操作数物化合成局部
            //（如 left() + (rightA() and rightB()) 中 left() 须先执行）
            var guard = new EvalOrderGuard(ctx);
            var left = guard.Lower(binary.Left, env);
            var right = guard.Lower(binary.Right, env);
            if (left == null || right == null) return null;
            var sealedOperands = guard.Seal();
            return new LoweredBinaryExpression(binary, binary.Op,
                sealedOperands[0], sealedOperands[1]);
        }
    }

    internal sealed class UnaryRewriter : LoweredVisitor<UnaryRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var unary = (BoundUnaryExpression)node;
            var operand = LowerExpressionDispatcher.Visit(unary.Operand, ctx, env);
            if (operand == null) return null;
            return new LoweredUnaryExpression(unary, unary.Op, operand);
        }
    }

    internal sealed class CallExpressionRewriter
        : LoweredVisitor<CallExpressionRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var call = (BoundCallExpression)node;
            // 兄弟求值序保护（EvalOrderGuard）：实参加间接调用目标统一
            // 登记，任一兄弟产前置语句时先求值槽位物化合成局部
            var guard = new EvalOrderGuard(ctx);
            var arguments = LoweringFacility.LowerArguments(call.Arguments, call.Method.Parameters,
                ctx, env, guard, parameterMethod: call.Method, methodTypeArguments: call.TypeArguments);
            if (arguments == null) return null;
            // S9d-2：泛型包无值子节点，恒等透传（打包归 P4b）
            var genericPack = call.GenericPack == null ? null
                : new LoweredGenericVarArgsArgument(call.GenericPack, call.GenericPack.IsNamed,
                    call.GenericPack.TypeArguments, call.GenericPack.NamedTypes);
            // §15.3 间接调用：目标对象表达式降级透传（§10.2 求值序——
            // 保持「实参先、目标后」的既有相对顺序不变）
            LoweredExpression? indirectTarget = null;
            if (call.IsIndirect)
            {
                indirectTarget = guard.Lower(call.IndirectTarget!, env);
                if (indirectTarget == null) return null;
            }
            var sealedSlots = guard.Seal();
            var sealedArguments = new List<LoweredExpression>(call.Arguments.Count);
            for (var i = 0; i < call.Arguments.Count; i++) sealedArguments.Add(sealedSlots[i]);
            if (call.IsIndirect) indirectTarget = sealedSlots[^1];
            return new LoweredCallExpression(call, call.Method, sealedArguments, call.TypeArguments,
                genericPack, call.IsIndirect, indirectTarget);
        }
    }

    internal sealed class LambdaRewriter
        : LoweredVisitor<LambdaRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            // lambda 对象模型（SYNTAX §5.2）：表达式降级为隐藏类构造——
            // new type(..lambda..UUID) [逐捕获 cell/this 实参]；实参取当前
            // 函数上下文中各捕获符号的 cell 对象（cell 沿引用传递共享语义）
            var lambda = (BoundLambdaExpression)node;
            var arguments = new List<LoweredExpression>();
            foreach (var capture in lambda.Closure.Captures)
            {
                if (!capture.IsThis && !ctx.Closure.HasCellObjectFor(capture.Symbol))
                {
                    // stdlib 缺席的降级路径（P3 已就 core::Func/Action 族
                    // 缺失落诊断）——不抛出，跳过本函数体
                    env.Error(lambda.Syntax.Span,
                        "P4: lambda closure lowering requires the stdlib core::Cell family");
                    return null;
                }
                arguments.Add(capture.IsThis
                    ? ctx.Closure.ThisValueFor(lambda, ctx.Method.Owner
                        ?? env.Unit.Symbols.ErrorType)
                    : ctx.Closure.CellObjectFor(lambda, capture.Symbol));
            }
            // Method wrapper 带实参（SYNTAX §14.4）：构造点改走 new.wrapped，
            // 第一表 = ..init.wrapper 实参（已在外层函数作用域绑定），
            // 第二表 = 捕获/this 实参
            IReadOnlyList<LoweredExpression>? wrapperArguments = null;
            if (lambda.Closure.InitWrapper is { Parameters.Count: > 0 })
            {
                wrapperArguments = LoweringFacility.LowerArguments(
                    lambda.Closure.WrapperInitArguments, lambda.Closure.InitWrapper.Parameters,
                    ctx, env);
                if (wrapperArguments == null) return null;
            }
            return new LoweredNewExpression(lambda, lambda.Closure.Init, arguments,
                lambda.Type, wrapperArguments);
        }
    }

    internal sealed class NewExpressionRewriter
        : LoweredVisitor<NewExpressionRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var newExpression = (BoundNewExpression)node;
            // 兄弟求值序保护（EvalOrderGuard）：普通实参加 wrapper 前缀
            // 实参统一登记（保持「普通实参先、wrapper 实参后」的既有顺序，
            // 与 NewExpressionEmitter 发射序一致）
            var guard = new EvalOrderGuard(ctx);
            var arguments = LoweringFacility.LowerArguments(newExpression.Arguments,
                newExpression.Init?.Parameters, ctx, env, guard,
                newExpression.Type as TypeSymbol);
            if (arguments == null) return null;
            // cell 隐藏子类构造（companion init 里构造静态字段 cell，§8.7）：
            // 有参 ..init.wrapper → new.wrapped 前缀实参（与局部 cell 构造点同）
            var cellStorage = newExpression.Init?.Owner?.CellStorage;
            var hasWrapperArgs = cellStorage?.InitWrapper is { Parameters.Count: > 0 };
            if (hasWrapperArgs)
            {
                var wrapperArgs = CellWrappedNew.LowerWrapperInitArgs(newExpression,
                    cellStorage!, ctx, env, guard);
                if (wrapperArgs == null) return null;
            }
            var sealedSlots = guard.Seal();
            var sealedArguments = new List<LoweredExpression>(newExpression.Arguments.Count);
            for (var i = 0; i < newExpression.Arguments.Count; i++)
                sealedArguments.Add(sealedSlots[i]);
            IReadOnlyList<LoweredExpression>? wrapperArgs2 = null;
            if (hasWrapperArgs)
            {
                // 封口后半段 = wrapper 前缀实参（Track 序 = 求值序切片）
                var sealedWrapper = new List<LoweredExpression>();
                for (var i = newExpression.Arguments.Count; i < sealedSlots.Count; i++)
                    sealedWrapper.Add(sealedSlots[i]);
                wrapperArgs2 = sealedWrapper;
            }
            return new LoweredNewExpression(newExpression, newExpression.Init, sealedArguments,
                wrapperArguments: wrapperArgs2);
        }
    }

    // 动态 new（SYNTAX §3.7，BIL §14.2 new.indirect）：TypeValue 与实参
    // 共用一次求值序保护（目标值先、实参后——与源文书写序一致）；
    // 实参无静态 init 形参（运行期重载解析），声明类型代入直通
    internal sealed class DynamicNewRewriter
        : LoweredVisitor<DynamicNewRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var dynamicNew = (BoundDynamicNewExpression)node;
            var guard = new EvalOrderGuard(ctx);
            LoweredExpression? typeValue = null;
            if (dynamicNew.TypeValue != null)
            {
                typeValue = guard.Lower(dynamicNew.TypeValue, env);
                if (typeValue == null) return null;
            }
            var arguments = LoweringFacility.LowerArguments(dynamicNew.Arguments, null, ctx, env,
                guard);
            if (arguments == null) return null;
            var sealedSlots = guard.Seal();
            var sealedIndex = 0;
            if (typeValue != null) typeValue = sealedSlots[sealedIndex++];
            var sealedArguments = new List<LoweredExpression>(dynamicNew.Arguments.Count);
            for (var i = sealedIndex; i < sealedSlots.Count; i++)
                sealedArguments.Add(sealedSlots[i]);
            return new LoweredDynamicNewExpression(dynamicNew, typeValue,
                dynamicNew.GenericParameter, sealedArguments);
        }
    }

    // 可变参数包实参（S9d）：元素递归降级透传（恒等重写——打包归 P4b）
    internal sealed class VarArgsRewriter
        : LoweredVisitor<VarArgsRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var pack = (BoundVarArgsArgument)node;
            // 兄弟求值序保护（EvalOrderGuard）：位置元素与具名元素统一
            // 登记（既有顺序 = 位置元素先、具名元素后）
            var guard = new EvalOrderGuard(ctx);
            foreach (var value in pack.Values)
            {
                if (guard.Lower(value, env) == null) return null;
            }
            foreach (var (name, value) in pack.NamedValues)
            {
                if (guard.Lower(value, env) == null) return null;
            }
            var sealedSlots = guard.Seal();
            var values = new List<LoweredExpression>(pack.Values.Count);
            for (var i = 0; i < pack.Values.Count; i++) values.Add(sealedSlots[i]);
            var namedValues = new List<(string Name, LoweredExpression Value)>();
            for (var i = 0; i < pack.NamedValues.Count; i++)
                namedValues.Add((pack.NamedValues[i].Name, sealedSlots[pack.Values.Count + i]));
            return new LoweredVarArgsArgument(pack, pack.IsNamed, values, namedValues);
        }
    }

    // bool 短路 and/or（BIL §11.3）脱糖：
    //   a and b ⇒ 合成局部 s；前置 if a' { s = b' } else { s = false }；
    //             表达式位 s 引用
    //   a or  b ⇒ 合成局部 s；前置 if a' { s = true } else { s = b }；
    //             表达式位 s 引用
    // 条件与分支内的表达式递归降级（前置语句追加到对应块的输出列表）；
    // true/false 用 LoweredConstantExpression（合成节点，Origin 指
    // and/or 表达式本身的 Bound 节点）
    internal static class ShortCircuitRewriter
    {
        public static LoweredExpression? Lower(BoundBinaryExpression binary, LowerContext ctx,
            LowerEnvironment env)
        {
            var s = ctx.Synth.NewSynthLocal(binary.Type);
            var condition = LowerExpressionDispatcher.Visit(binary.Left, ctx, env);
            if (condition == null) return null;
            var assignRight = ExpressionFacility.LowerAssignInNewBlock(binary, binary.Right, s,
                ctx, env);
            if (assignRight == null) return null;
            var constant = new LoweredConstantExpression(binary,
                binary.Op == BilIntrinsicOp.And ? false : true, binary.Type);
            var assignConstant = new LoweredBlock(binary, new List<LoweredStatement>
            {
                new LoweredAssignmentStatement(binary, SynthLocalFactory.ReferenceTo(binary, s),
                    constant),
            });
            var (trueBlock, falseBlock) = binary.Op == BilIntrinsicOp.And
                ? (assignRight, assignConstant)
                : (assignConstant, assignRight);
            ctx.Output.Add(new LoweredIfStatement(binary, condition,
                trueBlock, falseBlock, ctx.Synth.NewBreakIdLocal()));
            return SynthLocalFactory.ReferenceTo(binary, s);
        }
    }

    // if 表达式脱糖：合成结果局部 v；前置 LoweredIfStatement（两分支
    // 值块降级产物，写 v）；表达式位 v 引用。Stage B：breakId 预建并
    // 注册两分支值块的目标映射（降级 body 前）——分支内 return@ 由
    // StructuredExitRouting 展开为「写 v + break 本 if region」
    internal sealed class IfExpressionRewriter
        : LoweredVisitor<IfExpressionRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var ifExpression = (BoundIfExpression)node;
            var result = ctx.Synth.NewSynthLocal(ifExpression.Type);
            var ifBreakId = ctx.Synth.NewBreakIdLocal();
            ctx.ExitTargets.Register(ifExpression.TrueBranch, result, ifBreakId);
            ctx.ExitTargets.Register(ifExpression.FalseBranch, result, ifBreakId);
            var condition = LowerExpressionDispatcher.Visit(ifExpression.Condition, ctx, env);
            if (condition == null) return null;
            var trueBranch = ValueBlockRewriter.Visit(ifExpression.TrueBranch,
                new ValueBlockContext(ctx, result), env);
            var falseBranch = ValueBlockRewriter.Visit(ifExpression.FalseBranch,
                new ValueBlockContext(ctx, result), env);
            if (trueBranch == null || falseBranch == null) return null;
            ctx.Output.Add(new LoweredIfStatement(ifExpression, condition,
                trueBranch, falseBranch, ifBreakId));
            return SynthLocalFactory.ReferenceTo(ifExpression, result);
        }
    }

    // switch 表达式：合成结果局部；各分支值块降级写结果局部；前置
    // switch/if 链语句，表达式位结果局部引用。Stage B：switchBreakId
    // 预建并注册全部分支值块（降级 body 前）——全值路径产物即
    // LoweredSwitch（region = switch 自身）；pattern 路径产物 if 链
    // 无总 region，外包一层 LoweredSeqBlock 承载 switchBreakId
    internal sealed class SwitchExpressionRewriter
        : LoweredVisitor<SwitchExpressionRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var switchExpression = (BoundSwitchExpression)node;
            var result = ctx.Synth.NewSynthLocal(switchExpression.Type);
            var switchBreakId = ctx.Synth.NewBreakIdLocal();
            var isPattern = switchExpression.Cases.Any(c => c.IsPattern);
            foreach (var boundCase in switchExpression.Cases)
            {
                ctx.ExitTargets.Register(boundCase.Body, result, switchBreakId);
            }
            ctx.ExitTargets.Register(switchExpression.DefaultBody, result, switchBreakId);
            var cases = new List<(BoundNode Origin, BoundExpression Match, bool IsPattern,
                LoweredBlock Body)>();
            foreach (var boundCase in switchExpression.Cases)
            {
                var body = ValueBlockRewriter.Visit(boundCase.Body,
                    new ValueBlockContext(ctx, result), env);
                if (body == null) return null;
                cases.Add((boundCase, boundCase.Match, boundCase.IsPattern, body));
            }
            var defaultBody = ValueBlockRewriter.Visit(switchExpression.DefaultBody,
                new ValueBlockContext(ctx, result), env);
            if (defaultBody == null) return null;
            var statement = SwitchFacility.LowerCore(switchExpression, switchExpression.Selector,
                cases, defaultBody, ctx, env, switchBreakId);
            if (statement == null) return null;
            // pattern 路径：if 链外包 seq region 承载 switchBreakId
            // （分支内 return@ 跨 if 层经 dispatcher relay 至此）
            ctx.Output.Add(isPattern
                ? new LoweredSeqBlock(switchExpression, (LoweredBlock)statement,
                    isVolatile: false, switchBreakId)
                : statement);
            return SynthLocalFactory.ReferenceTo(switchExpression, result);
        }
    }

    // switch pattern 占位：pattern 降级已把 selector 物化为合成局部
    // （selector 全 switch 只求值一次），占位即读该局部；目标经 Selector
    // 引用查映射栈（嵌套 switch 消歧）
    internal sealed class SwitchPlaceholderRewriter
        : LoweredVisitor<SwitchPlaceholderRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var placeholder = (BoundSwitchPlaceholderExpression)node;
            return SynthLocalFactory.ReferenceTo(placeholder,
                SwitchFacility.FindTemp(placeholder.Selector, ctx));
        }
    }

    // 复合赋值脱糖（SYNTAX §13.2 通用规则——M60 定稿单次求值）：前置
    // 「Target = Target op Value」赋值，表达式位为写回后值（纯读取目标
    // 直取 Target 重读；非纯读取目标——索引/带 getter 字段——取写回值
    // 物化的合成局部，避免表达式位二次触发 getAtIndex/getter）。
    // 含副作用的目标子表达式（实例字段 receiver / 索引 receiver+index）
    // 先物化合成局部（前置赋值，求值序先于右值），赋值左/运算左/表达式
    // 位三处共用同一物化目标——节点复用安全（Lowered 节点无父链不可变，
    // continuation 编织先例）；局部/参数/静态字段无副作用，零物化直通
    internal sealed class CompoundAssignmentRewriter
        : LoweredVisitor<CompoundAssignmentRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var compound = (BoundCompoundAssignmentExpression)node;
            // S11c/M84：wrapper place 直接字段复合赋值——读/写分离专用路径
            //（读 = Materialize + get.field、写 = set.wrapper.field 链；宿主单次求值共享）
            if (compound.Target is BoundFieldAccessExpression
                { Receiver: BoundWrapperAccessExpression } placeAccess)
            {
                return RewriteWrapperPlaceCompound(compound, placeAccess, ctx, env);
            }
            // M84：深层纯字段复合赋值 place.a.b op= rhs
            if (WrapperPlaceLowering.TryDeepFieldWriteTarget(compound.Target,
                    out var deepPlace, out var deepChain))
            {
                return WrapperPlaceLowering.LowerDeepFieldCompound(compound, deepPlace, deepChain,
                    ctx, env);
            }
            // S1/g9：普通值类型中间链复合赋值 host.a.b... op= rhs（§13.2）
            //（根含局部/参数/this/静态·全局字段）
            if (WrapperPlaceLowering.TryValueChainWriteTarget(compound.Target,
                    out var valueRoot, out var valueChain))
            {
                return WrapperPlaceLowering.LowerValueChainFieldCompound(compound, valueRoot,
                    valueChain, ctx, env);
            }
            if (WrapperPlaceLowering.ContainsPlaceInTarget(compound.Target))
            {
                WrapperPlaceLowering.UnsupportedWrite(compound.Target, env);
                return null;
            }
            // 闭包存储计划（SYNTAX §5.2）：被捕获变量的复合赋值——
            // 读 = cell getValue、写 = cell setValue；写回值物化 .sN 共享
            //（§13.2 单次求值，与 wrapper place 复合赋值同构）
            if (compound.Target is BoundValueReferenceExpression compoundReference
                && ctx.Closure.TryRewriteValueRead(compoundReference) is { } cellRead)
            {
                var cellCompoundValue = LowerExpressionDispatcher.Visit(compound.Value, ctx, env);
                if (cellCompoundValue == null) return null;
                cellCompoundValue = LoweringFacility.EnsureDeclaredType(compound,
                    cellCompoundValue, compound.Target.Type);
                LoweredExpression cellBinary = new LoweredBinaryExpression(compound,
                    compound.Op, cellRead, cellCompoundValue);
                cellBinary = LoweringFacility.EnsureDeclaredType(compound, cellBinary,
                    compound.Target.Type);
                var cellResult = ctx.Synth.NewSynthLocal(compound.Target.Type);
                ctx.Output.Add(new LoweredAssignmentStatement(compound,
                    SynthLocalFactory.ReferenceTo(compound, cellResult), cellBinary));
                var cellWrite = ctx.Closure.TryRewriteCellWrite(compound, compound.Target,
                    SynthLocalFactory.ReferenceTo(compound, cellResult));
                if (cellWrite == null) return null;
                ctx.Output.Add(cellWrite);
                return SynthLocalFactory.ReferenceTo(compound, cellResult);
            }
            // 静态/全局 cell 化字段的复合赋值（统一 cell 存储，SYNTAX §14.3）
            // ——读 = cell getValue、写 = cell setValue，与局部 cell 同构
            if (compound.Target is BoundFieldReferenceExpression staticReference
                && CellStorageLowering.TryRewriteStaticRead(staticReference, env)
                    is { } staticCellRead)
            {
                var staticCompoundValue = LowerExpressionDispatcher.Visit(compound.Value, ctx, env);
                if (staticCompoundValue == null) return null;
                staticCompoundValue = LoweringFacility.EnsureDeclaredType(compound,
                    staticCompoundValue, compound.Target.Type);
                LoweredExpression staticBinary = new LoweredBinaryExpression(compound,
                    compound.Op, staticCellRead, staticCompoundValue);
                staticBinary = LoweringFacility.EnsureDeclaredType(compound, staticBinary,
                    compound.Target.Type);
                var staticResult = ctx.Synth.NewSynthLocal(compound.Target.Type);
                ctx.Output.Add(new LoweredAssignmentStatement(compound,
                    SynthLocalFactory.ReferenceTo(compound, staticResult), staticBinary));
                var staticWrite = CellStorageLowering.TryRewriteStaticWrite(compound,
                    compound.Target, SynthLocalFactory.ReferenceTo(compound, staticResult), env);
                if (staticWrite == null) return null;
                ctx.Output.Add(staticWrite);
                return SynthLocalFactory.ReferenceTo(compound, staticResult);
            }
            var target = LowerExpressionDispatcher.Visit(compound.Target, ctx, env);
            if (target == null) return null;
            target = MaterializeTarget(target, ctx);
            var value = LowerExpressionDispatcher.Visit(compound.Value, ctx, env);
            if (value == null) return null;
            // #28④：RHS cast 物化到 place 运算类型（降级 Any→T；§11 同型）
            value = LoweringFacility.EnsureDeclaredType(compound, value, compound.Target.Type);
            LoweredExpression binary = new LoweredBinaryExpression(compound, compound.Op,
                target, value);
            // 写回值按 place 声明类型物化 cast（BIL §6.5，与普通赋值
            // 同规则）：SmartCast 包装时声明类型 = Operand 类型（收窄类型
            // 只参与运算定型，变量声明类型不变——s: String? 收窄区域内
            // s += "b" 的运算结果 String 写回须物化装箱 cast）；
            // variadic 参数索引（BIL §7.1）place 剥壳后声明类型 = 容器
            // ABI 元素类型（运算按拆箱后 P3 元素类型进行，写回装箱）
            var declaredTargetType = LoweringFacility.VariadicIndexAbiTypeOfPlace(target, env)
                ?? (compound.Target is BoundSmartCastExpression smartCast
                    ? smartCast.Operand.Type
                    : compound.Target.Type);
            binary = LoweringFacility.EnsureDeclaredType(compound, binary, declaredTargetType);
            // §13.2 单次求值：非纯读取目标（索引/带 getter 字段）在表达式位
            // 重读会再次触发 getAtIndex/getter 调用——写回值先物化合成局部，
            // 写回与表达式位共用该局部（与 wrapper place/cell 路径同构）；
            // 纯读取目标（局部/参数/静态字段/backing 字段）重读无副作用且
            // 值恒等于写回值，保持直通零物化
            if (!IsSideEffectFree(target))
            {
                var result = ctx.Synth.NewSynthLocal(declaredTargetType);
                ctx.Output.Add(new LoweredAssignmentStatement(compound,
                    SynthLocalFactory.ReferenceTo(compound, result), binary));
                ctx.Output.Add(new LoweredAssignmentStatement(compound, target,
                    SynthLocalFactory.ReferenceTo(compound, result)));
                // 写回类型与表达式类型不同（variadic 索引装箱/SmartCast）时
                // 表达式位补回 P3 类型 cast——读的是合成局部，纯读取
                return LoweringFacility.EnsureDeclaredType(compound,
                    SynthLocalFactory.ReferenceTo(compound, result), compound.Target.Type);
            }
            ctx.Output.Add(new LoweredAssignmentStatement(compound, target, binary));
            return target;
        }

        // wrapper place 直接字段复合赋值（S11c）：place.field op= value。
        // 读路径（Materialize + get.field）与写路径（set.wrapper.field 链）
        // 分离——读路径产物是值拷贝，写后重读不到新值，故表达式位直取
        // 写回值的结果局部（与一般路径的「重读 place」等价——post-write
        // 值）。终极宿主物化合成局部共享（§13.2 单次求值：宿主只降级一次）
        private static LoweredExpression? RewriteWrapperPlaceCompound(
            BoundCompoundAssignmentExpression compound, BoundFieldAccessExpression placeAccess,
            LowerContext ctx, LowerEnvironment env)
        {
            var place = (BoundWrapperAccessExpression)placeAccess.Receiver;
            var host = WrapperPlaceLowering.LowerUltimateHostForWrite(place, ctx, env);
            if (host == null) return null;
            var read = WrapperPlaceLowering.LowerFieldRead(placeAccess, place, host, ctx, env);
            if (read == null) return null;
            var write = WrapperPlaceLowering.BuildWrapperFieldPlace(placeAccess, place,
                placeAccess.Field, host, ctx, env);
            if (write == null) return null;
            var value = LowerExpressionDispatcher.Visit(compound.Value, ctx, env);
            if (value == null) return null;
            // #28④：RHS cast 物化到 place 运算类型（与一般复合赋值同规则）
            value = LoweringFacility.EnsureDeclaredType(compound, value, placeAccess.Type);
            LoweredExpression binary = new LoweredBinaryExpression(compound, compound.Op,
                read, value);
            binary = LoweringFacility.EnsureDeclaredType(compound, binary, placeAccess.Type);
            var result = ctx.Synth.NewSynthLocal(placeAccess.Type);
            ctx.Output.Add(new LoweredAssignmentStatement(compound,
                SynthLocalFactory.ReferenceTo(compound, result), binary));
            ctx.Output.Add(new LoweredAssignmentStatement(compound, write,
                SynthLocalFactory.ReferenceTo(compound, result)));
            return SynthLocalFactory.ReferenceTo(compound, result);
        }

        // 目标单次求值物化（M60）：实例字段 receiver 与索引 receiver/index
        // 降级产物换为合成局部引用（前置「.sN = expr」赋值语句）；
        // 纯读取形态（局部/参数/静态字段/字面量/常量/this 及其链式组合）
        // 无副作用，直通不物化
        private static LoweredExpression MaterializeTarget(LoweredExpression target,
            LowerContext ctx)
        {
            if (IsSideEffectFree(target)) return target;
            switch (target)
            {
                // variadic 参数索引（BIL §7.1）：读形态产物为拆箱 cast
                // 包索引 place——剥壳物化内层（索引恒物化语义不变）后
                // 包回壳；壳的目标/类型不变（拆箱回 P3 静态元素类型）
                case LoweredCastExpression castShell
                    when castShell.Source is LoweredIndexExpression innerIndex
                        && LoweringFacility.IsVariadicParameterIndex(innerIndex):
                    return new LoweredCastExpression(castShell.Origin,
                        MaterializeTarget(innerIndex, ctx), castShell.TargetType,
                        castShell.IsSafe, castShell.Type);
                case LoweredFieldAccessExpression fieldAccess:
                    return new LoweredFieldAccessExpression(fieldAccess.Origin,
                        MaterializeInto(fieldAccess.Origin, fieldAccess.Receiver, ctx),
                        fieldAccess.Field);
                case LoweredIndexExpression indexAccess:
                    // Type 覆盖透传（variadic 场景的 ABI 元素类型——
                    // 经上方剥壳分支到达；常规场景覆盖为 null 等价透传）
                    return new LoweredIndexExpression((BoundIndexExpression)indexAccess.Origin,
                        MaterializeInto(indexAccess.Origin, indexAccess.Receiver, ctx),
                        MaterializeInto(indexAccess.Origin, indexAccess.Index, ctx),
                        indexAccess.Type);
                default:
                    return target;
            }
        }

        // 物化一条「.sN = expr」前置赋值（求值一次），返回合成局部引用；
        // 纯读取子表达式直通（不产多余局部）
        private static LoweredExpression MaterializeInto(BoundNode origin,
            LoweredExpression expr, LowerContext ctx)
        {
            if (IsSideEffectFree(expr)) return expr;
            var local = ctx.Synth.NewSynthLocal(expr.Type);
            ctx.Output.Add(new LoweredAssignmentStatement(origin,
                SynthLocalFactory.ReferenceTo(origin, local), expr));
            return SynthLocalFactory.ReferenceTo(origin, local);
        }

        // 纯读取判定（无副作用，重复求值安全）：局部/参数/静态字段/字面量/
        // 常量/this，及全由它们构成的字段链。
        // 索引读取恒非纯读取（§13.2 单次求值）：索引访问本身是 getAtIndex
        // 调用（S8c），即使 receiver/index 无副作用也必须物化一次
        // （a[i].c += 1 / a[i][j] += 1 的内层索引不许重复求值）；
        // 字段读取同理按 Field.Getter 判定——无 getter（backing 存储直读）
        // 才纯读取，computed/用户 getter 的读取是 getter 调用（§9.4.1），
        // 重复求值即重复调用
        private static bool IsSideEffectFree(LoweredExpression expr) => expr switch
        {
            LoweredValueReferenceExpression or LoweredFieldReferenceExpression
                or LoweredLiteralExpression or LoweredConstantExpression
                or LoweredThisExpression => true,
            LoweredFieldAccessExpression fieldAccess => fieldAccess.Field.Getter == null
                && IsSideEffectFree(fieldAccess.Receiver),
            _ => false,
        };
    }

    internal sealed class ThisRewriter : LoweredVisitor<ThisRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            // lambda 体内 this 命中捕获条目时改写为 .capture.this 字段访问
            // （SYNTAX §5.2）；否则为普通 $.this
            var thisExpression = (BoundThisExpression)node;
            return ctx.Closure.TryRewriteThis(thisExpression)
                ?? new LoweredThisExpression(thisExpression);
        }
    }

    // proxy 体 self → get.self（M88，BIL §12.5）
    internal sealed class SelfRewriter : LoweredVisitor<SelfRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            return new LoweredGetSelfExpression((BoundSelfExpression)node);
        }
    }

    // proxy 体 inner(...) → invoke fn(..inner)（M88，BIL §15.4）；实参逐一下降
    internal sealed class InnerCallRewriter
        : LoweredVisitor<InnerCallRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var inner = (BoundInnerCallExpression)node;
            // 兄弟求值序保护（EvalOrderGuard）：实参逐一登记后统一 Seal
            var guard = new EvalOrderGuard(ctx);
            foreach (var argument in inner.Arguments)
            {
                if (guard.Lower(argument, env) == null) return null;
            }
            var sealedSlots = guard.Seal();
            var arguments = new List<LoweredExpression>(inner.Arguments.Count);
            for (var i = 0; i < sealedSlots.Count; i++) arguments.Add(sealedSlots[i]);
            return new LoweredCallInnerExpression(inner, arguments);
        }
    }

    internal sealed class AwaitRewriter
        : LoweredVisitor<AwaitRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var awaitExpression = (BoundAwaitExpression)node;
            var operand = LowerExpressionDispatcher.Visit(awaitExpression.Operand, ctx, env);
            return operand == null ? null : new LoweredAwaitExpression(awaitExpression, operand);
        }
    }

    internal sealed class SuperCallRewriter
        : LoweredVisitor<SuperCallRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var super = (BoundSuperCallExpression)node;
            var arguments = LoweringFacility.LowerArguments(super.Arguments,
                super.Method.Parameters, ctx, env);
            if (arguments == null) return null;
            var genericPack = super.GenericPack == null ? null
                : new LoweredGenericVarArgsArgument(super.GenericPack, super.GenericPack.IsNamed,
                    super.GenericPack.TypeArguments, super.GenericPack.NamedTypes);
            return new LoweredSuperCallExpression(super, arguments, genericPack);
        }
    }

    // 实例调用：receiver 降级 + 调用点 cast 物化（BIL §6.5）——
    // receiver 静态类型 ≠ 方法宿主时包显式 cast（沿 BaseType 链找到的
    // 成员在子类 receiver 上调用时的装箱/基类视图转换，§12.1）。
    // S11c/M84：wrapper place 作 receiver——Entity = get.wrapper、字段-Value
    // = get.wrapper.field 值拷贝物化（§12.4）；物化产物类型即 wrapper
    // 类型（方法宿主本身），不再做宿主 cast
    internal sealed class InstanceCallRewriter
        : LoweredVisitor<InstanceCallRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var instanceCall = (BoundInstanceCallExpression)node;
            // S1/g9：值类型 receiver 的可写 place 链（§10）——receiver
            // 拷贝物化，调用结果物化后把 this 修改反向写回 place
            if (instanceCall.Receiver is not BoundWrapperAccessExpression
                && WrapperPlaceLowering.TryValueReceiverCallTarget(instanceCall.Receiver,
                    instanceCall.Method.Owner, out var valueRoot, out var valueChain))
            {
                return RewriteValueReceiverCall(instanceCall, valueRoot, valueChain, ctx, env);
            }
            // 兄弟求值序保护（EvalOrderGuard）：receiver 先于实参登记——
            // 实参产前置语句（如短路 and/or）时 receiver 物化合成局部，
            // 保证 receiver 先求值（BIL §10.2 从左到右）
            var guard = new EvalOrderGuard(ctx);
            var isWrapperPlace = instanceCall.Receiver is BoundWrapperAccessExpression;
            var receiver = isWrapperPlace
                ? WrapperPlaceLowering.Materialize(
                    (BoundWrapperAccessExpression)instanceCall.Receiver, null, ctx, env)
                : LowerExpressionDispatcher.Visit(instanceCall.Receiver, ctx, env);
            if (receiver == null) return null;
            if (!isWrapperPlace)
            {
                receiver = LoweringFacility.EnsureReceiverType(instanceCall, receiver,
                    instanceCall.Method.Owner);
            }
            guard.Track(instanceCall.Receiver, receiver);
            var arguments = LoweringFacility.LowerArguments(instanceCall.Arguments,
                instanceCall.Method.Parameters, ctx, env, guard,
                instanceCall.Receiver.Type as TypeSymbol, instanceCall.Method, instanceCall.TypeArguments);
            if (arguments == null) return null;
            var genericPack = instanceCall.GenericPack == null ? null
                : new LoweredGenericVarArgsArgument(instanceCall.GenericPack,
                    instanceCall.GenericPack.IsNamed, instanceCall.GenericPack.TypeArguments,
                    instanceCall.GenericPack.NamedTypes);
            var sealedSlots = guard.Seal();
            var sealedArguments = new List<LoweredExpression>(instanceCall.Arguments.Count);
            for (var i = 1; i < sealedSlots.Count; i++) sealedArguments.Add(sealedSlots[i]);
            return new LoweredInstanceCallExpression(instanceCall, sealedSlots[0],
                instanceCall.Method, sealedArguments, instanceCall.Type, instanceCall.TypeArguments,
                genericPack);
        }

        // 值类型 receiver 可写 place 链调用（S1/g9，§10）：正向 get 链
        // 物化 receiver 拷贝（根单次求值共享）→ 调用 → 结果物化合成
        // 局部 → this 修改经值类型中间逐层反向 set 写回 place；表达式
        // 位读结果局部。调用与写回都落在前置流，求值序 = receiver 链 →
        // 实参 → 调用 → 写回（guard 只管实参段，正向 get 先于 guard 定位）
        private static LoweredExpression? RewriteValueReceiverCall(
            BoundInstanceCallExpression instanceCall, BoundExpression valueRoot,
            List<BoundFieldAccessExpression> valueChain, LowerContext ctx,
            LowerEnvironment env)
        {
            var receiver = WrapperPlaceLowering.MaterializeValueReceiver(instanceCall,
                valueRoot, valueChain, ctx, env, out var intermediates, out var host);
            if (receiver == null) return null;
            var guard = new EvalOrderGuard(ctx);
            guard.Track(instanceCall.Receiver, receiver);
            var arguments = LoweringFacility.LowerArguments(instanceCall.Arguments,
                instanceCall.Method.Parameters, ctx, env, guard,
                instanceCall.Receiver.Type as TypeSymbol, instanceCall.Method, instanceCall.TypeArguments);
            if (arguments == null) return null;
            var genericPack = instanceCall.GenericPack == null ? null
                : new LoweredGenericVarArgsArgument(instanceCall.GenericPack,
                    instanceCall.GenericPack.IsNamed, instanceCall.GenericPack.TypeArguments,
                    instanceCall.GenericPack.NamedTypes);
            var sealedSlots = guard.Seal();
            var sealedArguments = new List<LoweredExpression>(instanceCall.Arguments.Count);
            for (var i = 1; i < sealedSlots.Count; i++) sealedArguments.Add(sealedSlots[i]);
            var callExpression = new LoweredInstanceCallExpression(instanceCall, sealedSlots[0],
                instanceCall.Method, sealedArguments, instanceCall.Type,
                instanceCall.TypeArguments, genericPack);
            var result = ctx.Synth.NewSynthLocal(instanceCall.Type);
            ctx.Output.Add(new LoweredAssignmentStatement(instanceCall,
                SynthLocalFactory.ReferenceTo(instanceCall, result), callExpression));
            var writebacks = WrapperPlaceLowering.BuildValueReceiverWritebacks(instanceCall,
                intermediates, host!, valueRoot, ctx, env);
            if (writebacks == null) return null;
            foreach (var writeback in writebacks)
            {
                ctx.Output.Add(writeback);
            }
            return SynthLocalFactory.ReferenceTo(instanceCall, result);
        }
    }

    internal sealed class FieldAccessRewriter
        : LoweredVisitor<FieldAccessRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var fieldAccess = (BoundFieldAccessExpression)node;
            // S11c：wrapper place 作 receiver——Materialize 值拷贝 + 普通
            // get.field（Entity = get.wrapper；字段-Value = get.wrapper.field）
            if (fieldAccess.Receiver is BoundWrapperAccessExpression place)
            {
                return WrapperPlaceLowering.LowerFieldRead(fieldAccess, place, null, ctx, env);
            }
            var receiver = LowerExpressionDispatcher.Visit(fieldAccess.Receiver, ctx, env);
            if (receiver == null) return null;
            return new LoweredFieldAccessExpression(fieldAccess, receiver, fieldAccess.Field);
        }
    }

    // 索引访问降级（S8c，BIL §13.6 直接对应，无脱糖）：receiver/index
    // 递归降级；读/写共用节点，指令选择归 P4b 按所在位置。
    // variadic 参数索引（BIL §7.1 ABI ↔ P3 体内视角桥接）：节点 Type
    // 改为 .nullable<容器 ABI 元素类型>（与 .vargs./.kwargs. 声明对齐——
    // Q6 后 get.array 内建形态结果恒为可空），并外包拆箱 cast 回 P3 静态
    // 类型 Nullable\<元素\>——读位置下游按 P3 类型消费零适配；
    // 写位置由 AssignmentRewriter/CompoundAssignmentRewriter 剥壳后按
    // ABI 元素类型装箱（§6.5）
    internal sealed class IndexRewriter
        : LoweredVisitor<IndexRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var indexAccess = (BoundIndexExpression)node;
            // 兄弟求值序保护（EvalOrderGuard）：receiver 先、index 后——
            // index 产前置语句时 receiver 物化合成局部
            var guard = new EvalOrderGuard(ctx);
            // S11c/M84：wrapper place 作 receiver——Entity = get.wrapper、
            // 字段-Value = get.wrapper.field 值拷贝物化（索引写归 Assignment/
            // Compound 专用路径，读路径仍走本 rewriter）
            LoweredExpression? receiver;
            if (indexAccess.Receiver is BoundWrapperAccessExpression place)
            {
                receiver = WrapperPlaceLowering.Materialize(place, null, ctx, env);
                if (receiver != null) guard.Track(indexAccess.Receiver, receiver);
            }
            else
            {
                receiver = guard.Lower(indexAccess.Receiver, env);
            }
            if (receiver == null) return null;
            var index = guard.Lower(indexAccess.Index, env);
            if (index == null) return null;
            var sealedSlots = guard.Seal();
            receiver = sealedSlots[0];
            index = sealedSlots[1];
            var result = new LoweredIndexExpression(indexAccess, receiver, index);
            if (!LoweringFacility.IsVariadicParameterIndex(result)) return result;
            // 写形态（place，Operator = setAtIndex）：Type = ABI 元素类型
            //（set.array 元素对齐，装箱 cast 目标）；读形态（Q6）：Type =
            // .nullable<ABI 元素>（get.array 内建形态结果恒为可空），外包
            // 位置包拆箱回 P3 静态类型；具名包逐元素转换并重建 Pair。
            var abiElementType = LoweringFacility.VariadicIndexElementType(result, env);
            if (indexAccess.Operator.Name == "setAtIndex")
            {
                return new LoweredIndexExpression(indexAccess, receiver, index,
                    abiElementType);
            }
            var abiReadType = env.Unit.Symbols.GetNullable(abiElementType);
            result = new LoweredIndexExpression(indexAccess, receiver, index, abiReadType);
            if (receiver is LoweredValueReferenceExpression { Symbol: ParameterSymbol { IsNamedVariadic: true } })
                return LoweringFacility.AdaptNamedArgumentPair(indexAccess, result, indexAccess.Type, env);
            return new LoweredCastExpression(indexAccess, result,
                indexAccess.Type, isSafe: false, indexAccess.Type);
        }
    }

    // cast 恒等降级（BIL §12.1/§12.2 直接对应；as? 的 Nullable 包装
    // 已在 P3 定型进 Type）
    internal sealed class CastRewriter : LoweredVisitor<CastRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var cast = (BoundCastExpression)node;
            var source = LowerExpressionDispatcher.Visit(cast.Source, ctx, env);
            if (source == null) return null;
            return new LoweredCastExpression(cast, source, cast.TargetType, cast.IsSafe, cast.Type);
        }
    }

    // smart cast 物化（S8b，ARCH §6.1「smart cast 标记 → 显式 cast」）：
    // 操作数递归降级后包显式 cast（§12.1——T? → T 的 unwrap 与子类型收窄
    // 同形态；收窄事实已保证运行期检查必过）
    internal sealed class SmartCastRewriter
        : LoweredVisitor<SmartCastRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var smartCast = (BoundSmartCastExpression)node;
            var operand = LowerExpressionDispatcher.Visit(smartCast.Operand, ctx, env);
            if (operand == null) return null;
            return new LoweredCastExpression(smartCast, operand, smartCast.NarrowedType,
                isSafe: false, smartCast.NarrowedType);
        }
    }

    // is/supers/with 恒等降级（S8a，BIL §12.3 直接对应，无脱糖）；
    // 动态形态的 TargetValue 递归降级；S11：IsCase 形态的 Case 槽透传
    internal sealed class TypeCheckRewriter
        : LoweredVisitor<TypeCheckRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var typeCheck = (BoundTypeCheckExpression)node;
            // 兄弟求值序保护（EvalOrderGuard）：operand 先、TargetValue 后
            var guard = new EvalOrderGuard(ctx);
            var operand = guard.Lower(typeCheck.Operand, env);
            if (operand == null) return null;
            LoweredExpression? targetValue = null;
            if (typeCheck.TargetValue != null)
            {
                targetValue = guard.Lower(typeCheck.TargetValue, env);
                if (targetValue == null) return null;
            }
            var sealedSlots = guard.Seal();
            operand = sealedSlots[0];
            if (typeCheck.TargetValue != null) targetValue = sealedSlots[1];
            return new LoweredTypeCheckExpression(typeCheck, typeCheck.Kind,
                operand, typeCheck.TargetType, targetValue, typeCheck.Type, typeCheck.Case);
        }
    }

    // typeOf 恒等降级（S8a，BIL §12.5 直接对应，无脱糖）；
    // 值形态的 Operand 递归降级
    internal sealed class TypeOfRewriter
        : LoweredVisitor<TypeOfRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var typeOf = (BoundTypeOfExpression)node;
            LoweredExpression? operand = null;
            if (typeOf.Operand != null)
            {
                operand = LowerExpressionDispatcher.Visit(typeOf.Operand, ctx, env);
                if (operand == null) return null;
            }
            return new LoweredTypeOfExpression(typeOf, operand, typeOf.TargetType, typeOf.Type);
        }
    }

    // enum case 构造降级（S11，BIL §14.3 new.case 直接对应）：实参按
    // init 参数序组合「声明点固定实参 + 调用点洞实参」（RUNTIME §16：
    // case 入口按模板固定参数与参数洞组成实参调用已解析 init），逐条
    // 递归降级 + 按 init 形参类型物化 cast（§14.3 严格匹配；与
    // LowerArguments 按形参类型物化同先例）。模板信息缺失（HoleParameters
    // null——绑定失败 P3 已诊断静默；或无显式 init 的零参 case）时
    // 退化为仅洞实参直通
    internal sealed class EnumCaseRewriter
        : LoweredVisitor<EnumCaseRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var enumCase = (BoundEnumCaseExpression)node;
            if (enumCase.ArgumentsAreInitArguments)
                return LowerHoleArgumentsOnly(enumCase, null, ctx, env);
            var holes = enumCase.Case.HoleParameters;
            var fixedArgs = enumCase.FixedArguments;
            var init = enumCase.Case.ResolvedInit;
            if (fixedArgs == null || init == null || holes == null
                || fixedArgs.Count != init.Parameters.Count)
            {
                return LowerHoleArgumentsOnly(enumCase, holes, ctx, env);
            }
            // 组合实参（init 参数序）：固定位置取声明点模板实参，洞位置
            // 经 InitParameterIndex 回指调用点洞实参（洞签名序）
            var guard = new EvalOrderGuard(ctx);
            for (var i = 0; i < init.Parameters.Count; i++)
            {
                BoundExpression source;
                if (fixedArgs[i] is { } fixedArg)
                {
                    source = fixedArg;
                }
                else
                {
                    var holeIndex = -1;
                    for (var h = 0; h < holes.Count; h++)
                    {
                        if (holes[h].InitParameterIndex == i) { holeIndex = h; break; }
                    }
                    if (holeIndex < 0)
                    {
                        // 结构不齐（模板绑定保证洞全覆盖——防御，不该到达）
                        env.Error(node.Syntax.Span,
                            $"P4: enum case '{enumCase.Case.Name}' template hole mapping " +
                            "is incomplete");
                        return null;
                    }
                    source = enumCase.Arguments[holeIndex];
                }
                var lowered = LowerExpressionDispatcher.Visit(source, ctx, env);
                if (lowered == null) return null;
                guard.Track(source, LoweringFacility.EnsureDeclaredType(source, lowered,
                    init.Parameters[i].Type));
            }
            var sealedSlots = guard.Seal();
            var arguments = new List<LoweredExpression>(sealedSlots.Count);
            for (var i = 0; i < sealedSlots.Count; i++) arguments.Add(sealedSlots[i]);
            return new LoweredEnumCaseExpression(enumCase, enumCase.Case, arguments);
        }

        // 旧形态：仅调用点洞实参（规范序 = 洞签名序；固定 case 为空）。
        // 兄弟求值序保护（EvalOrderGuard）：洞实参逐条登记（Track 的
        // 必须是 EnsureDeclaredType 包装后的最终形态）后统一 Seal
        private static LoweredExpression? LowerHoleArgumentsOnly(
            BoundEnumCaseExpression enumCase, IReadOnlyList<EnumCaseHoleParameter>? holes,
            LowerContext ctx, LowerEnvironment env)
        {
            var guard = new EvalOrderGuard(ctx);
            for (var i = 0; i < enumCase.Arguments.Count; i++)
            {
                var lowered = LowerExpressionDispatcher.Visit(enumCase.Arguments[i], ctx, env);
                if (lowered == null) return null;
                guard.Track(enumCase.Arguments[i], LoweringFacility.EnsureDeclaredType(
                    enumCase.Arguments[i], lowered, holes?[i].Type));
            }
            var sealedSlots = guard.Seal();
            var arguments = new List<LoweredExpression>(enumCase.Arguments.Count);
            for (var i = 0; i < sealedSlots.Count; i++) arguments.Add(sealedSlots[i]);
            return new LoweredEnumCaseExpression(enumCase, enumCase.Case, arguments);
        }
    }
}
