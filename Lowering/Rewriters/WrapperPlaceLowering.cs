namespace RigiCompiler
{
    // wrapper place 降级设施（S11c/M84，SYNTAX §14.5 + BIL §12.4/§13.3）：
    // 使用点与 proxy 体内共用同一 lowering 路径。
    //
    // 形态矩阵（按应用类别分派——应用记录的宿主形态决定可用指令）：
    // - Entity 应用：成员读/方法调用/索引读 = get.wrapper 值拷贝 +
    //   普通 get.field/invoke/get.array；字段写 = set.wrapper.field 链；
    // - 字段-Value 应用：字段读 = get.wrapper.field 值拷贝 + 普通
    //   get.field；字段写 = set.wrapper.field 链（PlaceChain =
    //   field(HOST_FIELD)+wrapper(W) 相邻对）；方法调用/索引读/写 =
    //   get.wrapper.field 值拷贝后复用普通 invoke/get.array/set.array
    //   （索引写含中间值写回，与深写同构）；
    // - 嵌套 wrapper 链：逐层 Materialize 值拷贝后继续普通 get.field；
    // - 局部/静态（统一 cell 存储，SYNTAX §14.3）：place 根是被 wrapper
    //   修饰的局部/静态/全局字段时，存储是逐变量合成的 cell 隐藏子类——
    //   宿主 = cell 对象引用，链 = field(value)+wrapper(W) 相邻对（子类
    //   value 字段的 wrapped(W) 标记与源符号应用同实例，验证器按字段
    //   应用对消歧）；读 = get.wrapper.field 值拷贝，写 = set.wrapper.field；
    // - 深层纯字段写穿 place.a.b... = rhs：P4a 多 get/set（正向 get +
    //   叶写 + 反向 set；值类型中间写回；引用中间停止；最外层必要
    //   写回复用 set.wrapper.field；普通值中间反向写回发 set.field）；
    // - 普通值类型中间链写穿（S1/g9，SYNTAX §10/§13.2）：深写机制泛化
    //   到任意可写 place 根（局部/参数/this/静态·全局字段）的纯字段链——
    //   普通赋值（LowerValueChainFieldWrite）、复合赋值
    //   （LowerValueChainFieldCompound）与值类型 receiver 方法调用写回
    //   （MaterializeValueReceiver + BuildValueReceiverWritebacks；
    //   rvalue 根/只读中间不写回）。静态/全局值类型根的 place 是
    //   get.field.static / cell getValue 的值拷贝，链上 set.field 只打在
    //   拷贝上——突变后须 set.field.static / cell setValue 写回槽位；
    // - 索引写 place[i]/place.a.b[i] = rhs（M111）：正向 get 物化到索引
    //   receiver + 叶 set.array/setAtIndex + 值类型中间反向写回（与深写
    //   同构）；place 直接作索引 receiver 时值拷贝后叶写、无需写回
    //   place 自身（Entity 与字段-Value/cell 同形——无整值 set.wrapper）。
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
                // 统一 cell 存储：局部/静态的 wrapper place = cell 子类
                // value 字段上的字段-Value 应用——get.wrapper.field 值拷贝
                //（宿主 = cell 对象引用；HOST_FIELD = 子类 value 字段）
                var storage = CellStorageOf(place);
                var cellObject = sharedHost ?? TryCellObjectOf(place, env);
                if (storage == null || cellObject == null)
                {
                    env.Error(place.Syntax.Span,
                        "P4: wrapper place cell storage is missing (P3 synthesis skipped)");
                    return null;
                }
                var cellLocal = ctx.Synth.NewSynthLocal(place.Wrapper);
                ctx.Output.Add(new LoweredAssignmentStatement(place,
                    SynthLocalFactory.ReferenceTo(place, cellLocal),
                    new LoweredGetFieldWrapperExpression(place, cellObject,
                        storage.ValueField, place.Wrapper)));
                return SynthLocalFactory.ReferenceTo(place, cellLocal);
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
        // HOST_FIELD 再加 W（相邻对，同 owner 多字段同 W 可区分）；局部/
        // 静态应用为 cell 根——加 cell 子类 value 字段再加 W，宿主 = cell
        // 对象引用（不再向下展开）
        public static LoweredWrapperFieldExpression? BuildWrapperFieldPlace(BoundNode origin,
            BoundWrapperAccessExpression place, FieldSymbol targetField,
            LoweredExpression? sharedHost, LowerContext ctx, LowerEnvironment env)
        {
            var places = new List<BoundWrapperAccessExpression>();
            BoundWrapperAccessExpression current = place;
            BoundWrapperAccessExpression? cellRoot = null;
            while (true)
            {
                if (IsLocalOrStaticApplication(current))
                {
                    if (CellStorageOf(current) == null)
                    {
                        env.Error(current.Syntax.Span,
                            "P4: wrapper place cell storage is missing (P3 synthesis skipped)");
                        return null;
                    }
                    places.Add(current);
                    cellRoot = current;
                    break;
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
                else if (IsLocalOrStaticApplication(p))
                {
                    // cell 根的字段-Value 应用对：field(value)+wrapper(W)
                    chain.Add(CellStorageOf(p)!.ValueField);
                }
                chain.Add(p.Wrapper);
            }
            LoweredExpression? hostExpr;
            if (cellRoot != null)
            {
                hostExpr = sharedHost ?? TryCellObjectOf(cellRoot, env);
            }
            else
            {
                hostExpr = sharedHost ?? LowerExpressionDispatcher.Visit(
                    UltimateHostExpression(place), ctx, env);
            }
            if (hostExpr == null) return null;
            return new LoweredWrapperFieldExpression(origin, hostExpr, chain, targetField,
                targetField.FieldType!);
        }

        // ===== 局部/静态 cell 根设施（统一 cell 存储）=====
        // place 接收者符号的 cell 存储（局部/静态/全局字段；无 = 未 cell 化）
        private static CellStorageInfo? CellStorageOf(BoundWrapperAccessExpression place)
        {
            return place.Receiver switch
            {
                BoundValueReferenceExpression { Symbol: LocalSymbol local } =>
                    local.CellStorage,
                BoundFieldReferenceExpression fieldReference =>
                    fieldReference.Field.CellStorage,
                _ => null,
            };
        }

        // cell 对象引用：局部 = cell 变量直引（零指令）；静态/全局 =
        // get.field.static 取 cell。storage 缺失（毒化/stdlib 缺席）返回 null
        private static LoweredExpression? TryCellObjectOf(BoundWrapperAccessExpression place,
            LowerEnvironment env)
        {
            switch (place.Receiver)
            {
                case BoundValueReferenceExpression
                    { Symbol: LocalSymbol { CellStorage: { } storage } local }:
                    return new LoweredCellReferenceExpression(place, local, storage.CellType);
                case BoundFieldReferenceExpression
                    { Field.CellStorage: { } storage } fieldReference:
                    return CellStorageLowering.CellObjectOf(place, fieldReference.Field,
                        storage, env);
                default:
                    return null;
            }
        }

        // 写路径终极宿主物化（复合赋值/深写的宿主单次求值共享入口）：
        // cell 根返回 cell 对象引用（变量/静态字段直读，零副作用，无需
        // 共享物化）；其余 = Visit + MaterializeSharedWriteHost 共享
        public static LoweredExpression? LowerUltimateHostForWrite(
            BoundWrapperAccessExpression place, LowerContext ctx, LowerEnvironment env)
        {
            BoundWrapperAccessExpression current = place;
            while (true)
            {
                if (IsLocalOrStaticApplication(current))
                {
                    var cellObject = TryCellObjectOf(current, env);
                    if (cellObject == null)
                    {
                        env.Error(current.Syntax.Span,
                            "P4: wrapper place cell storage is missing (P3 synthesis skipped)");
                    }
                    return cellObject;
                }
                var host = HostOf(current);
                if (host is BoundWrapperAccessExpression nested)
                {
                    current = nested;
                    continue;
                }
                var hostBound = UltimateHostExpression(place);
                var hostValue = LowerExpressionDispatcher.Visit(hostBound, ctx, env);
                return hostValue == null
                    ? null
                    : MaterializeSharedWriteHost(hostBound, hostValue, ctx);
            }
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

        // 索引写目标 place[i] / place.a.b[i]（叶为索引；中间可含纯字段链；
        // 索引 receiver 侧再含索引/调用则 false——归口 UnsupportedWrite）
        public static bool TryIndexWriteTarget(BoundExpression target,
            out BoundWrapperAccessExpression place, out List<BoundFieldAccessExpression> fieldChain,
            out BoundIndexExpression indexExpr)
        {
            place = null!;
            fieldChain = new List<BoundFieldAccessExpression>();
            indexExpr = null!;
            if (PeelSmartCast(target) is not BoundIndexExpression index) return false;
            indexExpr = index;
            BoundExpression current = PeelSmartCast(index.Receiver);
            while (current is BoundFieldAccessExpression fieldAccess)
            {
                fieldChain.Add(fieldAccess);
                current = PeelSmartCast(fieldAccess.Receiver);
            }
            if (current is not BoundWrapperAccessExpression wrapperPlace)
            {
                fieldChain.Clear();
                indexExpr = null!;
                return false;
            }
            place = wrapperPlace;
            fieldChain.Reverse();
            return true;
        }

        private static BoundExpression PeelSmartCast(BoundExpression expr)
        {
            while (expr is BoundSmartCastExpression smartCast) expr = smartCast.Operand;
            return expr;
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

        // 链根写回 place 构造策略（深写/复合/索引写/receiver 写回共用）：
        // wrapper 根 = set.wrapper.field 链（BuildWrapperFieldPlace）；
        // 普通可写 place 根（S1/g9）= 共享宿主上的常规字段 place
        private delegate LoweredExpression? RootWritePlaceBuilder(
            BoundFieldAccessExpression access, FieldSymbol field);

        // ===== 深层纯字段写穿（P4a 展开：正向 get + 叶写 + 反向 set）=====
        // wrapper place 根入口：终极宿主单次求值后交共享核心
        public static LoweredStatement? LowerDeepFieldWrite(BoundNode origin,
            BoundWrapperAccessExpression place, List<BoundFieldAccessExpression> chain,
            BoundExpression rhsBound, LowerContext ctx, LowerEnvironment env)
        {
            // 终极宿主单次求值（cell 根直取 cell 对象引用；其余共享
            // 写路径：字段宿主亦物化）
            var host = LowerUltimateHostForWrite(place, ctx, env);
            if (host == null) return null;
            return LowerDeepFieldWriteCore(origin, chain, rhsBound,
                access => LowerFieldRead(access, place, host, ctx, env),
                (access, field) => BuildWrapperFieldPlace(access, place, field, host, ctx, env),
                ctx, env);
        }

        // 深写共享核心（wrapper 根 / 普通值类型中间链根共用）。
        // 求值序：终极宿主 → 正向各字段 get（各一次）→ RHS → 叶写 → 反向写回。
        // 返回完成的赋值语句（叶写）；写回作为前置语句落入 Output。
        // 失败时已落诊断并返回 null。
        private static LoweredStatement? LowerDeepFieldWriteCore(BoundNode origin,
            List<BoundFieldAccessExpression> chain, BoundExpression rhsBound,
            Func<BoundFieldAccessExpression, LoweredExpression?> firstFieldRead,
            RootWritePlaceBuilder rootWritePlace, LowerContext ctx, LowerEnvironment env)
        {
            // 正向：首字段经 firstFieldRead；其后逐字段 get 并物化局部
            var intermediates = new List<(LoweredExpression Local, FieldSymbol Field,
                BoundFieldAccessExpression Access)>();
            var firstAccess = chain[0];
            var firstRead = firstFieldRead(firstAccess);
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

            // 叶写 + 反向写回（叶写必须先于写回；返回值由调用方
            // 追加到 Output，故无写回时直接返回叶写，有写回时叶写先
            // Output.Add、末条写回作返回值——块收集序 = 前缀 + 返回）
            var leafWrite = new LoweredAssignmentStatement(origin, leafPlace, rhs);
            return EmitLeafWriteWithWritebacks(origin, leafWrite, intermediates,
                rootWritePlace, ctx, env);
        }

        // 深层复合赋值 place.a.b op= rhs：wrapper 根入口，共享核心同深写
        public static LoweredExpression? LowerDeepFieldCompound(
            BoundCompoundAssignmentExpression compound, BoundWrapperAccessExpression place,
            List<BoundFieldAccessExpression> chain, LowerContext ctx, LowerEnvironment env)
        {
            var host = LowerUltimateHostForWrite(place, ctx, env);
            if (host == null) return null;
            return LowerDeepFieldCompoundCore(compound, chain,
                access => LowerFieldRead(access, place, host, ctx, env),
                (access, field) => BuildWrapperFieldPlace(access, place, field, host, ctx, env),
                ctx, env);
        }

        // 深层复合赋值共享核心：读叶 → 运算 → 叶写 → 同反向写回
        private static LoweredExpression? LowerDeepFieldCompoundCore(
            BoundCompoundAssignmentExpression compound,
            List<BoundFieldAccessExpression> chain,
            Func<BoundFieldAccessExpression, LoweredExpression?> firstFieldRead,
            RootWritePlaceBuilder rootWritePlace, LowerContext ctx, LowerEnvironment env)
        {
            var intermediates = new List<(LoweredExpression Local, FieldSymbol Field,
                BoundFieldAccessExpression Access)>();
            var firstAccess = chain[0];
            var firstRead = firstFieldRead(firstAccess);
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

            if (!EmitWritebacksOnly(compound, intermediates, rootWritePlace, ctx, env))
            {
                return null;
            }
            return resultRef;
        }

        // ===== 索引写（M111：place[i] / place.a.b[i] = rhs）=====
        // 求值序：终极宿主 → 正向字段 get → 索引 → RHS → 叶 set.array → 反向写回。
        // place 直接作索引 receiver：值拷贝后叶写，无 place 自身写回。
        public static LoweredStatement? LowerIndexWrite(BoundNode origin,
            BoundWrapperAccessExpression place, List<BoundFieldAccessExpression> fieldChain,
            BoundIndexExpression indexExpr, BoundExpression rhsBound,
            LowerContext ctx, LowerEnvironment env)
        {
            var host = LowerUltimateHostForWrite(place, ctx, env);
            if (host == null) return null;

            var (indexReceiver, intermediates) = MaterializeIndexReceiver(
                origin, place, fieldChain, host, ctx, env);
            if (indexReceiver == null) return null;

            var indexValue = LowerExpressionDispatcher.Visit(indexExpr.Index, ctx, env);
            if (indexValue == null) return null;
            indexValue = MaterializeInto(indexExpr, indexValue, ctx);

            var rhs = LowerExpressionDispatcher.Visit(rhsBound, ctx, env);
            if (rhs == null) return null;
            rhs = LoweringFacility.EnsureDeclaredType(origin, rhs, indexExpr.Type);

            var leafPlace = new LoweredIndexExpression(indexExpr, indexReceiver, indexValue);
            var leafWrite = new LoweredAssignmentStatement(origin, leafPlace, rhs);
            return EmitLeafWriteWithWritebacks(origin, leafWrite, intermediates,
                (access, field) => BuildWrapperFieldPlace(access, place, field, host, ctx, env),
                ctx, env);
        }

        // 正向物化索引 receiver：place 值拷贝 + 可选字段链；返回 (receiver, 字段中间层)
        private static (LoweredExpression? Receiver,
            List<(LoweredExpression Local, FieldSymbol Field, BoundFieldAccessExpression Access)>
                Intermediates)
            MaterializeIndexReceiver(BoundNode origin, BoundWrapperAccessExpression place,
                List<BoundFieldAccessExpression> fieldChain, LoweredExpression host,
                LowerContext ctx, LowerEnvironment env)
        {
            var intermediates =
                new List<(LoweredExpression Local, FieldSymbol Field, BoundFieldAccessExpression Access)>();
            if (fieldChain.Count == 0)
            {
                var materialized = Materialize(place, host, ctx, env);
                return (materialized, intermediates);
            }

            var firstAccess = fieldChain[0];
            var firstRead = LowerFieldRead(firstAccess, place, host, ctx, env);
            if (firstRead == null) return (null, intermediates);
            var firstLocal = MaterializeInto(firstAccess, firstRead, ctx);
            intermediates.Add((firstLocal, firstAccess.Field, firstAccess));

            for (var i = 1; i < fieldChain.Count; i++)
            {
                var access = fieldChain[i];
                var read = new LoweredFieldAccessExpression(access, intermediates[i - 1].Local,
                    access.Field);
                var local = MaterializeInto(access, read, ctx);
                intermediates.Add((local, access.Field, access));
            }
            return (intermediates[intermediates.Count - 1].Local, intermediates);
        }

        // 叶写 + 反向写回（叶写必须先于写回；无写回时直接返回叶写）
        private static LoweredStatement? EmitLeafWriteWithWritebacks(BoundNode origin,
            LoweredStatement leafWrite,
            List<(LoweredExpression Local, FieldSymbol Field, BoundFieldAccessExpression Access)>
                intermediates,
            RootWritePlaceBuilder rootWritePlace, LowerContext ctx, LowerEnvironment env)
        {
            var writebacks = BuildWritebacks(origin, intermediates, rootWritePlace, ctx, env);
            if (writebacks == null) return null;
            if (writebacks.Count == 0) return leafWrite;
            ctx.Output.Add(leafWrite);
            for (var i = 0; i < writebacks.Count - 1; i++)
            {
                ctx.Output.Add(writebacks[i]);
            }
            return writebacks[writebacks.Count - 1];
        }

        // 仅反向写回（复合赋值路径叶写已入 Output）
        private static bool EmitWritebacksOnly(BoundNode origin,
            List<(LoweredExpression Local, FieldSymbol Field, BoundFieldAccessExpression Access)>
                intermediates,
            RootWritePlaceBuilder rootWritePlace, LowerContext ctx, LowerEnvironment env)
        {
            var writebacks = BuildWritebacks(origin, intermediates, rootWritePlace, ctx, env);
            if (writebacks == null) return false;
            foreach (var wb in writebacks)
            {
                ctx.Output.Add(wb);
            }
            return true;
        }

        // 按值类型边界从内向外构造写回语句列表（null = 已诊断失败）
        private static List<LoweredStatement>? BuildWritebacks(BoundNode origin,
            List<(LoweredExpression Local, FieldSymbol Field, BoundFieldAccessExpression Access)>
                intermediates,
            RootWritePlaceBuilder rootWritePlace, LowerContext ctx, LowerEnvironment env)
        {
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
                    var writePlace = rootWritePlace(access, field);
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
            return writebacks;
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

        // 写回判定：TypeSymbol → 值/引用分支；GenericParameterSymbol 按
        // extends 约束可否定到 class（引用）或值类型分支（写回）时落地，
        // 否则收窄诊断（true=值/false=引用/null=已诊断失败）
        private static bool? ClassifyWritebackType(SemanticSymbol? type, CharRange? span,
            LowerEnvironment env)
        {
            if (type is TypeSymbol ts) return ts.IsValueTypeBranch;
            if (type is GenericParameterSymbol gp)
            {
                var forced = TryClassifyGenericWriteback(gp);
                if (forced != null) return forced;
                env.Error(span,
                    "P4: deep field write-back through unconstrained generic parameter " +
                    $"intermediate type ('{gp.Name}') is not supported");
                return null;
            }
            env.Error(span,
                "P4: deep field write-back through non-concrete intermediate type " +
                $"(got {type?.GetType().Name ?? "null"}) is not supported");
            return null;
        }

        // 泛型参数写回：extends class → 引用；extends 值类型分支（struct/
        // enum/wrapper/ValueType 链）→ 值；interface/with/无约束 → 不可解
        private static bool? TryClassifyGenericWriteback(GenericParameterSymbol gp)
        {
            bool? forced = null;
            foreach (var constraint in gp.Constraints)
            {
                if (constraint.Kind != GenericConstraintKind.Extends) continue;
                if (constraint.Bound is not TypeSymbol bound) continue;
                bool? branch = bound.Kind switch
                {
                    TypeKind.Class => false,
                    TypeKind.Struct or TypeKind.EnumStruct or TypeKind.Wrapper => true,
                    _ when bound.IsValueTypeBranch => true,
                    _ => null,
                };
                if (branch == null) continue;
                if (forced != null && forced != branch) return null;
                forced = branch;
            }
            return forced;
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

        // ===== 普通值类型中间链写穿（S1/g9，SYNTAX §10/§13.2 +
        // SEMANTIC_ARCHITECTURE「正向 get + 叶写 + 反向 set；值类型中间
        // 写回」）=====
        // wrapper place 之外的任意可写 place 字段链：VM get.field 对值
        // 类型 .Copy()，叶写打在拷贝上——与 wrapper 深写同构地物化中间
        // 值、叶写后逐层反向 set 写回（引用类型中间停止）。

        // 写目标判定：纯字段链 host.f1.f2...，根是可写 place（局部/参数/
        // this/静态·全局字段）。>=2 层且非叶中间含值类型环节才接管（全
        // 引用链走普通路径零开销）；静态/全局值类型根的单层字段写
        // （SomeStatic.origin = rhs）也须接管——根自身是值拷贝。wrapper
        // 根/索引/call 等非字段节点垫底时不接管（归 wrapper 专用路径或
        // 普通路径）
        public static bool TryValueChainWriteTarget(BoundExpression target,
            out BoundExpression root, out List<BoundFieldAccessExpression> chain)
        {
            root = null!;
            chain = new List<BoundFieldAccessExpression>();
            BoundExpression current = target;
            while (current is BoundFieldAccessExpression fieldAccess)
            {
                chain.Add(fieldAccess);
                current = fieldAccess.Receiver;
            }
            if (chain.Count == 0 || !IsWritableValueChainRoot(current))
            {
                chain.Clear();
                return false;
            }
            chain.Reverse();
            if (chain.Count >= 2)
            {
                for (var i = 0; i < chain.Count - 1; i++)
                {
                    if (IsValueTypeIntermediate(chain[i].Type))
                    {
                        root = current;
                        return true;
                    }
                }
            }
            // 静态/全局值类型根：单层也须拷贝-改-写回槽位
            if (IsValueTypeFieldRoot(current))
            {
                root = current;
                return true;
            }
            chain.Clear();
            return false;
        }

        // 普通赋值 host.a.b... = rhs：正向 get 物化中间值 → 叶写 →
        // 值类型中间反向 set 写回（求值序 = 根 → 正向 get → RHS → 叶写 → 写回）
        public static LoweredStatement? LowerValueChainFieldWrite(BoundNode origin,
            BoundExpression root, List<BoundFieldAccessExpression> chain,
            BoundExpression rhsBound, LowerContext ctx, LowerEnvironment env)
        {
            var host = LowerValueChainRoot(root, ctx, env);
            if (host == null) return null;
            if (chain.Count == 1)
            {
                return LowerValueChainSingleFieldWrite(origin, root, host, chain[0],
                    rhsBound, ctx, env);
            }
            var stmt = LowerDeepFieldWriteCore(origin, chain, rhsBound,
                access => new LoweredFieldAccessExpression(access, host, access.Field),
                (access, field) => new LoweredFieldAccessExpression(access, host, field),
                ctx, env);
            if (stmt == null) return null;
            return FinishWithFieldRootWriteback(origin, root, host, stmt, ctx, env);
        }

        // 复合赋值 host.a.b... op= rhs：读叶 → 运算 → 叶写 → 同反向写回
        public static LoweredExpression? LowerValueChainFieldCompound(
            BoundCompoundAssignmentExpression compound, BoundExpression root,
            List<BoundFieldAccessExpression> chain, LowerContext ctx, LowerEnvironment env)
        {
            var host = LowerValueChainRoot(root, ctx, env);
            if (host == null) return null;
            if (chain.Count == 1)
            {
                return LowerValueChainSingleFieldCompound(compound, root, host, chain[0],
                    ctx, env);
            }
            var result = LowerDeepFieldCompoundCore(compound, chain,
                access => new LoweredFieldAccessExpression(access, host, access.Field),
                (access, field) => new LoweredFieldAccessExpression(access, host, field),
                ctx, env);
            if (result == null) return null;
            if (!TryAppendFieldRootWriteback(compound, root, host, ctx, env))
            {
                return null;
            }
            return result;
        }

        // ===== 值类型 receiver 方法调用写回（§10：「只有以可写 place 为
        // receiver 的调用原地生效」；无 mut 标注分析——一律写回可写
        // place）=====
        // receiver 判定：方法宿主是值类型（wrapper 除外——走 wrapper
        // 派发链）、receiver 是可写字段链 place（根 = 局部/参数/this/
        // 静态·全局字段）、链每一环均可写回（无 setter 的访问器/const
        // 中间 → 不写回，落普通路径——只读 place 上的 this 修改按 §10
        // 口径本就不生效）。值类型静态/全局根若自身只读（const/无
        // setter）亦不接管——写回无处落。rvalue 根（call/索引/new 等）
        // 不接管——修改无处写回，无意义
        public static bool TryValueReceiverCallTarget(BoundExpression receiver,
            SemanticSymbol? methodOwner, out BoundExpression root,
            out List<BoundFieldAccessExpression> chain)
        {
            root = null!;
            chain = new List<BoundFieldAccessExpression>();
            if (methodOwner is not TypeSymbol { IsValueTypeBranch: true } owner
                || owner.Kind == TypeKind.Wrapper)
            {
                return false;
            }
            BoundExpression current = receiver;
            while (current is BoundFieldAccessExpression fieldAccess)
            {
                chain.Add(fieldAccess);
                current = fieldAccess.Receiver;
            }
            if (chain.Count == 0 || !IsWritableValueChainRoot(current))
            {
                chain.Clear();
                return false;
            }
            chain.Reverse();
            for (var i = 0; i < chain.Count; i++)
            {
                var field = chain[i].Field;
                if ((field.Getter != null || field.Setter != null) && field.Setter == null)
                {
                    chain.Clear();
                    return false;
                }
                if (field.IsConst)
                {
                    chain.Clear();
                    return false;
                }
                // 中间层类型须可静态分类（泛型参数等不可解形态不接管——
                // 避免 receiver 调用落新诊断；赋值路径诊断口径不变）
                if (i < chain.Count - 1 && chain[i].Type is not TypeSymbol)
                {
                    chain.Clear();
                    return false;
                }
            }
            // 值类型静态/全局根须可写回槽位（const/getter-only 不接管，
            // 与中间环节同口径——调用路径静默不写回）
            if (IsValueTypeFieldRoot(current)
                && current is BoundFieldReferenceExpression fieldRoot)
            {
                var field = fieldRoot.Field;
                if ((field.Getter != null || field.Setter != null) && field.Setter == null)
                {
                    chain.Clear();
                    return false;
                }
                if (field.IsConst)
                {
                    chain.Clear();
                    return false;
                }
            }
            root = current;
            return true;
        }

        // receiver 正向物化：根单次求值共享 → 逐字段 get 物化局部，
        // 末层局部 = receiver 值拷贝（调用后由 BuildValueReceiverWritebacks
        // 写回）。返回的 intermediates 含全部链环（末环 = receiver 自身）
        public static LoweredExpression? MaterializeValueReceiver(BoundNode origin,
            BoundExpression root, List<BoundFieldAccessExpression> chain,
            LowerContext ctx, LowerEnvironment env,
            out List<(LoweredExpression Local, FieldSymbol Field,
                BoundFieldAccessExpression Access)> intermediates,
            out LoweredExpression? host)
        {
            intermediates = new List<(LoweredExpression, FieldSymbol,
                BoundFieldAccessExpression)>();
            host = LowerValueChainRoot(root, ctx, env);
            if (host == null) return null;
            LoweredExpression current = host;
            foreach (var access in chain)
            {
                var read = new LoweredFieldAccessExpression(access, current, access.Field);
                var local = MaterializeInto(access, read, ctx);
                intermediates.Add((local, access.Field, access));
                current = local;
            }
            return current;
        }

        // receiver 写回语句构造（调用之后执行；null = 已诊断失败）：
        // receiver 拷贝写回父 place 后按值类型边界逐层向外（引用中间停止）；
        // 值类型静态/全局根额外把宿主拷贝写回槽位
        public static List<LoweredStatement>? BuildValueReceiverWritebacks(BoundNode origin,
            List<(LoweredExpression Local, FieldSymbol Field, BoundFieldAccessExpression Access)>
                intermediates,
            LoweredExpression host, BoundExpression root, LowerContext ctx, LowerEnvironment env)
        {
            var writebacks = BuildWritebacks(origin, intermediates,
                (access, field) => new LoweredFieldAccessExpression(access, host, field),
                ctx, env);
            if (writebacks == null) return null;
            if (!TryBuildFieldRootWriteback(origin, root, host, ctx, env, out var extra))
            {
                return null;
            }
            if (extra != null) writebacks.Add(extra);
            return writebacks;
        }

        // 链根单次求值共享（与 wrapper 写路径同口径：仅稳定局部/参数/
        // this 直通，其余先落合成局部——RHS/实参不得引起根重评）。
        // 静态/全局字段引用会落入合成局部（get.field.static / cell
        // getValue 的值拷贝），写回由 TryBuildFieldRootWriteback 承担。
        private static LoweredExpression? LowerValueChainRoot(BoundExpression root,
            LowerContext ctx, LowerEnvironment env)
        {
            var hostValue = LowerExpressionDispatcher.Visit(root, ctx, env);
            return hostValue == null
                ? null
                : MaterializeSharedWriteHost(root, hostValue, ctx);
        }

        // 可写链根：局部/参数/this/静态·全局字段（索引/call 等 rvalue
        // 根不接管——保持原路径行为）
        private static bool IsWritableValueChainRoot(BoundExpression root) =>
            root is BoundValueReferenceExpression or BoundThisExpression
                or BoundFieldReferenceExpression;

        // 静态/全局字段根且其声明类型是值分支（或不可解泛型参数）——
        // get.field.static / cell getValue 得到值拷贝，必须写回槽位
        private static bool IsValueTypeFieldRoot(BoundExpression root) =>
            root is BoundFieldReferenceExpression
            && root.Type is TypeSymbol { IsValueTypeBranch: true } or GenericParameterSymbol;

        // 单层字段写 SomeStatic.origin = rhs：叶写打在宿主拷贝上，再写回槽位
        private static LoweredStatement? LowerValueChainSingleFieldWrite(BoundNode origin,
            BoundExpression root, LoweredExpression host, BoundFieldAccessExpression leafAccess,
            BoundExpression rhsBound, LowerContext ctx, LowerEnvironment env)
        {
            var rhs = LowerExpressionDispatcher.Visit(rhsBound, ctx, env);
            if (rhs == null) return null;
            rhs = LoweringFacility.EnsureDeclaredType(origin, rhs, leafAccess.Type);
            var leafPlace = new LoweredFieldAccessExpression(leafAccess, host, leafAccess.Field);
            var leafWrite = new LoweredAssignmentStatement(origin, leafPlace, rhs);
            return FinishWithFieldRootWriteback(origin, root, host, leafWrite, ctx, env);
        }

        // 单层复合赋值 SomeStatic.origin op= rhs
        private static LoweredExpression? LowerValueChainSingleFieldCompound(
            BoundCompoundAssignmentExpression compound, BoundExpression root,
            LoweredExpression host, BoundFieldAccessExpression leafAccess,
            LowerContext ctx, LowerEnvironment env)
        {
            var leafRead = new LoweredFieldAccessExpression(leafAccess, host, leafAccess.Field);
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
            ctx.Output.Add(new LoweredAssignmentStatement(compound,
                new LoweredFieldAccessExpression(leafAccess, host, leafAccess.Field),
                resultRef));
            if (!TryAppendFieldRootWriteback(compound, root, host, ctx, env))
            {
                return null;
            }
            return resultRef;
        }

        // 深写返回句之后追加值类型静态/全局根写回（无则原句直通）
        private static LoweredStatement? FinishWithFieldRootWriteback(BoundNode origin,
            BoundExpression root, LoweredExpression host, LoweredStatement last,
            LowerContext ctx, LowerEnvironment env)
        {
            if (!TryBuildFieldRootWriteback(origin, root, host, ctx, env, out var extra))
            {
                return null;
            }
            if (extra == null) return last;
            ctx.Output.Add(last);
            return extra;
        }

        // 复合/调用路径：值类型静态/全局根写回落入 Output（true = 成功）
        private static bool TryAppendFieldRootWriteback(BoundNode origin, BoundExpression root,
            LoweredExpression host, LowerContext ctx, LowerEnvironment env)
        {
            if (!TryBuildFieldRootWriteback(origin, root, host, ctx, env, out var extra))
            {
                return false;
            }
            if (extra != null) ctx.Output.Add(extra);
            return true;
        }

        // 构造静态/全局值类型根写回：普通 = set.field.static；cell 化 =
        // setValue。引用类型根无需写回槽位（对象字段突变已生效）。
        // extra = null 且 true = 无需写回；false = 已诊断失败
        private static bool TryBuildFieldRootWriteback(BoundNode origin, BoundExpression root,
            LoweredExpression host, LowerContext ctx, LowerEnvironment env,
            out LoweredStatement? extra)
        {
            extra = null;
            if (!IsValueTypeFieldRoot(root)
                || root is not BoundFieldReferenceExpression fieldRef)
            {
                return true;
            }
            if (!CheckWritebackWritable(fieldRef.Field, fieldRef.Syntax.Span, ctx, env))
            {
                return false;
            }
            if (fieldRef.Field.CellStorage != null)
            {
                extra = CellStorageLowering.TryRewriteStaticWrite(origin, root, host, env);
                return extra != null;
            }
            extra = new LoweredAssignmentStatement(origin,
                new LoweredFieldReferenceExpression(fieldRef, fieldRef.Field), host);
            return true;
        }

        // 值类型中间判定：TypeSymbol 值分支；泛型参数保守接管（写回
        // 分类归 ClassifyWritebackType——不可解时按既有口径诊断）
        private static bool IsValueTypeIntermediate(SemanticSymbol? type) =>
            type is TypeSymbol { IsValueTypeBranch: true } or GenericParameterSymbol;
    }
}
