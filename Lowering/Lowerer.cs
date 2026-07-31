using System.Collections.Generic;

namespace LatteCompiler
{
    // P4a 降级重写（SEMANTIC_ARCHITECTURE §6.1）：BoundTree → LoweredTree，
    // 树到树重写。S6 最小闭环为恒等重写——只覆盖 hello world 端到端所需的
    // 五类节点（块/void 调用/return/字面量/值引用）；§6.1 脱糖清单随 S7+
    // 逐项落地为独立 rewriter。
    //
    // 输入是无错 BoundTree（任一前置 pass 结束时有 Error 即不推进，§8）。
    // 遇未覆盖节点：报 P4 Error 诊断并跳过整个函数体（不产出
    // LoweredFunctionBody）——函数体之间诊断互不阻断（同 P3 原则）。
    public static class Lowerer
    {
        public static IReadOnlyList<LoweredFunctionBody> Lower(
            CompilationUnit unit, IReadOnlyList<BoundFunctionBody> bodies)
        {
            var result = new List<LoweredFunctionBody>();
            foreach (var body in bodies)
            {
                var lowered = LowerBody(unit, body);
                if (lowered != null) result.Add(lowered);
            }
            return result;
        }

        private static LoweredFunctionBody? LowerBody(CompilationUnit unit, BoundFunctionBody body)
        {
            var lowered = LowerBlock(unit, body.Body);
            if (lowered == null) return null;
            return new LoweredFunctionBody(body.Method, body.Locals, lowered);
        }

        // null 返回 = 已诊断失败（遇未覆盖节点），调用方放弃整个函数体
        private static LoweredBlock? LowerBlock(CompilationUnit unit, BoundBlock block)
        {
            var statements = new List<LoweredStatement>();
            foreach (var statement in block.Statements)
            {
                var lowered = LowerStatement(unit, statement);
                if (lowered == null) return null;
                statements.Add(lowered);
            }
            return new LoweredBlock(block, statements);
        }

        private static LoweredStatement? LowerStatement(CompilationUnit unit,
            BoundStatement statement)
        {
            switch (statement)
            {
                case BoundBlock block:
                    return LowerBlock(unit, block);
                case BoundCallStatement call:
                    var arguments = LowerArguments(unit, call.Arguments);
                    if (arguments == null) return null;
                    return new LoweredCallStatement(call, call.Method, arguments);
                case BoundReturnStatement ret:
                    LoweredExpression? value = null;
                    if (ret.Value != null)
                    {
                        value = LowerExpression(unit, ret.Value);
                        if (value == null) return null;
                    }
                    return new LoweredReturnStatement(ret, value);
                default:
                    Unsupported(unit, statement);
                    return null;
            }
        }

        private static List<LoweredExpression>? LowerArguments(CompilationUnit unit,
            IReadOnlyList<BoundExpression> arguments)
        {
            var result = new List<LoweredExpression>();
            foreach (var argument in arguments)
            {
                var lowered = LowerExpression(unit, argument);
                if (lowered == null) return null;
                result.Add(lowered);
            }
            return result;
        }

        private static LoweredExpression? LowerExpression(CompilationUnit unit,
            BoundExpression expression)
        {
            switch (expression)
            {
                case BoundLiteralExpression literal:
                    return new LoweredLiteralExpression(literal);
                case BoundValueReferenceExpression valueReference:
                    return new LoweredValueReferenceExpression(valueReference, valueReference.Symbol);
                default:
                    Unsupported(unit, expression);
                    return null;
            }
        }

        private static void Unsupported(CompilationUnit unit, BoundNode node)
        {
            unit.Diagnostics.Error(DiagnosticPhase.P4, node.Syntax.Span,
                $"P4: node kind not supported by minimal lowering (S7): {node.GetType().Name}");
        }
    }
}
