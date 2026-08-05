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
    /// - var name: Type { get... set... } = value (属性访问器块，SYNTAX §9.4)
    /// - ext var Type.member: Type (ext 限定名，SYNTAX §4.4，仅 allowExtension 时)
    ///
    /// 状态流转：
    /// Initial → KeywordSeen → NameSeen → [TypeColonSeen → TypeSeen]
    ///   → [AccessorsSeen（委托 PropertyAccessorParserLayer）] → [AssignSeen → ValueSeen] → Completed
    ///
    /// 施工协议（大扫除后）：初始化表达式由 ExpressionParserLayer 直接附加到
    /// declNode.Initializer（ExpressionRootASTNode），无任何结果回传。
    /// </summary>
    public class VariableDeclarationParserLayer : IParserLayer, ISpanReceiver
    {
        private readonly VariableDeclarationASTNode declNode;
        private readonly bool allowExtension;   // ext 允许限定名（String.isEmpty，§4.4）
        // 父上下文是否允许裸 return（SYNTAX §5.1）：初始化表达式深处的
        // if/switch 表达式分支体继承该标记（lambda 体内为 false）
        private readonly bool allowBareReturn;

        private enum State
        {
            Initial,           // 初始状态
            KeywordSeen,       // 已看到 var/const
            NameSeen,          // 已看到变量名
            NameDotSeen,       // ext 限定名的段间点已读：等待下一段名称
            DestructureNameExpected,          // 解构（S7f）：( 或 , 之后等待名字
            DestructureCommaOrCloseExpected,  // 解构：名字之后等待 , 或 )
            DestructureCloseSeen,             // 解构：) 已读，等待 =
            TypeColonSeen,     // 已看到 :
            TypeSeen,          // 已看到类型
            AccessorsSeen,     // 已看到属性访问器块 { get... set... }
            AssignSeen,        // 已看到 =
            ValueSeen,         // 已看到初始化值
            Completed          // 完成
        }

        private State state = State.Initial;

        public VariableDeclarationParserLayer(
            VariableDeclarationASTNode node, bool allowExtension = false, bool allowBareReturn = true)
        {
            declNode = node;
            this.allowExtension = allowExtension;
            this.allowBareReturn = allowBareReturn;
        }

        // 层弹出时回填施工目标的源码范围（M28）
        public void ReceiveSpan(CharRange span) => declNode.Span ??= span;

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

                case State.NameDotSeen:
                    return HandleNameDotSeen(currentToken, context);

                case State.DestructureNameExpected:
                    return HandleDestructureNameExpected(currentToken, context);

                case State.DestructureCommaOrCloseExpected:
                    return HandleDestructureCommaOrCloseExpected(currentToken, context);

                case State.DestructureCloseSeen:
                    return HandleDestructureCloseSeen(currentToken, context);

                case State.TypeColonSeen:
                    return HandleTypeColonSeen(currentToken, context);

                case State.TypeSeen:
                    return HandleTypeSeen(currentToken, context);

                case State.AccessorsSeen:
                    return HandleAccessorsSeen(currentToken, context);

                case State.AssignSeen:
                    return HandleAssignSeen(currentToken, context);

                case State.ValueSeen:
                    return HandleValueSeen(currentToken, context);

                default:
                    context.RaiseError($"Invalid VariableDeclarationParserLayer state: {state}");
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
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
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 已看到关键字 - 等待变量名或解构开界（S7f）
        private ParserLayerResult HandleKeywordSeen(Token currentToken, ParserLayerContext context)
        {
            // 解构声明（S7f，SYNTAX §18）：var (a, b) = pair——
            // 与单名形态互斥（节点双字段，同 TypeCheck 互斥先例）
            if (currentToken is NotationToken openParen && openParen.Content == "(")
            {
                declNode.DestructureNames = new List<string>();
                state = State.DestructureNameExpected;
                return ParserLayerResult.Continue.Instance;
            }

            // 变量名必须是合法标识符：非数字词、非保留字（M31 统一走 Keywords.IsIdentifier）
            if (currentToken is WordToken wt && Keywords.IsIdentifier(wt.Content))
            {
                declNode.Name = wt.Content;
                state = State.NameSeen;
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is WordToken wt2)
            {
                context.RaiseError($"Cannot use reserved keyword or invalid identifier '{wt2.Content}' as variable name");
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            context.RaiseError($"Expected variable name, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 解构：等待一个名字（( 或 , 之后；至少一个名字）
        private ParserLayerResult HandleDestructureNameExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && Keywords.IsIdentifier(wt.Content))
            {
                declNode.DestructureNames!.Add(wt.Content);
                state = State.DestructureCommaOrCloseExpected;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError(
                $"Expected a name in destructuring declaration, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 解构：一个名字之后——等待 , 继续或 ) 收尾
        private ParserLayerResult HandleDestructureCommaOrCloseExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt)
            {
                if (nt.Content == ",")
                {
                    state = State.DestructureNameExpected;
                    return ParserLayerResult.Continue.Instance;
                }
                if (nt.Content == ")")
                {
                    state = State.DestructureCloseSeen;
                    return ParserLayerResult.Continue.Instance;
                }
            }

            context.RaiseError(
                $"Expected ',' or ')' in destructuring declaration, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 解构 ) 已读：等待 =（解构必须有初始化器，无类型标注形态）
        private ParserLayerResult HandleDestructureCloseSeen(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "=")
            {
                state = State.AssignSeen;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError(
                $"Destructuring declaration requires '=' with an initializer, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 已看到变量名 - 等待 : 或 = 或访问器块或结束
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
                else if (nt.Content == "{")
                {
                    // 属性访问器块（§9.4）：委托 PropertyAccessorParserLayer
                    state = State.AccessorsSeen;
                    return new ParserLayerResult.PushLayer(
                        new PropertyAccessorParserLayer(declNode), TokenDisposition.Replay);
                }
                else if (allowExtension && nt.Content == ".")
                {
                    // ext 限定名的段间点（String.isEmpty，§4.4）
                    state = State.NameDotSeen;
                    return ParserLayerResult.Continue.Instance;
                }
            }

            if (currentToken is LineBreakToken)
            {
                // 仅声明，无类型标注和初始化
                // 这在 Latte 中可能不合法，需要类型推断或显式类型
                context.LogWarning("Variable declaration without type or initializer");
                state = State.Completed;
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            // EOF：声明就此收尾（结构完整），EOF 上交 Root
            if (currentToken is EndOfFileToken)
            {
                context.LogWarning("Variable declaration without type or initializer");
                state = State.Completed;
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            // } 终止块内最后一条语句（保留 token 交给代码块层）
            if (currentToken is NotationToken closeBrace && closeBrace.Content == "}")
            {
                context.LogWarning("Variable declaration without type or initializer");
                state = State.Completed;
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            context.RaiseError($"Expected ':', '=' or line break after variable name, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // ext 限定名的段间点已读：拼接下一段（Type.member，可多段路径）
        private ParserLayerResult HandleNameDotSeen(Token currentToken, ParserLayerContext context)
        {
            // 段名必须是合法标识符（非数字词、非保留字，与变量名首段一致）
            if (currentToken is WordToken wt && Keywords.IsIdentifier(wt.Content))
            {
                declNode.Name += "." + wt.Content;
                state = State.NameSeen;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected member name after '.' in extension declaration, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 已看到类型冒号 - 解析类型引用
        private ParserLayerResult HandleTypeColonSeen(Token currentToken, ParserLayerContext context)
        {
            // 创建类型引用节点并解析
            declNode.TypeAnnotation = new TypeReferenceASTNode(declNode);
            state = State.TypeSeen;

            return new ParserLayerResult.PushLayer(
                new TypeReferenceParserLayer(declNode.TypeAnnotation), TokenDisposition.Replay  // 保留当前 token
            );
        }

        // 已看到类型 - 等待 = 或访问器块或结束
        private ParserLayerResult HandleTypeSeen(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "=")
            {
                state = State.AssignSeen;
                return ParserLayerResult.Continue.Instance;
            }

            // 属性访问器块（§9.4）：委托 PropertyAccessorParserLayer
            if (currentToken is NotationToken openBrace && openBrace.Content == "{")
            {
                state = State.AccessorsSeen;
                return new ParserLayerResult.PushLayer(
                    new PropertyAccessorParserLayer(declNode), TokenDisposition.Replay);
            }

            if (currentToken is LineBreakToken)
            {
                // 仅声明类型，无初始化
                state = State.Completed;
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            // EOF：声明就此收尾（结构完整），EOF 上交 Root
            if (currentToken is EndOfFileToken)
            {
                state = State.Completed;
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            // } 终止块内最后一条语句（保留 token 交给代码块层）
            if (currentToken is NotationToken closeBrace && closeBrace.Content == "}")
            {
                state = State.Completed;
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            context.RaiseError($"Expected '=' or line break after type annotation, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 访问器块已解析 - 等待 = 或结束（与 TypeSeen 的收尾逻辑一致）
        private ParserLayerResult HandleAccessorsSeen(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "=")
            {
                state = State.AssignSeen;
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is LineBreakToken)
            {
                state = State.Completed;
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            // EOF：声明就此收尾（结构完整），EOF 上交 Root
            if (currentToken is EndOfFileToken)
            {
                state = State.Completed;
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            // } 终止块内最后一条语句（保留 token 交给代码块层）
            if (currentToken is NotationToken closeBrace && closeBrace.Content == "}")
            {
                state = State.Completed;
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            context.RaiseError($"Expected '=' or line break after accessor block, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 已看到赋值符号 - 委托 ExpressionParserLayer 解析初始化表达式
        private ParserLayerResult HandleAssignSeen(Token currentToken, ParserLayerContext context)
        {
            // 创建 Initializer Root，表达式层直接向其附加
            declNode.Initializer = new ExpressionRootASTNode(declNode);
            state = State.ValueSeen;

            return new ParserLayerResult.PushLayer(
                new ExpressionParserLayer(declNode.Initializer) { allowBareReturn = allowBareReturn },
                TokenDisposition.Replay  // 保留当前 token，作为表达式的第一个 token
            );
        }

        // 已看到初始化值 - 完成
        private ParserLayerResult HandleValueSeen(Token currentToken, ParserLayerContext context)
        {
            // 表达式层弹出后，下一个 token 应当是换行；
            // 换行不属于声明（Replay 上交）——span 不拖尾换行符（M31，M28 约定）
            if (currentToken is LineBreakToken)
            {
                state = State.Completed;
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            // EOF：声明就此收尾（结构完整），EOF 上交 Root
            if (currentToken is EndOfFileToken)
            {
                state = State.Completed;
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            // } 终止块内最后一条语句（保留 token 交给代码块层）
            if (currentToken is NotationToken nt && nt.Content == "}")
            {
                state = State.Completed;
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            context.RaiseError($"Unexpected token after initializer: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }
    }
}
