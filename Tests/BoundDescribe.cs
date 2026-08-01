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
    ///   IfExpr(c, 真值块, 假值块, i32)  CompoundAssign(Add, t, v, i32)
    ///   This(C)（S7c-2）  InstCall(name, receiver, [args], ret)  InstField(f, receiver, T)
    ///   SwitchExpr(sel, [Case(m, 值块); CaseP(m, 值块)], 默认值块, T)（S7d；CaseP = pattern 分支）
    ///   Placeholder(T)（S7d，switch pattern 的 _）
    /// 格式约定（语句）：
    ///   Decl(x, i32, = init)  ExprStmt(e)  CallStmt(name, [args])  Assign(t, v)  Return(v)  Return
    ///   If(c, [真], [假])  If(c, [真])  ReturnValue(_, v)（标签取 Target.Label）
    ///   值块：ValueBlock(标签, 类型, [块])；隐式取值带 implicit 标记；纯穿透类型显式 -
    ///   Loop(while, c, [体])  Loop(do-while, c, [体])（named 标签带 @ 后缀：Loop(while@outer, ...)）
    ///   Break  Continue（标签取 Target.Label，非空时带 @：Break@outer）
    ///   For(i, iterable, [体])（S7c-2，标签带 @：For(i@outer, ...)；变量名取 LoopVariable）
    ///   InstCallStmt(name, receiver, [args])（void 实例调用语句，S7c-2）
    ///   Switch(sel, [Case(m, [体]); CaseP(m, [体])], [default])（S7d；CaseP = pattern 分支）
    ///   Throw(e)（S7d）
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
                BoundCallStatement call => call.Receiver == null
                    ? $"CallStmt({call.Method.Name}, [{string.Join(", ", call.Arguments.Select(Expr))}])"
                    : $"InstCallStmt({call.Method.Name}, {Expr(call.Receiver)}, " +
                        $"[{string.Join(", ", call.Arguments.Select(Expr))}])",
                BoundAssignmentStatement assign => $"Assign({Expr(assign.Target)}, {Expr(assign.Value)})",
                BoundReturnStatement ret =>
                    ret.Value != null ? $"Return({Expr(ret.Value)})" : "Return",
                BoundIfStatement ifStmt => ifStmt.FalseBlock != null
                    ? $"If({Expr(ifStmt.Condition)}, {Block(ifStmt.TrueBlock)}, " +
                        $"{Block(ifStmt.FalseBlock)})"
                    : $"If({Expr(ifStmt.Condition)}, {Block(ifStmt.TrueBlock)})",
                BoundReturnValueStatement returnValue =>
                    $"ReturnValue({returnValue.Target.Label}, {Expr(returnValue.Value)})",
                BoundLoop loop => loop.Kind == LoopKind.For
                    ? $"For({loop.LoopVariable!.Name}" +
                        $"{(loop.Label != null ? "@" + loop.Label : "")}, " +
                        $"{Expr(loop.Iterable!)}, {Block(loop.Body)})"
                    : $"Loop({(loop.Kind == LoopKind.While ? "while" : "do-while")}" +
                        $"{(loop.Label != null ? "@" + loop.Label : "")}, " +
                        $"{Expr(loop.Condition!)}, {Block(loop.Body)})",
                BoundLoopControl loopControl =>
                    $"{(loopControl.IsBreak ? "Break" : "Continue")}" +
                    $"{(loopControl.Target.Label != null ? "@" + loopControl.Target.Label : "")}",
                BoundSwitchStatement switchStmt =>
                    $"Switch({Expr(switchStmt.Selector)}, [{string.Join("; ", switchStmt.Cases.Select(c => $"{(c.IsPattern ? "CaseP" : "Case")}({Expr(c.Match)}, {Block(c.Body)})"))}], {Block(switchStmt.DefaultBody)})",
                BoundThrowStatement throwStmt => $"Throw({Expr(throwStmt.Exception)})",
                _ => $"<{stmt.GetType().Name}>",
            };
        }

        // 值块：ValueBlock(标签, 产值类型, [块])；隐式取值带 implicit 标记；
        // 纯穿透（无本块产值）类型显式 -
        public static string ValueBlock(BoundValueBlock valueBlock)
        {
            var type = valueBlock.ValueType != null ? TypeShort(valueBlock.ValueType) : "-";
            var implicitMark = valueBlock.IsImplicitValue ? ", implicit" : "";
            return $"ValueBlock({valueBlock.Label}, {type}{implicitMark}, " +
                $"{Block(valueBlock.Block)})";
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
                BoundIfExpression ifExpr =>
                    $"IfExpr({Expr(ifExpr.Condition)}, {ValueBlock(ifExpr.TrueBranch)}, " +
                    $"{ValueBlock(ifExpr.FalseBranch)}, {TypeShort(ifExpr.Type)})",
                BoundCompoundAssignmentExpression compound =>
                    $"CompoundAssign({compound.Op}, {Expr(compound.Target)}, " +
                    $"{Expr(compound.Value)}, {TypeShort(compound.Type)})",
                BoundThisExpression => $"This({TypeShort(expr.Type)})",
                BoundInstanceCallExpression instCall =>
                    $"InstCall({instCall.Method.Name}, {Expr(instCall.Receiver)}, " +
                    $"[{string.Join(", ", instCall.Arguments.Select(Expr))}], " +
                    $"{TypeShort(instCall.Type)})",
                BoundFieldAccessExpression fieldAccess =>
                    $"InstField({fieldAccess.Field.Name}, {Expr(fieldAccess.Receiver)}, " +
                    $"{TypeShort(fieldAccess.Type)})",
                BoundSwitchExpression switchExpr =>
                    $"SwitchExpr({Expr(switchExpr.Selector)}, [{string.Join("; ", switchExpr.Cases.Select(c => $"{(c.IsPattern ? "CaseP" : "Case")}({Expr(c.Match)}, {ValueBlock(c.Body)})"))}], {ValueBlock(switchExpr.DefaultBody)}, {TypeShort(switchExpr.Type)})",
                BoundSwitchPlaceholderExpression placeholder =>
                    $"Placeholder({TypeShort(placeholder.Type)})",
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

        // 类型短名：Nullable\<T\> 显示为 T?，其余构造类型 Name<args> 递归；
        // null = .breakid 局部（P4a 合成物，Bound 层不出现，签名与
        // LoweredDescribe 对齐）
        private static string TypeShort(TypeSymbol? type)
        {
            if (type == null) return ".breakid";
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
