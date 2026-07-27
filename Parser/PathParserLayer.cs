using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LatteCompiler
{
    public class PathParserLayer : IParserLayer, ISpanReceiver
    {
        private class SymbolLayer : IParserLayer
        {
            private Symbol targetSymbol;
            private bool lineBreakSensitive;
            public SymbolLayer(Symbol target, bool lineBreakSensitive)
            {
                targetSymbol = target;
                this.lineBreakSensitive = lineBreakSensitive;
            }
            private bool isParsingGeneric = false;
            // 已看到 \ ，正在期待 < （泛型列表开启符 \< ，见 SYNTAX.md §3.6）
            private bool backslashSeen = false;
            private SymbolElement currentElement = new();
            public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
            {
                // \ 之后必须紧跟 < ，否则不是合法的泛型开启
                if (backslashSeen && !(currentToken is NotationToken bn && bn.Content == "<"))
                {
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
                                        new SymbolLayer(symbol, lineBreakSensitive),
                                        TokenDisposition.Replay
                                    );
                            }
                            else { 
                                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                            } 
                        }
                        else
                        {
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
                                    // 连续第二个 . （如 TArgs... 的可变参数标记）：
                                    // 本层无法判定其含义，交还给上层处理
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
                                    isParsingGeneric = false;
                                    return ParserLayerResult.Continue.Instance;
                                }
                                else
                                {
                                    return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                                }
                            case Notations.COMMA:
                                if (isParsingGeneric)
                                {
                                    var symbol = new Symbol();
                                    currentElement.generics.Add(symbol);
                                    return new ParserLayerResult.PushLayer(
                                            new SymbolLayer(symbol, lineBreakSensitive),
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
                            return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                        }
                            default:
                        return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                }
            }
        }
        public enum PathType
        {
            ValuePath,
            SymbolPath
        }
        private PathType pathType;
        private readonly ASTNode self;  // 施工目标节点（保留构造函数的 self 引用，M28 用于回填 Span）
        private SymbolASTNode? symbolNode;
        private AcquisitionExpressionASTNode? acquisitionNode;
        private bool lineBreakSensitive;
        public PathParserLayer(PathType pathType,ASTNode self,bool lineBreakSensitive)
        {
            this.self = self;
            this.pathType = pathType;
            this.lineBreakSensitive = lineBreakSensitive;
            switch (pathType)
            {
                case PathType.ValuePath:
                    if (self is not AcquisitionExpressionASTNode)
                    {
                        throw new ArgumentException(
                            $"Wrong type of self node when creating {nameof(PathParserLayer)}:{self}");
                    }
                    acquisitionNode = self as AcquisitionExpressionASTNode;
                    symbolNode = null;
                    break;
                case PathType.SymbolPath:
                    if(self is not SymbolASTNode)
                    {
                        throw new ArgumentException(
                            $"Wrong type of self node when creating {nameof(PathParserLayer)}:{self}");
                    }
                    symbolNode = self as SymbolASTNode;
                    acquisitionNode = null;
                    break;
            }
        }

        // 层弹出时回填施工目标的源码范围（M28）
        public void ReceiveSpan(CharRange span) => self.Span ??= span;

        private bool symbolParsed = false;
        private enum AcqExprParseState
        {
            SourceNotStarted,
            SourceNotFinished,
            WrapperNotFinished
        }
        private AcqExprParseState acqExprState = AcqExprParseState.SourceNotStarted;
        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            if(pathType == PathType.SymbolPath)
            {
                if (symbolNode == null) {
                    throw context.RaiseError("Invalid parserLayer state,symbolNode is null");
                }
                if (symbolParsed)
                {
                    return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                }
                else
                {
                    symbolParsed = true;
                    return new ParserLayerResult.PushLayer(
                            new SymbolLayer(symbolNode.symbol,lineBreakSensitive),
                            TokenDisposition.Replay);
                }
            }
            else
            {
                if (acquisitionNode == null)
                {
                    throw context.RaiseError("Invalid parserLayer state,symbolNode is null");
                }
                switch (acqExprState)
                {
                    case AcqExprParseState.SourceNotStarted:
                        acquisitionNode.sourceSymbol = new(acquisitionNode);
                        acqExprState = AcqExprParseState.SourceNotFinished;
                        return new ParserLayerResult.PushLayer(
                                new SymbolLayer(acquisitionNode.sourceSymbol.symbol, lineBreakSensitive),
                                TokenDisposition.Replay);
                    case AcqExprParseState.SourceNotFinished:
                        switch (currentToken)
                        {
                            case NotationToken nt:
                                if(nt.Content == Notations.COLON.ToString())
                                {
                                    acqExprState = AcqExprParseState.WrapperNotFinished;
                                    var symbolNode = new SymbolASTNode(acquisitionNode);
                                    acquisitionNode.wrapperSymbols.Add(symbolNode);
                                    return new ParserLayerResult.PushLayer(
                                            new SymbolLayer(symbolNode.symbol, lineBreakSensitive),
                                            TokenDisposition.Consume
                                        );
                                }
                                else
                                {
                                    return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                                }
                            default:
                                if (lineBreakSensitive)
                                {
                                    return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                                }
                                else
                                {
                                    return ParserLayerResult.Continue.Instance;
                                }
                        }
                    case AcqExprParseState.WrapperNotFinished:
                        if((currentToken is NotationToken)&&(currentToken.Content == Notations.COLON.ToString()))
                        {
                            var symbolNode = new SymbolASTNode(acquisitionNode);
                            acquisitionNode.wrapperSymbols.Add(symbolNode);
                            return new ParserLayerResult.PushLayer(
                                    new SymbolLayer(symbolNode.symbol, lineBreakSensitive),
                                    TokenDisposition.Consume
                                );
                        }
                        else if((!lineBreakSensitive)&&(currentToken is LineBreakToken))
                        {
                            return ParserLayerResult.Continue.Instance;
                        }
                        else
                        {
                            return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                        }
                    default:
                        throw context.RaiseError("Illegal acqExpr PathParserLayer state:"+acqExprState);
                }
            }
        }
    }
}
