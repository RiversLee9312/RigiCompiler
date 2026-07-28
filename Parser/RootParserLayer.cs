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
                case EndOfFileToken:
                    // EOF 只由 Root 消费：收下后 Parser 主循环结束，栈恰好收敛为 Root
                    return ParserLayerResult.Continue.Instance;

                case StringToken:
                case CharToken:
                    // 字符串/字符字面量：父层创建 LiteralExpression 目标并挂接，子层原地填充
                    return PushLiteral();

                case WordToken token:
                    // 检查是否为字面量关键字
                    if (token.Content == Keywords.TRUE ||
                        token.Content == Keywords.FALSE ||
                        token.Content == Keywords.NULL)
                    {
                        return PushLiteral();
                    }
                    // 检查是否为数字字面量（判定统一走 NumericLiteral，M31）
                    else if (NumericLiteral.IsNumericWord(token.Content))
                    {
                        return PushLiteral();
                    }
                    // 全局声明（var/const/func/class/... 及其修饰符）：统一交给通用声明层。
                    // 全局与类成员走同一个 Layer（见 SYNTAX.md §14.8：类名段可为空）。
                    else if (
                        Keywords.DeclarationDescriptors.Contains(token.Content)||
                        Keywords.DeclarationKeywords.Contains(token.Content))
                    {
                        return new ParserLayerResult.PushLayer(
                                new DeclarationParserLayer(root, root.Declarations),
                                TokenDisposition.Replay
                            );
                    }
                    // import 关键字
                    else if(token.Content == Keywords.IMPORT)
                    {
                        var importNode = new ImportASTNode(root);
                        root.Declarations.Add(importNode);
                        return new ParserLayerResult.PushLayer(
                                new ImportParserLayer(importNode),
                                TokenDisposition.Replay
                            );
                    }
                    // namespace 关键字（§15.1）
                    else if(token.Content == Keywords.NAMESPACE)
                    {
                        var nsNode = new NamespaceDeclarationASTNode(root);
                        root.Declarations.Add(nsNode);
                        return new ParserLayerResult.PushLayer(
                                new NamespaceParserLayer(nsNode),
                                TokenDisposition.Replay
                            );
                    }
                    else
                    {
                        throw context.RaiseError($"Unexpected token:{token}");
                    }

                case NotationToken token:
                    if(token.Content == Notations.AT_SIGN.ToString())
                    {
                        return new ParserLayerResult.PushLayer(
                                new DeclarationParserLayer(root, root.Declarations),
                                TokenDisposition.Replay
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

        // 顶层字面量：创建 LiteralExpression 目标并挂到 root，委托 LiteralParserLayer 填充
        private ParserLayerResult PushLiteral()
        {
            var literalExpr = new LiteralExpressionASTNode(root);
            root.Declarations.Add(literalExpr);
            return new ParserLayerResult.PushLayer(
                new LiteralParserLayer(literalExpr),
                TokenDisposition.Replay
            );
        }
    }
}
