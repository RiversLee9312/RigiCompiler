using System;
using System.Reflection;
using System.Text;

namespace LatteCompiler
{
    // stdlib 内嵌源载入（ROADMAP S6/S10 机制的最小子集，M43）：stdlib/**/*.latte 以
    // EmbeddedResource 内嵌进本程序集（LogicalName 保持 stdlib/ 路径形态），编译时
    // 取出经 Lexer+Parser 解析为 RootASTNode，作为编译单元（CompilationUnit）的源
    // 文件注入，与用户源同走 P1/P2/P3/P4 路径。CLI（compile）默认注入；测试可显式
    // 调用 ParseAll。输出按逻辑名排序，保证可重现。
    public static class StdlibSources
    {
        // 内嵌资源的逻辑名前缀（csproj EmbeddedResource 的 LogicalName 约定）
        private const string ResourcePrefix = "stdlib/";

        // sourceName 前缀：尖括号标示非磁盘文件（与 Lexer 默认 <inline> 同风格）
        private const string SourceNamePrefix = "<stdlib>/";

        // 解析全部内嵌 stdlib 源；stdlib 是编译器自携源，解析失败
        // （LexerException/ParserException）属编译器内部错误，直接上抛不容忍
        public static IReadOnlyList<RootASTNode> ParseAll()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var names = new List<string>(assembly.GetManifestResourceNames());
            names.Sort(StringComparer.Ordinal);

            var roots = new List<RootASTNode>();
            foreach (var name in names)
            {
                if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                    !name.EndsWith(".latte", StringComparison.Ordinal))
                {
                    continue;
                }

                // 逻辑名 → sourceName：stdlib/core/Console.latte → <stdlib>/core/Console.latte
                // （RecursiveDir 的分隔符与平台相关，统一归一为 /）
                var sourceName = SourceNamePrefix
                    + name.Substring(ResourcePrefix.Length).Replace('\\', '/');
                string text;
                using (var stream = assembly.GetManifestResourceStream(name))
                using (var reader = new StreamReader(stream!, Encoding.UTF8))
                {
                    text = reader.ReadToEnd();
                }
                roots.Add((RootASTNode)new Parser().Parse(new Lexer().Tokenize(text, sourceName)));
            }
            return roots;
        }
    }
}
