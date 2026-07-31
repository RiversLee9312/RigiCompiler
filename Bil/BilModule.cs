using System.Collections.Generic;

namespace LatteCompiler.Bil
{
    // BIL 模块与程序集结构（BIL_STANDARD §4）。
    // BIL 对象模型是自足的：不引用 Semantic/Lowering/AST 的任何类型
    // （ARCHITECTURE §6.3）——类型引用、符号一律以 canonical 字符串承载
    // （BIL 世界以字符串为身份；CanonicalSymbolPrinter 的投影即其来源）。
    // 依赖方向单向：Lowering → Bil；verifier/VM（后续里程碑）只依赖本目录。

    public sealed class BilModule
    {
        // BIL 版本头（§4：BIL "1.1"）
        public string BilVersion { get; set; } = "1.1";

        // 各段物理顺序固定（§4）：Metadata → Resources → LocalSymbols →
        // ExternalSymbols → 函数定义；标准生成器输出全部段（空段也输出）。
        // 段内条目（§8.4.1）：类型声明与裸成员声明（全局函数/全局字段）
        // 按生成器输出顺序混合排列
        public List<BilMetadataEntry> Metadata { get; } = new List<BilMetadataEntry>();
        public List<BilResource> Resources { get; } = new List<BilResource>();
        public List<BilSymbolSectionEntry> LocalSymbols { get; } = new List<BilSymbolSectionEntry>();
        public List<BilSymbolSectionEntry> ExternalSymbols { get; } = new List<BilSymbolSectionEntry>();
        public List<BilFunction> Functions { get; } = new List<BilFunction>();
    }

    // Metadata 条目（§4.1：程序集级非执行信息，指令不得读取）：
    // module = string "com.example.app"
    public sealed class BilMetadataEntry
    {
        public string Key { get; }
        public string TypeKeyword { get; }    // string / i64 / bool ...
        public string LiteralText { get; }    // 字面量原文（含引号）

        public BilMetadataEntry(string key, string typeKeyword, string literalText)
        {
            Key = key;
            TypeKeyword = typeKeyword;
            LiteralText = literalText;
        }
    }

    // Resources 段条目（§4.2/§18）：指令不得内联字面量，一切字面值与
    // 静态表在 Resources 声明后经 load res(...) 引用。资源是不可变值。
    public abstract class BilResource
    {
        // 本地标识符（§5.1：R_Message 等；资源名不以 . 开头）
        public string Name { get; }

        protected BilResource(string name)
        {
            Name = name;
        }
    }

    // 标量资源（§18.1/§18.3）：R_X = string "..." / bool true / i64 123 /
    // f64 0.5 / raw.hex x2FF... / raw.bin b0101...
    public sealed class BilScalarResource : BilResource
    {
        public string TypeKeyword { get; }    // string / bool / i64 / f64 / raw.hex / raw.bin ...
        public string LiteralText { get; }    // 字面量原文（字符串含引号）

        public BilScalarResource(string name, string typeKeyword, string literalText) : base(name)
        {
            TypeKeyword = typeKeyword;
            LiteralText = literalText;
        }
    }

    // null 资源（§18.1）：R_X = null type(com.example::User)
    public sealed class BilNullResource : BilResource
    {
        public string TypeRef { get; }

        public BilNullResource(string name, string typeRef) : base(name)
        {
            TypeRef = typeRef;
        }
    }

    // 复合资源（§18.2/§18.4/§18.5）：array/pair/map/switch-table/catch-table。
    // Header 为构造头文本（array<string> / pair<string, i64> / map<string, i64> /
    // switch-table<.i32> / catch-table）；元素为字面量原文（map 为 "k" = v 行、
    // catch-table 为 type(...) -> blk(...) 行）。
    // Multiline = false：{ "a", "b" } 单行（array/pair/switch-table）；
    // Multiline = true：元素各占一行（map/catch-table，§18.2/§18.5 规范排版）。
    public sealed class BilCollectionResource : BilResource
    {
        public string Header { get; }
        public IReadOnlyList<string> Elements { get; }
        public bool Multiline { get; }

        public BilCollectionResource(string name, string header, IReadOnlyList<string> elements,
            bool multiline = false) : base(name)
        {
            Header = header;
            Elements = elements;
            Multiline = multiline;
        }
    }
}
