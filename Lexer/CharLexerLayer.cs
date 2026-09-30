using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RigiCompiler
{
    /// <summary>
    /// 字符字面量层（SYNTAX §3.3）：单引号内必须恰好一个字符或一个转义序列，
    /// 转义与字符串同一套（StringEscape）。空（''）、多字符（'ab'，收到第二个
    /// 内容字符即报错）、未知转义、换行未闭合（含 EOF 冲刷帧的虚拟换行）均为
    /// 词法错误；char 无插值概念。Base 层看到 ' 即推入本层（开界 ' 预消费）。
    /// char 是 32 位 Unicode 标量（STDLIB §4.3.1）：源码里补充平面字符以
    /// UTF-16 代理对到达，本层把合法代理对合成为单个标量值，孤立代理拒绝。
    /// </summary>
    public class CharLexerLayer : ILexerLayer
    {
        private enum State
        {
            AwaitContent,      // 等待内容字符（开界 ' 之后）
            EscapeSeen,        // 已见反斜杠，等待转义字符
            HighSurrogateSeen, // 已收代理对高半（0xD800–0xDBFF），等待低半
            ContentSeen        // 已收到恰好一个内容标量，等待闭界 '
        }

        private State state = State.AwaitContent;
        private uint value;
        private ushort highSurrogate;

        public LexerLayerResult ParseChar(char currentChar, LexerLayerContext context)
        {
            switch (state)
            {
                case State.AwaitContent:
                    // 空字符字面量 ''：编译错误
                    if (currentChar == Notations.SINGLE_QUOTATION_MARK)
                    {
                        throw context.RaiseError("Empty character literal");
                    }
                    if (currentChar == Notations.BACK_SLASH)
                    {
                        state = State.EscapeSeen;
                        return LexerLayerResult.Continue.Instance;
                    }
                    if (currentChar == '\n')
                    {
                        // 换行未闭合（真实换行或 EOF 冲刷帧的虚拟换行）
                        throw context.RaiseError("Unterminated character literal");
                    }
                    // 代理对高半：先暂存，等下一个码元合成完整标量
                    if (char.IsHighSurrogate(currentChar))
                    {
                        highSurrogate = currentChar;
                        state = State.HighSurrogateSeen;
                        return LexerLayerResult.Continue.Instance;
                    }
                    if (char.IsLowSurrogate(currentChar))
                    {
                        // 孤立低代理不是合法 Unicode 标量
                        throw context.RaiseError("Unpaired surrogate in character literal");
                    }
                    value = currentChar;
                    state = State.ContentSeen;
                    return LexerLayerResult.Continue.Instance;

                case State.EscapeSeen:
                    // 反斜杠后紧跟真实换行（或 EOF 虚拟换行）：不支持行接续
                    if (currentChar == '\n')
                    {
                        throw context.RaiseError("Unexpected line break after \\ in character literal");
                    }
                    if (StringEscape.TryProcess(currentChar, out char escaped))
                    {
                        value = escaped;
                        state = State.ContentSeen;
                        return LexerLayerResult.Continue.Instance;
                    }
                    throw context.RaiseError($"Unknown escape sequence:\\{currentChar}");

                case State.HighSurrogateSeen:
                    if (char.IsLowSurrogate(currentChar))
                    {
                        // 合法代理对：U+10000 + (高半-0xD800)<<10 + (低半-0xDC00)
                        value = 0x10000u
                            + (((uint)highSurrogate - 0xD800u) << 10)
                            + ((uint)currentChar - 0xDC00u);
                        state = State.ContentSeen;
                        return LexerLayerResult.Continue.Instance;
                    }
                    if (currentChar == '\n')
                    {
                        // 高半后 EOF 虚拟换行/真实换行：未闭合优先（与单字符同口径）
                        throw context.RaiseError("Unterminated character literal");
                    }
                    // 孤立高代理（后跟非低代理）不是合法 Unicode 标量
                    throw context.RaiseError("Unpaired surrogate in character literal");

                default: // State.ContentSeen
                    // 闭界 '：恰好一个标量，产出 token（闭界属于 token 范围）
                    if (currentChar == Notations.SINGLE_QUOTATION_MARK)
                    {
                        context.PushToken(new CharToken(value), includesCurrentChar: true);
                        return new LexerLayerResult.PopLayer(shouldKeepChar: false);
                    }
                    if (currentChar == '\n')
                    {
                        throw context.RaiseError("Unterminated character literal");
                    }
                    // 第二个内容字符：多字符字面量是编译错误
                    throw context.RaiseError("Character literal must contain exactly one character");
            }
        }
    }
}
