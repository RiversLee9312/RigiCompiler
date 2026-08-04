using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LatteCompiler
{
    /// <summary>
    /// 引号分流层：单行字符串 "..."、空字符串 ""、多行字符串 """ 的统一入口。
    /// Base 层看到 " 即推入本层（首个 " 已预消费），本层按随后字符决定形态；
    /// 字符串形态转发给持有的字符串层实例（Delegate, don't implement，
    /// 与 SlashLexerLayer 同模式），字符串层弹出时本层一并弹出。
    /// </summary>
    public class QuoteLexerLayer : ILexerLayer
    {
        private enum State
        {
            Decision,    // 等待第二字符：" → 可能是多行；其他 → 单行字符串
            AwaitThird,  // 已见 ""：第三字符是 " → 多行字符串；否则 → 空字符串 ""
            Forward      // 形态已定：转发给持有的字符串层
        }

        private State state = State.Decision;
        private ILexerLayer? stringLayer = null;

        // 未闭合错误的用户可读描述（Lexer 冲刷后的栈检查用）
        public string UnterminatedDescription =>
            stringLayer is MultilineStringLexerLayer ? "multi-line string literal" : "string literal";

        public LexerLayerResult ParseChar(char currentChar, LexerLayerContext context)
        {
            switch (state)
            {
                case State.Decision:
                    if (currentChar == Notations.DOUBLE_QUOTATION_MARK)
                    {
                        state = State.AwaitThird;
                        return LexerLayerResult.Continue.Instance;
                    }
                    stringLayer = new StringLexerLayer();
                    state = State.Forward;
                    return Forward(currentChar, context);

                case State.AwaitThird:
                    if (currentChar == Notations.DOUBLE_QUOTATION_MARK)
                    {
                        // 多行开界 """：第三个引号消费掉，内容从下一字符开始
                        stringLayer = new MultilineStringLexerLayer();
                        state = State.Forward;
                        return LexerLayerResult.Continue.Instance;
                    }
                    // 只是空字符串 ""：当前字符回流给下层重新分发
                    context.PushToken(new StringToken(""), includesCurrentChar: false);
                    return new LexerLayerResult.PopLayer(shouldKeepChar: true);

                default:
                    return Forward(currentChar, context);
            }
        }

        // 转发给持有的字符串层；字符串层弹出时本层同步弹出
        private LexerLayerResult Forward(char currentChar, LexerLayerContext context)
        {
            var result = stringLayer!.ParseChar(currentChar, context);
            if (result is LexerLayerResult.PopLayer pop)
            {
                return new LexerLayerResult.PopLayer(pop.shouldKeepChar);
            }
            return result;
        }
    }
}
