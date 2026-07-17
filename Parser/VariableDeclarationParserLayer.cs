using System;

namespace LatteCompiler
{
    /// <summary>
    /// 变量声明解析器层
    ///
    /// 支持的语法：
    /// - var name = value
    /// - const name = value
    /// - var name: Type = value
    /// - const name: Type = value
    /// - var name: Type (无初始化)
    ///
    /// 状态流转：
    /// Initial → KeywordSeen → NameSeen → [TypeColonSeen → TypeSeen] → [AssignSeen → ValueSeen] → Completed
    ///
    /// 实现 IResultConsumer：初始化表达式由 ExpressionParserLayer 解析，
    /// 结果通过 OnChildResult 保存到 declNode.Initializer
    /// </summary>
    public class VariableDeclarationParserLayer : IParserLayer, IResultConsumer
    {
        private readonly VariableDeclarationASTNode declNode;

        private enum State
        {
            Initial,           // 初始状态
            KeywordSeen,       // 已看到 var/const
            NameSeen,          // 已看到变量名
            TypeColonSeen,     // 已看到 :
            TypeSeen,          // 已看到类型
            AssignSeen,        // 已看到 =
            ValueSeen,         // 已看到初始化值
            Completed          // 完成
        }

        private State state = State.Initial;

        public VariableDeclarationParserLayer(VariableDeclarationASTNode node)
        {
            declNode = node;
        }

        // IResultConsumer：接收 ExpressionParserLayer 的解析结果，保存为初始化表达式
        public void OnChildResult(ASTNode? result, IParserLayer child)
        {
            if (result is ExpressionASTNode expr)
            {
                declNode.Initializer = expr;
            }
        }

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            switch (state)
            {
                case State.Initial:
                    return HandleInitial(currentToken, context);

                case State.KeywordSeen:
                    return HandleKeywordSeen(currentToken, context);

                case State.NameSeen:
                    return HandleNameSeen(currentToken, context);

                case State.TypeColonSeen:
                    return HandleTypeColonSeen(currentToken, context);

                case State.TypeSeen:
                    return HandleTypeSeen(currentToken, context);

                case State.AssignSeen:
                    return HandleAssignSeen(currentToken, context);

                case State.ValueSeen:
                    return HandleValueSeen(currentToken, context);

                default:
                    context.RaiseError($"Invalid VariableDeclarationParserLayer state: {state}");
                    return new ParserLayerResult.PopLayer(false);
            }
        }

        // 初始状态 - 等待 var/const 关键字
        private ParserLayerResult HandleInitial(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt)
            {
                if (wt.Content == Keywords.VAR)
                {
                    declNode.IsConst = false;
                    state = State.KeywordSeen;
                    return ParserLayerResult.Continue.Instance;
                }
                else if (wt.Content == Keywords.CONST)
                {
                    declNode.IsConst = true;
                    state = State.KeywordSeen;
                    return ParserLayerResult.Continue.Instance;
                }
            }

            context.RaiseError($"Expected 'var' or 'const', got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 已看到关键字 - 等待变量名
        private ParserLayerResult HandleKeywordSeen(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt)
            {
                // 检查是否为保留关键字
                if (IsReservedKeyword(wt.Content))
                {
                    context.RaiseError($"Cannot use reserved keyword '{wt.Content}' as variable name");
                    return new ParserLayerResult.PopLayer(false);
                }

                declNode.Name = wt.Content;
                state = State.NameSeen;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected variable name, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 已看到变量名 - 等待 : 或 = 或结束
        private ParserLayerResult HandleNameSeen(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt)
            {
                if (nt.Content == ":")
                {
                    // 类型标注
                    state = State.TypeColonSeen;
                    return ParserLayerResult.Continue.Instance;
                }
                else if (nt.Content == "=")
                {
                    // 直接初始化，无类型标注
                    state = State.AssignSeen;
                    return ParserLayerResult.Continue.Instance;
                }
            }

            if (currentToken is LineBreakToken)
            {
                // 仅声明，无类型标注和初始化
                // 这在 Latte 中可能不合法，需要类型推断或显式类型
                context.LogWarning("Variable declaration without type or initializer");
                state = State.Completed;
                return new ParserLayerResult.PopLayer(true);
            }

            context.RaiseError($"Expected ':', '=' or line break after variable name, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 已看到类型冒号 - 解析类型引用
        private ParserLayerResult HandleTypeColonSeen(Token currentToken, ParserLayerContext context)
        {
            // 创建类型引用节点并解析
            declNode.TypeAnnotation = new TypeReferenceASTNode(declNode);
            state = State.TypeSeen;

            return new ParserLayerResult.PushLayer(
                new TypeReferenceParserLayer(declNode.TypeAnnotation),
                true  // 保留当前 token
            );
        }

        // 已看到类型 - 等待 = 或结束
        private ParserLayerResult HandleTypeSeen(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "=")
            {
                state = State.AssignSeen;
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is LineBreakToken)
            {
                // 仅声明类型，无初始化
                state = State.Completed;
                return new ParserLayerResult.PopLayer(true);
            }

            context.RaiseError($"Expected '=' or line break after type annotation, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 已看到赋值符号 - 委托 ExpressionParserLayer 解析初始化表达式
        private ParserLayerResult HandleAssignSeen(Token currentToken, ParserLayerContext context)
        {
            state = State.ValueSeen;

            // 表达式解析完成后，结果通过 OnChildResult 保存到 declNode.Initializer
            return new ParserLayerResult.PushLayer(
                new ExpressionParserLayer(declNode),
                true  // 保留当前 token，作为表达式的第一个 token
            );
        }

        // 已看到初始化值 - 完成
        private ParserLayerResult HandleValueSeen(Token currentToken, ParserLayerContext context)
        {
            // 表达式层弹出后，下一个 token 应当是换行
            if (currentToken is LineBreakToken)
            {
                state = State.Completed;
                return new ParserLayerResult.PopLayer(false);
            }

            context.RaiseError($"Unexpected token after initializer: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 辅助方法：检查是否为保留关键字
        private bool IsReservedKeyword(string word)
        {
            return Keywords.ControlStreamKeywords.Contains(word) ||
                   Keywords.DeclarationKeywords.Contains(word) ||
                   Keywords.StringOperators.Contains(word) ||
                   Keywords.StringValues.Contains(word);
        }
    }
}
