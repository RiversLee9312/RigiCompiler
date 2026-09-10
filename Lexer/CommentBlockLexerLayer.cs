using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RigiCompiler
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
                        currentToken.Append(Notations.ASTERISK);
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
                    currentToken.Append(currentChar);
                    return LexerLayerResult.Continue.Instance;
                case '\n':
                    // 挂起的 * 归入内容；注释段推送（不含换行）；换行本身以
                    // LineBreakToken 入流；新段继续收注释（层不弹出）
                    if (asteriskAppeared)
                    {
                        currentToken.Append(Notations.ASTERISK);
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
                        currentToken.Append(Notations.ASTERISK);
                        asteriskAppeared = false;
                    }
                    currentToken.Append(currentChar);
                    return LexerLayerResult.Continue.Instance;
            }
        }
    }
}
