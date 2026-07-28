using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
        public int column = 0;
        public long line = 0;
        // 0 起始的字符索引（M28 修复：此前从未增长，恒为 0）
        public long offset = 0;

        public CharPosition()
        {
        }
    }
    public abstract class LexerLayerContext
    {
        public abstract void PushToken(Token token, bool includesCurrentChar);
        [DoesNotReturn]
        public abstract Exception RaiseError(string message);
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
            // 源名唯一来源：CharRange.sourceName（M28 起 CharPosition 不再携带）
            public string sourceName = "";
            // 主循环正在处理的字符（读字符时设置；keepChar 重放时保持同一字符）
            internal char currentChar;
            public CharPosition position {
                get { return _position; }
                set { _position = value; }
            }

            // 由主循环在每个字符处调用：上一个 token 推送后，首个非空白字符
            // 成为下一个 token 的头（M28 修复：此前空白字符也会被记为 token 头，
            // 缩进行 token 的 Start 总是落在前导空格上）。
            // 换行是例外——它本身是 LineBreakToken，必须能当 token 头
            public void CaptureTokenHead(char currentChar)
            {
                if (updatePosition && (!Char.IsWhiteSpace(currentChar) || currentChar == '\n'))
                {
                    tokenHeadPosition = position;
                    updatePosition = false;
                }
            }
            public List<Token> tokens = new List<Token>();
            private CharPosition tokenHeadPosition = new CharPosition();

            // 初始化位置（M28）：不走 position setter——tokenHeadPosition
            // 留给首个真实字符的位置，避免把 col 0 冻结成首个 token 的 Start
            public void InitPosition(CharPosition p)
            {
                _position = p;
            }

            public override List<Token> GetTokens()
            {
                return tokens;
            }

            // 日志统一走 Logger（禁止直接 Console.WriteLine）；
            // source 标识子系统便于 grep，位置信息保留在 message 前缀里
            public override void Log(string message)
            {
                Logger.Verbose("Lexer", $"[{sourceName}][Line {position.line} Col {position.column}]{message}");
            }

            // Token 的 CharRange 是左闭右开区间 [Start, End)（M31 起）：
            // Start 指向首个字符，End 指向最后一个字符的下一位置
            public override void PushToken(Token token,bool includesCurrentChar)
            {
                token.CharRange.Start = tokenHeadPosition;
                token.CharRange.End = includesCurrentChar ? Advance(position) : position;
                // M28 修复：普通 token 的 sourceName 此前从未设置（仅 EOF 有）
                token.CharRange.sourceName = sourceName;
                // 预设下一个 token 的头为当前字符位置：同一字符连续 PushToken
                // （如块注释按行分段后再推 LineBreakToken）时第二个 token 头正确；
                // 之后 CaptureTokenHead 遇到非空白字符会再覆盖
                tokenHeadPosition = position;
                updatePosition = true;
                tokens.Add(token);
            }

            // 当前字符位置的下一位置（开区间 End）：换行 → 下一行行首；
            // 其余 → 同行下一列；offset 恒 +1（与主循环的位置推进一致）
            private CharPosition Advance(CharPosition p)
            {
                var next = p;
                if (currentChar == '\n')
                {
                    next.line++;
                    next.column = 1;
                }
                else
                {
                    next.column++;
                }
                next.offset++;
                return next;
            }
            [DoesNotReturn]
            public override Exception RaiseError(string message) => throw new LexerException($"ERROR [{sourceName}][Line {position.line} Col {position.column}]{message}");

        }

        public async Task<List<Token>> Tokenize(
            StreamReader reader,
            string sourceName
            )
        {
            var content = await reader.ReadToEndAsync();
            // 行尾归一（M31）：只把 \r\n / \r 归一为 \n；不用 ReplaceLineEndings——
            // 它会把 \f/\x85/\u2028/\u2029 一并替换，误伤字符串字面量内的原始字符
            content = content.Replace("\r\n", "\n").Replace("\r", "\n");
            var lexerLayers = new Stack<ILexerLayer>();
            lexerLayers.Push(new BaseLexerLayer());
            var offset = 0;
            var currentChar = '\0';  // 初值不会被使用（keepChar 初始为 false，循环内总会先读字符）
            var keepChar = false;
            var context = new ContextImpl();
            context.sourceName = sourceName;
            // 初始位置不走 setter：tokenHeadPosition 留给首个真实字符的位置
            // （M28 修复：此前经 setter 初始化，把 col 0 冻结成了首个 token 的 Start）
            context.InitPosition(new CharPosition() {
                offset = offset,line = 1,column = 0
            });

            // 换行挂起：换行符本身算当前行的最后一列，下一行首个字符从 col 1 开始
            // （M28 修复：此前换行即切到下一行 col 1，导致第二行起所有列号 +1）
            var newLinePending = false;
            while (offset < content.Length) {
                ILexerLayer? currentLayer;
                if (lexerLayers.TryPeek(out currentLayer))
                {
                    var newPosition = context.position;
                    if (!keepChar)
                    {
                        currentChar = content[offset];
                        context.currentChar = currentChar;
                        context.Log($"Current char:{currentChar}");
                        if (newLinePending)
                        {
                            newPosition.line++;
                            newPosition.column = 1;
                            newLinePending = false;
                        }
                        else
                        {
                            newPosition.column++;
                        }
                        if (currentChar == '\n')
                        {
                            newLinePending = true;
                        }
                        // offset 同步进位置（M28 修复：此前 CharPosition.offset 恒为 0）
                        newPosition.offset = offset;
                    }
                    else
                    {
                        keepChar = false;
                    }
                    context.position = newPosition;
                    // 捕获下一个 token 的头（仅当上一个 token 已推送且当前字符非空白）
                    context.CaptureTokenHead(currentChar);

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
            // 虚拟换行也占一个位置（最后一个字符之后）：冲刷出的 token 才能以
            // 最近真实字符为 End——否则 includesCurrentChar:false 的 End 会回退一个字符
            // （M28 修复：EOF 处 Word/Slash 层 token 的 End 少算一个字符、换行后甚至倒置）
            var flushPosition = context.position;
            if (newLinePending)
            {
                flushPosition.line++;
                flushPosition.column = 1;
                newLinePending = false;
            }
            else
            {
                flushPosition.column++;
            }
            flushPosition.offset = offset;
            context.position = flushPosition;
            context.currentChar = '\n';  // 冲刷帧字符是虚拟换行（Advance 按换行推进）
            FlushLayers(lexerLayers, context);
            // 冲刷后栈必须收敛为 Base 层：未闭合的字符串/块注释即词法错误
            if (lexerLayers.Count != 1)
            {
                // 错误信息面向用户：不暴露内部层类名
                var what = lexerLayers.Peek() switch
                {
                    QuoteLexerLayer q => $"Unterminated {q.UnterminatedDescription}",
                    SlashLexerLayer => "Unterminated block comment",
                    // 防御性 case：CharLexerLayer 对任何换行（含冲刷帧虚拟换行）
                    // 自行报错，正常路径不会滞留到本检查
                    CharLexerLayer => "Unterminated character literal",
                    var top => $"Unterminated {top.GetType().Name}"
                };
                context.RaiseError(what);
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
