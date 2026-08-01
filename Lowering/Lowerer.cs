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
    // S7c-1 落地循环降级（while/do-while → LoweredLoop，BIL §16.3/§16.4）：
    // 条件求值移入 Judge 块（末尾写合成 bool 条件局部 .sN）、合成
    // .breakid 局部（.bN 命名）、BoundLoop → BreakId 映射栈供
    // break/continue 引用命中（穿透值块/嵌套块直接发 BIL 跳转，不展开）。
    // S7c-2 落地实例成员恒等降级（this/实例调用/实例字段）与 for 脱糖
    // （for-each 协议，SYNTAX §7.3）：前置 iterate() 写合成枚举器局部、
    // Judge = moveNext() 写条件局部、Body 头 = 循环变量 = current()——
    // 产物复用 LoweredLoop，P4b 零新增；协议三方法符号取 P3 挂在
    // BoundLoop 上的产物（P4 不做名字分析，ARCH §11.3 纪律）。
    // S7d 落地 switch 与 throw（SYNTAX §7.2/§8）：
    // - 全值匹配 switch → LoweredSwitch（BIL §16.6 直接对应，合成
    //   .breakid 局部 .bN 满足指令形态；Latte 层 break 不指向 switch）；
    // - 含 pattern（_）的 switch → selector 物化合成局部 .sN（前置语句，
    //   全 switch 只求值一次；占位 _ 即读该局部，经 selector 引用查
    //   映射栈）+ 嵌套 LoweredIfStatement 链（§16.6 规则：值分支条件 =
    //   合成 cmp.eq(.sN, 常量)，pattern 分支条件直接降级，default 落
    //   最内层 else）；
    // - switch 表达式 → 合成结果局部 + 前置 switch/if 链语句（各分支
    //   值块降级写结果局部，复用值块映射栈与 if 转换）；
    // - throw 恒等降级。
    // S7d 同批修复 M46 值块 if 转换缺陷（else-if 链混合终止时后续语句
    // 被错误编织到已终止路径——TransformStatements 重写为 continuation
    // 编织，见方法注释）。
    // S7e 落地 cast / try / seq（SYNTAX §3.5/§8/§10）：
    // - cast 恒等降级（as → cast、as? → cast.safe，BIL §12.1/§12.2）；
    // - try → LoweredTryStatement（BIL §16.7 直接对应）：ExceptionSlot =
    //   finally(e) 的 e（非空时）或合成 .sN（Nullable<core.Exception>），
    //   有名 catch 体头合成「变量 = cast slot」赋值；
    // - seq 双形态汇合 LoweredSeqBlock（BIL §3.4 独立 block + call 化）：
    //   语句形态恒等降级；表达式形态脱糖（合成结果局部 + 前置 seq 块
    //   包值块降级产物，同 if 表达式模式）；
    // - 值块编织扩展：LoweredSeqBlock 与 LoweredBlock 同构透明；
    //   LoweredTryStatement 无 finally 时同 if 规则编织，有 finally 且
    //   部分分支含值块写入/终止且 continuation 非空时拦截（P4 Error，
    //   S7e 技术债——BIL 的 finally 全路径执行后必落到 continuation，
    //   无法表达「终止路径跳过 continuation」），finally 自身终止时
    //   continuation 全丢弃。
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
            private int breakIdCount;
            private readonly Stack<List<LoweredStatement>> outputStack =
                new Stack<List<LoweredStatement>>();
            private readonly Stack<(BoundValueBlock Block, LocalSymbol Target)> valueBlocks =
                new Stack<(BoundValueBlock, LocalSymbol)>();
            // 循环映射栈（S7c-1）：BoundLoop → 合成 .breakid 局部（引用相等
            // 查找），进循环压栈、出循环弹栈；BoundLoopControl 经 Target
            // 引用查映射得 BreakId（仿值块映射栈模式）
            private readonly Stack<(BoundLoop Loop, LocalSymbol BreakId)> loops =
                new Stack<(BoundLoop, LocalSymbol)>();
            // switch pattern 占位映射栈（S7d）：pattern 降级期间所属 switch 的
            // selector 表达式 → selector 物化局部（引用相等查找，嵌套 switch
            // 逐层向内命中）；BoundSwitchPlaceholderExpression 经 Selector
            // 引用查栈得读取目标
            private readonly Stack<(BoundExpression Selector, LocalSymbol Temp)> switchTemps =
                new Stack<(BoundExpression, LocalSymbol)>();
            // 编织拦截失败标记（S7e）：try+finally 部分终止编织拦截在
            // TransformWithContinuation 深处触发（void 链路无法返回值传播），
            // 置位后 LowerValueBlock 放弃产物——诊断已落袋，函数体跳过
            private bool transformFailed;

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
                breakIdCount = 0;
                valueBlocks.Clear();    // 函数体互不嵌套，防御性清空
                loops.Clear();
                switchTemps.Clear();
                transformFailed = false;
                var lowered = LowerBlock(body.Body);
                if (lowered == null) return null;
                // 合成局部（.s/.b 前缀，脱糖产物）跟在源码局部之后
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

            // 合成 .breakid 局部（S7c-1，BIL §9.3 capability）：.bN 命名，
            // 函数内唯一；Type 为 null（无对应 TypeSymbol，见 LocalSymbol
            // 注释），emitter 侧 .vars 条目按 .breakid 投影
            private LocalSymbol NewBreakIdLocal()
            {
                var local = new LocalSymbol(".b" + breakIdCount, null, isConst: false);
                breakIdCount++;
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
                        // 实例 void 调用（S7c-2）：receiver 恒等降级
                        LoweredExpression? callReceiver = null;
                        if (call.Receiver != null)
                        {
                            callReceiver = LowerExpression(call.Receiver);
                            if (callReceiver == null) return null;
                        }
                        return new LoweredCallStatement(call, call.Method, arguments, callReceiver);
                    case BoundLoop loop:
                        return loop.Kind == LoopKind.For ? LowerForLoop(loop) : LowerLoop(loop);
                    case BoundSwitchStatement switchStatement:
                        return LowerSwitchStatement(switchStatement);
                    case BoundTryStatement tryStatement:
                        return LowerTry(tryStatement);
                    case BoundSeqStatement seqStatement:
                        // seq 语句恒等降级（BIL §3.4 独立 block + call 化）
                        var seqBody = LowerBlock(seqStatement.Body);
                        if (seqBody == null) return null;
                        return new LoweredSeqBlock(seqStatement, seqBody, seqStatement.IsVolatile);
                    case BoundThrowStatement throwStatement:
                        // throw 恒等降级（BIL §16.9 直接对应）
                        var thrown = LowerExpression(throwStatement.Exception);
                        if (thrown == null) return null;
                        return new LoweredThrowStatement(throwStatement, thrown);
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
                    case BoundLoopControl loopControl:
                        // break/continue 是 BIL 真跳转（§16.5），直接携带目标
                        // 循环的 breakid——穿透值块/嵌套块无需任何展开；
                        // 其后语句在 BIL 块内自然不可达（无需 if 转换介入）
                        return new LoweredLoopControl(loopControl, loopControl.IsBreak,
                            FindLoopBreakId(loopControl.Target));
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

            // 循环降级（S7c-1，BIL §16.3/§16.4）：合成 bool 条件局部（.sN）
            // 与 .breakid 局部（.bN）；条件表达式在独立块上下文降级并写条件
            // 局部，产物即 Judge 块（前置语句随块走——条件内短路/if 表达式
            // 的展开自然落在 Judge 内）；映射压栈后降级 Body（体内
            // break/continue 经引用命中本循环）；Enumerator 恒 null（for
            // 的枚举器块随 S7c-2）。仅 While/DoWhile 路径——Condition 恒
            // 非空（For 走 LowerForLoop 脱糖，不经此）
            private LoweredStatement? LowerLoop(BoundLoop loop)
            {
                var condition = NewSynthLocal(loop.Condition!.Type);
                var breakId = NewBreakIdLocal();
                var judge = LowerAssignInNewBlock(loop, loop.Condition!, condition);
                if (judge == null) return null;
                loops.Push((loop, breakId));
                LoweredBlock? body;
                try
                {
                    body = LowerBlock(loop.Body);
                }
                finally
                {
                    loops.Pop();
                }
                if (body == null) return null;
                return new LoweredLoop(loop, loop.Kind == LoopKind.DoWhile,
                    judge, condition, body, enumerator: null, breakId);
            }

            // BoundLoopControl.Target 经引用查循环映射栈得 BreakId；未命中
            // 即内部错误（P3 已保证目标循环包含该语句，降级上下文必在栈上）
            private LocalSymbol FindLoopBreakId(BoundLoop target)
            {
                foreach (var (loop, breakId) in loops)
                {
                    if (ReferenceEquals(loop, target)) return breakId;
                }
                throw new CompilerInternalException(
                    "break/continue 目标循环不在降级上下文内（P3 已保证目标包含语句）");
            }

            // for 脱糖（S7c-2，SYNTAX §7.3 for-each 协议）：
            //   前置（当前块）：.e = <iterable 降级>.iterate()
            //   LoweredLoop{ IsRev=false,
            //     Judge = [ .c = .e.moveNext() ]（条件局部 .c 为合成 bool），
            //     Body = [ LoopVariable = .e.current(); <体降级> ],
            //     Enumerator = null, BreakId = .bN }
            // 产物复用 LoweredLoop——P4b 零新增。协议三方法符号与元素类型
            // 取 P3 挂在 BoundLoop 上的产物（P4 不做名字分析）；枚举器局部
            // 类型 = GetConstructedType(IEnumerator 定义, TItem)——定义经
            // MoveNextMethod.Owner 取（接口方法宿主编译期即定）
            private LoweredStatement? LowerForLoop(BoundLoop loop)
            {
                // P3 已保证 For 路径字段齐备（BoundLoop 注释的形态互斥约定）
                var loopVariable = loop.LoopVariable!;
                var itemType = loopVariable.Type!;
                var enumeratorDef = (TypeSymbol)loop.MoveNextMethod!.Owner!;
                var enumeratorType = unit.Symbols.GetConstructedType(enumeratorDef, itemType);
                var moveNextType = (TypeSymbol)loop.MoveNextMethod.ReturnType!;
                var enumerator = NewSynthLocal(enumeratorType);
                var condition = NewSynthLocal(moveNextType);
                var breakId = NewBreakIdLocal();
                // 前置：.e = <iterable>.iterate()（合成节点 Origin 指 for 语句）
                var iterable = LowerExpression(loop.Iterable!);
                if (iterable == null) return null;
                outputStack.Peek().Add(new LoweredAssignmentStatement(loop,
                    ReferenceTo(loop, enumerator),
                    new LoweredInstanceCallExpression(loop, iterable, loop.IterateMethod!,
                        new List<LoweredExpression>(), enumeratorType)));
                // Judge：.c = .e.moveNext()
                var judge = new LoweredBlock(loop, new List<LoweredStatement>
                {
                    new LoweredAssignmentStatement(loop, ReferenceTo(loop, condition),
                        new LoweredInstanceCallExpression(loop, ReferenceTo(loop, enumerator),
                            loop.MoveNextMethod, new List<LoweredExpression>(), moveNextType)),
                });
                // Body：头 = LoopVariable = .e.current()，其后体降级语句
                loops.Push((loop, breakId));
                LoweredBlock? body;
                try
                {
                    body = LowerBlock(loop.Body);
                }
                finally
                {
                    loops.Pop();
                }
                if (body == null) return null;
                var bodyStatements = new List<LoweredStatement>
                {
                    new LoweredAssignmentStatement(loop, ReferenceTo(loop, loopVariable),
                        new LoweredInstanceCallExpression(loop, ReferenceTo(loop, enumerator),
                            loop.CurrentMethod!, new List<LoweredExpression>(), itemType)),
                };
                bodyStatements.AddRange(body.Statements);
                return new LoweredLoop(loop, isRev: false, judge, condition,
                    new LoweredBlock(loop.Body, bodyStatements), enumerator: null, breakId);
            }

            // ===== switch 降级（S7d，SYNTAX §7.2；BIL §16.6）=====

            // switch 语句：分支体恒等降级（LowerBlock），汇合进共用核心
            private LoweredStatement? LowerSwitchStatement(BoundSwitchStatement switchStatement)
            {
                var cases = new List<(BoundNode Origin, BoundExpression Match, bool IsPattern,
                    LoweredBlock Body)>();
                foreach (var boundCase in switchStatement.Cases)
                {
                    var body = LowerBlock(boundCase.Body);
                    if (body == null) return null;
                    cases.Add((boundCase, boundCase.Match, boundCase.IsPattern, body));
                }
                var defaultBody = LowerBlock(switchStatement.DefaultBody);
                if (defaultBody == null) return null;
                return LowerSwitchCore(switchStatement, switchStatement.Selector, cases, defaultBody);
            }

            // switch 表达式：合成结果局部；各分支值块降级写结果局部（复用
            // 值块映射栈与 if 转换）；前置 switch/if 链语句，表达式位结果局部引用
            private LoweredExpression? LowerSwitchExpression(BoundSwitchExpression switchExpression)
            {
                var result = NewSynthLocal(switchExpression.Type);
                var cases = new List<(BoundNode Origin, BoundExpression Match, bool IsPattern,
                    LoweredBlock Body)>();
                foreach (var boundCase in switchExpression.Cases)
                {
                    var body = LowerValueBlock(boundCase.Body, result);
                    if (body == null) return null;
                    cases.Add((boundCase, boundCase.Match, boundCase.IsPattern, body));
                }
                var defaultBody = LowerValueBlock(switchExpression.DefaultBody, result);
                if (defaultBody == null) return null;
                var statement = LowerSwitchCore(switchExpression, switchExpression.Selector,
                    cases, defaultBody);
                if (statement == null) return null;
                outputStack.Peek().Add(statement);
                return ReferenceTo(switchExpression, result);
            }

            // switch 降级共用核心（两形态汇合）：分支体已按形态预先降级。
            // 全值匹配 → LoweredSwitch（§16.6 指令 + 常量表；selector 只求值
            // 一次——发射期经临时变量物化）；任一 case 为 pattern →
            // LowerPatternSwitch 嵌套 if 链（§16.6：含 _ 的 pattern 分支
            // 不能进常量表）
            private LoweredStatement? LowerSwitchCore(BoundNode origin, BoundExpression selector,
                IReadOnlyList<(BoundNode Origin, BoundExpression Match, bool IsPattern,
                    LoweredBlock Body)> cases,
                LoweredBlock defaultBody)
            {
                if (cases.Any(c => c.IsPattern))
                {
                    return LowerPatternSwitch(origin, selector, cases, defaultBody);
                }
                var loweredSelector = LowerExpression(selector);
                if (loweredSelector == null) return null;
                var breakId = NewBreakIdLocal();
                var loweredCases = new List<LoweredSwitchCase>();
                foreach (var (caseOrigin, match, _, body) in cases)
                {
                    // 值匹配分支：P3 已限定编译期常量（BoundLiteralExpression），
                    // 恒等降级无前置语句
                    var value = LowerExpression(match);
                    if (value == null) return null;
                    loweredCases.Add(new LoweredSwitchCase(caseOrigin, value, body));
                }
                return new LoweredSwitch(origin, loweredSelector, loweredCases, defaultBody, breakId);
            }

            // pattern 降级（§16.6 规则）：selector 先求值进合成局部 .sN
            // （前置语句，全 switch 只求值一次——占位 _ 即读该局部）；随后按
            // case 顺序构造嵌套 if 链（值匹配分支条件 = 合成 cmp.eq(.sN, 常量)，
            // pattern 分支条件 = 占位映射开启下的表达式降级），default 落最内
            // 层 else。首个命中胜出，与 §16.6 表序语义一致
            private LoweredStatement? LowerPatternSwitch(BoundNode origin, BoundExpression selector,
                IReadOnlyList<(BoundNode Origin, BoundExpression Match, bool IsPattern,
                    LoweredBlock Body)> cases,
                LoweredBlock defaultBody)
            {
                var selectorTemp = NewSynthLocal(selector.Type);
                var loweredSelector = LowerExpression(selector);
                if (loweredSelector == null) return null;
                outputStack.Peek().Add(new LoweredAssignmentStatement(origin,
                    ReferenceTo(origin, selectorTemp), loweredSelector));
                switchTemps.Push((selector, selectorTemp));
                try
                {
                    return BuildPatternChain(origin, cases, 0, selectorTemp, defaultBody);
                }
                finally
                {
                    switchTemps.Pop();
                }
            }

            // 嵌套 if 链构造（就地递归；每层在独立块上下文降级条件——条件内
            // 短路/if 表达式的前置语句自然落在该 if 所属块内，仿 else-if 链形态）
            private LoweredBlock? BuildPatternChain(BoundNode origin,
                IReadOnlyList<(BoundNode Origin, BoundExpression Match, bool IsPattern,
                    LoweredBlock Body)> cases,
                int index, LocalSymbol selectorTemp, LoweredBlock defaultBody)
            {
                var statements = new List<LoweredStatement>();
                outputStack.Push(statements);
                try
                {
                    var (caseOrigin, match, isPattern, body) = cases[index];
                    LoweredExpression? condition;
                    if (isPattern)
                    {
                        // 占位映射已由 LowerPatternSwitch 压栈，_ 读 selector 局部
                        condition = LowerExpression(match);
                    }
                    else
                    {
                        var constant = LowerExpression(match);
                        if (constant == null) return null;
                        condition = new LoweredBinaryExpression(match, BilIntrinsicOp.CmpEq,
                            ReferenceTo(match, selectorTemp), constant,
                            unit.Symbols.Bootstrap.Bool);
                    }
                    if (condition == null) return null;
                    LoweredBlock elseBlock;
                    if (index + 1 < cases.Count)
                    {
                        var nested = BuildPatternChain(origin, cases, index + 1,
                            selectorTemp, defaultBody);
                        if (nested == null) return null;
                        elseBlock = nested;
                    }
                    else
                    {
                        elseBlock = defaultBody;
                    }
                    statements.Add(new LoweredIfStatement(caseOrigin, condition, body, elseBlock));
                    return new LoweredBlock(origin, statements);
                }
                finally
                {
                    outputStack.Pop();
                }
            }

            // BoundSwitchPlaceholderExpression.Selector 经引用查占位映射栈得
            // selector 物化局部；未命中即内部错误（P3 已保证 _ 只在 pattern
            // 匹配表达式内，降级上下文必在栈上）
            private LocalSymbol FindSwitchTemp(BoundExpression selector)
            {
                foreach (var (boundSelector, temp) in switchTemps)
                {
                    if (ReferenceEquals(boundSelector, selector)) return temp;
                }
                throw new CompilerInternalException(
                    "switch 占位 _ 不在 pattern 降级上下文内（P3 已保证只在 case 匹配表达式内）");
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

            // ===== try 降级（S7e，SYNTAX §8；BIL §16.7）=====

            // try → LoweredTryStatement（指令直接对应）：
            // - ExceptionSlot（$slot 操作数承载局部）：finally(e) 的 e 非空时
            //   即该局部（指令直写——e 的「无异常为 null」语义即指令写 slot
            //   语义），否则合成 .sN（类型 Nullable<core.Exception>）；
            // - 有名 catch：体头合成「变量 = cast slot」（Origin 指
            //   BoundCatchClause）——slot 的 Nullable<core.Exception> 到
            //   catch 类型的收窄走显式 cast（BIL §12.1），P3 已查兼容；
            // - 三分支体各自恒等降级（前置语句随块走）
            private LoweredStatement? LowerTry(BoundTryStatement tryStatement)
            {
                var exceptionSlot = tryStatement.FinallyVariable
                    ?? NewSynthLocal(unit.Symbols.GetNullable(unit.Symbols.Bootstrap.Exception));
                var tryBlock = LowerBlock(tryStatement.TryBlock);
                if (tryBlock == null) return null;
                var catches = new List<LoweredTryCatch>();
                foreach (var boundCatch in tryStatement.Catches)
                {
                    var body = LowerBlock(boundCatch.Body);
                    if (body == null) return null;
                    if (boundCatch.Variable != null)
                    {
                        var castAssign = new LoweredAssignmentStatement(boundCatch,
                            ReferenceTo(boundCatch, boundCatch.Variable),
                            new LoweredCastExpression(boundCatch,
                                ReferenceTo(boundCatch, exceptionSlot),
                                boundCatch.ExceptionType, isSafe: false,
                                boundCatch.ExceptionType));
                        body = new LoweredBlock(body.Origin,
                            new List<LoweredStatement> { castAssign }
                                .Concat(body.Statements).ToList());
                    }
                    catches.Add(new LoweredTryCatch(boundCatch, boundCatch.Variable,
                        boundCatch.ExceptionType, body));
                }
                LoweredBlock? finallyBlock = null;
                if (tryStatement.FinallyBlock != null)
                {
                    finallyBlock = LowerBlock(tryStatement.FinallyBlock);
                    if (finallyBlock == null) return null;
                }
                return new LoweredTryStatement(tryStatement, tryBlock, catches, finallyBlock,
                    exceptionSlot);
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
                        // 编织拦截（S7e try+finally 部分终止）：诊断已落袋，放弃产物
                        if (transformFailed) return null;
                    }
                    return new LoweredBlock(valueBlock.Block, statements);
                }
                finally
                {
                    valueBlocks.Pop();
                    outputStack.Pop();
                }
            }

            // 值块语句序列的 if/switch 转换（就地；分支块不可变，故新建节点
            // 替换原位置）。本质是 continuation 编织（S7d 重写，修复 M46
            // else-if 链缺陷）：BIL 的 if/switch 分支块执行完必回到结构指令的
            // 下一条指令（§16.2/§16.6），而值块 return@ 语义是「写目标局部 +
            // 本路径不再执行后续」——故把「其后语句」（continuation）编织进
            // 每个会落到块尾的路径末端；终止于值块写入/throw 的路径丢弃
            // continuation。规则（对每条结构语句）：
            // - LoweredIfStatement/LoweredSwitch：其后语句序列 + 块外
            //   continuation 织入每个不终止分支末端（无 else 且需要时新建
            //   else 块），结构语句成为块内最后一条；全部分支终止时其后
            //   语句全丢弃（不可达）；
            // - 嵌套 LoweredBlock：同规则（continuation 织入块内）；
            // - 顶层值块写入：同块其后语句不可达（死代码，P3 已保证路径
            //   必终止），截断且不接收 continuation；
            // - 全无终止路径的结构语句不编织（continuation 留原位，保形）。
            // 「值块写入」判定：赋值目标引用值块映射栈中的目标局部（合成
            // 局部 .sN 只被值块写入与短路/if/switch 表达式结果使用——后
            // 两者不在映射栈上，互不混淆）。continuation 织入多个分支时
            // 语句节点对象共享（Lowered 节点无父链、不可变，发射期各分支
            // 块独立展开为指令文本）。
            // 例：{ if (c) { return@_ 1 }  x = 2  return@_ x }
            //   ⇒ { if (c) { v = 1 } else { x = 2; v = x } }
            private void TransformStatements(List<LoweredStatement> statements)
            {
                TransformWithContinuation(statements, new List<LoweredStatement>());
            }

            // continuation = 本块结束后要执行的语句序列（块外 continuation；
            // 就地编织，见 TransformStatements 注释）
            private void TransformWithContinuation(List<LoweredStatement> statements,
                List<LoweredStatement> continuation)
            {
                for (int i = 0; i < statements.Count; i++)
                {
                    switch (statements[i])
                    {
                        case LoweredAssignmentStatement write when IsValueBlockWrite(write):
                            // 值块写入终止本路径：同块其后语句不可达，截断；
                            // continuation 同样不可达，不接收
                            statements.RemoveRange(i + 1, statements.Count - i - 1);
                            return;
                        case LoweredThrowStatement:
                            // throw 终止本路径（BIL §16.9 真终止）：同截断
                            statements.RemoveRange(i + 1, statements.Count - i - 1);
                            return;
                        case LoweredIfStatement ifStatement:
                        {
                            if (!HasTerminatingPath(ifStatement.TrueBlock)
                                && (ifStatement.FalseBlock == null
                                    || !HasTerminatingPath(ifStatement.FalseBlock)))
                            {
                                break;    // 无终止路径：continuation 留原位（保形）
                            }
                            var rest = statements.GetRange(i + 1, statements.Count - i - 1);
                            statements.RemoveRange(i + 1, statements.Count - i - 1);
                            rest.AddRange(continuation);
                            var trueBlock = BlockTerminates(ifStatement.TrueBlock)
                                ? WeaveContinuation(ifStatement.TrueBlock, new List<LoweredStatement>())
                                : WeaveContinuation(ifStatement.TrueBlock, rest);
                            LoweredBlock? falseBlock;
                            if (ifStatement.FalseBlock != null)
                            {
                                falseBlock = BlockTerminates(ifStatement.FalseBlock)
                                    ? WeaveContinuation(ifStatement.FalseBlock,
                                        new List<LoweredStatement>())
                                    : WeaveContinuation(ifStatement.FalseBlock, rest);
                            }
                            else
                            {
                                // 无 else：不终止的 false 路径需要 continuation 时新建 else 块
                                falseBlock = rest.Count > 0
                                    ? WeaveContinuation(
                                        new LoweredBlock(ifStatement.Origin,
                                            new List<LoweredStatement>()), rest)
                                    : null;
                            }
                            statements[i] = new LoweredIfStatement(ifStatement.Origin,
                                ifStatement.Condition, trueBlock, falseBlock);
                            return;    // 结构语句成为块内最后一条
                        }
                        case LoweredSwitch switchStatement:
                        {
                            if (!switchStatement.Cases.Any(c => HasTerminatingPath(c.Body))
                                && !HasTerminatingPath(switchStatement.DefaultBody))
                            {
                                break;    // 无终止路径：continuation 留原位（保形）
                            }
                            var rest = statements.GetRange(i + 1, statements.Count - i - 1);
                            statements.RemoveRange(i + 1, statements.Count - i - 1);
                            rest.AddRange(continuation);
                            var cases = switchStatement.Cases
                                .Select(c => new LoweredSwitchCase(c.Origin, c.Value,
                                    BlockTerminates(c.Body)
                                        ? WeaveContinuation(c.Body, new List<LoweredStatement>())
                                        : WeaveContinuation(c.Body, rest)))
                                .ToList();
                            var defaultBody = BlockTerminates(switchStatement.DefaultBody)
                                ? WeaveContinuation(switchStatement.DefaultBody,
                                    new List<LoweredStatement>())
                                : WeaveContinuation(switchStatement.DefaultBody, rest);
                            statements[i] = new LoweredSwitch(switchStatement.Origin,
                                switchStatement.Selector, cases, defaultBody,
                                switchStatement.BreakId);
                            return;
                        }
                        case LoweredBlock nested:
                        {
                            if (!HasTerminatingPath(nested))
                            {
                                break;    // 无终止路径：continuation 留原位（保形）
                            }
                            var rest = statements.GetRange(i + 1, statements.Count - i - 1);
                            statements.RemoveRange(i + 1, statements.Count - i - 1);
                            rest.AddRange(continuation);
                            statements[i] = WeaveContinuation(nested, rest);
                            return;
                        }
                        case LoweredSeqBlock seqBlock:
                        {
                            // seq 块与 LoweredBlock 同构透明（S7e）：体编织后重建
                            if (!HasTerminatingPath(seqBlock.Body))
                            {
                                break;    // 无终止路径：continuation 留原位（保形）
                            }
                            var rest = statements.GetRange(i + 1, statements.Count - i - 1);
                            statements.RemoveRange(i + 1, statements.Count - i - 1);
                            rest.AddRange(continuation);
                            statements[i] = new LoweredSeqBlock(seqBlock.Origin,
                                WeaveContinuation(seqBlock.Body, rest), seqBlock.IsVolatile);
                            return;
                        }
                        case LoweredTryStatement tryStatement:
                        {
                            // try 编织（S7e 定稿规则）：
                            // - finally 自身终止（含值块写入/throw，或末语句
                            //   ret——BIL 的 finally 全路径执行，其终止覆盖
                            //   所有路径）→ continuation 全丢弃；
                            // - 有 finally 且 try/catch 分支含终止路径且
                            //   continuation 非空 → 拦截（P4 Error，S7e
                            //   技术债）：BIL 的 finally 执行后必落到
                            //   continuation，无法表达「终止路径跳过
                            //   continuation」；
                            // - 其余（无 finally 或 continuation 为空）→
                            //   同 if 规则编织进每个不终止分支末端
                            var branchTerminates = HasTerminatingPath(tryStatement.TryBlock)
                                || tryStatement.Catches.Any(c => HasTerminatingPath(c.Body));
                            var finallyTerminates = tryStatement.FinallyBlock != null
                                && (HasTerminatingPath(tryStatement.FinallyBlock)
                                    || tryStatement.FinallyBlock.Statements.Count > 0
                                    && tryStatement.FinallyBlock.Statements[^1]
                                        is LoweredReturnStatement);
                            if (!branchTerminates && !finallyTerminates)
                            {
                                break;    // 无终止路径：continuation 留原位（保形）
                            }
                            var rest = statements.GetRange(i + 1, statements.Count - i - 1);
                            statements.RemoveRange(i + 1, statements.Count - i - 1);
                            rest.AddRange(continuation);
                            if (finallyTerminates)
                            {
                                // finally 终止覆盖：continuation 全丢弃，节点原样保留
                                return;
                            }
                            if (tryStatement.FinallyBlock != null && rest.Count > 0)
                            {
                                Error(tryStatement.Origin.Syntax.Span,
                                    "P4: value block weaving across try-finally with " +
                                    "partial termination is not supported yet (S7e)");
                                transformFailed = true;
                                return;
                            }
                            var newTryBlock = BlockTerminates(tryStatement.TryBlock)
                                ? WeaveContinuation(tryStatement.TryBlock,
                                    new List<LoweredStatement>())
                                : WeaveContinuation(tryStatement.TryBlock, rest);
                            var newCatches = tryStatement.Catches
                                .Select(c => new LoweredTryCatch(c.Origin, c.Variable,
                                    c.ExceptionType,
                                    BlockTerminates(c.Body)
                                        ? WeaveContinuation(c.Body, new List<LoweredStatement>())
                                        : WeaveContinuation(c.Body, rest)))
                                .ToList();
                            statements[i] = new LoweredTryStatement(tryStatement.Origin,
                                newTryBlock, newCatches, tryStatement.FinallyBlock,
                                tryStatement.ExceptionSlot);
                            return;
                        }
                    }
                }
                // 块内无待编织结构：continuation 原样接到块尾
                statements.AddRange(continuation);
            }

            // 把 continuation 织入块内（递归转换，产物为新建块）
            private LoweredBlock WeaveContinuation(LoweredBlock block,
                List<LoweredStatement> continuation)
            {
                var statements = new List<LoweredStatement>(block.Statements);
                TransformWithContinuation(statements, continuation);
                return new LoweredBlock(block.Origin, statements);
            }

            // 「块内所有路径都不会落到块尾」判定：末语句是值块写入/throw →
            // true；末语句是双分支都终止的 LoweredIfStatement / 全部分支体
            // （含 default）都终止的 LoweredSwitch → true；末语句是嵌套
            // LoweredBlock → 递归；其余 false（LoweredReturnStatement 函数
            // 返回与 LoweredLoopControl 真跳转不参与——其后语句在 BIL 块内
            // 本就不可达，无需编织介入）
            private bool BlockTerminates(LoweredBlock block)
            {
                if (block.Statements.Count == 0) return false;
                return block.Statements[^1] switch
                {
                    LoweredAssignmentStatement assignment => IsValueBlockWrite(assignment),
                    LoweredThrowStatement => true,
                    LoweredIfStatement ifStatement => ifStatement.FalseBlock != null
                        && BlockTerminates(ifStatement.TrueBlock)
                        && BlockTerminates(ifStatement.FalseBlock),
                    LoweredSwitch switchStatement =>
                        switchStatement.Cases.All(c => BlockTerminates(c.Body))
                        && BlockTerminates(switchStatement.DefaultBody),
                    // S7e：seq 透明递归；try——finally 终止覆盖所有路径，
                    // 否则 try 与全部 catch 都终止（未捕获异常穿透即终止，
                    // 无 catch 时只剩 try 正常完成路径——单块判定）
                    LoweredSeqBlock seqBlock => BlockTerminates(seqBlock.Body),
                    LoweredTryStatement tryStatement =>
                        (tryStatement.FinallyBlock != null
                            && BlockTerminates(tryStatement.FinallyBlock))
                        || (BlockTerminates(tryStatement.TryBlock)
                            && tryStatement.Catches.All(c => BlockTerminates(c.Body))),
                    LoweredBlock nested => BlockTerminates(nested),
                    _ => false,
                };
            }

            // 「块内含不落到块尾的路径」判定（编织必要性闸门）：值块写入或
            // throw 出现即 true（递归 if/switch/嵌套块/seq/try 三分支——
            // S7e）；全无终止路径的结构语句无需编织，保持旧形态
            private bool HasTerminatingPath(LoweredBlock block)
            {
                foreach (var statement in block.Statements)
                {
                    switch (statement)
                    {
                        case LoweredAssignmentStatement assignment:
                            if (IsValueBlockWrite(assignment)) return true;
                            break;
                        case LoweredThrowStatement:
                            return true;
                        case LoweredIfStatement ifStatement:
                            if (HasTerminatingPath(ifStatement.TrueBlock)
                                || (ifStatement.FalseBlock != null
                                    && HasTerminatingPath(ifStatement.FalseBlock)))
                            {
                                return true;
                            }
                            break;
                        case LoweredSwitch switchStatement:
                            if (switchStatement.Cases.Any(c => HasTerminatingPath(c.Body))
                                || HasTerminatingPath(switchStatement.DefaultBody))
                            {
                                return true;
                            }
                            break;
                        case LoweredBlock nested:
                            if (HasTerminatingPath(nested)) return true;
                            break;
                        case LoweredSeqBlock seqBlock:
                            if (HasTerminatingPath(seqBlock.Body)) return true;
                            break;
                        case LoweredTryStatement tryStatement:
                            if (HasTerminatingPath(tryStatement.TryBlock)
                                || tryStatement.Catches.Any(c => HasTerminatingPath(c.Body))
                                || (tryStatement.FinallyBlock != null
                                    && HasTerminatingPath(tryStatement.FinallyBlock)))
                            {
                                return true;
                            }
                            break;
                    }
                }
                return false;
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
                    case BoundSwitchExpression switchExpression:
                        return LowerSwitchExpression(switchExpression);
                    case BoundSwitchPlaceholderExpression placeholder:
                        // pattern 降级已把 selector 物化为合成局部（selector 全
                        // switch 只求值一次），占位即读该局部；目标经 Selector
                        // 引用查映射栈（嵌套 switch 消歧）
                        return ReferenceTo(placeholder, FindSwitchTemp(placeholder.Selector));
                    case BoundCompoundAssignmentExpression compound:
                        return LowerCompoundAssignment(compound);
                    case BoundThisExpression thisExpression:
                        return new LoweredThisExpression(thisExpression);
                    case BoundInstanceCallExpression instanceCall:
                        var instanceReceiver = LowerExpression(instanceCall.Receiver);
                        if (instanceReceiver == null) return null;
                        var instanceArguments = LowerArguments(instanceCall.Arguments);
                        if (instanceArguments == null) return null;
                        return new LoweredInstanceCallExpression(instanceCall, instanceReceiver,
                            instanceCall.Method, instanceArguments, instanceCall.Type);
                    case BoundFieldAccessExpression fieldAccess:
                        var accessReceiver = LowerExpression(fieldAccess.Receiver);
                        if (accessReceiver == null) return null;
                        return new LoweredFieldAccessExpression(fieldAccess, accessReceiver,
                            fieldAccess.Field);
                    case BoundCastExpression cast:
                        // cast 恒等降级（BIL §12.1/§12.2 直接对应；as? 的
                        // Nullable 包装已在 P3 定型进 Type）
                        var castSource = LowerExpression(cast.Source);
                        if (castSource == null) return null;
                        return new LoweredCastExpression(cast, castSource, cast.TargetType,
                            cast.IsSafe, cast.Type);
                    case BoundSeqExpression seqExpression:
                        return LowerSeqExpression(seqExpression);
                    default:
                        Unsupported(expression);
                        return null;
                }
            }

            // seq 表达式脱糖（S7e，BIL §3.4 call 化）：合成结果局部 v；
            // 前置 LoweredSeqBlock（值块降级产物写 v，volatile 随值块置位）；
            // 表达式位 v 引用——与 if 表达式同构，差异仅在 seq 块发射形态
            // （独立 block + call 指令）
            private LoweredExpression? LowerSeqExpression(BoundSeqExpression seqExpression)
            {
                var result = NewSynthLocal(seqExpression.Type);
                var body = LowerValueBlock(seqExpression.Body, result);
                if (body == null) return null;
                outputStack.Peek().Add(new LoweredSeqBlock(seqExpression, body,
                    seqExpression.Body.IsVolatile));
                return ReferenceTo(seqExpression, result);
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
