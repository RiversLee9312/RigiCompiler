using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LatteCompiler
{
    class RootParserLayer : IParserLayer
    {
        private RootASTNode root;
        public RootParserLayer(RootASTNode rootNode) {
            root = rootNode;
        }
        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            switch (currentToken) {
                case CommentToken:
                    return ParserLayerResult.Continue.Instance;

                case StringToken:
                    // 字符串字面量
                    return new ParserLayerResult.PushLayer(
                        layerToPush: new LiteralParserLayer(root),
                        shouldKeepToken: true
                    );

                case WordToken token:
                    // 检查是否为字面量关键字
                    if (token.Content == Keywords.TRUE ||
                        token.Content == Keywords.FALSE ||
                        token.Content == Keywords.NULL)
                    {
                        return new ParserLayerResult.PushLayer(
                            layerToPush: new LiteralParserLayer(root),
                            shouldKeepToken: true
                        );
                    }
                    // 检查是否为数字字面量
                    else if (IsNumericLiteral(token.Content))
                    {
                        return new ParserLayerResult.PushLayer(
                            layerToPush: new LiteralParserLayer(root),
                            shouldKeepToken: true
                        );
                    }
                    // 变量声明关键字 var/const
                    else if (token.Content == Keywords.VAR || token.Content == Keywords.CONST)
                    {
                        var varDeclNode = new VariableDeclarationASTNode(root);
                        root.Children.Add(varDeclNode);
                        return new ParserLayerResult.PushLayer(
                                layerToPush: new VariableDeclarationParserLayer(varDeclNode),
                                shouldKeepToken: true
                            );
                    }
                    // 其他声明关键字（func, class, etc.）
                    else if (
                        Keywords.DeclarationDescriptors.Contains(token.Content)||
                        Keywords.DeclarationKeywords.Contains(token.Content))
                    {
                        var declNode = new DeclarationASTNode(root);
                        root.Children.Add(declNode);
                        return new ParserLayerResult.PushLayer(
                                layerToPush: new DeclarationParserLayer(declNode),
                                shouldKeepToken: true
                            );
                    }
                    // import 关键字
                    else if(token.Content == Keywords.IMPORT)
                    {
                        var importNode = new ImportASTNode(root);
                        root.Children.Add(importNode);
                        return new ParserLayerResult.PushLayer(
                                layerToPush: new ImportParserLayer(importNode),
                                shouldKeepToken: true
                            );
                    }
                    else
                    {
                        throw context.RaiseError($"Unexpected token:{token}");
                    }

                case NotationToken token:
                    if(token.Content == Notations.AT_SIGN.ToString())
                    {
                        var declNode = new DeclarationASTNode(root);
                        root.Children.Add(declNode);
                        return new ParserLayerResult.PushLayer(
                                layerToPush:new DeclarationParserLayer(declNode),
                                shouldKeepToken:true
                            );
                    }
                    else
                    {
                        throw context.RaiseError($"Unexpected token:{token}");
                    }

                case LineBreakToken:
                    return ParserLayerResult.Continue.Instance;

                default:
                    throw context.RaiseError($"Unexpected Token:{currentToken}");
            }
        }

        // 辅助方法：判断是否为数字字面量
        private bool IsNumericLiteral(string content)
        {
            if (string.IsNullOrEmpty(content)) return false;

            // 十六进制
            if (content.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return true;

            // 检查第一个字符是否为数字
            if (!char.IsDigit(content[0]))
                return false;

            // 去除可能的后缀
            string withoutSuffix = content.TrimEnd('L', 'l', 'S', 's', 'B', 'b', 'U', 'u', 'F', 'f');

            // 去除 UL, US, UB 这样的双字符后缀
            if (withoutSuffix.Length >= 2)
            {
                string last2 = withoutSuffix.Substring(withoutSuffix.Length - 2).ToUpper();
                if (last2 == "UL" || last2 == "US" || last2 == "UB")
                {
                    withoutSuffix = withoutSuffix.Substring(0, withoutSuffix.Length - 2);
                }
            }

            // 检查是否包含小数点（浮点数）
            if (withoutSuffix.Contains('.'))
                return true;

            // 检查是否全为数字
            return withoutSuffix.All(c => char.IsDigit(c));
        }
    }
}
