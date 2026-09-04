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
            var name = template.Canonical;
            // 泛型值类型（G1）：构造 struct/enum 各自具化计划——字段复用
            // 模板（占位字段恒 16B 胖值槽），无对象头隐藏 typeid 槽；
            // core::Type 无 stdlib 声明，走 ConstructedLayout 合成 sheet
            if (template.Declaration.Kind is BilTypeKind.Struct or BilTypeKind.EnumStruct)
            {
                return name != "core::Type";
            }
            if (template.Declaration.Kind != BilTypeKind.Class)
            {
                return false;
            }
            return name != TypeLayout.ArrayTypeCanonical
                && name != "core::Nullable"
                && name != "core::Type";
        }

        // 值类型宿主（struct/enum struct/wrapper）：无对象头，类级 typeid
        // 不能藏实例隐藏字段——泛型值类型 fn 的类级 .generic.* 参数保留在
        // LLVM 调用约定内，由调用点按 §7.2 序直传（class「被调方自取」对偶）
        public static bool IsValueTypeOwner(MwTypeSymbol? owner) =>
            owner != null && owner.Declaration.Kind is BilTypeKind.Struct
                or BilTypeKind.EnumStruct or BilTypeKind.Wrapper;

        // 类级 typeid 参数是否保留在 LLVM 调用约定内（G1）：值类型宿主的
        // 实例成员（.this 居首）保留、调用点直传；class 宿主剔除（prologue
        // 从隐藏字段自取）；值类型静态成员剔除（§9.2.3 不得用类级参数，
        // 调用点不传——BIL 仍声明该形参，VM AlignGenericHiddenArgs 缺省
        // 填 .any，native 由 EmitBody 落 core::Any sheet 常量兜底）
        public static bool KeepsClassTypeIdInAbi(MwMemberSymbol? fnSymbol, bool hasThisParam) =>
            fnSymbol != null && IsValueTypeOwner(fnSymbol.Owner) && hasThisParam;

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
