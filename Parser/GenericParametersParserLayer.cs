using System;
using System.Linq;

namespace RigiCompiler
{
    /// <summary>
    /// 泛型参数列表解析器
    ///
    /// 解析泛型声明中的 \<...> 参数列表（SYNTAX.md §3.6）：
    /// - 参数声明子句：TElement、out TElement、in TElement、TArgs...、named TValues...
    /// - 约束子句：TItem extends Comparable、X supers Y、TItem with Serializable
    /// - 可变参数后可接约束：named TValues... with Serializable
    ///
    /// 状态流转：
    /// Initial → BackslashSeen → ClauseStart →（各子句路径）→ Completed
    ///
    /// 委托说明（Delegate, don't implement）：
    /// - 子句中的类型（约束 Target/Bound、参数名候选）委托 TypeReferenceParserLayer 解析
    /// - out/in/named 前缀是关键字，由本层直接消费，不经过类型引用解析
    /// </summary>
    public class GenericParametersParserLayer : IParserLayer, ISpanReceiver
    {
        private readonly GenericParameterListASTNode targetNode;

        private enum State
        {
            Initial,              // 等待 \
            BackslashSeen,        // 等待 <
            ClauseStart,          // 等待子句开头：标识符 / out / in / named
            VarianceNameExpected, // out/in 已读，等待参数名
            NamedNameExpected,    // named 已读，等待参数名
            PrefixParamSeen,      // 前缀路径参数名已读，等待 . , > 或约束关键字
            Dots1,                // 前缀路径：已读第一个 .
            Dots2,                // 前缀路径：已读第二个 .
            DotsAwaitThird,       // TypeRef 路径：前两点已被符号解析消耗，等待第三个 .
            VariadicDone,         // ... 已读完，等待 , > 或约束关键字
            TargetParsed,         // 子句开头类型已解析，等待 , > . 或约束关键字
            BoundParsed,          // 约束 Bound 已解析，等待 , 或 >
            Completed             // 完成
        }

        private State state = State.Initial;

        // 读取中的参数信息（在各路径中累积，完成时统一提交）
        private string pendingName = "";
        private GenericVariance pendingVariance = GenericVariance.None;
        private bool pendingVariadic = false;
        private bool pendingNamedVariadic = false;
        private bool pendingSharedSafe = false;

        // TypeRef 路径：子句开头解析出的类型（可能是参数名候选或约束 Target）
        private TypeReferenceASTNode? pendingTarget = null;

        // 子句起点的源码位置（out/in/named 前缀或类型首 token），作参数/约束 span 的 Start（M28）；
        // 不在 ClearPending 清理：约束紧跟参数时 StartConstraint 仍需它，且每个子句入口都会重写
        private CharPosition? pendingStart = null;
        // 施工中的约束子句（StartConstraint 创建，BoundParsed 状态见到 , 或 > 时封 End）（M28）
        private GenericConstraintASTNode? pendingConstraint = null;

        public GenericParametersParserLayer(GenericParameterListASTNode target)
        {
            targetNode = target;
        }

        // 层弹出时回填泛型参数列表节点的源码范围（M28）
        public void ReceiveSpan(CharRange span) => targetNode.Span ??= span;

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            switch (state)
            {
                case State.Initial:
                    return HandleInitial(currentToken, context);
                case State.BackslashSeen:
                    return HandleBackslashSeen(currentToken, context);
                case State.ClauseStart:
                    return HandleClauseStart(currentToken, context);
                case State.VarianceNameExpected:
                case State.NamedNameExpected:
                    return HandleParameterPrefix(currentToken, context);
                case State.PrefixParamSeen:
                    return HandlePrefixParamSeen(currentToken, context);
                case State.Dots1:
                    return HandleDots1(currentToken, context);
                case State.Dots2:
                    return HandleDots2(currentToken, context);
                case State.DotsAwaitThird:
                    return HandleDotsAwaitThird(currentToken, context);
                case State.VariadicDone:
                    return HandleVariadicDone(currentToken, context);
                case State.TargetParsed:
                    return HandleTargetParsed(currentToken, context);
                case State.BoundParsed:
                    return HandleBoundParsed(currentToken, context);
                default:
                    context.RaiseError($"Invalid GenericParametersParserLayer state: {state}");
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }
        }

        // 等待 \（泛型列表开启符的第一半）
        private ParserLayerResult HandleInitial(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "\\")
            {
                state = State.BackslashSeen;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected '\\' to start generic parameter list, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 等待 <（泛型列表开启符的第二半）
        private ParserLayerResult HandleBackslashSeen(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "<")
            {
                state = State.ClauseStart;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected '<' after '\\' in generic parameter list, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 子句开头：分派到前缀路径（out/in/named）或类型引用路径
        private ParserLayerResult HandleClauseStart(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt)
            {
                // 子句起点：前缀修饰词（out/in/named）或类型首 token，即当前 token（M28）
                pendingStart = context.GetLocation().Start;

                if (wt.Content == Keywords.SHARED)
                {
                    pendingSharedSafe = true;
                    state = State.VarianceNameExpected;
                    return ParserLayerResult.Continue.Instance;
                }
                // out/in 型变前缀
                if (wt.Content == Keywords.OUT)
                {
                    pendingVariance = GenericVariance.Out;
                    state = State.VarianceNameExpected;
                    return ParserLayerResult.Continue.Instance;
                }
                if (wt.Content == Keywords.IN)
                {
                    pendingVariance = GenericVariance.In;
                    state = State.VarianceNameExpected;
                    return ParserLayerResult.Continue.Instance;
                }
                // named 具名可变参数前缀
                if (wt.Content == Keywords.NAMED)
                {
                    pendingNamedVariadic = true;
                    state = State.NamedNameExpected;
                    return ParserLayerResult.Continue.Instance;
                }

                // 其他标识符：委托 TypeReferenceParserLayer 解析子句开头的类型
                // （可能是参数名候选，也可能是约束 Target）
                pendingTarget = new TypeReferenceASTNode(targetNode);
                state = State.TargetParsed;
                return new ParserLayerResult.PushLayer(
                    new TypeReferenceParserLayer(pendingTarget), TokenDisposition.Replay  // 保留当前 token
                );
            }

            context.RaiseError(
                $"Expected type parameter, 'out', 'in' or 'named' in generic parameter list, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 前缀路径（out/in/named）参数名 token 的范围（M28）：
        // 作为约束 Target（隐含为参数名裸符号）的 span 来源
        private CharRange? pendingNameRange;

        // 前缀组合共用入口；保留首前缀 span，拒绝重复或冲突修饰。
        private ParserLayerResult HandleParameterPrefix(Token token, ParserLayerContext context)
        {
            if (token is WordToken word)
            {
                if (word.Content == Keywords.SHARED)
                {
                    if (pendingSharedSafe) context.RaiseError("Duplicate 'shared' generic parameter modifier");
                    pendingSharedSafe = true;
                    return ParserLayerResult.Continue.Instance;
                }
                if (word.Content == Keywords.IN || word.Content == Keywords.OUT)
                {
                    if (pendingVariance != GenericVariance.None)
                        context.RaiseError("Duplicate or conflicting variance modifier");
                    pendingVariance = word.Content == Keywords.IN ? GenericVariance.In : GenericVariance.Out;
                    return ParserLayerResult.Continue.Instance;
                }
                if (word.Content == Keywords.NAMED)
                {
                    if (pendingNamedVariadic) context.RaiseError("Duplicate 'named' generic parameter modifier");
                    pendingNamedVariadic = true;
                    return ParserLayerResult.Continue.Instance;
                }
            }
            return HandleVarianceNameExpected(token, context);
        }

        // out/in 已读：等待参数名
        private ParserLayerResult HandleVarianceNameExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && Keywords.IsIdentifier(wt.Content))
            {
                pendingName = wt.Content;
                pendingNameRange = context.GetLocation();
                state = State.PrefixParamSeen;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected type parameter name after generic modifier, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 前缀路径参数名已读：named 只接受 ...；out/in 还接受 , > 与约束关键字
        private ParserLayerResult HandlePrefixParamSeen(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == ".")
            {
                state = State.Dots1;
                return ParserLayerResult.Continue.Instance;
            }

            // named 参数必须带 ...
            if (pendingNamedVariadic)
            {
                context.RaiseError($"'named' variadic parameter requires '...', got: {currentToken}");
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            if (currentToken is NotationToken comma && comma.Content == ",")
            {
                CompleteParameter(context);
                state = State.ClauseStart;
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken close && close.Content == ">")
            {
                CompleteParameter(context);
                state = State.Completed;
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            var kind = TryGetConstraintKind(currentToken);
            if (kind != null)
            {
                // 型变参数 + 约束（如 out TElement extends Comparable）
                string name = pendingName;
                CompleteParameter(context);
                return StartConstraint(kind.Value, MakeBareSymbol(name), false, pendingNameRange, context);
            }

            context.RaiseError($"Expected '...', ',', '>' or constraint after parameter name, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 前缀路径：第一个 . 已读
        private ParserLayerResult HandleDots1(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == ".")
            {
                state = State.Dots2;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected '...' for variadic parameter, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 前缀路径：第二个 . 已读
        private ParserLayerResult HandleDots2(Token currentToken, ParserLayerContext context)
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

        // TypeRef 路径：符号解析已消耗前两个点（见 SymbolLayer 对连续 . 的处理），
        // 这里等待第三个 .
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

        // ... 已读完：等待 , > 或约束关键字（如 named TValues... with Serializable）
        private ParserLayerResult HandleVariadicDone(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken comma && comma.Content == ",")
            {
                CompleteParameter(context);
                state = State.ClauseStart;
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken close && close.Content == ">")
            {
                CompleteParameter(context);
                state = State.Completed;
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            var kind = TryGetConstraintKind(currentToken);
            if (kind != null)
            {
                // 可变参数的约束 Target 隐含为刚完成的参数
                string name = pendingName;
                CompleteParameter(context);
                return StartConstraint(kind.Value, MakeBareSymbol(name), false, pendingNameRange, context);
            }

            context.RaiseError($"Expected ',', '>' or constraint after variadic parameter, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 子句开头类型已解析：判断是参数声明、可变参数还是约束子句
        private ParserLayerResult HandleTargetParsed(Token currentToken, ParserLayerContext context)
        {
            var kind = TryGetConstraintKind(currentToken);
            if (kind != null)
            {
                // 裸标识符 Target（如 TItem extends Comparable）：该标识符即泛型参数
                // （SYNTAX §3.6 与 AST 注释），先提交进 Parameters 再开约束子句
                // （M40 修复：此前约束形态裸名参数只进 Constraints、丢失 Parameters
                // 声明，与 out/in 前缀路径的「参数+约束」双写形态不对称，
                // P2 无法解析参数符号）；
                // 非裸名 Target（路径/泛型实参/nullable）保留为纯约束目标，
                // 「Target 必须是泛型参数」的合法性检查归 P2（可恢复诊断）
                if (IsBareIdentifier(pendingTarget!, out var bareName))
                {
                    var nameRange = pendingTarget!.Span;
                    CommitParameterFromTarget(context);
                    return StartConstraint(kind.Value, MakeBareSymbol(bareName), false, nameRange, context);
                }
                var target = pendingTarget!;
                pendingTarget = null;
                // 约束子句：已解析类型的符号数据灌进 constraint 自带 Target 节点；
                // Target 原文范围即已解析类型的 span（M28）
                return StartConstraint(
                    kind.Value, target.TypeSymbol.symbol, target.IsNullable, target.Span, context);
            }

            if (currentToken is NotationToken nt)
            {
                if (nt.Content == ",")
                {
                    CommitParameterFromTarget(context);
                    state = State.ClauseStart;
                    return ParserLayerResult.Continue.Instance;
                }

                if (nt.Content == ">")
                {
                    CommitParameterFromTarget(context);
                    state = State.Completed;
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
                }

                if (nt.Content == ".")
                {
                    // 可变参数：前两个点已被符号解析消耗，还差一个。
                    // 名字先入 pending 字段但不提交——待 ... 读完后由 VariadicDone 统一提交
                    string? name = TryConvertToParamName(pendingTarget!, context);
                    pendingName = name!;
                    // 约束 Target 隐含为该参数名：其原文范围即已解析类型的 span（M28）
                    pendingNameRange = pendingTarget!.Span;
                    pendingTarget = null;
                    state = State.DotsAwaitThird;
                    return ParserLayerResult.Continue.Instance;
                }
            }

            context.RaiseError(
                $"Expected ',', '>', '...' or constraint after type in generic parameter list, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 约束 Bound 已解析：等待 , 或 >
        private ParserLayerResult HandleBoundParsed(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken comma && comma.Content == ",")
            {
                SealConstraintSpan(context);
                state = State.ClauseStart;
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken close && close.Content == ">")
            {
                SealConstraintSpan(context);
                state = State.Completed;
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            context.RaiseError($"Expected ',' or '>' after constraint, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 约束子句完成（当前 token 是 , 或 >）：span 的 End 封到 Bound 最后一个 token（M28）
        private void SealConstraintSpan(ParserLayerContext context)
        {
            if (pendingConstraint?.Span is { } s)
            {
                s.End = context.GetPreviousLocation().End;
                pendingConstraint.Span = s;
            }
            pendingConstraint = null;
        }

        // ===== 辅助方法 =====

        // 判断是否为约束关键字
        private GenericConstraintKind? TryGetConstraintKind(Token token)
        {
            if (token is WordToken wt)
            {
                if (wt.Content == Keywords.EXTENDS) return GenericConstraintKind.Extends;
                if (wt.Content == Keywords.SUPERS) return GenericConstraintKind.Supers;
                if (wt.Content == Keywords.WITH) return GenericConstraintKind.With;
            }
            return null;
        }

        // 把 pending 字段累积的参数信息提交到 Parameters，并重置。
        // 此时当前 token 是 , > 或约束关键字（终止符、不属于参数），
        // span 的 End 封到参数最后一个 token（M28）
        private void CompleteParameter(ParserLayerContext context)
        {
            var parameter = new GenericParameterASTNode(targetNode)
            {
                Name = pendingName,
                Variance = pendingVariance,
                IsVariadic = pendingVariadic,
                IsNamedVariadic = pendingNamedVariadic,
                RequiresSharedSafe = pendingSharedSafe
            };
            // Start 取子句起点；防御：未记录时退化为以最近消费 token 起点的单点 span
            var prev = context.GetPreviousLocation();
            parameter.Span = new CharRange
            {
                Start = pendingStart ?? prev.Start,
                End = prev.End,
                sourceName = prev.sourceName
            };
            targetNode.Parameters.Add(parameter);
            ClearPending();
        }

        private void ClearPending()
        {
            pendingName = "";
            pendingVariance = GenericVariance.None;
            pendingVariadic = false;
            pendingNamedVariadic = false;
            pendingSharedSafe = false;
            pendingTarget = null;
        }

        // TypeRef 路径：把已解析的类型校验并转换为参数名后提交
        private void CommitParameterFromTarget(ParserLayerContext context)
        {
            string? name = TryConvertToParamName(pendingTarget!, context);
            pendingName = name!;
            // TypeRef 路径不存在前缀修饰
            pendingVariance = GenericVariance.None;
            pendingNamedVariadic = false;
            pendingTarget = null;
            CompleteParameter(context);
        }

        // 类型引用 → 参数名：必须是裸标识符（单元素、无泛型实参、非可空）。
        // 容忍可变参数解析在符号末尾留下的空名元素。
        private string? TryConvertToParamName(TypeReferenceASTNode typeRef, ParserLayerContext context)
        {
            if (typeRef.IsNullable)
            {
                context.RaiseError("Nullable type cannot be a generic parameter name");
                return null;
            }

            var nonEmpty = typeRef.TypeSymbol.symbol.elements
                .Where(e => !string.IsNullOrEmpty(e.name))
                .ToList();

            if (nonEmpty.Count != 1 || nonEmpty[0].generics.Count > 0)
            {
                context.RaiseError(
                    "Generic parameter name must be a plain identifier (no path, no generic arguments)");
                return null;
            }

            return nonEmpty[0].name;
        }

        // 裸标识符判定（不抛错版，供 HandleTargetParsed 分派）：同 TryConvertToParamName
        // 的过滤规则；路径/泛型实参/nullable 形态返回 false（留给 P2 诊断）
        private static bool IsBareIdentifier(TypeReferenceASTNode typeRef, out string name)
        {
            name = "";
            if (typeRef.IsNullable) return false;
            var nonEmpty = typeRef.TypeSymbol.symbol.elements
                .Where(e => !string.IsNullOrEmpty(e.name))
                .ToList();
            if (nonEmpty.Count != 1 || nonEmpty[0].generics.Count > 0) return false;
            name = nonEmpty[0].name;
            return true;
        }

        // 由裸名构建单元素符号（作为约束 Target 的数据）
        private static Symbol MakeBareSymbol(string name)
        {
            var symbol = new Symbol();
            symbol.elements.Add(new SymbolElement { name = name });
            return symbol;
        }

        // 开始约束子句：把 Target 的符号数据深拷贝进 constraint 自带节点（其 Parent
        // 已是 constraint），再委托 TypeReferenceParserLayer 解析 Bound。
        // 注意：禁止把外部已建成的 Target 节点挂进来——节点 Parent 在创建时即定、
        // 禁止搬家；泛型实参已是 AST 节点（g1），symbol 数据也不再能直接转移，
        // 必须深拷贝以 constraint 自带 TypeSymbol 为父重挂实参子树。
        // targetSpan：Target 原文的范围（M28）——TypeRef 路径即已解析类型的 span，
        // 前缀/可变参数路径即参数名 token 的范围
        private ParserLayerResult StartConstraint(
            GenericConstraintKind kind, Symbol targetSymbol, bool targetNullable,
            CharRange? targetSpan, ParserLayerContext context)
        {
            var constraint = new GenericConstraintASTNode(targetNode) { Kind = kind };
            constraint.Target.TypeSymbol.symbol = targetSymbol.DeepClone(constraint.Target.TypeSymbol);
            constraint.Target.IsNullable = targetNullable;
            // 自带 Target 节点未经解析层施工，span 需按原文范围显式设置（含其符号节点）
            constraint.Target.Span = targetSpan;
            constraint.Target.TypeSymbol.Span = targetSpan;
            targetNode.Constraints.Add(constraint);
            // 约束 span（M28）：Start 同子句起点（Target 首 token；前缀路径即 out/in 修饰词），
            // End 待 Bound 解析完（BoundParsed 状态见到 , 或 >）时封闭
            if (pendingStart is { } start)
                constraint.Span = new CharRange { Start = start, End = start, sourceName = context.GetLocation().sourceName };
            pendingConstraint = constraint;

            state = State.BoundParsed;
            return new ParserLayerResult.PushLayer(
                new TypeReferenceParserLayer(constraint.Bound), TokenDisposition.Consume  // 消费掉 extends/supers/with 关键字
            );
        }
    }
}
