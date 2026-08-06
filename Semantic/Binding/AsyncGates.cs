using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler
{
    // async 边界闸门 P3 侧（SYNTAX §4.5，S8f；仅分析侧，无 P4 面）：
    // async 调用把一批值从当前协程送进新协程，跨界类型必须共享安全
    // （shared class / shared rich struct / wrapper、非 rich ValueType、
    // T 共享安全的 Nullable\<T>，TypeSymbol.IsSharedSafe）。
    //
    //   - CheckFunctionBody：调用点闸门 1（receiver）/ 2（参数实际类型）/
    //     5（泛型实参实际类型，S9f 落地——S9d-2 泛型可变包的推导类型经
    //     GenericPack 槽同查）——函数体绑完后对 BoundTree 后置遍历（单一
    //     落点覆盖全部调用形态：全局/静态调用、实例调用、语句 void 调用；
    //     BoundCallStatement 是 void 调用的语句形态，同查）。闸门 2 对
    //     可变参数包按 §4.5 判定「展开后的每一个实参类型」——包自身
    //     的 Array\<Any\> 打包形态不参与判定。
    //   - CheckLambdaCaptures：闸门 4（async lambda 捕获变量）——lambda
    //     绑定归 S13，此处做 AST 级捕获扫描（详见方法注释）。
    // 声明侧闸门 2/3/5 在 P2 AsyncGateChecker（"在 async 声明处检查
    // 2、3、5 的声明类型"）；"在 async 调用点检查 1、2、5 的实际类型"
    // 中 1、2、5 落本文件。
    internal static class AsyncGates
    {
        public static void CheckFunctionBody(BoundBlock body, BindEnvironment env)
        {
            foreach (var statement in body.Statements)
            {
                WalkStatement(statement, env);
            }
        }

        // ===== 调用点闸门 1/2：async 调用（含实例 receiver）=====

        private static void WalkStatement(BoundStatement statement, BindEnvironment env)
        {
            switch (statement)
            {
                case BoundBlock block:
                    foreach (var s in block.Statements) WalkStatement(s, env);
                    break;
                case BoundLocalDeclarationStatement local:
                    if (local.Initializer != null) WalkExpression(local.Initializer, env);
                    break;
                case BoundDestructuringDeclarationStatement destructuring:
                    WalkExpression(destructuring.Initializer, env);
                    break;
                case BoundExpressionStatement expression:
                    WalkExpression(expression.Expression, env);
                    break;
                case BoundCallStatement call:
                    CheckAsyncCall(call.Method, call.Receiver, call.Arguments, call.TypeArguments,
                        call.GenericPack, statement.Syntax, env);
                    foreach (var argument in call.Arguments) WalkExpression(argument, env);
                    if (call.Receiver != null) WalkExpression(call.Receiver, env);
                    break;
                case BoundAssignmentStatement assignment:
                    WalkExpression(assignment.Target, env);
                    WalkExpression(assignment.Value, env);
                    break;
                case BoundReturnStatement returnStatement:
                    if (returnStatement.Value != null) WalkExpression(returnStatement.Value, env);
                    break;
                case BoundIfStatement ifStatement:
                    WalkExpression(ifStatement.Condition, env);
                    WalkBlock(ifStatement.TrueBlock, env);
                    if (ifStatement.FalseBlock != null) WalkBlock(ifStatement.FalseBlock, env);
                    break;
                case BoundReturnValueStatement returnValue:
                    WalkExpression(returnValue.Value, env);
                    break;
                case BoundLoop loop:
                    if (loop.Condition != null) WalkExpression(loop.Condition, env);
                    if (loop.Iterable != null) WalkExpression(loop.Iterable, env);
                    WalkBlock(loop.Body, env);
                    break;
                case BoundSwitchStatement switchStatement:
                    WalkExpression(switchStatement.Selector, env);
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        WalkExpression(switchCase.Match, env);
                        WalkBlock(switchCase.Body, env);
                    }
                    WalkBlock(switchStatement.DefaultBody, env);
                    break;
                case BoundThrowStatement throwStatement:
                    WalkExpression(throwStatement.Exception, env);
                    break;
                case BoundTryStatement tryStatement:
                    WalkBlock(tryStatement.TryBlock, env);
                    foreach (var catchClause in tryStatement.Catches)
                    {
                        WalkBlock(catchClause.Body, env);
                    }
                    if (tryStatement.FinallyBlock != null)
                    {
                        WalkBlock(tryStatement.FinallyBlock, env);
                    }
                    break;
                case BoundSeqStatement seqStatement:
                    WalkBlock(seqStatement.Body, env);
                    break;
                case BoundLoopControl:
                case BoundSeqExitStatement:
                    break;
                default:
                    throw new CompilerInternalException(
                        "async 闸门遍历遇未知 Bound 语句节点: " + statement.GetType().Name);
            }
        }

        private static void WalkBlock(BoundBlock block, BindEnvironment env)
        {
            foreach (var statement in block.Statements)
            {
                WalkStatement(statement, env);
            }
        }

        private static void WalkExpression(BoundExpression expression, BindEnvironment env)
        {
            switch (expression)
            {
                case BoundCallExpression call:
                    CheckAsyncCall(call.Method, null, call.Arguments, call.TypeArguments,
                        call.GenericPack, expression.Syntax, env);
                    foreach (var argument in call.Arguments) WalkExpression(argument, env);
                    break;
                case BoundInstanceCallExpression instanceCall:
                    CheckAsyncCall(instanceCall.Method, instanceCall.Receiver,
                        instanceCall.Arguments, instanceCall.TypeArguments,
                        instanceCall.GenericPack, expression.Syntax, env);
                    WalkExpression(instanceCall.Receiver, env);
                    foreach (var argument in instanceCall.Arguments) WalkExpression(argument, env);
                    break;
                case BoundBinaryExpression binary:
                    WalkExpression(binary.Left, env);
                    WalkExpression(binary.Right, env);
                    break;
                case BoundUnaryExpression unary:
                    WalkExpression(unary.Operand, env);
                    break;
                case BoundNewExpression newExpression:
                    foreach (var argument in newExpression.Arguments)
                    {
                        WalkExpression(argument, env);
                    }
                    break;
                case BoundIfExpression ifExpression:
                    WalkExpression(ifExpression.Condition, env);
                    WalkBlock(ifExpression.TrueBranch.Block, env);
                    WalkBlock(ifExpression.FalseBranch.Block, env);
                    break;
                case BoundCompoundAssignmentExpression compound:
                    WalkExpression(compound.Target, env);
                    WalkExpression(compound.Value, env);
                    break;
                case BoundFieldAccessExpression fieldAccess:
                    WalkExpression(fieldAccess.Receiver, env);
                    break;
                case BoundIndexExpression index:
                    WalkExpression(index.Receiver, env);
                    WalkExpression(index.Index, env);
                    break;
                // S11：enum case 构造（洞实参递归；case 符号无调用语义，
                // 闸门无涉）
                case BoundEnumCaseExpression enumCase:
                    foreach (var argument in enumCase.Arguments) WalkExpression(argument, env);
                    break;
                // S11：wrapper place（只读 place 无调用语义，receiver 递归）
                case BoundWrapperAccessExpression wrapperAccess:
                    WalkExpression(wrapperAccess.Receiver, env);
                    break;
                case BoundSwitchExpression switchExpression:
                    WalkExpression(switchExpression.Selector, env);
                    foreach (var switchCase in switchExpression.Cases)
                    {
                        WalkExpression(switchCase.Match, env);
                        WalkBlock(switchCase.Body.Block, env);
                    }
                    WalkBlock(switchExpression.DefaultBody.Block, env);
                    break;
                case BoundSwitchPlaceholderExpression placeholder:
                    WalkExpression(placeholder.Selector, env);
                    break;
                case BoundCastExpression cast:
                    WalkExpression(cast.Source, env);
                    break;
                case BoundSmartCastExpression smartCast:
                    WalkExpression(smartCast.Operand, env);
                    break;
                case BoundSeqExpression seqExpression:
                    WalkBlock(seqExpression.Body.Block, env);
                    break;
                case BoundSafeAccessExpression safeAccess:
                    WalkExpression(safeAccess.Receiver, env);
                    WalkExpression(safeAccess.Access, env);
                    break;
                case BoundNullFallbackExpression nullFallback:
                    WalkExpression(nullFallback.Left, env);
                    WalkExpression(nullFallback.Right, env);
                    break;
                case BoundTypeCheckExpression typeCheck:
                    WalkExpression(typeCheck.Operand, env);
                    if (typeCheck.TargetValue != null)
                    {
                        WalkExpression(typeCheck.TargetValue, env);
                    }
                    break;
                case BoundTypeOfExpression typeOf:
                    if (typeOf.Operand != null) WalkExpression(typeOf.Operand, env);
                    break;
                case BoundVarArgsArgument varArgs:
                    foreach (var value in varArgs.Values) WalkExpression(value, env);
                    foreach (var (_, value) in varArgs.NamedValues) WalkExpression(value, env);
                    break;
                case BoundLiteralExpression:
                case BoundValueReferenceExpression:
                case BoundFieldReferenceExpression:
                case BoundThisExpression:
                case BoundSafeAccessReceiverExpression:
                    break;
                default:
                    throw new CompilerInternalException(
                        "async 闸门遍历遇未知 Bound 表达式节点: " + expression.GetType().Name);
            }
        }

        // 闸门 1 + 2 + 5：async 方法调用点的 receiver、全部实际实参类型与
        // 泛型实参（S9f 解开 #23④：M69 后 BoundCall 携带 TypeArguments，
        // 闸门 5 调用点检查落地——泛型实参的 typeid 与实际值一同跨边界；
        // S9d-2 泛型可变包的推导类型不进 TypeArguments，由 GenericPack 槽
        // 承载，本方法一并判定）
        private static void CheckAsyncCall(MethodSymbol method, BoundExpression? receiver,
            IReadOnlyList<BoundExpression> arguments, IReadOnlyList<SemanticSymbol> typeArguments,
            BoundGenericVarArgsArgument? genericPack, ASTNode syntax, BindEnvironment env)
        {
            if (!method.IsAsync) return;
            // S9a：泛型参数类型判型后跳过（实参实际类型检查归 S9f 闸门 5）
            if (receiver != null && receiver.Type is not ErrorTypeSymbol
                && receiver.Type is TypeSymbol receiverType && !receiverType.IsSharedSafe())
            {
                env.Error(syntax.Span,
                    $"async call receiver must be a shared-safe type: " +
                    $"'{BoundAnalysis.TypeDisplay(receiver.Type)}'");
            }
            foreach (var argument in arguments)
            {
                // 可变参数包（S9d，规范参数序末元素）：闸门 2 按 SYNTAX §4.5
                // 判定「可变参数展开后的每一个实参类型」——包自身定型为
                // Array\<Any\>（BIL 传参打包形态，非跨界值形态），整体判定
                // 会对一切带包调用（含空包）误报
                if (argument is BoundVarArgsArgument pack)
                {
                    IEnumerable<BoundExpression> elements = pack.IsNamed
                        ? pack.NamedValues.Select(v => v.Value)
                        : pack.Values;
                    foreach (var element in elements)
                    {
                        CheckArgumentSharedSafe(element, method, syntax, env);
                    }
                    continue;
                }
                CheckArgumentSharedSafe(argument, method, syntax, env);
            }
            // 闸门 5：泛型实参实际类型共享安全（泛型参数自身/ErrorType
            // 毒化跳过——泛型参数的实际类型由调用点实参约束）
            foreach (var typeArgument in typeArguments)
            {
                CheckTypeArgumentSharedSafe(typeArgument, method, syntax, env);
            }
            // 闸门 5 续：泛型可变包的推导类型实参（S9d-2——由值实参静态
            // 类型推导，永不显式书写，不进 TypeArguments）
            if (genericPack != null)
            {
                IEnumerable<SemanticSymbol> derivedTypes = genericPack.IsNamed
                    ? genericPack.NamedTypes.Select(t => t.Type)
                    : genericPack.TypeArguments;
                foreach (var derivedType in derivedTypes)
                {
                    CheckTypeArgumentSharedSafe(derivedType, method, syntax, env);
                }
            }
        }

        // 单实参闸门 2 判定：实际类型须共享安全（泛型参数类型判型后不
        // 可判，按非共享安全拒绝；ErrorType 毒化静默）
        private static void CheckArgumentSharedSafe(BoundExpression argument, MethodSymbol method,
            ASTNode syntax, BindEnvironment env)
        {
            if (argument.Type is ErrorTypeSymbol) return;
            if (argument.Type is not TypeSymbol argumentType
                || !argumentType.IsSharedSafe())
            {
                env.Error(syntax.Span,
                    $"argument of async function '{method.Name}' must be a " +
                    $"shared-safe type: '{BoundAnalysis.TypeDisplay(argument.Type)}'");
            }
        }

        // 单泛型实参闸门 5 判定：实际类型须共享安全
        private static void CheckTypeArgumentSharedSafe(SemanticSymbol typeArgument,
            MethodSymbol method, ASTNode syntax, BindEnvironment env)
        {
            if (typeArgument is not TypeSymbol typeArgumentType
                || typeArgumentType is ErrorTypeSymbol) return;
            if (!typeArgumentType.IsSharedSafe())
            {
                env.Error(syntax.Span,
                    $"type argument of async function '{method.Name}' must be a " +
                    $"shared-safe type: '{BoundAnalysis.TypeDisplay(typeArgument)}'");
            }
        }

        // ===== 闸门 4：async lambda 捕获变量 =====

        // 粗粒度 AST 扫描（lambda 绑定归 S13，无符号级作用域）：收集体内
        // 全部裸路径头名（PathExpressionASTNode.Head.Name），排除 lambda
        // 自身形参与体内任意嵌套深度声明的局部名（嵌套 lambda 形参同排——
        // 内层作用域名不算外层捕获），剩余名经外层词法作用域链解析（局部）
        // 或宿主形参表（参数）；命中即捕获，检查其类型共享安全。
        // 局限（S13 符号级绑定后收紧）：体内局部声明名全量排除——块内
        // 「先引用、后声明」的形态漏判捕获（保守漏报，方向安全）；字段/
        // 方法/全局函数名不是捕获（解析只查局部/参数，自然跳过）
        public static void CheckLambdaCaptures(LambdaExpressionASTNode lambda, Scope scope,
            BindContext ctx, BindEnvironment env)
        {
            var body = (ASTNode?)lambda.BlockBody ?? lambda.Body;
            if (body == null) return;
            var declared = new HashSet<string>();
            foreach (var parameter in lambda.Parameters.Parameters)
            {
                declared.Add(parameter.Name);
            }
            CollectDeclaredNames(body, declared);
            var checkedSymbols = new HashSet<SemanticSymbol>();
            foreach (var name in CollectPathHeadNames(body))
            {
                if (declared.Contains(name)) continue;
                SemanticSymbol? symbol = scope.Lookup(name);
                if (symbol == null)
                {
                    symbol = ctx.Frame.Method.Parameters.FirstOrDefault(p => p.Name == name);
                }
                if (symbol == null || !checkedSymbols.Add(symbol)) continue;
                var type = symbol switch
                {
                    LocalSymbol local => local.Type,
                    ParameterSymbol parameter => parameter.Type,
                    _ => null,
                };
                // S9a：泛型参数类型判型后跳过（实际类型检查归 S9f 闸门 5）
                if (type is not TypeSymbol checkedType || checkedType is ErrorTypeSymbol) continue;
                if (!checkedType.IsSharedSafe())
                {
                    env.Error(lambda.Span,
                        $"async lambda captures '{name}' of non-shared-safe type " +
                        $"'{BoundAnalysis.TypeDisplay(type)}'");
                }
            }
        }

        // 收集体内任意嵌套深度的局部声明名与嵌套 lambda 形参名（AST 遍历）
        private static void CollectDeclaredNames(ASTNode node, HashSet<string> declared)
        {
            switch (node)
            {
                case VariableDeclarationASTNode variable:
                    declared.Add(variable.Name);
                    break;
                case LambdaExpressionASTNode nestedLambda:
                    foreach (var parameter in nestedLambda.Parameters.Parameters)
                    {
                        declared.Add(parameter.Name);
                    }
                    break;
            }
            foreach (var (child, _) in AstStructureReflection.EnumerateChildren(node))
            {
                CollectDeclaredNames(child, declared);
            }
        }

        // 收集体内全部裸路径头名（PathExpressionASTNode.Head.Name；AST 遍历）
        private static IEnumerable<string> CollectPathHeadNames(ASTNode node)
        {
            if (node is PathExpressionASTNode path && path.Head.Name != null)
            {
                yield return path.Head.Name;
            }
            foreach (var (child, _) in AstStructureReflection.EnumerateChildren(node))
            {
                foreach (var name in CollectPathHeadNames(child))
                {
                    yield return name;
                }
            }
        }
    }
}
