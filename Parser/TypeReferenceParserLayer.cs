using System;

namespace LatteCompiler
{
    /// <summary>
    /// 类型引用解析器层
    /// 负责解析类型引用（使用类型时），包括：
    /// - 基本类型: i32, i64, String, bool 等
    /// - 用户定义类型: MyClass, MyStruct
    /// - 泛型类型: Container<T>, Map<K, V>
    /// - 可空类型: String?, i32?
    ///
    /// 注意：rich 和 shared 是类型声明的修饰符，不在这里处理！
    /// 它们应该由 ClassDeclarationParserLayer 等处理。
    /// </summary>
    public class TypeReferenceParserLayer : IParserLayer
    {
        private readonly TypeReferenceASTNode targetNode;

        private enum State
        {
            Initial,           // 初始状态，等待类型名
            TypeNameSeen,      // 已看到类型名，等待 ? 或结束
            Completed          // 解析完成
        }

        private State state = State.Initial;

        public TypeReferenceParserLayer(TypeReferenceASTNode target)
        {
            targetNode = target;
        }

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            switch (state)
            {
                case State.Initial:
                    return HandleInitial(currentToken, context);

                case State.TypeNameSeen:
                    return HandleTypeNameSeen(currentToken, context);

                default:
                    context.RaiseError($"Invalid TypeReferenceParserLayer state: {state}");
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }
        }

        // 处理初始状态 - 直接解析类型符号
        private ParserLayerResult HandleInitial(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken)
            {
                // 使用 PathParserLayer 解析类型符号（支持泛型）
                state = State.TypeNameSeen;

                return new ParserLayerResult.PushLayer(
                    new PathParserLayer(
                        PathParserLayer.PathType.SymbolPath,
                        targetNode.TypeSymbol,
                        lineBreakSensitive: true
                    ),
                    TokenDisposition.Replay
                );
            }

            context.RaiseError($"Expected type name, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 处理已看到类型名的状态 - 检查可空标记
        private ParserLayerResult HandleTypeNameSeen(Token currentToken, ParserLayerContext context)
        {
            // 检查是否为可空类型标记 ?
            if (currentToken is NotationToken notation && notation.Content == "?")
            {
                targetNode.IsNullable = true;
                state = State.Completed;
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume); // 消费 ? token，结束解析
            }

            // 其他 token，类型引用解析完成
            state = State.Completed;
            return new ParserLayerResult.PopLayer(TokenDisposition.Replay); // 保留当前 token
        }
    }
}

