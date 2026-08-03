namespace LatteCompiler
{
    // cast（S7e，SYNTAX §3.5）：as / as?。as 结果类型即目标类型，as? 结果
    // 类型 = Nullable<目标类型>（P3 定型，P4 不再区分包装）。可转性
    // 不做静态拒绝（as 失败是运行时 core.CastException；castTo/castFrom
    // 名字分析归 S8f）；ErrorType 毒化静默（结果沿用 ErrorType）。
    // 自旧 BindSession.BindCast 迁移，行为不变。
    internal sealed class CastVisitor : ExpressionVisitor<CastVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            var cast = (CastExpressionASTNode)node;
            var source = ExpressionDispatcher.Visit(cast.Object.Expression, scope, ctx, env);
            var targetType = TypeReferences.Resolve(cast.TargetType, cast.Span, ctx, env);
            if (source == null || targetType == null) return null;
            var resultType = targetType is ErrorTypeSymbol
                ? targetType
                : cast.IsSafe ? env.Unit.Symbols.GetNullable(targetType) : targetType;
            return new BoundCastExpression(node, source, targetType, cast.IsSafe, resultType);
        }
    }

    // is / supers / with（S8a，SYNTAX §3.5；BIL §12.3）：右侧双形态——
    // 类型引用（静态）或 Type\<T\> 值（动态）。解析顺序：先按类型引用解析
    // （reportErrors: false 不落袋试探），失败再按值绑定；结果恒 bool。
    // 不做静态不可能性拒绝（12 is String 不报错，运行时判定）。
    // 自旧 BindSession.BindTypeCheck 迁移，行为不变。
    internal sealed class TypeCheckVisitor : ExpressionVisitor<TypeCheckVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            var check = (TypeCheckExpressionASTNode)node;
            var operand = ExpressionDispatcher.Visit(check.Object.Expression, scope, ctx, env);
            // is .Case（前导点 enum case 匹配）归 S11
            if (check.TargetCase != null)
            {
                env.Error(check.Span, "P3: enum case is pattern is not supported yet (S11)");
                return null;
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
            var probed = env.Names.ResolveSymbolPath(typeRef.TypeSymbol.symbol, ctx.FileCtx,
                ctx.DeclaringType, ctx.Method, allowImports: true, reportErrors: false, span: null);
            if (probed is GenericParameterSymbol)
            {
                env.Error(span, "P3: generic type parameters are not supported yet (S9)");
                return null;
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
            if (targetValue.Type is not ErrorTypeSymbol
                && !ReferenceEquals(targetValue.Type.ConstructedFrom, env.B.TypeDefinition))
            {
                env.Error(span, TargetMessage(check));
                return null;
            }
            if (operand == null) return null;
            return new BoundTypeCheckExpression(node, kind, operand, null, targetValue, env.B.Bool);
        }

        // 动态形态右侧的值绑定（不落袋纯查找，命中后正常构造值引用 bound
        // 节点）：单段名 = 局部 → 参数 → 字段（FindField 全链）；多段路径 =
        // 容器（前 N-1 段静默解析）+ 末段字段。路径带泛型实参按未命中处理
        // （使用侧泛型归 S9）。valueFound = 是否有值符号命中（命中但绑定
        // 失败时诊断已落袋，调用方不再重复报）
        private static BoundExpression? BindTargetValue(TypeCheckExpressionASTNode node,
            TypeReferenceASTNode typeRef, Scope scope, BindContext ctx, BindEnvironment env,
            out bool valueFound)
        {
            valueFound = false;
            var elements = typeRef.TypeSymbol.symbol.elements;
            if (elements.Any(e => e.generics.Count > 0)) return null;
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
                var parameter = ctx.Method.Parameters.FirstOrDefault(p => p.Name == name);
                if (parameter != null)
                {
                    valueFound = true;
                    if (parameter.Type is not TypeSymbol paramType)
                    {
                        env.Error(span, "P3: generic type parameters are not supported yet (S9)");
                        return null;
                    }
                    return new BoundValueReferenceExpression(typeRef, parameter, paramType);
                }
                var field = MemberLookup.FindField(name, ctx, env);
                if (field == null) return null;
                valueFound = true;
                return PathFacility.BindFieldReference(typeRef, field, ctx, env);
            }
            // 多段：前 N-1 段解析为容器（静默——失败由调用方统一诊断；
            // ResolveContainer 契约是传全段、内部取前 N-1 段），
            // 末段查字段成员（实例字段命中由 BindFieldReference 补 this）
            var container = MemberLookup.ResolveContainer(
                elements.Select(e => e.name).ToList(), null, ctx, env, reportErrors: false);
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
                    || ctx.Method.Parameters.Any(p => p.Name == name)
                    || MemberLookup.FindField(name, ctx, env) != null;
                if (!isValue)
                {
                    // 值未命中 → 试探类型解析（不落袋），命中即类型形态
                    var head = new Symbol();
                    head.elements.Add(new SymbolElement { name = name });
                    var probed = env.Names.ResolveSymbolPath(head, ctx.FileCtx, ctx.DeclaringType,
                        ctx.Method, allowImports: true, reportErrors: false, span: null);
                    if (probed is GenericParameterSymbol)
                    {
                        env.Error(path.Span ?? typeOf.Span,
                            "P3: generic type parameters are not supported yet (S9)");
                        return null;
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
        // （结果沿用 ErrorType，同 cast）
        private static TypeSymbol ResultType(TypeSymbol element, BindEnvironment env)
        {
            return element is ErrorTypeSymbol
                ? element
                : env.Unit.Symbols.GetConstructedType(env.B.TypeDefinition, element);
        }
    }
}
