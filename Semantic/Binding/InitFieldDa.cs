namespace RigiCompiler
{
    // init 字段定值赋值分析（P18/S2，SYNTAX §9.3「DA 规则」）：所有实体
    //（值类型与对象同规则）的实例字段声明后默认视为未赋值；非 Nullable
    // 字段必须三选一——声明初始值（编译器合成的 ..init.field.*，构造进入
    // 时已赋值）、init 内显式赋值（`init(_ -> x)` 映射算赋值）、字段类型
    // 为 Nullable。循环出口取全部出环路径交集：体赋值只在正常落到底计入；
    // break/break@label 出环点与其它出口（含 while/for 的循环前态）取交；
    // do-while 无 break 仍取体尾。检查全部是前端静态检查（无 VM 哨兵），检查点：
    //   1. 每个显式 init（含有体/无体映射形态）的每条路径出口——块尾与
    //      中途裸 return；super(...) 被调用时基类闭包字段由基类 init 担保，
    //      义务收窄为本类声明字段；不调 super 时基类无初始值非空字段计入
    //      本 init 义务（子类可直接给可见的基类字段赋值，赋不了的在诊断里
    //      引导调 super）；
    //   2. 合成默认构造（BindingDriver 阶段 1.8）按集合直接判定；
    //   3. 无 init 类型的零参 new 使用点（NewVisitor）——类型从未声明
    //      init 时声明点不报错，构造点才报；
    //   4. 无 init enum struct 的固定 case 模板（BindingDriver 阶段 1.5）。
    // 数组元素不做 DA（native 魔法，getAtIndex 返回 T?）；抽象类自身不
    // 可构造，其无初始值非空字段的义务转移给具体子类的 init。
    internal static class InitFieldDa
    {
        // 类型的 DA 义务字段集（定义级 FieldSymbol 身份，引用相等）：
        // 继承闭包内「非内建宿主声明的、有存储的、非 Nullable 的、无声明
        // 初始值的」实例字段。泛型参数类型的字段按非 Nullable 悲观计入
        //（T 可具化为非 Nullable；要豁免请显式写 T?）
        public static List<FieldSymbol> RequiredFields(TypeSymbol type, BindEnvironment env)
        {
            var result = new List<FieldSymbol>();
            var rootDef = type.ConstructedFrom ?? type;
            if (rootDef.IsBuiltin) return result;
            foreach (var (field, fieldType) in FieldClosureChecker.ClosureFieldsOf(type,
                env.Unit.Symbols))
            {
                if (IsObligation(field, fieldType, rootDef, env)) result.Add(field);
            }
            return result;
        }

        private static bool IsObligation(FieldSymbol field, SemanticSymbol? fieldType,
            TypeSymbol rootDef, BindEnvironment env)
        {
            if (field.IsStatic) return false;
            // ext 实例字段豁免（§4.4/§9.3 边界）：模块化附加槽——宿主 init
            // 声明先于扩展存在，不应被迫感知；读出语义按分配零值（文档化
            // 边界，与数组元素同型）
            if (field.ExtTargetPath != null) return false;
            if (field.Owner is not { } owner || owner.IsBuiltin) return false;
            // Exception.message：源码化后非 builtin，但 SYNTAX §8.1 规定未
            // 赋值时为零值（空字符串）；用户自定义异常子类不赋值 message
            // 仍合法，定点豁免本字段（不经 Bootstrap.Exception 懒解析，
            // 避免无 stdlib 的 DA 路径误触内部异常）
            if (field.Name == "message"
                && owner.Name == "Exception"
                && owner.GenericParameters.Count == 0
                && ReferenceEquals(owner.Namespace, env.B.Core))
            {
                return false;
            }
            // 计算属性无存储
            if ((field.Getter != null || field.Setter != null) && !field.HasBackingStorage)
            {
                return false;
            }
            // 仅 get 无 set 的访问器字段豁免（§9.3 边界）：规范禁止其携带
            // 初始值、无 set 通道即无任何赋值点——读出分配零值是其既定语义
            if (field.Getter != null && field.Setter == null) return false;
            // 毒化 / 无标注未推断（P3 推断失败已诊断）静默
            if (fieldType is null or ErrorTypeSymbol) return false;
            // Nullable 豁免（T? 即 Nullable\<T> 构造）
            if (fieldType is TypeSymbol t
                && ReferenceEquals(t.ConstructedFrom ?? t, env.B.NullableDefinition))
            {
                return false;
            }
            // 声明初始值：..init.field.<名> 沿继承闭包可查（字段 override 时
            // 虚派发选中最高派生实现，同一槽只写一次）
            if (WrapperInitSynthesis.HasFieldInitializerMethod(rootDef, field.Name))
            {
                return false;
            }
            return true;
        }

        // 义务集二分：本类声明 / 基类闭包（super 调用判定义务收窄用）
        public static bool IsOwnField(FieldSymbol field, TypeSymbol ownerDef)
        {
            return ReferenceEquals(field.Owner, ownerDef);
        }

        // ===== init 体流式检查（BindingDriver 阶段 2，体已含映射前导）=====

        public static void CheckInitBody(MethodSymbol init, BoundBlock body,
            CharRange? span, BindEnvironment env)
        {
            var owner = init.Owner;
            if (owner == null) return;
            var ownerDef = owner.ConstructedFrom ?? owner;
            if (ownerDef.IsBuiltin) return;
            var all = RequiredFields(owner, env);
            if (all.Count == 0) return;
            // super(...) 调用（顶层语句任一位置）→ 基类闭包字段由基类 init 担保
            var callsSuper = body.Statements.Any(static s =>
                s is BoundExpressionStatement { Expression: BoundSuperCallExpression });
            var obligations = all.Where(f => !callsSuper || IsOwnField(f, ownerDef)).ToList();
            if (obligations.Count == 0) return;
            var context = new CheckContext(init, ownerDef, obligations,
                callsSuper, span, env);
            var path = new PathState(new HashSet<FieldSymbol>(ReferenceEqualityComparer.Instance));
            AnalyzeBlock(body, path, context);
            // 块尾出口（能落尾的 init 路径；中途 return 已在出口点报过）
            if (path.FallsThrough) ReportMissing(path.Assigned, context);
        }

        private sealed class CheckContext
        {
            public MethodSymbol Init { get; }
            public TypeSymbol OwnerDef { get; }
            public List<FieldSymbol> Obligations { get; }
            public bool CallsSuper { get; }
            public CharRange? Span { get; }
            public BindEnvironment Env { get; }
            // 每字段每 init 只报一条（多个 return 出口命中同一字段不重复）
            public HashSet<FieldSymbol> Reported { get; } =
                new HashSet<FieldSymbol>(ReferenceEqualityComparer.Instance);

            public CheckContext(MethodSymbol init, TypeSymbol ownerDef,
                List<FieldSymbol> obligations, bool callsSuper, CharRange? span,
                BindEnvironment env)
            {
                Init = init;
                OwnerDef = ownerDef;
                Obligations = obligations;
                CallsSuper = callsSuper;
                Span = span;
                Env = env;
            }
        }

        private static void ReportMissing(HashSet<FieldSymbol> assigned, CheckContext context)
        {
            foreach (var field in context.Obligations)
            {
                if (assigned.Contains(field) || !context.Reported.Add(field)) continue;
                var baseHint = IsOwnField(field, context.OwnerDef)
                    ? ""
                    : ", or call super(...) to delegate base fields to a base init";
                context.Env.Error(context.Span,
                    $"Field '{field.Name}' of '{field.Owner?.Name ?? context.OwnerDef.Name}'" +
                    $" is not definitely assigned on all paths of this init of " +
                    $"'{context.OwnerDef.Name}' (§9.3: add a declaration " +
                    $"initializer, assign it in this init, or make the field Nullable" +
                    baseHint + ")");
            }
        }

        private static HashSet<FieldSymbol> CopyOf(HashSet<FieldSymbol> set)
        {
            return new HashSet<FieldSymbol>(set, ReferenceEqualityComparer.Instance);
        }

        // 一条路径的 DA 态：已赋值集 + 是否还能顺序落尾 + 尚未被目标循环
        // 消费的 break/continue 出环点快照。循环出口 = 全部出环路径交集：
        //   do-while 无 break：体尾（至少一次，保持既有口径）；
        //   while/for：循环前态（体可能零次）；
        //   break/break@label：出环点态（与其它出口取交；named break 穿透
        //   外层时内层体尾不计入外层出口）。
        private sealed class PathState
        {
            public HashSet<FieldSymbol> Assigned { get; }
            public bool FallsThrough { get; set; } = true;
            public bool InitExited { get; set; }
            public Dictionary<BoundLoop, List<HashSet<FieldSymbol>>> Breaks { get; } =
                new Dictionary<BoundLoop, List<HashSet<FieldSymbol>>>();
            public Dictionary<BoundLoop, List<HashSet<FieldSymbol>>> Continues { get; } =
                new Dictionary<BoundLoop, List<HashSet<FieldSymbol>>>();

            public PathState(HashSet<FieldSymbol> assigned)
            {
                Assigned = assigned;
            }

            public PathState Fork()
            {
                return new PathState(CopyOf(Assigned));
            }
        }

        private static void AnalyzeBlock(BoundBlock block, PathState path, CheckContext context)
        {
            foreach (var statement in block.Statements)
            {
                if (!path.FallsThrough) return;
                AnalyzeStatement(statement, path, context);
            }
        }

        private static HashSet<FieldSymbol> IntersectAll(List<HashSet<FieldSymbol>> sets)
        {
            var merged = CopyOf(sets[0]);
            for (var i = 1; i < sets.Count; i++) merged.IntersectWith(sets[i]);
            return merged;
        }

        private static void AbsorbJumps(PathState dest, PathState src)
        {
            AbsorbJumpMap(dest.Breaks, src.Breaks);
            AbsorbJumpMap(dest.Continues, src.Continues);
        }

        private static void AbsorbJumpMap(
            Dictionary<BoundLoop, List<HashSet<FieldSymbol>>> dest,
            Dictionary<BoundLoop, List<HashSet<FieldSymbol>>> src)
        {
            foreach (var pair in src)
            {
                if (!dest.TryGetValue(pair.Key, out var list))
                {
                    dest[pair.Key] = new List<HashSet<FieldSymbol>>(pair.Value);
                }
                else
                {
                    list.AddRange(pair.Value);
                }
            }
        }

        private static List<HashSet<FieldSymbol>> TakeJumps(
            Dictionary<BoundLoop, List<HashSet<FieldSymbol>>> map, BoundLoop loop)
        {
            return map.Remove(loop, out var list) ? list : new List<HashSet<FieldSymbol>>();
        }

        private static void RecordJump(
            Dictionary<BoundLoop, List<HashSet<FieldSymbol>>> map, BoundLoop loop,
            HashSet<FieldSymbol> assigned)
        {
            if (!map.TryGetValue(loop, out var list))
            {
                list = new List<HashSet<FieldSymbol>>();
                map[loop] = list;
            }
            list.Add(CopyOf(assigned));
        }

        private static void DelayJumpsThroughFinally(PathState jumps, BoundBlock finallyBlock,
            CheckContext context)
        {
            var extra = new PathState(new HashSet<FieldSymbol>(ReferenceEqualityComparer.Instance));
            DelayJumpMap(jumps.Breaks, finallyBlock, context, extra);
            DelayJumpMap(jumps.Continues, finallyBlock, context, extra);
            AbsorbJumps(jumps, extra);
        }

        private static void DelayJumpMap(
            Dictionary<BoundLoop, List<HashSet<FieldSymbol>>> map, BoundBlock finallyBlock,
            CheckContext context, PathState extra)
        {
            foreach (var loop in map.Keys.ToList())
            {
                var delayed = new List<HashSet<FieldSymbol>>();
                foreach (var snapshot in map[loop])
                {
                    var finallyPath = new PathState(CopyOf(snapshot));
                    AnalyzeBlock(finallyBlock, finallyPath, context);
                    if (finallyPath.FallsThrough) delayed.Add(CopyOf(finallyPath.Assigned));
                    AbsorbJumps(extra, finallyPath);
                }
                map[loop] = delayed;
            }
        }

        private static void MergeFallThrough(PathState path, List<HashSet<FieldSymbol>> falls)
        {
            if (falls.Count == 0)
            {
                path.FallsThrough = false;
                return;
            }
            var merged = IntersectAll(falls);
            path.Assigned.Clear();
            path.Assigned.UnionWith(merged);
        }

        private static void AnalyzeStatement(BoundStatement statement,
            PathState path, CheckContext context)
        {
            switch (statement)
            {
                case BoundAssignmentStatement assignment:
                    MarkTarget(assignment.Target, path.Assigned, context);
                    ScanExpression(assignment.Value, path.Assigned, context);
                    break;
                case BoundExpressionStatement expressionStatement:
                    ScanExpression(expressionStatement.Expression, path.Assigned, context);
                    break;
                case BoundIfStatement ifStatement:
                    ScanExpression(ifStatement.Condition, path.Assigned, context);
                    var truePath = path.Fork();
                    AnalyzeBlock(ifStatement.TrueBlock, truePath, context);
                    var falsePath = path.Fork();
                    if (ifStatement.FalseBlock != null)
                    {
                        AnalyzeBlock(ifStatement.FalseBlock, falsePath, context);
                    }
                    AbsorbJumps(path, truePath);
                    AbsorbJumps(path, falsePath);
                    if (truePath.InitExited) path.InitExited = true;
                    if (falsePath.InitExited) path.InitExited = true;
                    var ifFalls = new List<HashSet<FieldSymbol>>();
                    if (truePath.FallsThrough) ifFalls.Add(truePath.Assigned);
                    if (falsePath.FallsThrough) ifFalls.Add(falsePath.Assigned);
                    MergeFallThrough(path, ifFalls);
                    break;
                case BoundSwitchStatement switchStatement:
                    ScanExpression(switchStatement.Selector, path.Assigned, context);
                    var switchFalls = new List<HashSet<FieldSymbol>>();
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        var casePath = path.Fork();
                        AnalyzeBlock(switchCase.Body, casePath, context);
                        AbsorbJumps(path, casePath);
                        if (casePath.InitExited) path.InitExited = true;
                        if (casePath.FallsThrough) switchFalls.Add(casePath.Assigned);
                    }
                    var defaultPath = path.Fork();
                    AnalyzeBlock(switchStatement.DefaultBody, defaultPath, context);
                    AbsorbJumps(path, defaultPath);
                    if (defaultPath.InitExited) path.InitExited = true;
                    if (defaultPath.FallsThrough) switchFalls.Add(defaultPath.Assigned);
                    MergeFallThrough(path, switchFalls);
                    break;
                case BoundLoop loop:
                    var bodyPath = path.Fork();
                    AnalyzeBlock(loop.Body, bodyPath, context);
                    AbsorbJumps(path, bodyPath);
                    if (bodyPath.InitExited) path.InitExited = true;
                    var exits = new List<HashSet<FieldSymbol>>();
                    if (loop.Kind == LoopKind.DoWhile)
                    {
                        // 正常落到底 / continue 至条件：体至少一次，赋值计入
                        if (bodyPath.FallsThrough) exits.Add(CopyOf(bodyPath.Assigned));
                        exits.AddRange(TakeJumps(path.Continues, loop));
                    }
                    else
                    {
                        // while/for：零次路径 = 循环前态；continue 不另开出口
                        exits.Add(CopyOf(path.Assigned));
                        TakeJumps(path.Continues, loop);
                    }
                    // break/break@label 出环点与其它出口取交
                    exits.AddRange(TakeJumps(path.Breaks, loop));
                    MergeFallThrough(path, exits);
                    break;
                case BoundTryStatement tryStatement:
                    var tryPath = path.Fork();
                    AnalyzeBlock(tryStatement.TryBlock, tryPath, context);
                    var tryFalls = new List<HashSet<FieldSymbol>>();
                    if (tryPath.FallsThrough) tryFalls.Add(CopyOf(tryPath.Assigned));
                    var tryJumps = new PathState(
                        new HashSet<FieldSymbol>(ReferenceEqualityComparer.Instance));
                    AbsorbJumps(tryJumps, tryPath);
                    if (tryPath.InitExited) tryJumps.InitExited = true;
                    foreach (var catchClause in tryStatement.Catches)
                    {
                        var catchPath = path.Fork();
                        AnalyzeBlock(catchClause.Body, catchPath, context);
                        if (catchPath.FallsThrough) tryFalls.Add(CopyOf(catchPath.Assigned));
                        AbsorbJumps(tryJumps, catchPath);
                        if (catchPath.InitExited) tryJumps.InitExited = true;
                    }
                    if (tryStatement.FinallyBlock != null)
                    {
                        // finally 必跑：落尾路径与 break/continue 出环点都叠其赋值
                        var finallyEntry = tryFalls.Count > 0
                            ? IntersectAll(tryFalls)
                            : CopyOf(path.Assigned);
                        var finallyPath = new PathState(CopyOf(finallyEntry));
                        AnalyzeBlock(tryStatement.FinallyBlock, finallyPath, context);
                        AbsorbJumps(tryJumps, finallyPath);
                        if (finallyPath.InitExited) tryJumps.InitExited = true;
                        if (tryFalls.Count > 0 && finallyPath.FallsThrough)
                        {
                            path.Assigned.Clear();
                            path.Assigned.UnionWith(finallyPath.Assigned);
                        }
                        else
                        {
                            path.FallsThrough = false;
                        }
                        DelayJumpsThroughFinally(tryJumps, tryStatement.FinallyBlock, context);
                    }
                    else
                    {
                        MergeFallThrough(path, tryFalls);
                    }
                    AbsorbJumps(path, tryJumps);
                    if (tryJumps.InitExited) path.InitExited = true;
                    break;
                case BoundSeqStatement seqStatement:
                    // 保守：seqexit/return@ 可跳过体尾，体内赋值不向外传播
                    var seqPath = path.Fork();
                    AnalyzeBlock(seqStatement.Body, seqPath, context);
                    AbsorbJumps(path, seqPath);
                    if (seqPath.InitExited)
                    {
                        path.InitExited = true;
                        path.FallsThrough = false;
                    }
                    else if (!seqPath.FallsThrough
                        && (seqPath.Breaks.Count > 0 || seqPath.Continues.Count > 0))
                    {
                        path.FallsThrough = false;
                    }
                    break;
                case BoundBlock nested:
                    AnalyzeBlock(nested, path, context);
                    break;
                case BoundReturnStatement:
                    ReportMissing(path.Assigned, context);
                    path.FallsThrough = false;
                    path.InitExited = true;
                    break;
                case BoundThrowStatement throwStatement:
                    ScanExpression(throwStatement.Exception, path.Assigned, context);
                    path.FallsThrough = false;
                    path.InitExited = true;
                    break;
                case BoundLoopControl control:
                    if (control.IsBreak) RecordJump(path.Breaks, control.Target, path.Assigned);
                    else RecordJump(path.Continues, control.Target, path.Assigned);
                    path.FallsThrough = false;
                    break;
                case BoundSeqExitStatement:
                    path.FallsThrough = false;
                    break;
                case BoundLocalDeclarationStatement declaration:
                    if (declaration.Initializer != null)
                    {
                        ScanExpression(declaration.Initializer, path.Assigned, context);
                    }
                    break;
                case BoundDestructuringDeclarationStatement destructuring:
                    ScanExpression(destructuring.Initializer, path.Assigned, context);
                    break;
                case BoundCallStatement call:
                    if (call.Receiver != null)
                    {
                        ScanExpression(call.Receiver, path.Assigned, context);
                    }
                    foreach (var argument in call.Arguments)
                    {
                        ScanExpression(argument, path.Assigned, context);
                    }
                    break;
                case BoundYieldStatement yieldStatement:
                    if (yieldStatement.Alarm != null)
                    {
                        ScanExpression(yieldStatement.Alarm, path.Assigned, context);
                    }
                    break;
                case BoundReturnValueStatement returnValue:
                    ScanExpression(returnValue.Value, path.Assigned, context);
                    path.FallsThrough = false;
                    break;
            }
        }

        // 赋值目标若是 this 的直达字段（含映射合成与 setter 形态——setter
        // 写 backing 槽）即标记已赋值；嵌套字段链（this.a.b = ...）不算
        private static void MarkTarget(BoundExpression target, HashSet<FieldSymbol> assigned,
            CheckContext context)
        {
            if (target is BoundFieldAccessExpression { Receiver: BoundThisExpression } access
                && context.Obligations.Contains(access.Field))
            {
                assigned.Add(access.Field);
            }
        }

        // 表达式内的复合赋值（this.x += 1 读写在同一点——写出后字段即已
        // 赋值）扫描；不下钻 lambda 体（闭包调用时机不可控，体内赋值不计）
        private static void ScanExpression(BoundExpression expression,
            HashSet<FieldSymbol> assigned, CheckContext context)
        {
            if (expression is BoundLambdaExpression) return;
            if (expression is BoundCompoundAssignmentExpression compound)
            {
                MarkTarget(compound.Target, assigned, context);
            }
            foreach (var child in BoundAnalysis.ChildExpressions(expression))
            {
                ScanExpression(child, assigned, context);
            }
        }
    }
}
