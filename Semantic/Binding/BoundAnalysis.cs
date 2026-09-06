namespace RigiCompiler
{
    // BoundTree 静态分析（自旧 BindSession 原样迁移，行为不变）：
    // 对绑定产物的纯查询设施，visitor 与驱动器共用。
    internal static class BoundAnalysis
    {
        // 所有路径显式返回（SYNTAX §4.1 无隐式返回）：
        // 末语句 return/throw、双分支 if 均保证、switch 全分支（含 default）保证、
        // try（finally 终止覆盖全路径；否则 try 与全 catch 保证）、嵌套块递归；
        // 语句位置的 seq 块透视（GuaranteesValueReturn/EnumerateStatements 同口径——
        // seq 语句体的裸 return 直达外层函数）；值块（BoundValueBlock）语义不同，
        // 不在此列；
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
                BoundSeqStatement seqStatement => GuaranteesReturn(seqStatement.Body)
                    && !HasEscapingSeqExit(seqStatement),
                BoundBlock nested => GuaranteesReturn(nested),
                _ => false,
            };
        }

        // seq 语句透视前提：体内无逃逸型 return@seq——命中本 seq 或更外层
        // seq 的 BoundSeqExitStatement（该路径落出 seq 边界继续执行，不算
        // 本路径终止）。命中内层 seq 的 exit 不出本边界，不拦截
        //（EnumerateStatements 已下钻嵌套 seq 体，收集即传递闭包）
        private static bool HasEscapingSeqExit(BoundSeqStatement seq)
        {
            var nested = new HashSet<BoundSeqStatement>(
                EnumerateStatements(seq.Body).OfType<BoundSeqStatement>(),
                ReferenceEqualityComparer.Instance);
            return EnumerateStatements(seq.Body)
                .OfType<BoundSeqExitStatement>()
                .Any(exit => !nested.Contains(exit.Target));
        }

        // 值块视角的路径终止（S7b）：return@ 命中、break/continue 穿透
        // （GuaranteesValueReturn 视其为路径终止）、throw、裸 return
        // （SYNTAX §6.1 裁决起裸 return 不得穿透值块——绑定期已拦截，
        // 此处仅为防御性兜底：以 return 终止的路径不落到块尾，不要求
        // return@）；复合结构
        // 递归同 GuaranteesReturn，另含 seq 语句体（S7e）与末语句携带
        // 表达式内「绝不落穿」的嵌套值块（NestedValueBlockEscapes）。
        // 循环：do-while 至少执行一次，体保证产值即本路径保证；while/for
        // 可零次执行仍保守 false（条件位逃逸另由 NestedValueBlockEscapes 覆盖）
        public static bool GuaranteesValueReturn(BoundBlock block)
        {
            return block.Statements.Count > 0 && block.Statements[^1] switch
            {
                BoundReturnValueStatement => true,
                BoundReturnStatement => true,
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
                BoundSeqStatement seqStatement => GuaranteesValueReturn(seqStatement.Body)
                    && !HasEscapingSeqExit(seqStatement),
                BoundBlock nested => GuaranteesValueReturn(nested),
                BoundLoop loop =>
                    (loop.Kind == LoopKind.DoWhile && GuaranteesValueReturn(loop.Body))
                    || StatementCarriedExpressions(loop).Any(NestedValueBlockEscapes),
                // 末语句携带表达式内的嵌套值块逃逸（如
                // `var t = seq { if (c) { return@outer a } else { return@outer b } }`）：
                // 内层值块全路径向外逃逸（不命中自身产值、不落穿）时，
                // 表达式求值永不完成，本路径同样终止
                var last => StatementCarriedExpressions(last).Any(NestedValueBlockEscapes),
            };
        }

        // 嵌套值块逃逸判定（GuaranteesValueReturn 的表达式下钻，返回
        // bool 而非枚举）：表达式求值是否「绝不落穿」。
        // - seq 表达式：ValueType == null（无命中自身的 return@ ⇒ 体内
        //   每条 return@ 都向外逃逸）且体全路径终止；
        // - if/switch 表达式：全部分支值块同口径逃逸（if 表达式前端
        //   保证双分支；switch 含 default）；
        // - 条件求值位置不算：and/or 右侧、?? 右侧、?. 访问体仅在特定
        //   条件下求值，其逃逸不代表整体不落穿——只看无条件求值侧；
        // - 其余表达式透明穿透子表达式（ChildExpressions）。
        // 关键正确性约束：绝不下钻 lambda 体——lambda 内的 exit 只在
        // 被调用时发生，不能算作外层路径终止（ChildExpressions 对
        // BoundLambdaExpression 会穿透体语句携带表达式，此处必须显式
        // 拦截）。
        // public：P4 循环降级复用——条件表达式「绝不落穿」时（逃逸型
        // seq/if/switch 在条件位）条件写回动态不可达，改写为 false
        // 字面量（LoopRewriter），避免读取永不赋值的结果局部
        public static bool NestedValueBlockEscapes(BoundExpression expression)
        {
            switch (expression)
            {
                case BoundLambdaExpression:
                    return false;
                case BoundSeqExpression seqExpression:
                    return ValueBlockEscapes(seqExpression.Body);
                case BoundIfExpression ifExpression:
                    return ValueBlockEscapes(ifExpression.TrueBranch)
                        && ValueBlockEscapes(ifExpression.FalseBranch);
                case BoundSwitchExpression switchExpression:
                    return switchExpression.Cases.All(c => ValueBlockEscapes(c.Body))
                        && ValueBlockEscapes(switchExpression.DefaultBody);
                case BoundBinaryExpression { Op: BilIntrinsicOp.And or BilIntrinsicOp.Or } logical:
                    return NestedValueBlockEscapes(logical.Left);
                case BoundNullFallbackExpression nullFallback:
                    return NestedValueBlockEscapes(nullFallback.Left);
                case BoundSafeAccessExpression safeAccess:
                    return NestedValueBlockEscapes(safeAccess.Receiver);
                default:
                    return ChildExpressions(expression).Any(NestedValueBlockEscapes);
            }
        }

        // 分支值块逃逸 = 自身无产值（ValueType null ⇒ 体内 return@ 全部
        // 命中外层块）且全路径终止（if/switch 表达式「全分支逃逸」
        // 判定复用——NestedValueBlockEscapes 与各表达式 visitor 共用）
        public static bool ValueBlockEscapes(BoundValueBlock branch)
        {
            return branch.ValueType == null && GuaranteesValueReturn(branch.Block);
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
        // 命中外层值块，收集/终止判定须看得到）、BoundTryStatement 三个块、
        // BoundSeqStatement 体（S7e：同理穿透可见）与 BoundLoop 体（值块
        // 目标跨循环放行后，CollectBranchValueType 须看到体内 return@；
        // 按 ReferenceEquals 精确过滤目标块，不下钻即误判「不产值」））；
        // 并下钻语句携带表达式内部的嵌套值块——return@ 可藏在表达式位置
        // 的 if/switch/seq 表达式分支体里（如
        // `return@outer if (c) { return@outer v } else { ... }`、
        // `var x = if (c) { return@outer v } else { ... }`），其值类型同样
        // 参与外层统一
        public static IEnumerable<BoundStatement> EnumerateStatements(BoundBlock block)
        {
            foreach (var statement in block.Statements)
            {
                yield return statement;
                // 语句携带表达式内的嵌套值块（先于语句级复合下钻——源码序）
                foreach (var expression in StatementCarriedExpressions(statement))
                {
                    foreach (var s in EnumerateExpressionValueBlocks(expression)) yield return s;
                }
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
                    case BoundLoop loop:
                        foreach (var s in EnumerateStatements(loop.Body))
                            yield return s;
                        break;
                }
            }
        }

        // 语句直接携带的表达式（嵌套值块的下钻入口；复合语句的块由
        // 语句级递归覆盖，不在此列出）
        internal static IEnumerable<BoundExpression> StatementCarriedExpressions(
            BoundStatement statement)
        {
            switch (statement)
            {
                case BoundLocalDeclarationStatement declaration:
                    if (declaration.Initializer != null) yield return declaration.Initializer;
                    break;
                case BoundDestructuringDeclarationStatement destructuring:
                    yield return destructuring.Initializer;
                    break;
                case BoundExpressionStatement expressionStatement:
                    yield return expressionStatement.Expression;
                    break;
                case BoundYieldStatement yield:
                    if (yield.Alarm != null) yield return yield.Alarm;
                    break;
                case BoundCallStatement call:
                    if (call.Receiver != null) yield return call.Receiver;
                    foreach (var argument in call.Arguments) yield return argument;
                    break;
                case BoundAssignmentStatement assignment:
                    yield return assignment.Target;
                    yield return assignment.Value;
                    break;
                case BoundReturnStatement returnStatement:
                    if (returnStatement.Value != null) yield return returnStatement.Value;
                    break;
                case BoundReturnValueStatement returnValue:
                    yield return returnValue.Value;
                    break;
                case BoundThrowStatement throwStatement:
                    yield return throwStatement.Exception;
                    break;
                case BoundIfStatement ifStatement:
                    yield return ifStatement.Condition;
                    break;
                case BoundLoop loop:
                    if (loop.Condition != null) yield return loop.Condition;
                    if (loop.Iterable != null) yield return loop.Iterable;
                    break;
                case BoundSwitchStatement switchStatement:
                    yield return switchStatement.Selector;
                    foreach (var switchCase in switchStatement.Cases)
                        yield return switchCase.Match;
                    break;
                case BoundNewWrapperStatement newWrapper:
                    foreach (var argument in newWrapper.Arguments) yield return argument;
                    break;
            }
        }

        // 表达式内部的嵌套值块枚举：命中 if/switch/seq 表达式即下钻其值块
        // （块内语句回到语句级枚举）；其余表达式透明穿透子表达式
        private static IEnumerable<BoundStatement> EnumerateExpressionValueBlocks(
            BoundExpression expression)
        {
            switch (expression)
            {
                case BoundIfExpression ifExpression:
                    foreach (var s in EnumerateExpressionValueBlocks(ifExpression.Condition))
                        yield return s;
                    foreach (var s in EnumerateStatements(ifExpression.TrueBranch.Block))
                        yield return s;
                    foreach (var s in EnumerateStatements(ifExpression.FalseBranch.Block))
                        yield return s;
                    break;
                case BoundSwitchExpression switchExpression:
                    foreach (var s in EnumerateExpressionValueBlocks(switchExpression.Selector))
                        yield return s;
                    foreach (var switchCase in switchExpression.Cases)
                    {
                        foreach (var s in EnumerateExpressionValueBlocks(switchCase.Match))
                            yield return s;
                        foreach (var s in EnumerateStatements(switchCase.Body.Block))
                            yield return s;
                    }
                    foreach (var s in EnumerateStatements(switchExpression.DefaultBody.Block))
                        yield return s;
                    break;
                case BoundSeqExpression seqExpression:
                    foreach (var s in EnumerateStatements(seqExpression.Body.Block))
                        yield return s;
                    break;
                default:
                    foreach (var child in ChildExpressions(expression))
                    {
                        foreach (var s in EnumerateExpressionValueBlocks(child)) yield return s;
                    }
                    break;
            }
        }

        // 表达式的直接子表达式（透明穿透用）。注意
        // BoundSwitchPlaceholderExpression.Selector 是回指边（selector 已由
        // 所属 switch 枚举），不跟随——避免重复枚举
        internal static IEnumerable<BoundExpression> ChildExpressions(BoundExpression expression)
        {
            switch (expression)
            {
                case BoundBinaryExpression binary:
                    yield return binary.Left;
                    yield return binary.Right;
                    break;
                case BoundUnaryExpression unary:
                    yield return unary.Operand;
                    break;
                case BoundAwaitExpression awaitExpression:
                    yield return awaitExpression.Operand;
                    break;
                case BoundCallExpression call:
                    foreach (var argument in call.Arguments) yield return argument;
                    break;
                case BoundNewExpression newExpression:
                    foreach (var argument in newExpression.Arguments) yield return argument;
                    break;
                case BoundDynamicNewExpression dynamicNew:
                    if (dynamicNew.TypeValue != null) yield return dynamicNew.TypeValue;
                    foreach (var argument in dynamicNew.Arguments) yield return argument;
                    break;
                case BoundCompoundAssignmentExpression compound:
                    yield return compound.Target;
                    yield return compound.Value;
                    break;
                case BoundInstanceCallExpression instanceCall:
                    yield return instanceCall.Receiver;
                    foreach (var argument in instanceCall.Arguments) yield return argument;
                    break;
                case BoundFieldAccessExpression fieldAccess:
                    yield return fieldAccess.Receiver;
                    break;
                case BoundIndexExpression index:
                    yield return index.Receiver;
                    yield return index.Index;
                    break;
                case BoundCastExpression cast:
                    yield return cast.Source;
                    break;
                case BoundSmartCastExpression smartCast:
                    yield return smartCast.Operand;
                    break;
                case BoundSafeAccessExpression safeAccess:
                    yield return safeAccess.Receiver;
                    yield return safeAccess.Access;
                    break;
                case BoundNullFallbackExpression nullFallback:
                    yield return nullFallback.Left;
                    yield return nullFallback.Right;
                    break;
                case BoundTypeCheckExpression typeCheck:
                    yield return typeCheck.Operand;
                    if (typeCheck.TargetValue != null) yield return typeCheck.TargetValue;
                    break;
                case BoundTypeOfExpression typeOf:
                    if (typeOf.Operand != null) yield return typeOf.Operand;
                    break;
                case BoundPlaceOfExpression placeOf:
                    yield return placeOf.Operand;
                    break;
                case BoundVarArgsArgument varArgs:
                    foreach (var value in varArgs.Values) yield return value;
                    foreach (var (_, value) in varArgs.NamedValues) yield return value;
                    break;
                case BoundInnerCallExpression innerCall:
                    foreach (var argument in innerCall.Arguments) yield return argument;
                    break;
                case BoundSuperCallExpression superCall:
                    foreach (var argument in superCall.Arguments) yield return argument;
                    break;
                case BoundEnumCaseExpression enumCase:
                    foreach (var argument in enumCase.Arguments) yield return argument;
                    break;
                case BoundWrapperAccessExpression wrapperAccess:
                    yield return wrapperAccess.Receiver;
                    break;
                case BoundLambdaExpression lambda:
                    foreach (var statement in lambda.CallBody.Body.Statements)
                        foreach (var childExpression in StatementCarriedExpressions(statement))
                            yield return childExpression;
                    break;
                // 叶子（字面量/值引用/字段引用/this/self/安全访问占位）与
                // BoundSwitchPlaceholderExpression（回指跳过）：无子表达式
            }
        }

        // M88/#28④：降级调用结果判定（SYNTAX §14.7）——调用点静态类型恒 Any
        //（胖值 ABI），P3 类型兼容性检查豁免。识别：BoundInstanceCallExpression
        // 且方法即 bootstrap Any.call???（引用相等）。语义边界：仅直接包裹
        // 降级调用的表达式。豁免位置：声明初始化/赋值（含索引写）/return/
        // 实参/if? 右操作数/throw 操作数/复合赋值 RHS——转换均骑 P4a
        // EnsureDeclaredType（§6.5）物化显式 cast
        public static bool IsDowngradeCallResult(BoundExpression expression, BindEnvironment env)
        {
            return expression is BoundInstanceCallExpression { Method: { } method }
                && ReferenceEquals(method, env.B.CallWildcard);
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
