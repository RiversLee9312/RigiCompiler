using System.Linq;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// 类型短名共享工具（自 BoundDescribe/LoweredDescribe 提取，消除两份逐行重复）：
    /// Nullable\&lt;T&gt; 显示为 T?，其余构造类型 Name&lt;args&gt; 递归；
    /// null = P4a 合成 .breakid 局部（BIL §9.3 别名，无 TypeSymbol）；
    /// S9 起签名放宽为 SemanticSymbol：泛型参数显示其名。
    /// </summary>
    internal static class TypeShort
    {
        public static string Of(SemanticSymbol? type)
        {
            if (type == null) return ".breakid";
            if (type is not TypeSymbol symbol) return type.Name;
            if (symbol.ConstructedFrom == null) return symbol.Name;
            if (symbol.Name == "Nullable" && symbol.TypeArguments!.Count == 1
                && symbol.TypeArguments[0] is TypeSymbol element)
            {
                return Of(element) + "?";
            }
            return symbol.Name + "<" + string.Join(", ",
                symbol.TypeArguments!.Select(a => a is TypeSymbol t ? Of(t) : a.Name)) + ">";
        }
    }
}
