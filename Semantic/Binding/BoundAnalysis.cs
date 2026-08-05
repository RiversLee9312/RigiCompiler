namespace LatteCompiler
{
    // BoundTree 静态分析（自旧 BindSession 原样迁移，行为不变）：
    // 对绑定产物的纯查询设施，visitor 与驱动器共用。
    internal static class BoundAnalysis
    {
        // 所有路径显式返回（SYNTAX §4.1 无隐式返回）：
        // 末语句 return/throw、双分支 if 均保证、switch 全分支（含 default）保证、
        // try（finally 终止覆盖全路径；否则 try 与全 catch 保证）、嵌套块递归；
        // 循环保守 false（S7c-1：体可能零次执行的一般情形无法判定，S7c 技术债）；
        // BoundLoopControl/Break 终止循环路径、BoundReturnValueStatement 终止值块
        // 路径，均不算函数返回。
        public static bool GuaranteesReturn(BoundBlock block)
        {
            return block.Statements.Count > 0 && block.Statements[^1] switch
            {
                BoundReturnStatement => true,
                BoundIfStatement ifStatement => ifStatement.FalseBlock != null
                    && GuaranteesReturn(ifStatement.TrueBlock)
                    && GuaranteesReturn(ifStatement.FalseBlock),
                BoundThrowStatement => true,
                BoundSwitchStatement switchStatement =>
                    switchStatement.Cases.All(c => GuaranteesReturn(c.Body))
                    && GuaranteesReturn(switchStatement.DefaultBody),
                BoundTryStatement tryStatement =>
                    (tryStatement.FinallyBlock != null
                        && GuaranteesReturn(tryStatement.FinallyBlock))
                    || (GuaranteesReturn(tryStatement.TryBlock)
                        && tryStatement.Catches.All(c => GuaranteesReturn(c.Body))),
                BoundBlock nested => GuaranteesReturn(nested),
                _ => false,
            };
        }

        // 值块视角的路径终止（S7b）：return@ 命中、break/continue 穿透
        // （GuaranteesValueReturn 视其为路径终止）、throw；复合结构递归同
        // GuaranteesReturn，另含 seq 语句体（S7e）
        public static bool GuaranteesValueReturn(BoundBlock block)
        {
            return block.Statements.Count > 0 && block.Statements[^1] switch
            {
                BoundReturnValueStatement => true,
                BoundLoopControl => true,
                BoundThrowStatement => true,
                BoundIfStatement ifStatement => ifStatement.FalseBlock != null
                    && GuaranteesValueReturn(ifStatement.TrueBlock)
                    && GuaranteesValueReturn(ifStatement.FalseBlock),
                BoundSwitchStatement switchStatement =>
                    switchStatement.Cases.All(c => GuaranteesValueReturn(c.Body))
                    && GuaranteesValueReturn(switchStatement.DefaultBody),
                BoundTryStatement tryStatement =>
                    (tryStatement.FinallyBlock != null
                        && GuaranteesValueReturn(tryStatement.FinallyBlock))
                    || (GuaranteesValueReturn(tryStatement.TryBlock)
                        && tryStatement.Catches.All(c => GuaranteesValueReturn(c.Body))),
                BoundSeqStatement seqStatement => GuaranteesValueReturn(seqStatement.Body),
                BoundBlock nested => GuaranteesValueReturn(nested),
                _ => false,
            };
        }

        // 收集分支块内命中本块的 return@ 值类型（递归嵌套块、if 分支与
        // switch 分支体）；全部须类型一致（符号 ==，驻留保证；ErrorType
        // 毒化静默跳过），不一致诊断并以首个为准；无命中（纯穿透终止）→ null。
        // construct 为诊断消息中的构造名（"if expression"/"switch expression"）。
        // S9a：值类型放宽为 SemanticSymbol（泛型参数产值按引用相等参与）
        public static SemanticSymbol? CollectBranchValueType(BoundBlock block,
            BoundValueBlock shell, string construct, BindEnvironment env)
        {
            SemanticSymbol? collected = null;
            foreach (var statement in EnumerateStatements(block))
            {
                if (statement is BoundReturnValueStatement returnValue
                    && ReferenceEquals(returnValue.Target, shell)
                    && returnValue.Value.Type is not ErrorTypeSymbol)
                {
                    if (collected == null)
                    {
                        collected = returnValue.Value.Type;
                    }
                    else if (!ReferenceEquals(collected, returnValue.Value.Type))
                    {
                        env.Error(statement.Syntax.Span,
                            $"{construct} branch produces different types " +
                            $"('{TypeDisplay(collected)}' and " +
                            $"'{TypeDisplay(returnValue.Value.Type)}')");
                    }
                }
            }
            return collected;
        }

        // 块内语句的平铺枚举（递归嵌套 BoundBlock、BoundIfStatement 两分支、
        // BoundSwitchStatement 全部分支体（S7d：switch 体内 return@ 可穿透
        // 命中外层值块，收集/终止判定须看得到）、BoundTryStatement 三个块与
        // BoundSeqStatement 体（S7e：同理穿透可见））
        public static IEnumerable<BoundStatement> EnumerateStatements(BoundBlock block)
        {
            foreach (var statement in block.Statements)
            {
                yield return statement;
                switch (statement)
                {
                    case BoundBlock nested:
                        foreach (var s in EnumerateStatements(nested)) yield return s;
                        break;
                    case BoundIfStatement ifStatement:
                        foreach (var s in EnumerateStatements(ifStatement.TrueBlock))
                            yield return s;
                        if (ifStatement.FalseBlock != null)
                        {
                            foreach (var s in EnumerateStatements(ifStatement.FalseBlock))
                                yield return s;
                        }
                        break;
                    case BoundSwitchStatement switchStatement:
                        foreach (var switchCase in switchStatement.Cases)
                        {
                            foreach (var s in EnumerateStatements(switchCase.Body))
                                yield return s;
                        }
                        foreach (var s in EnumerateStatements(switchStatement.DefaultBody))
                            yield return s;
                        break;
                    case BoundTryStatement tryStatement:
                        foreach (var s in EnumerateStatements(tryStatement.TryBlock))
                            yield return s;
                        foreach (var catchClause in tryStatement.Catches)
                        {
                            foreach (var s in EnumerateStatements(catchClause.Body))
                                yield return s;
                        }
                        if (tryStatement.FinallyBlock != null)
                        {
                            foreach (var s in EnumerateStatements(tryStatement.FinallyBlock))
                                yield return s;
                        }
                        break;
                    case BoundSeqStatement seqStatement:
                        foreach (var s in EnumerateStatements(seqStatement.Body))
                            yield return s;
                        break;
                }
            }
        }

        // 诊断用类型显示名（迁移自旧 BindSession.TypeDisplay）；
        // S9 放宽为 SemanticSymbol：泛型参数显示其名，构造实参递归显示
        public static string TypeDisplay(SemanticSymbol type)
        {
            if (type is not TypeSymbol symbol)
            {
                return type.Name;
            }
            if (symbol.ConstructedFrom == null) return symbol.Name;
            return symbol.Name + "<" + string.Join(", ",
                symbol.TypeArguments!.Select(a => a is TypeSymbol t ? TypeDisplay(t) : a.Name)) + ">";
        }
    }
}
