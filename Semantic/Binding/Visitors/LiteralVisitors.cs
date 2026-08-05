namespace LatteCompiler
{
    // 字面量绑定（S5 定型；S7f 字符串插值规范化，SYNTAX §3.8）。
    // 自旧 BindSession.BindLiteral/BindStringInterpolation/NullLiteralError 迁移，行为不变。
    internal sealed class LiteralVisitor : ExpressionVisitor<LiteralVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            var literalNode = (LiteralExpressionASTNode)node;
            var literal = literalNode.Literal;
            // 字符串插值（S7f，SYNTAX §3.8）：Parser 已拆分插值段，绑定即规范化
            if (literal is StringLiteralASTNode { InterpolationParts: not null } interpolated)
            {
                return BindStringInterpolation(literalNode, interpolated, scope, ctx, env);
            }
            TypeSymbol type = literal switch
            {
                IntLiteralASTNode i => i.IntType switch
                {
                    IntType.I8 => env.B.Int8,
                    IntType.I16 => env.B.Int16,
                    IntType.I32 => env.B.Int32,
                    IntType.I64 => env.B.Int64,
                    IntType.U8 => env.B.UInt8,
                    IntType.U16 => env.B.UInt16,
                    IntType.U32 => env.B.UInt32,
                    IntType.U64 => env.B.UInt64,
                    _ => throw new CompilerInternalException("未知 IntType: " + i.IntType),
                },
                FloatLiteralASTNode f => f.IsFloat ? env.B.Float : env.B.Double,
                StringLiteralASTNode => env.B.String,
                CharLiteralASTNode => env.B.Char,
                BoolLiteralASTNode => env.B.Bool,
                // null 的类型由上下文给出（var x: T? = null；实参/return/赋值同）
                NullLiteralASTNode => expectedType ?? NullLiteralError(node, env),
                _ => throw new CompilerInternalException("未知字面量节点: " + literal.GetType().Name),
            };
            return new BoundLiteralExpression(node, type);
        }

        // 字符串插值（S7f，SYNTAX §3.8）：段序列绑定为 toString/拼接链——
        // 字面量段按普通 String 字面量绑定（段级子结构，复用字面量机器）；
        // 表达式段非 String 时包 toString() 实例调用（Any 承诺，沿 BaseType
        // 链静态绑定最近声明，运行期虚派发——override 自然生效）；全 String
        // 段按源码顺序左结合折叠为 + 链（String.Add intrinsic，BIL §11.2）。
        // 每个插值段在 bound 树中恰出现一次（求值一次，§3.8）
        private static BoundExpression? BindStringInterpolation(LiteralExpressionASTNode node,
            StringLiteralASTNode literal, Scope scope, BindContext ctx, BindEnvironment env)
        {
            BoundExpression? chain = null;
            foreach (var part in literal.InterpolationParts!)
            {
                BoundExpression? segment;
                if (part.Text != null)
                {
                    segment = LiteralVisitor.Visit(part.Text, scope, ctx, env);
                    if (segment == null) return null;
                }
                else
                {
                    var value = ExpressionDispatcher.Visit(part.Expression!.Expression, scope, ctx, env);
                    if (value == null) return null;
                    // 毒化静默：段已失败时不再报次生错误
                    if (value.Type is ErrorTypeSymbol) return null;
                    segment = value;
                    if (!ReferenceEquals(value.Type, env.B.String))
                    {
                        // 非 String 段 → toString()（SYNTAX §3.8 全类型承诺；
                        // Any 恒在 BaseType 链顶，查找不可能落空；S9a：
                        // 泛型参数段在 Object 上查找——共享代码体运行时按
                        // typeid 分派实际 toString）
                        var toString = (value.Type is TypeSymbol valueType
                                ? SymbolLookup.FindInstanceMethods(valueType, "toString")
                                : SymbolLookup.FindInstanceMethods(env.B.Object, "toString"))
                            .FirstOrDefault(m => m.Parameters.Count == 0);
                        if (toString == null)
                        {
                            throw new CompilerInternalException(
                                $"类型 '{BoundAnalysis.TypeDisplay(value.Type)}' 的 BaseType 链上未找到 toString");
                        }
                        segment = new BoundInstanceCallExpression(node, value, toString,
                            new List<BoundExpression>(), env.B.String);
                    }
                }
                if (chain == null)
                {
                    chain = segment;
                    continue;
                }
                chain = new BoundBinaryExpression(node, BilIntrinsicOp.Add, chain, segment,
                    env.B.String);
            }
            // HasInterpolation ⇒ 至少一个表达式段，chain 恒非空
            return chain;
        }

        private static TypeSymbol NullLiteralError(ASTNode node, BindEnvironment env)
        {
            env.Error(node.Span, "null requires a nullable type context");
            return env.Unit.Symbols.ErrorType;
        }
    }
}
