using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LatteCompiler
{
    // 块注释层（M31 重写）：只跟踪 * 状态（*/ 闭合），不做任何转义。
    // 换行不吞（与行注释一致）：注释按行分段，换行本身以 LineBreakToken 入流——
    // 两条语句间唯一的分隔换行在块注释内时，语句分隔不丢失（SYNTAX §1.1）。
    public class CommentBlockLexerLayer : ILexerLayer
    {
        private bool asteriskAppeared = false;
        private CommentToken currentToken = new CommentToken("");
        public LexerLayerResult ParseChar(char currentChar, LexerLayerContext context)
        {
            switch (currentChar)
            {
                case Notations.ASTERISK:
                    // 连续的 *：前一个 * 确定属于内容
                    if (asteriskAppeared)
                    {
                        currentToken.Content += Notations.ASTERISK;
                    }
                    asteriskAppeared = true;
                    return LexerLayerResult.Continue.Instance;
                case Notations.FORWARD_SLASH:
                    if (asteriskAppeared)
                    {
                        // */ 闭合：注释 token 到此为止（内容不含 */）
                        context.PushToken(currentToken, includesCurrentChar: true);
                        currentToken = new CommentToken("");
                        asteriskAppeared = false;
                        return new LexerLayerResult.PopLayer(shouldKeepChar: false);
                    }
                    currentToken.Content += currentChar;
                    return LexerLayerResult.Continue.Instance;
                case '\n':
                    // 挂起的 * 归入内容；注释段推送（不含换行）；换行本身以
                    // LineBreakToken 入流；新段继续收注释（层不弹出）
                    if (asteriskAppeared)
                    {
                        currentToken.Content += Notations.ASTERISK;
                        asteriskAppeared = false;
                    }
                    context.PushToken(currentToken, includesCurrentChar: false);
                    context.PushToken(new LineBreakToken(), includesCurrentChar: true);
                    currentToken = new CommentToken("");
                    return LexerLayerResult.Continue.Instance;
                default:
                    // 普通字符：挂起的 * 归入内容（修复：此前孤 * 在此被静默丢弃）
                    if (asteriskAppeared)
                    {
                        currentToken.Content += Notations.ASTERISK;
                        asteriskAppeared = false;
                    }
                    currentToken.Content += currentChar;
                    return LexerLayerResult.Continue.Instance;
            }
        }
    }
    public class CommentLineLexerLayer : ILexerLayer
    {
        private CommentToken currentToken = new CommentToken("");
        public LexerLayerResult ParseChar(char currentChar, LexerLayerContext context)
        {
            if (currentChar != '\n')
            {
                currentToken.Content += currentChar;
                return LexerLayerResult.Continue.Instance;
            }
            else
            {
                // 换行不属于注释：注释 token 到此为止，\n 回流给下层产出 LineBreakToken
                // （Parser 以换行作为语句终止符，行注释不能把它吞掉）
                context.PushToken(currentToken, includesCurrentChar: false);
                currentToken = new CommentToken("");
                return new LexerLayerResult.PopLayer(shouldKeepChar: true);
            }
        }
    }
    public class WordLexerLayer : ILexerLayer
    {
        private WordToken currentToken = new WordToken("");

        public LexerLayerResult ParseChar(char currentChar, LexerLayerContext context)
        {
            if (Char.IsLetterOrDigit(currentChar) || (currentChar == Notations.UNDERSCORE))
            {
                currentToken.Content += currentChar;
                return LexerLayerResult.Continue.Instance;
            }
            else
            {
                context.PushToken(currentToken, includesCurrentChar: false);
                currentToken = new WordToken("");
                return new LexerLayerResult.PopLayer(shouldKeepChar: true);
            }
        }
    }
    /// <summary>
    /// 字符串转义表（单行/多行字符串共用，SYNTAX §3.3）：
    /// \ 后的字符 → 实际字符；未知转义返回 false（由调用方带位置报错）。
    /// </summary>
    internal static class StringEscape
    {
        public static bool TryProcess(char currentChar, out char value)
        {
            switch (currentChar)
            {
                case Notations.DOLLAR_SYMBOL: value = Notations.DOLLAR_SYMBOL; return true;
                case 'a': value = '\a'; return true;
                case 'b': value = '\b'; return true;
                case 't': value = '\t'; return true;
                case 'n': value = '\n'; return true;
                case 'v': value = '\v'; return true;
                case 'f': value = '\f'; return true;
                case 'r': value = '\r'; return true;
                case Notations.SINGLE_QUOTATION_MARK: value = Notations.SINGLE_QUOTATION_MARK; return true;
                case Notations.DOUBLE_QUOTATION_MARK: value = Notations.DOUBLE_QUOTATION_MARK; return true;
                case Notations.BACK_SLASH: value = Notations.BACK_SLASH; return true;
                default: value = '\0'; return false;
            }
        }
    }
    /// <summary>
    /// Tokenizes a string（双引号字符串；单引号字符字面量由 CharLexerLayer 处理）。
    /// 插值帧机制（M53）：未转义的 $ 挂起不立即入内容——下一字符是 { 即插值
    /// 引导（$ 丢弃，产出文本段 + InterpolationStartToken 并压基础层嵌套解析
    /// 表达式，配平由驱动按 token 层大括号计数、归零自动弹回本层）；否则 $
    /// 补入内容。空段也产出（"${x}" 两端情形），解码后判空归 Parser 统一跳过。
    /// </summary>
    public class StringLexerLayer : ILexerLayer
    {
        private bool backSlashAppeared = false;
        // 前一个字符是未转义的 $（插值引导判定；\$ 转义产出的字面 $ 不算，
        // 挂起未决期间不入内容——确认非引导后补入）
        private bool dollarAppeared = false;
        // 首段已产出标记（首段 span 修正：开界引号不属于任何段内容）
        private bool firstSegmentProduced = false;
        private StringToken currentToken = new StringToken("");
        public LexerLayerResult ParseChar(char currentChar, LexerLayerContext context)
        {

            if (backSlashAppeared)
            {
                backSlashAppeared = false;
                // 转义产出的任何字符（含 $）都不参与插值引导
                dollarAppeared = false;
                if (StringEscape.TryProcess(currentChar, out char value))
                {
                    currentToken.Content += value;
                }
                else
                {
                    context.RaiseError($"Unknown escape sequence:\\{currentChar}");
                }
                return LexerLayerResult.Continue.Instance;
            }
            else
            {
                // 插值引导（M53 帧机制）：未转义的 $ 后紧跟 {
                if (currentChar == '{' && dollarAppeared)
                {
                    // 文本段产出（$ 挂起未入内容，随引导一并丢弃；空段也产
                    // 出——解码后判空归 Parser 段序列统一跳过）；
                    // 压基础层嵌套解析表达式，配平弹回后从 currentToken 继续
                    context.PushToken(currentToken, includesCurrentChar: false);
                    if (!firstSegmentProduced)
                    {
                        // 首段 span 修正：token 头被开界引号占用（M28 规则），
                        // 段内容从引号后一字符开始
                        var start = currentToken.CharRange.Start;
                        start.column++;
                        start.offset++;
                        currentToken.CharRange.Start = start;
                        firstSegmentProduced = true;
                    }
                    // 段 span 尾修正：引导的 $ 不属于段内容（End 回收一列）
                    var end = currentToken.CharRange.End;
                    end.column--;
                    end.offset--;
                    currentToken.CharRange.End = end;
                    context.PushToken(new InterpolationStartToken(), includesCurrentChar: true);
                    currentToken = new StringToken("");
                    dollarAppeared = false;
                    return new LexerLayerResult.PushLayer(
                        new BaseLexerLayer(), shouldKeepChar: false);
                }
                // 挂起的 $ 确认非引导：补入内容
                if (dollarAppeared)
                {
                    currentToken.Content += Notations.DOLLAR_SYMBOL;
                    dollarAppeared = false;
                }
                if (currentChar == Notations.DOUBLE_QUOTATION_MARK)
                {
                    context.PushToken(currentToken, includesCurrentChar: true);
                    currentToken = new StringToken("");
                    return new LexerLayerResult.PopLayer(shouldKeepChar: false);
                }
                else if (currentChar == Notations.BACK_SLASH)
                {
                    backSlashAppeared = true;
                    return LexerLayerResult.Continue.Instance;
                }
                else if (currentChar == '\n')
                {
                    context.Log($"Line break symbol appears in string.Current token:{currentToken}");
                    throw context.RaiseError($"Line break symbol appears in string.");
                }
                else
                {
                    // $ 挂起（不入内容，待下一字符判定）；其余字符直接入内容
                    dollarAppeared = currentChar == Notations.DOLLAR_SYMBOL;
                    if (!dollarAppeared)
                    {
                        currentToken.Content += currentChar;
                    }
                    return LexerLayerResult.Continue.Instance;
                }
            }
        }
    }
    /// <summary>
    /// 字符字面量层（SYNTAX §3.3）：单引号内必须恰好一个字符或一个转义序列，
    /// 转义与字符串同一套（StringEscape）。空（''）、多字符（'ab'，收到第二个
    /// 内容字符即报错）、未知转义、换行未闭合（含 EOF 冲刷帧的虚拟换行）均为
    /// 词法错误；char 无插值概念。Base 层看到 ' 即推入本层（开界 ' 预消费）。
    /// </summary>
    public class CharLexerLayer : ILexerLayer
    {
        private enum State
        {
            AwaitContent,  // 等待内容字符（开界 ' 之后）
            EscapeSeen,    // 已见反斜杠，等待转义字符
            ContentSeen    // 已收到恰好一个内容字符，等待闭界 '
        }

        private State state = State.AwaitContent;
        private char value;

        public LexerLayerResult ParseChar(char currentChar, LexerLayerContext context)
        {
            switch (state)
            {
                case State.AwaitContent:
                    // 空字符字面量 ''：编译错误
                    if (currentChar == Notations.SINGLE_QUOTATION_MARK)
                    {
                        throw context.RaiseError("Empty character literal");
                    }
                    if (currentChar == Notations.BACK_SLASH)
                    {
                        state = State.EscapeSeen;
                        return LexerLayerResult.Continue.Instance;
                    }
                    if (currentChar == '\n')
                    {
                        // 换行未闭合（真实换行或 EOF 冲刷帧的虚拟换行）
                        throw context.RaiseError("Unterminated character literal");
                    }
                    value = currentChar;
                    state = State.ContentSeen;
                    return LexerLayerResult.Continue.Instance;

                case State.EscapeSeen:
                    // 反斜杠后紧跟真实换行（或 EOF 虚拟换行）：不支持行接续
                    if (currentChar == '\n')
                    {
                        throw context.RaiseError("Unexpected line break after \\ in character literal");
                    }
                    if (StringEscape.TryProcess(currentChar, out char escaped))
                    {
                        value = escaped;
                        state = State.ContentSeen;
                        return LexerLayerResult.Continue.Instance;
                    }
                    throw context.RaiseError($"Unknown escape sequence:\\{currentChar}");

                default: // State.ContentSeen
                    // 闭界 '：恰好一个字符，产出 token（闭界属于 token 范围）
                    if (currentChar == Notations.SINGLE_QUOTATION_MARK)
                    {
                        context.PushToken(new CharToken(value), includesCurrentChar: true);
                        return new LexerLayerResult.PopLayer(shouldKeepChar: false);
                    }
                    if (currentChar == '\n')
                    {
                        throw context.RaiseError("Unterminated character literal");
                    }
                    // 第二个内容字符：多字符字面量是编译错误
                    throw context.RaiseError("Character literal must contain exactly one character");
            }
        }
    }
    /// <summary>
    /// 多行字符串层（""" 开界已由 QuoteLexerLayer 预消费，SYNTAX §3.3）：
    /// 开界后必须紧跟换行（剥除）；闭界 """ 独占一行（其前仅空白，其缩进量 = 剥除基准）。
    /// 两阶段施工：原文按行缓冲（反斜杠只用于让 \" 不参与引号计数，不展开转义），
    /// 闭合时先剥缩进、再统一处理转义。
    /// 插值帧机制（M53）：未转义的 $ 挂起（下一字符是 { 即引导——段结算产出
    /// 段 token + InterpolationStartToken 并压基础层嵌套解析；配平弹回后行缓冲
    /// 继续）。段 token 以原文暂存（token 流保序），闭界确定缩进基准后统一回填
    /// 解码内容（剥缩进 + 转义）；词法先于解析全量完成，回填天然安全。
    /// </summary>
    public class MultilineStringLexerLayer : ILexerLayer
    {
        private enum State
        {
            OpeningNewline,  // 等待开界 """ 后的强制换行
            Content          // 内容累积中
        }

        private State state = State.OpeningNewline;
        // 当前段已完成的内容行（原文，未剥缩进未转义）；行间换行在段结算时补回
        private readonly List<string> lines = new List<string>();
        private readonly StringBuilder currentLine = new StringBuilder();
        private int quoteRun = 0;     // 挂起的连续引号数（<3 时归属未定，暂不入行）
        private bool escaped = false; // 前一字符是反斜杠（其后的 " 不参与终止判定）
        // 未转义的 $ 挂起未决（同行相邻判定天然成立：行间有 \n 时挂起 $
        // 已按字面补入行）；\$ 转义产出的字面 $ 不算（escaped 分支清零）
        private bool dollarSeen = false;
        // 已产出的段（token, 原文, 是否首段）：闭界统一回填
        private readonly List<(StringToken Token, string Raw, bool IsFirstSegment)> segments =
            new List<(StringToken, string, bool)>();

        public LexerLayerResult ParseChar(char currentChar, LexerLayerContext context)
        {
            // 开界后的强制换行：不属于内容（SYNTAX §3.3）
            if (state == State.OpeningNewline)
            {
                if (currentChar != '\n')
                {
                    throw context.RaiseError(
                        "Multi-line string literal must begin with a newline " +
                        "(content on the opening \"\"\" line is not allowed)");
                }
                state = State.Content;
                return LexerLayerResult.Continue.Instance;
            }

            if (escaped)
            {
                escaped = false;
                // 反斜杠后紧跟真实换行：转义表不含此项，立即报错（不支持行接续）
                if (currentChar == '\n')
                {
                    throw context.RaiseError("Unexpected line break after \\ in multi-line string literal");
                }
                // 转义产出的任何字符（含 $）都不参与插值引导
                dollarSeen = false;
                currentLine.Append(currentChar);
                return LexerLayerResult.Continue.Instance;
            }

            // 插值引导（M53 帧机制）：未转义的 $ 后紧跟 {
            if (currentChar == '{' && dollarSeen)
            {
                FlushQuoteRun();
                lines.Add(currentLine.ToString());
                currentLine.Clear();
                EmitSegment(context, includesCurrentChar: false);
                // 段 span 尾修正：引导的 $ 不属于段内容（End 回收一列）
                var segmentEnd = segments[^1].Token.CharRange.End;
                segmentEnd.column--;
                segmentEnd.offset--;
                segments[^1].Token.CharRange.End = segmentEnd;
                context.PushToken(new InterpolationStartToken(), includesCurrentChar: true);
                dollarSeen = false;
                return new LexerLayerResult.PushLayer(
                    new BaseLexerLayer(), shouldKeepChar: false);
            }
            // 挂起的 $ 确认非引导：补入行
            if (dollarSeen)
            {
                currentLine.Append(Notations.DOLLAR_SYMBOL);
                dollarSeen = false;
            }
            if (currentChar == Notations.BACK_SLASH)
            {
                currentLine.Append(currentChar);
                escaped = true;
                return LexerLayerResult.Continue.Instance;
            }
            if (currentChar == Notations.DOUBLE_QUOTATION_MARK)
            {
                quoteRun++;
                if (quoteRun == 3)
                {
                    // """ 终止判定：闭界必须独占一行（行前缀全为空白）
                    return Close(context);
                }
                return LexerLayerResult.Continue.Instance;
            }
            if (currentChar == '\n')
            {
                FlushQuoteRun();
                lines.Add(currentLine.ToString());
                currentLine.Clear();
                return LexerLayerResult.Continue.Instance;
            }
            FlushQuoteRun();
            // $ 挂起（不入行，待下一字符判定）；其余字符直接入行
            dollarSeen = currentChar == Notations.DOLLAR_SYMBOL;
            if (!dollarSeen)
            {
                currentLine.Append(currentChar);
            }
            return LexerLayerResult.Continue.Instance;
        }

        // 挂起的引号确认属于内容（遇到非引号字符 / 行尾）
        private void FlushQuoteRun()
        {
            currentLine.Append(Notations.DOUBLE_QUOTATION_MARK, quoteRun);
            quoteRun = 0;
        }

        // 段结算与产出（M53）：段 token 以原文暂存（span 由驱动按 token 头
        // 规则给——首段额外修正：开界 """ 与强制换行不属于任何段内容），
        // 加入回填队列；空段也产出（解码后判空归 Parser 统一跳过）。
        // 仅首段的段首行从行首开始（后续段首行是 } 的行内残余或空行，均不剥）
        private void EmitSegment(LexerLayerContext context, bool includesCurrentChar)
        {
            var raw = string.Join("\n", lines);
            var token = new StringToken(raw);
            if (segments.Count == 0)
            {
                // 首段 span 修正：token 头被开界引号占用——内容从开界 """
                // 3 字符 + 强制换行之后开始（下一行行首）
                var start = token.CharRange.Start;
                token.CharRange.Start = new CharPosition
                {
                    line = start.line + 1,
                    column = 1,
                    offset = start.offset + 4,
                };
            }
            segments.Add((token, raw, segments.Count == 0));
            lines.Clear();
            context.PushToken(token, includesCurrentChar: includesCurrentChar);
        }

        // 闭合：校验闭界行前缀 → 结算尾段 → 统一回填（剥缩进 + 转义）
        private LexerLayerResult Close(LexerLayerContext context)
        {
            string indent = currentLine.ToString();
            if (indent.Any(c => !Char.IsWhiteSpace(c)))
            {
                throw context.RaiseError(
                    "The closing \"\"\" of a multi-line string literal must be on its own line " +
                    "(to include \"\"\" in content, escape it as \\\"\"\")");
            }

            // 尾段结算：无插值时即整串——保持单 token 与含闭界引号的 span 行为
            bool hadInterpolation = segments.Count > 0;
            EmitSegment(context, includesCurrentChar: !hadInterpolation);

            // 统一回填：按行剥缩进（仅首段的段首行从行首开始，参与剥除）+ 转义展开
            foreach (var (token, raw, isFirstSegment) in segments)
            {
                token.Content = DecodeSegment(raw, indent.Length, isFirstSegment, context);
            }
            return new LexerLayerResult.PopLayer(shouldKeepChar: false);
        }

        // 段解码：行间 \n 属于内容；行首缩进剥除基准 N（全空白行输出空行）
        private static string DecodeSegment(string raw, int indent, bool isFirstSegment,
            LexerLayerContext context)
        {
            var rawLines = raw.Split('\n');
            var sb = new StringBuilder();
            for (int i = 0; i < rawLines.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append('\n');
                }
                var line = rawLines[i];
                string stripped;
                if (i == 0 && !isFirstSegment)
                {
                    // 后续段的段首行（} 的行内残余）：行前导已随表达式消费，不剥
                    stripped = line;
                }
                else if (string.IsNullOrWhiteSpace(line))
                {
                    stripped = "";
                }
                else
                {
                    if (line.Length < indent || line.Take(indent).Any(c => !Char.IsWhiteSpace(c)))
                    {
                        throw context.RaiseError(
                            "Line of multi-line string literal is less indented " +
                            "than the closing delimiter");
                    }
                    stripped = line.Substring(indent);
                }
                sb.Append(DecodeEscapes(stripped, context));
            }
            return sb.ToString();
        }

        // 转义展开（剥除缩进先于转义，SYNTAX §3.3；StringEscape 单源）：
        // 原文中的 \ 必有后继字符（\ 后紧跟真实换行在累积阶段已报错）
        private static string DecodeEscapes(string stripped, LexerLayerContext context)
        {
            var sb = new StringBuilder(stripped.Length);
            bool esc = false;
            foreach (char c in stripped)
            {
                if (esc)
                {
                    esc = false;
                    if (StringEscape.TryProcess(c, out char value))
                    {
                        sb.Append(value);
                    }
                    else
                    {
                        context.RaiseError($"Unknown escape sequence:\\{c}");
                    }
                }
                else if (c == Notations.BACK_SLASH)
                {
                    esc = true;
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
    public class NotationLexerLayer : ILexerLayer
    {
        // count 最大 2：两字符时要么合并产出、要么拆出单字符，必然 Pop，
        // 不会再收到第三个字符（原 notationCharCount > 2 分支不可达，已删）
        private int notationCharCount = 0;
        private NotationToken notationToken = new NotationToken("");
        public LexerLayerResult ParseChar(char currentChar, LexerLayerContext context)
        {
            notationCharCount++;
            notationToken.Content += currentChar;
            var isValidNotation = Notations.CharNotations.Contains(currentChar);
            if (notationCharCount == 1)
            {
                if (isValidNotation)
                {
                    return LexerLayerResult.Continue.Instance;
                }
                else
                {
                    throw context.RaiseError("Invalid notation:" + notationToken.Content);
                }
            }
            else
            {
                if (isValidNotation)
                {
                    if (Notations.StringNotations.Contains(notationToken.Content))
                    {
                        context.PushToken(notationToken, includesCurrentChar: true);
                        return new LexerLayerResult.PopLayer(shouldKeepChar: false);
                    }
                    else
                    {
                        notationToken.Content = notationToken.Content[0].ToString();
                        context.PushToken(notationToken, includesCurrentChar: false);
                        return new LexerLayerResult.PopLayer(shouldKeepChar: true);
                    }
                }
                else
                {
                    notationToken.Content = notationToken.Content[0].ToString();
                    context.PushToken(notationToken, includesCurrentChar: false);
                    return new LexerLayerResult.PopLayer(shouldKeepChar: true);
                }
            }
        }
    }

    /// <summary>
    /// 斜杠分流层：除号 /、除法赋值 /=、行注释 //、块注释 /* 的统一入口。
    /// Base 层看到 / 即推入本层（首个 / 已预消费），本层按下一个字符决定形态；
    /// 注释形态转发给持有的注释层实例（Delegate, don't implement），
    /// 注释层弹出时本层一并弹出。
    /// </summary>
    public class SlashLexerLayer : ILexerLayer
    {
        private enum State
        {
            Decision,     // 等待分流字符（/ * = 或其他）
            LineComment,  // 行注释形态：转发 CommentLineLexerLayer
            BlockComment  // 块注释形态：转发 CommentBlockLexerLayer
        }

        private State state = State.Decision;
        private ILexerLayer? commentLayer = null;

        public LexerLayerResult ParseChar(char currentChar, LexerLayerContext context)
        {
            switch (state)
            {
                case State.Decision:
                    // 行注释 //
                    if (currentChar == Notations.FORWARD_SLASH)
                    {
                        state = State.LineComment;
                        commentLayer = new CommentLineLexerLayer();
                        return LexerLayerResult.Continue.Instance;
                    }
                    // 块注释 /*
                    if (currentChar == Notations.ASTERISK)
                    {
                        state = State.BlockComment;
                        commentLayer = new CommentBlockLexerLayer();
                        return LexerLayerResult.Continue.Instance;
                    }
                    // 除号 /：产出单字符 token，当前字符回流给下层重新分发
                    // （/= 不再合并——复合赋值一律拆成两个 token，与 >= 同策略，
                    //   将来由 Parser 重组；M31）
                    context.PushToken(new NotationToken("/"), includesCurrentChar: false);
                    return new LexerLayerResult.PopLayer(shouldKeepChar: true);

                default:
                    // 注释形态：转发给注释层；注释层弹出时本层同步弹出
                    var result = commentLayer!.ParseChar(currentChar, context);
                    if (result is LexerLayerResult.PopLayer pop)
                    {
                        return new LexerLayerResult.PopLayer(pop.shouldKeepChar);
                    }
                    return result;
            }
        }
    }

    /// <summary>
    /// 引号分流层：单行字符串 "..."、空字符串 ""、多行字符串 """ 的统一入口。
    /// Base 层看到 " 即推入本层（首个 " 已预消费），本层按随后字符决定形态；
    /// 字符串形态转发给持有的字符串层实例（Delegate, don't implement，
    /// 与 SlashLexerLayer 同模式），字符串层弹出时本层一并弹出。
    /// </summary>
    public class QuoteLexerLayer : ILexerLayer
    {
        private enum State
        {
            Decision,    // 等待第二字符：" → 可能是多行；其他 → 单行字符串
            AwaitThird,  // 已见 ""：第三字符是 " → 多行字符串；否则 → 空字符串 ""
            Forward      // 形态已定：转发给持有的字符串层
        }

        private State state = State.Decision;
        private ILexerLayer? stringLayer = null;

        // 未闭合错误的用户可读描述（Lexer 冲刷后的栈检查用）
        public string UnterminatedDescription =>
            stringLayer is MultilineStringLexerLayer ? "multi-line string literal" : "string literal";

        public LexerLayerResult ParseChar(char currentChar, LexerLayerContext context)
        {
            switch (state)
            {
                case State.Decision:
                    if (currentChar == Notations.DOUBLE_QUOTATION_MARK)
                    {
                        state = State.AwaitThird;
                        return LexerLayerResult.Continue.Instance;
                    }
                    stringLayer = new StringLexerLayer();
                    state = State.Forward;
                    return Forward(currentChar, context);

                case State.AwaitThird:
                    if (currentChar == Notations.DOUBLE_QUOTATION_MARK)
                    {
                        // 多行开界 """：第三个引号消费掉，内容从下一字符开始
                        stringLayer = new MultilineStringLexerLayer();
                        state = State.Forward;
                        return LexerLayerResult.Continue.Instance;
                    }
                    // 只是空字符串 ""：当前字符回流给下层重新分发
                    context.PushToken(new StringToken(""), includesCurrentChar: false);
                    return new LexerLayerResult.PopLayer(shouldKeepChar: true);

                default:
                    return Forward(currentChar, context);
            }
        }

        // 转发给持有的字符串层；字符串层弹出时本层同步弹出
        private LexerLayerResult Forward(char currentChar, LexerLayerContext context)
        {
            var result = stringLayer!.ParseChar(currentChar, context);
            if (result is LexerLayerResult.PopLayer pop)
            {
                return new LexerLayerResult.PopLayer(pop.shouldKeepChar);
            }
            return result;
        }
    }

    public class BaseLexerLayer : ILexerLayer
    {
        public LexerLayerResult ParseChar(char currentChar, LexerLayerContext context)
        {
            if (Char.IsLetterOrDigit(currentChar) || (currentChar == Notations.UNDERSCORE))
            {

                return new LexerLayerResult.PushLayer(
                    layerToPush: new WordLexerLayer(),
                    shouldKeepChar: true
                );
            }
            else if (currentChar == Notations.SINGLE_QUOTATION_MARK)
            {
                // 字符字面量 '...'：委托 CharLexerLayer（开界 ' 预消费，SYNTAX §3.3）
                return new LexerLayerResult.PushLayer(
                    layerToPush: new CharLexerLayer(),
                    shouldKeepChar: false
                );
            }
            else if (currentChar == Notations.DOUBLE_QUOTATION_MARK)
            {
                // 引号家族（"..."、""、"""）：委托 QuoteLexerLayer 分流（首个 " 预消费）
                return new LexerLayerResult.PushLayer(
                    layerToPush: new QuoteLexerLayer(),
                    shouldKeepChar: false
                );
            }
            else if (currentChar == Notations.FORWARD_SLASH)
            {
                // 斜杠家族（/、/=、//、/*）：委托 SlashLexerLayer 分流（/ 预消费）
                return new LexerLayerResult.PushLayer(
                    layerToPush: new SlashLexerLayer(),
                    shouldKeepChar: false
                );
            }
            else if (Char.IsWhiteSpace(currentChar)) { 
                if(currentChar == '\n')
                {
                    context.PushToken(new LineBreakToken(),includesCurrentChar:true);
                    return LexerLayerResult.Continue.Instance;
                }
                else
                {
                    return LexerLayerResult.Continue.Instance;
                }
            }
            else
            {
                return new LexerLayerResult.PushLayer(
                        layerToPush: new NotationLexerLayer(),
                        shouldKeepChar: true
                    );
            }

        }
    }
}