namespace LatteCompiler
{
    // 共享符号查询设施（自旧 BindSession 静态/实例辅助原样迁移，行为不变）：
    // 实例成员沿 BaseType 链查找、泛型字段最小替换（S7f/M52，S9 前置）、
    // 实例 operator 按名与参数个数查找（S8c 索引访问）、可赋值性判定。
    // 全部为无副作用纯查询，各簇 visitor 共用。
    internal static class SymbolLookup
    {
        // 构造类型的泛型字段最小替换（M52，S9 前置；S9a 放宽返回
        // SemanticSymbol）：字段声明类型是泛型参数时，沿 receiver 类型的
        // BaseType 链找到泛型定义构造，按形参索引取实参替换——实参可为
        // 具体类型或外层泛型参数（引用相等身份，`Box<T>` 内 T 即外层 T）；
        // receiver 是定义级（泛型函数体内 this）或链上无匹配构造时返回
        // 声明类型原样（宿主泛型参数身份保留，P4 按 §7.5 投影）。
        // 非泛型字段直通声明类型。receiverType 为 null（静态上下文）时同直通
        public static SemanticSymbol? SubstituteFieldType(FieldSymbol field,
            TypeSymbol? receiverType)
        {
            if (field.FieldType is not GenericParameterSymbol param)
            {
                return field.FieldType;
            }
            for (var t = receiverType; t != null; t = t.BaseType)
            {
                if (t.ConstructedFrom == null || field.Owner == null
                    || !ReferenceEquals(t.ConstructedFrom, field.Owner))
                {
                    continue;
                }
                var index = t.ConstructedFrom.GenericParameters.IndexOf(param);
                if (index >= 0 && index < t.TypeArguments!.Count)
                {
                    return t.TypeArguments[index];
                }
            }
            return field.FieldType;
        }

        // 实例方法查找：receiver 静态类型沿 BaseType 链（接口 receiver
        // 即查接口自身，BaseType 为 null 自然终止；ext 注册成员已在目标
        // 类型成员表）。仅 Regular 实例方法——operator 不经点号调用
        // （for 头专用解析），init/getter/setter 归各自里程碑。
        // 构造类型的成员表在其泛型定义上（构造器不复制成员列表，
        // S7f 起经 ConstructedFrom 回退——实参替换在使用侧特判）。
        // override 遮蔽（S8e，§9.2.1）：override 在分派语义上替换继承
        // 成员——派生层已收集的 override 与基类层候选签名严格相等时
        // 基类候选不进重载候选池（否则同签名候选歧义）
        public static List<MethodSymbol> FindInstanceMethods(TypeSymbol type, string name)
        {
            var result = new List<MethodSymbol>();
            for (var t = type; t != null; t = t.BaseType)
            {
                var owner = t.ConstructedFrom ?? t;
                foreach (var method in owner.Methods.Where(m => m.Name == name
                    && !m.IsStatic && m.Kind == MethodKind.Regular))
                {
                    if (result.Any(derived => derived.IsOverride
                        && SignaturesEqual(derived, method)))
                    {
                        continue;
                    }
                    result.Add(method);
                }
            }
            return result;
        }

        // 签名严格相等（参数类型序列 + 返回类型，引用相等——OverrideChecker
        // 同口径；构造宿主代入实参后的精确比较归 S9，比较失败退回不去重，
        // 行为与遮蔽规则引入前一致）
        private static bool SignaturesEqual(MethodSymbol a, MethodSymbol b)
        {
            if (a.Parameters.Count != b.Parameters.Count)
            {
                return false;
            }
            for (int i = 0; i < a.Parameters.Count; i++)
            {
                if (!ReferenceEquals(a.Parameters[i].Type, b.Parameters[i].Type))
                {
                    return false;
                }
            }
            return ReferenceEquals(a.ReturnType, b.ReturnType);
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
            int parameterCount)
        {
            var result = new List<MethodSymbol>();
            for (var t = type; t != null; t = t.BaseType)
            {
                var owner = t.ConstructedFrom ?? t;
                result.AddRange(owner.Methods.Where(m => m.Name == name
                    && !m.IsStatic && m.Kind == MethodKind.Operator
                    && m.Parameters.Count == parameterCount));
            }
            return result;
        }

        // 实例 operator 查找（for 头专用）：首个 1 参数命中
        // （经 FindInstanceOperators 实现，行为不变）
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

        // 可赋值性：同符号（驻留引用相等）直通；ErrorType 毒化静默放行；
        // T → Nullable\<T\> 装箱视图（M52）；沿 BaseType 链与接口表命中。
        // S9 放宽为 SemanticSymbol：泛型参数参与判定——同参数引用相等直通
        // （已先行），与具体类型或异参数比较一律不可赋（false，不落诊断）
        public static bool IsAssignable(SemanticSymbol from, SemanticSymbol to,
            BindEnvironment env)
        {
            if (ReferenceEquals(from, to)) return true;
            if (from is ErrorTypeSymbol || to is ErrorTypeSymbol) return true;
            // 泛型参数 vs 具体类型：不可赋（ErrorType 已先行放行）
            if (from is GenericParameterSymbol || to is GenericParameterSymbol) return false;
            var fromType = (TypeSymbol)from;
            var toType = (TypeSymbol)to;
            if (ReferenceEquals(toType.ConstructedFrom, env.B.NullableDefinition)
                && toType.TypeArguments![0] is TypeSymbol element
                && IsAssignable(fromType, element, env))
            {
                return true;
            }
            for (var t = fromType.BaseType; t != null; t = t.BaseType)
            {
                if (ReferenceEquals(t, toType)) return true;
            }
            return fromType.Interfaces.Any(i => ReferenceEquals(i, toType));
        }
    }
}
