using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RigiCompiler
{
    public class PathParserLayer : IParserLayer, ISpanReceiver
    {
        private class SymbolLayer : IParserLayer
        {
            private Symbol targetSymbol;
            private bool lineBreakSensitive;
            // 是否允许把连续点的剩余部分交还上层（类型引用/参数列表语境的
            // 可变参数标记 ...，M31）；表达式符号引用语境为 false（foo..bar 报错）
            private bool allowVariadicDots;
            public SymbolLayer(Symbol target, bool lineBreakSensitive, bool allowVariadicDots)
            {
                targetSymbol = target;
                this.lineBreakSensitive = lineBreakSensitive;
                this.allowVariadicDots = allowVariadicDots;
            }
            private bool isParsingGeneric = false;
            // 已看到 \ ，正在期待 < （泛型列表开启符 \< ，见 SYNTAX.md §3.6）
            private bool backslashSeen = false;
            private SymbolElement currentElement = new();

            // 点已消费、正在等新元素名（elements 非空但当前元素还没名字）：
            // `foo.` 的悬空腹；用于换行/EOF 时识别不完整结构（M31）
            private bool AwaitingElementName =>
                targetSymbol.elements.Count > 0 && string.IsNullOrEmpty(currentElement.name);

            public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
            {
                // EOF：结构完整则上交；尾点 / 泛型未闭合 / \ 悬空腹为不完整结构（M31 修复：
                // 此前 `foo.`、`List\<i32` 遇 EOF 被静默吞并为 foo / List）
                if (currentToken is EndOfFileToken)
                {
                    if (AwaitingElementName || isParsingGeneric || backslashSeen)
                    {
                        throw context.RaiseError("Unexpected end of file");
                    }
                    return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                }

                // \ 之后必须紧跟 < ，否则不是合法的泛型开启
                if (backslashSeen && !(currentToken is NotationToken bn && bn.Content == "<"))
                {
                    // 闭括号写成 \> 是旧式残留：给出针对性诊断，避免被读成「少了 <」
                    if (currentToken is NotationToken close && close.Content == ">")
                    {
                        throw context.RaiseError(
                            "Expected '<' after '\\' to open generic list (generic lists close with '>', not '\\>')");
                    }
                    throw context.RaiseError($"Expected '<' after '\\' in generic list, got: {currentToken}");
                }
                switch (currentToken)
                {
                    case WordToken wt:
                        if (!string.IsNullOrEmpty(currentElement.name))
                        {
                            if (isParsingGeneric) {
                                var symbol = new Symbol();
                                currentElement.generics.Add(symbol);
                                return new ParserLayerResult.PushLayer(
                                        new SymbolLayer(symbol, lineBreakSensitive, allowVariadicDots),
                                        TokenDisposition.Replay
                                    );
                            }
                            else { 
                                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                            } 
                        }
                        else
                        {
                            // 元素名不能是数字词（M31：List\<123>、a.123.b 此前被接受；只查首字符）
                            if (!Keywords.IsIdentifierStart(wt.Content))
                            {
                                throw context.RaiseError(
                                    $"Expected identifier in symbol path, got: {wt.Content}");
                            }
                            currentElement.name = wt.Content;
                            targetSymbol.elements.Add(currentElement);
                            return ParserLayerResult.Continue.Instance;
                        }
                    case NotationToken nt:
                        if (nt.Content.Length > 1)
                        {
                            if (isParsingGeneric)
                            {
                                throw context.RaiseError("Unexpected token in symbol:" + currentToken);
                            }
                            return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                        }
                        switch (nt.Content[0]) {
                            case Notations.DOT:
                                if (string.IsNullOrEmpty(currentElement.name))
                                {
                                    // 连续第二个 .：类型引用/参数列表语境下是可变参数
                                    // 标记 ... 的剩余部分，交还给上层处理；
                                    // 其余语境（表达式符号引用等）是非法双点，报错
                                    // （M31 修复：foo..bar 此前被静默解析为 foo.bar）
                                    if (!allowVariadicDots)
                                    {
                                        throw context.RaiseError(
                                            "Unexpected '.' in symbol path: expected element name");
                                    }
                                    return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                                }else if (isParsingGeneric)
                                {
                                    throw context.RaiseError("Unexpected token in symbol:" + currentToken);
                                }
                                currentElement = new SymbolElement();
                                return ParserLayerResult.Continue.Instance;
                            case Notations.L_ANGLE:
                                if (string.IsNullOrEmpty(currentElement.name))
                                {
                                    throw context.RaiseError("Unexpected token in symbol:" + currentToken);
                                }else if (isParsingGeneric)
                                {
                                    throw context.RaiseError("Unexpected token in symbol:" + currentToken);
                                }
                                else if (!backslashSeen)
                                {
                                    // 新语法（SYNTAX.md §3.6）：泛型列表必须以 \< 开启；
                                    // 裸 < 是小于号等运算符，交还给上层处理
                                    return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                                }
                                backslashSeen = false;
                                isParsingGeneric = true;
                                return ParserLayerResult.Continue.Instance;
                            case Notations.BACK_SLASH:
                                // \< 是泛型列表的开启符：\ 必须跟在符号名之后、且不在泛型模式内
                                if (string.IsNullOrEmpty(currentElement.name) || isParsingGeneric)
                                {
                                    throw context.RaiseError("Unexpected token in symbol:" + currentToken);
                                }
                                backslashSeen = true;
                                return ParserLayerResult.Continue.Instance;
                            case Notations.R_ANGLE:
                                if (isParsingGeneric)
                                {
                                    // 本层确实在解析泛型实参：逗号后的悬空实参不得被 '>' 吞掉
                                    if (currentElement.generics.Count > 0)
                                    {
                                        var last = currentElement.generics[currentElement.generics.Count - 1];
                                        if (last.elements.Count == 0 ||
                                            string.IsNullOrEmpty(last.elements[0].name))
                                        {
                                            throw context.RaiseError(
                                                "Expected type argument before '>' in generic list");
                                        }
                                    }
                                    isParsingGeneric = false;
                                    return ParserLayerResult.Continue.Instance;
                                }
                                else
                                {
                                    // 本层未开启 \< ：把 '>' 交还父层（嵌套闭合 / 多余闭合）
                                    return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                                }
                            case Notations.COMMA:
                                if (isParsingGeneric)
                                {
                                    var symbol = new Symbol();
                                    currentElement.generics.Add(symbol);
                                    return new ParserLayerResult.PushLayer(
                                            new SymbolLayer(symbol, lineBreakSensitive, allowVariadicDots),
                                            TokenDisposition.Consume
                                        );
                                }
                                else
                                {
                                    return new ParserLayerResult.PopLayer(
                                            TokenDisposition.Replay
                                        );
                                }
                            default:
                                return new ParserLayerResult.PopLayer(
                                            TokenDisposition.Replay
                                        );
                        }
                    case LineBreakToken:
                        if (!lineBreakSensitive)
                        {
                            return ParserLayerResult.Continue.Instance;
                        }
                        else
                        {
                            // 换行结束符号路径：尾点空腹 / 泛型未闭合为不完整结构（M31 修复：
                            // 此前 `foo.` + 换行被静默吞并为 foo）
                            if (AwaitingElementName)
                            {
                                throw context.RaiseError(
                                    "Unexpected line break in symbol path: expected element name after '.'");
                            }
                            if (isParsingGeneric)
                            {
                                throw context.RaiseError(
                                    "Unexpected line break in symbol path: generic list not closed");
                            }
                            return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                        }
                            default:
                        return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                }
            }
        }
        private readonly SymbolASTNode self;  // 施工目标节点（M28 用于回填 Span）
        private bool lineBreakSensitive;
        private bool allowVariadicDots;
        private bool symbolParsed = false;

        public PathParserLayer(SymbolASTNode self, bool lineBreakSensitive, bool allowVariadicDots = false)
        {
            this.self = self;
            this.lineBreakSensitive = lineBreakSensitive;
            this.allowVariadicDots = allowVariadicDots;
        }

        // 层弹出时回填施工目标的源码范围（M28）
        public void ReceiveSpan(CharRange span) => self.Span ??= span;

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            // 符号路径整体委托 SymbolLayer 原地填充 self.symbol（Delegate, don't implement）；
            // SymbolLayer 弹出后本层使命完成，随下一个 token 弹栈上交
            if (symbolParsed)
            {
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }
            else
            {
                symbolParsed = true;
                return new ParserLayerResult.PushLayer(
                        new SymbolLayer(self.symbol, lineBreakSensitive, allowVariadicDots),
                        TokenDisposition.Replay);
            }
        }
    }
}
