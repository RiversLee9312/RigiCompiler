using System.Collections.Generic;

namespace RigiCompiler.Middleware.Symbols
{
    // BIL canonical 成员符号的签名解析（§5/§8.1）：
    //   NAME(PARAM_0, ...)@RETURN，参数为 名:类型引用 逗号列表，
    //   类型引用可含 <...> 嵌套（嵌套内逗号不分割）。
    // 唯一消费者是 native 声明的调用面提取（native 无 fn 体，签名只能从
    // canonical 解析）；本地 fn 的签名一律以 fn 定义 .args 为准。
    public sealed class CanonicalSignature
    {
        public IReadOnlyList<(string Name, string TypeRef)> Parameters { get; }
        public string ReturnTypeRef { get; }

        private CanonicalSignature(IReadOnlyList<(string, string)> parameters, string returnTypeRef)
        {
            Parameters = parameters;
            ReturnTypeRef = returnTypeRef;
        }

        // 宿主泛型代入后的签名（invoke.indirect：Func\<TRet, T0\> 的 $$call 模板 → 具化）
        internal static CanonicalSignature Create(
            IReadOnlyList<(string, string)> parameters, string returnTypeRef) =>
            new(parameters, returnTypeRef);

        public static CanonicalSignature Parse(string canonical)
        {
            var open = canonical.IndexOf('(');
            if (open < 0)
            {
                throw new CompilerInternalException($"canonical 符号缺参数列表: {canonical}");
            }
            var depth = 0;
            var close = -1;
            for (var i = open; i < canonical.Length; i++)
            {
                switch (canonical[i])
                {
                    case '<': depth++; break;
                    case '>': depth--; break;
                    case ')' when depth == 0: close = i; i = canonical.Length; break;
                }
            }
            if (close < 0 || close + 1 >= canonical.Length || canonical[close + 1] != '@')
            {
                throw new CompilerInternalException($"canonical 符号形状非法: {canonical}");
            }

            var parameters = new List<(string, string)>();
            var inner = canonical.Substring(open + 1, close - open - 1);
            if (inner.Length > 0)
            {
                foreach (var part in SplitTopLevel(inner))
                {
                    var colon = part.IndexOf(':');
                    if (colon <= 0)
                    {
                        throw new CompilerInternalException($"canonical 参数缺名或类型: {part}（{canonical}）");
                    }
                    parameters.Add((part.Substring(0, colon), part.Substring(colon + 1)));
                }
            }
            return new CanonicalSignature(parameters, canonical.Substring(close + 2));
        }

        private static IEnumerable<string> SplitTopLevel(string text)
        {
            var depth = 0;
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                switch (text[i])
                {
                    case '<': depth++; break;
                    case '>': depth--; break;
                    case ',' when depth == 0:
                        yield return text.Substring(start, i - start);
                        start = i + 1;
                        break;
                }
            }
            yield return text.Substring(start);
        }
    }
}
