using System.Linq;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// 统一 LoweredTree 描述器（S7a）：P4a 测试共用的唯一描述器，仿 BoundDescribe。
    /// 字面量值经 Origin 链回指取（LoweredNode.Origin → BoundNode.Syntax →
    /// LiteralExpressionASTNode.Literal），定型类型透传 Origin（Type => Bound.Type），
    /// 以短名显示（Nullable\<T\> 显示为 T?）。
    ///
    /// 格式约定（表达式）：
    ///   Int(42,i32)  Float(3.14,double)  Str("...",String)  Char('A',char)  Bool(True,bool)  Null(String?)
    ///   Local(x,i32)  Param(a,i32)  Field(g,i32)  Const(True,bool)  Const(null,T?)（P4a 合成常量）
    ///   Binary(Add, l, r, i32)  Unary(Opposite, x, i32)
    ///   Call(name, [args], ret)  New(T, [args])  New(T, init, [args])
    ///   This(C)（S7c-2）  InstCall(name, receiver, [args], ret)  InstField(f, receiver, T)
    ///   Index(recv, idx, T)（S8c，读/写共用——写形态只作 Assign 目标）
    ///   Cast(e, T, RT)  SafeCast(e, T, RT)（S7e，as / as?；T = 目标类型，RT = 结果类型）
    ///   Is(e, T)  Supers(e, T)  With(e, T)（S8a；动态形态目标带 dyn 前缀：
    ///   Is(e, dyn t)；结果恒 bool 不打印）
    ///   TypeOf(e, RT)（值形态）  TypeOf(type T, RT)（类型形态）（S8a；RT = Type\<T\>）
    ///   IsCase(e, RequestResult.Failed)（S11；§12.3 判别匹配）
    ///   EnumCase(RequestResult.Success, [])  EnumCase(RequestResult.Failed, [args])（S11）
    /// 格式约定（语句）：
    ///   Decl(x, i32, = init)  ExprStmt(e)  CallStmt(name, [args])  Assign(t, v)  Return(v)  Return
    ///   If(c, [真], [假], .bN)  If(c, [真], .bN)
    ///   Loop([judge], .s0, [body], .b0)（do-while 带 rev 标记：Loop(rev, ...)）
    ///   Break(.b0)  Continue(.b0)（breakid 取目标循环/region 的合成 .breakid 局部名）
    ///   StructuredExit(@label, v?)（Stage B：return@ 标记，routing 后不得残留）
    ///   InstCallStmt(name, receiver, [args])（void 实例调用语句，S7c-2）
    ///   Switch(sel, [Case(v, [体]); ...], [default], .b0)（S7d，全值匹配形态）
    ///   Throw(e)（S7d）
    ///   Try([try], [Catch(e, T, [体]); Catch(T, [体])], Finally([体]), slot, .bN)（S7e；
    ///   无变量 catch 省变量名，无 finally 省第三参；slot 恒显式——合成 .sN 或
    ///   finally 变量名；breakid 为 §16.5 推广的合成 .breakid 局部，恒显式）
    ///   Seq([体], .bN)  SeqVolatile([体], .bN)（S7e 两形态汇合）
    ///   块：[s1; s2]；函数体：Body(name, [x: i32, ...], [块])
    /// </summary>
    public static class LoweredDescribe
    {
        public static string Body(LoweredFunctionBody body)
        {
            var locals = string.Join(", ", body.Locals.Select(l => $"{l.Name}: {TypeShort.Of(l.Type)}"));
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
                    $"Decl({decl.Local.Name}, {TypeShort.Of(decl.Local.Type)}" +
                    $"{(decl.Initializer != null ? $", = {Expr(decl.Initializer)}" : "")})",
                LoweredExpressionStatement exprStmt => $"ExprStmt({Expr(exprStmt.Expression)})",
                LoweredYieldStatement yield => yield.Alarm == null
                    ? "Yield" : $"Yield({Expr(yield.Alarm)})",
                LoweredCallStatement call => call.Receiver == null
                    ? $"CallStmt({call.Method.Name}, [{string.Join(", ", call.Arguments.Select(Expr))}])"
                    : $"InstCallStmt({call.Method.Name}, {Expr(call.Receiver)}, " +
                        $"[{string.Join(", ", call.Arguments.Select(Expr))}])",
                LoweredAssignmentStatement assign => $"Assign({Expr(assign.Target)}, {Expr(assign.Value)})",
                LoweredReturnStatement ret =>
                    ret.Value != null ? $"Return({Expr(ret.Value)})" : "Return",
                LoweredIfStatement ifStmt => ifStmt.FalseBlock != null
                    ? $"If({Expr(ifStmt.Condition)}, {Block(ifStmt.TrueBlock)}, " +
                        $"{Block(ifStmt.FalseBlock)}, {ifStmt.BreakId.Name})"
                    : $"If({Expr(ifStmt.Condition)}, {Block(ifStmt.TrueBlock)}, " +
                        $"{ifStmt.BreakId.Name})",
                LoweredLoop loop =>
                    $"Loop({(loop.IsRev ? "rev, " : "")}{Block(loop.Judge)}, " +
                    $"{loop.Condition.Name}, {Block(loop.Body)}, {loop.BreakId.Name})",
                LoweredBreakStatement breakStatement => $"Break({breakStatement.BreakId.Name})",
                LoweredContinueStatement continueStatement =>
                    $"Continue({continueStatement.BreakId.Name})",
                LoweredSwitch switchStmt =>
                    $"Switch({Expr(switchStmt.Selector)}, [{string.Join("; ", switchStmt.Cases.Select(c => $"Case({Expr(c.Value)}, {Block(c.Body)})"))}], {Block(switchStmt.DefaultBody)}, {switchStmt.BreakId.Name})",
                LoweredThrowStatement throwStmt => $"Throw({Expr(throwStmt.Exception)})",
                LoweredTryStatement tryStmt => Try(tryStmt),
                LoweredSeqBlock seqBlock =>
                    $"{(seqBlock.IsVolatile ? "SeqVolatile" : "Seq")}({Block(seqBlock.Body)}, " +
                    $"{seqBlock.BreakId.Name})",
                // Stage B：source-level exit 标记（调试兜底——
                // StructuredExitRouting pass 后不得残留；硬不变量测试
                // 断言快照全文不含本串）
                LoweredStructuredExit exit => $"StructuredExit(@{exit.Target switch
                {
                    BoundValueBlock valueBlock => valueBlock.Label,
                    BoundSeqStatement seq => seq.Label ?? "<unnamed>",
                    _ => "<unknown>",
                }}{(exit.Value != null ? ", " + Expr(exit.Value) : "")})",
                LoweredNewWrapperStatement nw =>
                    $"NewWrapper({nw.Kind}, {nw.WrapperType.Name}" +
                    $"{(nw.Target != null ? ", " + nw.Target.Name : "")}, " +
                    $"[{string.Join(", ", nw.Arguments.Select(Expr))}])",
                _ => $"<{stmt.GetType().Name}>",
            };
        }

        // try：Try([try], [Catch(e, T, [体]); Catch(T, [体])], Finally([体]), slot, .bN)；
        // 无变量 catch 省变量名，无 finally 省第三参，slot/breakid 恒显式（S7e）
        private static string Try(LoweredTryStatement tryStmt)
        {
            var catches = string.Join("; ", tryStmt.Catches.Select(c =>
                c.Variable != null
                    ? $"Catch({c.Variable.Name}, {TypeShort.Of(c.ExceptionType)}, {Block(c.Body)})"
                    : $"Catch({TypeShort.Of(c.ExceptionType)}, {Block(c.Body)})"));
            var finallyPart = tryStmt.FinallyBlock != null
                ? $", Finally({Block(tryStmt.FinallyBlock)})"
                : "";
            return $"Try({Block(tryStmt.TryBlock)}, [{catches}]{finallyPart}, " +
                $"{tryStmt.ExceptionSlot.Name}, {tryStmt.BreakId.Name})";
        }

        public static string Expr(LoweredExpression? expr)
        {
            return expr switch
            {
                null => "<null>",
                LoweredLiteralExpression literal => Literal(literal),
                // P4a 合成常量（S7b bool；S7f null——安全访问/空值回退脱糖产物；
                // Stage B int——StructuredExitRouting 的 route tag / 0 初始化）
                LoweredConstantExpression constant => constant.Value switch
                {
                    bool b => $"Const({b},{TypeShort.Of(constant.Type)})",
                    int i => $"Const({i},{TypeShort.Of(constant.Type)})",
                    null => $"Const(null,{TypeShort.Of(constant.Type)})",
                    var other => $"<Const {other}>",
                },
                LoweredValueReferenceExpression valueRef => valueRef.Symbol switch
                {
                    LocalSymbol local => $"Local({local.Name},{TypeShort.Of(valueRef.Type)})",
                    ParameterSymbol param => $"Param({param.Name},{TypeShort.Of(valueRef.Type)})",
                    _ => $"<{valueRef.Symbol.GetType().Name}>",
                },
                LoweredFieldReferenceExpression fieldRef =>
                    $"Field({fieldRef.Field.Name},{TypeShort.Of(fieldRef.Type)})",
                LoweredBinaryExpression binary =>
                    $"Binary({binary.Op}, {Expr(binary.Left)}, {Expr(binary.Right)}, {TypeShort.Of(binary.Type)})",
                LoweredUnaryExpression unary =>
                    $"Unary({unary.Op}, {Expr(unary.Operand)}, {TypeShort.Of(unary.Type)})",
                LoweredAwaitExpression awaitExpression =>
                    $"Await({Expr(awaitExpression.Operand)}, " +
                    $"{(awaitExpression.HasResult ? TypeShort.Of(awaitExpression.ResultType!) : "void")})",
                LoweredCallExpression call =>
                    $"Call({call.Method.Name}, [{string.Join(", ", call.Arguments.Select(Expr))}], " +
                    $"{TypeShort.Of(call.Type)})",
                LoweredNewExpression newExpr =>
                    $"New({TypeShort.Of(newExpr.Type)}{(newExpr.Init != null ? ", init" : "")}, " +
                    $"[{string.Join(", ", newExpr.Arguments.Select(Expr))}]" +
                    $"{(newExpr.WrapperArguments != null ?
                        ", wrapped=[" + string.Join(", ", newExpr.WrapperArguments.Select(Expr)) + "]" : "")})",
                // SYNTAX §5.2：cell 对象引用（捕获局部/参数的 cell 变量本身）
                LoweredCellReferenceExpression cellRef =>
                    $"CellRef({cellRef.Symbol.Name},{TypeShort.Of(cellRef.Type)})",
                LoweredThisExpression => $"This({TypeShort.Of(expr.Type)})",
                LoweredInstanceCallExpression instCall =>
                    $"InstCall({instCall.Method.Name}, {Expr(instCall.Receiver)}, " +
                    $"[{string.Join(", ", instCall.Arguments.Select(Expr))}], " +
                    $"{TypeShort.Of(instCall.Type)})",
                LoweredFieldAccessExpression fieldAccess =>
                    $"InstField({fieldAccess.Field.Name}, {Expr(fieldAccess.Receiver)}, " +
                    $"{TypeShort.Of(fieldAccess.Type)})",
                // S11c：wrapper 值拷贝与 set.wrapper.field 写 place
                LoweredGetWrapperExpression getWrapper =>
                    $"GetWrapper({Expr(getWrapper.Source)}, {TypeShort.Of(getWrapper.Wrapper)})",
                LoweredGetFieldWrapperExpression getFieldWrapper =>
                    $"GetFieldWrapper({Expr(getFieldWrapper.Object)}, {getFieldWrapper.HostField.Name}, " +
                    $"{TypeShort.Of(getFieldWrapper.Wrapper)})",
                LoweredWrapperFieldExpression wrapperField =>
                    $"WrapperField({Expr(wrapperField.Receiver)}, " +
                    $"[{string.Join(" > ", wrapperField.PlaceChain.Select(DescribePlaceElement))}], " +
                    $"{wrapperField.Field.Name}, {TypeShort.Of(wrapperField.Type)})",
                LoweredGetSelfExpression getSelf =>
                    $"GetSelf({TypeShort.Of(getSelf.Type)})",
                LoweredCallInnerExpression callInner =>
                    $"CallInner({string.Join(", ", callInner.Arguments.Select(Expr))}" +
                    $"{(callInner.ForwardedGenericPacks.Count == 0 ? ""
                        : ", packs=[" + string.Join(", ",
                            callInner.ForwardedGenericPacks.Select(p => p.Name)) + "]")}" +
                    $"{(callInner.IsVoid ? ", void" : "")}, {TypeShort.Of(callInner.Type)})",
                LoweredSuperCallExpression superCall =>
                    $"SuperCall({superCall.Method.Name}, [{string.Join(", ", superCall.Arguments.Select(Expr))}]" +
                    $"{(superCall.IsVoid ? ", void" : "")}, {TypeShort.Of(superCall.Type)})",
                LoweredIndexExpression indexAccess =>
                    $"Index({Expr(indexAccess.Receiver)}, {Expr(indexAccess.Index)}, " +
                    $"{TypeShort.Of(indexAccess.Type)})",
                LoweredCastExpression cast =>
                    $"{(cast.IsSafe ? "SafeCast" : "Cast")}({Expr(cast.Source)}, " +
                    $"{TypeShort.Of(cast.TargetType)}, {TypeShort.Of(cast.Type)})",
                // S8a：is/supers/with（Kind 枚举名即显示名；动态形态目标带
                // dyn 前缀）与 typeOf（类型形态目标带 type 前缀，RT 恒打印）；
                // S11：IsCase 打印 case 符号（§12.3 判别匹配）
                LoweredTypeCheckExpression typeCheck =>
                    typeCheck.Kind == BoundTypeCheckKind.IsCase
                        ? $"IsCase({Expr(typeCheck.Operand)}, " +
                            $"{TypeShort.Of(typeCheck.Case!.Owner)}.{typeCheck.Case.Name})"
                        : $"{typeCheck.Kind}({Expr(typeCheck.Operand)}, " +
                            $"{(typeCheck.TargetType != null ? TypeShort.Of(typeCheck.TargetType) : "dyn " + Expr(typeCheck.TargetValue))})",
                // S11：enum case 构造（类型恒为宿主 enum，不重复打印）
                LoweredEnumCaseExpression enumCase =>
                    $"EnumCase({TypeShort.Of(enumCase.Case.Owner)}.{enumCase.Case.Name}, " +
                    $"[{string.Join(", ", enumCase.Arguments.Select(Expr))}])",
                LoweredTypeOfExpression typeOf =>
                    typeOf.TargetType != null
                        ? $"TypeOf(type {TypeShort.Of(typeOf.TargetType)}, {TypeShort.Of(typeOf.Type)})"
                        : $"TypeOf({Expr(typeOf.Operand)}, {TypeShort.Of(typeOf.Type)})",
                _ => $"<{expr.GetType().Name}>",
            };
        }

        // PlaceChain 元素：FieldSymbol 用字段名，TypeSymbol 用短类型名
        private static string DescribePlaceElement(SemanticSymbol element) => element switch
        {
            FieldSymbol field => field.Name,
            TypeSymbol type => TypeShort.Of(type),
            _ => element.Name,
        };

        // 字面量：值经 Origin.Syntax 回指取，类型取透传的定型结果
        private static string Literal(LoweredLiteralExpression literal)
        {
            var type = TypeShort.Of(literal.Type);
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
    }
}
