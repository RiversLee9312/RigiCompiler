namespace RigiCompiler.Tests;

/// <summary>无状态的编译器测试工具：解析、黄金文本、AST 定位与输出分节。</summary>
public static class CompilerTestTools
{
        public static void Section(string title) => Console.WriteLine($"=== {title} ===");

        public static void Blank() => Console.WriteLine();

        // ===== Parse 驱动 =====

        // 全管线：Lexer → Parser（含 ASTIntegrityValidator）→ Root
        public static RootASTNode ParseRoot(string code)
        {
            return ParseRoot(code, "<inline>");
        }

        // 带源名变体（重载而非可选参数：方法组 Select(CompilerTestTools.ParseRoot)
        // 的类型推断依赖单签名）；sourceName 供中端套件的 Span/诊断链断言
        public static RootASTNode ParseRoot(string code, string sourceName)
        {
            return Frontend.Parse(new SourceInput(code, sourceName));
        }

        // 独立代码块驱动（TestRootParserLayer 垫底：被测层漏消费 token 会立即暴露）
        public static CodeBlockASTNode ParseBlock(string code)
        {
            var block = new CodeBlockASTNode(null);
            ParseWithLayer(new CodeBlockParserLayer(block), code);
            return block;
        }

        // 独立 Layer 驱动（TestRootParserLayer 只接受 EOF）
        public static void ParseWithLayer(IParserLayer entryLayer, string code)
        {
            new Parser().Parse(new Lexer().Tokenize(code), new TestRootParserLayer(), entryLayer);
        }

        // 组合便捷：解析顶层并取第一个声明（字面量/变量声明套件常用）
        public static ASTNode ParseFirstDecl(string code)
        {
            var root = ParseRoot(code);
            if (root.Declarations.Count == 0)
                throw new InvalidOperationException("No AST node produced");
            return root.Declarations[0];
        }

        // ===== 黄金文本拼装 =====

        // 逐行精确比对的黄金文本：显式 \n，与源文件换行编码无关（autocrlf 免疫）
        public static string Lines(params string[] lines)
        {
            return string.Join("\n", lines) + "\n";
        }

        // 按短名取函数体：stdlib 与用户源可能同名（如 CoroutineLocal.get
        // 与用例 `func get()`）。唯一命中直接返回；多名时优先非
        // `<stdlib>/` 源。再歧义则抛，避免 Single() 把套件打崩。
        public static T UniqueNamedBody<T>(IReadOnlyList<T> bodies, string name,
            Func<T, MethodSymbol> methodOf)
        {
            T? any = default;
            T? user = default;
            var anyCount = 0;
            var userCount = 0;
            foreach (var body in bodies)
            {
                var method = methodOf(body);
                if (method.Name != name)
                {
                    continue;
                }
                anyCount++;
                any = body;
                var source = method.SourceFile?.Span?.sourceName ?? "";
                if (!source.StartsWith("<stdlib>/", StringComparison.Ordinal))
                {
                    userCount++;
                    user = body;
                }
            }
            if (anyCount == 1)
            {
                return any!;
            }
            if (userCount == 1)
            {
                return user!;
            }
            throw new InvalidOperationException(
                $"UniqueNamedBody('{name}') 匹配 {anyCount} 个，用户源 {userCount} 个");
        }

}
