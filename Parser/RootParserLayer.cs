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
                    // 字符串字面量：父层创建 LiteralExpression 目标并挂接，子层原地填充
                    return PushLiteral();

                case WordToken token:
                    // 检查是否为字面量关键字
                    if (token.Content == Keywords.TRUE ||
                        token.Content == Keywords.FALSE ||
                        token.Content == Keywords.NULL)
                    {
                        return PushLiteral();
                    }
                    // 检查是否为数字字面量
                    else if (IsNumericLiteral(token.Content))
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
