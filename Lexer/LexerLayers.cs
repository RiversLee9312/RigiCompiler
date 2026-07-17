using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static LatteCompiler.StringLexerLayer;

namespace LatteCompiler
{
    public class CommentBlockLexerLayer : ILexerLayer
    {
        private bool backSlashAppeared = false;
        private bool asterickAppeared = false;
        private CommentToken currentToken = new CommentToken("");
        public LexerLayerResult ParseChar(char currentChar, LexerLayerContext context)
        {
            bool shouldExit = false;
            string stringToPadFront = "";
            bool shouldSkipCurrentChar = false;
            switch (currentChar)
            {
                case Notations.ASTERISK:
                    if (backSlashAppeared)
                    {
                        backSlashAppeared = false;
                    }
                    else
                    {
                        if (asterickAppeared)
                        {
                            stringToPadFront += Notations.ASTERISK;
                        }
                        asterickAppeared = true;
                        shouldSkipCurrentChar = true;
                    }
                    break;
                case Notations.FORWARD_SLASH:
                    if (asterickAppeared)
                    {
                        shouldExit = true;
                    }
                    else
                    {
                        backSlashAppeared = false;
                    }
                    break;
                case Notations.BACK_SLASH:
                    if (!backSlashAppeared)
                    {
                        asterickAppeared = false;
                        backSlashAppeared = true;
                        shouldSkipCurrentChar = true;
                    }
                    else
                    {
                        stringToPadFront += Notations.BACK_SLASH;
                        backSlashAppeared = false;
                        asterickAppeared = false;
                    }
                    break;
                default:
                    asterickAppeared = false;
                    backSlashAppeared = false;
                    break;
            }
            if (shouldExit)
            {
                context.PushToken(currentToken,includesCurrentChar:true);
                currentToken = new CommentToken("");
                asterickAppeared = backSlashAppeared = false;
                return new LexerLayerResult.PopLayer(shouldKeepChar:false);
            }
            else
            {
                currentToken.Content += stringToPadFront;
                if (!shouldSkipCurrentChar)
                {
                    currentToken.Content += currentChar;
                }
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
                context.PushToken(currentToken, includesCurrentChar: true);
                currentToken = new CommentToken("");
                return new LexerLayerResult.PopLayer(shouldKeepChar:false);
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
    /// Tokenizes a string.
    /// </summary>
    public class StringLexerLayer : ILexerLayer
    {
        public enum HeadType
        {
            DoubleQuotationMark,
            SingleQuotationMark
        }
        public StringLexerLayer(HeadType headType)
        {
            switch (headType)
            {
                case HeadType.DoubleQuotationMark:
                    head = Notations.DOUBLE_QUOTATION_MARK;
                    break;
                case HeadType.SingleQuotationMark:
                    head = Notations.SINGLE_QUOTATION_MARK;
                    break;
            }
        }
        private readonly char head;
        private bool backSlashAppeared = false;
        private StringToken currentToken = new StringToken("");
        public LexerLayerResult ParseChar(char currentChar, LexerLayerContext context)
        {

            if (backSlashAppeared)
            {
                backSlashAppeared = false;
                switch (currentChar)
                {
                    case Notations.DOLLAR_SYMBOL:
                        currentToken.Content += Notations.DOLLAR_SYMBOL;
                        break;
                    case 'a':
                        currentToken.Content += '\a';
                        break;
                    case 'b':
                        currentToken.Content += '\b';
                        break;
                    case 't':
                        currentToken.Content += '\t';
                        break;
                    case 'n':
                        currentToken.Content += '\n';
                        break;
                    case 'v':
                        currentToken.Content += '\v';
                        break;
                    case 'f':
                        currentToken.Content += '\f';
                        break;
                    case 'r':
                        currentToken.Content += '\r';
                        break;
                    case Notations.SINGLE_QUOTATION_MARK:
                        currentToken.Content += Notations.SINGLE_QUOTATION_MARK;
                        break;
                    case Notations.DOUBLE_QUOTATION_MARK:
                        currentToken.Content += Notations.DOUBLE_QUOTATION_MARK;
                        break;
                    case Notations.BACK_SLASH:
                        currentToken.Content += Notations.BACK_SLASH;
                        break;
                    default:
                        context.RaiseError($"Unknown escape sequence:\\{currentChar}");
                        break;
                }
                return LexerLayerResult.Continue.Instance;
            }
            else
            {
                if (currentChar == head)
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
                    currentToken.Content += currentChar;
                    return LexerLayerResult.Continue.Instance;
                }
            }
        }
    }
    public class NotationLexerLayer : ILexerLayer
    {
        private int notationCharCount = 0;
        private NotationToken notationToken = new NotationToken("");
        public LexerLayerResult ParseChar(char currentChar, LexerLayerContext context)
        {
            notationCharCount++;
            notationToken.Content += currentChar;
            if (notationCharCount > 2)
            {
                throw context.RaiseError("Illegal notation:" + notationToken.Content);
            }
            else
            {
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

    public class BaseLexerLayer : ILexerLayer
    {
        private bool forwardSlashAppeared = false;
        public LexerLayerResult ParseChar(char currentChar, LexerLayerContext context)
        {
            if (forwardSlashAppeared&&((currentChar!=Notations.ASTERISK)&&(currentChar!=Notations.FORWARD_SLASH)))
            {
                context.RaiseError($"Incorrect comment block or line start:/{currentChar} ");
            }

            if (Char.IsLetterOrDigit(currentChar) || (currentChar == Notations.UNDERSCORE))
            {

                return new LexerLayerResult.PushLayer(
                    layerToPush: new WordLexerLayer(),
                    shouldKeepChar: true
                );
            }
            else if (
                (currentChar == Notations.SINGLE_QUOTATION_MARK) || (currentChar == Notations.DOUBLE_QUOTATION_MARK)
                )
            {
                var headType = currentChar == Notations.SINGLE_QUOTATION_MARK ? StringLexerLayer.HeadType.SingleQuotationMark : StringLexerLayer.HeadType.DoubleQuotationMark;
                return new LexerLayerResult.PushLayer(
                    layerToPush: new StringLexerLayer(headType),
                    shouldKeepChar: false
                );
            }
            else if (currentChar == Notations.FORWARD_SLASH)
            {
                if (forwardSlashAppeared)
                {
                    forwardSlashAppeared = false;
                    return new LexerLayerResult.PushLayer(
                        layerToPush: new CommentLineLexerLayer(),
                        shouldKeepChar: false
                    );
                }
                else
                {
                    forwardSlashAppeared = true;
                    return LexerLayerResult.Continue.Instance;
                }
            }
            else if (currentChar == Notations.ASTERISK)
            {
                if (forwardSlashAppeared)
                {
                    forwardSlashAppeared = false;
                    return new LexerLayerResult.PushLayer(
                        layerToPush: new CommentBlockLexerLayer(),
                        shouldKeepChar: false
                    );
                }
                else
                {
                    return new LexerLayerResult.PushLayer(
                        layerToPush: new NotationLexerLayer(),
                        shouldKeepChar: true
                    );
                }
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