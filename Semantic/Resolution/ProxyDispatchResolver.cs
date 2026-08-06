namespace LatteCompiler
{
    // ===== S11a：wrapper 派发链计算与符号合成（ROADMAP S11a 后半）=====
    //
    // 前置：WrapperApplicationChecker（应用登记，WrapperApplication 记录）
    // 与 ProxyShapeChecker（声明侧形状校验）已运行。本阶段两件事：
    //
    //   1. `.wrapper.` 隐藏字段合成（BIL §5.3 命名、§8.3.1 声明形态）：
    //      Entity 应用挂宿主类型；interface 传染落到每个实现者（TTarget
    //      按实现者重建构造）；Value 应用（实例字段）挂字段宿主。同一
    //      宿主上出现同名隐藏字段（同一 wrapper 的多次应用）是编译错误。
    //      栈上局部与全局/静态目标的存储不在这里（栈帧/全局静态存储
    //      归后续里程碑）。
    //   2. Entity wrapper 派发链计算（§14.6）与逐组合特化符号合成：
    //      被修饰成员（实例方法/运算符/已声明访问器，HasBody）→
    //      outer→inner [(应用, 命中 specific|wildcard proxy)]。specific
    //      名命中且形状全等（TTarget 代入宿主后参数名/参数类型/返回类型
    //      逐项一致）优先；名命中但形状不符是编译错误；名未命中落类别
    //      wildcard；两者皆无则本层不拦截（inert）。泛型成员只参与
    //      wildcard（specific 形状无法表达成员泛型参数）。链非空时合成
    //      链环特化 fn（名 .proxy.<序>.<成员键>，签名 = 成员签名拷贝）
    //      与原始体 fn（名 .wrapped.<成员键>）——wrapper 泛型代入在
    //      应用记录已完成（TTarget 显形）。
    //
    // 边界（归后续里程碑）：Value/Method wrapper 链（.proxy.get/
    // .proxy.set/.proxy.call 拦截）、interface 实现者的链继承与
    // override 覆写链、无访问器字段的 get/set 拦截（需自动访问器合成
    // + P4 接线）、async 成员交互（S13）、static/init 成员不拦截、
    // 合成 fn 的 BIL 发射（S11d）与隐藏字段声明发射（S11c）。
    internal sealed class ProxyDispatchResolver : ResolverVisitor<ProxyDispatchResolver>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            SynthesizeHiddenFields(env);
            ComputeDispatchChains(env);
        }

        // ===== 1. `.wrapper.` 隐藏字段合成（BIL §5.3/§8.3.1）=====
        private static void SynthesizeHiddenFields(ResolveEnvironment env)
        {
            foreach (var entry in env.TypeEntries)
            {
                if (!entry.InGraph) continue;
                var host = (TypeSymbol)entry.Symbol;
                if (host.IsBuiltin || host.ConstructedFrom != null) continue;
                // Entity 应用（类型上的 AppliedWrappers 恒为 Entity 类，矩阵已查）。
                // interface 自身不合成：interface 不产生实例，wrapper 实例落在
                // 每个实现者上（§14.9，见下）
                if (host.Kind != TypeKind.Interface)
                {
                    foreach (var application in host.AppliedWrappers)
                    {
                        if (HasInvalidGenericArity(application.WrapperDefinition)) continue;
                        application.HiddenField = AddHiddenField(env, host,
                            application.Wrapper, application.Syntax);
                    }
                }
                // interface 传染（§14.9）：实现者获得 interface 应用的隐藏
                // 字段（TTarget 按实现者重建构造；应用记录归 interface，
                // HiddenField 槽不回写——一记录对多实现者）
                foreach (var iface in host.Interfaces)
                {
                    foreach (var application in (iface.ConstructedFrom ?? iface).AppliedWrappers)
                    {
                        AddHiddenField(env, host,
                            ReconstructForImplementer(application, host, env), application.Syntax);
                    }
                }
                // Value 应用（实例字段）：隐藏字段挂字段宿主
                // （快照遍历：本循环内向 Fields 追加合成字段）
                foreach (var field in host.Fields.ToArray())
                {
                    if (field.IsStatic || field.IsCompilerGenerated) continue;
                    foreach (var application in field.AppliedWrappers)
                    {
                        if (HasInvalidGenericArity(application.WrapperDefinition)) continue;
                        application.HiddenField = AddHiddenField(env, host,
                            application.Wrapper, application.Syntax);
                    }
                }
            }
        }

        // 元数非法的 wrapper 声明（Entity 多参数 / Value·Method 带参数）已由
        // ProxyShapeChecker 诊断——合成阶段一律静默跳过，不次生噪音
        private static bool HasInvalidGenericArity(TypeSymbol wrapperDefinition)
        {
            return wrapperDefinition.WrapperTarget == WrapperTargetKind.Entity
                ? wrapperDefinition.GenericParameters.Count > 1
                : wrapperDefinition.GenericParameters.Count > 0;
        }

        private static FieldSymbol? AddHiddenField(ResolveEnvironment env, TypeSymbol host,
            TypeSymbol wrapperType, AnnotationASTNode syntax)
        {
            // §5.3：字段名部分 = .wrapper. + wrapper canonical 完整类型名
            //（构造类型含实参段；同一宿主同名即重复应用）
            var name = ".wrapper." + CanonicalSymbolPrinter.PrintTypeReference(wrapperType);
            if (host.Fields.Any(f => f.Name == name))
            {
                env.Error(syntax.Span,
                    $"Wrapper '{wrapperType.Name}' has multiple applications on host " +
                    $"'{host.Name}' (hidden field collision, §5.3)");
                return null;
            }
            var field = new FieldSymbol(name, owner: host, fieldType: wrapperType)
            {
                Accessibility = Accessibility.Private,
                IsCompilerGenerated = true,
            };
            host.Fields.Add(field);
            return field;
        }

        // interface 传染的构造重建：TTarget 从 interface 换为实现者
        private static TypeSymbol ReconstructForImplementer(WrapperApplication application,
            TypeSymbol implementer, ResolveEnvironment env)
        {
            if (application.Wrapper.ConstructedFrom is { } definition
                && definition.GenericParameters.Count == 1)
            {
                return env.Unit.Symbols.GetConstructedType(definition, implementer);
            }
            return application.Wrapper;
        }

        // ===== 2. 派发链计算与特化符号合成（§14.6）=====
        private static void ComputeDispatchChains(ResolveEnvironment env)
        {
            foreach (var entry in env.TypeEntries)
            {
                if (!entry.InGraph) continue;
                var host = (TypeSymbol)entry.Symbol;
                if (host.IsBuiltin || host.ConstructedFrom != null) continue;
                if (host.AppliedWrappers.Count == 0) continue;
                // 快照遍历：合成符号会追加进 Methods/Fields
                foreach (var member in host.Methods.ToArray())
                {
                    if (member.Kind != MethodKind.Regular && member.Kind != MethodKind.Operator)
                    {
                        continue;
                    }
                    if (member.Name.StartsWith('.') || member.IsStatic || member.IsNative
                        || member.IsAbstract || !member.HasBody)
                    {
                        continue;
                    }
                    var isOperator = member.Kind == MethodKind.Operator;
                    BuildChainForMember(env, host, member,
                        memberKey: isOperator ? "opr." + member.Name : member.Name,
                        specificName: (isOperator ? ".proxy.opr." : ".proxy.") + member.Name,
                        wildcardName: isOperator ? ".proxy.opr.*" : ".proxy.*");
                }
                // 访问器链：仅已声明访问器的字段（访问器体由 P3 绑定/合成，
                // 恒有产物——HasBody 标记对访问器不适用；无访问器字段的
                // get/set 拦截需自动访问器合成，归后续里程碑）
                foreach (var field in host.Fields.ToArray())
                {
                    if (field.IsStatic || field.IsCompilerGenerated || field.Name.StartsWith('.'))
                    {
                        continue;
                    }
                    if (field.Getter is { } getter)
                    {
                        BuildChainForMember(env, host, getter,
                            memberKey: "get." + field.Name,
                            specificName: ".proxy.get." + field.Name,
                            wildcardName: ".proxy.get.*");
                    }
                    if (field.Setter is { } setter)
                    {
                        BuildChainForMember(env, host, setter,
                            memberKey: "set." + field.Name,
                            specificName: ".proxy.set." + field.Name,
                            wildcardName: ".proxy.set.*");
                    }
                }
            }
        }

        private static void BuildChainForMember(ResolveEnvironment env, TypeSymbol host,
            MethodSymbol member, string memberKey, string specificName, string wildcardName)
        {
            if (ContainsErrorType(member)) return;   // 毒化静默
            // 第一趟：逐应用定命中（outer→inner，§14.6 specific 优先于 wildcard）
            var hits = new List<(WrapperApplication Application, MethodSymbol Proxy, ProxyLinkKind Kind)>();
            foreach (var application in host.AppliedWrappers)
            {
                var wrapperDef = application.WrapperDefinition;
                if (HasInvalidGenericArity(wrapperDef)) continue;
                MethodSymbol? proxy = null;
                var kind = ProxyLinkKind.Specific;
                var specific = wrapperDef.Methods.FirstOrDefault(m => m.Name == specificName);
                if (specific != null && !ContainsErrorType(specific))
                {
                    if (member.Kind is MethodKind.Getter or MethodKind.Setter)
                    {
                        // 访问器 specific 形状 canonical（value: TField），名中即匹配
                        proxy = specific;
                    }
                    else if (member.GenericParameters.Count == 0
                        && SpecificShapeMatches(specific, member, application, env))
                    {
                        proxy = specific;
                    }
                    else if (member.GenericParameters.Count == 0)
                    {
                        env.Error(env.EntryOfSymbol[member].Node.Span,
                            $"Specific proxy '{specific.Name}' on wrapper '{wrapperDef.Name}' " +
                            $"does not match the shape of member '{member.Name}' of " +
                            $"'{host.Name}' (§14.2)");
                        continue;
                    }
                    // 泛型成员：specific 无法表达成员泛型参数，落 wildcard
                }
                if (proxy == null)
                {
                    proxy = wrapperDef.Methods.FirstOrDefault(m => m.Name == wildcardName);
                    kind = ProxyLinkKind.Wildcard;
                }
                if (proxy == null) continue;   // 本层不拦截（inert）
                hits.Add((application, proxy, kind));
            }
            if (hits.Count == 0) return;
            // 第二趟：合成原始体与逐环特化（签名 = 成员签名拷贝）
            var original = SynthesizeBodySymbol(host, member, ".wrapped." + memberKey, env);
            host.Methods.Add(original);
            member.WrappedBodySymbol = original;
            member.WrapperChain = new List<MethodSymbol>();
            for (var i = 0; i < hits.Count; i++)
            {
                var link = SynthesizeBodySymbol(host, member, ".proxy." + i + "." + memberKey, env);
                link.ProxySpecialization = new ProxySpecializationInfo(
                    hits[i].Proxy, hits[i].Application, hits[i].Kind, member, original);
                member.WrapperChain.Add(link);
                host.Methods.Add(link);
            }
        }

        // 特化/原始体 fn：名 + Regular 种类 + 宿主 Owner + 成员签名拷贝
        // （泛型参数与参数符号全部新建，签名类型中的成员泛型参数替换为
        // 新符号副本）；发射归 S11d（LocalSymbolEmitters 跳过 "." 前缀名）
        private static MethodSymbol SynthesizeBodySymbol(TypeSymbol host, MethodSymbol member,
            string name, ResolveEnvironment env)
        {
            var symbol = new MethodSymbol(name, MethodKind.Regular, owner: host)
            {
                HasBody = true,
            };
            var genericMap = new Dictionary<GenericParameterSymbol, GenericParameterSymbol>(
                ReferenceEqualityComparer.Instance);
            foreach (var gp in member.GenericParameters)
            {
                var copy = new GenericParameterSymbol(gp.Name, gp.IsVariadic, gp.IsNamedVariadic);
                symbol.GenericParameters.Add(copy);
                genericMap[gp] = copy;
            }
            foreach (var parameter in member.Parameters)
            {
                symbol.Parameters.Add(new ParameterSymbol(parameter.Name,
                    SubstituteSignatureTypes(parameter.Type, genericMap, env),
                    defaultValue: null,
                    isVariadic: parameter.IsVariadic,
                    isNamedVariadic: parameter.IsNamedVariadic));
            }
            symbol.ReturnType = SubstituteSignatureTypes(member.ReturnType, genericMap, env);
            return symbol;
        }

        // 签名类型中的成员泛型参数替换为拷贝符号（嵌套构造逐实参代入）
        private static SemanticSymbol? SubstituteSignatureTypes(SemanticSymbol? type,
            Dictionary<GenericParameterSymbol, GenericParameterSymbol> genericMap,
            ResolveEnvironment env)
        {
            if (type is GenericParameterSymbol gp)
            {
                return genericMap.TryGetValue(gp, out var copy) ? copy : gp;
            }
            if (type is TypeSymbol { ConstructedFrom: not null } constructed)
            {
                var args = new SemanticSymbol[constructed.TypeArguments!.Count];
                for (var i = 0; i < args.Length; i++)
                {
                    args[i] = SubstituteSignatureTypes(constructed.TypeArguments[i], genericMap, env)!;
                }
                return env.Unit.Symbols.GetConstructedType(constructed.ConstructedFrom!, args);
            }
            return type;
        }

        // specific 形状全等判定（§14.2）：TTarget 代入宿主后参数名/参数类型/
        // 返回类型逐项一致（访问器 specific 不走此判定——名中即匹配）
        private static bool SpecificShapeMatches(MethodSymbol proxy, MethodSymbol member,
            WrapperApplication application, ResolveEnvironment env)
        {
            if (proxy.Parameters.Count != member.Parameters.Count) return false;
            for (var i = 0; i < proxy.Parameters.Count; i++)
            {
                if (proxy.Parameters[i].Name != member.Parameters[i].Name) return false;
                if (!ReferenceEquals(
                        SubstituteViaApplication(proxy.Parameters[i].Type, application, env),
                        member.Parameters[i].Type))
                {
                    return false;
                }
            }
            return ReferenceEquals(
                SubstituteViaApplication(proxy.ReturnType, application, env), member.ReturnType);
        }

        // wrapper 泛型参数按应用记录代入（Entity 恰一泛型参数时
        // application.Wrapper 是 TTarget=宿主 的构造类型，经 SymbolGraph
        // Substitute 单源代入；非泛型 wrapper 原样返回）
        private static SemanticSymbol? SubstituteViaApplication(SemanticSymbol? type,
            WrapperApplication application, ResolveEnvironment env)
        {
            if (application.Wrapper.ConstructedFrom is { } definition)
            {
                return env.Substitute(type, definition, application.Wrapper);
            }
            return type;
        }

        // 签名含毒化类型（ErrorType）的符号跳过——上游已诊断，不次生噪音
        private static bool ContainsErrorType(MethodSymbol method)
        {
            if (method.ReturnType is ErrorTypeSymbol) return true;
            foreach (var parameter in method.Parameters)
            {
                if (parameter.Type is ErrorTypeSymbol) return true;
            }
            return false;
        }
    }
}
