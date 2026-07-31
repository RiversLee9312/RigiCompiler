using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler
{
    // P4a 降级重写（SEMANTIC_ARCHITECTURE §6.1）：BoundTree → LoweredTree，
    // 树到树重写。S6 最小闭环为恒等重写；S7a 补齐覆盖 P3（S5）能产出的
    // 全部 Bound 节点；S7b 落地首批脱糖（§6.1 脱糖清单）：
    // - bool 短路 and/or（BIL §11.3：if + 合成局部展开）；
    // - if 表达式（合成结果局部 + LoweredIfStatement 前置 + 值块降级）；
    // - 复合赋值（前置「Target = Target op Value」赋值，表达式位 Target 引用）；
    // - if 语句恒等降级为 LoweredIfStatement。
    //
    // 实例化 session（仿 BilEmitter.EmitSession）的机制：
    // - 当前块输出语句列表栈：LowerBlock 为每块建输出列表；表达式降级时向
    //   当前列表追加前置语句，语句产物随后由循环追加——前置语句自然排在
    //   属主语句之前；
    // - 合成局部：.s0/.s1...（BIL §5.1 编译器保留名，与用户标识符零冲突，
    //   函数内唯一），收尾追加进 LoweredFunctionBody.Locals；
    // - 值块目标映射栈：BoundValueBlock → 写入局部（引用相等查找），供嵌套
    //   return@（穿透外层值块）命中外层映射；
    // - 合成节点的 Origin 约定：指向最近的语法来源（ARCH §5.1），如 and/or
    //   表达式 / if 表达式 / return@ 语句本身的 Bound 节点。
    //
    // 输入是无错 BoundTree（任一前置 pass 结束时有 Error 即不推进，§8）。
    // 遇未覆盖节点：报 P4 Error 诊断并跳过整个函数体（不产出
    // LoweredFunctionBody）——函数体之间诊断互不阻断（同 P3 原则）。
    public static class Lowerer
    {
        public static IReadOnlyList<LoweredFunctionBody> Lower(
            CompilationUnit unit, IReadOnlyList<BoundFunctionBody> bodies)
        {
            return new LowerSession(unit, bodies).Run();
        }

        private sealed class LowerSession
        {
            private readonly CompilationUnit unit;
            private readonly IReadOnlyList<BoundFunctionBody> bodies;

            // 当前函数上下文（函数体互不嵌套，无重入）
            private readonly List<LocalSymbol> synthLocals = new List<LocalSymbol>();
            private int synthCount;
            private readonly Stack<List<LoweredStatement>> outputStack =
                new Stack<List<LoweredStatement>>();
            private readonly Stack<(BoundValueBlock Block, LocalSymbol Target)> valueBlocks =
                new Stack<(BoundValueBlock, LocalSymbol)>();

            public LowerSession(CompilationUnit unit, IReadOnlyList<BoundFunctionBody> bodies)
            {
                this.unit = unit;
                this.bodies = bodies;
            }

            public IReadOnlyList<LoweredFunctionBody> Run()
            {
                var result = new List<LoweredFunctionBody>();
                foreach (var body in bodies)
                {
                    var lowered = LowerBody(body);
                    if (lowered != null) result.Add(lowered);
                }
                return result;
            }

            private LoweredFunctionBody? LowerBody(BoundFunctionBody body)
            {
                synthLocals.Clear();
                synthCount = 0;
                valueBlocks.Clear();    // 函数体互不嵌套，防御性清空
                var lowered = LowerBlock(body.Body);
                if (lowered == null) return null;
                // 合成局部（.s 前缀，脱糖产物）跟在源码局部之后
                return new LoweredFunctionBody(body.Method,
                    body.Locals.Concat(synthLocals).ToList(), lowered);
            }

            private void Error(CharRange? span, string message)
            {
                unit.Diagnostics.Error(DiagnosticPhase.P4, span, message);
            }

            // 合成局部（BIL §5.1 编译器保留名 .sN，函数内唯一）
            private LocalSymbol NewSynthLocal(TypeSymbol type)
            {
                var local = new LocalSymbol(".s" + synthCount, type, isConst: false);
                synthCount++;
                synthLocals.Add(local);
                return local;
            }

            // 合成值引用（Origin 指最近语法来源，见类头约定）
            private static LoweredValueReferenceExpression ReferenceTo(BoundNode origin,
                SemanticSymbol symbol)
            {
                return new LoweredValueReferenceExpression(origin, symbol);
            }

            // ===== 块与语句 =====

            // null 返回 = 已诊断失败（遇未覆盖节点），调用方放弃整个函数体
            private LoweredBlock? LowerBlock(BoundBlock block)
            {
                var statements = new List<LoweredStatement>();
                outputStack.Push(statements);
                try
                {
                    foreach (var statement in block.Statements)
                    {
                        var lowered = LowerStatement(statement);
                        if (lowered == null) return null;
                        statements.Add(lowered);
                    }
                    return new LoweredBlock(block, statements);
                }
                finally
                {
                    outputStack.Pop();
                }
            }

            private LoweredStatement? LowerStatement(BoundStatement statement)
            {
                switch (statement)
                {
                    case BoundBlock block:
                        return LowerBlock(block);
                    case BoundLocalDeclarationStatement decl:
                        LoweredExpression? initializer = null;
                        if (decl.Initializer != null)
                        {
                            initializer = LowerExpression(decl.Initializer);
                            if (initializer == null) return null;
                        }
                        return new LoweredLocalDeclarationStatement(decl, decl.Local, initializer);
                    case BoundExpressionStatement expressionStatement:
                        var expression = LowerExpression(expressionStatement.Expression);
                        if (expression == null) return null;
                        return new LoweredExpressionStatement(expressionStatement, expression);
                    case BoundCallStatement call:
                        var arguments = LowerArguments(call.Arguments);
                        if (arguments == null) return null;
                        return new LoweredCallStatement(call, call.Method, arguments);
                    case BoundAssignmentStatement assignment:
                        var target = LowerExpression(assignment.Target);
                        var value = LowerExpression(assignment.Value);
                        if (target == null || value == null) return null;
                        return new LoweredAssignmentStatement(assignment, target, value);
                    case BoundReturnStatement ret:
                        LoweredExpression? returnValue = null;
                        if (ret.Value != null)
                        {
                            returnValue = LowerExpression(ret.Value);
                            if (returnValue == null) return null;
                        }
                        return new LoweredReturnStatement(ret, returnValue);
                    case BoundIfStatement ifStatement:
                        // 恒等降级：条件/分支内表达式递归降级（前置语句进对应块）
                        var condition = LowerExpression(ifStatement.Condition);
                        if (condition == null) return null;
                        var trueBlock = LowerBlock(ifStatement.TrueBlock);
                        var falseBlock = ifStatement.FalseBlock == null
                            ? null : LowerBlock(ifStatement.FalseBlock);
                        if (trueBlock == null || (ifStatement.FalseBlock != null && falseBlock == null))
                        {
                            return null;
                        }
                        return new LoweredIfStatement(ifStatement, condition, trueBlock, falseBlock);
                    case BoundReturnValueStatement returnValueStatement:
                        // return@ 脱糖为「写目标值块局部」；目标沿映射栈查找
                        // （穿透外层时命中外层值块）。同块其后语句的截断由
                        // 值块降级循环处理。普通块（非值块降级上下文）出现即
                        // 内部错误——P3 已保证 return@ 只在值块内
                        var writeTarget = FindValueBlockTarget(returnValueStatement.Target);
                        var written = LowerExpression(returnValueStatement.Value);
                        if (written == null) return null;
                        return new LoweredAssignmentStatement(returnValueStatement,
                            ReferenceTo(returnValueStatement, writeTarget), written);
                    default:
                        Unsupported(statement);
                        return null;
                }
            }

            private LocalSymbol FindValueBlockTarget(BoundValueBlock valueBlock)
            {
                foreach (var (block, target) in valueBlocks)
                {
                    if (ReferenceEquals(block, valueBlock)) return target;
                }
                throw new CompilerInternalException(
                    "return@ 目标值块不在降级上下文内（P3 已保证只在值块内）");
            }

            private List<LoweredExpression>? LowerArguments(
                IReadOnlyList<BoundExpression> arguments)
            {
                var result = new List<LoweredExpression>();
                foreach (var argument in arguments)
                {
                    var lowered = LowerExpression(argument);
                    if (lowered == null) return null;
                    result.Add(lowered);
                }
                return result;
            }

            // ===== 值块降级与 if 转换 =====

            // 值块降级（BoundValueBlock → LoweredBlock）：写目标局部由调用方
            // （if 表达式脱糖）给定；值块映射压栈供嵌套 return@ 查找。
            // - 隐式取值 → 单语句 v = expr；
            // - 显式：语句流降级，return@ ⇒ 写对应局部（本块或外层），同块其
            //   后语句丢弃（不可达死代码，P3 已保证路径必终止）；
            // - 收尾做 if 转换（TransformStatements）
            private LoweredBlock? LowerValueBlock(BoundValueBlock valueBlock, LocalSymbol target)
            {
                var statements = new List<LoweredStatement>();
                outputStack.Push(statements);
                valueBlocks.Push((valueBlock, target));
                try
                {
                    if (valueBlock.IsImplicitValue)
                    {
                        // P3 已判定：唯一语句是 BoundExpressionStatement
                        if (valueBlock.Block.Statements.Count != 1
                            || valueBlock.Block.Statements[0]
                                is not BoundExpressionStatement expressionStatement)
                        {
                            throw new CompilerInternalException(
                                "隐式取值值块的唯一语句不是表达式语句（P3 不变量破坏）");
                        }
                        var value = LowerExpression(expressionStatement.Expression);
                        if (value == null) return null;
                        statements.Add(new LoweredAssignmentStatement(expressionStatement,
                            ReferenceTo(expressionStatement, target), value));
                    }
                    else
                    {
                        foreach (var statement in valueBlock.Block.Statements)
                        {
                            var lowered = LowerStatement(statement);
                            if (lowered == null) return null;
                            statements.Add(lowered);
                            // return@ 终止本块路径：其后语句不可达（死代码，
                            // P3 已保证路径必终止），直接截断不降级
                            if (statement is BoundReturnValueStatement) break;
                        }
                        TransformStatements(statements);
                    }
                    return new LoweredBlock(valueBlock.Block, statements);
                }
                finally
                {
                    valueBlocks.Pop();
                    outputStack.Pop();
                }
            }

            // 值块语句序列的 if 转换（就地；分支块不可变，故新建
            // LoweredIfStatement/LoweredBlock 替换原位置）。规则：
            // BIL 的 if 分支块执行完必回到 if 的下一条指令（§16.2），而值块
            // return@ 语义是「写目标局部 + 本路径不再执行后续」——故遇
            // LoweredIfStatement：
            // - 某分支以「值块写入」终止而另一分支不终止：把 if 之后的语句
            //   序列移动追加到不终止分支末尾（无 else 则新建 else 块，Origin
            //   指 if 的 Bound 节点），if 成为块内最后一条；
            // - 双分支都终止：其后语句全丢弃（不可达）；
            // - 嵌套块/分支块内部递归同规则处理；嵌套块整体终止时其后语句
            //   同样丢弃。
            // 「值块写入」判定：赋值目标引用值块映射栈中的目标局部（合成
            // 局部 .sN 只被值块写入与短路/if 表达式结果使用——后两者不在
            // 映射栈上，互不混淆）。
            // 例：{ if (c) { return@_ 1 }  x = 2  return@_ x }
            //   ⇒ { if (c) { v = 1 } else { x = 2; v = x } }
            private void TransformStatements(List<LoweredStatement> statements)
            {
                for (int i = 0; i < statements.Count; i++)
                {
                    switch (statements[i])
                    {
                        case LoweredIfStatement ifStatement:
                        {
                            var trueBlock = TransformBlock(ifStatement.TrueBlock);
                            var falseBlock = ifStatement.FalseBlock == null
                                ? null : TransformBlock(ifStatement.FalseBlock);
                            var trueTerminates = BlockTerminates(trueBlock);
                            var falseTerminates =
                                falseBlock != null && BlockTerminates(falseBlock);
                            if (trueTerminates || falseTerminates)
                            {
                                var rest = statements.GetRange(i + 1, statements.Count - i - 1);
                                statements.RemoveRange(i + 1, statements.Count - i - 1);
                                if (trueTerminates && falseTerminates)
                                {
                                    // 其后语句全丢弃（不可达）
                                    statements[i] = new LoweredIfStatement(ifStatement.Origin,
                                        ifStatement.Condition, trueBlock, falseBlock);
                                    return;
                                }
                                // 后续语句先按同规则转换，再移入不终止分支末尾
                                TransformStatements(rest);
                                if (trueTerminates)
                                {
                                    var merged = new List<LoweredStatement>();
                                    if (falseBlock != null) merged.AddRange(falseBlock.Statements);
                                    merged.AddRange(rest);
                                    falseBlock = new LoweredBlock(
                                        falseBlock?.Origin ?? ifStatement.Origin, merged);
                                }
                                else
                                {
                                    var merged = new List<LoweredStatement>(trueBlock.Statements);
                                    merged.AddRange(rest);
                                    trueBlock = new LoweredBlock(trueBlock.Origin, merged);
                                }
                                statements[i] = new LoweredIfStatement(ifStatement.Origin,
                                    ifStatement.Condition, trueBlock, falseBlock);
                                return;    // if 成为块内最后一条
                            }
                            statements[i] = new LoweredIfStatement(ifStatement.Origin,
                                ifStatement.Condition, trueBlock, falseBlock);
                            break;
                        }
                        case LoweredBlock nested:
                        {
                            var transformed = TransformBlock(nested);
                            statements[i] = transformed;
                            if (BlockTerminates(transformed))
                            {
                                statements.RemoveRange(i + 1, statements.Count - i - 1);
                                return;
                            }
                            break;
                        }
                    }
                }
            }

            private LoweredBlock TransformBlock(LoweredBlock block)
            {
                var statements = new List<LoweredStatement>(block.Statements);
                TransformStatements(statements);
                return new LoweredBlock(block.Origin, statements);
            }

            // 「块内所有路径终止于值块写入」判定：末语句是值块写入赋值 → true；
            // 末语句是双分支都终止的 LoweredIfStatement → true；末语句是嵌套
            // LoweredBlock → 递归；其余 false（LoweredReturnStatement 函数返回
            // 是 BIL 真跳转，不参与值块终止判定）
            private bool BlockTerminates(LoweredBlock block)
            {
                if (block.Statements.Count == 0) return false;
                return block.Statements[^1] switch
                {
                    LoweredAssignmentStatement assignment => IsValueBlockWrite(assignment),
                    LoweredIfStatement ifStatement => ifStatement.FalseBlock != null
                        && BlockTerminates(ifStatement.TrueBlock)
                        && BlockTerminates(ifStatement.FalseBlock),
                    LoweredBlock nested => BlockTerminates(nested),
                    _ => false,
                };
            }

            private bool IsValueBlockWrite(LoweredAssignmentStatement assignment)
            {
                if (assignment.Target is not LoweredValueReferenceExpression reference)
                {
                    return false;
                }
                foreach (var (_, target) in valueBlocks)
                {
                    if (ReferenceEquals(reference.Symbol, target)) return true;
                }
                return false;
            }

            // ===== 表达式降级（前置语句追加到当前块输出列表）=====

            private LoweredExpression? LowerExpression(BoundExpression expression)
            {
                switch (expression)
                {
                    case BoundLiteralExpression literal:
                        return new LoweredLiteralExpression(literal);
                    case BoundValueReferenceExpression valueReference:
                        return new LoweredValueReferenceExpression(valueReference,
                            valueReference.Symbol);
                    case BoundFieldReferenceExpression fieldReference:
                        return new LoweredFieldReferenceExpression(fieldReference,
                            fieldReference.Field);
                    case BoundBinaryExpression binary:
                        // P3 仅对内建 bool 定型 and/or（SYNTAX §13.2：未被重载
                        // 才短路），Op=And/Or 即短路展开（BIL §11.3）
                        if (binary.Op is BilIntrinsicOp.And or BilIntrinsicOp.Or)
                        {
                            return LowerShortCircuit(binary);
                        }
                        var left = LowerExpression(binary.Left);
                        var right = LowerExpression(binary.Right);
                        if (left == null || right == null) return null;
                        return new LoweredBinaryExpression(binary, binary.Op, left, right);
                    case BoundUnaryExpression unary:
                        var operand = LowerExpression(unary.Operand);
                        if (operand == null) return null;
                        return new LoweredUnaryExpression(unary, unary.Op, operand);
                    case BoundCallExpression call:
                        var callArguments = LowerArguments(call.Arguments);
                        if (callArguments == null) return null;
                        return new LoweredCallExpression(call, call.Method, callArguments);
                    case BoundNewExpression newExpression:
                        var newArguments = LowerArguments(newExpression.Arguments);
                        if (newArguments == null) return null;
                        return new LoweredNewExpression(newExpression, newExpression.Init,
                            newArguments);
                    case BoundIfExpression ifExpression:
                        return LowerIfExpression(ifExpression);
                    case BoundCompoundAssignmentExpression compound:
                        return LowerCompoundAssignment(compound);
                    default:
                        Unsupported(expression);
                        return null;
                }
            }

            // bool 短路 and/or（BIL §11.3）脱糖：
            //   a and b ⇒ 合成局部 s；前置 if a' { s = b' } else { s = false }；
            //             表达式位 s 引用
            //   a or  b ⇒ 合成局部 s；前置 if a' { s = true } else { s = b }；
            //             表达式位 s 引用
            // 条件与分支内的表达式递归降级（前置语句追加到对应块的输出列表）；
            // true/false 用 LoweredConstantExpression（合成节点，Origin 指
            // and/or 表达式本身的 Bound 节点）
            private LoweredExpression? LowerShortCircuit(BoundBinaryExpression binary)
            {
                var s = NewSynthLocal(binary.Type);
                var condition = LowerExpression(binary.Left);
                if (condition == null) return null;
                var assignRight = LowerAssignInNewBlock(binary, binary.Right, s);
                if (assignRight == null) return null;
                var constant = new LoweredConstantExpression(binary,
                    binary.Op == BilIntrinsicOp.And ? false : true, binary.Type);
                var assignConstant = new LoweredBlock(binary, new List<LoweredStatement>
                {
                    new LoweredAssignmentStatement(binary, ReferenceTo(binary, s), constant),
                });
                var (trueBlock, falseBlock) = binary.Op == BilIntrinsicOp.And
                    ? (assignRight, assignConstant)
                    : (assignConstant, assignRight);
                outputStack.Peek().Add(new LoweredIfStatement(binary, condition,
                    trueBlock, falseBlock));
                return ReferenceTo(binary, s);
            }

            // 在独立块上下文里降级表达式并写目标局部（前置语句随块走）；
            // 产物是单语句合成块（Origin 指最近语法来源）
            private LoweredBlock? LowerAssignInNewBlock(BoundNode origin, BoundExpression value,
                LocalSymbol target)
            {
                var statements = new List<LoweredStatement>();
                outputStack.Push(statements);
                try
                {
                    var lowered = LowerExpression(value);
                    if (lowered == null) return null;
                    statements.Add(new LoweredAssignmentStatement(origin,
                        ReferenceTo(origin, target), lowered));
                    return new LoweredBlock(origin, statements);
                }
                finally
                {
                    outputStack.Pop();
                }
            }

            // if 表达式脱糖：合成结果局部 v；前置 LoweredIfStatement（两分支
            // 值块降级产物，写 v）；表达式位 v 引用
            private LoweredExpression? LowerIfExpression(BoundIfExpression ifExpression)
            {
                var result = NewSynthLocal(ifExpression.Type);
                var condition = LowerExpression(ifExpression.Condition);
                if (condition == null) return null;
                var trueBranch = LowerValueBlock(ifExpression.TrueBranch, result);
                var falseBranch = LowerValueBlock(ifExpression.FalseBranch, result);
                if (trueBranch == null || falseBranch == null) return null;
                outputStack.Peek().Add(new LoweredIfStatement(ifExpression, condition,
                    trueBranch, falseBranch));
                return ReferenceTo(ifExpression, result);
            }

            // 复合赋值脱糖（SYNTAX §13.2）：前置「Target = Target op Value」
            // 赋值，表达式位 Target 引用（写回后值）。Target 是局部/字段引用
            // （P3 强制 place），降级为纯引用构造无副作用，三处引用（赋值左/
            // 运算左/表达式位）各自独立构造
            private LoweredExpression? LowerCompoundAssignment(
                BoundCompoundAssignmentExpression compound)
            {
                var target = LowerExpression(compound.Target);
                var targetRead = LowerExpression(compound.Target);
                var value = LowerExpression(compound.Value);
                if (target == null || targetRead == null || value == null) return null;
                outputStack.Peek().Add(new LoweredAssignmentStatement(compound, target,
                    new LoweredBinaryExpression(compound, compound.Op, targetRead, value)));
                return LowerExpression(compound.Target);
            }

            private void Unsupported(BoundNode node)
            {
                Error(node.Syntax.Span,
                    $"P4: node kind not supported by minimal lowering (S7): " +
                    node.GetType().Name);
            }
        }
    }
}
