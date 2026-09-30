using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RigiCompiler
{
    public class BaseLexerLayer : ILexerLayer
    {
        public LexerLayerResult ParseChar(char currentChar, LexerLayerContext context)
        {
            if (IdentifierCharacters.IsLetterOrDigit(currentChar) || (currentChar == Notations.UNDERSCORE))
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
