using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LatteCompiler
{
    public abstract record LexerLayerResult
    {
        // 使用 sealed record 来防止进一步继承
        public sealed record PopLayer(bool shouldKeepChar) : LexerLayerResult;

        public sealed record PushLayer(ILexerLayer layerToPush, bool shouldKeepChar) : LexerLayerResult;

        // 使用单例模式的 Continue 记录
        public sealed record Continue : LexerLayerResult
        {
            private Continue() { }
            public static readonly LexerLayerResult Instance = new Continue();
        }
    }
    public struct CharPosition
    {
        public string sourceName = "";
        public int column = 0;
        public long line = 0;
        public long offset = 0;

        public CharPosition()
        {
        }
    }
    public abstract class LexerLayerContext
    {
        public abstract void PushToken(Token token, bool includesCurrentChar);
        public abstract CharPosition GetPosition();
        [DoesNotReturn]
        public abstract Exception RaiseError(string message);
        public abstract void LogWarning(string message);
        public abstract List<Token> GetTokens();
        public abstract void Log(string message);
    }
    public interface ILexerLayer
    {
        public LexerLayerResult ParseChar(char currentChar,LexerLayerContext context);
    }
    public class Lexer
    {
        private class ContextImpl : LexerLayerContext
        {
            private bool updatePosition = true;
            private CharPosition _position = new();
            public CharPosition position { 
                get { return _position; }
                set {
                    lastPosition = _position;
                    _position = value;
                    if (updatePosition) { 
                        tokenHeadPosition = _position;
                        updatePosition = false;
                    }
                }
            }
            public List<Token> tokens = new List<Token>();
            private CharPosition tokenHeadPosition = new CharPosition();
            private CharPosition lastPosition = new CharPosition();
            public override CharPosition GetPosition()
            {
                return position;
            }

            public override List<Token> GetTokens()
            {
                return tokens;
            }

            public override void Log(string message)
            {
                Console.WriteLine($"VERBOSE [{position.sourceName}][Line {position.line} Col {position.column}]{message}");
            }

            public override void LogWarning(string message)
            {
                Console.WriteLine($"WARNING [{position.sourceName}][Line {position.line} Col {position.column}]{message}");
            }

            public override void PushToken(Token token,bool includesCurrentChar)
            {
                token.CharRange.Start = tokenHeadPosition;
                token.CharRange.End = includesCurrentChar?position:lastPosition;
                updatePosition = true;
                tokens.Add(token);
            }
            [DoesNotReturn]
            public override Exception RaiseError(string message) => throw new LexerException($"ERROR [{position.sourceName}][Line {position.line} Col {position.column}]{message}");

        }

        public async Task<List<Token>> Tokenize(
            StreamReader reader,
            string sourceName
            )
        {
            //var result = new List<Token>();
            var content = await reader.ReadToEndAsync();
            content.ReplaceLineEndings("\n");
            content += "\t";
            var lexerLayers = new Stack<ILexerLayer>();
            lexerLayers.Push(new BaseLexerLayer());
            var offset = 0;
            var currentChar = content[0];
            var keepChar = false;
            var context = new ContextImpl();
            context.position = new CharPosition() {
                offset = offset,line = 1,column = 0,sourceName = sourceName
            };

            while (offset < content.Length) {
                ILexerLayer? currentLayer;
                if (lexerLayers.TryPeek(out currentLayer))
                {
                    var newPosition = context.position;
                    if (!keepChar)
                    {
                        currentChar = content[offset];
                        context.Log($"Current char:{currentChar}");
                        if (currentChar == '\n')
                        {
                            newPosition.line++;
                            newPosition.column = 1;
                        }
                        else
                        {
                            newPosition.column++;
                        }
                    }
                    else
                    {
                        keepChar = false;
                    }
                    context.position = newPosition;

                    var layerResult = currentLayer.ParseChar(
                        currentChar, context
                        );
                    switch (layerResult)
                    {
                        case LexerLayerResult.Continue:
                            keepChar = false;
                            break;
                        case LexerLayerResult.PopLayer r:
                            var popped = lexerLayers.Pop();
                            keepChar = r.shouldKeepChar;
                            context.Log("Popped layer:"+popped.GetType().Name);
                            break;
                        case LexerLayerResult.PushLayer r:
                            lexerLayers.Push(r.layerToPush);
                            keepChar = r.shouldKeepChar;
                            context.Log("Pushed layer:" + r.layerToPush.GetType().Name);
                            break;
                    }
                    if (!keepChar)
                    {
                        offset++;
                    }
                }
                else
                {
                    context.RaiseError("Unexpected lexer end");
                }
            }
            return context.GetTokens();
        }

        // 便捷方法：从字符串直接分词（用于测试）
        public List<Token> Tokenize(string code, string sourceName = "<inline>")
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(code)))
            using (var reader = new StreamReader(stream))
            {
                var task = Tokenize(reader, sourceName);
                task.Wait();
                return task.Result;
            }
        }
    }


}
