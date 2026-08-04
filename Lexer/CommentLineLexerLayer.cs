using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LatteCompiler
{
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
}
