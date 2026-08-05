namespace LatteCompiler
{
    // cast（S7e，SYNTAX §3.5）：as / as?。as 结果类型即目标类型，as? 结果
    // 类型 = Nullable<目标类型>（P3 定型，P4 不再区分包装）。可转性
    // 不做静态拒绝（as 失败是运行时 core.CastException）；S8f 起执行
    // castTo/castFrom 名字分析（转换优先级：源类型 castTo → 目标类型
    // castFrom，BIL §12.1 语义第 1、2 条——适用候选记录在
    // BoundCastExpression.Conversion，P4 仍发 cast，运行时自行分派；
    // 均无适用候选 = 内建引用视图/数值转换，第 3 条兜底）。
    // ErrorType 毒化静默（结果沿用 ErrorType）。
    // 自旧 BindSession.BindCast 迁移，行为不变（S8f 增补名字分析）。
    internal sealed class CastVisitor : ExpressionVisitor<CastVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            var cast = (CastExpressionASTNode)node;
            var source = ExpressionDispatcher.Visit(cast.Object.Expression, scope, ctx, env);
            var targetType = TypeReferences.Resolve(cast.TargetType, cast.Span, ctx.Frame, env);
            if (source == null || targetType == null) return null;
            // S9a：目标为泛型参数时 as? 无静态 Nullable 构造——结果类型
            // 保守取 T 自身（运行时按 typeid 判定可空）
            var resultType = targetType is ErrorTypeSymbol
                ? targetType
                : cast.IsSafe && targetType is TypeSymbol nullableTarget
                    ? env.Unit.Symbols.GetNullable(nullableTarget)
                    : targetType;
            var conversion = targetType is ErrorTypeSymbol || source.Type is ErrorTypeSymbol
                ? null
                : targetType is TypeSymbol conversionTarget
                    ? ResolveConversion(source, conversionTarget, env)
                    : null;
            return new BoundCastExpression(node, source, targetType, cast.IsSafe, resultType,
                conversion);
        }

        // 名字分析（S8f，SYNTAX §3.5 转换优先级）：源类型的 castTo 优先，
        // 目标类型的 castFrom 兜底；均无适用候选返回 null（内建兜底）。
        // 适用判定 = 单泛型参数代入后的签名匹配（SymbolLookup 查询）。
        // S9a：源类型为泛型参数时跳过名字分析（内建兜底——判型短路）
        private static MethodSymbol? ResolveConversion(BoundExpression source,
            TypeSymbol targetType, BindEnvironment env)
        {
            if (source.Type is not TypeSymbol sourceType) return null;
            var symbols = env.Unit.Symbols;
            var castTo = SymbolLookup.FindConversionOperator(sourceType, "castTo", 0,
                targetType, targetType, symbols);
            if (castTo != null) return castTo;
            return SymbolLookup.FindConversionOperator(targetType, "castFrom", 1,
                sourceType, targetType, symbols);
        }
    }

    // is / supers / with（S8a，SYNTAX §3.5；BIL §12.3）：右侧双形态——
    // 类型引用（静态）或 Type\<T\> 值（动态）。解析顺序：先按类型引用解析
    // （reportErrors: false 不落袋试探），失败再按值绑定；结果恒 bool。
    // 不做静态不可能性拒绝（12 is String 不报错，运行时判定）。
    // 自旧 BindSession.BindTypeCheck 迁移，行为不变。
    // S11 增补第三右侧形态：is .Case 前导点 enum case 判别匹配（§12.3）。
    internal sealed class TypeCheckVisitor : ExpressionVisitor<TypeCheckVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            var check = (TypeCheckExpressionASTNode)node;
            var operand = ExpressionDispatcher.Visit(check.Object.Expression, scope, ctx, env);
            // is .Case（S11，SYNTAX §12.3）：前导点 enum case 判别匹配——
            // 只查隐藏判别字段，不比较 payload，也不改变值的静态类型
            // （Kind = IsCase + Case 第三槽；不触发 smart cast——收窄事实
            // 只匹配静态 Is 形态，ConditionFactsExtractor）
            if (check.TargetCase != null)
            {
                if (operand == null || operand.Type is ErrorTypeSymbol) return null;
                // 操作数静态类型（定义级）必须是 enum struct；泛型构造归口
                //（S11 范围决策——声明侧模板绑定同步跳过）
                if (operand.Type is TypeSymbol { ConstructedFrom: not null } constructedOperand
                    && constructedOperand.ConstructedFrom.Kind == TypeKind.EnumStruct)
                {
                    env.Error(check.Span, "P3: generic enum cases are not supported yet (S11)");
                    return null;
                }
                if (operand.Type is not TypeSymbol operandType
                    || operandType.Kind != TypeKind.EnumStruct)
                {
                    env.Error(check.Span, $"Left operand of 'is .Case' must be an enum struct " +
                        $"type (got '{BoundAnalysis.TypeDisplay(operand.Type)}')");
                    return null;
                }
                // case 名解析（模板绑定未落定不拦截——判别比较不消费模板
                // 产物；声明点诊断已报，不二次报）
                var targetCase = EnumCaseFacility.FindCase(operandType, check.TargetCase.CaseName,
                    check.TargetCase.Span ?? check.Span, env);
                if (targetCase == null) return null;
                return new BoundTypeCheckExpression(node, BoundTypeCheckKind.IsCase, operand,
                    null, null, env.B.Bool, targetCase);
            }
            var kind = check.Operator switch
            {
                "is" => BoundTypeCheckKind.Is,
                "supers" => BoundTypeCheckKind.Supers,
                "with" => BoundTypeCheckKind.With,
                _ => throw new CompilerInternalException("未知类型检查运算符: " + check.Operator),
            };
            var typeRef = check.TargetType!;
            var span = typeRef.Span ?? check.Span;
            // 静态形态试探：按类型引用解析（不落袋；失败返回 ErrorType
            // 毒化符号——TypeSymbol 子类，必须显式排除才落入动态形态）。
            // T? 目标包装 Nullable\<T\>（与 NameResolver.ResolveTypeReference
            // 同规则）
            var probed = env.Names.ResolveSymbolPath(typeRef.TypeSymbol.symbol, ctx.Frame.FileCtx,
                ctx.Frame.DeclaringType, ctx.Frame.Method, allowImports: true, reportErrors: false, span: null);
            // S9a 放行：右侧为泛型参数时作静态目标（is/supers/with 均合法
            // ——运行时按 T 的 typeid 判定；with 不做 wrapper 静态拒绝）
            if (probed is GenericParameterSymbol genericTarget)
            {
                if (operand == null) return null;
                return new BoundTypeCheckExpression(node, kind, operand, genericTarget, null,
                    env.B.Bool);
            }
            if (probed is TypeSymbol targetType && probed is not ErrorTypeSymbol)
            {
                if (typeRef.IsNullable)
                {
                    targetType = env.Unit.Symbols.GetNullable(targetType);
                }
                // with 的静态目标必须声明为 wrapper（is/supers 任意类型）
                if (kind == BoundTypeCheckKind.With && targetType.Kind != TypeKind.Wrapper)
                {
                    env.Error(span, $"'with' target must be a wrapper type: " +
                        $"'{BoundAnalysis.TypeDisplay(targetType)}'");
                    return null;
                }
                if (operand == null) return null;
                return new BoundTypeCheckExpression(node, kind, operand, targetType, null, env.B.Bool);
            }
            // 动态形态：右侧按值绑定，值必须承载 Type\<T\>（BIL §12.3
            // .indirect 指令的 TYPEID_VAR 操作数）；ErrorType 毒化静默放行
            var targetValue = BindTargetValue(check, typeRef, scope, ctx, env, out var valueFound);
            if (targetValue == null)
            {
                // 值符号命中但绑定失败时诊断已落袋，不重复报
                if (!valueFound)
                {
                    env.Error(span, TargetMessage(check));
                }
                return null;
            }
            // S9a：泛型参数值无 Type\<T\> 构造展开（判型后不命中即报目标诊断）
            if (targetValue.Type is not ErrorTypeSymbol
                && (targetValue.Type is not TypeSymbol { ConstructedFrom: { } targetValueType }
                    || !ReferenceEquals(targetValueType, env.B.TypeDefinition)))
            {
                env.Error(span, TargetMessage(check));
                return null;
            }
            if (operand == null) return null;
            return new BoundTypeCheckExpression(node, kind, operand, null, targetValue, env.B.Bool);
        }

        // 动态形态右侧的值绑定（不落袋纯查找，命中后正常构造值引用 bound
        // 节点）：单段名 = 局部 → 参数 → 字段（FindField 全链）；多段路径 =
        // 容器（前 N-1 段静默解析）+ 末段字段。S9f 解开 #18②：值路径元素
        // 带泛型实参不再按未命中处理——实参先经 NameResolver 静默解析
        // （M69 使用侧泛型已落地），成功即正常绑定值（值路径无类型实参
        // 消费点，实参本身不参与绑定）；失败返回 null（落统一目标诊断）。
        // valueFound = 是否有值符号命中（命中但绑定失败时诊断已落袋，
        // 调用方不再重复报）
        private static BoundExpression? BindTargetValue(TypeCheckExpressionASTNode node,
            TypeReferenceASTNode typeRef, Scope scope, BindContext ctx, BindEnvironment env,
            out bool valueFound)
        {
            valueFound = false;
            var elements = typeRef.TypeSymbol.symbol.elements;
            foreach (var element in elements)
            {
                foreach (var generic in element.generics)
                {
                    var resolved = env.Names.ResolveSymbolPath(generic, ctx.Frame.FileCtx,
                        ctx.Frame.DeclaringType, ctx.Frame.Method, allowImports: true,
                        reportErrors: false, span: null);
                    if (resolved == null || resolved is ErrorTypeSymbol) return null;
                }
            }
            var span = typeRef.Span ?? node.Span;
            if (elements.Count == 1)
            {
                var name = elements[0].name;
                var local = scope.Lookup(name);
                if (local != null)
                {
                    valueFound = true;
                    if (!ctx.Flow.IsAssigned(local))
                    {
                        env.Error(span, $"Use of unassigned local variable '{name}'");
                    }
                    // 源码局部 Type 恒非空（同路径绑定单段分支）
                    return new BoundValueReferenceExpression(typeRef, local, local.Type!);
                }
                var parameter = ctx.Frame.Method.Parameters.FirstOrDefault(p => p.Name == name);
                if (parameter != null)
                {
                    valueFound = true;
                    // S9a 放行：参数类型可为泛型参数（引用相等身份）
                    return new BoundValueReferenceExpression(typeRef, parameter,
                        parameter.Type!);
                }
                var field = MemberLookup.FindField(name, ctx.Frame, env);
                if (field == null) return null;
                valueFound = true;
                return PathFacility.BindFieldReference(typeRef, field, ctx, env);
            }
            // 多段：前 N-1 段解析为容器（静默——失败由调用方统一诊断；
            // ResolveContainer 契约是传全段、内部取前 N-1 段），
            // 末段查字段成员（实例字段命中由 BindFieldReference 补 this）
            var container = MemberLookup.ResolveContainer(
                elements.Select(e => e.name).ToList(), null, ctx.Frame, env, reportErrors: false);
            if (container == null) return null;
            if (MemberLookup.FindMember(container, elements[^1].name) is not FieldSymbol memberField)
            {
                return null;
            }
            valueFound = true;
            return PathFacility.BindFieldReference(typeRef, memberField, ctx, env);
        }

        // 动态形态失败（非类型也非 Type\<T\> 值）的统一诊断消息（附路径原文）
        private static string TargetMessage(TypeCheckExpressionASTNode node)
        {
            return $"right side of '{node.Operator}' must be a type or " +
                $"a Type\\<T\\> value: " +
                $"'{NameResolver.PathText(node.TargetType!.TypeSymbol.symbol)}'";
        }
    }

    // typeOf（S8a，SYNTAX §3.7；BIL §12.5）：双形态——值形态取操作数的
    // 运行时实际类型，静态定型 Type\<操作数静态类型\>；类型形态取类型
    // 本身的 Type 值，静态定型 Type\<T\>。操作数解析顺序：先值后类型
    // （单段裸名两可时值优先——局部/参数/字段遮蔽同名类型）。
    // 自旧 BindSession.BindTypeOf 迁移，行为不变。
    internal sealed class TypeOfVisitor : ExpressionVisitor<TypeOfVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            var typeOf = (TypeOfExpressionASTNode)node;
            // 单段裸名路径（无泛型/无后缀/无段，this 除外）做不落袋分类；
            // 其余形态一律值形态直通
            if (typeOf.Operand.Expression is PathExpressionASTNode path
                && path.Head.Name != null && path.Head.Name != "this"
                && path.Head.GenericArguments.Count == 0 && path.Head.Suffixes.Count == 0
                && path.Segments.Count == 0)
            {
                var name = path.Head.Name;
                bool isValue = scope.Lookup(name) != null
                    || ctx.Frame.Method.Parameters.Any(p => p.Name == name)
                    || MemberLookup.FindField(name, ctx.Frame, env) != null;
                if (!isValue)
                {
                    // 值未命中 → 试探类型解析（不落袋），命中即类型形态
                    var head = new Symbol();
                    head.elements.Add(new SymbolElement { name = name });
                    var probed = env.Names.ResolveSymbolPath(head, ctx.Frame.FileCtx, ctx.Frame.DeclaringType,
                        ctx.Frame.Method, allowImports: true, reportErrors: false, span: null);
                    // S9a 放行：typeOf 类型形态命中泛型参数（T → Type\<T\> 构造）
                    if (probed is GenericParameterSymbol genericTarget)
                    {
                        return new BoundTypeOfExpression(node, null, genericTarget,
                            ResultType(genericTarget, env));
                    }
                    // 失败返回 ErrorType 毒化符号（TypeSymbol 子类，必须
                    // 显式排除才会落入下方路径绑定的「未解析」诊断）
                    if (probed is TypeSymbol targetType && probed is not ErrorTypeSymbol)
                    {
                        return new BoundTypeOfExpression(node, null, targetType,
                            ResultType(targetType, env));
                    }
                    // 值/类型都未命中：落入下方路径绑定自然报「未解析」
                }
                var bound = ExpressionDispatcher.Visit(path, scope, ctx, env);
                if (bound == null) return null;
                return new BoundTypeOfExpression(node, bound, null, ResultType(bound.Type, env));
            }
            var value = ExpressionDispatcher.Visit(typeOf.Operand.Expression, scope, ctx, env);
            if (value == null) return null;
            return new BoundTypeOfExpression(node, value, null, ResultType(value.Type, env));
        }

        // typeOf 结果类型：Type\<T\> 构造类型；ErrorType 毒化静默
        // （结果沿用 ErrorType，同 cast）。S9a 放宽为 SemanticSymbol：
        // 泛型参数实参构造 Type\<T\>（GetConstructedType 实参可含泛型参数）
        private static TypeSymbol ResultType(SemanticSymbol element, BindEnvironment env)
        {
            if (element is ErrorTypeSymbol error) return error;
            return env.Unit.Symbols.GetConstructedType(env.B.TypeDefinition, element);
        }
    }
}
