using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 统一 BoundTree 描述器（S5，M41）：P3 测试共用的唯一描述器，仿 AstDescribe。
    /// 字面量值经 Syntax 回指取（BoundNode.Syntax → LiteralExpressionASTNode.Literal），
    /// 定型类型以短名显示（Nullable\<T\> 显示为 T?）。
    ///
    /// 格式约定（表达式）：
    ///   Int(42,i32)  Float(3.14,double)  Str("...",String)  Char('A',char)  Bool(True,bool)  Null(String?)
    ///   Local(x,i32)  Param(a,i32)  Field(g,i32)
    ///   Binary(Add, l, r, i32)  Unary(Opposite, x, i32)
    ///   Call(name, [args], ret)  New(T, [args])  New(T, init, [args])
    /// 格式约定（语句）：
    ///   Decl(x, i32, = init)  ExprStmt(e)  CallStmt(name, [args])  Assign(t, v)  Return(v)  Return
    ///   块：[s1; s2]；函数体：Body(name, [x: i32, ...], [块])
    /// </summary>
    public static class BoundDescribe
    {
        public static string Body(BoundFunctionBody body)
        {
            var locals = string.Join(", ", body.Locals.Select(l => $"{l.Name}: {TypeShort(l.Type)}"));
            return $"Body({body.Method.Name}, [{locals}], {Block(body.Body)})";
        }

        public static string Block(BoundBlock block)
        {
            return $"[{string.Join("; ", block.Statements.Select(Stmt))}]";
        }

        public static string Stmt(BoundStatement stmt)
        {
            return stmt switch
            {
                BoundBlock block => Block(block),
                BoundLocalDeclarationStatement decl =>
                    $"Decl({decl.Local.Name}, {TypeShort(decl.Local.Type)}" +
                    $"{(decl.Initializer != null ? $", = {Expr(decl.Initializer)}" : "")})",
                BoundExpressionStatement exprStmt => $"ExprStmt({Expr(exprStmt.Expression)})",
                BoundCallStatement call =>
                    $"CallStmt({call.Method.Name}, [{string.Join(", ", call.Arguments.Select(Expr))}])",
                BoundAssignmentStatement assign => $"Assign({Expr(assign.Target)}, {Expr(assign.Value)})",
                BoundReturnStatement ret =>
                    ret.Value != null ? $"Return({Expr(ret.Value)})" : "Return",
                _ => $"<{stmt.GetType().Name}>",
            };
        }

        public static string Expr(BoundExpression? expr)
        {
            return expr switch
            {
                null => "<null>",
                BoundLiteralExpression literal => Literal(literal),
                BoundValueReferenceExpression valueRef => valueRef.Symbol switch
                {
                    LocalSymbol local => $"Local({local.Name},{TypeShort(valueRef.Type)})",
                    ParameterSymbol param => $"Param({param.Name},{TypeShort(valueRef.Type)})",
                    _ => $"<{valueRef.Symbol.GetType().Name}>",
                },
                BoundFieldReferenceExpression fieldRef =>
                    $"Field({fieldRef.Field.Name},{TypeShort(fieldRef.Type)})",
                BoundBinaryExpression binary =>
                    $"Binary({binary.Op}, {Expr(binary.Left)}, {Expr(binary.Right)}, {TypeShort(binary.Type)})",
                BoundUnaryExpression unary =>
                    $"Unary({unary.Op}, {Expr(unary.Operand)}, {TypeShort(unary.Type)})",
                BoundCallExpression call =>
                    $"Call({call.Method.Name}, [{string.Join(", ", call.Arguments.Select(Expr))}], " +
                    $"{TypeShort(call.Type)})",
                BoundNewExpression newExpr =>
                    $"New({TypeShort(newExpr.Type)}{(newExpr.Init != null ? ", init" : "")}, " +
                    $"[{string.Join(", ", newExpr.Arguments.Select(Expr))}])",
                _ => $"<{expr.GetType().Name}>",
            };
        }

        // 字面量：值经 Syntax 回指取，类型取定型结果
        private static string Literal(BoundLiteralExpression literal)
        {
            var type = TypeShort(literal.Type);
            return ((LiteralExpressionASTNode)literal.Syntax).Literal switch
            {
                IntLiteralASTNode i => $"Int({i.Value},{type})",
                FloatLiteralASTNode f => $"Float({f.Value},{type})",
                StringLiteralASTNode s => $"Str(\"{s.Value}\",{type})",
                CharLiteralASTNode c => $"Char('{c.Value}',{type})",
                BoolLiteralASTNode b => $"Bool({b.Value},{type})",
                NullLiteralASTNode => $"Null({type})",
                var other => $"<{other.GetType().Name}>",
            };
        }

        // 类型短名：Nullable\<T\> 显示为 T?，其余构造类型 Name<args> 递归
        private static string TypeShort(TypeSymbol type)
        {
            if (type.ConstructedFrom == null) return type.Name;
            if (type.Name == "Nullable" && type.TypeArguments!.Count == 1
                && type.TypeArguments[0] is TypeSymbol element)
            {
                return TypeShort(element) + "?";
            }
            return type.Name + "<" + string.Join(", ",
                type.TypeArguments!.Select(a => a is TypeSymbol t ? TypeShort(t) : a.Name)) + ">";
        }
    }
}
