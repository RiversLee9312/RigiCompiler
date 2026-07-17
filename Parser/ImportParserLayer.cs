using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LatteCompiler
{
    public class ImportParserLayer : IParserLayer
    {
        public ImportParserLayer(ImportASTNode self)
        {
            this.self = self;
        }
        private enum InputStatementType
        {
            SingleItem,
            WithAlias,
            MultiItem,
            Unknown
        }
        private ImportASTNode self;
        private bool isFirstToken = true;
        private ImportItem currentItem = new();
        private InputStatementType type = InputStatementType.Unknown;
        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            if(currentToken.Type is not( TokenType.Word or TokenType.LineBreak or TokenType.Notation))
            {
                throw context.RaiseError($"Unexpected token appeared in import statement:{currentToken}");
            }
            if (isFirstToken)
            {
                if ((currentToken.Content != Keywords.IMPORT)||(currentToken is not WordToken))
                {
                    throw context.RaiseError($"Unexpected token appeared in import statement:{currentToken}");
                }
                else
                {
                    isFirstToken = false;
                    return ParserLayerResult.Continue.Instance;
                }
            }
            else
            {
                if (currentToken is LineBreakToken) {
                    switch (type)
                    {
                        case InputStatementType.Unknown:
                            throw context.RaiseError("Unexpected end of line in import statement");
                        case InputStatementType.SingleItem or InputStatementType.MultiItem:
                            if ((currentItem.alias != null)&&(type == InputStatementType.MultiItem))
                            {
                                throw context.RaiseError("Multiple import item has alias.");
                            }
                            else if (currentItem.symbolNode == null)
                            {
                                throw context.RaiseError("Import item missing symbol.");
                            }
                            else
                            {
                                self.importedSymbols.Add(currentItem);
                                return new ParserLayerResult.PopLayer(shouldKeepToken: false);
                            }
                        case InputStatementType.WithAlias:
                            if (currentItem.alias == null)
                            {
                                throw context.RaiseError("Import item missing alias.");
                            }
                            else if (currentItem.symbolNode == null)
                            {
                                throw context.RaiseError("Import item missing symbol.");
                            }
                            else if (self.importedSymbols.Count != 0)
                            {
                                throw context.RaiseError("Multiple import items with alias are not allowed.");
                            }
                            else
                            {
                                self.importedSymbols.Add(currentItem);
                                return new ParserLayerResult.PopLayer(shouldKeepToken: false);
                            }
                        default:
                            throw context.RaiseError("Unexpected state in import statement.");
                    }
                }
                else
                {
                    if (currentToken.Content == Keywords.AS)
                    {
                        if (type != InputStatementType.SingleItem)
                        {
                            throw context.RaiseError($"Unexpected token in import statement:{currentToken}.");
                        }
                        else
                        {
                            type = InputStatementType.WithAlias;
                            return ParserLayerResult.Continue.Instance;
                        }
                    }
                    else if (currentToken.Content == Notations.COMMA.ToString())
                    {
                        if(type == InputStatementType.WithAlias)
                        {
                            throw context.RaiseError("Multiple import items with alias are not allowed.");
                        }
                        else if(type == InputStatementType.Unknown)
                        {
                            throw context.RaiseError("Import item missing.");
                        }
                        else //type == InputStatementType.MultiItem or SingleItem
                        {
                            if (currentItem.symbolNode == null)
                            {
                                throw context.RaiseError("Import item missing symbol.");
                            }else if(currentItem.alias != null)
                            {
                                throw context.RaiseError("Multiple import item has alias.");
                            }
                            self.importedSymbols.Add(currentItem);
                            currentItem = new();
                            type = InputStatementType.MultiItem;
                            return ParserLayerResult.Continue.Instance;
                        }
                    }
                    else if (currentToken is WordToken) {
                        if (type == InputStatementType.WithAlias)
                        {
                            if(currentItem.symbolNode == null)
                            {
                                throw context.RaiseError("Import item missing symbol.");
                            }
                            else if(currentItem.alias != null)
                            {
                                throw context.RaiseError("Import item has multiple alias.");
                            } else
                            {
                                currentItem.alias = currentToken.Content;
                                return ParserLayerResult.Continue.Instance;
                            }
                        }
                        else 
                        {
                            
                            if(type == InputStatementType.Unknown)
                            {
                                type = InputStatementType.SingleItem;
                            }
                            var symbolNode = new SymbolASTNode(self);
                            currentItem.symbolNode = symbolNode;
                                return new ParserLayerResult.PushLayer(
                                        shouldKeepToken: true,
                                        layerToPush: new PathParserLayer(PathParserLayer.PathType.SymbolPath, symbolNode,true)
                                    );
                        }
                    }
                    else if(currentToken.Content == Notations.ASTERISK.ToString())
                    {
                        if (type == InputStatementType.WithAlias)
                        {
                            throw context.RaiseError("Unexpected token in alias:"+currentToken);
                        }else if(type== InputStatementType.Unknown)
                        {
                            throw context.RaiseError("Import symbol missing.");
                        }
                        else
                        {
                            currentItem.importAll = true;
                            return ParserLayerResult.Continue.Instance;
                        }
                    }
                    else
                    {
                        throw context.RaiseError($"Unexpected token in import statement:{currentToken}.");
                    }
                }
            }
        }
    }
}
