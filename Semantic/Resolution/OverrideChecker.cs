namespace RigiCompiler
{
    // ===== override 配套检查（SYNTAX §9.2/§9.2.1，S8e）=====
    //
    // 前置：参数/返回类型（TypeReferenceResolver）与基类/接口图
    // （InheritanceResolver）已解析；修饰符位置合法性已在 ModifierChecker。
    // 构造宿主（基类/接口带泛型实参）的成员签名按定义 → 构造代入实参后比较
    // （复用 ResolveEnvironment.Substitute——stdlib 双接口协议即依赖此路径）；
    // 嵌套泛型以外的精确性（泛型参数约束等）归 S9。
    // 内建类型参与覆写关系（Any.toString open 承诺与 Object.toString open
    // override 默认实现都是合法覆写目标），但不产生待实现成员（toString 承诺
    // 由 Object 默认实现满足）。
    internal sealed class OverrideChecker : ResolverVisitor<OverrideChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            // 一、成员级覆写关系与 abstract 位置（仅 Regular 实例成员方法）
            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph || entry.DeclaringType == null ||
                    entry.Symbol is not MethodSymbol { Kind: MethodKind.Regular, IsStatic: false } method)
                {
                    continue;
                }
                var host = entry.DeclaringType;
                var fn = (CallableDeclarationASTNode)entry.Node;
                // 毒化（自身签名含 ErrorType）：静默，抑制次生噪音
                var view = SignatureView.Raw(method);
                if (view.HasErrorType) continue;

                var matches = FindInheritedMatches(host, view, env.Unit.Symbols);
                // async 一致性（§9.2.1，bug A1）：IsAsync 不同的继承成员既不
                // 是合法覆写目标，也不允许静默隐藏——否则 sync 签名被 async
                // 实现「满足」，调用点静态类型 T 而运行期实得 Task\<T\>
                // （ReturnType 存 T、调用点按被绑定方法的 IsAsync 改写）。
                // 双 async / 双 sync 不受影响
                var asyncConsistent = matches.Where(m => m.Method.IsAsync == method.IsAsync)
                    .ToList();
                if (method.IsOverride)
                {
                    if (matches.Count == 0)
                    {
                        env.Error(fn.Span, $"'{method.Name}': no inherited member to override");
                    }
                    else if (asyncConsistent.Count == 0)
                    {
                        env.Error(fn.Span,
                            $"'{method.Name}': 'async' modifier does not match the inherited member");
                    }
                    else if (!asyncConsistent.Exists(m => m.IsOverridable))
                    {
                        env.Error(fn.Span,
                            $"'{method.Name}': inherited member is not 'open' or 'abstract'");
                    }
                }
                else if (matches.Count > 0)
                {
                    if (asyncConsistent.Count == 0)
                    {
                        env.Error(fn.Span,
                            $"'{method.Name}': 'async' modifier does not match the inherited member");
                    }
                    else
                    {
                        // 禁止静默隐藏继承成员（§9.2.1：同名同签名必须显式 override）
                        env.Error(fn.Span,
                            $"'{method.Name}' hides an inherited member; declare it 'override'");
                    }
                }

                // abstract 位置与体（§9.2.1）
                if (method.IsAbstract &&
                    !(host.Kind == TypeKind.Class && host.IsAbstract))
                {
                    env.Error(fn.Span, $"'{method.Name}': abstract method requires an abstract class");
                }
                if (method.IsAbstract && fn.Body != null)
                {
                    env.Error(fn.Span, $"'{method.Name}': abstract method cannot have a body");
                }
                // 接口外的无体方法必须 abstract 或 native（init 映射形态天然无体，豁免）
                if (!method.IsAbstract && !method.IsNative && fn.Body == null &&
                    host.Kind != TypeKind.Interface)
                {
                    env.Error(fn.Span,
                        $"'{method.Name}' must have a body or be marked 'abstract'");
                }
            }

            // 二、访问器是独立的 override 单元。访问器符号挂在字段三槽上，
            // 不进入 P2 条目表，因此必须从字段条目反向访问；getter 与 setter
            // 分别匹配，不能因同名字段或另一种访问器而互相命中。
            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph || entry.DeclaringType == null
                    || entry.Symbol is not FieldSymbol field
                    || entry.Node is not VariableDeclarationASTNode declaration)
                {
                    continue;
                }
                CheckAccessorOverride(entry.DeclaringType, field, field.Getter,
                    declaration.Getter, env);
                CheckAccessorOverride(entry.DeclaringType, field, field.Setter,
                    declaration.Setter, env);
            }

            // 二·五、字段 open/override（§9.2.1 字段覆写）：override 字段 =
            // 同名继承 open 字段的初始值替换——类型一致、必须给出新初始值、
            // 不得携带 wrapper 应用与访问器（存储仍是基类槽）。校验通过后
            // 符号从宿主 Fields 表移除并挂 OverriddenField——名称解析与
            // BIL 发射自然落到基类槽，本类只为它合成 override 版
            // ..init.field.<名>。带访问器的重声明归第二节访问器机制；无访问器
            // 的 hiding 沿用既有行为，仅双侧都带声明初始值时才拒绝（见
            // CheckFieldOverride 注释）
            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph || entry.DeclaringType == null
                    || entry.Symbol is not FieldSymbol field
                    || entry.Node is not VariableDeclarationASTNode fieldDecl)
                {
                    continue;
                }
                CheckFieldOverride(entry.DeclaringType, field, fieldDecl, env);
            }

            // 三、具体类必须实现继承链全部 abstract 成员与无体接口成员（§9.2.1；
            // 有默认实现的接口成员隐式继承）。like 委托（§9.6）：委托字段类型
            // 提供同签名具体实现的待实现成员视为已实现（转发成员由 P3
            // BindingDriver 合成，LikeDelegationFacility 同口径）；like 目标
            // 不是本类实例字段时专项诊断
            foreach (var entry in env.TypeEntries)
            {
                if (!entry.InGraph ||
                    entry.Symbol is not TypeSymbol
                    { Kind: TypeKind.Class, IsAbstract: false, IsBuiltin: false } type)
                {
                    continue;
                }
                if (type.LikeTarget != null
                    && LikeDelegationFacility.FindLikeField(type) == null)
                {
                    env.Error(entry.Node.Span,
                        $"'{type.Name}': like delegation target '{type.LikeTarget}' " +
                        "is not an instance field");
                }
                var delegatedKeys = new HashSet<string>(
                    LikeDelegationFacility.CollectDelegatedMembers(type, env.Unit.Symbols)
                        .Select(d => d.Key));
                var reported = new HashSet<string>();
                foreach (var required in FindRequiredMembers(type, env.Unit.Symbols))
                {
                    // 同签名多源（多接口/继承交叠）只报一次
                    if (!reported.Add(required.Key)) continue;
                    if (FindImplementation(type, required, env.Unit.Symbols) == null
                        && !delegatedKeys.Contains(required.Key))
                    {
                        env.Error(entry.Node.Span,
                            $"'{type.Name}' does not implement abstract member '{required.Symbol.Name}'");
                    }
                }

                // bug S3（§11）：接口闭包中存在 ≥2 个不同符号的同签名默认
                // 方法（HasBody）且类未提供自己的实现 → 类声明点编译错误，
                // 强制显式 override（`-> InterfaceName` 委托语法不支持）。
                // 真菱形放行：两条继承路径收到同一符号的默认方法（闭包按
                // 定义去重后只有一条）不构成冲突；报错放在类声明点而非接口
                // 声明点——两接口各自声明同签名默认方法本身合法，冲突只在
                // 被同一具体类实现时成立。
                foreach (var conflict in FindConflictingDefaultMethods(type, env.Unit.Symbols))
                {
                    var ownerA = (conflict.First.Owner?.ConstructedFrom ?? conflict.First.Owner)?.Name;
                    var ownerB = (conflict.Second.Owner?.ConstructedFrom ?? conflict.Second.Owner)?.Name;
                    env.Error(entry.Node.Span,
                        $"'{type.Name}': interface default method '{conflict.First.Name}' " +
                        $"conflicts between '{ownerA}' and '{ownerB}'; " +
                        "declare an explicit 'override'");
                }
            }
        }

        private static string AccessorText(MethodSymbol accessor) =>
            accessor.Kind == MethodKind.Getter ? "getter" : "setter";

        // 字段 override 校验（§9.2.1 字段覆写）。最近基类同名字段为命中
        // （构造基类归定义查成员表，类型比对按 extends 实参代入）。全部
        // 校验通过才挂 OverriddenField 并移出宿主 Fields 表——任一失败
        // 保留原 hiding 形态，避免次生崩溃（诊断已落袋）。
        // 带访问器的重声明不在此管辖：访问器是独立 override 单元（第二节）；
        // 无访问器的同名实例字段仅当双侧都带声明初始值时才构成非法
        // hiding——..init.field.<名> 按名成族，静默隐藏会让基类槽初值在
        // 子类构造中被虚派发吞掉（单侧有初始值时另一侧无 ..init.field
        // 槽，无碰撞，沿用既有 hiding 行为）
        private static void CheckFieldOverride(TypeSymbol host, FieldSymbol field,
            VariableDeclarationASTNode declaration, ResolveEnvironment env)
        {
            if (field.IsStatic || field.FieldType is ErrorTypeSymbol)
            {
                return;
            }
            FieldSymbol? inherited = null;
            TypeSymbol? inheritedConstructed = null;
            for (var type = host.BaseType; type != null; type = type.BaseType)
            {
                var definition = type.ConstructedFrom ?? type;
                var hit = definition.Fields.FirstOrDefault(f => f.Name == field.Name);
                if (hit != null)
                {
                    inherited = hit;
                    inheritedConstructed = type;
                    break;
                }
            }
            if (!field.IsOverride)
            {
                if (declaration.Getter != null || declaration.Setter != null)
                {
                    return; // 访问器覆写/隐藏归访问器检查（第二节）
                }
                if (declaration.Initializer != null && inherited != null
                    && !inherited.IsStatic && HasDeclaredInitializer(inherited, env))
                {
                    env.Error(declaration.Span,
                        $"'{field.Name}' hides an inherited field with an initial value; " +
                        "declare it 'override'");
                }
                return;
            }
            if (inherited == null || inherited.IsStatic || inheritedConstructed == null)
            {
                env.Error(declaration.Span, $"'{field.Name}': no inherited field to override");
                return;
            }
            if (!inherited.IsOpen)
            {
                env.Error(declaration.Span,
                    $"'{field.Name}': inherited field is not 'open'");
                return;
            }
            if (field.FieldType == null)
            {
                env.Error(declaration.Span,
                    $"'{field.Name}': field override requires a type annotation");
                return;
            }
            var inheritedDefinition = inheritedConstructed.ConstructedFrom ?? inheritedConstructed;
            var inheritedType = ReferenceEquals(inheritedDefinition, inheritedConstructed)
                ? inherited.FieldType
                : env.Substitute(inherited.FieldType, inheritedDefinition, inheritedConstructed);
            if (inheritedType != null && !EquivalentAccessorType(inheritedType, field.FieldType))
            {
                env.Error(declaration.Span,
                    $"'{field.Name}': field type must match the overridden field");
                return;
            }
            if (declaration.Initializer == null)
            {
                env.Error(declaration.Span,
                    $"'{field.Name}': field override requires a new initial value");
                return;
            }
            if (field.AppliedWrappers.Count > 0)
            {
                env.Error(declaration.Span,
                    $"'{field.Name}': field override cannot declare wrappers");
                return;
            }
            if (declaration.Getter != null || declaration.Setter != null)
            {
                env.Error(declaration.Span,
                    $"'{field.Name}': field override cannot declare accessors");
                return;
            }
            field.OverriddenField = inherited;
            host.Fields.Remove(field);
        }

        // 字段符号是否有声明处初始值（按 P2 条目表反查声明节点；
        // 合成/外部字段无条目 = 无初始值）
        private static bool HasDeclaredInitializer(FieldSymbol field, ResolveEnvironment env)
        {
            foreach (var entry in env.Entries)
            {
                if (ReferenceEquals(entry.Symbol, field))
                {
                    return entry.Node is VariableDeclarationASTNode { Initializer: not null };
                }
            }
            return false;
        }

        private static void CheckAccessorOverride(TypeSymbol host, FieldSymbol field,
            MethodSymbol? accessor, PropertyAccessorASTNode? declaration,
            ResolveEnvironment env)
        {
            if (field.IsStatic || accessor == null || declaration == null || field.FieldType == null
                || field.FieldType is ErrorTypeSymbol)
            {
                return;
            }

            var matches = FindInheritedAccessorMatches(host, field, accessor, env.Unit.Symbols);
            if (accessor.IsOverride)
            {
                if (matches.Count == 0)
                {
                    env.Error(declaration.Span,
                        $"'{accessor.Name}' {AccessorText(accessor)} has no inherited accessor to override");
                }
                else if (!matches.Exists(m => m.IsOverridable))
                {
                    env.Error(declaration.Span,
                        $"'{accessor.Name}' {AccessorText(accessor)} inherited accessor is not 'open'");
                }
            }
            else if (matches.Count > 0)
            {
                env.Error(declaration.Span,
                    $"'{accessor.Name}' {AccessorText(accessor)} hides an inherited accessor; declare it 'override'");
            }
        }

        private static List<InheritedMatch> FindInheritedAccessorMatches(TypeSymbol host,
            FieldSymbol field, MethodSymbol accessor, SymbolGraph symbols)
        {
            var result = new List<InheritedMatch>();
            for (var type = host.BaseType; type != null; type = type.BaseType)
            {
                AddAccessorMatches(result, type, field.Name, accessor, symbols);
            }
            foreach (var iface in InterfaceClosure(host, symbols))
            {
                AddAccessorMatches(result, iface, field.Name, accessor, symbols);
            }
            return result;
        }

        private static void AddAccessorMatches(List<InheritedMatch> result, TypeSymbol constructed,
            string fieldName, MethodSymbol accessor, SymbolGraph symbols)
        {
            var definition = constructed.ConstructedFrom ?? constructed;
            var field = definition.Fields.FirstOrDefault(f => f.Name == fieldName);
            if (field == null) return;
            var candidate = accessor.Kind == MethodKind.Getter ? field.Getter : field.Setter;
            if (candidate == null) return;
            var candidateType = accessor.Kind == MethodKind.Getter
                ? candidate.ReturnType
                : candidate.Parameters.FirstOrDefault()?.Type;
            var currentType = accessor.Kind == MethodKind.Getter
                ? accessor.ReturnType
                : accessor.Parameters.FirstOrDefault()?.Type;
            if (ReferenceEquals(definition, constructed)
                ? EquivalentAccessorType(candidateType, currentType)
                : EquivalentAccessorType(symbols.Substitute(candidateType, definition, constructed), currentType))
            {
                result.Add(new InheritedMatch(candidate,
                    candidate.IsOpen || candidate.IsAbstract || definition.Kind == TypeKind.Interface));
            }
        }

        private static bool EquivalentAccessorType(SemanticSymbol? a, SemanticSymbol? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a is TypeSymbol { ConstructedFrom: not null } ta
                && b is TypeSymbol { ConstructedFrom: not null } tb)
            {
                if (!ReferenceEquals(ta.ConstructedFrom, tb.ConstructedFrom)
                    || ta.TypeArguments!.Count != tb.TypeArguments!.Count) return false;
                for (var i = 0; i < ta.TypeArguments.Count; i++)
                    if (!EquivalentAccessorType(ta.TypeArguments[i], tb.TypeArguments[i])) return false;
                return true;
            }
            return false;
        }

        // 继承命中集：基类链（含内建——Any/Object.toString 是合法覆写目标）+
        // 接口闭包中与 method 同名同签名的成员
        private static List<InheritedMatch> FindInheritedMatches(TypeSymbol host,
            SignatureView method, SymbolGraph symbols)
        {
            var result = new List<InheritedMatch>();
            for (var t = host.BaseType; t != null; t = t.BaseType)
            {
                var def = t.ConstructedFrom ?? t;
                foreach (var candidate in def.Methods)
                {
                    var view = SignatureView.Of(candidate, def, t, symbols);
                    if (!view.HasErrorType && view.Matches(method))
                    {
                        // 接口成员天然可覆写（§9.2.1；Any 在基类链末端亦为接口）
                        result.Add(new InheritedMatch(candidate,
                            candidate.IsOpen || candidate.IsAbstract ||
                            def.Kind == TypeKind.Interface));
                    }
                }
            }
            foreach (var iface in InterfaceClosure(host, symbols))
            {
                var def = iface.ConstructedFrom ?? iface;
                foreach (var candidate in def.Methods)
                {
                    var view = SignatureView.Of(candidate, def, iface, symbols);
                    if (!view.HasErrorType && view.Matches(method))
                    {
                        result.Add(new InheritedMatch(candidate, true));
                    }
                }
            }
            return result;
        }

        internal static IEnumerable<MethodSymbol> InheritedMethodsForWrapper(TypeSymbol host,
            MethodSymbol method, SymbolGraph symbols)
        {
            return FindInheritedMatches(host, SignatureView.Raw(method), symbols)
                .Where(match => match.IsOverridable)
                .Select(match => match.Method)
                .Distinct();
        }

        internal static IEnumerable<FieldSymbol> InheritedFieldsForAccessor(TypeSymbol host,
            MethodSymbol accessor, SymbolGraph symbols)
        {
            var result = new List<FieldSymbol>();
            for (var type = host.BaseType; type != null; type = type.BaseType)
            {
                AddInheritedField(result, type, accessor);
            }
            foreach (var iface in InterfaceClosure(host, symbols))
            {
                AddInheritedField(result, iface, accessor);
            }
            return result.Distinct();
        }

        private static void AddInheritedField(List<FieldSymbol> result, TypeSymbol constructed,
            MethodSymbol accessor)
        {
            var definition = constructed.ConstructedFrom ?? constructed;
            var field = definition.Fields.FirstOrDefault(f => f.Name == accessor.Name);
            if (field != null && (accessor.Kind == MethodKind.Getter ? field.Getter : field.Setter) != null)
            {
                result.Add(field);
            }
        }

        // 待实现成员闭包：基类链的 abstract 方法 + 接口闭包的无体方法。
        // 内建类型成员的豁免仅限「自身有实现」者（native 或有方法体）——
        // 基类链循环只收 abstract，内建 abstract（Exception.getMessage）照收，
        // 用户子类必须实现；Any/Object.toString 是 open 具体方法（自带默认
        // 实现，体由发射阶段合成），天然不在 abstract 之列，先例不破。
        // 有体接口成员隐式继承，不待实现。
        // internal：like 委托设施（§9.6，LikeDelegationFacility）同口径复用
        internal static List<SignatureView> FindRequiredMembers(TypeSymbol type,
            SymbolGraph symbols)
        {
            var result = new List<SignatureView>();
            for (var t = type.BaseType; t != null; t = t.BaseType)
            {
                var def = t.ConstructedFrom ?? t;
                foreach (var m in def.Methods)
                {
                    if (m.IsAbstract)
                    {
                        result.Add(SignatureView.Of(m, def, t, symbols));
                    }
                }
            }
            foreach (var iface in InterfaceClosure(type, symbols))
            {
                var def = iface.ConstructedFrom ?? iface;
                if (def.IsBuiltin) continue;
                foreach (var m in def.Methods)
                {
                    if (!m.HasBody)
                    {
                        result.Add(SignatureView.Of(m, def, iface, symbols));
                    }
                }
            }
            return result;
        }

        // 沿宿主向上找签名匹配的具体实现；命中 abstract 或无体非 native 视为未实现
        // internal：like 委托设施（§9.6，LikeDelegationFacility）同口径复用
        internal static MethodSymbol? FindImplementation(TypeSymbol type, SignatureView required,
            SymbolGraph symbols)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var def = t.ConstructedFrom ?? t;
                foreach (var candidate in def.Methods)
                {
                    var view = SignatureView.Of(candidate, def, t, symbols);
                    if (view.Matches(required))
                    {
                        return !candidate.IsAbstract && (candidate.HasBody || candidate.IsNative)
                            ? candidate
                            : null;
                    }
                }
            }
            return null;
        }

        // bug S3 冲突检测：接口闭包中按签名分组收集 HasBody 的默认方法，
        // 同组出现 ≥2 个不同符号且类/基类链未提供自己的实现 → 该签名冲突
        // （返回首对冲突符号供诊断指名两个来源接口）。签名比较沿用
        // SignatureView 口径（构造宿主代入实参后比较）；含 ErrorType 的
        // 视图跳过（毒化签名抑制次生噪音）。仅收 Regular 实例方法——
        // 访问器/operator 的默认实现冲突不在本 bug 范围。
        private static List<DefaultMethodConflict> FindConflictingDefaultMethods(TypeSymbol type,
            SymbolGraph symbols)
        {
            var result = new List<DefaultMethodConflict>();
            var groups = new List<(SignatureView View, MethodSymbol Method)>();
            foreach (var iface in InterfaceClosure(type, symbols))
            {
                var def = iface.ConstructedFrom ?? iface;
                if (def.IsBuiltin) continue;
                foreach (var m in def.Methods)
                {
                    if (!m.HasBody || m.IsStatic || m.Kind != MethodKind.Regular) continue;
                    var view = SignatureView.Of(m, def, iface, symbols);
                    if (view.HasErrorType) continue;
                    groups.Add((view, m));
                }
            }
            var reported = new HashSet<SignatureView>();
            for (var i = 0; i < groups.Count; i++)
            {
                if (reported.Contains(groups[i].View)) continue;
                MethodSymbol? other = null;
                for (var j = i + 1; j < groups.Count; j++)
                {
                    if (ReferenceEquals(groups[j].Method, groups[i].Method)
                        || !groups[j].View.Matches(groups[i].View))
                    {
                        continue;
                    }
                    other = groups[j].Method;
                    break;
                }
                if (other == null) continue;
                // 类已提供自己的实现（含基类链具体实现）→ 双视图都派发到
                // 类实现，无歧义，放行
                if (FindImplementation(type, groups[i].View, symbols) != null) continue;
                reported.Add(groups[i].View);
                result.Add(new DefaultMethodConflict(groups[i].Method, other));
            }
            return result;
        }

        private sealed class DefaultMethodConflict
        {
            public MethodSymbol First { get; }
            public MethodSymbol Second { get; }

            public DefaultMethodConflict(MethodSymbol first, MethodSymbol second)
            {
                First = first;
                Second = second;
            }
        }

        // 接口闭包：宿主及基类链的 implements 传递闭包（定义级去重，保留
        // 构造形态）。S9f：接口可声明在泛型基类上（`RangeEnumerator\<T\>
        // implements IEnumerator\<T\>`），闭包遍历沿宿主链把接口实参代入
        // 构造实参（IEnumerator\<T\> → IEnumerator\<i32\>——签名匹配按
        // 代入后形态比较）
        internal static List<TypeSymbol> InterfaceClosure(TypeSymbol host, SymbolGraph symbols)
        {
            var result = new List<TypeSymbol>();
            var visited = new HashSet<TypeSymbol>();
            var stack = new Stack<TypeSymbol>();
            for (var t = host; t != null; t = t.BaseType)
            {
                var def = t.ConstructedFrom ?? t;
                foreach (var iface in def.Interfaces)
                {
                    stack.Push(ReferenceEquals(def, t)
                        ? iface
                        : (TypeSymbol)(symbols.Substitute(iface, def, t) ?? iface));
                }
            }
            while (stack.Count > 0)
            {
                var iface = stack.Pop();
                if (!visited.Add(iface.ConstructedFrom ?? iface)) continue;
                result.Add(iface);
                var def = iface.ConstructedFrom ?? iface;
                foreach (var next in def.Interfaces)
                {
                    stack.Push(ReferenceEquals(def, iface)
                        ? next
                        : (TypeSymbol)(symbols.Substitute(next, def, iface) ?? next));
                }
            }
            return result;
        }

        // 候选成员的签名视图：构造宿主场景下按定义 → 构造代入实参后的
        // 参数/返回类型序列（Substitute 对非构造宿主原样返回）。
        // internal：like 委托设施（§9.6，LikeDelegationFacility）同口径复用
        internal sealed class SignatureView
        {
            public MethodSymbol Symbol { get; }
            private readonly SemanticSymbol?[] _paramTypes;
            private readonly SemanticSymbol? _returnType;

            private SignatureView(MethodSymbol symbol, SemanticSymbol?[] paramTypes,
                SemanticSymbol? returnType)
            {
                Symbol = symbol;
                _paramTypes = paramTypes;
                _returnType = returnType;
            }

            // 宿主声明自身的原始视图（构造代入只发生在继承链/接口侧）
            public static SignatureView Raw(MethodSymbol symbol)
            {
                return new SignatureView(symbol,
                    symbol.Parameters.Select(p => p.Type).ToArray(), symbol.ReturnType);
            }

            public static SignatureView Of(MethodSymbol symbol, TypeSymbol definition,
                TypeSymbol constructed, SymbolGraph symbols)
            {
                if (ReferenceEquals(definition, constructed)) return Raw(symbol);
                return new SignatureView(symbol,
                    symbol.Parameters.Select(p => symbols.Substitute(p.Type, definition, constructed))
                        .ToArray(),
                    symbols.Substitute(symbol.ReturnType, definition, constructed));
            }

            public bool HasErrorType =>
                _returnType is ErrorTypeSymbol ||
                _paramTypes.Any(t => t is ErrorTypeSymbol or null);

            // 签名匹配：名 + 泛型元数 + 参数个数 + 参数类型同构 + 返回类型同构
            // （S9f 解开 #22⑥：泛型方法覆写——两侧各自的泛型参数是不同
            // 符号，按声明序对应比较而非引用相等；嵌套构造递归逐实参。
            // 元数不同即不同派发契约，直接不匹配）。
            // 注意：async 一致性（§9.2.1，bug A1）不在本口径内——由
            // 成员级覆写检查在 Matches 命中集上按 IsAsync 分层判定并出
            // 专项诊断；待实现闭包/实现查找沿用本口径（async 实现仍计为
            // 「已实现」，避免与专项诊断重复的次生噪音）
            public bool Matches(SignatureView other)
            {
                if (Symbol.Name != other.Symbol.Name
                    || Symbol.GenericParameters.Count != other.Symbol.GenericParameters.Count
                    || _paramTypes.Length != other._paramTypes.Length)
                {
                    return false;
                }
                for (int i = 0; i < _paramTypes.Length; i++)
                {
                    if (!Equivalent(_paramTypes[i], other._paramTypes[i], Symbol,
                        other.Symbol))
                    {
                        return false;
                    }
                }
                return Equivalent(_returnType, other._returnType, Symbol, other.Symbol);
            }

            // 类型同构比较：引用相等即匹配；两侧的泛型参数（方法声明序）按
            // 索引对应——覆写的 V 与基类的 U 同构；构造类型按定义 + 逐实参
            // 递归。宿主泛型参数经 SignatureView.Of 的 Substitute 已统一为
            // 覆写侧符号（引用相等），不在此列
            private static bool Equivalent(SemanticSymbol? a, SemanticSymbol? b,
                MethodSymbol aMethod, MethodSymbol bMethod)
            {
                if (ReferenceEquals(a, b)) return true;
                if (a == null || b == null) return false;
                if (a is GenericParameterSymbol ga && b is GenericParameterSymbol gb)
                {
                    var ia = aMethod.GenericParameters.IndexOf(ga);
                    var ib = bMethod.GenericParameters.IndexOf(gb);
                    return ia >= 0 && ia == ib;
                }
                if (a is TypeSymbol { ConstructedFrom: not null } ta
                    && b is TypeSymbol { ConstructedFrom: not null } tb)
                {
                    if (!ReferenceEquals(ta.ConstructedFrom, tb.ConstructedFrom)
                        || ta.TypeArguments!.Count != tb.TypeArguments!.Count)
                    {
                        return false;
                    }
                    for (int i = 0; i < ta.TypeArguments!.Count; i++)
                    {
                        if (!Equivalent(ta.TypeArguments[i], tb.TypeArguments[i],
                            aMethod, bMethod))
                        {
                            return false;
                        }
                    }
                    return true;
                }
                return false;
            }

            public string Key =>
                Symbol.Name + "(" + string.Join(",", _paramTypes.Select(t => t?.Name)) + ")";
        }

        private sealed class InheritedMatch
        {
            public MethodSymbol Method { get; }
            public bool IsOverridable { get; }

            public InheritedMatch(MethodSymbol method, bool isOverridable)
            {
                Method = method;
                IsOverridable = isOverridable;
            }
        }
    }
}
