using System.Collections.Generic;
using System.Text;

namespace RigiCompiler.Bil
{
    // BIL 文本读取器（BilReader）：把 BilWriter 输出的标准 BIL 文本完整
    // 解析回 BilModule，与 BilWriter 构成往返（round-trip 幂等——Write →
    // Read → Write 逐字节一致）。只依赖 Bil/ 目录；模型以字符串为身份，
    // blk/res 操作数持对象引用，读取器在解析期把引用解析到真实对象。
    // 读取器与生成器拼写共享 BilSpellings 反查（见 Reader 内映射）。

    // BIL 文本解析错误（含行号）：vm 命令读取的 .bil 文件非法时抛出，
    // 与 CompilerInternalException（编译器内部错误）区分——这是输入错误。
    public sealed class BilParseException : System.Exception
    {
        public int Line { get; }

        public BilParseException(int line, string message)
            : base($"第 {line} 行: {message}")
        {
            Line = line;
        }
    }

    public static class BilReader
    {
        public static BilModule Read(string text) => new Reader(text).Parse();
    }

    internal sealed class Reader
    {
        private readonly string[] _lines;
        private int _pos;
        private readonly BilModule _module = new BilModule();

        // 资源名 → 资源对象（res(...) 操作数解析；catch-table 在 try 解析期懒绑定）
        private readonly Dictionary<string, BilResource> _resources = new();
        // 资源段内的出现顺序（含 catch-table 名——用于末尾按序重建 module.Resources）
        private readonly List<string> _resourceOrder = new();
        // catch-table 原始条目（typeRef, blockId）——handler block 归所属函数
        private readonly Dictionary<string, List<(string TypeRef, string BlockId)>> _catchTableRaw = new();
        // 当前函数 block id → BilBlock（blk(...)/catch-table handler 解析）
        private Dictionary<string, BilBlock>? _currentBlocks;

        // 结构条目（指令/类型声明）的行号锚点：解析其内容时 Advance 已越过
        // 首行，Error 用此锚点报首行行号（0 = 用 CurrentLine）
        private int _errorLine;

        internal Reader(string text)
        {
            var raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            // 去掉末尾由换行产生的空行（不影响中间空行）
            _lines = raw;
        }

        private bool AtEnd => _pos >= _lines.Length;

        private string CurrentRaw => AtEnd ? "" : _lines[_pos];

        private int CurrentIndent
        {
            get
            {
                var line = CurrentRaw;
                int i = 0;
                while (i < line.Length && line[i] == ' ') i++;
                return i;
            }
        }

        private string CurrentContent => CurrentRaw.Trim();

        private int CurrentLine => _pos + 1;

        private void Advance() => _pos++;

        private BilParseException Error(string message) =>
            new BilParseException(_errorLine > 0 ? _errorLine : CurrentLine, message);

        private void ExpectContent(string expected)
        {
            if (AtEnd || CurrentContent != expected)
            {
                throw Error($"期望 \"{expected}\"，实际 \"{CurrentContent}\"");
            }
            Advance();
        }

        private void SkipBlankLines()
        {
            while (!AtEnd && CurrentContent.Length == 0) Advance();
        }

        public BilModule Parse()
        {
            SkipBlankLines();
            ParseHeader();
            SkipBlankLines();
            ExpectContent("Metadata {");
            ParseMetadata();
            SkipBlankLines();
            ExpectContent("Resources {");
            ParseResources();
            SkipBlankLines();
            ExpectContent("LocalSymbols {");
            ParseSymbolSection(_module.LocalSymbols);
            SkipBlankLines();
            ExpectContent("ExternalSymbols {");
            ParseSymbolSection(_module.ExternalSymbols);
            while (true)
            {
                SkipBlankLines();
                if (AtEnd) break;
                ParseFunction();
            }
            FinalizeResources();
            return _module;
        }

        private void ParseHeader()
        {
            var line = CurrentContent;
            if (!line.StartsWith("BIL \"") || !line.EndsWith("\""))
            {
                throw Error("模块头必须形如 BIL \"1.1\"");
            }
            _module.BilVersion = line.Substring(5, line.Length - 6);
            Advance();
        }

        // ===== Metadata（§4.1）=====

        private void ParseMetadata()
        {
            while (true)
            {
                SkipBlankLines();
                if (AtEnd) throw Error("Metadata 段未闭合");
                var line = CurrentContent;
                if (line == "}")
                {
                    Advance();
                    return;
                }
                Advance();
                var eq = line.IndexOf('=');
                if (eq < 0) throw Error("Metadata 条目缺 \"=\"");
                var key = line.Substring(0, eq).Trim();
                var rest = line.Substring(eq + 1).Trim();
                var space = rest.IndexOf(' ');
                if (space < 0) throw Error("Metadata 条目缺标量类型");
                var typeText = rest.Substring(0, space);
                var literal = rest.Substring(space + 1).Trim();
                _module.Metadata.Add(new BilMetadataEntry(key, ParseScalarType(typeText), literal));
            }
        }

        // ===== Resources（§19）=====

        private void ParseResources()
        {
            while (true)
            {
                SkipBlankLines();
                if (AtEnd) throw Error("Resources 段未闭合");
                if (CurrentContent == "}")
                {
                    Advance();
                    return;
                }
                // 资源头行：Name = 值（多行形态的 { 在此行末尾）
                var headerIndent = CurrentIndent;
                var header = CurrentContent;
                Advance();
                var eq = header.IndexOf('=');
                if (eq < 0) throw Error("资源条目缺 \"=\"");
                var name = header.Substring(0, eq).Trim();
                var value = header.Substring(eq + 1).Trim();
                value = StripTrailingComma(value);

                if (value.StartsWith("null ", System.StringComparison.Ordinal))
                {
                    // null 资源：null type(REFTYPE)
                    var typeRef = Unwrap(value.Substring(5), "type(");
                    _resources[name] = new BilNullResource(name, typeRef);
                    _resourceOrder.Add(name);
                }
                else if (value == "catch-table {")
                {
                    // catch-table：多行，元素 type(T) -> blk(id)
                    var entries = new List<(string, string)>();
                    while (true)
                    {
                        SkipBlankLines();
                        if (AtEnd) throw Error("catch-table 资源未闭合");
                        var line = StripTrailingComma(CurrentContent);
                        Advance();
                        if (line == "}")
                        {
                            break;
                        }
                        var arrow = line.IndexOf("->");
                        if (arrow < 0) throw Error("catch-table 元素缺 \"->\"");
                        var typeRef = Unwrap(line.Substring(0, arrow).Trim(), "type(");
                        var blockId = Unwrap(line.Substring(arrow + 2).Trim(), "blk(");
                        entries.Add((typeRef, blockId));
                    }
                    _catchTableRaw[name] = entries;
                    _resourceOrder.Add(name);
                }
                else if (value.StartsWith("switch-table<", System.StringComparison.Ordinal))
                {
                    // switch-table<SELECTOR> { e1, e2 }
                    var angle = value.IndexOf('>');
                    if (angle < 0) throw Error("switch-table 缺 \">\"");
                    var selector = value.Substring("switch-table<".Length, angle - "switch-table<".Length);
                    var elements = ParseBracedElements(value, "switch-table");
                    _resources[name] = new BilSwitchTableResource(name, selector, elements);
                    _resourceOrder.Add(name);
                }
                else if (value.EndsWith("{", System.StringComparison.Ordinal))
                {
                    // 多行集合：HEADER { 后逐行元素
                    var headerText = value.Substring(0, value.Length - 1).Trim();
                    var elements = new List<string>();
                    while (true)
                    {
                        SkipBlankLines();
                        if (AtEnd) throw Error("集合资源未闭合");
                        var line = StripTrailingComma(CurrentContent);
                        Advance();
                        if (line == "}")
                        {
                            break;
                        }
                        elements.Add(line);
                    }
                    _resources[name] = new BilCollectionResource(name, headerText, elements,
                        multiline: true);
                    _resourceOrder.Add(name);
                }
                else
                {
                    // 含 { ... } 的单行集合，或标量
                    var brace = value.IndexOf('{');
                    if (brace >= 0 && value.EndsWith("}", System.StringComparison.Ordinal))
                    {
                        var headerText = value.Substring(0, brace).Trim();
                        var elements = ParseBracedElements(value, "collection");
                        _resources[name] = new BilCollectionResource(name, headerText, elements,
                            multiline: false);
                    }
                    else
                    {
                        var space = value.IndexOf(' ');
                        if (space < 0) throw Error("标量资源缺字面量");
                        var type = ParseScalarType(value.Substring(0, space));
                        var literal = value.Substring(space + 1).Trim();
                        ValidateScalar(type, literal);
                        _resources[name] = new BilScalarResource(name, type, literal);
                    }
                    _resourceOrder.Add(name);
                }
            }
        }

        private void ValidateScalar(BilScalarType type, string literal)
        {
            try
            {
                switch (type)
                {
                    case BilScalarType.I8: sbyte.Parse(literal,
                        System.Globalization.CultureInfo.InvariantCulture); break;
                    case BilScalarType.I16: short.Parse(literal,
                        System.Globalization.CultureInfo.InvariantCulture); break;
                    case BilScalarType.I32: int.Parse(literal,
                        System.Globalization.CultureInfo.InvariantCulture); break;
                    case BilScalarType.I64: long.Parse(literal,
                        System.Globalization.CultureInfo.InvariantCulture); break;
                    case BilScalarType.U8: byte.Parse(literal,
                        System.Globalization.CultureInfo.InvariantCulture); break;
                    case BilScalarType.U16: ushort.Parse(literal,
                        System.Globalization.CultureInfo.InvariantCulture); break;
                    case BilScalarType.U32: uint.Parse(literal,
                        System.Globalization.CultureInfo.InvariantCulture); break;
                    case BilScalarType.U64: BilScalarLiteral.ParseUnsigned(literal); break;
                    case BilScalarType.String:
                        if (BilScalarLiteral.DecodeString(literal).IndexOf('\0') >= 0)
                            throw new FormatException("字符串资源含 NUL");
                        break;
                    case BilScalarType.Char:
                        if (BilScalarLiteral.DecodeChar(literal) == '\0')
                            throw new FormatException("字符资源含 NUL");
                        break;
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw Error($"标量资源字面量非法或越界：{literal}");
            }
        }

        // 单行 { e1, e2 } 元素抽取（switch-table 与单行集合共用）
        private List<string> ParseBracedElements(string value, string kind)
        {
            var brace = value.IndexOf('{');
            if (brace < 0 || !value.EndsWith("}", System.StringComparison.Ordinal))
            {
                throw Error($"{kind} 资源形态非法");
            }
            var inner = value.Substring(brace + 1, value.Length - brace - 2).Trim();
            var elements = new List<string>();
            if (inner.Length == 0) return elements;
            foreach (var part in SplitTopLevel(inner, ','))
            {
                elements.Add(part.Trim());
            }
            return elements;
        }

        // ===== 符号段（§8）=====

        private void ParseSymbolSection(List<BilSymbolSectionEntry> section)
        {
            while (true)
            {
                SkipBlankLines();
                if (AtEnd) throw Error("符号段未闭合");
                var line = CurrentContent;
                if (line == "}")
                {
                    Advance();
                    return;
                }
                if (line.StartsWith(".type ", System.StringComparison.Ordinal))
                {
                    section.Add(ParseTypeDeclaration());
                }
                else if (line.StartsWith(".case ", System.StringComparison.Ordinal))
                {
                    section.Add(ParseCaseDeclaration(line));
                }
                else if (line.StartsWith(".method", System.StringComparison.Ordinal)
                    || line.StartsWith(".field", System.StringComparison.Ordinal)
                    || line.StartsWith(".static-method", System.StringComparison.Ordinal)
                    || line.StartsWith(".static-field", System.StringComparison.Ordinal))
                {
                    int memberIndent = CurrentIndent;
                    Advance();
                    section.Add(ParseSimpleMember(line, memberIndent));
                }
                else
                {
                    throw Error($"未知符号段条目 \"{line}\"");
                }
            }
        }

        // .type SYM = kind [generic(...)] [mods] { ... }（或 extends/implements 续行）
        private BilTypeDeclaration ParseTypeDeclaration()
        {
            var header = CurrentContent;
            Advance();
            var rest = header.Substring(".type ".Length);
            var eq = rest.IndexOf('=');
            if (eq < 0) throw Error("类型声明缺 \"=\"");
            var symbol = rest.Substring(0, eq).Trim();
            var tail = rest.Substring(eq + 1).Trim();

            var words = SplitWords(tail);
            if (words.Count == 0) throw Error("类型声明缺 kind");
            var kind = ParseTypeKind(words[0]);
            var declaration = new BilTypeDeclaration(symbol, kind);
            int index = 1;
            if (index < words.Count && words[index].StartsWith("generic(", System.StringComparison.Ordinal))
            {
                var inner = Unwrap(words[index], "generic(");
                foreach (var param in SplitTopLevel(inner, ','))
                {
                    var text = param.Trim();
                    if (text.StartsWith("out ", System.StringComparison.Ordinal))
                    {
                        declaration.GenericParameters.Add(text.Substring(4));
                        declaration.GenericVariances.Add(BilGenericVariance.Out);
                    }
                    else if (text.StartsWith("in ", System.StringComparison.Ordinal))
                    {
                        declaration.GenericParameters.Add(text.Substring(3));
                        declaration.GenericVariances.Add(BilGenericVariance.In);
                    }
                    else
                    {
                        declaration.GenericParameters.Add(text);
                        declaration.GenericVariances.Add(BilGenericVariance.None);
                    }
                }
                index++;
            }

            // 余下 words：单行形态 = [mods] {；多行形态（无 {）= 续行 extends/implements/[mods] {
            var singleLine = false;
            var modifierWords = new List<string>();
            for (int i = index; i < words.Count; i++)
            {
                if (words[i] == "{")
                {
                    singleLine = true;
                    break;
                }
                modifierWords.Add(words[i]);
            }
            if (!singleLine)
            {
                // extends / implements 续行 + 修饰符行 { 收尾
                while (true)
                {
                    SkipBlankLines();
                    if (AtEnd) throw Error("类型声明未闭合");
                    var line = CurrentContent;
                    Advance();
                    if (line.StartsWith("extends ", System.StringComparison.Ordinal))
                    {
                        declaration.ExtendsType = line.Substring("extends ".Length).Trim();
                        continue;
                    }
                    if (line.StartsWith("implements ", System.StringComparison.Ordinal))
                    {
                        foreach (var iface in SplitTopLevel(
                            line.Substring("implements ".Length).Trim(), ','))
                        {
                            declaration.ImplementsTypes.Add(iface.Trim());
                        }
                        continue;
                    }
                    // 修饰符行以 { 收尾
                    if (line.EndsWith("{", System.StringComparison.Ordinal))
                    {
                        var mods = line.Substring(0, line.Length - 1).Trim();
                        if (mods.Length > 0)
                        {
                            AddModifiers(declaration.Modifiers, mods);
                        }
                        break;
                    }
                    throw Error($"类型声明续行非法 \"{line}\"");
                }
            }
            else if (modifierWords.Count > 0)
            {
                AddModifiers(declaration.Modifiers, string.Join(" ", modifierWords));
            }

            ParseTypeMembers(declaration);
            return declaration;
        }

        private void ParseTypeMembers(BilTypeDeclaration declaration)
        {
            while (true)
            {
                SkipBlankLines();
                if (AtEnd) throw Error("类型声明未闭合");
                var line = CurrentContent;
                if (line == "}")
                {
                    Advance();
                    return;
                }
                if (line.StartsWith(".case ", System.StringComparison.Ordinal))
                {
                    Advance();
                    declaration.Members.Add(ParseCaseDeclaration(line));
                }
                else if (line.StartsWith(".method", System.StringComparison.Ordinal)
                    || line.StartsWith(".field", System.StringComparison.Ordinal)
                    || line.StartsWith(".static-method", System.StringComparison.Ordinal)
                    || line.StartsWith(".static-field", System.StringComparison.Ordinal))
                {
                    int memberIndent = CurrentIndent;
                    Advance();
                    declaration.Members.Add(ParseSimpleMember(line, memberIndent));
                }
                else
                {
                    throw Error($"未知类型成员 \"{line}\"");
                }
            }
        }

        // 成员声明（.method/.field/.static-method/.static-field SYM [mods]，
        // 或两行形态：符号行 + 修饰符续行）
        private BilSimpleMemberDeclaration ParseSimpleMember(string line, int memberIndent)
        {
            var space = line.IndexOf(' ');
            if (space < 0) throw Error("成员声明缺符号");
            var kindText = line.Substring(0, space);
            var rest = line.Substring(space + 1).Trim();
            var kind = kindText switch
            {
                ".field" => BilMemberKind.Field,
                ".static-field" => BilMemberKind.StaticField,
                ".method" => BilMemberKind.Method,
                ".static-method" => BilMemberKind.StaticMethod,
                _ => throw Error($"未知成员声明关键字 \"{kindText}\""),
            };

            var symSpace = rest.IndexOf(' ');
            string symbol;
            string modifierText;
            if (symSpace < 0)
            {
                symbol = rest;
                modifierText = "";
            }
            else
            {
                symbol = rest.Substring(0, symSpace);
                modifierText = rest.Substring(symSpace + 1).Trim();
            }

            bool modifiersOnNextLine = false;
            if (modifierText.Length == 0 && !AtEnd && CurrentIndent > memberIndent)
            {
                // 两行形态（§20 wrapper 隐藏字段示例）：下一行更深缩进承载修饰符
                Advance();
                modifierText = CurrentContent;
                modifiersOnNextLine = true;
            }

            var modifiers = new List<BilModifier>();
            if (modifierText.Length > 0)
            {
                AddModifiers(modifiers, modifierText);
            }
            return new BilSimpleMemberDeclaration(kind, symbol, modifiers, modifiersOnNextLine);
        }

        // .case NAME(params) discriminant auto|res(R)
        private BilCaseDeclaration ParseCaseDeclaration(string line)
        {
            var rest = line.Substring(".case ".Length).Trim();
            var open = rest.IndexOf('(');
            if (open < 0) throw Error("case 声明缺参数段");
            var qualifiedName = rest.Substring(0, open);
            var close = rest.IndexOf(')', open);
            if (close < 0) throw Error("case 声明参数段未闭合");
            var parameters = new List<BilCaseParameter>();
            var paramText = rest.Substring(open + 1, close - open - 1).Trim();
            if (paramText.Length > 0)
            {
                foreach (var param in SplitTopLevel(paramText, ','))
                {
                    var colon = param.IndexOf(':');
                    if (colon < 0) throw Error("case 参数缺 \":\"");
                    parameters.Add(new BilCaseParameter(
                        param.Substring(0, colon).Trim(),
                        param.Substring(colon + 1).Trim()));
                }
            }
            var tail = rest.Substring(close + 1).Trim();
            string? discriminant = null;
            if (tail.Length > 0)
            {
                if (!tail.StartsWith("discriminant ", System.StringComparison.Ordinal))
                {
                    throw Error("case 声明缺 discriminant");
                }
                var disc = tail.Substring("discriminant ".Length).Trim();
                if (disc != "auto")
                {
                    discriminant = Unwrap(disc, "res(");
                }
            }
            return new BilCaseDeclaration(qualifiedName, parameters, discriminant);
        }

        // ===== 函数（§9）=====

        private void ParseFunction()
        {
            var line = CurrentContent;
            Advance();
            // fn(SYMBOL) { → 取出 SYMBOL（符号自身含括号，取首 fn( 与末 ) { 之间）
            if (!line.StartsWith("fn(", System.StringComparison.Ordinal)
                || !line.EndsWith(") {", System.StringComparison.Ordinal))
            {
                throw Error($"函数头形态非法 \"{line}\"");
            }
            var symbol = line.Substring(3, line.Length - 6).Trim();
            var function = new BilFunction(symbol);
            _module.Functions.Add(function);

            SkipBlankLines();
            ExpectContent(".args {");
            while (true)
            {
                SkipBlankLines();
                if (AtEnd) throw Error(".args 未闭合");
                var argLine = CurrentContent;
                if (argLine == "}")
                {
                    Advance();
                    break;
                }
                Advance();
                argLine = StripTrailingComma(argLine);
                var eq = argLine.IndexOf('=');
                if (eq < 0) throw Error(".args 条目缺 \"=\"");
                function.Args.Add(new BilArgDeclaration(
                    argLine.Substring(0, eq).Trim(),
                    argLine.Substring(eq + 1).Trim()));
            }

            SkipBlankLines();
            ExpectContent(".vars {");
            while (true)
            {
                SkipBlankLines();
                if (AtEnd) throw Error(".vars 未闭合");
                var varLine = CurrentContent;
                if (varLine == "}")
                {
                    Advance();
                    break;
                }
                Advance();
                varLine = StripTrailingComma(varLine);
                var lastSpace = varLine.LastIndexOf(' ');
                if (lastSpace < 0) throw Error(".vars 条目缺变量名");
                function.Vars.Add(new BilVarDeclaration(
                    varLine.Substring(0, lastSpace).Trim(),
                    varLine.Substring(lastSpace + 1).Trim()));
            }

            // 第一遍：扫描 .block 头，建立 blockMap——块可被指令前向引用
            // （entry 块内的 if 引用其后的 if0-then 块），必须先建全块对象
            var blockMap = new Dictionary<string, BilBlock>();
            for (int scan = _pos; scan < _lines.Length; scan++)
            {
                var raw = _lines[scan];
                var content = raw.Trim();
                if (content.Length == 0) continue;
                if (content == "}" && IndentOf(raw) == 0) break;   // 函数收尾 }
                if (content.StartsWith(".block ", System.StringComparison.Ordinal))
                {
                    var (id, modifiers) = ParseBlockHeader(content);
                    var block = new BilBlock(id, modifiers.ToArray());
                    function.Blocks.Add(block);
                    blockMap[id] = block;
                }
            }

            // 第二遍：解析块头 + 指令
            while (true)
            {
                SkipBlankLines();
                if (AtEnd) throw Error("函数未闭合");
                if (CurrentContent == "}")
                {
                    Advance();
                    break;
                }
                ParseBlockInstructions(function, blockMap);
            }
        }

        private void ParseBlockInstructions(BilFunction function,
            Dictionary<string, BilBlock> blockMap)
        {
            var line = CurrentContent;
            Advance();
            var (id, _) = ParseBlockHeader(line);
            var block = blockMap[id];
            var savedBlocks = _currentBlocks;
            _currentBlocks = blockMap;

            while (true)
            {
                SkipBlankLines();
                if (AtEnd) throw Error("block 未闭合");
                if (CurrentContent == "}")
                {
                    Advance();
                    break;
                }
                var instrIndent = CurrentIndent;
                int instrLine = CurrentLine;
                var text = new StringBuilder(CurrentContent);
                Advance();
                while (!AtEnd && CurrentIndent > instrIndent)
                {
                    text.Append(' ').Append(CurrentContent);
                    Advance();
                }
                _errorLine = instrLine;
                try
                {
                    block.Instructions.Add(ParseInstruction(text.ToString()));
                }
                finally
                {
                    _errorLine = 0;
                }
            }
            _currentBlocks = savedBlocks;
        }

        private (string Id, List<BilBlockModifier> Modifiers) ParseBlockHeader(string line)
        {
            if (!line.StartsWith(".block ", System.StringComparison.Ordinal))
            {
                throw Error($"期望 .block，实际 \"{line}\"");
            }
            var rest = line.Substring(".block ".Length).Trim();
            if (!rest.EndsWith("{", System.StringComparison.Ordinal))
            {
                throw Error(".block 头未以 { 收尾");
            }
            rest = rest.Substring(0, rest.Length - 1).Trim();
            var space = rest.IndexOf(' ');
            string id;
            var modifiers = new List<BilBlockModifier>();
            if (space < 0)
            {
                id = rest;
            }
            else
            {
                id = rest.Substring(0, space);
                foreach (var mod in SplitWords(rest.Substring(space + 1)))
                {
                    modifiers.Add(mod switch
                    {
                        "entrypoint" => BilBlockModifier.Entrypoint,
                        "volatile" => BilBlockModifier.Volatile,
                        "unsafe" => BilBlockModifier.Unsafe,
                        _ => throw Error($"未知 block 修饰符 \"{mod}\""),
                    });
                }
            }
            return (id, modifiers);
        }

        private static int IndentOf(string line)
        {
            int i = 0;
            while (i < line.Length && line[i] == ' ') i++;
            return i;
        }

        // ===== 指令（§10–§18）=====

        private BilInstruction ParseInstruction(string text)
        {
            var words = SplitWords(text);
            if (words.Count == 0) throw Error("空指令");
            var opcode = words[0];
            var operands = new List<BilOperand>();
            for (int i = 1; i < words.Count; i++)
            {
                operands.Add(ParseOperand(words[i]));
            }
            return BuildInstruction(opcode, operands);
        }

        private BilInstruction BuildInstruction(string opcode, List<BilOperand> ops)
        {
            switch (opcode)
            {
                // §11 二元
                case "add": case "sub": case "mul": case "div": case "mod":
                case "and": case "or":
                case "bin.and": case "bin.or": case "bin.xor":
                case "shift.left": case "shift.right": case "shift.right.unsigned":
                case "cmp.eq": case "cmp.ne": case "cmp.lt": case "cmp.le": case "cmp.gt": case "cmp.ge":
                    Count(opcode, ops, 3);
                    return new BinaryIntrinsicInstruction(ParseBinaryOp(opcode),
                        Var(ops[0], opcode), Var(ops[1], opcode), Var(ops[2], opcode));
                // §11 一元
                case "opposite": case "not": case "bin.not":
                    Count(opcode, ops, 2);
                    return new UnaryIntrinsicInstruction(ParseUnaryOp(opcode),
                        Var(ops[0], opcode), Var(ops[1], opcode));
                // §12.1/§12.2 cast
                case "cast":
                    Count(opcode, ops, 3);
                    return new CastInstruction(Var(ops[0], opcode), Var(ops[1], opcode),
                        Type(ops[2], opcode), isSafe: false);
                case "cast.safe":
                    Count(opcode, ops, 3);
                    return new CastInstruction(Var(ops[0], opcode), Var(ops[1], opcode),
                        Type(ops[2], opcode), isSafe: true);
                case "cast.indirect":
                    Count(opcode, ops, 3);
                    return new CastIndirectInstruction(Var(ops[0], opcode), Var(ops[1], opcode),
                        Var(ops[2], opcode), isSafe: false);
                case "cast.safe.indirect":
                    Count(opcode, ops, 3);
                    return new CastIndirectInstruction(Var(ops[0], opcode), Var(ops[1], opcode),
                        Var(ops[2], opcode), isSafe: true);
                // §12.3 类型检查
                case "type.is": case "type.supers": case "type.with":
                    Count(opcode, ops, 3);
                    return new DirectTypeCheckInstruction(ParseTypeCheckKind(opcode),
                        Var(ops[0], opcode), Type(ops[1], opcode), Var(ops[2], opcode));
                case "type.is.indirect": case "type.supers.indirect": case "type.with.indirect":
                    Count(opcode, ops, 3);
                    return new IndirectTypeCheckInstruction(ParseTypeCheckKind(opcode.Substring(0,
                        opcode.Length - ".indirect".Length)),
                        Var(ops[0], opcode), Var(ops[1], opcode), Var(ops[2], opcode));
                case "type.is.case":
                    Count(opcode, ops, 3);
                    return new IsCaseInstruction(Var(ops[0], opcode), Case(ops[1], opcode),
                        Var(ops[2], opcode));
                // §12.4 wrapper
                case "get.wrapper":
                    Count(opcode, ops, 3);
                    return new GetWrapperInstruction(Var(ops[0], opcode), Type(ops[1], opcode),
                        Var(ops[2], opcode));
                case "get.wrapper.indirect":
                    Count(opcode, ops, 3);
                    return new GetWrapperIndirectInstruction(Var(ops[0], opcode),
                        Var(ops[1], opcode), Var(ops[2], opcode));
                case "get.wrapper.field":
                    Count(opcode, ops, 4);
                    return new GetWrapperFieldInstruction(Var(ops[0], opcode), Field(ops[1], opcode),
                        Type(ops[2], opcode), Var(ops[3], opcode));
                case "get.self":
                    Count(opcode, ops, 1);
                    return new GetSelfInstruction(Var(ops[0], opcode));
                // §12.6 getid
                case "getid.var":
                    Count(opcode, ops, 2);
                    return new GetIdVarInstruction(Var(ops[0], opcode), Var(ops[1], opcode));
                case "getid.type":
                    Count(opcode, ops, 2);
                    return new GetIdTypeInstruction(Type(ops[0], opcode), Var(ops[1], opcode));
                case "getid.field":
                    Count(opcode, ops, 2);
                    return new GetIdFieldInstruction(Field(ops[0], opcode), Var(ops[1], opcode));
                // §13
                case "load":
                    Count(opcode, ops, 2);
                    return new LoadInstruction(Res(ops[0], opcode), Var(ops[1], opcode));
                case "get.var":
                    Count(opcode, ops, 2);
                    return new GetVarInstruction(Var(ops[0], opcode), Var(ops[1], opcode));
                case "set.var":
                    Count(opcode, ops, 2);
                    return new SetVarInstruction(Var(ops[0], opcode), Var(ops[1], opcode));
                case "get.field":
                    Count(opcode, ops, 3);
                    return new GetFieldInstruction(Var(ops[0], opcode), Var(ops[1], opcode),
                        Field(ops[2], opcode));
                case "get.field.indirect":
                    Count(opcode, ops, 3);
                    return new GetFieldIndirectInstruction(Var(ops[0], opcode),
                        Var(ops[1], opcode), Var(ops[2], opcode));
                case "set.field":
                    Count(opcode, ops, 3);
                    return new SetFieldInstruction(Var(ops[0], opcode), Var(ops[1], opcode),
                        Field(ops[2], opcode));
                case "set.field.indirect":
                    Count(opcode, ops, 3);
                    return new SetFieldIndirectInstruction(Var(ops[0], opcode),
                        Var(ops[1], opcode), Var(ops[2], opcode));
                case "set.wrapper.field":
                    if (ops.Count < 3) throw Error("set.wrapper.field 操作数不足");
                    {
                        var chain = new List<BilOperand>();
                        for (int i = 2; i < ops.Count - 1; i++) chain.Add(ops[i]);
                        return new SetWrapperFieldInstruction(Var(ops[0], opcode),
                            Var(ops[1], opcode), chain, Field(ops[ops.Count - 1], opcode));
                    }
                case "get.field.static":
                    Count(opcode, ops, 3);
                    return new GetFieldStaticInstruction(Var(ops[0], opcode), Type(ops[1], opcode),
                        Field(ops[2], opcode));
                case "get.field.static.indirect":
                    Count(opcode, ops, 3);
                    return new GetFieldStaticIndirectInstruction(Var(ops[0], opcode),
                        Var(ops[1], opcode), Var(ops[2], opcode));
                case "set.field.static":
                    Count(opcode, ops, 3);
                    return new SetFieldStaticInstruction(Var(ops[0], opcode), Type(ops[1], opcode),
                        Field(ops[2], opcode));
                case "set.field.static.indirect":
                    Count(opcode, ops, 3);
                    return new SetFieldStaticIndirectInstruction(Var(ops[0], opcode),
                        Var(ops[1], opcode), Var(ops[2], opcode));
                case "get.array":
                    Count(opcode, ops, 3);
                    return new GetArrayInstruction(Var(ops[0], opcode), Var(ops[1], opcode),
                        Var(ops[2], opcode));
                case "set.array":
                    Count(opcode, ops, 3);
                    return new SetArrayInstruction(Var(ops[0], opcode), Var(ops[1], opcode),
                        Var(ops[2], opcode));
                // §14 构造
                case "new":
                    Count(opcode, ops, 3);
                    return new NewInstruction(Type(ops[0], opcode), Var(ops[1], opcode),
                        Variables(ops[2], opcode));
                case "new.indirect":
                    Count(opcode, ops, 3);
                    return new NewIndirectInstruction(Var(ops[0], opcode), Var(ops[1], opcode),
                        Variables(ops[2], opcode));
                case "new.case":
                    Count(opcode, ops, 4);
                    return new NewCaseInstruction(Type(ops[0], opcode), Case(ops[1], opcode),
                        Var(ops[2], opcode), Variables(ops[3], opcode));
                case "new.wrapped":
                    Count(opcode, ops, 4);
                    return new NewWrappedInstruction(Type(ops[0], opcode), Var(ops[1], opcode),
                        Variables(ops[2], opcode), Variables(ops[3], opcode));
                case "new.wrapped.case":
                    Count(opcode, ops, 5);
                    return new NewWrappedCaseInstruction(Type(ops[0], opcode), Case(ops[1], opcode),
                        Var(ops[2], opcode), Variables(ops[3], opcode), Variables(ops[4], opcode));
                case "new.wrapper.field":
                    Count(opcode, ops, 3);
                    return new NewWrapperFieldInstruction(Field(ops[0], opcode), Type(ops[1], opcode),
                        Variables(ops[2], opcode));
                case "new.wrapper.method":
                    Count(opcode, ops, 3);
                    return new NewWrapperMethodInstruction(Fn(ops[0], opcode), Type(ops[1], opcode),
                        Variables(ops[2], opcode));
                case "new.wrapper.entity":
                    Count(opcode, ops, 2);
                    return new NewWrapperEntityInstruction(Type(ops[0], opcode),
                        Variables(ops[1], opcode));
                // §15 调用
                case "invoke":
                    Count(opcode, ops, 3);
                    return new InvokeInstruction(Fn(ops[0], opcode), Var(ops[1], opcode),
                        Variables(ops[2], opcode));
                case "invoke.noret":
                    Count(opcode, ops, 2);
                    return new InvokeNoResultInstruction(Fn(ops[0], opcode),
                        Variables(ops[1], opcode));
                case "invoke.indirect":
                    Count(opcode, ops, 3);
                    return new InvokeIndirectInstruction(Var(ops[0], opcode), Var(ops[1], opcode),
                        Variables(ops[2], opcode));
                case "invoke.indirect.noret":
                    Count(opcode, ops, 2);
                    return new InvokeIndirectNoResultInstruction(Var(ops[0], opcode),
                        Variables(ops[1], opcode));
                // §16 控制流
                case "ret":
                    if (ops.Count == 0) return new RetInstruction();
                    if (ops.Count == 1) return new RetInstruction(Var(ops[0], opcode));
                    throw Error("ret 操作数过多");
                case "if":
                    Count(opcode, ops, 4);
                    return new IfInstruction(Var(ops[0], opcode), Block(ops[1], opcode),
                        ops[2] is BilNoneOperand ? null : Block(ops[2], opcode),
                        Var(ops[3], opcode));
                case "loop": case "loop.rev":
                    Count(opcode, ops, 5);
                    return new LoopInstruction(Var(ops[0], opcode), Block(ops[1], opcode),
                        ops[2] is BilNoneOperand ? null : Block(ops[2], opcode),
                        Block(ops[3], opcode), Var(ops[4], opcode), opcode == "loop.rev");
                case "break":
                    Count(opcode, ops, 1);
                    return new BreakInstruction(Var(ops[0], opcode));
                case "continue":
                    Count(opcode, ops, 1);
                    return new ContinueInstruction(Var(ops[0], opcode));
                case "switch":
                    Count(opcode, ops, 5);
                    return new SwitchInstruction(Var(ops[0], opcode), Res(ops[1], opcode),
                        Blocks(ops[2], opcode), Block(ops[3], opcode), Var(ops[4], opcode));
                case "call":
                    Count(opcode, ops, 2);
                    return new CallBlockInstruction(Block(ops[0], opcode), Var(ops[1], opcode));
                case "try":
                    Count(opcode, ops, 5);
                    return new TryInstruction(Block(ops[0], opcode), Var(ops[1], opcode),
                        Res(ops[2], opcode), ops[3] is BilNoneOperand ? null : Block(ops[3], opcode),
                        Var(ops[4], opcode));
                case "throw":
                    Count(opcode, ops, 1);
                    return new ThrowInstruction(Var(ops[0], opcode));
                // §17 协程
                case "await":
                    if (ops.Count == 1) return new AwaitInstruction(Var(ops[0], opcode));
                    if (ops.Count == 2) return new AwaitInstruction(Var(ops[0], opcode),
                        Var(ops[1], opcode));
                    throw Error("await 操作数个数非法");
                case "yield":
                    if (ops.Count == 0) return new YieldInstruction();
                    if (ops.Count == 1) return new YieldInstruction(Var(ops[0], opcode));
                    throw Error("yield 操作数个数非法");
                // §18 hint
                case "hint":
                    Count(opcode, ops, 1);
                    return new HintInstruction(Res(ops[0], opcode));
                default:
                    throw Error($"未知指令 opcode \"{opcode}\"");
            }
        }

        private void Count(string opcode, List<BilOperand> ops, int expected)
        {
            if (ops.Count != expected)
            {
                throw Error($"指令 {opcode} 期望 {expected} 个操作数，实际 {ops.Count}");
            }
        }

        // ===== 操作数（§10）=====

        private BilOperand ParseOperand(string token)
        {
            if (token == "none") return BilNoneOperand.Instance;
            if (token.StartsWith("$", System.StringComparison.Ordinal))
                return new BilVariableOperand(token.Substring(1));
            if (TryUnwrap(token, "fn(", out var inner)) return new BilFnOperand(inner);
            if (TryUnwrap(token, "field(", out inner)) return new BilFieldOperand(inner);
            if (TryUnwrap(token, "wrapper(", out inner)) return new BilWrapperOperand(inner);
            if (TryUnwrap(token, "type(", out inner)) return new BilTypeOperand(inner);
            if (TryUnwrap(token, "case(", out inner)) return new BilCaseOperand(inner);
            if (TryUnwrap(token, "blk(", out inner)) return new BilBlockOperand(RequireBlock(inner));
            if (TryUnwrap(token, "res(", out inner)) return new BilResourceOperand(RequireResource(inner));
            if (token.StartsWith("[", System.StringComparison.Ordinal)
                && token.EndsWith("]", System.StringComparison.Ordinal))
            {
                var innerList = token.Substring(1, token.Length - 2).Trim();
                var items = new List<BilOperand>();
                if (innerList.Length > 0)
                {
                    foreach (var item in SplitTopLevel(innerList, ','))
                    {
                        items.Add(ParseOperand(item.Trim()));
                    }
                }
                return new BilOperandList(items);
            }
            throw Error($"无法识别的操作数 \"{token}\"");
        }

        private BilBlock RequireBlock(string id)
        {
            if (_currentBlocks != null && _currentBlocks.TryGetValue(id, out var block))
            {
                return block;
            }
            throw Error($"引用了未定义的 block \"{id}\"");
        }

        private BilResource RequireResource(string name)
        {
            // catch-table 的块名属于使用它的函数。共享同一张文本表时，每个
            // try 都重新绑定当前函数的块，不能复用首个函数的驻留 handler。
            if (_catchTableRaw.TryGetValue(name, out var rawCatch))
            {
                var bound = BuildCatchTable(name, rawCatch);
                _resources.TryAdd(name, bound);
                return bound;
            }
            if (_resources.TryGetValue(name, out var resource))
            {
                return resource;
            }
            if (_catchTableRaw.TryGetValue(name, out var raw))
            {
                resource = BuildCatchTable(name, raw);
                _resources[name] = resource;
                return resource;
            }
            throw Error($"引用了未定义的资源 \"{name}\"");
        }

        private BilCatchTableResource BuildCatchTable(string name,
            List<(string TypeRef, string BlockId)> raw)
        {
            var entries = new List<BilCatchEntry>();
            foreach (var (typeRef, blockId) in raw)
            {
                BilBlock handler;
                if (_currentBlocks != null && _currentBlocks.TryGetValue(blockId, out var found))
                {
                    handler = found;
                }
                else
                {
                    if (_currentBlocks != null) throw Error($"catch-table 引用当前函数未定义的 block \"{blockId}\"");
                    handler = new BilBlock(blockId);
                }
                entries.Add(new BilCatchEntry(new BilTypeOperand(typeRef), handler));
            }
            return new BilCatchTableResource(name, entries);
        }

        // ===== 类型化操作数提取 =====

        private BilVariableOperand Var(BilOperand op, string ctx) =>
            op as BilVariableOperand ?? throw Error($"指令 {ctx} 期望变量，实际 \"{op.Render()}\"");

        private BilTypeOperand Type(BilOperand op, string ctx) =>
            op as BilTypeOperand ?? throw Error($"指令 {ctx} 期望 type(...)，实际 \"{op.Render()}\"");

        private BilFieldOperand Field(BilOperand op, string ctx) =>
            op as BilFieldOperand ?? throw Error($"指令 {ctx} 期望 field(...)，实际 \"{op.Render()}\"");

        private BilFnOperand Fn(BilOperand op, string ctx) =>
            op as BilFnOperand ?? throw Error($"指令 {ctx} 期望 fn(...)，实际 \"{op.Render()}\"");

        private BilCaseOperand Case(BilOperand op, string ctx) =>
            op as BilCaseOperand ?? throw Error($"指令 {ctx} 期望 case(...)，实际 \"{op.Render()}\"");

        private BilBlock Block(BilOperand op, string ctx) =>
            (op as BilBlockOperand)?.Block
                ?? throw Error($"指令 {ctx} 期望 blk(...)，实际 \"{op.Render()}\"");

        private BilResource Res(BilOperand op, string ctx) =>
            (op as BilResourceOperand)?.Resource
                ?? throw Error($"指令 {ctx} 期望 res(...)，实际 \"{op.Render()}\"");

        private List<BilVariableOperand> Variables(BilOperand op, string ctx)
        {
            if (op is not BilOperandList list)
            {
                throw Error($"指令 {ctx} 期望 [...] 实参列表，实际 \"{op.Render()}\"");
            }
            var result = new List<BilVariableOperand>();
            foreach (var item in list.Items)
            {
                result.Add(Var(item, ctx));
            }
            return result;
        }

        private List<BilBlock> Blocks(BilOperand op, string ctx)
        {
            if (op is not BilOperandList list)
            {
                throw Error($"指令 {ctx} 期望 [...] block 列表，实际 \"{op.Render()}\"");
            }
            var result = new List<BilBlock>();
            foreach (var item in list.Items)
            {
                result.Add(Block(item, ctx));
            }
            return result;
        }

        // ===== 修饰符（§8.2/§8.3/§8.4）=====

        private void AddModifiers(List<BilModifier> modifiers, string text)
        {
            foreach (var word in SplitWords(text))
            {
                modifiers.Add(ParseModifier(word));
            }
        }

        private BilModifier ParseModifier(string word)
        {
            switch (word)
            {
                case "pub": return new BilAccessibilityModifier(BilAccessibility.Public);
                case "protected": return new BilAccessibilityModifier(BilAccessibility.Protected);
                case "internal": return new BilAccessibilityModifier(BilAccessibility.Internal);
                case "priv": return new BilAccessibilityModifier(BilAccessibility.Private);
                case "open": return new BilKeywordModifier(BilKeyword.Open);
                case "abstract": return new BilKeywordModifier(BilKeyword.Abstract);
                case "singleton": return new BilKeywordModifier(BilKeyword.Singleton);
                case "rich": return new BilKeywordModifier(BilKeyword.Rich);
                case "shared": return new BilKeywordModifier(BilKeyword.Shared);
                case "ext": return new BilKeywordModifier(BilKeyword.Ext);
                case "init": return new BilKeywordModifier(BilKeyword.Init);
                case "native": return new BilKeywordModifier(BilKeyword.Native);
                case "entrypoint": return new BilKeywordModifier(BilKeyword.Entrypoint);
                case "const": return new BilKeywordModifier(BilKeyword.Const);
                case "var": return new BilKeywordModifier(BilKeyword.Var);
                case "backing": return new BilKeywordModifier(BilKeyword.Backing);
                case "computed": return new BilKeywordModifier(BilKeyword.Computed);
                case "readable": return new BilKeywordModifier(BilKeyword.Readable);
                case "writable": return new BilKeywordModifier(BilKeyword.Writable);
                case "compiler-generated": return new BilKeywordModifier(BilKeyword.CompilerGenerated);
                case "override": return new BilKeywordModifier(BilKeyword.Override);
                case "async": return new BilKeywordModifier(BilKeyword.Async);
                case "unsafe": return new BilKeywordModifier(BilKeyword.Unsafe);
                // 3b-δ1：借用返回标记（仅 native 声明；RcInjection 按此豁免
                // 调用结果槽的 acquire/release 义务）
                case "native-borrow": return new BilKeywordModifier(BilKeyword.NativeBorrow);
            }
            if (TryUnwrap(word, "operator(", out var name))
                return new BilOperatorModifier(name);
            if (TryUnwrap(word, "getter(", out var field))
                return new BilAccessorModifier(BilAccessorKind.Getter, field);
            if (TryUnwrap(word, "setter(", out field))
                return new BilAccessorModifier(BilAccessorKind.Setter, field);
            if (TryUnwrap(word, "symbol(", out var symbol))
                return new BilNativeSymbolModifier(symbol.Trim('"'));
            if (TryUnwrap(word, "lib(", out var lib))
                return new BilNativeLibraryModifier(lib.Trim('"'));
            if (TryUnwrap(word, "wrapper-proxy(", out var proxy))
            {
                return new BilWrapperProxyModifier(proxy == "wildcard"
                    ? BilProxyKind.Wildcard : BilProxyKind.Specific);
            }
            if (TryUnwrap(word, "wrapped(", out var wrapped))
                return new BilWrappedModifier(wrapped);
            throw Error($"未知修饰符 \"{word}\"");
        }

        // ===== 拼写反查 =====

        private BilScalarType ParseScalarType(string text)
        {
            return text switch
            {
                "string" => BilScalarType.String,
                "bool" => BilScalarType.Bool,
                "char" => BilScalarType.Char,
                "f32" => BilScalarType.F32,
                "f64" => BilScalarType.F64,
                "i8" => BilScalarType.I8,
                "i16" => BilScalarType.I16,
                "i32" => BilScalarType.I32,
                "i64" => BilScalarType.I64,
                "u8" => BilScalarType.U8,
                "u16" => BilScalarType.U16,
                "u32" => BilScalarType.U32,
                "u64" => BilScalarType.U64,
                "raw.hex" => BilScalarType.RawHex,
                "raw.bin" => BilScalarType.RawBin,
                _ => throw Error($"未知标量类型 \"{text}\""),
            };
        }

        private BilTypeKind ParseTypeKind(string text)
        {
            return text switch
            {
                "class" => BilTypeKind.Class,
                "struct" => BilTypeKind.Struct,
                "enum-struct" => BilTypeKind.EnumStruct,
                "interface" => BilTypeKind.Interface,
                "wrapper" => BilTypeKind.Wrapper,
                _ => throw Error($"未知类型种类 \"{text}\""),
            };
        }

        private BilBinaryOp ParseBinaryOp(string text)
        {
            return text switch
            {
                "add" => BilBinaryOp.Add,
                "sub" => BilBinaryOp.Sub,
                "mul" => BilBinaryOp.Mul,
                "div" => BilBinaryOp.Div,
                "mod" => BilBinaryOp.Mod,
                "and" => BilBinaryOp.And,
                "or" => BilBinaryOp.Or,
                "bin.and" => BilBinaryOp.BinAnd,
                "bin.or" => BilBinaryOp.BinOr,
                "bin.xor" => BilBinaryOp.BinXor,
                "shift.left" => BilBinaryOp.ShiftLeft,
                "shift.right" => BilBinaryOp.ShiftRight,
                "shift.right.unsigned" => BilBinaryOp.ShiftRightUnsigned,
                "cmp.eq" => BilBinaryOp.CmpEq,
                "cmp.ne" => BilBinaryOp.CmpNe,
                "cmp.lt" => BilBinaryOp.CmpLt,
                "cmp.le" => BilBinaryOp.CmpLe,
                "cmp.gt" => BilBinaryOp.CmpGt,
                "cmp.ge" => BilBinaryOp.CmpGe,
                _ => throw Error($"未知二元运算 \"{text}\""),
            };
        }

        private BilUnaryOp ParseUnaryOp(string text)
        {
            return text switch
            {
                "opposite" => BilUnaryOp.Opposite,
                "not" => BilUnaryOp.Not,
                "bin.not" => BilUnaryOp.BinNot,
                _ => throw Error($"未知一元运算 \"{text}\""),
            };
        }

        private BilTypeCheckKind ParseTypeCheckKind(string text)
        {
            return text switch
            {
                "type.is" => BilTypeCheckKind.Is,
                "type.supers" => BilTypeCheckKind.Supers,
                "type.with" => BilTypeCheckKind.With,
                _ => throw Error($"未知类型检查 \"{text}\""),
            };
        }

        // ===== 文本工具 =====

        // 按空白切分（()/[]/<> 深度内不切分）——操作数/修饰符/类型头共用
        private static List<string> SplitWords(string text)
        {
            var words = new List<string>();
            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length) break;
                int depth = 0;
                int start = i;
                while (i < text.Length)
                {
                    char c = text[i];
                    if (c == '(' || c == '[' || c == '<') depth++;
                    else if (c == ')' || c == ']' || c == '>') depth--;
                    else if (char.IsWhiteSpace(c) && depth == 0) break;
                    i++;
                }
                words.Add(text.Substring(start, i - start));
            }
            return words;
        }

        // 顶层逗号切分（()/[]/<> 深度内不切分）
        private static List<string> SplitTopLevel(string text, char separator)
        {
            var parts = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '(' || c == '[' || c == '<') depth++;
                else if (c == ')' || c == ']' || c == '>') depth--;
                else if (c == separator && depth == 0)
                {
                    parts.Add(text.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            var tail = text.Substring(start).Trim();
            if (tail.Length > 0) parts.Add(tail);
            return parts;
        }

        // token 形如 prefix(inner) 时取出 inner（含括号配平校验）
        private static bool TryUnwrap(string token, string prefix, out string inner)
        {
            inner = "";
            if (!token.StartsWith(prefix, System.StringComparison.Ordinal)
                || !token.EndsWith(")", System.StringComparison.Ordinal))
            {
                return false;
            }
            inner = token.Substring(prefix.Length, token.Length - prefix.Length - 1);
            return true;
        }

        private string Unwrap(string token, string prefix)
        {
            if (TryUnwrap(token, prefix, out var inner)) return inner;
            throw Error($"期望 {prefix}...) 形态，实际 \"{token}\"");
        }

        private static string StripTrailingComma(string text)
        {
            var trimmed = text.TrimEnd();
            if (trimmed.EndsWith(",", System.StringComparison.Ordinal))
            {
                return trimmed.Substring(0, trimmed.Length - 1).TrimEnd();
            }
            return trimmed;
        }

        // 末尾按序重建 module.Resources：catch-table 在 try 解析期已懒绑定到
        // _resources；未被 try 引用的 catch-table 以占位 block 补全（保 id）
        private void FinalizeResources()
        {
            _module.Resources.Clear();
            foreach (var name in _resourceOrder)
            {
                if (_resources.TryGetValue(name, out var resource))
                {
                    _module.Resources.Add(resource);
                }
                else if (_catchTableRaw.TryGetValue(name, out var raw))
                {
                    _module.Resources.Add(BuildCatchTable(name, raw));
                }
                else
                {
                    throw Error($"资源 \"{name}\" 未解析");
                }
            }
        }
    }
}
