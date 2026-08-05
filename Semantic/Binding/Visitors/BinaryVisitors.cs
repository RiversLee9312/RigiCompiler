namespace LatteCompiler
{
    // 二元/一元/复合赋值运算（S5/S7b，SYNTAX §13.2）与 if? 空值回退（S7f，§3.4）。
    // 自旧 BindSession.BindBinary/BindNullFallback/BindUnary/BindCompoundAssignment
    // 迁移，行为不变。
    internal sealed class BinaryVisitor : ExpressionVisitor<BinaryVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            var binary = (BinaryExpressionASTNode)node;
            // if? 空值回退（S7f，SYNTAX §3.4）：不走 intrinsic 键查询
            if (binary.Operator == "if?")
            {
                return BindNullFallback(binary, scope, ctx, env);
            }
            // null 判等（S8b，SYNTAX §3.4）：==/!= 一侧为 null 字面量的特例
            // ——不走同类型规则；null 定型为 Nullable\<T0\>，非空侧装箱视图
            // cast（§12.1），两侧统一 Nullable\<T0\> 满足 §11.5 严格相同
            if (binary.Operator is "==" or "!=")
            {
                var leftIsNull = IsNullLiteral(binary.Left.Expression);
                var rightIsNull = IsNullLiteral(binary.Right.Expression);
                if (leftIsNull || rightIsNull)
                {
                    return BindNullEquality(binary, leftIsNull, scope, ctx, env);
                }
            }
            var op = IntrinsicMapping.MapBinary(binary.Operator);
            var left = ExpressionDispatcher.Visit(binary.Left.Expression, scope, ctx, env);
            // S8b and/or 右侧收窄上下文（SYNTAX §3.5 短路语义）：
            // and 右侧以左真边、or 右侧以左假边为收窄上下文
            // （`(x is String) and (x.length > 0)` 中右侧在收窄后类型上解析）
            BoundExpression? right;
            if (left != null && binary.Operator is "and" or "or")
            {
                var leftFacts = ConditionFactsExtractor.Extract(left, ctx.Frame);
                var narrowedSnapshot = ctx.Flow.SnapshotNarrowed();
                ctx.Flow.ApplyNarrow(binary.Operator == "and" ? leftFacts.True : leftFacts.False);
                right = ExpressionDispatcher.Visit(binary.Right.Expression, scope, ctx, env);
                ctx.Flow.RestoreNarrowed(narrowedSnapshot);
            }
            else
            {
                right = ExpressionDispatcher.Visit(binary.Right.Expression, scope, ctx, env);
            }
            if (left == null || right == null) return null;
            // 毒化静默：操作数已失败时不再报次生错误
            if (left.Type is ErrorTypeSymbol || right.Type is ErrorTypeSymbol) return null;
            if (!ReferenceEquals(left.Type, right.Type))
            {
                env.Error(binary.Span, $"Binary operator '{binary.Operator}' requires operands " +
                    $"of the same type (got '{BoundAnalysis.TypeDisplay(left.Type)}' and " +
                    $"'{BoundAnalysis.TypeDisplay(right.Type)}')");
                return null;
            }
            // intrinsic 存在检查（S9：泛型参数无 intrinsic 表，判型后自然不命中）
            if (left.Type is not TypeSymbol leftType || !leftType.IntrinsicOps.Contains(op))
            {
                env.Error(binary.Span, $"Operator '{binary.Operator}' is not defined for type " +
                    $"'{BoundAnalysis.TypeDisplay(left.Type)}'");
                return null;
            }
            // BIL §11：比较结果 bool；算术/位/逻辑结果同操作数类型
            var resultType = IntrinsicMapping.IsComparison(op) ? env.B.Bool : left.Type;
            return new BoundBinaryExpression(node, op, left, right, resultType);
        }

        // if? 空值回退（S7f，SYNTAX §3.4）：左操作数必须 Nullable<T>，
        // 右操作数（回退值）必须可赋值到 T，结果类型 T；右操作数延迟求值
        // （P4a 以 if 结构保证）
        private static BoundExpression? BindNullFallback(BinaryExpressionASTNode node, Scope scope,
            BindContext ctx, BindEnvironment env)
        {
            var left = ExpressionDispatcher.Visit(node.Left.Expression, scope, ctx, env);
            if (left == null) return null;
            if (left.Type is ErrorTypeSymbol) return null;
            if (left.Type is not TypeSymbol leftType
                || leftType.ConstructedFrom == null
                || leftType.ConstructedFrom != env.B.NullableDefinition
                || leftType.TypeArguments![0] is not TypeSymbol element)
            {
                env.Error(node.Left.Span ?? node.Span,
                    $"Operator 'if?' requires a nullable left operand " +
                    $"(got '{BoundAnalysis.TypeDisplay(left.Type)}')");
                return null;
            }
            var right = ExpressionDispatcher.Visit(node.Right.Expression, scope, ctx, env, element);
            if (right == null) return null;
            // 毒化静默：任一侧已失败时不再报次生错误
            if (right.Type is ErrorTypeSymbol) return null;
            if (!SymbolLookup.IsAssignable(right.Type, element, env))
            {
                env.Error(node.Right.Span ?? node.Span,
                    $"Null fallback must be assignable to '{BoundAnalysis.TypeDisplay(element)}' " +
                    $"(got '{BoundAnalysis.TypeDisplay(right.Type)}')");
                return null;
            }
            return new BoundNullFallbackExpression(node, left, right, element);
        }

        // null 判等（S8b，SYNTAX §3.4）：一侧 null 字面量 + 另一侧任意
        // 类型 T0——合法，结果 bool；null 定型为 Nullable\<T0\>；T0 非可空
        // 时按「不做静态不可能性拒绝」放行（运行期恒 false/true），非空侧
        // 包装箱视图 cast（§12.1，两侧统一 Nullable\<T0\> 满足 §11.5 严格
        // 相同）；两侧皆 null 无锚定类型——编译错误
        private static BoundExpression? BindNullEquality(BinaryExpressionASTNode node,
            bool leftIsNull, Scope scope, BindContext ctx, BindEnvironment env)
        {
            if (leftIsNull && IsNullLiteral(node.Right.Expression))
            {
                env.Error(node.Span, "null requires a nullable type context");
                return null;
            }
            // 先绑非 null 侧定型 T0
            var valueRoot = leftIsNull ? node.Right : node.Left;
            var value = ExpressionDispatcher.Visit(valueRoot.Expression, scope, ctx, env);
            if (value == null) return null;
            if (value.Type is ErrorTypeSymbol) return null;
            // 非可空 T0 包装箱到 Nullable<T0>（§12.1 装箱视图 cast）；泛型
            // 参数侧无静态 Nullable 构造（S9a 保守：operandType 即 T 自身，
            // null 判等恒 false/true——不落装箱 cast）
            var operandType = value.Type switch
            {
                // 已是 Nullable 构造：原样
                TypeSymbol v when v.ConstructedFrom == env.B.NullableDefinition =>
                    (SemanticSymbol)v,
                // 普通类型：包装
                TypeSymbol v2 => env.Unit.Symbols.GetNullable(v2),
                // 泛型参数：无静态 Nullable 构造，原样
                _ => value.Type,
            };
            // null 侧按 Nullable\<T0\> 定型（泛型参数侧无静态 Nullable，expectedType 空）
            var nullRoot = leftIsNull ? node.Left : node.Right;
            var nullLiteral = ExpressionDispatcher.Visit(nullRoot.Expression, scope, ctx, env,
                operandType as TypeSymbol);
            if (nullLiteral == null) return null;
            // 非空侧装箱到 Nullable\<T0\>（§12.1 装箱视图 cast）
            if (!ReferenceEquals(value.Type, operandType))
            {
                value = new BoundCastExpression(valueRoot, value, operandType, isSafe: false,
                    operandType);
            }
            var (left, right) = leftIsNull ? (nullLiteral, value) : (value, nullLiteral);
            var op = node.Operator == "==" ? BilIntrinsicOp.CmpEq : BilIntrinsicOp.CmpNe;
            return new BoundBinaryExpression(node, op, left, right, env.B.Bool);
        }

        // null 字面量判定（剥透明分组——`(null) == x` 同形态）
        private static bool IsNullLiteral(ASTNode node)
        {
            return node switch
            {
                LiteralExpressionASTNode { Literal: NullLiteralASTNode } => true,
                GroupExpressionASTNode group => IsNullLiteral(group.InnerExpression.Expression),
                _ => false,
            };
        }
    }

    // 一元运算（S5）：await 归 S13（归口诊断）；not/!/-/+ 经 intrinsic
    // 键查询（+ 按恒等——SYNTAX §13.2 无一元正号）
    internal sealed class UnaryVisitor : ExpressionVisitor<UnaryVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            var unary = (UnaryExpressionASTNode)node;
            BilIntrinsicOp op;
            switch (unary.Operator)
            {
                case "await":
                    env.Error(node.Span, "P3: await is not supported yet (S13)");
                    return null;
                case "-": op = BilIntrinsicOp.Opposite; break;
                case "not": op = BilIntrinsicOp.Not; break;
                case "!": op = BilIntrinsicOp.BinNot; break;
                case "+":
                    // 一元正号：SYNTAX §13.2 无此运算符，按恒等处理
                    return ExpressionDispatcher.Visit(unary.Operand.Expression, scope, ctx, env);
                default:
                    throw new CompilerInternalException("未知一元运算符: " + unary.Operator);
            }
            var operand = ExpressionDispatcher.Visit(unary.Operand.Expression, scope, ctx, env);
            if (operand == null) return null;
            if (operand.Type is ErrorTypeSymbol) return null;
            // intrinsic 存在检查（S9：泛型参数无 intrinsic 表，判型后不命中）
            if (operand.Type is not TypeSymbol operandType
                || !operandType.IntrinsicOps.Contains(op))
            {
                env.Error(node.Span, $"Operator '{unary.Operator}' is not defined for type " +
                    $"'{BoundAnalysis.TypeDisplay(operand.Type)}'");
                return null;
            }
            return new BoundUnaryExpression(node, op, operand, operand.Type);
        }
    }

            // 复合赋值（S7b，SYNTAX §13.2；S8c 增补索引 place）：a op= b 即 a = a op b
            // 的语义糖，表达式值为写回后值。Target 规则同赋值（局部/参数/字段/索引
            // place），但读前须已赋值
            // （读语义——普通路径绑定的 unassigned 检查，不做 forAssignment 特免）；
            // Op 复用二元映射（10 个基础运算符，Parser 保证不含 and/or）；
            // 类型一致与 intrinsic 存在检查同 BindBinary；Type = Target 类型；
            // 赋值后 Target 标记 assigned
            internal sealed class CompoundAssignmentVisitor
                : ExpressionVisitor<CompoundAssignmentVisitor, BindContext>
            {
                protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
                    BindEnvironment env, TypeSymbol? expectedType)
                {
                    var compound = (CompoundAssignmentExpressionASTNode)node;
                    var op = IntrinsicMapping.MapBinary(compound.Operator);
                    var target = ExpressionDispatcher.Visit(compound.Target.Expression, scope, ctx, env);
                    var value = ExpressionDispatcher.Visit(compound.Value.Expression, scope, ctx, env,
                        target?.Type as TypeSymbol);
            if (target == null || value == null) return null;
            switch (target)
            {
                case BoundValueReferenceExpression { Symbol: LocalSymbol local }:
                    if (local.IsConst)
                    {
                        env.Error(node.Span, $"Cannot assign to const '{local.Name}'");
                        return null;
                    }
                    ctx.Flow.MarkAssigned(local);
                    // S8b：复合赋值同赋值——收窄失效
                    ctx.Flow.ClearRoot(local);
                    break;
                case BoundValueReferenceExpression { Symbol: ParameterSymbol parameter }:
                    ctx.Flow.ClearRoot(parameter);
                    break;
                case BoundFieldReferenceExpression fieldReference:
                case BoundFieldAccessExpression:
                    // 参数与全局/实例字段：同赋值的放行规则（S8e：带访问器
                    // 字段查 setter 存在性与可见性，无访问器字段走 const 规则）
                    {
                        var field = target is BoundFieldReferenceExpression fr
                            ? fr.Field : ((BoundFieldAccessExpression)target).Field;
                        if (!ConstFieldRules.CheckWritable(field, node.Span, ctx.Frame, env))
                        {
                            return null;
                        }
                        break;
                    }
                case BoundIndexExpression indexTarget:
                    // S8c 索引复合赋值：读语义已含 getAtIndex 检查（目标按
                    // 读模式绑定）；此处要求 2 参数 setAtIndex 存在（写回
                    // 能力）。receiver/index 的读-写双重求值与字段复合
                    // 既有行为一致（P4a 展开时处理）
                    {
                        // S9：泛型参数 receiver 无索引运算符（判型后集合为空）
                        var setters = indexTarget.Receiver.Type is TypeSymbol receiverType
                            ? SymbolLookup.FindInstanceOperators(
                                receiverType, "setAtIndex", 2)
                            : new List<MethodSymbol>();
                        if (setters.Count == 0)
                        {
                            env.Error(node.Span, $"Type " +
                                $"'{BoundAnalysis.TypeDisplay(indexTarget.Receiver.Type)}' " +
                                "does not define an index operator ('setAtIndex')");
                            return null;
                        }
                        if (setters.Count > 1)
                        {
                            env.Error(node.Span, "P3: overload resolution for 'setAtIndex' " +
                                "is not supported yet (S8)");
                            return null;
                        }
                        break;
                    }
                default:
                    env.Error(compound.Target.Span ?? node.Span,
                        "Assignment target must be a variable");
                    return null;
            }
            // 毒化静默：任一侧已失败时不再报次生错误
            if (target.Type is ErrorTypeSymbol || value.Type is ErrorTypeSymbol) return null;
            if (!ReferenceEquals(target.Type, value.Type))
            {
                env.Error(node.Span, $"Compound assignment requires operands of the same type " +
                    $"(got '{BoundAnalysis.TypeDisplay(target.Type)}' and " +
                    $"'{BoundAnalysis.TypeDisplay(value.Type)}')");
                return null;
            }
            // intrinsic 存在检查（S9：泛型参数无 intrinsic 表，判型后不命中）
            if (target.Type is not TypeSymbol targetType || !targetType.IntrinsicOps.Contains(op))
            {
                env.Error(node.Span, $"Operator '{compound.Operator}=' is not defined for type " +
                    $"'{BoundAnalysis.TypeDisplay(target.Type)}'");
                return null;
            }
            return new BoundCompoundAssignmentExpression(node, target, op, value, target.Type);
        }
    }

    // intrinsic 运算符映射（自旧 BindSession 迁移，行为不变）
    internal static class IntrinsicMapping
    {
        public static bool IsComparison(BilIntrinsicOp op)
        {
            return op is BilIntrinsicOp.CmpEq or BilIntrinsicOp.CmpNe or BilIntrinsicOp.CmpLt
                or BilIntrinsicOp.CmpLe or BilIntrinsicOp.CmpGt or BilIntrinsicOp.CmpGe;
        }

        public static BilIntrinsicOp MapBinary(string token)
        {
            return token switch
            {
                "+" => BilIntrinsicOp.Add,
                "-" => BilIntrinsicOp.Sub,
                "*" => BilIntrinsicOp.Mul,
                "/" => BilIntrinsicOp.Div,
                "and" => BilIntrinsicOp.And,
                "or" => BilIntrinsicOp.Or,
                "&" => BilIntrinsicOp.BinAnd,
                "|" => BilIntrinsicOp.BinOr,
                "^" => BilIntrinsicOp.BinXor,
                "<<" => BilIntrinsicOp.ShiftLeft,
                ">>" => BilIntrinsicOp.ShiftRight,
                ">>>" => BilIntrinsicOp.ShiftRightUnsigned,
                "==" => BilIntrinsicOp.CmpEq,
                "!=" => BilIntrinsicOp.CmpNe,
                "<" => BilIntrinsicOp.CmpLt,
                "<=" => BilIntrinsicOp.CmpLe,
                ">" => BilIntrinsicOp.CmpGt,
                ">=" => BilIntrinsicOp.CmpGe,
                _ => throw new CompilerInternalException("未知二元运算符: " + token),
            };
        }
    }
}
