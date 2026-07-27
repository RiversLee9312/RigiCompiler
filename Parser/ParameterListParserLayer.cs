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
    /// - init 参数映射（§9.3，仅 allowMapping 时）：_ -> x、horizontal: i32 -> x、_ -> x = 0
    ///
    /// 状态流转：
    /// Initial → ParamStart → NameSeen → TypeExpected → TypeParsed
    ///   → [DotsAwaitThird → VariadicDone] / [ValueParsed] → Completed
    ///   →（allowMapping）NameSeen/TypeParsed → MappedFieldExpected → AfterMappedField
    ///     → [ValueParsed] → Completed
    ///
    /// 施工协议（大扫除后）：读到参数名时即创建 ParameterASTNode，
    /// 类型由 TypeReferenceParserLayer 原地填充该节点的 Type；
    /// 默认值由 ExpressionParserLayer 直接附加到该节点的 DefaultValue Root，
    /// 无任何结果回传。
    /// </summary>
    public class ParameterListParserLayer : IParserLayer, ISpanReceiver
    {
        private readonly ParameterListASTNode targetNode;
        private readonly bool allowMapping;   // 仅 init 形参列表允许 _ -> field 映射（§9.3）

        private enum State
        {
            Initial,          // 等待 (
            ParamStart,       // 等待参数名或 )
            NameSeen,         // 已读参数名，等待 : 或 ->
            TypeExpected,     // : 已读，等待类型或 named
            TypeParsed,       // 类型已解析，等待 = . , ) 或 ->
            DotsAwaitThird,   // 等待第三个 .（前两个点已被符号解析消耗）
            VariadicDone,     // ... 已读完，等待 , 或 )
            MappedFieldExpected, // -> 已读，等待目标字段名
            AfterMappedField, // 字段名已读，等待 = , )
            ValueParsed,      // 默认值已解析，等待 , 或 )
            Completed         // 完成
        }

        private State state = State.Initial;

        // 正在解析的参数（读到参数名时创建，完成时提交到 Parameters）
        private ParameterASTNode? currentParameter = null;
        private bool pendingVariadic = false;
        private bool pendingNamedVariadic = false;
        private string? pendingMappedField = null;

        public ParameterListParserLayer(ParameterListASTNode target, bool allowMapping = false)
        {
            targetNode = target;
            this.allowMapping = allowMapping;
        }

        // 层弹出时回填形参列表节点的源码范围（M28）
        public void ReceiveSpan(CharRange span) => targetNode.Span ??= span;

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            // EOF：形参列表必须由 ) 结束，收到 EOF 是不完整结构
            if (currentToken is EndOfFileToken)
            {
                context.RaiseError("Unexpected end of file");
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

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
                case State.MappedFieldExpected:
                    return HandleMappedFieldExpected(currentToken, context);
                case State.AfterMappedField:
                    return HandleAfterMappedField(currentToken, context);
                case State.ValueParsed:
                    return HandleValueParsed(currentToken, context);
                default:
                    context.RaiseError($"Invalid ParameterListParserLayer state: {state}");
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
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
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 等待参数名或 )（空列表）
        private ParserLayerResult HandleParamStart(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == ")")
            {
                state = State.Completed;
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            if (currentToken is WordToken wt)
            {
                // 读到参数名即创建参数节点（后续类型/默认值原地填充）；
                // span 起点即参数名 token（named 修饰词在 : 之后才出现，见 HandleTypeExpected）（M28）
                currentParameter = new ParameterASTNode(targetNode) { Name = wt.Content };
                var loc = context.GetLocation();
                currentParameter.Span = new CharRange { Start = loc.Start, End = loc.End, sourceName = loc.sourceName };
                state = State.NameSeen;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected parameter name or ')', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 已读参数名：: （类型标注）；allowMapping 时 -> 直接转字段映射（类型沿用字段）
        private ParserLayerResult HandleNameSeen(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt)
            {
                if (nt.Content == ":")
                {
                    state = State.TypeExpected;
                    return ParserLayerResult.Continue.Instance;
                }

                if (allowMapping && nt.Content == Notations.ARROW)
                {
                    state = State.MappedFieldExpected;
                    return ParserLayerResult.Continue.Instance;
                }
            }

            context.RaiseError($"Expected ':' after parameter name '{currentParameter!.Name}', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // : 已读：等待类型或 named
        private ParserLayerResult HandleTypeExpected(Token currentToken, ParserLayerContext context)
        {
            // 具名可变参数：named Type...
            if (currentToken is WordToken wt && wt.Content == Keywords.NAMED)
            {
                pendingNamedVariadic = true;
                // 继续等待类型，named 已消费
                state = State.TypeParsed;
                return new ParserLayerResult.PushLayer(
                    new TypeReferenceParserLayer(currentParameter!.Type), TokenDisposition.Consume);
            }

            if (currentToken is WordToken)
            {
                // 委托 TypeReferenceParserLayer 原地填充参数类型
                state = State.TypeParsed;
                return new ParserLayerResult.PushLayer(
                    new TypeReferenceParserLayer(currentParameter!.Type), TokenDisposition.Replay);
            }

            context.RaiseError($"Expected parameter type, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 类型已解析：等待 =（默认值）、.（可变）、, 或 )
        private ParserLayerResult HandleTypeParsed(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt)
            {
                // 默认值：创建 DefaultValue Root，委托表达式层填充
                if (nt.Content == "=")
                {
                    currentParameter!.DefaultValue = new ExpressionRootASTNode(currentParameter);
                    state = State.ValueParsed;
                    return new ParserLayerResult.PushLayer(
                        new ExpressionParserLayer(currentParameter.DefaultValue), TokenDisposition.Consume);
                }

                // 可变参数：前两个点已被符号解析消耗，还差一个
                if (nt.Content == ".")
                {
                    state = State.DotsAwaitThird;
                    return ParserLayerResult.Continue.Instance;
                }

                // init 参数映射：horizontal: i32 -> x（§9.3）
                if (allowMapping && nt.Content == Notations.ARROW)
                {
                    state = State.MappedFieldExpected;
                    return ParserLayerResult.Continue.Instance;
                }

                // 下一个参数
                if (nt.Content == ",")
                {
                    CompleteParameter(context);
                    state = State.ParamStart;
                    return ParserLayerResult.Continue.Instance;
                }

                // 列表结束
                if (nt.Content == ")")
                {
                    CompleteParameter(context);
                    state = State.Completed;
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
                }
            }

            context.RaiseError($"Expected '=', '...', ',' or ')' after parameter type, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
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
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // ... 已读完：等待 , 或 )
        private ParserLayerResult HandleVariadicDone(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && (nt.Content == "," || nt.Content == ")"))
            {
                bool isClose = nt.Content == ")";
                CompleteParameter(context);
                state = isClose ? State.Completed : State.ParamStart;
                return isClose
                    ? new ParserLayerResult.PopLayer(TokenDisposition.Consume)
                    : ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected ',' or ')' after variadic parameter, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // -> 已读：等待映射目标字段名（§9.3）
        private ParserLayerResult HandleMappedFieldExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt)
            {
                pendingMappedField = wt.Content;
                state = State.AfterMappedField;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected field name after '->' in init parameter mapping, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 字段名已读：=（默认值，委托表达式）、, 或 )
        private ParserLayerResult HandleAfterMappedField(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt)
            {
                if (nt.Content == "=")
                {
                    currentParameter!.DefaultValue = new ExpressionRootASTNode(currentParameter);
                    state = State.ValueParsed;
                    return new ParserLayerResult.PushLayer(
                        new ExpressionParserLayer(currentParameter.DefaultValue), TokenDisposition.Consume);
                }

                if (nt.Content == ",")
                {
                    CompleteParameter(context);
                    state = State.ParamStart;
                    return ParserLayerResult.Continue.Instance;
                }

                if (nt.Content == ")")
                {
                    CompleteParameter(context);
                    state = State.Completed;
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
                }
            }

            context.RaiseError($"Expected '=', ',' or ')' after mapped field name, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 默认值已解析：等待 , 或 )
        private ParserLayerResult HandleValueParsed(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && (nt.Content == "," || nt.Content == ")"))
            {
                bool isClose = nt.Content == ")";
                CompleteParameter(context);
                state = isClose ? State.Completed : State.ParamStart;
                return isClose
                    ? new ParserLayerResult.PopLayer(TokenDisposition.Consume)
                    : ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected ',' or ')' after default value, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // ===== 辅助方法 =====

        // 把当前参数提交到 Parameters，并重置（映射参数省略类型时 Type 保持
        // 构造出的空引用节点，沿用字段类型，语义阶段回填）。
        // 此时当前 token 是 , 或 )（终止符、不属于参数），span 的 End 封到参数最后一个 token（M28）
        private void CompleteParameter(ParserLayerContext context)
        {
            if (currentParameter!.Span is { } s)
            {
                s.End = context.GetPreviousLocation().End;
                currentParameter.Span = s;
            }
            currentParameter.IsVariadic = pendingVariadic;
            currentParameter.IsNamedVariadic = pendingNamedVariadic;
            currentParameter.MappedFieldName = pendingMappedField;
            CleanTrailingEmptyElements(currentParameter.Type);
            // 映射参数省略类型的空 Type 节点未经解析层施工：连同其符号节点
            // 以参数 span 兜底（诊断时指向参数本身，M28）
            if (currentParameter.Type.Span == null)
            {
                currentParameter.Type.Span = currentParameter.Span;
                currentParameter.Type.TypeSymbol.Span ??= currentParameter.Span;
            }
            targetNode.Parameters.Add(currentParameter);
            currentParameter = null;
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
            pendingVariadic = false;
            pendingNamedVariadic = false;
            pendingMappedField = null;
        }
    }
}
