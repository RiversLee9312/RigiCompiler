using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 统一 LoweredTree 描述器（S7a）：P4a 测试共用的唯一描述器，仿 BoundDescribe。
    /// 字面量值经 Origin 链回指取（LoweredNode.Origin → BoundNode.Syntax →
    /// LiteralExpressionASTNode.Literal），定型类型透传 Origin（Type => Bound.Type），
    /// 以短名显示（Nullable\<T\> 显示为 T?）。
    ///
    /// 格式约定（表达式）：
    ///   Int(42,i32)  Float(3.14,double)  Str("...",String)  Char('A',char)  Bool(True,bool)  Null(String?)
    ///   Local(x,i32)  Param(a,i32)  Field(g,i32)  Const(True,bool)（P4a 合成常量）
    ///   Binary(Add, l, r, i32)  Unary(Opposite, x, i32)
    ///   Call(name, [args], ret)  New(T, [args])  New(T, init, [args])
    /// 格式约定（语句）：
    ///   Decl(x, i32, = init)  ExprStmt(e)  CallStmt(name, [args])  Assign(t, v)  Return(v)  Return
    ///   If(c, [真], [假])  If(c, [真])
    ///   块：[s1; s2]；函数体：Body(name, [x: i32, ...], [块])
    /// </summary>
    public static class LoweredDescribe
    {
        public static string Body(LoweredFunctionBody body)
        {
            var locals = string.Join(", ", body.Locals.Select(l => $"{l.Name}: {TypeShort(l.Type)}"));
            return $"Body({body.Method.Name}, [{locals}], {Block(body.Body)})";
        }

        public static string Block(LoweredBlock block)
        {
            return $"[{string.Join("; ", block.Statements.Select(Stmt))}]";
        }

        public static string Stmt(LoweredStatement stmt)
        {
            return stmt switch
            {
                LoweredBlock block => Block(block),
                LoweredLocalDeclarationStatement decl =>
                    $"Decl({decl.Local.Name}, {TypeShort(decl.Local.Type)}" +
                    $"{(decl.Initializer != null ? $", = {Expr(decl.Initializer)}" : "")})",
                LoweredExpressionStatement exprStmt => $"ExprStmt({Expr(exprStmt.Expression)})",
                LoweredCallStatement call =>
                    $"CallStmt({call.Method.Name}, [{string.Join(", ", call.Arguments.Select(Expr))}])",
                LoweredAssignmentStatement assign => $"Assign({Expr(assign.Target)}, {Expr(assign.Value)})",
                LoweredReturnStatement ret =>
                    ret.Value != null ? $"Return({Expr(ret.Value)})" : "Return",
                LoweredIfStatement ifStmt => ifStmt.FalseBlock != null
                    ? $"If({Expr(ifStmt.Condition)}, {Block(ifStmt.TrueBlock)}, " +
                        $"{Block(ifStmt.FalseBlock)})"
                    : $"If({Expr(ifStmt.Condition)}, {Block(ifStmt.TrueBlock)})",
                _ => $"<{stmt.GetType().Name}>",
            };
        }

        public static string Expr(LoweredExpression? expr)
        {
            return expr switch
            {
                null => "<null>",
                LoweredLiteralExpression literal => Literal(literal),
                // P4a 合成常量（S7b 仅 bool）：值在节点上（无字面量语法来源）
                LoweredConstantExpression constant => constant.Value switch
                {
                    bool b => $"Const({b},{TypeShort(constant.Type)})",
                    var other => $"<Const {other}>",
                },
                LoweredValueReferenceExpression valueRef => valueRef.Symbol switch
                {
                    LocalSymbol local => $"Local({local.Name},{TypeShort(valueRef.Type)})",
                    ParameterSymbol param => $"Param({param.Name},{TypeShort(valueRef.Type)})",
                    _ => $"<{valueRef.Symbol.GetType().Name}>",
                },
                LoweredFieldReferenceExpression fieldRef =>
                    $"Field({fieldRef.Field.Name},{TypeShort(fieldRef.Type)})",
                LoweredBinaryExpression binary =>
                    $"Binary({binary.Op}, {Expr(binary.Left)}, {Expr(binary.Right)}, {TypeShort(binary.Type)})",
                LoweredUnaryExpression unary =>
                    $"Unary({unary.Op}, {Expr(unary.Operand)}, {TypeShort(unary.Type)})",
                LoweredCallExpression call =>
                    $"Call({call.Method.Name}, [{string.Join(", ", call.Arguments.Select(Expr))}], " +
                    $"{TypeShort(call.Type)})",
                LoweredNewExpression newExpr =>
                    $"New({TypeShort(newExpr.Type)}{(newExpr.Init != null ? ", init" : "")}, " +
                    $"[{string.Join(", ", newExpr.Arguments.Select(Expr))}])",
                _ => $"<{expr.GetType().Name}>",
            };
        }

        // 字面量：值经 Origin.Syntax 回指取，类型取透传的定型结果
        private static string Literal(LoweredLiteralExpression literal)
        {
            var type = TypeShort(literal.Type);
            return ((LiteralExpressionASTNode)literal.Origin.Syntax).Literal switch
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
