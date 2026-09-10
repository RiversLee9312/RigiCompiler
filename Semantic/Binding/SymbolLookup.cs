namespace RigiCompiler
{
    // 共享符号查询设施：实例成员沿 BaseType 链查找、泛型字段最小替换、
    // 实例 operator 按名与参数个数查找、可赋值性判定、泛型参数有效成员类型
    // （SYNTAX §3.6 / §13.3）。接口 lookup 另走 InterfaceClosure。
    // 全部为无副作用纯查询，各簇 visitor 共用。
    internal static class SymbolLookup
    {
        // 字段声明类型按 receiver 宿主代入：声明类型中出现的宿主泛型
        // 参数（裸 T 或嵌套构造 Array\<T\> / Node\<T\>?）沿 receiver
        // BaseType 链找到 field.Owner 所在层，经 SubstituteHost 整树替换。
        // 定义级 receiver（泛型类体内 this 提升前，或链上无匹配构造）
        // 原样返回（宿主泛型参数身份保留）。receiverType 为 null 时直通。
        // 调用方保证 field.FieldType 非 null——返回值恒非空
        public static SemanticSymbol SubstituteFieldType(FieldSymbol field,
            TypeSymbol? receiverType, SymbolGraph symbols)
        {
            if (receiverType == null || field.Owner == null) return field.FieldType!;
            for (var t = receiverType; t != null; t = t.BaseType)
            {
                var owner = t.ConstructedFrom ?? t;
                if (!ReferenceEquals(owner, field.Owner)) continue;
                return SubstituteHost(field.FieldType!, owner, t, symbols);
            }
            return field.FieldType!;
        }

        // 泛型定义作为 this/receiver 时提升为「自身具化」构造类型：
        // Box → Box\<T\>（T 即定义自身的泛型参数）。已是构造类型或
        // 非泛型原样返回。null 透传。
        public static TypeSymbol? AsSelfConstructed(TypeSymbol? type, SymbolGraph symbols)
        {
            if (type == null || type.ConstructedFrom != null
                || type.GenericParameters.Count == 0)
            {
                return type;
            }
            var args = new SemanticSymbol[type.GenericParameters.Count];
            for (int i = 0; i < args.Length; i++)
            {
                args[i] = type.GenericParameters[i];
            }
            return symbols.GetConstructedType(type, args);
        }

        // 方法签名类型的宿主代入（不走 OverloadResolution 的特判路径：
        // 索引写模式 setAtIndex 形参、索引复合赋值写回校验）：沿 receiver
        // 的 BaseType 链找到 method.Owner 所在层（同 FindInstanceMethods
        // 链序口径）——该层为构造类型时按 SubstituteHost 代入宿主泛型
        // 参数（Box\<T\>.setAtIndex(index, element: T) 在 Box\<i32\> 上
        // element → i32）；链上无宿主层（ext/定义级直通）或 method 无
        // 宿主时原样返回
        public static SemanticSymbol SubstituteForReceiver(SemanticSymbol type,
            MethodSymbol method, TypeSymbol receiverType, SymbolGraph symbols)
        {
            if (method.Owner == null) return type;
            for (var t = receiverType; t != null; t = t.BaseType)
            {
                var owner = t.ConstructedFrom ?? t;
                if (!ReferenceEquals(owner, method.Owner)) continue;
                return SubstituteHost(type, owner, t, symbols);
            }
            // class 实现 IBox\<i32\> 时方法宿主是接口定义，BaseType 链
            // 到不了 IBox\<i32\>——沿接口闭包再找一层代入
            foreach (var iface in OverrideChecker.InterfaceClosure(receiverType, symbols))
            {
                var owner = iface.ConstructedFrom ?? iface;
                if (!ReferenceEquals(owner, method.Owner)) continue;
                return SubstituteHost(type, owner, iface, symbols);
            }
            return type;
        }

        // 有效成员类型（SYNTAX §3.6 / §13 泛型参数操作数）：
        // TypeSymbol → 自身；GenericParameterSymbol 有 extends B 时 → B
        // （构造界已是代入/保留宿主参数身份的 TypeSymbol；界本身是外层
        // 泛型参数时递归取其有效类型）；无约束 / 仅 supers / 仅 with → Any。
        // 成员解析（方法、operator、字段、索引）统一按本类型查找。
        public static TypeSymbol EffectiveMemberType(SemanticSymbol type, BindEnvironment env)
        {
            return EffectiveMemberType(type, env.B);
        }

        // BootstrapSymbols 版（P2 填入点检查无 BindEnvironment，g4 框架）
        public static TypeSymbol EffectiveMemberType(SemanticSymbol type, BootstrapSymbols b)
        {
            return EffectiveMemberTypeCore(type, b, null);
        }

        // visiting 防 T extends T / 环界沿 GP 链发散（环则保守回落 Any）
        private static TypeSymbol EffectiveMemberTypeCore(SemanticSymbol type, BootstrapSymbols b,
            HashSet<GenericParameterSymbol>? visiting)
        {
            if (type is TypeSymbol typeSymbol) return typeSymbol;
            if (type is GenericParameterSymbol parameter)
            {
                visiting ??= new HashSet<GenericParameterSymbol>();
                if (!visiting.Add(parameter)) return b.Any;
                try
                {
                    foreach (var constraint in parameter.Constraints)
                    {
                        if (constraint.Kind != GenericConstraintKind.Extends) continue;
                        if (constraint.Bound is TypeSymbol bound) return bound;
                        if (constraint.Bound is GenericParameterSymbol outer)
                        {
                            return EffectiveMemberTypeCore(outer, b, visiting);
                        }
                    }
                    return b.Any;
                }
                finally
                {
                    visiting.Remove(parameter);
                }
            }
            return b.Any;
        }

        // 泛型参数是否仅有 supers/with（无 extends）——运算符诊断用
        public static bool HasOnlyNonMemberConstraint(SemanticSymbol type)
        {
            if (type is not GenericParameterSymbol parameter
                || parameter.Constraints.Count == 0)
            {
                return false;
            }
            return parameter.Constraints.All(c =>
                c.Kind is GenericConstraintKind.Supers or GenericConstraintKind.With);
        }

        public static string OperatorNotDefinedMessage(string token, SemanticSymbol type)
        {
            var display = BoundAnalysis.TypeDisplay(type);
            if (HasOnlyNonMemberConstraint(type) && type is GenericParameterSymbol parameter)
            {
                var kind = parameter.Constraints[0].Kind == GenericConstraintKind.Supers
                    ? "supers" : "with";
                return $"Operator '{token}' is not defined for type '{display}' " +
                    $"({kind} constraint does not provide members)";
            }
            return $"Operator '{token}' is not defined for type '{display}'";
        }

        // 实例方法查找：receiver 静态类型沿 BaseType 链（接口 receiver
        // 即查接口自身，BaseType 为 null 自然终止；ext 注册成员已在目标
        // 类型成员表）。Regular 实例方法 + operator（S9f 复核 M69 注记：
        // SYNTAX §4.2 定稿「operator 名字形式与普通方法同规则」——名字
        // 调用 a.plus\<T>(b) 命中 operator；运算符位置（a + b）/for 头
        // （EnumerateInRange）/索引（getAtIndex）仍走各自专用解析），
        // init/getter/setter 归各自里程碑。
        // 构造类型的成员表在其泛型定义上（构造器不复制成员列表，
        // S7f 起经 ConstructedFrom 回退——实参替换在使用侧特判）。
        // override 遮蔽（S8e，§9.2.1）：override 在分派语义上替换继承
        // 成员——派生层已收集的 override 与基类层候选签名严格相等时
        // 基类候选不进重载候选池（否则同签名候选歧义）。
        // 接口闭包：lookup 类型本身是 interface 时收全部闭包成员（与接口
        // 类型变量调基接口成员同一口径）；class/struct 只并入有体默认实现
        // （SYNTAX §11 / §9.2.1：带默认实现的接口成员由实现类隐式继承；
        // 无体成员必须已在类成员表，再收会与 override 双候选）
        public static List<MethodSymbol> FindInstanceMethods(TypeSymbol type, string name,
            SymbolGraph? symbols = null)
        {
            var result = new List<MethodSymbol>();
            CollectInstanceMethods(result, type, name, symbols);
            AppendInterfaceMembers(result, type, name, symbols, operatorsOnly: false,
                parameterCount: -1);
            return result;
        }

        private static void CollectInstanceMethods(List<MethodSymbol> result, TypeSymbol type,
            string name, SymbolGraph? symbols)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                CollectMethodsFromOwner(result, t.ConstructedFrom ?? t, name,
                    operatorsOnly: false, parameterCount: -1, type, symbols);
            }
        }

        private static void CollectMethodsFromOwner(List<MethodSymbol> result, TypeSymbol owner,
            string name, bool operatorsOnly, int parameterCount, TypeSymbol receiver, SymbolGraph? symbols)
        {
            foreach (var method in owner.Methods.Where(m => m.Name == name && !m.IsStatic
                && (operatorsOnly
                    ? m.Kind == MethodKind.Operator && m.Parameters.Count == parameterCount
                    : m.Kind is MethodKind.Regular or MethodKind.Operator)))
            {
                if (result.Any(derived => derived.IsOverride && SignatureForReceiver(derived, receiver, symbols)
                    .Matches(SignatureForReceiver(method, receiver, symbols))))
                {
                    continue;
                }
                if (result.Any(existing => ReferenceEquals(existing, method))) continue;
                result.Add(method);
            }
        }

        // 接口 lookup 收全部闭包成员；class/struct 只收有体默认实现
        // （已由类/基类覆盖的签名跳过，避免与 override 双候选）
        private static void AppendInterfaceMembers(List<MethodSymbol> result, TypeSymbol type,
            string name, SymbolGraph? symbols, bool operatorsOnly, int parameterCount)
        {
            if (symbols == null) return;
            var definition = type.ConstructedFrom ?? type;
            if (definition.Kind == TypeKind.Interface)
            {
                foreach (var iface in OverrideChecker.InterfaceClosure(type, symbols))
                {
                    CollectMethodsFromOwner(result, iface.ConstructedFrom ?? iface, name,
                        operatorsOnly, parameterCount, type, symbols);
                }
                return;
            }
            foreach (var iface in OverrideChecker.InterfaceClosure(type, symbols))
            {
                AppendInheritedDefaultMethods(result, iface, name, symbols, operatorsOnly,
                    parameterCount);
            }
        }

        // 实现类隐式继承接口默认方法：只收 HasBody，且类/基类尚未覆盖
        // 该签名（代入后比较）。两接口同签名默认实现都进池，交重载解析
        private static void AppendInheritedDefaultMethods(List<MethodSymbol> result,
            TypeSymbol iface, string name, SymbolGraph symbols, bool operatorsOnly,
            int parameterCount)
        {
            var owner = iface.ConstructedFrom ?? iface;
            foreach (var method in owner.Methods.Where(m => m.Name == name && !m.IsStatic
                && m.HasBody
                && (operatorsOnly
                    ? m.Kind == MethodKind.Operator && m.Parameters.Count == parameterCount
                    : m.Kind is MethodKind.Regular or MethodKind.Operator)))
            {
                if (result.Any(existing => ReferenceEquals(existing, method))) continue;
                var view = OverrideChecker.SignatureView.Of(method, owner, iface, symbols);
                if (result.Any(existing => existing.Owner?.Kind != TypeKind.Interface
                    && OverrideChecker.SignatureView.Raw(existing).Matches(view)))
                {
                    continue;
                }
                result.Add(method);
            }
        }

        // 覆写槽比较复用声明检查的签名视图：先代入 receiver 的闭合宿主，
        // 再比较方法泛型元数及同构参数，不能拿基类模板 T 与派生 i32 比引用。
        private static OverrideChecker.SignatureView SignatureForReceiver(MethodSymbol method,
            TypeSymbol receiver, SymbolGraph? symbols)
        {
            if (symbols != null)
            {
                for (var t = receiver; t != null; t = t.BaseType)
                {
                    var owner = t.ConstructedFrom ?? t;
                    if (ReferenceEquals(owner, method.Owner))
                        return OverrideChecker.SignatureView.Of(method, owner, t, symbols);
                }
            }
            return OverrideChecker.SignatureView.Raw(method);
        }
        // 实例字段查找：同链（仅实例字段；构造类型回退泛型定义，同 FindInstanceMethods）
        public static FieldSymbol? FindInstanceField(TypeSymbol type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var owner = t.ConstructedFrom ?? t;
                var hit = owner.Fields.FirstOrDefault(f => f.Name == name && !f.IsStatic);
                if (hit != null) return hit;
            }
            return null;
        }

        // 实例 operator 查找（S8c 索引访问 getAtIndex/setAtIndex）：receiver
        // 静态类型沿 BaseType 链按名字与参数个数过滤（ext 注册 operator 已在
        // 目标类型成员表；构造类型回退泛型定义，同 FindInstanceMethods）
        public static List<MethodSymbol> FindInstanceOperators(TypeSymbol type, string name,
            int parameterCount, SymbolGraph? symbols = null)
        {
            var result = new List<MethodSymbol>();
            for (var t = type; t != null; t = t.BaseType)
            {
                CollectMethodsFromOwner(result, t.ConstructedFrom ?? t, name,
                    operatorsOnly: true, parameterCount, type, symbols);
            }
            AppendInterfaceMembers(result, type, name, symbols, operatorsOnly: true,
                parameterCount);
            return result;
        }

        // 实例 operator 查找：首个 1 参数命中（范围循环已改走
        // FindInstanceOperators + ResolveBound，本入口仅遗留调用方）
        public static MethodSymbol? FindInstanceOperator(TypeSymbol type, string name)
        {
            return FindInstanceOperators(type, name, 1).FirstOrDefault();
        }

        // S8f castTo/castFrom 名字分析核心查询（SYNTAX §3.5 转换优先级）：
        // 沿 host 的 BaseType 链找名为 name 的实例 operator（ext 注册成员
        // 同路径），返回首个「签名适用」的候选（链序即派生层优先，同
        // FindInstanceOperator 口径）。适用 = 至多一个泛型参数 G 时，把
        // 签名中的 G 替换为 replaceWith 后：
        //   - castTo（0 参）：返回类型 == target；
        //   - castFrom（1 参）：唯一参数类型 == replaceWith 且返回类型
        //     == target（target 为构造类型时允许 == 其泛型定义本身）。
        // 多泛型参数 / 宿主泛型参数（S9 使用侧未落地）按不适用处理——
        // 名字分析回退内建转换，不落诊断（BIL §12.1 第 3 条兜底）。
        // 参数类型/返回类型是 void（null）或不可代入的类型时必不适用
        public static MethodSymbol? FindConversionOperator(TypeSymbol host, string name,
            int parameterCount, TypeSymbol replaceWith, TypeSymbol target,
            SymbolGraph symbols)
        {
            foreach (var candidate in FindInstanceOperators(host, name, parameterCount))
            {
                if (candidate.GenericParameters.Count > 1) continue;
                var genericParam = candidate.GenericParameters.Count == 1
                    ? candidate.GenericParameters[0] : null;
                // 返回类型：G 本身（castTo\<TTarget>()）或代入 G 后与 target
                // 一致的普通类型；void/其它泛型参数必不适用
                var substitutedReturn = SubstituteForConversion(candidate.ReturnType,
                    genericParam, replaceWith, symbols);
                if (substitutedReturn == null) continue;
                if (!ReferenceEquals(substitutedReturn, target)
                    && !(target.ConstructedFrom != null
                        && ReferenceEquals(substitutedReturn, target.ConstructedFrom)))
                {
                    continue;
                }
                if (parameterCount == 0) return candidate;
                var substitutedArgument = SubstituteForConversion(
                    candidate.Parameters[0].Type, genericParam, replaceWith, symbols);
                if (substitutedArgument != null
                    && ReferenceEquals(substitutedArgument, replaceWith))
                {
                    return candidate;
                }
            }
            return null;
        }

        // 名字分析的单泛型参数代入（仿 SubstituteFieldType 的最小替换）：
        // signature 是 G 本身 → replacement；构造类型 → 实参逐项替换后经
        // 驻留入口重建；其余（普通类型/其它泛型参数/void）原样返回。
        // 宿主泛型参数（G 之外的 GenericParameterSymbol）返回 null——
        // 不可代入即不适用，由调用方按不适用处理
        private static TypeSymbol? SubstituteForConversion(SemanticSymbol? signature,
            GenericParameterSymbol? genericParam, TypeSymbol replacement,
            SymbolGraph symbols)
        {
            if (signature == null) return null;
            if (genericParam != null && ReferenceEquals(signature, genericParam))
            {
                return replacement;
            }
            if (signature is GenericParameterSymbol) return null;
            if (signature is TypeSymbol { ConstructedFrom: not null, TypeArguments: not null } type)
            {
                var args = new SemanticSymbol[type.TypeArguments.Count];
                var changed = false;
                for (int i = 0; i < args.Length; i++)
                {
                    var argument = type.TypeArguments[i];
                    if (argument is GenericParameterSymbol innerGeneric)
                    {
                        if (genericParam != null && ReferenceEquals(innerGeneric, genericParam))
                        {
                            args[i] = replacement;
                            changed = true;
                            continue;
                        }
                        return null;    // 宿主/其它泛型参数：不可代入
                    }
                    var inner = SubstituteForConversion(argument, genericParam, replacement,
                        symbols);
                    if (inner == null) return null;
                    args[i] = inner;
                    changed |= !ReferenceEquals(inner, argument);
                }
                if (!changed) return type;
                return symbols.GetConstructedType(type.ConstructedFrom, args);
            }
            return signature as TypeSymbol;
        }

        // 泛型签名代入（S9b）：type 中出现的 generics[i] 按索引替换为
        // arguments[i]（引用相等身份——实参可为泛型参数）；构造类型逐项
        // 替换后经驻留入口重建。返回 null = 任一项替换产物非法（仅防御）
        public static SemanticSymbol? SubstituteType(SemanticSymbol type,
            IReadOnlyList<GenericParameterSymbol> generics, IReadOnlyList<SemanticSymbol> arguments,
            SymbolGraph symbols)
        {
            if (type is GenericParameterSymbol parameter)
            {
                for (int i = 0; i < generics.Count; i++)
                {
                    if (ReferenceEquals(generics[i], parameter)) return arguments[i];
                }
                return type;
            }
            if (type is TypeSymbol { ConstructedFrom: not null, TypeArguments: { } args } constructed)
            {
                var substituted = new SemanticSymbol[args.Count];
                var changed = false;
                for (int i = 0; i < args.Count; i++)
                {
                    var inner = SubstituteType(args[i], generics, arguments, symbols);
                    if (inner == null) return null;
                    substituted[i] = inner;
                    changed |= !ReferenceEquals(inner, args[i]);
                }
                if (!changed) return constructed;
                return symbols.GetConstructedType(constructed.ConstructedFrom, substituted);
            }
            return type;
        }

        // 类型含未替换泛型参数（自身是泛型参数，或构造类型的实参递归
        // 含有）——泛型使用侧归 S9 的统一拦截点
        public static bool ContainsGenericParameter(SemanticSymbol type)
        {
            if (type is GenericParameterSymbol) return true;
            return type is TypeSymbol { TypeArguments: { } arguments }
                && arguments.Any(ContainsGenericParameter);
        }

        // 可空类型判定（g8/g10 统一口径）：只认 Nullable 构造（ConstructedFrom
        // == NullableDefinition），内层放宽为 SemanticSymbol——泛型参数 T 的
        // T? 构造内层是 GenericParameterSymbol（NameResolver 经
        // GetConstructedType 构造），不是 TypeSymbol。命中时 element 输出内层
        public static bool IsNullableType(SemanticSymbol type, BindEnvironment env,
            out SemanticSymbol element)
        {
            return IsNullableType(type, env.B, out element);
        }

        // BootstrapSymbols 版（P2 填入点检查无 BindEnvironment，g4 框架）
        public static bool IsNullableType(SemanticSymbol type, BootstrapSymbols b,
            out SemanticSymbol element)
        {
            if (type is TypeSymbol { ConstructedFrom: { } definition,
                TypeArguments: { } arguments }
                && ReferenceEquals(definition, b.NullableDefinition))
            {
                element = arguments[0];
                return true;
            }
            element = type;
            return false;
        }

        // 可赋值性：同符号（驻留引用相等）直通；ErrorType 毒化静默放行；
        // T → Nullable\<T\> 装箱视图（M52）；沿 BaseType 链与接口表命中。
        // 泛型参数：同参数引用相等直通（已先行）；from 为 T 时先同型参装箱
        // （T → T?），再按 extends 界代入后递归判定（T extends B ⟹ T 可赋
        // 给 B 及 B 的上界，含再装箱 B → B?）；无约束 T 可赋给 Any；to 为
        // 异参数一律不可赋。界链沿外层 GP 递归，visiting 防环界发散。
        // S9f：沿 BaseType 链的接口判定——接口可声明在泛型基类上
        // （RangeEnumerator\<T\> implements IEnumerator\<T\>），构造宿主
        // RangeEnumerator\<i32\> 的接口实参沿链代入后比较
        public static bool IsAssignable(SemanticSymbol from, SemanticSymbol to,
            BindEnvironment env)
        {
            return IsAssignable(from, to, env.Unit.Symbols);
        }

        // SymbolGraph 版（P2 填入点检查无 BindEnvironment，g4 框架）
        public static bool IsAssignable(SemanticSymbol from, SemanticSymbol to,
            SymbolGraph symbols)
        {
            return IsAssignableCore(from, to, symbols, null);
        }

        // 约束证明允许沿显式上下界推导；不改变普通赋值/转换的语言规则。
        internal static bool ProvesConstraintAssignable(SemanticSymbol from, SemanticSymbol to,
            SymbolGraph symbols) => IsAssignableCore(from, to, symbols, null, constraintProof: true);

        private static bool IsAssignableCore(SemanticSymbol from, SemanticSymbol to,
            SymbolGraph symbols, HashSet<GenericParameterSymbol>? visiting, bool constraintProof = false)
        {
            var b = symbols.Bootstrap;
            if (ReferenceEquals(from, to)) return true;
            if (from is ErrorTypeSymbol || to is ErrorTypeSymbol) return true;
            // 先保留来源型参身份尝试目标下界，再展开来源上界；否则
            // U supers T 的 T→U 证明会因先把 T 展开成 Any 而丢失。
            if (constraintProof && to is GenericParameterSymbol lowerTarget)
            {
                visiting ??= new HashSet<GenericParameterSymbol>();
                if (visiting.Add(lowerTarget))
                {
                    try
                    {
                        if (lowerTarget.Constraints.Any(c => c.Kind == GenericConstraintKind.Supers
                            && IsAssignableCore(from, c.Bound, symbols, visiting, constraintProof))) return true;
                    }
                    finally { visiting.Remove(lowerTarget); }
                }
            }
            if (from is GenericParameterSymbol fromGp)
            {
                // g8：T → Nullable\<T\> 同型参装箱优先（to 为 Nullable 构造
                // 且内层与 from 引用相等）——先于界路径：无约束 T 没有
                // extends 界，到不了 Nullable\<T\>；异型参/异类型内层
                // （U ≠ T）引用不等，落入下方界路径不误放
                if (IsNullableType(to, b, out var nullableElement)
                    && ReferenceEquals(nullableElement, from))
                {
                    return true;
                }
                if (to is GenericParameterSymbol && !constraintProof) return false;
                // T extends B → 先界代入再判可赋（含再装箱：B → B?）。
                // 不用 EffectiveMemberType 压扁：那会把外层 GP 界走到 Any，
                // 丢掉 B 身份，误拒 T extends U → Nullable\<U\>
                visiting ??= new HashSet<GenericParameterSymbol>();
                if (!visiting.Add(fromGp)) return false;
                try
                {
                    var hasExtends = false;
                    foreach (var constraint in fromGp.Constraints)
                    {
                        if (constraint.Kind != GenericConstraintKind.Extends) continue;
                        hasExtends = true;
                        if (IsAssignableCore(constraint.Bound, to, symbols, visiting, constraintProof))
                        {
                            return true;
                        }
                    }
                    return !hasExtends
                        && IsAssignableCore(b.Any, to, symbols, visiting, constraintProof);
                }
                finally
                {
                    visiting.Remove(fromGp);
                }
            }
            if (to is GenericParameterSymbol) return false;
            var fromType = (TypeSymbol)from;
            var toType = (TypeSymbol)to;
            // 装箱视图：内层比较放宽为 SemanticSymbol 口径（g8——内层为泛型
            // 参数时递归 IsAssignable 自然拒绝：TypeSymbol → GP 不可赋）
            if (ReferenceEquals(toType.ConstructedFrom, b.NullableDefinition)
                && IsAssignableCore(fromType, toType.TypeArguments![0], symbols, visiting, constraintProof))
            {
                return true;
            }
            for (var t = fromType; t != null; t = t.BaseType)
            {
                if (TypesAssignableWithVariance(t, toType, symbols, visiting, constraintProof)) return true;
            }
            // 接口赋值与成员查找共用闭包：逐层代入闭合宿主，包含传递继承。
            foreach (var iface in OverrideChecker.InterfaceClosure(fromType, symbols))
            {
                if (TypesAssignableWithVariance(iface, toType, symbols, visiting, constraintProof)) return true;
            }
            return false;
        }

        private static bool TypesAssignableWithVariance(SemanticSymbol? from,
            SemanticSymbol to, SymbolGraph symbols,
            HashSet<GenericParameterSymbol>? visiting, bool constraintProof = false)
        {
            if (ReferenceEquals(from, to)) return true;
            if (from is not TypeSymbol { ConstructedFrom: { } fromDefinition,
                TypeArguments: { } fromArguments }
                || to is not TypeSymbol { ConstructedFrom: { } toDefinition,
                    TypeArguments: { } toArguments }
                || !ReferenceEquals(fromDefinition, toDefinition)
                || fromArguments.Count != toArguments.Count)
            {
                return false;
            }

            for (var i = 0; i < fromArguments.Count; i++)
            {
                var variance = fromDefinition.GenericParameters[i].Variance;
                if (variance == GenericVariance.None)
                {
                    if (!ReferenceEquals(fromArguments[i], toArguments[i])) return false;
                }
                else if (variance == GenericVariance.Out)
                {
                    if (!IsAssignableCore(fromArguments[i], toArguments[i], symbols,
                        visiting, constraintProof))
                    {
                        return false;
                    }
                }
                else if (!IsAssignableCore(toArguments[i], fromArguments[i], symbols,
                    visiting, constraintProof))
                {
                    return false;
                }
            }
            return true;
        }

        // 宿主泛型参数代入（S9f）：type 中出现的 definition 泛型参数按
        // 声明序索引替换为 constructed 的构造实参（构造类型递归；实参可
        // 为具体类型或外层泛型参数——引用相等身份，`Box\<T\>` 内 T 即
        // 外层 T）。definition 与 constructed 同体时原样返回；替换产物
        // 经驻留入口重建（P3 侧与 ResolveEnvironment.Substitute 同构）
        public static SemanticSymbol SubstituteHost(SemanticSymbol type, TypeSymbol definition,
            TypeSymbol constructed, SymbolGraph symbols)
        {
            if (ReferenceEquals(definition, constructed)) return type;
            if (type is GenericParameterSymbol gp)
            {
                var index = definition.GenericParameters.IndexOf(gp);
                return index >= 0 ? constructed.TypeArguments![index] : type;
            }
            if (type is TypeSymbol { ConstructedFrom: not null } inner)
            {
                var args = new SemanticSymbol[inner.TypeArguments!.Count];
                var changed = false;
                for (int i = 0; i < args.Length; i++)
                {
                    var argument = inner.TypeArguments[i];
                    var substituted = SubstituteHost(argument, definition, constructed, symbols);
                    if (substituted == null) return type;
                    args[i] = substituted;
                    changed |= !ReferenceEquals(substituted, argument);
                }
                if (!changed) return inner;
                return symbols.GetConstructedType(inner.ConstructedFrom!, args);
            }
            return type;
        }
    }
}
