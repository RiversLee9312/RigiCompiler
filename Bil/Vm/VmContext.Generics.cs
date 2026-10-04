using System.Globalization;
using System.IO;
using System.Text;

namespace RigiCompiler.Bil.Vm
{
    public sealed partial class VmContext
    {
        // Generics 职责；与主文件共享同一类型、字段及生命周期。

        public static string HiddenEntityKey(string wrapperType) =>
            ".wrapper.entity:" + wrapperType;

        public static string HiddenFieldKey(string fieldSymbol, string wrapperType) =>
            ".wrapper.field:" + fieldSymbol + ":" + wrapperType;

        public static string HiddenMethodKey(string methodSymbol, string wrapperType) =>
            ".wrapper.method:" + methodSymbol + ":" + wrapperType;

        // 嵌套类外层 GP 名链（最外层在前）：类型符号 "ns::A.B.C" 的宿主前缀
        // 逐级 FindType 收 GenericParameters，按 CollectFrameGenericParameters
        // 同口径按名去重（同名遮蔽只留最外层槽位）
        internal static List<string> OuterGenericParametersOf(VmContext context,
            string typeSymbol)
        {
            var names = new List<string>();
            var nsEnd = typeSymbol.IndexOf("::", StringComparison.Ordinal);
            var nsPrefix = nsEnd >= 0 ? typeSymbol.Substring(0, nsEnd + 2) : "";
            var path = nsEnd >= 0 ? typeSymbol.Substring(nsEnd + 2) : typeSymbol;
            var parts = path.Split('.');
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var depth = 1; depth < parts.Length; depth++)
            {
                var ownerSymbol = nsPrefix + string.Join(".", parts, 0, depth);
                var ownerDecl = context.FindType(ownerSymbol);
                if (ownerDecl == null)
                {
                    continue;
                }
                foreach (var parameter in ownerDecl.GenericParameters)
                {
                    if (seen.Add(parameter))
                    {
                        names.Add(parameter);
                    }
                }
            }
            return names;
        }

        // 构造点捕获外层宿主 GP 的当前帧绑定（找不到外层链或无 GP 时不写）；
        // 构造发生在宿主帧外（外层 GP 未绑定）留空串占位，对齐端回落 .any
        // ——与既有「推不出即 .any」降级口径一致
        internal static void CaptureOuterGenericBindings(VmContext context,
            VmCoroutine coroutine, string typeRef, VmObject instance)
        {
            var declaration = context.FindType(typeRef);
            if (declaration == null)
            {
                return;
            }
            var outerNames = OuterGenericParametersOf(context, declaration.Symbol);
            if (outerNames.Count == 0)
            {
                return;
            }
            var resolved = new List<string>(outerNames.Count);
            foreach (var name in outerNames)
            {
                resolved.Add(VmTypeOps.TryResolveFrameGeneric(coroutine, name, out var bound)
                    ? bound : "");
            }
            var array = new VmArray(".string", resolved.Count, new VmString(""));
            for (var i = 0; i < resolved.Count; i++)
            {
                array.SetAt(i, new VmString(resolved[i]));
            }
            instance.WriteHidden(HiddenOuterGenericsKey, array);
        }

    }
}
