using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// AST JSONL 序列化测试：解析小段 Latte 源码 → AstJsonlSerializer
    /// 序列化到 StringWriter → 断言：
    /// - 每行是合法 JSON，id/parent/via/type/span/fields 六个键齐全；
    /// - id 从 1 连续自增（行数 == 节点数），parent 引用已出现的 id（根为 null）；
    /// - 非根行 via 非空，fields 不含 Parent；
    /// - 每行 span 非空且首尾不颠倒（M28）；
    /// - 关键节点类型与 via/fields 内容出现（含 private 字段下钻、
    ///   carrier 下钻、Symbol 点分字符串渲染、enum 渲染为名字）。
    /// </summary>
    public static class AstJsonlSerializerTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        public static void TestVariableDeclaration()
        {
            Console.WriteLine("=== Testing AST JSONL: var x = 42 ===");
            var docs = new List<JsonDocument>();
            try
            {
                var lines = Dump("var x = 42\n", docs);
                CheckStructure("var x = 42", lines);

                var decl = OfType(lines, "VariableDeclarationASTNode").ToList();
                Check("出现 VariableDeclarationASTNode（via=Declarations[0]）",
                    decl.Count == 1 && decl[0].GetProperty("via").GetString() == "Declarations[0]");
                Check("VariableDeclaration fields：Name=x, IsConst=false",
                    decl.Count == 1 &&
                    FieldString(decl[0], "Name") == "x" &&
                    decl[0].GetProperty("fields").TryGetProperty("IsConst", out var constEl) &&
                    constEl.GetBoolean() == false);

                var literal = OfType(lines, "IntLiteralASTNode").ToList();
                Check("IntLiteralASTNode fields：Value=42, IntType=I32（enum 渲染为名字）",
                    literal.Count == 1 &&
                    literal[0].GetProperty("fields").TryGetProperty("Value", out var valueEl) &&
                    valueEl.GetInt64() == 42 &&
                    FieldString(literal[0], "IntType") == "I32");

                Check("private 字段下钻：字面量经 via=literal 挂载",
                    lines.Any(l => l.GetProperty("via").GetString() == "literal"));
                Check("ExpressionRoot 的 private expression 字段下钻（via=expression）",
                    lines.Any(l => l.GetProperty("via").GetString() == "expression"));
            }
            catch (Exception ex)
            {
                Fail("var x = 42", $"unexpected {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                foreach (var d in docs) d.Dispose();
            }
            Console.WriteLine();
        }

        public static void TestBinaryExpression()
        {
            Console.WriteLine("=== Testing AST JSONL: var r = 1 + (2 * 3) ===");
            var docs = new List<JsonDocument>();
            try
            {
                var lines = Dump("var r = 1 + (2 * 3)\n", docs);
                CheckStructure("var r = 1 + (2 * 3)", lines);

                var binaries = OfType(lines, "BinaryExpressionASTNode").ToList();
                Check("两个 BinaryExpressionASTNode", binaries.Count == 2);
                Check("Operator 字段 + 与 * 都出现",
                    binaries.Any(b => FieldString(b, "Operator") == "+") &&
                    binaries.Any(b => FieldString(b, "Operator") == "*"));
                Check("via=Left / via=Right 都出现",
                    lines.Any(l => l.GetProperty("via").GetString() == "Left") &&
                    lines.Any(l => l.GetProperty("via").GetString() == "Right"));
                Check("括号分组 GroupExpressionASTNode 出现",
                    OfType(lines, "GroupExpressionASTNode").Any());
            }
            catch (Exception ex)
            {
                Fail("var r = 1 + (2 * 3)", $"unexpected {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                foreach (var d in docs) d.Dispose();
            }
            Console.WriteLine();
        }

        public static void TestImport()
        {
            Console.WriteLine("=== Testing AST JSONL: import core.collections.List ===");
            var docs = new List<JsonDocument>();
            try
            {
                var lines = Dump("import core.collections.List\n", docs);
                CheckStructure("import", lines);

                Check("ImportASTNode 出现", OfType(lines, "ImportASTNode").Any());
                Check("carrier 下钻：via 含 (ImportItem.symbolNode)",
                    lines.Any(l => (l.GetProperty("via").GetString() ?? "").Contains("(ImportItem.symbolNode)")));
                Check("Symbol 渲染为点分字符串 core.collections.List",
                    OfType(lines, "SymbolASTNode").Any(l => FieldString(l, "symbol") == "core.collections.List"));
            }
            catch (Exception ex)
            {
                Fail("import core.collections.List", $"unexpected {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                foreach (var d in docs) d.Dispose();
            }
            Console.WriteLine();
        }

        public static void TestGenericTypeReference()
        {
            Console.WriteLine("=== Testing AST JSONL: var list: List\\<i32> ===");
            var docs = new List<JsonDocument>();
            try
            {
                var lines = Dump("var list: List\\<i32>\n", docs);
                CheckStructure("var list: List\\<i32>", lines);

                Check("TypeReferenceASTNode 出现（via=TypeAnnotation）",
                    OfType(lines, "TypeReferenceASTNode").Any(l => l.GetProperty("via").GetString() == "TypeAnnotation"));
                Check("泛型 Symbol 渲染为 List\\<i32>",
                    OfType(lines, "SymbolASTNode").Any(l => FieldString(l, "symbol") == "List\\<i32>"));
            }
            catch (Exception ex)
            {
                Fail("var list: List\\<i32>", $"unexpected {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                foreach (var d in docs) d.Dispose();
            }
            Console.WriteLine();
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

        // 结构断言：六个键齐全、id 连续、parent 引用已出现 id、via 规则、
        // fields 不含 Parent、span 非空且首尾不颠倒（M28）
        private static void CheckStructure(string label, List<JsonElement> lines)
        {
            Check($"{label}：首行是 RootASTNode（id=1, parent/via 为 null）",
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
                // span：解析产物必然非空；source 非空；End 不早于 Start
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
            Check($"{label}：每行 id/parent/via/type/span/fields 六键齐全", keysComplete);
            Check($"{label}：id 从 1 连续自增（行数 == 节点数）", idsSequential);
            Check($"{label}：parent 均引用已出现的 id（根为 null）", parentsValid);
            Check($"{label}：非根行 via 非空", viaValid);
            Check($"{label}：fields 不含 Parent", fieldsExcludeParent);
            Check($"{label}：span 非空且首尾不颠倒（source 非空）", spanValid);
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

        private static void Check(string name, bool condition)
        {
            if (condition)
            {
                Console.WriteLine($"  [PASS] {name}");
                passCount++;
            }
            else
            {
                Fail(name, "断言不成立");
            }
        }

        private static void Fail(string name, string message)
        {
            Console.WriteLine($"  [FAIL] {name}");
            Console.WriteLine($"      => {message}");
            failCount++;
        }

        // ===== 入口 =====
        public static int RunAll()
        {
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  AST JSONL Serializer Tests        ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");

            passCount = 0;
            failCount = 0;

            TestVariableDeclaration();
            TestBinaryExpression();
            TestImport();
            TestGenericTypeReference();

            Console.WriteLine($"=== AST JSONL Serializer Tests Complete: {passCount} passed, {failCount} failed ===");
            return failCount;
        }
    }
}
