using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RigiCompiler
{
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
                    currentToken.Append(value);
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
                    currentToken.Append(Notations.DOLLAR_SYMBOL);
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
                        currentToken.Append(currentChar);
                    }
                    return LexerLayerResult.Continue.Instance;
                }
            }
        }
    }
}
