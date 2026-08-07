namespace LatteCompiler
{
    internal static class LambdaFacility
    {
        public static bool CheckUnsupportedConsumer(BoundExpression expression, ASTNode syntax,
            BindEnvironment env)
        {
            if (expression is not BoundLambdaExpression) return false;
            env.Error(syntax.Span,
                "S13 P4 pending: lambda value consumption is limited to local inference " +
                "and call arguments");
            return true;
        }
    }

    // 普通 lambda 的 S13 Slice A 绑定：只产生可推断的匿名 callable 类型，
    // closure ABI/运行时对象仍明确留给 S13 P4。
    internal sealed class LambdaVisitor : ExpressionVisitor<LambdaVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            var lambda = (LambdaExpressionASTNode)node;
            var parameterSymbols = new List<ParameterSymbol>();
            var lambdaMethod = env.NewSyntheticLambdaMethod(env.Unit.Symbols.ErrorType);

            foreach (var parameter in lambda.Parameters.Parameters)
            {
                var type = TypeReferences.Resolve(parameter.Type, parameter.Span ?? lambda.Span,
                    ctx.Frame, env);
                if (type == null) type = env.Unit.Symbols.ErrorType;
                if (type is not TypeSymbol && type is not GenericParameterSymbol)
                {
                    env.Error(parameter.Span ?? lambda.Span,
                        $"Lambda parameter '{parameter.Name}' requires a type");
                    type = env.Unit.Symbols.ErrorType;
                }
                var symbol = new ParameterSymbol(parameter.Name, type,
                    isVariadic: parameter.IsVariadic,
                    isNamedVariadic: parameter.IsNamedVariadic);
                parameterSymbols.Add(symbol);
                lambdaMethod.Parameters.Add(symbol);
            }

            var returnType = TypeReferences.Resolve(lambda.ReturnType, lambda.ReturnType.Span ?? lambda.Span,
                ctx.Frame, env);
            if (returnType == null) returnType = env.Unit.Symbols.ErrorType;
            lambdaMethod.ReturnType = returnType;

            var lambdaCtx = new BindContext(lambdaMethod, ctx.Frame.FileCtx,
                ctx.Frame.DeclaringType, isLambda: true, thisSymbol: ctx.This);
            lambdaCtx.Flow.InheritAssignedFrom(ctx.Flow);
            var lambdaScope = new Scope(scope);
            foreach (var outerParameter in ctx.Frame.Method.Parameters)
                lambdaScope.Declare(outerParameter);
            foreach (var parameter in parameterSymbols)
            {
                lambdaScope.Declare(parameter);
                lambdaCtx.LambdaParameters.Add(parameter);
            }

            BoundExpression? expressionBody = null;
            BoundBlock? blockBody = null;
            if (lambda.Body != null)
            {
                expressionBody = ExpressionDispatcher.Visit(lambda.Body.Expression, lambdaScope,
                    lambdaCtx, env, returnType as TypeSymbol);
                CheckReturnType(expressionBody, returnType, lambda, env);
            }
            else if (lambda.BlockBody != null)
            {
                var shell = new ValueBlockShell(new BoundValueBlock(lambda.BlockBody, lambda.Label ?? "_"),
                    "lambda expression", allowImplicitValue: false);
                ValueBlockVisitor.VisitInto(lambda.BlockBody, lambdaScope, shell, lambdaCtx, env);
                blockBody = shell.Block.Block;
                if (shell.Block.ValueType != null)
                    CheckReturnType(shell.Block.ValueType, returnType, lambda, env);
                else if (!BoundAnalysis.GuaranteesValueReturn(blockBody))
                    env.Error(lambda.Span,
                        "All code paths of a lambda expression must explicitly return@ a value");
            }
            else
            {
                env.Error(lambda.Span, "Lambda expression requires a body");
            }

            if (lambda.IsAsync)
            {
                AsyncGates.CheckLambdaSignature(parameterSymbols, returnType, lambda, env);
                AsyncGates.CheckLambdaCaptures(lambdaCtx.CapturedSymbols, lambda, env);
            }

            // 内层 lambda 的捕获是外层 lambda 的传递捕获；lambda 自身参数和
            // 体内局部只属于内层上下文，不能沿此边界泄漏为外捕获。
            if (ctx.IsLambda)
            {
                foreach (var captured in lambdaCtx.CapturedSymbols)
                {
                    if (captured is ParameterSymbol parameter
                        && lambdaCtx.LambdaParameters.Contains(parameter)) continue;
                    ctx.CapturedSymbols.Add(captured);
                }
            }
            var syntheticBlock = expressionBody != null
                ? new BoundBlock(lambda, new BoundStatement[]
                {
                    new BoundReturnStatement(lambda, expressionBody)
                })
                : blockBody ?? new BoundBlock(lambda, Array.Empty<BoundStatement>());
            var syntheticBody = new BoundFunctionBody(lambdaMethod, lambdaCtx.Locals.ToList(),
                syntheticBlock);
            var result = new BoundLambdaExpression(lambda, lambda.Parameters.Parameters, expressionBody,
                blockBody, returnType, lambdaCtx.CapturedSymbols, parameterSymbols,
                lambdaMethod, syntheticBody);
            env.SyntheticLambdas.Add(result);
            return result;
        }

        private static void CheckReturnType(BoundExpression? expression, SemanticSymbol returnType,
            LambdaExpressionASTNode lambda, BindEnvironment env)
        {
            if (expression != null) CheckReturnType(expression.Type, returnType, lambda, env);
        }

        private static void CheckReturnType(SemanticSymbol? actual, SemanticSymbol expected,
            LambdaExpressionASTNode lambda, BindEnvironment env)
        {
            if (actual == null || actual is ErrorTypeSymbol || expected is ErrorTypeSymbol) return;
            if (expected is TypeSymbol expectedType
                && !SymbolLookup.IsAssignable(actual, expectedType, env))
            {
                env.Error(lambda.ReturnType.Span ?? lambda.Span,
                    $"Lambda result '{BoundAnalysis.TypeDisplay(actual)}' is not assignable to " +
                    $"'{BoundAnalysis.TypeDisplay(expected)}'");
            }
        }
    }
}
