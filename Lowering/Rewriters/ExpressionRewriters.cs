namespace LatteCompiler
{
    // 表达式降级（S5–S8c；前置语句追加到当前块输出列表）。
    // 自旧 LowerSession.LowerExpression 各分支迁移，行为不变。

    // 表达式设施：独立块上下文降级（judge/分支块合成用）
    internal static class ExpressionFacility
    {
        // 在独立块上下文里降级表达式并写目标局部（前置语句随块走）；
        // 产物是单语句合成块（Origin 指最近语法来源）
        public static LoweredBlock? LowerAssignInNewBlock(BoundNode origin, BoundExpression value,
            LocalSymbol target, LowerContext ctx, LowerEnvironment env)
        {
            var statements = new List<LoweredStatement>();
            ctx.OutputStack.Push(statements);
            try
            {
                var lowered = LowerExpressionDispatcher.Visit(value, ctx, env);
                if (lowered == null) return null;
                statements.Add(new LoweredAssignmentStatement(origin,
                    LowerContext.ReferenceTo(origin, target), lowered));
                return new LoweredBlock(origin, statements);
            }
            finally
            {
                ctx.OutputStack.Pop();
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

    internal sealed class ValueReferenceRewriter
        : LoweredVisitor<ValueReferenceRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var reference = (BoundValueReferenceExpression)node;
            return new LoweredValueReferenceExpression(reference, reference.Symbol);
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
            return new LoweredCallExpression(call, call.Method, arguments);
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
            var s = ctx.NewSynthLocal(binary.Type);
            var condition = LowerExpressionDispatcher.Visit(binary.Left, ctx, env);
            if (condition == null) return null;
            var assignRight = ExpressionFacility.LowerAssignInNewBlock(binary, binary.Right, s,
                ctx, env);
            if (assignRight == null) return null;
            var constant = new LoweredConstantExpression(binary,
                binary.Op == BilIntrinsicOp.And ? false : true, binary.Type);
            var assignConstant = new LoweredBlock(binary, new List<LoweredStatement>
            {
                new LoweredAssignmentStatement(binary, LowerContext.ReferenceTo(binary, s),
                    constant),
            });
            var (trueBlock, falseBlock) = binary.Op == BilIntrinsicOp.And
                ? (assignRight, assignConstant)
                : (assignConstant, assignRight);
            ctx.OutputStack.Peek().Add(new LoweredIfStatement(binary, condition,
                trueBlock, falseBlock));
            return LowerContext.ReferenceTo(binary, s);
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
            var result = ctx.NewSynthLocal(ifExpression.Type);
            var condition = LowerExpressionDispatcher.Visit(ifExpression.Condition, ctx, env);
            if (condition == null) return null;
            var trueBranch = ValueBlockRewriter.Visit(ifExpression.TrueBranch,
                new ValueBlockContext(ctx, result), env);
            var falseBranch = ValueBlockRewriter.Visit(ifExpression.FalseBranch,
                new ValueBlockContext(ctx, result), env);
            if (trueBranch == null || falseBranch == null) return null;
            ctx.OutputStack.Peek().Add(new LoweredIfStatement(ifExpression, condition,
                trueBranch, falseBranch));
            return LowerContext.ReferenceTo(ifExpression, result);
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
            var result = ctx.NewSynthLocal(switchExpression.Type);
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
            ctx.OutputStack.Peek().Add(statement);
            return LowerContext.ReferenceTo(switchExpression, result);
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
            return LowerContext.ReferenceTo(placeholder,
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
            var target = LowerExpressionDispatcher.Visit(compound.Target, ctx, env);
            if (target == null) return null;
            target = MaterializeTarget(target, ctx);
            var value = LowerExpressionDispatcher.Visit(compound.Value, ctx, env);
            if (value == null) return null;
            ctx.OutputStack.Peek().Add(new LoweredAssignmentStatement(compound, target,
                new LoweredBinaryExpression(compound, compound.Op, target, value)));
            return target;
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
                case LoweredFieldAccessExpression fieldAccess:
                    return new LoweredFieldAccessExpression(fieldAccess.Origin,
                        MaterializeInto(fieldAccess.Origin, fieldAccess.Receiver, ctx),
                        fieldAccess.Field);
                case LoweredIndexExpression indexAccess:
                    return new LoweredIndexExpression((BoundIndexExpression)indexAccess.Origin,
                        MaterializeInto(indexAccess.Origin, indexAccess.Receiver, ctx),
                        MaterializeInto(indexAccess.Origin, indexAccess.Index, ctx));
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
            var local = ctx.NewSynthLocal(expr.Type);
            ctx.OutputStack.Peek().Add(new LoweredAssignmentStatement(origin,
                LowerContext.ReferenceTo(origin, local), expr));
            return LowerContext.ReferenceTo(origin, local);
        }

        // 纯读取判定（无副作用，重复求值安全）：局部/参数/静态字段/字面量/
        // 常量/this，及全由它们构成的字段/索引链
        private static bool IsSideEffectFree(LoweredExpression expr) => expr switch
        {
            LoweredValueReferenceExpression or LoweredFieldReferenceExpression
                or LoweredLiteralExpression or LoweredConstantExpression
                or LoweredThisExpression => true,
            LoweredFieldAccessExpression fieldAccess => IsSideEffectFree(fieldAccess.Receiver),
            LoweredIndexExpression indexAccess => IsSideEffectFree(indexAccess.Receiver)
                && IsSideEffectFree(indexAccess.Index),
            _ => false,
        };
    }

    internal sealed class ThisRewriter : LoweredVisitor<ThisRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            return new LoweredThisExpression((BoundThisExpression)node);
        }
    }

    // 实例调用：receiver 降级 + 调用点 cast 物化（BIL §6.5）——
    // receiver 静态类型 ≠ 方法宿主时包显式 cast（沿 BaseType 链找到的
    // 成员在子类 receiver 上调用时的装箱/基类视图转换，§12.1）
    internal sealed class InstanceCallRewriter
        : LoweredVisitor<InstanceCallRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var instanceCall = (BoundInstanceCallExpression)node;
            var receiver = LowerExpressionDispatcher.Visit(instanceCall.Receiver, ctx, env);
            if (receiver == null) return null;
            receiver = LoweringFacility.EnsureDeclaredType(instanceCall, receiver,
                instanceCall.Method.Owner);
            var arguments = LoweringFacility.LowerArguments(instanceCall.Arguments,
                instanceCall.Method.Parameters, ctx, env);
            if (arguments == null) return null;
            return new LoweredInstanceCallExpression(instanceCall, receiver,
                instanceCall.Method, arguments, instanceCall.Type);
        }
    }

    internal sealed class FieldAccessRewriter
        : LoweredVisitor<FieldAccessRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var fieldAccess = (BoundFieldAccessExpression)node;
            var receiver = LowerExpressionDispatcher.Visit(fieldAccess.Receiver, ctx, env);
            if (receiver == null) return null;
            return new LoweredFieldAccessExpression(fieldAccess, receiver, fieldAccess.Field);
        }
    }

    // 索引访问恒等降级（S8c，BIL §13.6 直接对应，无脱糖）：receiver/index
    // 递归降级；读/写共用节点，指令选择归 P4b 按所在位置
    internal sealed class IndexRewriter
        : LoweredVisitor<IndexRewriter, LoweredExpression, LowerContext>
    {
        protected override LoweredExpression? VisitCore(BoundNode node, LowerContext ctx,
            LowerEnvironment env)
        {
            var indexAccess = (BoundIndexExpression)node;
            var receiver = LowerExpressionDispatcher.Visit(indexAccess.Receiver, ctx, env);
            if (receiver == null) return null;
            var index = LowerExpressionDispatcher.Visit(indexAccess.Index, ctx, env);
            if (index == null) return null;
            return new LoweredIndexExpression(indexAccess, receiver, index);
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
    // 动态形态的 TargetValue 递归降级
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
                operand, typeCheck.TargetType, targetValue, typeCheck.Type);
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
}
