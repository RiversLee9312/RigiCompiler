using System.Collections.Generic;

namespace LatteCompiler.Bil
{
    // BIL 模块与程序集结构（BIL_STANDARD §4）。
    // BIL 对象模型是自足的：不引用 Semantic/Lowering/AST 的任何类型
    // （ARCHITECTURE §6.3）——类型引用、符号一律以 canonical 字符串承载
    // （BIL 世界以字符串为身份；CanonicalSymbolPrinter 的投影即其来源）。
    // 依赖方向单向：Lowering → Bil；BilVerifier（M58）与 VM（后续里程碑）
    // 只依赖本目录。
    // M57 起资源形态强类型化（标量类型枚举 + switch-table/catch-table
    // 专用资源类），header/元素文本拼写由模型自渲染。

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

    // §4.1/§19.1/§19.3 标量类型关键字（无前导点；拼写见 BilSpellings）
    public enum BilScalarType
    {
        String, Bool, Char, F32, F64,
        I8, I16, I32, I64, U8, U16, U32, U64,
        RawHex, RawBin,
    }

    // Metadata 条目（§4.1：程序集级非执行信息，指令不得读取）：
    // module = string "com.example.app"
    public sealed class BilMetadataEntry
    {
        public string Key { get; }
        public BilScalarType Type { get; }
        public string LiteralText { get; }    // 字面量原文（含引号）

        public BilMetadataEntry(string key, BilScalarType type, string literalText)
        {
            Key = key;
            Type = type;
            LiteralText = literalText;
        }
    }

    // Resources 段条目（§4.2/§19）：指令不得内联字面量，一切字面值与
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

    // 标量资源（§19.1/§19.3）：R_X = string "..." / bool true / i64 123 /
    // f64 0.5 / raw.hex x2FF... / raw.bin b0101...
    public sealed class BilScalarResource : BilResource
    {
        public BilScalarType Type { get; }
        public string LiteralText { get; }    // 字面量原文（字符串含引号）

        public BilScalarResource(string name, BilScalarType type, string literalText) : base(name)
        {
            Type = type;
            LiteralText = literalText;
        }
    }

    // null 资源（§19.1）：R_X = null type(com.example::User)
    public sealed class BilNullResource : BilResource
    {
        public string TypeRef { get; }

        public BilNullResource(string name, string typeRef) : base(name)
        {
            TypeRef = typeRef;
        }
    }

    // 复合资源（§19.2）：array/pair/map 通用形态。
    // Header 为构造头文本（array<string> / pair<string, i64> / map<string, i64>）；
    // 元素为字面量原文（map 为 "k" = v 行）。
    // Multiline = false：{ "a", "b" } 单行（array/pair）；
    // Multiline = true：元素各占一行（map，§19.2 规范排版）。
    // （switch-table/catch-table 有专用资源类，见下）
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

    // switch 常量表资源（§19.4）：R_X = switch-table<SELECTOR_TYPE> { ... }，
    // 单行形态；header 由 selector 类型引用自渲染
    public sealed class BilSwitchTableResource : BilResource
    {
        public string SelectorTypeRef { get; }
        public IReadOnlyList<string> Elements { get; }

        public BilSwitchTableResource(string name, string selectorTypeRef,
            IReadOnlyList<string> elements) : base(name)
        {
            SelectorTypeRef = selectorTypeRef;
            Elements = elements;
        }

        internal string HeaderText => $"switch-table<{SelectorTypeRef}>";
    }

    // catch 表条目（§19.5）：type(EXCEPTION_TYPE) -> blk(CATCH_BLOCK)，
    // 保序（表序即匹配序，不能重排）
    public sealed class BilCatchEntry
    {
        public BilTypeOperand ExceptionType { get; }
        public BilBlock Handler { get; }

        public BilCatchEntry(BilTypeOperand exceptionType, BilBlock handler)
        {
            ExceptionType = exceptionType;
            Handler = handler;
        }

        internal string Render() => $"{ExceptionType.Render()} -> blk({Handler.Id})";
    }

    // catch 表资源（§19.5）：R_X = catch-table { 元素各占一行 }，多行形态
    public sealed class BilCatchTableResource : BilResource
    {
        public IReadOnlyList<BilCatchEntry> Entries { get; }

        public BilCatchTableResource(string name, IReadOnlyList<BilCatchEntry> entries)
            : base(name)
        {
            Entries = entries;
        }
    }
}
