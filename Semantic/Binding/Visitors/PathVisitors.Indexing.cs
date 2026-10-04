
namespace RigiCompiler
{
    internal static partial class PathFacility
    {
        // Indexing 职责；与主文件共享同一类型、字段及生命周期。

        // 索引访问绑定（S8c，SYNTAX §13.2）：读模式绑 getAtIndex(index)
        // （恰 1 参数，Type = 返回类型）；写模式（赋值 place 全路径最后一
        // 步）绑 setAtIndex(index, element)（恰 2 参数，Type = 元素形参
        // 类型）。§13.2 签名固定单 TIndex——多参数索引非法；具名实参与
        // 普通调用同规则（读模式经 BindArguments 归位，诊断自然产生）
        private static BoundExpression? BindIndexAccess(ASTNode node, BoundExpression receiver,
            PathSuffixASTNode suffix, bool forWrite, Scope scope, BindContext ctx,
            BindEnvironment env)
        {
            // 毒化静默：receiver 已失败时不再报次生错误
            if (receiver.Type is ErrorTypeSymbol) return null;
            var display = BoundAnalysis.TypeDisplay(receiver.Type);
            // （S9a：泛型参数 receiver 判型后不命中 nullable 分支）
            if (receiver.Type is TypeSymbol { ConstructedFrom: not null } receiverType
                && receiverType.ConstructedFrom == env.B.NullableDefinition)
            {
                env.Error(node.Span, $"Cannot index nullable type '{display}'");
                return null;
            }
            var name = forWrite ? "setAtIndex" : "getAtIndex";
            var lookupType = SymbolLookup.EffectiveMemberType(receiver.Type, env);
            var candidates = receiver.Type is ErrorTypeSymbol
                ? new List<MethodSymbol>()
                : SymbolLookup.FindInstanceOperators(lookupType, name, forWrite ? 2 : 1,
                    env.Unit.Symbols);
            if (candidates.Count == 0)
            {
                env.Error(node.Span,
                    $"Type '{display}' does not define an index operator ('{name}')");
                return null;
            }
            // 使用点访问控制（S8e，SYNTAX §16.1：索引运算符是成员访问
            // 使用点）：不可见候选不参与；全部不可见报不可见诊断
            var accessible = candidates.Where(ctx.Frame.CanAccess).ToList();
            if (accessible.Count == 0)
            {
                env.Error(node.Span, AccessChecker.InaccessibleMessage(candidates[0]));
                return null;
            }
            candidates = accessible;
            if (!forWrite)
            {
                // 读模式：重载解析复用调用设施（S8d；多候选按索引实参类型
                // ranking）——实参绑定、多参数/具名/缺失诊断自然产生。
                // receiverType = receiver 静态类型（索引 operator 宿主代入）
                var resolved = OverloadResolution.Resolve(node, candidates, suffix.Arguments,
                    scope, ctx, env, receiverType: lookupType);
                if (resolved == null) return null;
                var (op, boundArguments, opResultType, _, _) = resolved.Value;
                if (opResultType == null)
                {
                    env.Error(node.Span, $"Method '{op.Name}' has no result (void) " +
                        "and cannot be used as a value");
                    return null;
                }
                // S9a 放行：索引返回类型可为泛型参数（引用相等身份）
                return new BoundIndexExpression(node, receiver, boundArguments[0], op,
                    opResultType);
            }
            // 写模式多候选仍归口（RHS 类型在赋值侧才可知，ranking 无法在此进行）
            if (candidates.Count > 1)
            {
                env.Error(node.Span, $"P3: overload resolution for write-mode '{name}' " +
                    "is not supported yet");
                return null;
            }
            var writeOp = candidates[0];
            UnsafeGates.CheckMethod(writeOp, node, ctx, env);
            // 写模式形参宿主代入（S8c 修复）：定义级 setAtIndex 的形参类型
            // 含宿主泛型参数时按 receiver 构造链代入（读模式经
            // OverloadResolution 的 receiverType 同口径——
            // Box\<T\>.setAtIndex(index, element: T) 在 Box\<i32\> 上
            // element → i32）；candidates 非空 ⇒ receiver.Type 必为
            // TypeSymbol（上方 FindInstanceOperators 判型查询）
            var writeReceiver = lookupType;
            // 写模式：恰一个索引实参（多参数索引非法的定稿诊断）
            if (suffix.Arguments.Count != 1)
            {
                env.Error(node.Span, $"Index access on '{display}' expects exactly one " +
                    $"index argument, got {suffix.Arguments.Count}");
                return null;
            }
            var argument = suffix.Arguments[0];
            var indexParameter = writeOp.Parameters[0];
            if (argument.Name != null && argument.Name != indexParameter.Name)
            {
                env.Error(argument.Span,
                    $"'setAtIndex' has no parameter named '{argument.Name}'");
                return null;
            }
            // S9a 放行：索引形参类型可为泛型参数（引用相等身份——代入后
            // 仍可能是外层泛型参数）
            var indexType = indexParameter.Type == null ? null
                : SymbolLookup.SubstituteForReceiver(indexParameter.Type, writeOp,
                    writeReceiver, env.Unit.Symbols);
            var index = ExpressionDispatcher.Visit(argument.Value.Expression, scope, ctx, env,
                indexType as TypeSymbol);
            if (index == null) return null;
            if (indexType != null && !SymbolLookup.IsAssignable(index.Type, indexType, env))
            {
                env.Error(argument.Value.Span ?? argument.Span,
                    $"Cannot pass '{BoundAnalysis.TypeDisplay(index.Type)}' as " +
                    $"'{BoundAnalysis.TypeDisplay(indexType)}'");
                return null;
            }
            // S9a 放行：元素形参类型可为泛型参数（引用相等身份）；形参类型
            // 必非空（P2 已定型）；宿主代入同索引形参
            var elementType = SymbolLookup.SubstituteForReceiver(writeOp.Parameters[1].Type!,
                writeOp, writeReceiver, env.Unit.Symbols);
            return new BoundIndexExpression(node, receiver, index, writeOp, elementType);
        }

        // 安全访问段（S7f，SYNTAX §3.4）：receiver 必须 Nullable<T>；段在
        // 非空 T 上绑定（占位叶子承载 unwrap 后的 receiver，P4a 物化替换）；
        // 结果类型：成员类型已可空则原样（不二次包装），否则包 Nullable。
        // g10：内层放宽为 SemanticSymbol——Nullable<T>（T 为泛型参数）
        // 同样合法，占位叶子与结果类型均可持泛型参数
        private static BoundExpression? BindSafeSegment(PathSegmentASTNode segment,
            BoundExpression receiver, Scope scope, BindContext ctx, BindEnvironment env)
        {
            if (!SymbolLookup.IsNullableType(receiver.Type, env, out var element))
            {
                env.Error(segment.Span, $"Safe access '?.' requires a nullable receiver " +
                    $"(got '{BoundAnalysis.TypeDisplay(receiver.Type)}')");
                return null;
            }
            var placeholder = new BoundSafeAccessReceiverExpression(segment, element);
            // `?.` 结果是值不是赋值 place——段内折叠恒为读语义
            var access = BindInstanceSegment(segment, placeholder, scope, ctx, env,
                forAssignment: false);
            if (access == null) return null;
            // 结果类型：成员类型已可空则原样（不二次包装），否则包 Nullable；
            // 泛型参数成员类型无静态 Nullable 构造，原样保留（S9a）
            var resultType = access.Type switch
            {
                TypeSymbol accessType when accessType.ConstructedFrom
                    == env.B.NullableDefinition => (SemanticSymbol)accessType,
                TypeSymbol accessType => env.Unit.Symbols.GetNullable(accessType),
                _ => access.Type,
            };
            return new BoundSafeAccessExpression(segment, receiver, placeholder, access,
                resultType);
        }

        // 实例字段访问：receiver 按有效成员类型查找（T extends B 按 B）。
        // S8e 使用点检查同 BindFieldReference 口径（访问器读侧/字段可见性）
        public static BoundExpression? BindInstanceFieldAccess(ASTNode node,
            BoundExpression receiver, string name, BindEnvironment env, BindContext ctx,
            bool forAssignment = false)
        {
            if (receiver.Type is ErrorTypeSymbol) return null;
            var receiverType = SymbolLookup.EffectiveMemberType(receiver.Type, env);
            var field = SymbolLookup.FindInstanceField(receiverType, name);
            if (field == null)
            {
                env.Error(node.Span, SymbolLookup.FindInstanceMethods(receiverType, name,
                    env.Unit.Symbols).Count > 0
                    ? $"'{name}' on type '{BoundAnalysis.TypeDisplay(receiver.Type)}' is not a field"
                    : $"Undefined member '{name}' on type " +
                        $"'{BoundAnalysis.TypeDisplay(receiver.Type)}'");
                return null;
            }
            if (field.Getter != null || field.Setter != null)
            {
                if (!forAssignment && !CheckReadable(field, node.Span, ctx, env))
                {
                    return null;
                }
            }
            else if (!ctx.Frame.CanAccess(field))
            {
                env.Error(node.Span, AccessChecker.InaccessibleMessage(field));
                return null;
            }
            if (field.FieldType == null)
            {
                env.Error(node.Span, $"P3: field '{field.Name}' has no type annotation " +
                    "(field type inference is not supported yet)");
                return null;
            }
            // 泛型字段类型的最小替换（S7f 解构场景）：声明类型是宿主泛型
            // 参数时按 receiver 链上的构造类型取实参；receiver 定义级时
            // 原样保留（S9a 放行——引用相等身份）；返回值恒非空
            // （FieldType 非 null 上面已查，SymbolLookup 契约）
            var fieldType = SymbolLookup.SubstituteFieldType(field, receiverType,
                env.Unit.Symbols);
            var access = new BoundFieldAccessExpression(node, receiver, field, fieldType);
            // S8b：const 字段稳定链收窄（TryFromFieldAccess 含 IsNarrowable
            // 判定；不稳定链返回 null 直通）；赋值 place（forAssignment）
            // 不包——目标被 SmartCast 包装会落赋值 switch 的 default，
            // 顶替应有的 const/可写诊断（同 BindFieldReference 口径）
            if (forAssignment) return access;
            return ApplyNarrowing(node, access,
                NarrowKey.TryFromFieldAccess(receiver, field, ctx.Frame), ctx.Flow);
        }

        private static BoundExpression? ErrorAndNull(BindEnvironment env, CharRange? span,
            string message)
        {
            env.Error(span, message);
            return null;
        }

    }
}
