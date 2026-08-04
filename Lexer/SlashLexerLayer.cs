using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LatteCompiler
{
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
}
