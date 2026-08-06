using LatteCompiler.Bil;

namespace LatteCompiler
{
    // 类别分派器（P4b，M55 visitor 化协议）：Lowered 节点 → 结构 visitor
    // 的唯一 switch 所在（对应旧 EmitSession 的 EmitStatement/EmitValue
    // 分派）。遇未覆盖节点：报 P4 Error——语句继续后续发射（无产物），
    // 值返回 "<error>" 占位操作数（同旧 default 分支行为）。
    internal static class EmitStatementDispatcher
    {
        public static Unit Visit(LoweredStatement statement, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            switch (statement)
            {
                case LoweredBlock:
                    return EmitBlockVisitor.Visit(statement, target, ctx, env);
                case LoweredLocalDeclarationStatement:
                    return LocalDeclarationEmitter.Visit(statement, target, ctx, env);
                case LoweredAssignmentStatement:
                    return AssignmentEmitter.Visit(statement, target, ctx, env);
                case LoweredExpressionStatement:
                    return ExpressionStatementEmitter.Visit(statement, target, ctx, env);
                case LoweredCallStatement:
                    return CallStatementEmitter.Visit(statement, target, ctx, env);
                case LoweredReturnStatement:
                    return ReturnEmitter.Visit(statement, target, ctx, env);
                case LoweredIfStatement:
                    return IfEmitter.Visit(statement, target, ctx, env);
                case LoweredLoop:
                    return LoopEmitter.Visit(statement, target, ctx, env);
                case LoweredLoopControl:
                    return LoopControlEmitter.Visit(statement, target, ctx, env);
                case LoweredSwitch:
                    return SwitchEmitter.Visit(statement, target, ctx, env);
                case LoweredThrowStatement:
                    return ThrowEmitter.Visit(statement, target, ctx, env);
                case LoweredSeqBlock:
                    return SeqBlockEmitter.Visit(statement, target, ctx, env);
                case LoweredTryStatement:
                    return TryEmitter.Visit(statement, target, ctx, env);
                default:
                    env.Error(statement.Origin.Syntax.Span,
                        $"P4: lowered statement kind not supported by minimal emission: " +
                        statement.GetType().Name);
                    return Unit.Value;
            }
        }
    }

    internal static class EmitValueDispatcher
    {
        // 表达式物化为变量操作数（§10.1），M57 起返回 BilVariableOperand
        public static BilVariableOperand Visit(LoweredExpression expression, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            switch (expression)
            {
                case LoweredLiteralExpression:
                    return LiteralEmitter.Visit(expression, target, ctx, env);
                case LoweredConstantExpression:
                    return ConstantEmitter.Visit(expression, target, ctx, env);
                case LoweredValueReferenceExpression:
                    return ValueReferenceEmitter.Visit(expression, target, ctx, env);
                case LoweredFieldReferenceExpression:
                    return FieldReferenceEmitter.Visit(expression, target, ctx, env);
                case LoweredBinaryExpression:
                    return BinaryEmitter.Visit(expression, target, ctx, env);
                case LoweredUnaryExpression:
                    return UnaryEmitter.Visit(expression, target, ctx, env);
                case LoweredCallExpression:
                    return CallExpressionEmitter.Visit(expression, target, ctx, env);
                case LoweredNewExpression:
                    return NewExpressionEmitter.Visit(expression, target, ctx, env);
                case LoweredEnumCaseExpression:
                    return EnumCaseEmitter.Visit(expression, target, ctx, env);
                case LoweredThisExpression:
                    return ThisEmitter.Visit(expression, target, ctx, env);
                case LoweredInstanceCallExpression:
                    return InstanceCallEmitter.Visit(expression, target, ctx, env);
                case LoweredFieldAccessExpression:
                    return FieldAccessEmitter.Visit(expression, target, ctx, env);
                case LoweredGetWrapperExpression:
                    return GetWrapperEmitter.Visit(expression, target, ctx, env);
                case LoweredGetFieldWrapperExpression:
                    return GetFieldWrapperEmitter.Visit(expression, target, ctx, env);
                case LoweredEmbeddedFieldExpression:
                    return EmbeddedFieldEmitter.Visit(expression, target, ctx, env);
                case LoweredGetSelfExpression:
                    return GetSelfEmitter.Visit(expression, target, ctx, env);
                case LoweredCallInnerExpression:
                    return CallInnerEmitter.Visit(expression, target, ctx, env);
                case LoweredIndexExpression:
                    return IndexAccessEmitter.Visit(expression, target, ctx, env);
                case LoweredCastExpression:
                    return CastEmitter.Visit(expression, target, ctx, env);
                case LoweredTypeCheckExpression:
                    return TypeCheckEmitter.Visit(expression, target, ctx, env);
                case LoweredTypeOfExpression:
                    return TypeOfEmitter.Visit(expression, target, ctx, env);
                case LoweredVarArgsArgument:
                    return VarArgsEmitter.Visit(expression, target, ctx, env);
                default:
                    env.Error(expression.Origin.Syntax.Span,
                        $"P4: lowered expression kind not supported by minimal emission: " +
                        expression.GetType().Name);
                    return BilOp.Var("<error>");
            }
        }
    }

    // 块语句平铺（嵌套块无独立 BIL 结构，共享函数的参数与局部变量）
    internal sealed class EmitBlockVisitor : EmitVisitor<EmitBlockVisitor, Unit>
    {
        protected override Unit VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var block = (LoweredBlock)node;
            foreach (var statement in block.Statements)
            {
                EmitStatementDispatcher.Visit(statement, target, ctx, env);
            }
            return Unit.Value;
        }
    }
}
