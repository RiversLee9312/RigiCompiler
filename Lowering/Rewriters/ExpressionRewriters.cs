namespace LatteCompiler
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

    internal sealed class FieldReferenceRewriter
        : LoweredVisitor<FieldReferenceRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var reference = (BoundFieldReferenceExpression)node;
            return new LoweredFieldReferenceExpression(reference, reference.Field);
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
            if (binary.Op is BilIntrinsicOp.And or BilIntrinsicOp.Or)
            {
                return ShortCircuitRewriter.Lower(binary, ctx, env);
            }
            var left = LowerExpressionDispatcher.Visit(binary.Left, ctx, env);
            var right = LowerExpressionDispatcher.Visit(binary.Right, ctx, env);
            if (left == null || right == null) return null;
            return new LoweredBinaryExpression(binary, binary.Op, left, right);
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
            var arguments = LoweringFacility.LowerArguments(call.Arguments, call.Method.Parameters,
                ctx, env);
            if (arguments == null) return null;
            // S9d-2：泛型包无值子节点，恒等透传（打包归 P4b）
            var genericPack = call.GenericPack == null ? null
                : new LoweredGenericVarArgsArgument(call.GenericPack, call.GenericPack.IsNamed,
                    call.GenericPack.TypeArguments, call.GenericPack.NamedTypes);
            // §15.3 间接调用：目标对象表达式降级透传（§10.2 求值序——
            // 目标在实参之后物化与直接调用 receiver 序一致归发射侧）
            LoweredExpression? indirectTarget = null;
            if (call.IsIndirect)
            {
                indirectTarget = LowerExpressionDispatcher.Visit(call.IndirectTarget!, ctx, env);
                if (indirectTarget == null) return null;
            }
            return new LoweredCallExpression(call, call.Method, arguments, call.TypeArguments,
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
            return new LoweredNewExpression(lambda, lambda.Closure.Init, arguments,
                lambda.Type);
        }
    }

    internal sealed class NewExpressionRewriter
        : LoweredVisitor<NewExpressionRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var newExpression = (BoundNewExpression)node;
            var arguments = LoweringFacility.LowerArguments(newExpression.Arguments,
                newExpression.Init?.Parameters, ctx, env);
            if (arguments == null) return null;
            return new LoweredNewExpression(newExpression, newExpression.Init, arguments);
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
            var values = new List<LoweredExpression>();
            foreach (var value in pack.Values)
            {
                var lowered = LowerExpressionDispatcher.Visit(value, ctx, env);
                if (lowered == null) return null;
                values.Add(lowered);
            }
            var namedValues = new List<(string Name, LoweredExpression Value)>();
            foreach (var (name, value) in pack.NamedValues)
            {
                var lowered = LowerExpressionDispatcher.Visit(value, ctx, env);
                if (lowered == null) return null;
                namedValues.Add((name, lowered));
            }
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
                trueBlock, falseBlock));
            return SynthLocalFactory.ReferenceTo(binary, s);
        }
    }

    // if 表达式脱糖：合成结果局部 v；前置 LoweredIfStatement（两分支
    // 值块降级产物，写 v）；表达式位 v 引用
    internal sealed class IfExpressionRewriter
        : LoweredVisitor<IfExpressionRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var ifExpression = (BoundIfExpression)node;
            var result = ctx.Synth.NewSynthLocal(ifExpression.Type);
            var condition = LowerExpressionDispatcher.Visit(ifExpression.Condition, ctx, env);
            if (condition == null) return null;
            var trueBranch = ValueBlockRewriter.Visit(ifExpression.TrueBranch,
                new ValueBlockContext(ctx, result), env);
            var falseBranch = ValueBlockRewriter.Visit(ifExpression.FalseBranch,
                new ValueBlockContext(ctx, result), env);
            if (trueBranch == null || falseBranch == null) return null;
            ctx.Output.Add(new LoweredIfStatement(ifExpression, condition,
                trueBranch, falseBranch));
            return SynthLocalFactory.ReferenceTo(ifExpression, result);
        }
    }

    // switch 表达式：合成结果局部；各分支值块降级写结果局部（复用
    // 值块映射栈与 if 转换）；前置 switch/if 链语句，表达式位结果局部引用
    internal sealed class SwitchExpressionRewriter
        : LoweredVisitor<SwitchExpressionRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var switchExpression = (BoundSwitchExpression)node;
            var result = ctx.Synth.NewSynthLocal(switchExpression.Type);
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
                cases, defaultBody, ctx, env);
            if (statement == null) return null;
            ctx.Output.Add(statement);
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
    // 「Target = Target op Value」赋值，表达式位 Target 引用（写回后值）。
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
            var hostBound = WrapperPlaceLowering.UltimateHostExpression(place);
            var host = LowerExpressionDispatcher.Visit(hostBound, ctx, env);
            if (host == null) return null;
            host = WrapperPlaceLowering.MaterializeSharedWriteHost(hostBound, host, ctx);
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
            var arguments = new List<LoweredExpression>(inner.Arguments.Count);
            foreach (var argument in inner.Arguments)
            {
                var lowered = LowerExpressionDispatcher.Visit(argument, ctx, env);
                if (lowered == null) return null;
                arguments.Add(lowered);
            }
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
            var isWrapperPlace = instanceCall.Receiver is BoundWrapperAccessExpression;
            var receiver = isWrapperPlace
                ? WrapperPlaceLowering.Materialize(
                    (BoundWrapperAccessExpression)instanceCall.Receiver, null, ctx, env)
                : LowerExpressionDispatcher.Visit(instanceCall.Receiver, ctx, env);
            if (receiver == null) return null;
            if (!isWrapperPlace)
            {
                receiver = LoweringFacility.EnsureDeclaredType(instanceCall, receiver,
                    instanceCall.Method.Owner);
            }
            var arguments = LoweringFacility.LowerArguments(instanceCall.Arguments,
                instanceCall.Method.Parameters, ctx, env);
            if (arguments == null) return null;
            var genericPack = instanceCall.GenericPack == null ? null
                : new LoweredGenericVarArgsArgument(instanceCall.GenericPack,
                    instanceCall.GenericPack.IsNamed, instanceCall.GenericPack.TypeArguments,
                    instanceCall.GenericPack.NamedTypes);
            return new LoweredInstanceCallExpression(instanceCall, receiver,
                instanceCall.Method, arguments, instanceCall.Type, instanceCall.TypeArguments,
                genericPack);
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
    // 改为容器 ABI 元素类型（与 .vargs./.kwargs. 声明对齐），并外包
    // 拆箱 cast 回 P3 静态元素类型——读位置下游按 P3 类型消费零适配；
    // 写位置由 AssignmentRewriter/CompoundAssignmentRewriter 剥壳后按
    // ABI 元素类型装箱（§6.5）
    internal sealed class IndexRewriter
        : LoweredVisitor<IndexRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var indexAccess = (BoundIndexExpression)node;
            // S11c/M84：wrapper place 作 receiver——Entity = get.wrapper、
            // 字段-Value = get.wrapper.field 值拷贝物化（索引写仍归口）
            var receiver = indexAccess.Receiver is BoundWrapperAccessExpression place
                ? WrapperPlaceLowering.Materialize(place, null, ctx, env)
                : LowerExpressionDispatcher.Visit(indexAccess.Receiver, ctx, env);
            if (receiver == null) return null;
            var index = LowerExpressionDispatcher.Visit(indexAccess.Index, ctx, env);
            if (index == null) return null;
            var result = new LoweredIndexExpression(indexAccess, receiver, index);
            if (!LoweringFacility.IsVariadicParameterIndex(result)) return result;
            var abiElementType = LoweringFacility.VariadicIndexElementType(result, env);
            result = new LoweredIndexExpression(indexAccess, receiver, index, abiElementType);
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
            var operand = LowerExpressionDispatcher.Visit(typeCheck.Operand, ctx, env);
            if (operand == null) return null;
            LoweredExpression? targetValue = null;
            if (typeCheck.TargetValue != null)
            {
                targetValue = LowerExpressionDispatcher.Visit(typeCheck.TargetValue, ctx, env);
                if (targetValue == null) return null;
            }
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

    // enum case 构造恒等降级（S11，BIL §14.3 new.case 直接对应）：洞实参
    // 逐条递归降级 + 按洞签名类型物化 cast（§14.3 ARG 类型严格匹配 case
    // 入口——P3 已 IsAssignable 兼容，BIL 侧严格相等；与 LowerArguments
    // 按形参类型物化同先例）。HoleParameters null（模板绑定失败，P3 已
    // 诊断静默）时跳过 cast 物化直通
    internal sealed class EnumCaseRewriter
        : LoweredVisitor<EnumCaseRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var enumCase = (BoundEnumCaseExpression)node;
            var holes = enumCase.Case.HoleParameters;
            var arguments = new List<LoweredExpression>();
            for (var i = 0; i < enumCase.Arguments.Count; i++)
            {
                var lowered = LowerExpressionDispatcher.Visit(enumCase.Arguments[i], ctx, env);
                if (lowered == null) return null;
                arguments.Add(LoweringFacility.EnsureDeclaredType(enumCase.Arguments[i],
                    lowered, holes?[i].Type));
            }
            return new LoweredEnumCaseExpression(enumCase, enumCase.Case, arguments);
        }
    }
}
