using System.Linq;

namespace RigiCompiler.Tests
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
    ///   Index(receiver, index, T)（S8c，SYNTAX §13.2；T = 读模式返回类型/写模式元素形参类型）
    ///   SwitchExpr(sel, [Case(m, 值块); CaseP(m, 值块)], 默认值块, T)（S7d；CaseP = pattern 分支）
    ///   Placeholder(T)（S7d，switch pattern 的 _）
    ///   Cast(e, T)  SafeCast(e, T)（S7e，as / as?；T = 目标类型）
    ///   SeqExpr(值块)（S7e；volatile 时值块带 volatile 标记）
    ///   SafeAccess(recv, access, T?)  SafeReceiver(T)（S7f，`?.` 与占位叶子）
    ///   NullFallback(l, r, T)（S7f，if? 空值回退）
    ///   Is(e, T)  Supers(e, T)  With(e, T)（S8a；动态形态目标带 dyn 前缀：
    ///   Is(e, dyn t)；结果恒 bool 不打印）
    ///   IsCase(e, RequestResult.Failed)（S11；§12.3 判别匹配）
    ///   EnumCase(RequestResult.Success, [])  EnumCase(RequestResult.Failed, [args])（S11）
    ///   TypeOf(e, RT)（值形态）  TypeOf(type T, RT)（类型形态）（S8a；RT = Type\<T\>）
    /// 格式约定（语句）：
    ///   Decl(x, i32, = init)  ExprStmt(e)  CallStmt(name, [args])  Assign(t, v)  Return(v)  Return
    ///   If(c, [真], [假])  If(c, [真])  ReturnValue(_, v)（标签取 Target.Label）
    ///   值块：ValueBlock(标签, 类型, [块])；隐式取值带 implicit 标记；纯穿透类型显式 -；
    ///   volatile（S7e seq）带 volatile 标记
    ///   Loop(while, c, [体])  Loop(do-while, c, [体])（named 标签带 @ 后缀：Loop(while@outer, ...)）
    ///   Break  Continue（标签取 Target.Label，非空时带 @：Break@outer）
    ///   For(i, iterable, [体])（S7c-2，标签带 @：For(i@outer, ...)；变量名取 LoopVariable）
    ///   InstCallStmt(name, receiver, [args])（void 实例调用语句，S7c-2）
    ///   Switch(sel, [Case(m, [体]); CaseP(m, [体])], [default])（S7d；CaseP = pattern 分支）
    ///   Throw(e)（S7d）
    ///   Try([try], [Catch(e, T, [体]); Catch(T, [体])], Finally(e, [体]))（S7e；
    ///   无变量 catch 省变量名，无参 finally 省参数，无 finally 省第三参）
    ///   Seq([体])  SeqVolatile([体])（S7e 语句形态）
    ///   Destructuring([a: T ← key; b: T ← value], init)（S7f，SYNTAX §18）
    ///   块：[s1; s2]；函数体：Body(name, [x: i32, ...], [块])
    /// </summary>
    public static class BoundDescribe
    {
        public static string Body(BoundFunctionBody body)
        {
            var locals = string.Join(", ", body.Locals.Select(l => $"{l.Name}: {TypeShort.Of(l.Type)}"));
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
                    $"Decl({decl.Local.Name}, {TypeShort.Of(decl.Local.Type)}" +
                    $"{(decl.Initializer != null ? $", = {Expr(decl.Initializer)}" : "")})",
                BoundExpressionStatement exprStmt => $"ExprStmt({Expr(exprStmt.Expression)})",
                BoundYieldStatement yield => yield.Alarm == null
                    ? "Yield" : $"Yield({Expr(yield.Alarm)})",
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
                BoundTryStatement tryStmt => Try(tryStmt),
                BoundSeqStatement seqStmt =>
                    $"{(seqStmt.IsVolatile ? "SeqVolatile" : "Seq")}" +
                    $"{(seqStmt.IsUnsafe ? "Unsafe" : "")}" +
                    $"{(seqStmt.Label != null ? "@" + seqStmt.Label : "")}" +
                    $"({(seqStmt.UsingBindings.Count == 0 ? "" : string.Join(", ", seqStmt.UsingBindings.Select(Using)))}{Block(seqStmt.Body)})",
                // M61：return@语句seq（不携带值，Target.Label 必非 null）
                BoundSeqExitStatement seqExit => $"SeqExit(@{seqExit.Target.Label})",
                BoundNewWrapperStatement nw =>
                    $"NewWrapper({nw.Kind}, {nw.WrapperType.Name}" +
                    $"{(nw.Target != null ? ", " + nw.Target.Name : "")}, " +
                    $"[{string.Join(", ", nw.Arguments.Select(Expr))}])",
                // S7f 解构声明：Destructuring([a: String ← key; b: i32 ← value], init)
                BoundDestructuringDeclarationStatement destructuring =>
                    $"Destructuring([{string.Join("; ", destructuring.Entries.Select(e => $"{e.Local.Name}: {TypeShort.Of(e.Local.Type)} ← {e.Field.Name}"))}], {Expr(destructuring.Initializer)})",
                _ => $"<{stmt.GetType().Name}>",
            };
        }

        // try：Try([try], [Catch(e, T, [体]); Catch(T, [体])], Finally(e, [体])；
        // 无变量 catch 省变量名，无参 finally 省参数，无 finally 省第三参（S7e）
        private static string Try(BoundTryStatement tryStmt)
        {
            var catches = string.Join("; ", tryStmt.Catches.Select(c =>
                c.Variable != null
                    ? $"Catch({c.Variable.Name}, {TypeShort.Of(c.ExceptionType)}, {Block(c.Body)})"
                    : $"Catch({TypeShort.Of(c.ExceptionType)}, {Block(c.Body)})"));
            if (tryStmt.FinallyBlock == null)
            {
                return $"Try({Block(tryStmt.TryBlock)}, [{catches}])";
            }
            var finallyPart = tryStmt.FinallyVariable != null
                ? $"Finally({tryStmt.FinallyVariable.Name}, {Block(tryStmt.FinallyBlock)})"
                : $"Finally({Block(tryStmt.FinallyBlock)})";
            return $"Try({Block(tryStmt.TryBlock)}, [{catches}], {finallyPart})";
        }

        private static string Using(BoundUsingBinding binding) =>
            $"using({(binding.Local.IsConst ? "const" : "var")} {binding.Local.Name}, " +
            $"{Expr(binding.Initializer)}, dispose={binding.DisposeMethod.Name})";

        // 值块：ValueBlock(标签, 产值类型, [块])；隐式取值带 implicit 标记；
        // 纯穿透（无本块产值）类型显式 -；volatile（S7e seq）带 volatile 标记
        public static string ValueBlock(BoundValueBlock valueBlock)
        {
            var type = valueBlock.ValueType != null ? TypeShort.Of(valueBlock.ValueType) : "-";
            var implicitMark = valueBlock.IsImplicitValue ? ", implicit" : "";
            var volatileMark = valueBlock.IsVolatile ? ", volatile" : "";
            var unsafeMark = valueBlock.IsUnsafe ? ", unsafe" : "";
            return $"ValueBlock({valueBlock.Label}, {type}{implicitMark}{volatileMark}{unsafeMark}, " +
                $"{Block(valueBlock.Block)})";
        }

        public static string Expr(BoundExpression? expr)
        {
            return expr switch
            {
                null => "<null>",
                BoundLiteralExpression literal => Literal(literal),
                BoundLambdaExpression lambda =>
                    $"Lambda([{string.Join(", ", lambda.Closure.Call.Parameters.Select(p => p.Name))}], " +
                    $"{Block(lambda.CallBody.Body)}, " +
                    $"{(lambda.ReturnType != null ? TypeShort.Of(lambda.ReturnType) : "void")}, " +
                    $"captures=[{string.Join(", ", lambda.CapturedSymbols.Select(s => s.Name))}]" +
                    $"{(lambda.Closure.Call.AppliedWrappers.Count == 0 ? "" :
                        ", wrappers=[" + string.Join(", ", lambda.Closure.Call.AppliedWrappers.Select(a => a.Wrapper.Name)) + "]")})",
                BoundValueReferenceExpression valueRef => valueRef.Symbol switch
                {
                    LocalSymbol local => $"Local({local.Name},{TypeShort.Of(valueRef.Type)})",
                    ParameterSymbol param => $"Param({param.Name},{TypeShort.Of(valueRef.Type)})",
                    _ => $"<{valueRef.Symbol.GetType().Name}>",
                },
                BoundFieldReferenceExpression fieldRef =>
                    $"Field({fieldRef.Field.Name},{TypeShort.Of(fieldRef.Type)})",
                BoundBinaryExpression binary =>
                    $"Binary({binary.Op}, {Expr(binary.Left)}, {Expr(binary.Right)}, {TypeShort.Of(binary.Type)})",
                BoundUnaryExpression unary =>
                    $"Unary({unary.Op}, {Expr(unary.Operand)}, {TypeShort.Of(unary.Type)})",
                BoundAwaitExpression awaitExpression =>
                    $"Await({Expr(awaitExpression.Operand)}, " +
                    $"{(awaitExpression.HasResult ? TypeShort.Of(awaitExpression.ResultType!) : "void")})",
                BoundCallExpression call =>
                    $"Call({call.Method.Name}, [{string.Join(", ", call.Arguments.Select(Expr))}], " +
                    $"{TypeShort.Of(call.Type)})",
                BoundNewExpression newExpr =>
                    $"New({TypeShort.Of(newExpr.Type)}{(newExpr.Init != null ? ", init" : "")}, " +
                    $"[{string.Join(", ", newExpr.Arguments.Select(Expr))}])",
                // 动态 new（§3.7）：目标带 dyn 前缀（Type\<T\> 值）或
                // generic 前缀（泛型参数具化构造），与 TypeCheck 动态形态同风
                BoundDynamicNewExpression dynamicNew =>
                    $"DynamicNew({(dynamicNew.GenericParameter != null
                        ? "generic " + dynamicNew.GenericParameter.Name
                        : "dyn " + Expr(dynamicNew.TypeValue!))}, " +
                    $"[{string.Join(", ", dynamicNew.Arguments.Select(Expr))}], " +
                    $"{TypeShort.Of(dynamicNew.Type)})",
                BoundIfExpression ifExpr =>
                    $"IfExpr({Expr(ifExpr.Condition)}, {ValueBlock(ifExpr.TrueBranch)}, " +
                    $"{ValueBlock(ifExpr.FalseBranch)}, {TypeShort.Of(ifExpr.Type)})",
                BoundCompoundAssignmentExpression compound =>
                    $"CompoundAssign({compound.Op}, {Expr(compound.Target)}, " +
                    $"{Expr(compound.Value)}, {TypeShort.Of(compound.Type)})",
                BoundThisExpression => $"This({TypeShort.Of(expr.Type)})",
                BoundSelfExpression => $"Self({TypeShort.Of(expr.Type)})",
                BoundInnerCallExpression innerCall =>
                    $"InnerCall([{string.Join(", ", innerCall.Arguments.Select(Expr))}]" +
                    $"{(innerCall.ForwardedGenericPacks.Count == 0 ? ""
                        : ", packs=[" + string.Join(", ",
                            innerCall.ForwardedGenericPacks.Select(p => p.Name)) + "]")}, " +
                    $"{(innerCall.IsVoid ? "void" : TypeShort.Of(innerCall.Type))})",
                BoundSuperCallExpression superCall =>
                    $"SuperCall({superCall.Method.Name}, [{string.Join(", ", superCall.Arguments.Select(Expr))}], " +
                    $"{(superCall.IsVoid ? "void" : TypeShort.Of(superCall.Type))})",
                BoundInstanceCallExpression instCall =>
                    $"InstCall({instCall.Method.Name}, {Expr(instCall.Receiver)}, " +
                    $"[{string.Join(", ", instCall.Arguments.Select(Expr))}], " +
                    $"{TypeShort.Of(instCall.Type)})",
                BoundFieldAccessExpression fieldAccess =>
                    $"InstField({fieldAccess.Field.Name}, {Expr(fieldAccess.Receiver)}, " +
                    $"{TypeShort.Of(fieldAccess.Type)})",
                // S8c：索引访问（Operator 符号不打印——黄金描述聚焦形态与定型）
                BoundIndexExpression index =>
                    $"Index({Expr(index.Receiver)}, {Expr(index.Index)}, {TypeShort.Of(index.Type)})",
                // S11：wrapper place（只读存储位置；Wrapper 符号即 Type，不重复打印）
                BoundWrapperAccessExpression wrapperAccess =>
                    $"WrapperPlace({Expr(wrapperAccess.Receiver)}, " +
                    $"{wrapperAccess.Wrapper.Name})",
                // S11：enum case 构造（类型恒为宿主 enum，不重复打印）
                BoundEnumCaseExpression enumCase =>
                    $"EnumCase{(enumCase.ArgumentsAreInitArguments ? "Init" : "")}({TypeShort.Of(enumCase.Type)}.{enumCase.Case.Name}, " +
                    $"[{string.Join(", ", enumCase.Arguments.Select(Expr))}])",
                BoundSwitchExpression switchExpr =>
                    $"SwitchExpr({Expr(switchExpr.Selector)}, [{string.Join("; ", switchExpr.Cases.Select(c => $"{(c.IsPattern ? "CaseP" : "Case")}({Expr(c.Match)}, {ValueBlock(c.Body)})"))}], {ValueBlock(switchExpr.DefaultBody)}, {TypeShort.Of(switchExpr.Type)})",
                BoundSwitchPlaceholderExpression placeholder =>
                    $"Placeholder({TypeShort.Of(placeholder.Type)})",
                BoundCastExpression cast =>
                    $"{(cast.IsSafe ? "SafeCast" : "Cast")}({Expr(cast.Source)}, " +
                    $"{TypeShort.Of(cast.TargetType)})",
                // S8b：smart cast 标记（Type = NarrowedType）
                BoundSmartCastExpression smartCast =>
                    $"SmartCast({Expr(smartCast.Operand)}, {TypeShort.Of(smartCast.NarrowedType)})",
                // S9d：可变参数包（调用点归包/前奏物化打包，S11b）
                BoundVarArgsArgument varArgs => varArgs.IsNamed
                    ? $"KwArgs([{string.Join(", ", varArgs.NamedValues.Select(p => $"{p.Name} = {Expr(p.Value)}"))}])"
                    : $"VarArgs([{string.Join(", ", varArgs.Values.Select(Expr))}])",
                BoundSeqExpression seqExpr =>
                    $"SeqExpr([{string.Join(", ", seqExpr.UsingBindings.Select(Using))}], " +
                    $"{ValueBlock(seqExpr.Body)})",
                // S7f：安全访问（占位叶子打 SafeReceiver；结果类型 P3 定型）
                BoundSafeAccessExpression safeAccess =>
                    $"SafeAccess({Expr(safeAccess.Receiver)}, {Expr(safeAccess.Access)}, " +
                    $"{TypeShort.Of(safeAccess.Type)})",
                BoundSafeAccessReceiverExpression safeReceiver =>
                    $"SafeReceiver({TypeShort.Of(safeReceiver.Type)})",
                BoundNullFallbackExpression nullFallback =>
                    $"NullFallback({Expr(nullFallback.Left)}, {Expr(nullFallback.Right)}, " +
                    $"{TypeShort.Of(nullFallback.Type)})",
                // S8a：is/supers/with（Kind 枚举名即显示名；动态形态目标带
                // dyn 前缀）与 typeOf（类型形态目标带 type 前缀，RT 恒打印）；
                // S11：IsCase 打印 case 符号（§12.3 判别匹配）
                BoundTypeCheckExpression typeCheck =>
                    typeCheck.Kind == BoundTypeCheckKind.IsCase
                        ? $"IsCase({Expr(typeCheck.Operand)}, " +
                            $"{TypeShort.Of(typeCheck.Case!.Owner)}.{typeCheck.Case.Name})"
                        : $"{typeCheck.Kind}({Expr(typeCheck.Operand)}, " +
                            $"{(typeCheck.TargetType != null ? TypeShort.Of(typeCheck.TargetType) : "dyn " + Expr(typeCheck.TargetValue))})",
                BoundTypeOfExpression typeOf =>
                    typeOf.TargetType != null
                        ? $"TypeOf(type {TypeShort.Of(typeOf.TargetType)}, {TypeShort.Of(typeOf.Type)})"
                        : $"TypeOf({Expr(typeOf.Operand)}, {TypeShort.Of(typeOf.Type)})",
                BoundPlaceOfExpression placeOf =>
                    $"PlaceOf({Expr(placeOf.Operand)}, {(placeOf.Storage == null ? "object" : "cell")})",
                _ => $"<{expr.GetType().Name}>",
            };
        }

        // 字面量：值经 Syntax 回指取，类型取定型结果
        private static string Literal(BoundLiteralExpression literal)
        {
            var type = TypeShort.Of(literal.Type);
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
    }
}
