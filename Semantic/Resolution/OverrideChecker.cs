namespace LatteCompiler
{
    // ===== override 配套检查（SYNTAX §9.2/§9.2.1，S8e）=====
    //
    // 前置：参数/返回类型（TypeReferenceResolver）与基类/接口图
    // （InheritanceResolver）已解析；修饰符位置合法性已在 ModifierChecker。
    // 构造宿主（基类/接口带泛型实参）的成员签名按定义 → 构造代入实参后比较
    // （复用 ResolveEnvironment.Substitute——stdlib 双接口协议即依赖此路径）；
    // 嵌套泛型以外的精确性（泛型参数约束等）归 S9。
    // 内建类型参与覆写关系（Object.toString open 默认实现是合法覆写目标），
    // 但不产生待实现成员（Any.toString 承诺由 Object 默认实现满足）。
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

                var matches = FindInheritedMatches(host, view, env);
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

            // 二、具体类必须实现继承链全部 abstract 成员与无体接口成员（§9.2.1；
            // 有默认实现的接口成员隐式继承——显式委托语法归后续，§11）
            foreach (var entry in env.TypeEntries)
            {
                if (!entry.InGraph ||
                    entry.Symbol is not TypeSymbol
                    { Kind: TypeKind.Class, IsAbstract: false, IsBuiltin: false } type)
                {
                    continue;
                }
                var reported = new HashSet<string>();
                foreach (var required in FindRequiredMembers(type, env))
                {
                    // 同签名多源（多接口/继承交叠）只报一次
                    if (!reported.Add(required.Key)) continue;
                    if (FindImplementation(type, required, env) == null)
                    {
                        env.Error(entry.Node.Span,
                            $"'{type.Name}' does not implement abstract member '{required.Symbol.Name}'");
                    }
                }
            }
        }

        // 继承命中集：基类链（含内建——Object.toString 是合法覆写目标）+
        // 接口闭包中与 method 同名同签名的成员
        private static List<InheritedMatch> FindInheritedMatches(TypeSymbol host,
            SignatureView method, ResolveEnvironment env)
        {
            var result = new List<InheritedMatch>();
            for (var t = host.BaseType; t != null; t = t.BaseType)
            {
                var def = t.ConstructedFrom ?? t;
                foreach (var candidate in def.Methods)
                {
                    var view = SignatureView.Of(candidate, def, t, env);
                    if (!view.HasErrorType && view.Matches(method))
                    {
                        // 接口成员天然可覆写（§9.2.1；Any 在基类链末端亦为接口）
                        result.Add(new InheritedMatch(candidate,
                            candidate.IsOpen || candidate.IsAbstract ||
                            def.Kind == TypeKind.Interface));
                    }
                }
            }
            foreach (var iface in InterfaceClosure(host, env))
            {
                var def = iface.ConstructedFrom ?? iface;
                foreach (var candidate in def.Methods)
                {
                    var view = SignatureView.Of(candidate, def, iface, env);
                    if (!view.HasErrorType && view.Matches(method))
                    {
                        result.Add(new InheritedMatch(candidate, true));
                    }
                }
            }
            return result;
        }

        // 待实现成员闭包：基类链的 abstract 方法 + 接口闭包的无体方法
        // （内建类型跳过；有体接口成员隐式继承，不待实现）
        private static List<SignatureView> FindRequiredMembers(TypeSymbol type,
            ResolveEnvironment env)
        {
            var result = new List<SignatureView>();
            for (var t = type.BaseType; t != null; t = t.BaseType)
            {
                var def = t.ConstructedFrom ?? t;
                if (def.IsBuiltin) continue;
                foreach (var m in def.Methods)
                {
                    if (m.IsAbstract)
                    {
                        result.Add(SignatureView.Of(m, def, t, env));
                    }
                }
            }
            foreach (var iface in InterfaceClosure(type, env))
            {
                var def = iface.ConstructedFrom ?? iface;
                if (def.IsBuiltin) continue;
                foreach (var m in def.Methods)
                {
                    if (!m.HasBody)
                    {
                        result.Add(SignatureView.Of(m, def, iface, env));
                    }
                }
            }
            return result;
        }

        // 沿宿主向上找签名匹配的具体实现；命中 abstract 或无体非 native 视为未实现
        private static MethodSymbol? FindImplementation(TypeSymbol type, SignatureView required,
            ResolveEnvironment env)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var def = t.ConstructedFrom ?? t;
                foreach (var candidate in def.Methods)
                {
                    var view = SignatureView.Of(candidate, def, t, env);
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
        private static List<TypeSymbol> InterfaceClosure(TypeSymbol host, ResolveEnvironment env)
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
                        : (TypeSymbol)(env.Substitute(iface, def, t) ?? iface));
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
                        : (TypeSymbol)(env.Substitute(next, def, iface) ?? next));
                }
            }
            return result;
        }

        // 候选成员的签名视图：构造宿主场景下按定义 → 构造代入实参后的
        // 参数/返回类型序列（Substitute 对非构造宿主原样返回）
        private sealed class SignatureView
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
                TypeSymbol constructed, ResolveEnvironment env)
            {
                if (ReferenceEquals(definition, constructed)) return Raw(symbol);
                return new SignatureView(symbol,
                    symbol.Parameters.Select(p => env.Substitute(p.Type, definition, constructed))
                        .ToArray(),
                    env.Substitute(symbol.ReturnType, definition, constructed));
            }

            public bool HasErrorType =>
                _returnType is ErrorTypeSymbol ||
                _paramTypes.Any(t => t is ErrorTypeSymbol or null);

            // 签名匹配：名 + 参数个数 + 参数类型同构 + 返回类型同构
            // （S9f 解开 #22⑥：泛型方法覆写——两侧各自的泛型参数是不同
            // 符号，按声明序对应比较而非引用相等；嵌套构造递归逐实参）
            public bool Matches(SignatureView other)
            {
                if (Symbol.Name != other.Symbol.Name
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
