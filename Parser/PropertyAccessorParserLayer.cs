using System;

namespace LatteCompiler
{
    /// <summary>
    /// 属性访问器块解析器（SYNTAX.md §9.4）
    ///
    /// 解析变量声明后的 { get... set... } 访问器块，结果直接写入
    /// VariableDeclarationASTNode.Getter / Setter。三类定义位置（类/struct 字段、
    /// 全局变量、栈上 var/const）都经由 VariableDeclarationParserLayer 汇聚到本层。
    ///
    /// 访问器形态：
    ///   pub get                     → 编译器生成实现（HasBackingField = true, Body = null）
    ///   get(value: _) { ... }       → backing field + 自定义体
    ///   get(_: _) { ... }           → 计算属性（HasBackingField = false）
    ///
    /// 状态流转：
    /// Initial → AccessorStart →（修饰符/get/set）→ AfterKeyword
    ///   →（( → ParamName → ParamColon → ParamPlaceholder → ParamClose → BodyExpected
    ///       → 委托 CodeBlockParserLayer）→ AccessorStart
    ///   →（换行/} ：自动访问器直接收尾）→ AccessorStart
    /// AccessorStart 遇 } 收尾：校验非空块与 get/set backing field 一致后弹栈
    ///
    /// 委托说明（Delegate, don't implement）：
    /// - 访问器体委托 CodeBlockParserLayer（它自己吃掉 '{' 到 '}'）
    /// </summary>
    public class PropertyAccessorParserLayer : IParserLayer
    {
        private readonly VariableDeclarationASTNode declNode;

        private enum State
        {
            Initial,           // 等待 {
            AccessorStart,     // 访问器开头：} 结束；修饰符/get/set 分派（跳过换行）
            AfterKeyword,      // get/set 已读：( 转参数；换行/} → 编译器生成访问器
            ParamName,         // ( 已读：等待参数名（value 或 _）
            ParamColon,        // 参数名已读：等待 :
            ParamPlaceholder,  // : 已读：等待 _ 占位符
            ParamClose,        // _ 已读：等待 )
            BodyExpected       // ) 已读：等待 { 并委托 CodeBlockParserLayer
        }

        private State state = State.Initial;
        private PropertyAccessorASTNode? current;   // 正在解析的访问器
        // 带体访问器：CommitAccessor 时体尚未解析，暂存于此，待体子层弹栈后封 span 的 End（M28）
        private PropertyAccessorASTNode? pendingBodySeal = null;

        public PropertyAccessorParserLayer(VariableDeclarationASTNode node)
        {
            declNode = node;
        }

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            switch (state)
            {
                case State.Initial:
                    return HandleInitial(currentToken, context);
                case State.AccessorStart:
                    return HandleAccessorStart(currentToken, context);
                case State.AfterKeyword:
                    return HandleAfterKeyword(currentToken, context);
                case State.ParamName:
                    return HandleParamName(currentToken, context);
                case State.ParamColon:
                    return HandleParamColon(currentToken, context);
                case State.ParamPlaceholder:
                    return HandleParamPlaceholder(currentToken, context);
                case State.ParamClose:
                    return HandleParamClose(currentToken, context);
                case State.BodyExpected:
                    return HandleBodyExpected(currentToken, context);
                default:
                    throw context.RaiseError($"Invalid PropertyAccessorParserLayer state: {state}");
            }
        }

        // 等待访问器块的 {
        private ParserLayerResult HandleInitial(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                state = State.AccessorStart;
                return ParserLayerResult.Continue.Instance;
            }
            throw context.RaiseError($"Expected '{{' to start property accessor block, got: {currentToken}");
        }

        // 访问器开头：} 收尾；否则收集修饰符、识别 get/set（结构性等待，跳过换行）
        private ParserLayerResult HandleAccessorStart(Token currentToken, ParserLayerContext context)
        {
            // 带体访问器：token 能回到本状态意味着体子层（CodeBlockParserLayer）已弹栈，
            // 最近消费的 token 即体的 }，在此封访问器 span 的 End（须在跳过换行之前）（M28）
            if (pendingBodySeal != null)
            {
                SealAccessorSpan(pendingBodySeal, context);
                pendingBodySeal = null;
            }

            if (currentToken is LineBreakToken) return ParserLayerResult.Continue.Instance;

            if (currentToken is NotationToken close && close.Content == "}")
                return CloseBlock(context);

            if (currentToken is WordToken wt)
            {
                if (Keywords.IsDescriptor(wt.Content))
                {
                    EnsureCurrent(context).Modifiers.Add(wt.Content);
                    return ParserLayerResult.Continue.Instance;
                }
                if (wt.Content == Keywords.GET || wt.Content == Keywords.SET)
                {
                    var accessor = EnsureCurrent(context);
                    accessor.Kind = wt.Content == Keywords.GET ? AccessorKind.Get : AccessorKind.Set;
                    state = State.AfterKeyword;
                    return ParserLayerResult.Continue.Instance;
                }
            }

            throw context.RaiseError($"Expected 'get', 'set', modifier or '}}' in accessor block, got: {currentToken}");
        }

        // get/set 已读：( 转参数路径；换行/} → 编译器生成访问器（无参无体）
        private ParserLayerResult HandleAfterKeyword(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "(")
            {
                state = State.ParamName;
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is LineBreakToken)
            {
                CommitAccessor(context);
                state = State.AccessorStart;
                return ParserLayerResult.Continue.Instance;
            }

            // 自动访问器也可以是块内最后一项：} 一并收尾（消费掉）
            if (currentToken is NotationToken close && close.Content == "}")
            {
                CommitAccessor(context);
                return CloseBlock(context);
            }

            throw context.RaiseError(
                $"Expected '(' for accessor parameters or line break for generated accessor, got: {currentToken}");
        }

        // ( 已读：参数名 value（要 backing field）或 _（计算属性）
        private ParserLayerResult HandleParamName(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is LineBreakToken) return ParserLayerResult.Continue.Instance;

            if (currentToken is WordToken wt && (wt.Content == "value" || wt.Content == "_"))
            {
                current!.HasBackingField = wt.Content == "value";
                state = State.ParamColon;
                return ParserLayerResult.Continue.Instance;
            }

            throw context.RaiseError($"Expected 'value' or '_' as accessor parameter, got: {currentToken}");
        }

        // 参数名已读：等待 :
        private ParserLayerResult HandleParamColon(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is LineBreakToken) return ParserLayerResult.Continue.Instance;

            if (currentToken is NotationToken nt && nt.Content == ":")
            {
                state = State.ParamPlaceholder;
                return ParserLayerResult.Continue.Instance;
            }

            throw context.RaiseError($"Expected ':' after accessor parameter name, got: {currentToken}");
        }

        // : 已读：类型位置只接受 _ 占位符（沿用字段类型，§9.4 示例形态）
        private ParserLayerResult HandleParamPlaceholder(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is LineBreakToken) return ParserLayerResult.Continue.Instance;

            if (currentToken is WordToken wt && wt.Content == "_")
            {
                state = State.ParamClose;
                return ParserLayerResult.Continue.Instance;
            }

            throw context.RaiseError($"Expected '_' placeholder as accessor parameter type, got: {currentToken}");
        }

        // _ 已读：等待 )
        private ParserLayerResult HandleParamClose(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is LineBreakToken) return ParserLayerResult.Continue.Instance;

            if (currentToken is NotationToken nt && nt.Content == ")")
            {
                state = State.BodyExpected;
                return ParserLayerResult.Continue.Instance;
            }

            throw context.RaiseError($"Expected ')' after accessor parameter, got: {currentToken}");
        }

        // ) 已读：等待 { 委托 CodeBlockParserLayer 解析访问器体（结构性等待，跳过换行）
        private ParserLayerResult HandleBodyExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is LineBreakToken) return ParserLayerResult.Continue.Instance;

            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                current!.Body = new CodeBlockASTNode(current);
                var body = current.Body;
                // 节点已完整（体由子层原地填充），立即提交并复位，回到 AccessorStart
                CommitAccessor(context);
                state = State.AccessorStart;
                return new ParserLayerResult.PushLayer(
                    new CodeBlockParserLayer(body), TokenDisposition.Replay);
            }

            throw context.RaiseError($"Expected '{{' for accessor body, got: {currentToken}");
        }

        // ===== 辅助方法 =====

        // 惰性创建当前访问器：修饰符先于 get/set 出现，节点在第一个修饰符或关键字时创建；
        // 同一访问器块内同种访问器只允许一个
        private PropertyAccessorASTNode EnsureCurrent(ParserLayerContext context)
        {
            if (current != null) return current;

            current = new PropertyAccessorASTNode(declNode);
            // span 起点：当前 token 即访问器第一个 token（pub/priv 修饰词或 get/set 关键字）（M28）
            var loc = context.GetLocation();
            current.Span = new CharRange { Start = loc.Start, End = loc.End, sourceName = loc.sourceName };
            return current;
        }

        // 访问器结束：按 Kind 挂到声明节点（重复定义在此报错），复位解析字段
        private void CommitAccessor(ParserLayerContext context)
        {
            if (current == null) return;

            // 无体访问器（编译器生成实现）立即封 span 的 End（当前 token 是换行/} 等终止符）；
            // 带体访问器暂存到 pendingBodySeal，待体子层弹栈后封口（M28）
            if (current.Body == null)
                SealAccessorSpan(current, context);
            else
                pendingBodySeal = current;

            if (current.Kind == AccessorKind.Get)
            {
                if (declNode.Getter != null)
                    throw context.RaiseError("Duplicate 'get' accessor in the same accessor block");
                declNode.Getter = current;
            }
            else
            {
                if (declNode.Setter != null)
                    throw context.RaiseError("Duplicate 'set' accessor in the same accessor block");
                declNode.Setter = current;
            }

            current = null;
        }

        // 访问器 span 封 End（M28）：End = 最近被消费的 token
        // （CharRange 是可变 struct：取出→改→写回）
        private static void SealAccessorSpan(PropertyAccessorASTNode accessor, ParserLayerContext context)
        {
            if (accessor.Span is { } s)
            {
                s.End = context.GetPreviousLocation().End;
                accessor.Span = s;
            }
        }

        // } 收尾：空块无意义；get/set 在是否需要 backing field 上必须一致（§9.4）
        private ParserLayerResult CloseBlock(ParserLayerContext context)
        {
            if (declNode.Getter == null && declNode.Setter == null)
                throw context.RaiseError("Accessor block must contain at least one 'get' or 'set'");

            if (declNode.Getter != null && declNode.Setter != null
                && declNode.Getter.HasBackingField != declNode.Setter.HasBackingField)
                throw context.RaiseError(
                    "'get' and 'set' must agree on whether a backing field is required (SYNTAX §9.4)");

            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }
    }
}
