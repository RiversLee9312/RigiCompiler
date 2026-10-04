
namespace RigiCompiler
{
    internal static partial class PathFacility
    {
        // Values 职责；与主文件共享同一类型、字段及生命周期。
        // S8b smart cast：引用收窄查询——收窄事实表命中且类型确实变窄时
        // 包 BoundSmartCastExpression（P4a 物化为显式 cast）
        private static BoundExpression ApplyNarrowing(ASTNode node, BoundExpression bound,
            NarrowKey? key, FlowState flow)
        {
            if (key == null) return bound;
            var narrowed = flow.LookupNarrow(key);
            if (narrowed == null || ReferenceEquals(narrowed, bound.Type)) return bound;
            return new BoundSmartCastExpression(node, bound, narrowed);
        }

        // 跨簇收窄包装入口（CallVisitors 调用链头用；语义同 ApplyNarrowing）
        public static BoundExpression ApplyNarrowingPublic(ASTNode node, BoundExpression bound,
            NarrowKey? key, FlowState flow)
        {
            return ApplyNarrowing(node, bound, key, flow);
        }

        // 源码层有效 this 类型：lambda 取外层；实例方法取宿主自身具化
        // （泛型定义 Box → Box\<T\>）；静态上下文 null
        public static TypeSymbol? EffectiveThisType(BindContext ctx, BindEnvironment env)
        {
            if (ctx.IsLambda) return ctx.LambdaThisType;
            if (!ctx.Frame.HasThis) return null;
            return SymbolLookup.AsSelfConstructed(ctx.Frame.Method.Owner, env.Unit.Symbols);
        }

        // 类型槽位的值绑定（is/supers/with 动态右侧与 new 动态目标共用，
        // SYNTAX §3.5/§3.7）：不落袋纯查找，命中后正常构造值引用 bound
        // 节点——单段名 = 局部 → 参数 → 字段（FindField 全链）；多段路径 =
        // 容器（前 N-1 段静默解析）+ 末段字段。S9f：值路径元素带泛型实参
        // 不再按未命中处理——实参先经 NameResolver 静默解析（值路径无类型
        // 实参消费点，实参本身不参与绑定）；失败返回 null（由调用方统一
        // 诊断）。valueFound = 是否有值符号命中（命中但绑定失败时诊断已
        // 落袋，调用方不再重复报）
        public static BoundExpression? BindTypeSlotValue(ASTNode node,
            TypeReferenceASTNode typeRef, Scope scope, BindContext ctx, BindEnvironment env,
            out bool valueFound)
        {
            valueFound = false;
            var elements = typeRef.TypeSymbol.symbol.elements;
            foreach (var element in elements)
            {
                foreach (var generic in element.generics)
                {
                    // 实参是完整类型引用（g1）：静默试探同走 ResolveTypeReference
                    var resolved = env.Names.ResolveTypeReference(generic, ctx.Frame.FileCtx,
                        ctx.Frame.DeclaringType, ctx.Frame.Method, span: null,
                        reportErrors: false);
                    if (resolved == null || resolved is ErrorTypeSymbol) return null;
                }
            }
            var span = typeRef.Span ?? node.Span;
            if (elements.Count == 1)
            {
                var name = elements[0].name;
                var symbol = scope.LookupSymbol(name);
                if (symbol is LocalSymbol local)
                {
                    valueFound = true;
                    if (!ctx.Flow.IsAssigned(local))
                    {
                        env.Error(span, $"Use of unassigned local variable '{name}'");
                    }
                    // 源码局部 Type 恒非空（同路径绑定单段分支）
                    return new BoundValueReferenceExpression(typeRef, local, local.Type!);
                }
                var parameter = symbol as ParameterSymbol
                    ?? ctx.Frame.Method.Parameters.FirstOrDefault(p => p.Name == name);
                if (parameter != null)
                {
                    valueFound = true;
                    if (ctx.IsLambda && !ctx.LambdaParameters.Contains(parameter))
                        ctx.CapturedSymbols.Add(parameter);
                    // S9a 放行：参数类型可为泛型参数（引用相等身份）
                    return new BoundValueReferenceExpression(typeRef, parameter,
                        parameter.Type!);
                }
                var field = MemberLookup.FindField(name, ctx.Frame, env);
                if (field == null) return null;
                valueFound = true;
                return BindFieldReference(typeRef, field, ctx, env);
            }
            // 多段：前 N-1 段解析为容器（静默——失败由调用方统一诊断；
            // ResolveContainer 契约是传全段、内部取前 N-1 段），
            // 末段查字段成员（实例字段命中由 BindFieldReference 补 this）
            var container = MemberLookup.ResolveContainer(
                elements.Select(e => e.name).ToList(), null, ctx.Frame, env, reportErrors: false,
                allowBareGenericDefinition: true);
            if (container == null) return null;
            if (MemberLookup.FindMember(container, elements[^1].name) is not FieldSymbol memberField)
            {
                return null;
            }
            valueFound = true;
            if (StaticGenericRules.CheckConstructedStaticAccess(container,
                memberField.IsStatic, memberField.Name, typeRef.Span, env.Error))
            {
                return null;
            }
            return BindFieldReference(typeRef, memberField, ctx, env);
        }

        // 可变参数体内视角类型（S9d 修正，BIL §7.1；PathVisitors 首段参数
        // 与 CallVisitors 调用链头共用同一包装）：位置包 = Array\<元素类型\>
        // （与 .array<.any> 装箱往返自洽）；具名包 =
        // Array\<Pair\<String, 元素类型\>\>——§7.1 ABI 是
        // .array<.pair<.string, .any>>（名+值对序列），体内元素访问必须
        // 看到 Pair（元素 = core::Pair 构造，名 String + 值 T，名字信息
        // 不丢失）。core::Pair 缺席（无 stdlib 的测试驱动）时具名包降级
        // Array\<元素类型\>（P4 发射不依赖本视角，不阻断编译）
        public static SemanticSymbol VariadicParameterViewType(ParameterSymbol parameter,
            BindEnvironment env)
        {
            if (parameter.IsNamedVariadic)
            {
                var pairDefinition = FindCorePairDefinition(env);
                if (pairDefinition != null)
                {
                    var pairType = env.Unit.Symbols.GetConstructedType(pairDefinition,
                        env.B.String, parameter.Type!);
                    return env.Unit.Symbols.GetConstructedType(env.B.ArrayDefinition, pairType);
                }
            }
            return parameter.IsVariadic || parameter.IsNamedVariadic
                ? env.Unit.Symbols.GetConstructedType(env.B.ArrayDefinition, parameter.Type!)
                : parameter.Type!;
        }

        // core::Pair 定义查找（.bootstrap.rg 自举提供，按「名 + 泛型
        // 元数」查询；与 DeclarationVisitors.FindCorePairDefinition 同一
        // 查询——具名包体内视角缺失时降级而非诊断，故不共享带诊断版本）
        private static TypeSymbol? FindCorePairDefinition(BindEnvironment env)
        {
            var core = env.Unit.Symbols.GlobalNamespace.ChildNamespaces
                .FirstOrDefault(n => n.Name == "core");
            return core?.Types.FirstOrDefault(t => t.Name == "Pair"
                && t.GenericParameters.Count == 2);
        }

        // 赋值目标绑定入口（ExpressionStatementVisitor 专用）：
        // 定义而非「使用」——符号引用不经 unassigned 检查
        public static BoundExpression? VisitForAssignment(PathExpressionASTNode node, Scope scope,
            BindContext ctx, BindEnvironment env)
        {
            return BindPath(node, scope, ctx, env, forAssignment: true);
        }

        // M105 语句位置尾 Call 分流：判定路径是否以「值上的 Call 后缀」结尾
        //（FoldSuffixes 会处理的形态——非 CallForm 直写、非实例段首 Call
        // 方法调用）。命中时绑定去掉该后缀后的 receiver；形态不匹配返回
        // false（调用方走通用兜底）。绑定失败 receiver 为 null（诊断已落）。
        // 不新建 AST：临时摘除尾 Call 后缀复用 BindPath，finally 还原。
        public static bool TryBindReceiverBeforeTrailingValueCall(PathExpressionASTNode node,
            Scope scope, BindContext ctx, BindEnvironment env,
            out BoundExpression? receiver, out PathSuffixASTNode? trailingCall)
        {
            receiver = null;
            trailingCall = null;
            List<PathSuffixASTNode> suffixList;
            int callIndex;
            if (node.Segments.Count == 0)
            {
                if (node.Head.Suffixes.Count == 0) return false;
                if (node.Head.Suffixes[^1].Kind != PathSuffixKind.Call) return false;
                // 符号头 + 唯一 Call = CallForm 形态，由既有分流处理
                if (node.Head.Name != null && node.Head.Expression == null
                    && node.Head.Suffixes.Count == 1)
                {
                    return false;
                }
                // 前导点 enum case 底座 + Call 后缀（`.Failed(404)`，
                // §12.1）= 参数化 case 调用形态，归 enum case 通道
                //（BindExpressionBasePath 特判），不作尾 Call 分流
                if (node.Head.Expression?.Expression is EnumCaseExpressionASTNode)
                {
                    return false;
                }
                suffixList = node.Head.Suffixes;
                callIndex = suffixList.Count - 1;
            }
            else
            {
                var lastSeg = node.Segments[^1];
                // 末段仅一个 Call → 实例方法/CallForm 链形态，不分流
                if (lastSeg.Suffixes.Count < 2) return false;
                if (lastSeg.Suffixes[^1].Kind != PathSuffixKind.Call) return false;
                suffixList = lastSeg.Suffixes;
                callIndex = lastSeg.Suffixes.Count - 1;
            }
            trailingCall = suffixList[callIndex];
            suffixList.RemoveAt(callIndex);
            try
            {
                receiver = BindPath(node, scope, ctx, env, forAssignment: false);
            }
            finally
            {
                suffixList.Insert(callIndex, trailingCall);
            }
            return true;
        }

    }
}
