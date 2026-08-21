namespace RigiCompiler
{
    // init 字段定值赋值分析（P18/S2，SYNTAX §9.3「DA 规则」）：所有实体
    //（值类型与对象同规则）的实例字段声明后默认视为未赋值；非 Nullable
    // 字段必须三选一——声明初始值（编译器合成的 ..init.field.*，构造进入
    // 时已赋值）、init 内显式赋值（`init(_ -> x)` 映射算赋值）、字段类型
    // 为 Nullable。检查全部是前端静态检查（无 VM 哨兵），检查点：
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
            // 内建宿主声明的字段（bootstrap Exception.message 等）不在义务集
            if (field.Owner is not { } owner || owner.IsBuiltin) return false;
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
            var assigned = new HashSet<FieldSymbol>(ReferenceEqualityComparer.Instance);
            AnalyzeBlock(body, assigned, context);
            // 块尾出口（能落尾的 init 路径）
            ReportMissing(assigned, context);
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

        private static void AnalyzeBlock(BoundBlock block, HashSet<FieldSymbol> assigned,
            CheckContext context)
        {
            foreach (var statement in block.Statements)
            {
                AnalyzeStatement(statement, assigned, context);
            }
        }

        private static void AnalyzeStatement(BoundStatement statement,
            HashSet<FieldSymbol> assigned, CheckContext context)
        {
            switch (statement)
            {
                case BoundAssignmentStatement assignment:
                    MarkTarget(assignment.Target, assigned, context);
                    ScanExpression(assignment.Value, assigned, context);
                    break;
                case BoundExpressionStatement expressionStatement:
                    ScanExpression(expressionStatement.Expression, assigned, context);
                    break;
                case BoundIfStatement ifStatement:
                    ScanExpression(ifStatement.Condition, assigned, context);
                    var beforeIf = CopyOf(assigned);
                    AnalyzeBlock(ifStatement.TrueBlock, assigned, context);
                    var tailTrue = CopyOf(assigned);
                    var tailFalse = CopyOf(beforeIf);
                    if (ifStatement.FalseBlock != null)
                    {
                        AnalyzeBlock(ifStatement.FalseBlock, tailFalse, context);
                    }
                    // 双分支合并：before ∪ (tailT ∩ tailF)
                    assigned.Clear();
                    tailTrue.IntersectWith(tailFalse);
                    assigned.UnionWith(beforeIf);
                    assigned.UnionWith(tailTrue);
                    break;
                case BoundSwitchStatement switchStatement:
                    ScanExpression(switchStatement.Selector, assigned, context);
                    var beforeSwitch = CopyOf(assigned);
                    var mergedSwitch = new HashSet<FieldSymbol>(
                        ReferenceEqualityComparer.Instance);
                    var first = true;
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        var tail = CopyOf(beforeSwitch);
                        AnalyzeBlock(switchCase.Body, tail, context);
                        if (first) mergedSwitch.UnionWith(tail);
                        else mergedSwitch.IntersectWith(tail);
                        first = false;
                    }
                    var defaultTail = CopyOf(beforeSwitch);
                    AnalyzeBlock(switchStatement.DefaultBody, defaultTail, context);
                    if (first) mergedSwitch.UnionWith(defaultTail);
                    else mergedSwitch.IntersectWith(defaultTail);
                    assigned.Clear();
                    assigned.UnionWith(beforeSwitch);
                    assigned.UnionWith(mergedSwitch);
                    break;
                case BoundLoop loop:
                    // while/for 体可能零次执行：体尾不计入出口，但体内 return
                    // 出口仍须检查（以体入口态起评）；do-while 至少一次，取体尾
                    if (loop.Kind == LoopKind.DoWhile)
                    {
                        AnalyzeBlock(loop.Body, assigned, context);
                    }
                    else
                    {
                        var bodyState = CopyOf(assigned);
                        AnalyzeBlock(loop.Body, bodyState, context);
                    }
                    break;
                case BoundTryStatement tryStatement:
                    var tryTail = CopyOf(assigned);
                    AnalyzeBlock(tryStatement.TryBlock, tryTail, context);
                    var mergedTry = CopyOf(tryTail);
                    if (tryStatement.Catches.Count > 0)
                    {
                        foreach (var catchClause in tryStatement.Catches)
                        {
                            var catchTail = CopyOf(assigned);
                            AnalyzeBlock(catchClause.Body, catchTail, context);
                            mergedTry.IntersectWith(catchTail);
                        }
                    }
                    assigned.Clear();
                    assigned.UnionWith(mergedTry);
                    if (tryStatement.FinallyBlock != null)
                    {
                        AnalyzeBlock(tryStatement.FinallyBlock, assigned, context);
                    }
                    break;
                case BoundSeqStatement seqStatement:
                    // 保守：return@ 可跳过体的尾部语句，体内赋值不向外传播；
                    // 体内 return 出口仍须检查（以入口态起评）
                    var seqState = CopyOf(assigned);
                    AnalyzeBlock(seqStatement.Body, seqState, context);
                    break;
                case BoundBlock nested:
                    AnalyzeBlock(nested, assigned, context);
                    break;
                case BoundReturnStatement:
                    // 中途裸 return 也是 init 的出口路径
                    ReportMissing(assigned, context);
                    break;
                case BoundLocalDeclarationStatement declaration:
                    if (declaration.Initializer != null)
                    {
                        ScanExpression(declaration.Initializer, assigned, context);
                    }
                    break;
                case BoundDestructuringDeclarationStatement destructuring:
                    ScanExpression(destructuring.Initializer, assigned, context);
                    break;
                case BoundCallStatement call:
                    if (call.Receiver != null) ScanExpression(call.Receiver, assigned, context);
                    foreach (var argument in call.Arguments)
                    {
                        ScanExpression(argument, assigned, context);
                    }
                    break;
                // throw/yield/break/continue/return@/seqexit/wrapper 安装：
                // 不产生 this 字段的整体赋值
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
