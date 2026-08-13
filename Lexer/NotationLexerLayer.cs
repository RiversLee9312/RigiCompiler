using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RigiCompiler
{
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
}
