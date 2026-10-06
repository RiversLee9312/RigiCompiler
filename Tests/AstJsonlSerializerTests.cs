using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// AST JSONL 序列化/反序列化测试（格式 v2，M32：carrier 记录化 + 往返无损）。
    ///
    /// 序列化：解析小段 Rigi 源码 → AstJsonlSerializer 序列化到 StringWriter → 断言：
    /// - 每行是合法 JSON，id/parent/via/type/span/fields 六个键齐全；
    /// - id 从 1 连续自增，parent 引用已出现的 id（根为 null）；
    /// - 非根行 via 非空，fields 不含 Parent；
    /// - 节点行 span 非空且首尾不颠倒（M28）；carrier 行（ImportItem）span 恒为 null（v2）；
    /// - 关键节点类型与 via/fields 内容出现（含 private 字段下钻、
    ///   carrier 行及其标量字段、Symbol 点分字符串渲染、enum 渲染为名字）。
    ///
    /// 反序列化（AstJsonlDeserializer）：
    /// - 往返无损：Parse → Serialize → Deserialize → 再 Serialize，两次 JSONL 逐行一致；
    /// - {"file":...} 元记录行跳过；
    /// - 非法输入（缺 id 键/未知 type/悬空 parent 引用）抛 CompilerInternalException。
    /// </summary>
    public static class AstJsonlSerializerTests
    {
        public static void TestVariableDeclaration()
        {
            CompilerTestTools.Section("AST JSONL: var x = 42");
            var docs = new List<JsonDocument>();
            try
            {
                var lines = Dump("var x = 42\n", docs);
                CheckStructure("var x = 42", lines);

                var decl = OfType(lines, "VariableDeclarationASTNode").ToList();
                CaseAssertions.CheckTrue("出现 VariableDeclarationASTNode（via=Declarations[0]）",
                    decl.Count == 1 && decl[0].GetProperty("via").GetString() == "Declarations[0]");
                CaseAssertions.CheckTrue("VariableDeclaration fields：Name=x, IsConst=false",
                    decl.Count == 1 &&
                    FieldString(decl[0], "Name") == "x" &&
                    decl[0].GetProperty("fields").TryGetProperty("IsConst", out var constEl) &&
                    constEl.GetBoolean() == false);

                var literal = OfType(lines, "IntLiteralASTNode").ToList();
                CaseAssertions.CheckTrue("IntLiteralASTNode fields：Value=42, IntType=I32（enum 渲染为名字）",
                    literal.Count == 1 &&
                    literal[0].GetProperty("fields").TryGetProperty("Value", out var valueEl) &&
                    valueEl.GetDecimal() == 42 &&
                    FieldString(literal[0], "IntType") == "I32");

                CaseAssertions.CheckTrue("private 字段下钻：字面量经 via=literal 挂载",
                    lines.Any(l => l.GetProperty("via").GetString() == "literal"));
                CaseAssertions.CheckTrue("ExpressionRoot 的 private expression 字段下钻（via=expression）",
                    lines.Any(l => l.GetProperty("via").GetString() == "expression"));
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue("var x = 42", false, $"unexpected {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                foreach (var d in docs) d.Dispose();
            }
            CompilerTestTools.Blank();
        }

        public static void TestBinaryExpression()
        {
            CompilerTestTools.Section("AST JSONL: var r = 1 + (2 * 3)");
            var docs = new List<JsonDocument>();
            try
            {
                var lines = Dump("var r = 1 + (2 * 3)\n", docs);
                CheckStructure("var r = 1 + (2 * 3)", lines);

                var binaries = OfType(lines, "BinaryExpressionASTNode").ToList();
                CaseAssertions.CheckTrue("两个 BinaryExpressionASTNode", binaries.Count == 2);
                CaseAssertions.CheckTrue("Operator 字段 + 与 * 都出现",
                    binaries.Any(b => FieldString(b, "Operator") == "+") &&
                    binaries.Any(b => FieldString(b, "Operator") == "*"));
                CaseAssertions.CheckTrue("via=Left / via=Right 都出现",
                    lines.Any(l => l.GetProperty("via").GetString() == "Left") &&
                    lines.Any(l => l.GetProperty("via").GetString() == "Right"));
                CaseAssertions.CheckTrue("括号分组 GroupExpressionASTNode 出现",
                    OfType(lines, "GroupExpressionASTNode").Any());
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue("var r = 1 + (2 * 3)", false, $"unexpected {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                foreach (var d in docs) d.Dispose();
            }
            CompilerTestTools.Blank();
        }

        public static void TestImport()
        {
            CompilerTestTools.Section("AST JSONL v2: import core.collections.List");
            var docs = new List<JsonDocument>();
            try
            {
                var lines = Dump("import core.collections.List\n", docs);
                CheckStructure("import", lines);

                CaseAssertions.CheckTrue("ImportASTNode 出现", OfType(lines, "ImportASTNode").Any());
                CaseAssertions.CheckTrue("carrier 行出现（type=ImportItem, via=importedSymbols[0]）",
                    lines.Any(l => l.GetProperty("type").GetString() == "ImportItem" &&
                                   l.GetProperty("via").GetString() == "importedSymbols[0]"));
                CaseAssertions.CheckTrue("v2：via 不再出现 (ImportItem.symbolNode) 复合串",
                    lines.All(l => !(l.GetProperty("via").GetString() ?? "").Contains("(")));
                CaseAssertions.CheckTrue("carrier 内节点经 via=symbolNode 挂载（parent 为 carrier 行 id）",
                    lines.Any(l => l.GetProperty("type").GetString() == "SymbolASTNode" &&
                                   l.GetProperty("via").GetString() == "symbolNode" &&
                                   l.GetProperty("parent").GetInt32() ==
                                       OfType(lines, "ImportItem").First().GetProperty("id").GetInt32()));
                CaseAssertions.CheckTrue("Symbol 渲染为点分字符串 core.collections.List",
                    OfType(lines, "SymbolASTNode").Any(l => FieldString(l, "symbol") == "core.collections.List"));
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue("import core.collections.List", false, $"unexpected {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                foreach (var d in docs) d.Dispose();
            }
            CompilerTestTools.Blank();
        }

        public static void TestImportAllForm()
        {
            CompilerTestTools.Section("AST JSONL v2: import core.collections.*（carrier importAll 字段）");
            var docs = new List<JsonDocument>();
            try
            {
                var lines = Dump("import core.collections.*\n", docs);
                CheckStructure("import core.collections.*", lines);

                var carriers = OfType(lines, "ImportItem").ToList();
                CaseAssertions.CheckTrue("carrier 行恰好一条（via=importedSymbols[0]）",
                    carriers.Count == 1 &&
                    carriers[0].GetProperty("via").GetString() == "importedSymbols[0]");
                CaseAssertions.CheckTrue("carrier 行 span 恒为 null",
                    carriers.Count == 1 &&
                    carriers[0].GetProperty("span").ValueKind == JsonValueKind.Null);
                CaseAssertions.CheckTrue("carrier fields 含 importAll=true（v1 中丢失的标量，v2 恢复）",
                    carriers.Count == 1 &&
                    carriers[0].GetProperty("fields").TryGetProperty("importAll", out var allEl) &&
                    allEl.GetBoolean());
                CaseAssertions.CheckTrue("carrier 行 parent 为宿主 ImportASTNode 的 id",
                    carriers.Count == 1 &&
                    carriers[0].GetProperty("parent").GetInt32() ==
                        OfType(lines, "ImportASTNode").First().GetProperty("id").GetInt32());
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue("import core.collections.*", false, $"unexpected {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                foreach (var d in docs) d.Dispose();
            }
            CompilerTestTools.Blank();
        }

        public static void TestGenericTypeReference()
        {
            CompilerTestTools.Section("AST JSONL: var list: List\\<i32>");
            var docs = new List<JsonDocument>();
            try
            {
                var lines = Dump("var list: List\\<i32>\n", docs);
                CheckStructure("var list: List\\<i32>", lines);

                CaseAssertions.CheckTrue("TypeReferenceASTNode 出现（via=TypeAnnotation）",
                    OfType(lines, "TypeReferenceASTNode").Any(l => l.GetProperty("via").GetString() == "TypeAnnotation"));
                CaseAssertions.CheckTrue("泛型 Symbol 渲染为 List\\<i32>",
                    OfType(lines, "SymbolASTNode").Any(l => FieldString(l, "symbol") == "List\\<i32>"));
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue("var list: List\\<i32>", false, $"unexpected {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                foreach (var d in docs) d.Dispose();
            }
            CompilerTestTools.Blank();
        }

        // ===== 往返无损（M32）=====

        public static void TestRoundTrip()
        {
            CompilerTestTools.Section("AST JSONL 往返无损（Parse → Serialize → Deserialize → Serialize）");

            CheckRoundTrip("显式 shared 泛型约束",
                "pub shared class Container\\<shared T, shared out U> {}\n");

            // 变量声明（含嵌套泛型类型 / 可空标注 / null 字面量初始化）
            CheckRoundTrip("变量声明（泛型 + 可空）",
                "var list: List\\<Map\\<String, i32>>? = null\n");
            // enum struct（含显式判别值 long?）
            CheckRoundTrip("enum struct（显式判别值）",
                "pub enum struct SteadyABIEnum {}[\n" +
                "    First -> 0,\n" +
                "    Second -> 2,\n" +
                "    Third -> 1\n" +
                "]\n");
            // import 三形态（carrier 路径是本次格式升级的动机）
            CheckRoundTrip("import 单导入", "import core.collections.List\n");
            CheckRoundTrip("import 多导入", "import core.collections.{List, Map}\n");
            CheckRoundTrip("import 全量导入（importAll=true）", "import core.collections.*\n");
            // 注解声明（带实参的 wrapper 应用）
            CheckRoundTrip("注解声明（带实参）",
                "@Clamped(0, 100)\nvar health: i32 = 50\n");
            // lambda 表达式
            CheckRoundTrip("lambda 表达式",
                "var f = func{(x: i32): i32 -> (x + 1)}\n");
            // void lambda（ReturnType = null）
            CheckRoundTrip("void lambda 表达式",
                "var f = func{() -> (1 + 1)}\n");
            // seq 块（using 资源绑定）
            CheckRoundTrip("seq 块（using 绑定）",
                "func main() {\n" +
                "    seq using(const file = open()) {\n" +
                "        use(file)\n" +
                "    }\n" +
                "}\n");
            // try-catch-finally
            CheckRoundTrip("try-catch-finally",
                "func main() {\n" +
                "    try {\n" +
                "        riskyOperation()\n" +
                "    } catch (e: IOException) {\n" +
                "        handleIO(e)\n" +
                "    } finally(f) {\n" +
                "        cleanup(f)\n" +
                "    }\n" +
                "}\n");
            // wrapper proxy 声明
            CheckRoundTrip("wrapper proxy 声明",
                "pub wrapper Logged {\n" +
                "    operator .proxy.doSomething(arg: i32): String {}\n" +
                "}\n");
            // switch 表达式
            CheckRoundTrip("switch 表达式",
                "var r = switch(x) { (1) -> { 1 } default -> { 0 } }\n");
            // switch 语句（语句形态，M33）+ if 表达式多语句分支体 + lambda 块体 named
            CheckRoundTrip("switch 语句 + if 多语句分支 + lambda 块体",
                "func main() {\n" +
                "    switch(x) {\n" +
                "        (1) -> { handleOne() }\n" +
                "        default -> { handleOther() }\n" +
                "    }\n" +
                "    var r = if (x > 0) named check { return@check x } else { return@check 0 }\n" +
                "    var f = func{(x: i32): i32 -> { return@_ x }}\n" +
                "}\n");
            // class 声明（泛型 + 继承 + implements + 成员方法）
            CheckRoundTrip("class 声明（泛型 + 继承 + implements）",
                "pub class MyList\\<TElement> : List implements Iterable {\n" +
                "    pub func size(): i32 {\n" +
                "        return 0\n" +
                "    }\n" +
                "}\n");

            CompilerTestTools.Blank();
        }

        // {"file":...} 元记录（多文件 dump 分隔行）应被跳过
        public static void TestMetaRecordSkip()
        {
            CompilerTestTools.Section("AST JSONL 反序列化：{\"file\":...} 元记录跳过");
            try
            {
                var ast = CompilerTestTools.ParseRoot("var x = 42\n");
                var first = new StringWriter();
                AstJsonlSerializer.Serialize(ast, first);

                var withMeta = "{\"file\":\"a.rg\"}\n" + first;
                var restored = AstJsonlDeserializer.Deserialize(new StringReader(withMeta));
                var second = new StringWriter();
                AstJsonlSerializer.Serialize(restored, second);

                CaseAssertions.Check("带元记录前缀的 JSONL 往返一致",
                    second.ToString(), first.ToString());
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue("{\"file\":...} 元记录跳过", false,
                    $"unexpected {ex.GetType().Name}: {ex.Message}");
            }
            CompilerTestTools.Blank();
        }

        // 反向用例：非法输入抛 CompilerInternalException（带行号/原因），不接受部分成功
        public static void TestDeserializeErrors()
        {
            CompilerTestTools.Section("AST JSONL 反序列化：非法输入拒绝");

            CheckDeserializeError("缺 id 键的行",
                "{\"parent\":null,\"via\":null,\"type\":\"RootASTNode\",\"span\":null,\"fields\":{}}",
                "missing the 'id' key");
            CheckDeserializeError("未知 type",
                "{\"id\":1,\"parent\":null,\"via\":null,\"type\":\"NoSuchNode\",\"span\":null,\"fields\":{}}",
                "unknown AST node/carrier type");
            CheckDeserializeError("parent 引用悬空",
                "{\"id\":1,\"parent\":null,\"via\":null,\"type\":\"RootASTNode\",\"span\":null,\"fields\":{}}\n" +
                "{\"id\":2,\"parent\":99,\"via\":\"Declarations[0]\",\"type\":\"IntLiteralASTNode\",\"span\":null,\"fields\":{}}",
                "dangling parent id");
            CheckDeserializeError("via 无法定位成员",
                "{\"id\":1,\"parent\":null,\"via\":null,\"type\":\"RootASTNode\",\"span\":null,\"fields\":{}}\n" +
                "{\"id\":2,\"parent\":1,\"via\":\"NoSuchMember\",\"type\":\"IntLiteralASTNode\",\"span\":null,\"fields\":{}}",
                "has no [ChildAstNode] member");
            // via 无 [i] 下标但成员是集合：落位前类型校验，不得抛裸 ArgumentException
            CheckDeserializeError("via 缺 [i] 下标的集合成员",
                "{\"id\":1,\"parent\":null,\"via\":null,\"type\":\"RootASTNode\",\"span\":null,\"fields\":{}}\n" +
                "{\"id\":2,\"parent\":1,\"via\":\"Declarations\",\"type\":\"IntLiteralASTNode\",\"span\":null,\"fields\":{}}",
                "cannot hold");
            // 单节点成员类型与记录类型不匹配（TypeReferenceASTNode 槽装 IntLiteralASTNode）
            CheckDeserializeError("单节点成员类型不匹配",
                "{\"id\":1,\"parent\":null,\"via\":null,\"type\":\"RootASTNode\",\"span\":null,\"fields\":{}}\n" +
                "{\"id\":2,\"parent\":1,\"via\":\"Declarations[0]\",\"type\":\"VariableDeclarationASTNode\",\"span\":null,\"fields\":{}}\n" +
                "{\"id\":3,\"parent\":2,\"via\":\"TypeAnnotation\",\"type\":\"IntLiteralASTNode\",\"span\":null,\"fields\":{}}",
                "cannot hold");

            CompilerTestTools.Blank();
        }

        // ===== 测试辅助 =====

        // 解析源码 → 序列化到 StringWriter → 逐行解析为 JsonElement
        // （JsonDocument 由调用方持有并在 finally 释放，JsonElement 生命周期依赖它）
        private static List<JsonElement> Dump(string code, List<JsonDocument> docs)
        {
            var tokens = new Lexer().Tokenize(code);
            var ast = new Parser().Parse(tokens);
            var writer = new StringWriter();
            AstJsonlSerializer.Serialize(ast, writer);

            var lines = new List<JsonElement>();
            foreach (var text in writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var doc = JsonDocument.Parse(text);  // 解析失败即抛异常 → 用例失败
                docs.Add(doc);
                lines.Add(doc.RootElement);
            }
            return lines;
        }

        // 往返无损断言：源码 → Parse → Serialize → Deserialize → 再 Serialize，
        // 两次 JSONL 输出逐行一致（Deserialize 内部已跑 ASTIntegrityValidator）
        private static void CheckRoundTrip(string label, string code)
        {
            try
            {
                var ast = CompilerTestTools.ParseRoot(code);
                var first = new StringWriter();
                AstJsonlSerializer.Serialize(ast, first);

                var restored = AstJsonlDeserializer.Deserialize(new StringReader(first.ToString()));
                var second = new StringWriter();
                AstJsonlSerializer.Serialize(restored, second);

                CaseAssertions.Check(label + "：两次序列化逐行一致",
                    second.ToString(), first.ToString());
                CaseAssertions.CheckTrue(label + "：往返后顶层声明数一致",
                    restored.Declarations.Count == ast.Declarations.Count);
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue(label, false, $"unexpected {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 非法输入断言：Deserialize 抛 CompilerInternalException 且消息含片段
        private static void CheckDeserializeError(string label, string jsonl, string expectedMessagePart)
        {
            try
            {
                AstJsonlDeserializer.Deserialize(new StringReader(jsonl));
                CaseAssertions.CheckTrue(label, false, "应失败但成功了");
            }
            catch (CompilerInternalException ex)
            {
                CaseAssertions.CheckTrue(label + "（正确失败）",
                    ex.Message.Contains(expectedMessagePart), ex.Message);
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue(label, false, $"unexpected {ex.GetType().Name}: {ex.Message}");
            }
        }

        // carrier 行判定：type 名映射到 [AstCarrier] 类型（carrier 行 span 恒为 null）
        private static readonly HashSet<string> CarrierTypeNames = typeof(RootASTNode).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<AstCarrierAttribute>() != null)
            .Select(t => t.Name)
            .ToHashSet();

        // 结构断言：六个键齐全、id 连续、parent 引用已出现 id、via 规则、
        // fields 不含 Parent、节点行 span 非空且首尾不颠倒（M28）、
        // carrier 行 span 恒为 null（v2，M32）
        private static void CheckStructure(string label, List<JsonElement> lines)
        {
            CaseAssertions.CheckTrue($"{label}：首行是 RootASTNode（id=1, parent/via 为 null）",
                lines.Count > 0 &&
                lines[0].GetProperty("id").GetInt32() == 1 &&
                lines[0].GetProperty("parent").ValueKind == JsonValueKind.Null &&
                lines[0].GetProperty("via").ValueKind == JsonValueKind.Null &&
                lines[0].GetProperty("type").GetString() == "RootASTNode");

            bool keysComplete = true;
            bool idsSequential = true;
            bool parentsValid = true;
            bool viaValid = true;
            bool fieldsExcludeParent = true;
            bool spanValid = true;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (!line.TryGetProperty("id", out var idEl) ||
                    !line.TryGetProperty("parent", out var parentEl) ||
                    !line.TryGetProperty("via", out var viaEl) ||
                    !line.TryGetProperty("type", out _) ||
                    !line.TryGetProperty("span", out var spanEl) ||
                    !line.TryGetProperty("fields", out var fieldsEl))
                {
                    keysComplete = false;
                    continue;
                }
                if (idEl.GetInt32() != i + 1) idsSequential = false;
                if (i == 0)
                {
                    if (parentEl.ValueKind != JsonValueKind.Null) parentsValid = false;
                }
                else
                {
                    // parent 只能引用已出现的 id（深度优先先父后子）
                    if (parentEl.ValueKind != JsonValueKind.Number ||
                        parentEl.GetInt32() < 1 || parentEl.GetInt32() > i)
                    {
                        parentsValid = false;
                    }
                    if (viaEl.ValueKind != JsonValueKind.String) viaValid = false;
                }
                if (fieldsEl.TryGetProperty("Parent", out _)) fieldsExcludeParent = false;
                if (CarrierTypeNames.Contains(line.GetProperty("type").GetString() ?? ""))
                {
                    // carrier 行（v2）：span 恒为 null
                    if (spanEl.ValueKind != JsonValueKind.Null) spanValid = false;
                }
                else
                {
                    // 节点行 span：解析产物必然非空；source 非空；End 不早于 Start
                    if (spanEl.ValueKind != JsonValueKind.Object ||
                        string.IsNullOrEmpty(spanEl.GetProperty("source").GetString()) ||
                        spanEl.GetProperty("endLine").GetInt64() < spanEl.GetProperty("startLine").GetInt64() ||
                        (spanEl.GetProperty("endLine").GetInt64() == spanEl.GetProperty("startLine").GetInt64() &&
                         spanEl.GetProperty("endCol").GetInt64() < spanEl.GetProperty("startCol").GetInt64()) ||
                        spanEl.GetProperty("endOffset").GetInt64() < spanEl.GetProperty("startOffset").GetInt64())
                    {
                        spanValid = false;
                    }
                }
            }
            CaseAssertions.CheckTrue($"{label}：每行 id/parent/via/type/span/fields 六键齐全", keysComplete);
            CaseAssertions.CheckTrue($"{label}：id 从 1 连续自增（行数 == 记录数）", idsSequential);
            CaseAssertions.CheckTrue($"{label}：parent 均引用已出现的 id（根为 null）", parentsValid);
            CaseAssertions.CheckTrue($"{label}：非根行 via 非空", viaValid);
            CaseAssertions.CheckTrue($"{label}：fields 不含 Parent", fieldsExcludeParent);
            CaseAssertions.CheckTrue($"{label}：节点行 span 非空且首尾不颠倒；carrier 行 span 为 null", spanValid);
        }

        private static IEnumerable<JsonElement> OfType(List<JsonElement> lines, string type)
        {
            return lines.Where(l => l.GetProperty("type").GetString() == type);
        }

        private static string? FieldString(JsonElement line, string name)
        {
            var fields = line.GetProperty("fields");
            if (!fields.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            return value.GetString();
        }

        // ===== 入口 =====


        internal static TestSuiteData Spec { get; } = new("AstJsonlSerializer",
        [
            (nameof(TestVariableDeclaration), TestVariableDeclaration),
            (nameof(TestBinaryExpression), TestBinaryExpression),
            (nameof(TestImport), TestImport),
            (nameof(TestImportAllForm), TestImportAllForm),
            (nameof(TestGenericTypeReference), TestGenericTypeReference),
            (nameof(TestRoundTrip), TestRoundTrip),
            (nameof(TestMetaRecordSkip), TestMetaRecordSkip),
            (nameof(TestDeserializeErrors), TestDeserializeErrors),
        ], sectionTitle: "AstJsonlSerializer");
    }
}
