using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RigiCompiler
{
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
                if (segments.Count == 1)
                {
                    // 首段 span 首修正（须在 PushToken 之后做：token 头由驱动按
                    // tokenHeadPosition 覆盖，先改会被整个冲掉）：开界 """ 3 字符
                    // 与强制换行不属于段内容——内容从开界行下一行行首开始
                    var segmentStart = segments[^1].Token.CharRange.Start;
                    segments[^1].Token.CharRange.Start = new CharPosition
                    {
                        line = segmentStart.line + 1,
                        column = 1,
                        offset = segmentStart.offset + 4,
                    };
                }
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
                // 挂起的引号先归属内容再收反斜杠（与其他分支一致）——
                // 否则转义对会插到引号串之前，解码内容顺序错乱
                FlushQuoteRun();
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
        // 规则给——首段因开界占位需首修正，在插值引导处 PushToken 之后做），
        // 加入回填队列；空段也产出（解码后判空归 Parser 统一跳过）。
        // 仅首段的段首行从行首开始（后续段首行是 } 的行内残余或空行，均不剥）
        private void EmitSegment(LexerLayerContext context, bool includesCurrentChar)
        {
            var raw = string.Join("\n", lines);
            var token = new StringToken(raw);
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
}
