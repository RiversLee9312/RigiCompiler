using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RigiCompiler
{
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
}
