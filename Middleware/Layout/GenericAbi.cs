using System;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Layout
{
    /// <summary>
    /// 泛型类级 typeid ABI（MW5 c2-a/c2-b）：隐藏字段符号、类级/方法级判别、
    /// 构造类型是否物化（class 与接口空壳）、TypeSheet 全局名转义。
    /// </summary>
    public static class GenericAbi
    {
        public const int TypeIdSlotSize = 8;
        public const int TypeIdSlotAlign = 8;
        public const string HiddenFieldInfix = "#..generic.";
        public const string HiddenFieldSuffix = "@.typeid";

        public static string HiddenFieldSymbol(string typeCanonical, string paramName) =>
            typeCanonical + HiddenFieldInfix + paramName + HiddenFieldSuffix;

        // 布局/TypeSheet 键：构造类型用自身 canonical；模板用 DeclarationKey
        //（同名不同元数 Func\<TRet> / Func\<TRet,T0> 不得撞键）
        public static string PlanKey(MwTypeSymbol type)
        {
            if (type.Canonical.IndexOf('<') >= 0)
            {
                return type.Canonical;
            }
            var arity = type.Declaration.GenericParameters.Count;
            return arity == 0 ? type.Canonical : type.Canonical + "<" + arity + ">";
        }

        public static bool IsClassLevelTypeId(MwMemberSymbol? owner, string paramName)
        {
            if (owner?.Owner == null
                || !paramName.StartsWith(".generic.", StringComparison.Ordinal))
            {
                return false;
            }
            var name = paramName.Substring(".generic.".Length);
            foreach (var parameter in owner.Owner.Declaration.GenericParameters)
            {
                if (parameter == name)
                {
                    return true;
                }
            }
            return false;
        }

        public static bool TryPlaceholderName(string typeRef, out string name)
        {
            name = "";
            const string dollar = ".generic<$.generic.";
            const string bare = ".generic<";
            if (typeRef.StartsWith(dollar, StringComparison.Ordinal) && typeRef.EndsWith(">"))
            {
                name = typeRef.Substring(dollar.Length, typeRef.Length - dollar.Length - 1);
                return name.Length > 0 && name.IndexOf('<') < 0;
            }
            if (typeRef.StartsWith(bare, StringComparison.Ordinal) && typeRef.EndsWith(">"))
            {
                name = typeRef.Substring(bare.Length, typeRef.Length - bare.Length - 1);
                return name.Length > 0 && name.IndexOf('<') < 0 && name.IndexOf('.') < 0;
            }
            return false;
        }

        public static bool IsClosedConstructed(string typeRef) =>
            ConstructedTypeCollector.IsConstructed(typeRef)
            && !MwTypeKey.Normalize(typeRef).Contains(".generic<", StringComparison.Ordinal);

        public static bool ShouldMaterialize(MwTypeSymbol template)
        {
            if (template.IsExternal
                || template.Declaration.GenericParameters.Count == 0)
            {
                return false;
            }
            if (template.Declaration.Kind == BilTypeKind.Interface)
            {
                return true;
            }
            if (template.Declaration.Kind != BilTypeKind.Class)
            {
                return false;
            }
            var name = template.Canonical;
            return name != TypeLayout.ArrayTypeCanonical
                && name != "core::Nullable"
                && name != "core::Type";
        }

        // 仅转义构造类型的 <,> 空格；无角括号的既有名（含 ::）保持不变
        public static string EscapeGlobalName(string prefix, string canonical)
        {
            if (canonical.IndexOfAny(new[] { '<', '>', ',' }) < 0)
            {
                return prefix + canonical;
            }
            return prefix + canonical.Replace(" ", "", StringComparison.Ordinal)
                .Replace('<', '$').Replace('>', '$').Replace(',', '.');
        }
    }
}
