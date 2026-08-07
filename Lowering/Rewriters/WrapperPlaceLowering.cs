namespace LatteCompiler
{
    // wrapper place 降级设施（S11c/M84，SYNTAX §14.5 + BIL §12.4/§13.3）：
    // 使用点与 proxy 体内共用同一 lowering 路径。
    //
    // 形态矩阵（按应用类别分派——应用记录的宿主形态决定可用指令）：
    // - Entity 应用：成员读/方法调用/索引读 = get.wrapper 值拷贝 +
    //   普通 get.field/invoke/get.array；字段写 = set.wrapper.field 链；
    // - 字段-Value 应用：字段读 = get.wrapper.field 值拷贝 + 普通
    //   get.field；字段写 = set.wrapper.field 链（PlaceChain =
    //   field(HOST_FIELD)+wrapper(W) 相邻对）；方法调用/索引读 =
    //   get.wrapper.field 值拷贝后复用普通 invoke/get.array；索引写显式拒绝；
    // - 嵌套 wrapper 链：逐层 Materialize 值拷贝后继续普通 get.field；
    // - 局部/静态：统一归口（栈帧/静态存储合成归 Middleware）；
    // - 深层纯字段写穿 place.a.b... = rhs：P4a 多 get/set（正向 get +
    //   叶写 + 反向 set；值类型中间写回；引用中间停止；最外层必要
    //   写回复用 set.wrapper.field；普通值中间反向写回发 set.field）。
    internal static class WrapperPlaceLowering
    {
        // ===== 类别判定（应用记录归属：类型应用 vs 字段/局部/静态应用）=====
        private static bool IsFieldApplication(BoundWrapperAccessExpression place)
        {
            return place.Receiver is BoundFieldAccessExpression fieldAccess
                && fieldAccess.Field.AppliedWrappers.Contains(place.Application);
        }

        private static bool IsLocalOrStaticApplication(BoundWrapperAccessExpression place)
        {
            return place.Receiver switch
            {
                BoundValueReferenceExpression { Symbol: LocalSymbol local } =>
                    local.AppliedWrappers.Contains(place.Application),
                BoundFieldReferenceExpression fieldReference =>
                    fieldReference.Field.AppliedWrappers.Contains(place.Application),
                _ => false,
            };
        }

        // ===== 值拷贝物化（Entity = get.wrapper；字段-Value = get.wrapper.field）=====
        // 方法调用/索引读的接收者物化。sharedHost 非 null 时在终极宿主
        // 层直接使用（复合赋值/深写的宿主单次求值共享）
        public static LoweredExpression? Materialize(BoundWrapperAccessExpression place,
            LoweredExpression? sharedHost, LowerContext ctx, LowerEnvironment env)
        {
            if (IsLocalOrStaticApplication(place))
            {
                env.Error(place.Syntax.Span,
                    "P4: local/static wrapper place storage is not supported yet (S11)");
                return null;
            }
            if (IsFieldApplication(place))
            {
                var fieldAccess = (BoundFieldAccessExpression)place.Receiver;
                var hostValue = MaterializeHost(fieldAccess.Receiver, sharedHost, ctx, env);
                if (hostValue == null) return null;
                var local = ctx.Synth.NewSynthLocal(place.Wrapper);
                ctx.Output.Add(new LoweredAssignmentStatement(place,
                    SynthLocalFactory.ReferenceTo(place, local),
                    new LoweredGetFieldWrapperExpression(place, hostValue, fieldAccess.Field,
                        place.Wrapper)));
                return SynthLocalFactory.ReferenceTo(place, local);
            }
            // Entity 应用：Receiver 可为嵌套 place 或普通宿主
            var source = MaterializeHost(place.Receiver, sharedHost, ctx, env);
            if (source == null) return null;
            var entityLocal = ctx.Synth.NewSynthLocal(place.Wrapper);
            ctx.Output.Add(new LoweredAssignmentStatement(place,
                SynthLocalFactory.ReferenceTo(place, entityLocal),
                new LoweredGetWrapperExpression(place, source, place.Wrapper)));
            return SynthLocalFactory.ReferenceTo(place, entityLocal);
        }

        // 宿主表达式物化：嵌套 place 递归 Materialize；否则 sharedHost 或 Visit
        private static LoweredExpression? MaterializeHost(BoundExpression host,
            LoweredExpression? sharedHost, LowerContext ctx, LowerEnvironment env)
        {
            if (host is BoundWrapperAccessExpression nested)
            {
                return Materialize(nested, sharedHost, ctx, env);
            }
            if (sharedHost != null) return sharedHost;
            return LowerExpressionDispatcher.Visit(host, ctx, env);
        }

        // ===== 字段读：一律 Materialize(place) + 普通 get.field =====
        // Entity = get.wrapper；字段-Value = get.wrapper.field；嵌套链逐层物化
        public static LoweredExpression? LowerFieldRead(BoundFieldAccessExpression access,
            BoundWrapperAccessExpression place, LoweredExpression? sharedHost,
            LowerContext ctx, LowerEnvironment env)
        {
            var materialized = Materialize(place, sharedHost, ctx, env);
            if (materialized == null) return null;
            return new LoweredFieldAccessExpression(access, materialized, access.Field);
        }

        // ===== set.wrapper.field 写 place 构造（仅写侧）=====
        // PlaceChain 最外层→最内层：Entity 应用只加 W；字段-Value 应用加
        // HOST_FIELD 再加 W（相邻对，同 owner 多字段同 W 可区分）
        public static LoweredWrapperFieldExpression? BuildWrapperFieldPlace(BoundNode origin,
            BoundWrapperAccessExpression place, FieldSymbol targetField,
            LoweredExpression? sharedHost, LowerContext ctx, LowerEnvironment env)
        {
            var places = new List<BoundWrapperAccessExpression>();
            BoundWrapperAccessExpression current = place;
            while (true)
            {
                if (IsLocalOrStaticApplication(current))
                {
                    env.Error(current.Syntax.Span,
                        "P4: local/static wrapper place storage is not supported yet (S11)");
                    return null;
                }
                places.Add(current);
                var host = HostOf(current);
                if (host is BoundWrapperAccessExpression nested)
                {
                    current = nested;
                    continue;
                }
                break;
            }
            places.Reverse();
            var chain = new List<SemanticSymbol>(places.Count * 2);
            foreach (var p in places)
            {
                if (IsFieldApplication(p))
                {
                    chain.Add(((BoundFieldAccessExpression)p.Receiver).Field);
                }
                chain.Add(p.Wrapper);
            }
            var hostExpr = sharedHost ?? LowerExpressionDispatcher.Visit(
                UltimateHostExpression(place), ctx, env);
            if (hostExpr == null) return null;
            return new LoweredWrapperFieldExpression(origin, hostExpr, chain, targetField,
                targetField.FieldType!);
        }

        private static BoundExpression HostOf(BoundWrapperAccessExpression place)
        {
            return IsFieldApplication(place)
                ? ((BoundFieldAccessExpression)place.Receiver).Receiver
                : place.Receiver;
        }

        public static BoundExpression UltimateHostExpression(BoundWrapperAccessExpression place)
        {
            BoundExpression host = place;
            while (host is BoundWrapperAccessExpression current)
            {
                host = HostOf(current);
            }
            return host;
        }

        // ===== 赋值目标拦截 =====
        // 目标为 wrapper place 直接字段（place.field）时返回该字段访问
        public static BoundFieldAccessExpression? DirectPlaceFieldTarget(BoundExpression target)
        {
            return target is BoundFieldAccessExpression access
                && access.Receiver is BoundWrapperAccessExpression ? access : null;
        }

        // 深层纯字段链 place.a.b...（至少两层字段；含索引则 false）
        public static bool TryDeepFieldWriteTarget(BoundExpression target,
            out BoundWrapperAccessExpression place, out List<BoundFieldAccessExpression> chain)
        {
            place = null!;
            chain = new List<BoundFieldAccessExpression>();
            BoundExpression current = target;
            while (current is BoundFieldAccessExpression fieldAccess)
            {
                chain.Add(fieldAccess);
                current = fieldAccess.Receiver;
            }
            if (current is not BoundWrapperAccessExpression wrapperPlace || chain.Count < 2)
            {
                chain.Clear();
                return false;
            }
            place = wrapperPlace;
            chain.Reverse();
            return true;
        }

        // 目标表达式 receiver 链深处含 wrapper place（非直接字段目标）
        public static bool ContainsPlaceInTarget(BoundExpression target)
        {
            BoundExpression current = target;
            while (true)
            {
                switch (current)
                {
                    case BoundFieldAccessExpression fieldAccess:
                        current = fieldAccess.Receiver;
                        break;
                    case BoundIndexExpression indexAccess:
                        if (indexAccess.Receiver is BoundWrapperAccessExpression) return true;
                        current = indexAccess.Receiver;
                        break;
                    case BoundSmartCastExpression smartCast:
                        current = smartCast.Operand;
                        break;
                    default:
                        return current is BoundWrapperAccessExpression;
                }
            }
        }

        public static void UnsupportedWrite(BoundNode node, LowerEnvironment env)
        {
            env.Error(node.Syntax.Span,
                "P4: writes through wrapper place member or index chains are not " +
                "supported yet (S11)");
        }

        // 共享写路径终极宿主物化（深写/深层复合/直接 wrapper 字段复合共用）：
        // 读路径与写回路径共用同一 host 引用，RHS 可能替换可变字段，故仅真正
        // 稳定的局部/参数引用与 this 可直通；字段引用/字段访问（即便无 getter）、
        // 调用、索引、字面量/常量等一律 RHS 前写合成局部（§13.2 单次固定）。
        // 不改变一般 CompoundAssignmentRewriter 的 IsSideEffectFree 全局策略。
        public static LoweredExpression MaterializeSharedWriteHost(BoundNode origin,
            LoweredExpression host, LowerContext ctx)
        {
            if (host is LoweredValueReferenceExpression or LoweredThisExpression)
            {
                return host;
            }
            var hostLocal = ctx.Synth.NewSynthLocal(host.Type);
            ctx.Output.Add(new LoweredAssignmentStatement(origin,
                SynthLocalFactory.ReferenceTo(origin, hostLocal), host));
            return SynthLocalFactory.ReferenceTo(origin, hostLocal);
        }

        // ===== 深层纯字段写穿（P4a 展开：正向 get + 叶写 + 反向 set）=====
        // 求值序：终极宿主 → 正向各字段 get（各一次）→ RHS → 叶写 → 反向写回。
        // 返回完成的赋值语句（叶写）；写回作为前置语句落入 Output。
        // 失败时已落诊断并返回 null。
        public static LoweredStatement? LowerDeepFieldWrite(BoundNode origin,
            BoundWrapperAccessExpression place, List<BoundFieldAccessExpression> chain,
            BoundExpression rhsBound, LowerContext ctx, LowerEnvironment env)
        {
            // 1. 终极宿主单次求值（共享写路径：字段宿主亦物化）
            var hostBound = UltimateHostExpression(place);
            var host = LowerExpressionDispatcher.Visit(hostBound, ctx, env);
            if (host == null) return null;
            host = MaterializeSharedWriteHost(hostBound, host, ctx);

            // 2. 正向：首字段经 LowerFieldRead；其后逐字段 get 并物化局部
            var intermediates = new List<(LoweredExpression Local, FieldSymbol Field,
                BoundFieldAccessExpression Access)>();
            var firstAccess = chain[0];
            var firstRead = LowerFieldRead(firstAccess, place, host, ctx, env);
            if (firstRead == null) return null;
            var firstLocal = MaterializeInto(firstAccess, firstRead, ctx);
            intermediates.Add((firstLocal, firstAccess.Field, firstAccess));

            for (var i = 1; i < chain.Count - 1; i++)
            {
                var access = chain[i];
                var read = new LoweredFieldAccessExpression(access, intermediates[i - 1].Local,
                    access.Field);
                var local = MaterializeInto(access, read, ctx);
                intermediates.Add((local, access.Field, access));
            }

            var leafAccess = chain[chain.Count - 1];
            var leafParent = intermediates[intermediates.Count - 1].Local;
            var leafPlace = new LoweredFieldAccessExpression(leafAccess, leafParent,
                leafAccess.Field);

            // 3. RHS（目标 receiver 已全部求值）
            var rhs = LowerExpressionDispatcher.Visit(rhsBound, ctx, env);
            if (rhs == null) return null;
            rhs = LoweringFacility.EnsureDeclaredType(origin, rhs, leafAccess.Type);

            // 4. 叶写 + 5. 反向写回（叶写必须先于写回；返回值由调用方
            //    追加到 Output，故无写回时直接返回叶写，有写回时叶写先
            //    Output.Add、末条写回作返回值——块收集序 = 前缀 + 返回）
            var leafWrite = new LoweredAssignmentStatement(origin, leafPlace, rhs);
            var writebacks = new List<LoweredStatement>();
            for (var i = intermediates.Count - 1; i >= 0; i--)
            {
                var (local, field, access) = intermediates[i];
                var valueKind = ClassifyWritebackType(local.Type, access.Syntax.Span, env);
                if (valueKind == null) return null;
                if (valueKind == false) break;
                if (!CheckWritebackWritable(field, access.Syntax.Span, ctx, env))
                {
                    return null;
                }
                if (i == 0)
                {
                    var writePlace = BuildWrapperFieldPlace(access, place, field, host, ctx, env);
                    if (writePlace == null) return null;
                    writebacks.Add(new LoweredAssignmentStatement(origin, writePlace, local));
                }
                else
                {
                    var parentLocal = intermediates[i - 1].Local;
                    var parentPlace = new LoweredFieldAccessExpression(access, parentLocal, field);
                    writebacks.Add(new LoweredAssignmentStatement(origin, parentPlace, local));
                }
            }
            if (writebacks.Count == 0) return leafWrite;
            ctx.Output.Add(leafWrite);
            for (var i = 0; i < writebacks.Count - 1; i++)
            {
                ctx.Output.Add(writebacks[i]);
            }
            return writebacks[writebacks.Count - 1];
        }

        // 深层复合赋值 place.a.b op= rhs：读叶 → 运算 → 叶写 → 同反向写回
        public static LoweredExpression? LowerDeepFieldCompound(
            BoundCompoundAssignmentExpression compound, BoundWrapperAccessExpression place,
            List<BoundFieldAccessExpression> chain, LowerContext ctx, LowerEnvironment env)
        {
            var hostBound = UltimateHostExpression(place);
            var host = LowerExpressionDispatcher.Visit(hostBound, ctx, env);
            if (host == null) return null;
            host = MaterializeSharedWriteHost(hostBound, host, ctx);

            var intermediates = new List<(LoweredExpression Local, FieldSymbol Field,
                BoundFieldAccessExpression Access)>();
            var firstAccess = chain[0];
            var firstRead = LowerFieldRead(firstAccess, place, host, ctx, env);
            if (firstRead == null) return null;
            var firstLocal = MaterializeInto(firstAccess, firstRead, ctx);
            intermediates.Add((firstLocal, firstAccess.Field, firstAccess));

            for (var i = 1; i < chain.Count - 1; i++)
            {
                var access = chain[i];
                var read = new LoweredFieldAccessExpression(access, intermediates[i - 1].Local,
                    access.Field);
                var local = MaterializeInto(access, read, ctx);
                intermediates.Add((local, access.Field, access));
            }

            var leafAccess = chain[chain.Count - 1];
            var leafParent = intermediates[intermediates.Count - 1].Local;
            var leafRead = new LoweredFieldAccessExpression(leafAccess, leafParent,
                leafAccess.Field);

            var value = LowerExpressionDispatcher.Visit(compound.Value, ctx, env);
            if (value == null) return null;
            value = LoweringFacility.EnsureDeclaredType(compound, value, leafAccess.Type);
            LoweredExpression binary = new LoweredBinaryExpression(compound, compound.Op,
                leafRead, value);
            binary = LoweringFacility.EnsureDeclaredType(compound, binary, leafAccess.Type);
            var result = ctx.Synth.NewSynthLocal(leafAccess.Type);
            ctx.Output.Add(new LoweredAssignmentStatement(compound,
                SynthLocalFactory.ReferenceTo(compound, result), binary));
            var resultRef = SynthLocalFactory.ReferenceTo(compound, result);
            // 叶写（复合赋值路径全部走 Output，表达式位返回结果引用）
            ctx.Output.Add(new LoweredAssignmentStatement(compound,
                new LoweredFieldAccessExpression(leafAccess, leafParent, leafAccess.Field),
                resultRef));

            for (var i = intermediates.Count - 1; i >= 0; i--)
            {
                var (local, field, access) = intermediates[i];
                var valueKind = ClassifyWritebackType(local.Type, access.Syntax.Span, env);
                if (valueKind == null) return null;
                if (valueKind == false) break;
                if (!CheckWritebackWritable(field, access.Syntax.Span, ctx, env))
                {
                    return null;
                }
                if (i == 0)
                {
                    var writePlace = BuildWrapperFieldPlace(access, place, field, host, ctx, env);
                    if (writePlace == null) return null;
                    ctx.Output.Add(new LoweredAssignmentStatement(compound, writePlace, local));
                }
                else
                {
                    var parentLocal = intermediates[i - 1].Local;
                    var parentPlace = new LoweredFieldAccessExpression(access, parentLocal, field);
                    ctx.Output.Add(new LoweredAssignmentStatement(compound, parentPlace, local));
                }
            }
            return resultRef;
        }

        private static LoweredExpression MaterializeInto(BoundNode origin,
            LoweredExpression expr, LowerContext ctx)
        {
            if (IsStableValueReference(expr))
            {
                return expr;
            }
            var local = ctx.Synth.NewSynthLocal(expr.Type);
            ctx.Output.Add(new LoweredAssignmentStatement(origin,
                SynthLocalFactory.ReferenceTo(origin, local), expr));
            return SynthLocalFactory.ReferenceTo(origin, local);
        }

        // 写回判定：TypeSymbol → 值/引用分支；GenericParameterSymbol 等非
        // 具体类型不得静默当引用丢写回——落 P4 诊断（true=值/false=引用/
        // null=已诊断失败）
        private static bool? ClassifyWritebackType(SemanticSymbol? type, CharRange? span,
            LowerEnvironment env)
        {
            if (type is TypeSymbol ts) return ts.IsValueTypeBranch;
            env.Error(span,
                "P4: deep field write-back through non-concrete intermediate type " +
                $"(got {type?.GetType().Name ?? "null"}) is not supported");
            return null;
        }

        // 中间值字段反向写回可写性（复用 FieldSymbol/ConstFieldRules 口径）
        private static bool CheckWritebackWritable(FieldSymbol field, CharRange? span,
            LowerContext ctx, LowerEnvironment env)
        {
            if (field.Getter != null || field.Setter != null)
            {
                if (field.Setter == null)
                {
                    env.Error(span, $"'{field.Name}' has no setter");
                    return false;
                }
                if (!AccessChecker.IsAccessible(field.Setter, ctx.Method.SourceFile,
                        AccessChecker.ContainingNamespaceOf(ctx.Method), ctx.Method.Owner))
                {
                    env.Error(span, AccessChecker.InaccessibleMessage(field.Setter));
                    return false;
                }
                return true;
            }
            if (field.IsConst)
            {
                if (ctx.Method.Kind == MethodKind.Init && field.Owner != null && !field.IsStatic)
                {
                    return true;
                }
                env.Error(span, $"Cannot assign to const field '{field.Name}'");
                return false;
            }
            return true;
        }

        // 中间层物化直通判定：仅已是稳定局部/参数引用可跳过多余 .sN
        private static bool IsStableValueReference(LoweredExpression expr) =>
            expr is LoweredValueReferenceExpression;
    }
}
