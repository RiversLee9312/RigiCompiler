using System;

namespace LatteCompiler
{
    /// <summary>
    /// 函数形参列表解析器（roadmap #5）
    ///
    /// 解析函数/lambda/运算符/init 声明中的 (...) 形参列表（SYNTAX.md §4）：
    /// - 普通参数：a: i32
    /// - 默认参数：name: String = "World"
    /// - 位置可变参数：numbers: i32...
    /// - 具名可变参数：options: named String...
    ///
    /// 状态流转：
    /// Initial → ParamStart → NameSeen → TypeExpected → TypeParsed
    ///   → [DotsAwaitThird → VariadicDone] / [ValueParsed] → Completed
    ///
    /// 委托说明（Delegate, don't implement）：
    /// - 参数类型委托 TypeReferenceParserLayer（原地写入 pendingType）
    /// - 默认值委托 ExpressionParserLayer（结果经 IResultConsumer 回传）
    /// </summary>
    public class ParameterListParserLayer : IParserLayer, IResultConsumer
    {
        private readonly ParameterListASTNode targetNode;

        private enum State
        {
            Initial,          // 等待 (
            ParamStart,       // 等待参数名或 )
            NameSeen,         // 已读参数名，等待 :
            TypeExpected,     // : 已读，等待类型或 named
            TypeParsed,       // 类型已解析，等待 = . , )
            DotsAwaitThird,   // 等待第三个 .（前两个点已被符号解析消耗）
            VariadicDone,     // ... 已读完，等待 , 或 )
            ValueParsed,      // 默认值已解析，等待 , 或 )
            Completed         // 完成
        }

        private State state = State.Initial;

        // 读取中的参数信息（累积，完成时统一提交）
        private string pendingName = "";
        private TypeReferenceASTNode? pendingType = null;
        private bool pendingVariadic = false;
        private bool pendingNamedVariadic = false;
        private ExpressionASTNode? pendingDefault = null;

        public ParameterListParserLayer(ParameterListASTNode target)
        {
            targetNode = target;
        }

        // IResultConsumer：接收 ExpressionParserLayer 的默认值解析结果
        public void OnChildResult(ASTNode? result, IParserLayer child)
        {
            if (result is ExpressionASTNode expr)
            {
                pendingDefault = expr;
            }
        }

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            switch (state)
            {
                case State.Initial:
                    return HandleInitial(currentToken, context);
                case State.ParamStart:
                    return HandleParamStart(currentToken, context);
                case State.NameSeen:
                    return HandleNameSeen(currentToken, context);
                case State.TypeExpected:
                    return HandleTypeExpected(currentToken, context);
                case State.TypeParsed:
                    return HandleTypeParsed(currentToken, context);
                case State.DotsAwaitThird:
                    return HandleDotsAwaitThird(currentToken, context);
                case State.VariadicDone:
                    return HandleVariadicDone(currentToken, context);
                case State.ValueParsed:
                    return HandleValueParsed(currentToken, context);
                default:
                    context.RaiseError($"Invalid ParameterListParserLayer state: {state}");
                    return new ParserLayerResult.PopLayer(false);
            }
        }

        // 等待 (
        private ParserLayerResult HandleInitial(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "(")
            {
                state = State.ParamStart;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected '(' to start parameter list, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 等待参数名或 )（空列表）
        private ParserLayerResult HandleParamStart(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == ")")
            {
                state = State.Completed;
                return new ParserLayerResult.PopLayer(false);
            }

            if (currentToken is WordToken wt)
            {
                pendingName = wt.Content;
                state = State.NameSeen;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected parameter name or ')', got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 已读参数名：必须是 : （类型标注）
        private ParserLayerResult HandleNameSeen(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == ":")
            {
                state = State.TypeExpected;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected ':' after parameter name '{pendingName}', got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // : 已读：等待类型或 named
        private ParserLayerResult HandleTypeExpected(Token currentToken, ParserLayerContext context)
        {
            // 具名可变参数：named Type...
            if (currentToken is WordToken wt && wt.Content == Keywords.NAMED)
            {
                pendingNamedVariadic = true;
                // 继续等待类型，named 已消费
                pendingType = new TypeReferenceASTNode(targetNode);
                state = State.TypeParsed;
                return new ParserLayerResult.PushLayer(
                    new TypeReferenceParserLayer(pendingType), false);
            }

            if (currentToken is WordToken)
            {
                // 委托 TypeReferenceParserLayer 解析参数类型
                pendingType = new TypeReferenceASTNode(targetNode);
                state = State.TypeParsed;
                return new ParserLayerResult.PushLayer(
                    new TypeReferenceParserLayer(pendingType), true);
            }

            context.RaiseError($"Expected parameter type, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 类型已解析：等待 =（默认值）、.（可变）、, 或 )
        private ParserLayerResult HandleTypeParsed(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt)
            {
                // 默认值
                if (nt.Content == "=")
                {
                    state = State.ValueParsed;
                    return new ParserLayerResult.PushLayer(
                        new ExpressionParserLayer(targetNode), false);
                }

                // 可变参数：前两个点已被符号解析消耗，还差一个
                if (nt.Content == ".")
                {
                    state = State.DotsAwaitThird;
                    return ParserLayerResult.Continue.Instance;
                }

                // 下一个参数
                if (nt.Content == ",")
                {
                    CompleteParameter();
                    state = State.ParamStart;
                    return ParserLayerResult.Continue.Instance;
                }

                // 列表结束
                if (nt.Content == ")")
                {
                    CompleteParameter();
                    state = State.Completed;
                    return new ParserLayerResult.PopLayer(false);
                }
            }

            context.RaiseError($"Expected '=', '...', ',' or ')' after parameter type, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 等待第三个 .
        private ParserLayerResult HandleDotsAwaitThird(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == ".")
            {
                pendingVariadic = true;
                state = State.VariadicDone;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected '...' for variadic parameter, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // ... 已读完：等待 , 或 )
        private ParserLayerResult HandleVariadicDone(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && (nt.Content == "," || nt.Content == ")"))
            {
                bool isClose = nt.Content == ")";
                CompleteParameter();
                state = isClose ? State.Completed : State.ParamStart;
                return isClose
                    ? new ParserLayerResult.PopLayer(false)
                    : ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected ',' or ')' after variadic parameter, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 默认值已解析：等待 , 或 )
        private ParserLayerResult HandleValueParsed(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && (nt.Content == "," || nt.Content == ")"))
            {
                bool isClose = nt.Content == ")";
                CompleteParameter();
                state = isClose ? State.Completed : State.ParamStart;
                return isClose
                    ? new ParserLayerResult.PopLayer(false)
                    : ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected ',' or ')' after default value, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // ===== 辅助方法 =====

        // 把 pending 字段累积的参数信息提交到 Parameters，并重置
        private void CompleteParameter()
        {
            CleanTrailingEmptyElements(pendingType!);

            targetNode.Parameters.Add(new ParameterASTNode(targetNode)
            {
                Name = pendingName,
                Type = pendingType!,
                IsVariadic = pendingVariadic,
                IsNamedVariadic = pendingNamedVariadic,
                DefaultValue = pendingDefault
            });
            ClearPending();
        }

        // 可变参数（...）经符号解析时会在符号末尾留下空名元素，
        // 提交前清理，保证 AST 干净
        private static void CleanTrailingEmptyElements(TypeReferenceASTNode typeRef)
        {
            typeRef.TypeSymbol.symbol.elements.RemoveAll(e => string.IsNullOrEmpty(e.name));
        }

        private void ClearPending()
        {
            pendingName = "";
            pendingType = null;
            pendingVariadic = false;
            pendingNamedVariadic = false;
            pendingDefault = null;
        }
    }
}
