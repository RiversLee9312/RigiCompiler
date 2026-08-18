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
                if (method.IsOverride)
                {
                    if (matches.Count == 0)
                    {
                        env.Error(fn.Span, $"'{method.Name}': no inherited member to override");
                    }
                    else if (!matches.Exists(m => m.IsOverridable))
                    {
                        env.Error(fn.Span,
                            $"'{method.Name}': inherited member is not 'open' or 'abstract'");
                    }
                }
                else if (matches.Count > 0)
                {
                    // 禁止静默隐藏继承成员（§9.2.1：同名同签名必须显式 override）
                    env.Error(fn.Span,
                        $"'{method.Name}' hides an inherited member; declare it 'override'");
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
            }
        }

        private static string AccessorText(MethodSymbol accessor) =>
            accessor.Kind == MethodKind.Getter ? "getter" : "setter";

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
            // 元数不同即不同派发契约，直接不匹配）
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
