using System;

namespace LatteCompiler
{
    /// <summary>
    /// namespace 声明解析器（SYNTAX.md §15.1，P5 收尾）
    ///
    /// 规范形态（顶层单行声明）：
    ///   namespace com.example.myapp
    ///
    /// 路径复用 PathParserLayer 解析（遇换行弹出并交还 token）。
    /// 唯一性与位置约束（应在文件首部）留待语义阶段。
    ///
    /// 状态流转：NamespaceKeyword → PathStart →（PathParserLayer 弹出）→ AfterPath（换行弹栈）
    /// </summary>
    public class NamespaceParserLayer : IParserLayer
    {
        private enum State
        {
            NamespaceKeyword,   // 等待 namespace 关键字（RootParserLayer 以 keepToken 传入）
            PathStart,          // 等待路径起点：委托 PathParserLayer
            AfterPath           // 路径已解析：换行收尾弹栈
        }

        private readonly NamespaceDeclarationASTNode self;
        private State state = State.NamespaceKeyword;

        public NamespaceParserLayer(NamespaceDeclarationASTNode self)
        {
            this.self = self;
        }

        public ParserLayerResult ParseToken(Token t, ParserLayerContext context)
        {
            switch (state)
            {
                case State.NamespaceKeyword:
                    if (t is WordToken w && w.Content == Keywords.NAMESPACE)
                    {
                        state = State.PathStart;
                        return ParserLayerResult.Continue.Instance;
                    }
                    throw context.RaiseError($"Expected 'namespace', got: {t}");

                case State.PathStart:
                    if (t is LineBreakToken)
                        throw context.RaiseError(
                            "Namespace declaration requires a namespace path (SYNTAX §15.1)");
                    state = State.AfterPath;
                    return new ParserLayerResult.PushLayer(
                        new PathParserLayer(
                            PathParserLayer.PathType.SymbolPath, self.Name, lineBreakSensitive: true), TokenDisposition.Replay);

                case State.AfterPath:
                    if (t is LineBreakToken || t is EndOfFileToken)
                    {
                        if (self.Name.symbol.elements.Count == 0)
                            throw context.RaiseError(
                                "Namespace declaration requires a namespace path (SYNTAX §15.1)");
                        // 换行由本层消费；EOF 上交 Root
                        return new ParserLayerResult.PopLayer(
                            t is EndOfFileToken ? TokenDisposition.Replay : TokenDisposition.Consume);
                    }
                    throw context.RaiseError($"Unexpected token in namespace declaration: {t}");

                default:
                    throw context.RaiseError($"Invalid NamespaceParserLayer state: {state}");
            }
        }
    }
}
