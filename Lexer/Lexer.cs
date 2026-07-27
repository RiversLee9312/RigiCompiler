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

            // 日志统一走 Logger（禁止直接 Console.WriteLine）；
            // source 标识子系统便于 grep，位置信息保留在 message 前缀里
            public override void Log(string message)
            {
                Logger.Verbose("Lexer", $"[{position.sourceName}][Line {position.line} Col {position.column}]{message}");
            }

            public override void LogWarning(string message)
            {
                Logger.Warning("Lexer", $"[{position.sourceName}][Line {position.line} Col {position.column}]{message}");
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
            var lexerLayers = new Stack<ILexerLayer>();
            lexerLayers.Push(new BaseLexerLayer());
            var offset = 0;
            var currentChar = '\0';  // 初值不会被使用（keepChar 初始为 false，循环内总会先读字符）
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
            // 冲刷帧：一次虚拟换行驱动各层冲刷手中 token 并弹栈（不产生任何 token）
            FlushLayers(lexerLayers, context);
            // 冲刷后栈必须收敛为 Base 层：未闭合的字符串/块注释即词法错误
            if (lexerLayers.Count != 1)
            {
                context.RaiseError(
                    $"Unterminated {lexerLayers.Peek().GetType().Name}");
            }
            // EOF 正式 token：由 Lexer 在输出末尾追加（Parser 不再自行追加）；
            // CharRange 为零长度范围，位于源文件末尾
            var eof = new EndOfFileToken();
            eof.CharRange = new CharRange
            {
                Start = context.position,
                End = context.position,
                sourceName = sourceName
            };
            context.GetTokens().Add(eof);
            return context.GetTokens();
        }

        // 冲刷帧（M25）：输入结束时向栈顶发送一次虚拟换行，驱动 Word/Notation/
        // 行注释/Slash 层冲刷手中 token 并弹栈；层回流（keepChar）的换行直接丢弃，
        // 不交给 Base 层——虚拟换行不产生任何 token。
        // 字符串层遇换行自行报错；块注释层收下换行不弹出（栈不收敛 → 调用方栈检查报错）。
        private static void FlushLayers(Stack<ILexerLayer> lexerLayers, LexerLayerContext context)
        {
            while (lexerLayers.Count > 1 && lexerLayers.TryPeek(out var layer))
            {
                var result = layer.ParseChar('\n', context);
                switch (result)
                {
                    case LexerLayerResult.PopLayer:
                        lexerLayers.Pop();
                        break;
                    case LexerLayerResult.PushLayer push:
                        lexerLayers.Push(push.layerToPush);
                        break;
                    case LexerLayerResult.Continue:
                        // 层收下虚拟换行但不弹出（块注释/字符串）——无法收敛，交给栈检查
                        return;
                }
            }
        }

        // 便捷方法：从字符串直接分词（用于测试）
        public List<Token> Tokenize(string code, string sourceName = "<inline>")
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(code)))
            using (var reader = new StreamReader(stream))
            {
                // GetAwaiter().GetResult() 不包 AggregateException：
                // 词法错误以原始 LexerException 抛出
                return Tokenize(reader, sourceName).GetAwaiter().GetResult();
            }
        }
    }


}
