namespace RigiCompiler
{
    // like 委托（SYNTAX §9.6）共享设施：`class Apple : Fruit like pear` 把
    // 待实现成员委托给本类实例字段。P2（OverrideChecker 待实现成员豁免）与
    // P3（BindingDriver 转发成员合成）共用同一份委托成员发现，口径（规范
    // 未明处取最保守合理行为）：
    //   - 委托范围 = OverrideChecker 待实现成员同集（基类链 abstract 方法 +
    //     接口闭包无体方法）；
    //   - 显式实现优先于委托（宿主链已有具体实现的成员不委托）；
    //   - 签名必须匹配（OverrideChecker.SignatureView 同口径，构造宿主代入
    //     后比较）；委托类型上命中的必须是具体实现（非 abstract、有体或
    //     native）；签名仍含泛型参数（泛型方法/泛型接口未代入形态）不委托
    //     ——转发形参具化未支持，按未实现照常诊断；
    //   - like 目标缺失/非本类实例字段/类型毒化：无委托项（缺失成员仍报
    //     未实现，专项诊断归调用方）。
    internal static class LikeDelegationFacility
    {
        // 一条委托：Required = 待实现成员符号（接口/基类侧），Target = like
        // 字段类型上的具体实现符号（转发调用的目标）；Key = 签名去重键
        // （OverrideChecker 待实现成员检查同口径）
        internal sealed class DelegatedMember
        {
            public MethodSymbol Required { get; }
            public MethodSymbol Target { get; }
            public string Key { get; }

            public DelegatedMember(MethodSymbol required, MethodSymbol target, string key)
            {
                Required = required;
                Target = target;
                Key = key;
            }
        }

        // like 目标字段：本类声明的同名实例字段（§9.6 形态——委托给自身
        // 字段；继承字段不算，静态字段不算）
        public static FieldSymbol? FindLikeField(TypeSymbol type)
        {
            if (type.LikeTarget == null) return null;
            var definition = type.ConstructedFrom ?? type;
            return definition.Fields.FirstOrDefault(f =>
                f.Name == type.LikeTarget && !f.IsStatic);
        }

        // 委托成员集（同签名多源按 Key 去重，序同 FindRequiredMembers）
        public static List<DelegatedMember> CollectDelegatedMembers(TypeSymbol type,
            SymbolGraph symbols)
        {
            var result = new List<DelegatedMember>();
            if (FindLikeField(type) is not { FieldType: TypeSymbol fieldType }
                || fieldType is ErrorTypeSymbol)
            {
                return result;
            }
            var seen = new HashSet<string>();
            foreach (var required in OverrideChecker.FindRequiredMembers(type, symbols))
            {
                // 同签名多源只委托一次；毒化静默；显式实现优先
                if (!seen.Add(required.Key)
                    || required.HasErrorType
                    || SignatureMentionsGenericParameter(required.Symbol)
                    || OverrideChecker.FindImplementation(type, required, symbols) != null)
                {
                    continue;
                }
                var target = FindConcreteMatch(fieldType, required, symbols);
                if (target != null)
                {
                    result.Add(new DelegatedMember(required.Symbol, target, required.Key));
                }
            }
            return result;
        }

        // 签名含泛型参数（方法级泛型参数或宿主泛型接口的未代入形参）——
        // 转发合成不支持具化，保守不委托
        private static bool SignatureMentionsGenericParameter(MethodSymbol method)
        {
            return method.GenericParameters.Count > 0
                || method.Parameters.Any(p => p.Type is GenericParameterSymbol)
                || method.ReturnType is GenericParameterSymbol;
        }

        // 委托类型上的具体实现命中：沿基类链找签名匹配的非 abstract
        // 有体/native 方法（FindImplementation 同口径，宿主换成字段类型）。
        // 字段类型为接口时另走接口闭包：签名匹配即接受（允许 abstract/
        // 无体）——转发体调接口方法，运行时对字段值虚派发；接口默认方法
        // （HasBody）同命中，宿主显式 override 优先语义不变（由
        // CollectDelegatedMembers 的 FindImplementation 检查保证）
        private static MethodSymbol? FindConcreteMatch(TypeSymbol fieldType,
            OverrideChecker.SignatureView required, SymbolGraph symbols)
        {
            for (var t = fieldType; t != null; t = t.BaseType)
            {
                var def = t.ConstructedFrom ?? t;
                foreach (var candidate in def.Methods)
                {
                    var view = OverrideChecker.SignatureView.Of(candidate, def, t, symbols);
                    if (view.Matches(required))
                    {
                        // 接口成员（abstract/无体或默认方法 HasBody）签名匹配即
                        // 接受——转发体调接口方法，运行时对字段值虚派发
                        if (def.Kind == TypeKind.Interface)
                        {
                            return candidate;
                        }
                        return !candidate.IsAbstract && (candidate.HasBody || candidate.IsNative)
                            ? candidate
                            : null;
                    }
                }
            }
            if ((fieldType.ConstructedFrom ?? fieldType).Kind == TypeKind.Interface)
            {
                foreach (var iface in OverrideChecker.InterfaceClosure(fieldType, symbols))
                {
                    var def = iface.ConstructedFrom ?? iface;
                    foreach (var candidate in def.Methods)
                    {
                        var view = OverrideChecker.SignatureView.Of(candidate, def, iface, symbols);
                        if (view.Matches(required))
                        {
                            return candidate;
                        }
                    }
                }
            }
            return null;
        }
    }
}
