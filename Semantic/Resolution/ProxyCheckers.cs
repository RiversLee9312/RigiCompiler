namespace LatteCompiler
{
    // ===== S11a：wrapper proxy 成员声明侧形状校验（SYNTAX §14.2/§14.3/§14.4）=====
    //
    // 前置：WrapperTarget 已解析（WrapperTargetResolver）、参数/返回类型已解析
    // （TypeReferenceResolver）。只检查非内建 wrapper 声明；@WrapperTarget
    // 缺失/非法已报过诊断，此处静默跳过。
    //
    // 校验规则（M81 定稿 + M82 修订——Entity wrapper 泛型参数放宽为至多一个，
    // 见 SYNTAX §14.2）：
    //   1. 泛型元数：Entity wrapper 至多一个泛型参数（恰一个即 TTarget 角色，
    //      self 的类型来源）；Value/Method wrapper 不得声明泛型参数（proxy
    //      方法自身的泛型参数不受此限）。
    //   2. 类别矩阵：Entity 允许 specific 四类（.proxy.<名>/.proxy.opr.<名>/
    //      .proxy.get.<名>/.proxy.set.<名>）与 wildcard 四类（.proxy.*/
    //      .proxy.opr.*/.proxy.get.*/.proxy.set.*）；Value 仅 .proxy.get/
    //      .proxy.set；Method 仅 .proxy.call。不带任何 proxy 的 wrapper 是
    //      合法的纯状态修饰器（§14.5 用法：`obj:W.member` 读写状态），
    //      不强制实现 get/call（§14.3「必须至少实现 get」是拦截能力的
    //      适用性说明而非声明义务；「只实现 get 只适用于只读变量」归
    //      Value 链计算时判定——Value/Method 烘焙归后续里程碑）。
    //   3. wildcard canonical shape（编译器固定，逐参数校验——参数名是
    //      canonical ABI 的一部分，泛型参数名不校验）：
    //        .proxy.* / .proxy.opr.*：
    //          \<named TNamedArgs..., TUnnamedArgs..., TReturn\>
    //          (symbol: String, namedArgs: named TNamedArgs...,
    //           unnamedArgs: TUnnamedArgs...): TReturn
    //        .proxy.get.*：\<TValue\>(symbol: String, value: TValue): TValue
    //        .proxy.set.*：\<TValue\>(symbol: String, value: TValue)（无返回）
    //   4. specific 形状：.proxy.get.<名>/.proxy.set.<名> 与 Value 的
    //      .proxy.get/.proxy.set 同形——恰一泛型参数 + 唯一 value 参数
    //      （类型即该泛型参数），get 返回该类型、set 无返回；.proxy.<名>/
    //      .proxy.opr.<名> 不得声明泛型参数（应用时与被代理成员按结构
    //      全等匹配，TTarget 代入宿主后判定）。
    //   5. .proxy.call 双形态（§14.4）：带 .name 首参者必须是 wildcard 形态
    //      (.name: String, args: named Any...): Any；否则为 specific 形态——
    //      恰一泛型参数且返回类型即该参数（参数列表镜像目标方法，应用时
    //      判定；Method wrapper 烘焙归后续里程碑）。
    internal sealed class ProxyShapeChecker : ResolverVisitor<ProxyShapeChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.TypeEntries)
            {
                if (!entry.InGraph) continue;
                var type = (TypeSymbol)entry.Symbol;
                if (type.Kind != TypeKind.Wrapper || type.IsBuiltin) continue;
                if (type.WrapperTarget is not { } targetKind) continue;   // 已诊断

                CheckGenericArity(type, targetKind, entry, env);

                foreach (var member in type.Methods)
                {
                    if (!member.Name.StartsWith(".proxy.")) continue;
                    var span = env.EntryOfSymbol[member].Node.Span;
                    var category = Classify(member.Name);
                    if (!IsAllowed(targetKind, category))
                    {
                        env.Error(span,
                            $"Proxy '{member.Name}' is not allowed on {targetKind} wrapper '{type.Name}'");
                        continue;
                    }
                    switch (category)
                    {
                        case ProxyCategory.MethodWildcard:
                        case ProxyCategory.OperatorWildcard:
                            if (!HasCanonicalArgsWildcardShape(member, env))
                            {
                                env.Error(span, $"Wildcard proxy '{member.Name}' must have the " +
                                    "canonical shape (symbol: String, namedArgs: named TNamedArgs..., " +
                                    $"unnamedArgs: TUnnamedArgs...): TReturn (§14.2)");
                            }
                            break;
                        case ProxyCategory.GetterWildcard:
                        case ProxyCategory.SetterWildcard:
                            if (!HasCanonicalAccessWildcardShape(member,
                                isGetter: category == ProxyCategory.GetterWildcard, env))
                            {
                                env.Error(span, $"Wildcard proxy '{member.Name}' must have the " +
                                    "canonical shape (symbol: String, value: TValue): TValue " +
                                    "(setter returns void) (§14.2)");
                            }
                            break;
                        case ProxyCategory.ValueGetter:
                        case ProxyCategory.ValueSetter:
                            goto case ProxyCategory.SpecificGetter;
                        case ProxyCategory.SpecificGetter:
                        case ProxyCategory.SpecificSetter:
                            if (!HasValueProxyShape(member,
                                returnsValue: category is ProxyCategory.SpecificGetter
                                    or ProxyCategory.ValueGetter))
                            {
                                env.Error(span, $"Accessor proxy '{member.Name}' must declare " +
                                    "exactly one generic parameter and a single 'value' parameter " +
                                    "of that type, returning it (getter) or void (setter) (§14.2)");
                            }
                            break;
                        case ProxyCategory.SpecificMethod:
                        case ProxyCategory.SpecificOperator:
                            if (member.GenericParameters.Count != 0)
                            {
                                env.Error(span, $"Specific proxy '{member.Name}' cannot declare " +
                                    "generic parameters (§14.2)");
                            }
                            break;
                        case ProxyCategory.MethodCall:
                            CheckMethodCallShape(member, span, env);
                            break;
                    }
                }
            }
        }

        // proxy 名分类（名恒以 ".proxy." 开头，Parser 保证）
        private enum ProxyCategory
        {
            MethodWildcard,      // .proxy.*
            GetterWildcard,      // .proxy.get.*
            SetterWildcard,      // .proxy.set.*
            OperatorWildcard,    // .proxy.opr.*
            ValueGetter,         // .proxy.get（Value wrapper）
            ValueSetter,         // .proxy.set（Value wrapper）
            MethodCall,          // .proxy.call（Method wrapper）
            SpecificGetter,      // .proxy.get.<名>
            SpecificSetter,      // .proxy.set.<名>
            SpecificOperator,    // .proxy.opr.<名>
            SpecificMethod,      // .proxy.<名>
        }

        private static ProxyCategory Classify(string name)
        {
            var rest = name[".proxy.".Length..];
            if (rest == "*") return ProxyCategory.MethodWildcard;
            if (rest == "get.*") return ProxyCategory.GetterWildcard;
            if (rest == "set.*") return ProxyCategory.SetterWildcard;
            if (rest == "opr.*") return ProxyCategory.OperatorWildcard;
            if (rest == "get") return ProxyCategory.ValueGetter;
            if (rest == "set") return ProxyCategory.ValueSetter;
            if (rest == "call") return ProxyCategory.MethodCall;
            if (rest.StartsWith("get.")) return ProxyCategory.SpecificGetter;
            if (rest.StartsWith("set.")) return ProxyCategory.SpecificSetter;
            if (rest.StartsWith("opr.")) return ProxyCategory.SpecificOperator;
            return ProxyCategory.SpecificMethod;
        }

        private static bool IsAllowed(WrapperTargetKind targetKind, ProxyCategory category)
        {
            return targetKind switch
            {
                WrapperTargetKind.Entity => category is not ProxyCategory.ValueGetter
                    and not ProxyCategory.ValueSetter and not ProxyCategory.MethodCall,
                WrapperTargetKind.Value => category is ProxyCategory.ValueGetter
                    or ProxyCategory.ValueSetter,
                _ => category == ProxyCategory.MethodCall,
            };
        }

        // 规则 1：泛型元数（Entity 至多一个 = TTarget 角色；Value/Method 零个）
        private static void CheckGenericArity(TypeSymbol type, WrapperTargetKind targetKind,
            DeclEntry entry, ResolveEnvironment env)
        {
            if (targetKind == WrapperTargetKind.Entity)
            {
                if (type.GenericParameters.Count > 1)
                {
                    env.Error(entry.Node.Span,
                        $"Entity wrapper '{type.Name}' must declare at most one generic parameter " +
                        "(the TTarget role)");
                }
            }
            else if (type.GenericParameters.Count > 0)
            {
                env.Error(entry.Node.Span,
                    $"{targetKind} wrapper '{type.Name}' cannot declare generic parameters (§14.2)");
            }
        }

        // .proxy.* / .proxy.opr.* canonical shape
        private static bool HasCanonicalArgsWildcardShape(MethodSymbol method, ResolveEnvironment env)
        {
            var gps = method.GenericParameters;
            var ps = method.Parameters;
            return gps.Count == 3
                && gps[0].IsNamedVariadic       // named 同置 IsVariadic（两标记独立填充）
                && gps[1].IsVariadic && !gps[1].IsNamedVariadic
                && !gps[2].IsVariadic && !gps[2].IsNamedVariadic
                && ps.Count == 3
                && ps[0].Name == "symbol" && IsString(ps[0].Type, env)
                && ps[1].Name == "namedArgs" && ps[1].IsNamedVariadic
                    && ReferenceEquals(ps[1].Type, gps[0])
                && ps[2].Name == "unnamedArgs" && ps[2].IsVariadic && !ps[2].IsNamedVariadic
                    && ReferenceEquals(ps[2].Type, gps[1])
                && ReferenceEquals(method.ReturnType, gps[2]);
        }

        // .proxy.get.* / .proxy.set.* canonical shape
        private static bool HasCanonicalAccessWildcardShape(MethodSymbol method, bool isGetter,
            ResolveEnvironment env)
        {
            var gps = method.GenericParameters;
            var ps = method.Parameters;
            var shapeOk = gps.Count == 1
                && !gps[0].IsVariadic && !gps[0].IsNamedVariadic
                && ps.Count == 2
                && ps[0].Name == "symbol" && IsString(ps[0].Type, env)
                && ps[1].Name == "value" && !ps[1].IsVariadic && !ps[1].IsNamedVariadic
                    && ReferenceEquals(ps[1].Type, gps[0]);
            return shapeOk && (isGetter
                ? ReferenceEquals(method.ReturnType, gps[0])
                : method.ReturnType == null);
        }

        // specific .proxy.get.<名>/.proxy.set.<名> 与 Value .proxy.get/.proxy.set 同形
        private static bool HasValueProxyShape(MethodSymbol method, bool returnsValue)
        {
            var gps = method.GenericParameters;
            var ps = method.Parameters;
            var shapeOk = gps.Count == 1
                && !gps[0].IsVariadic && !gps[0].IsNamedVariadic
                && ps.Count == 1
                && ps[0].Name == "value" && !ps[0].IsVariadic && !ps[0].IsNamedVariadic
                    && ReferenceEquals(ps[0].Type, gps[0]);
            return shapeOk && (returnsValue
                ? ReferenceEquals(method.ReturnType, gps[0])
                : method.ReturnType == null);
        }

        // .proxy.call 双形态（§14.4）
        private static void CheckMethodCallShape(MethodSymbol member, CharRange? span,
            ResolveEnvironment env)
        {
            var gps = member.GenericParameters;
            var ps = member.Parameters;
            if (ps.Count > 0 && ps[0].Name.StartsWith('.'))
            {
                // wildcard 形态：(.name: String, args: named Any...): Any
                var ok = gps.Count == 0
                    && ps.Count == 2
                    && ps[0].Name == ".name" && IsString(ps[0].Type, env)
                    && ps[1].Name == "args" && ps[1].IsNamedVariadic
                        && ReferenceEquals(ps[1].Type, env.Unit.Symbols.Bootstrap.Any)
                    && ReferenceEquals(member.ReturnType, env.Unit.Symbols.Bootstrap.Any);
                if (!ok)
                {
                    env.Error(span, "Wildcard '.proxy.call' must have shape " +
                        "(.name: String, args: named Any...): Any (§14.4)");
                }
            }
            else
            {
                // specific 形态：恰一泛型参数作返回类型，参数列表镜像目标方法
                var ok = gps.Count == 1 && !gps[0].IsVariadic && !gps[0].IsNamedVariadic
                    && ReferenceEquals(member.ReturnType, gps[0]);
                if (!ok)
                {
                    env.Error(span, "Specific '.proxy.call' must declare exactly one generic " +
                        "parameter used as the return type (§14.4)");
                }
            }
        }

        private static bool IsString(SemanticSymbol? type, ResolveEnvironment env)
        {
            return ReferenceEquals(type, env.Unit.Symbols.Bootstrap.String);
        }
    }
}
