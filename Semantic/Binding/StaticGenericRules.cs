namespace RigiCompiler
{
    // 静态成员 × 类级泛型禁令（SYNTAX §9.2.3 / §3.6，RUNTIME §10）：
    // 类级类型参数的 typeid 在构造时写入实例隐藏字段；静态成员没有实体，
    // 读该字段即非法。方法级泛型仍由调用点传 typeid，不受本禁令约束。
    internal static class StaticGenericRules
    {
        public const string EnclosingTypeParameterMessage =
            "static members cannot use type parameter '{0}' of enclosing type '{1}' " +
            "(typeid is stored on instances and unavailable without one)";

        public const string SingletonGenericMessage =
            "singleton type '{0}' cannot declare type parameters " +
            "(a singleton has one instance and cannot carry per-instantiation typeid)";

        public const string StaticOnlyGenericMessage =
            "type '{0}' cannot declare type parameters because it has only static members " +
            "(no instance to hold typeid); use method-level generics instead";

        public const string ConstructedStaticAccessMessage =
            "cannot access static member '{0}' via constructed type '{1}'; " +
            "use the generic definition name '{2}' " +
            "(constructed types do not pass typeid to static members)";

        // 符号是否提及 declaringType 链上声明的类型参数（不含方法级泛型）
        public static bool MentionsEnclosingTypeParameter(SemanticSymbol? type,
            TypeSymbol? declaringType, out GenericParameterSymbol? hit)
        {
            hit = null;
            if (type == null || declaringType == null) return false;
            if (type is GenericParameterSymbol generic
                && IsEnclosingTypeParameter(generic, declaringType))
            {
                hit = generic;
                return true;
            }
            if (type is TypeSymbol { TypeArguments: { } arguments })
            {
                foreach (var argument in arguments)
                {
                    if (MentionsEnclosingTypeParameter(argument, declaringType, out hit))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public static bool IsEnclosingTypeParameter(GenericParameterSymbol generic,
            TypeSymbol? declaringType)
        {
            for (var type = declaringType; type != null; type = type.DeclaringType)
            {
                foreach (var parameter in type.GenericParameters)
                {
                    if (ReferenceEquals(parameter, generic)) return true;
                }
            }
            return false;
        }

        // 静态语境下使用了类级类型参数则落诊断；返回 true = 已报错
        public static bool CheckEnclosingTypeParameterUse(SemanticSymbol? type,
            TypeSymbol? declaringType, CharRange? span, Action<CharRange?, string> error)
        {
            if (!MentionsEnclosingTypeParameter(type, declaringType, out var hit) || hit == null)
            {
                return false;
            }
            var owner = OwnerOf(hit, declaringType);
            error(span, string.Format(EnclosingTypeParameterMessage, hit.Name,
                owner?.Name ?? declaringType?.Name ?? "?"));
            return true;
        }

        public static bool CheckFrameUse(SemanticSymbol? type, BindFunctionFrame frame,
            CharRange? span, Action<CharRange?, string> error)
        {
            if (!frame.BanEnclosingTypeParameters) return false;
            return CheckEnclosingTypeParameterUse(type, frame.DeclaringType, span, error);
        }

        // 经构造类型访问静态成员（Ban 3）
        public static bool CheckConstructedStaticAccess(SemanticSymbol? container,
            bool isStaticMember, string memberName, CharRange? span,
            Action<CharRange?, string> error)
        {
            if (!isStaticMember || container is not TypeSymbol { ConstructedFrom: not null } constructed)
            {
                return false;
            }
            error(span, string.Format(ConstructedStaticAccessMessage, memberName,
                BoundAnalysis.TypeDisplay(constructed), constructed.ConstructedFrom!.Name));
            return true;
        }

        public static bool IsUserMember(FieldSymbol field)
        {
            return !field.Name.StartsWith("..", StringComparison.Ordinal);
        }

        public static bool IsUserMember(MethodSymbol method)
        {
            return !method.IsSynthetic
                && !method.Name.StartsWith("..", StringComparison.Ordinal);
        }

        private static TypeSymbol? OwnerOf(GenericParameterSymbol generic, TypeSymbol? declaringType)
        {
            for (var type = declaringType; type != null; type = type.DeclaringType)
            {
                foreach (var parameter in type.GenericParameters)
                {
                    if (ReferenceEquals(parameter, generic)) return type;
                }
            }
            return declaringType;
        }
    }
}
