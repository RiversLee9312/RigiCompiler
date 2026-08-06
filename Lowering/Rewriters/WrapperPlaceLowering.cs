namespace LatteCompiler
{
    // wrapper place 降级设施（S11c，SYNTAX §14.5 + BIL §12.4 注记/§13.3）：
    // 使用点与 proxy 体内共用同一 lowering 路径（proxy fn 体的发射闸门归
    // S11d，本设施是其唯一入口）。
    //
    // 形态矩阵（按应用类别分派——应用记录的宿主形态决定可用指令）：
    // - Entity 应用（挂类型，隐藏字段在宿主类型上）：成员读/方法调用/索引读
    //   = get.wrapper 值拷贝链（§12.4 注记）；成员写 = set.field.embedded 链；
    // - 字段-Value 应用（挂实例字段，隐藏字段在字段宿主类型上）：字段读 =
    //   get.field.embedded 链、字段写 = set.field.embedded 链（宿主值取字段
    //   访问的 receiver——wrapper 实例按字段槽挂在属主对象上，字段值本身
    //   寻址不到它）；方法调用/索引归口（get.wrapper 的 VALUE 规则只为
    //   Entity 形态定义，BIL 无 embedded 索引指令）；
    // - 局部/静态/全局目标：隐藏字段为 null（栈帧/静态存储合成归后续
    //   里程碑，P2 注记）——统一归口。
    internal static class WrapperPlaceLowering
    {
        // ===== 类别判定（应用记录归属：类型应用 vs 字段/局部/静态应用）=====
        // 经 place.Receiver 形状与 Application 引用相等比对判定——P3 已保证
        // 命中唯一（同名歧义在 BindWrapperSegment 诊断），此处按形状还原
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

        // ===== Entity 值拷贝物化（get.wrapper 链）=====
        // 字段读/方法调用/索引读的接收者物化：place 链逐级 get.wrapper 值拷贝
        // （§12.4 注记），返回最内层 wrapper 值的合成局部引用。非 Entity 应用
        // （字段-Value/局部/静态）归口——这些形态无 get.wrapper 的合法 VALUE。
        // sharedHost 非 null 时直接用作物化起点（复合赋值的宿主单次求值共享）
        public static LoweredExpression? Materialize(BoundWrapperAccessExpression place,
            LoweredExpression? sharedHost, LowerContext ctx, LowerEnvironment env)
        {
            // 逐级收集 place 链（最内层 → 最外层），每层都必须是 Entity 应用
            var chain = new List<BoundWrapperAccessExpression>();
            BoundExpression host = place;
            while (host is BoundWrapperAccessExpression current)
            {
                if (IsFieldApplication(current) || IsLocalOrStaticApplication(current))
                {
                    env.Error(current.Syntax.Span,
                        "P4: this wrapper place form cannot be materialized as a value " +
                        "(field-applied/local/static wrapper places, S11)");
                    return null;
                }
                chain.Add(current);
                host = current.Receiver;
            }
            var value = sharedHost ?? LowerExpressionDispatcher.Visit(host, ctx, env);
            if (value == null) return null;
            // 最外层 → 最内层逐缀 get.wrapper 物化（每层一个合成局部）
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var level = chain[i];
                var local = ctx.Synth.NewSynthLocal(level.Wrapper);
                ctx.Output.Add(new LoweredAssignmentStatement(level,
                    SynthLocalFactory.ReferenceTo(level, local),
                    new LoweredGetWrapperExpression(level, value, level.Wrapper)));
                value = SynthLocalFactory.ReferenceTo(level, local);
            }
            return value;
        }

        // ===== 字段读（Entity → 值拷贝 + get.field；字段-Value → embedded 读）=====
        public static LoweredExpression? LowerFieldRead(BoundFieldAccessExpression access,
            BoundWrapperAccessExpression place, LoweredExpression? sharedHost,
            LowerContext ctx, LowerEnvironment env)
        {
            if (IsFieldApplication(place) || IsLocalOrStaticApplication(place))
            {
                // 字段-Value 读 = embedded 链（原地读与值拷贝读取可观察等价）；
                // 局部/静态在 BuildEmbedded 内按 HiddenField null 归口
                return BuildEmbedded(access, place, access.Field, sharedHost, ctx, env);
            }
            var materialized = Materialize(place, sharedHost, ctx, env);
            if (materialized == null) return null;
            return new LoweredFieldAccessExpression(access, materialized, access.Field);
        }

        // ===== embedded 链构造（字段写 place 与字段-Value 读共用）=====
        // 自最内层 place 向外收集 wrapper(W) 链（Application.Wrapper，
        // 最外层→最内层）；终极宿主经 HostOf 下钻。局部/静态应用仍归口
        // （栈帧/静态存储合成归后续）。sharedHost 非 null 时直接使用
        public static LoweredEmbeddedFieldExpression? BuildEmbedded(BoundNode origin,
            BoundWrapperAccessExpression place, FieldSymbol targetField,
            LoweredExpression? sharedHost, LowerContext ctx, LowerEnvironment env)
        {
            var wrappers = new List<TypeSymbol>();
            BoundWrapperAccessExpression current = place;
            while (true)
            {
                if (IsLocalOrStaticApplication(current))
                {
                    env.Error(current.Syntax.Span,
                        "P4: local/static wrapper place storage is not supported yet (S11)");
                    return null;
                }
                wrappers.Add(current.Wrapper);
                var host = HostOf(current);
                if (host is BoundWrapperAccessExpression nested)
                {
                    current = nested;
                    continue;
                }
                break;
            }
            // wrappers 当前最内→最外（从 place 向外收集）；发射序最外→最内
            wrappers.Reverse();
            var hostExpr = sharedHost ?? LowerExpressionDispatcher.Visit(
                UltimateHostExpression(place), ctx, env);
            if (hostExpr == null) return null;
            return new LoweredEmbeddedFieldExpression(origin, hostExpr, wrappers, targetField,
                targetField.FieldType!);
        }

        // place 的宿主表达式：字段-Value 应用 = 字段访问的 receiver（wrapper
        // 实例按字段槽挂属主对象）；Entity 应用 = place.Receiver 本身
        private static BoundExpression HostOf(BoundWrapperAccessExpression place)
        {
            return IsFieldApplication(place)
                ? ((BoundFieldAccessExpression)place.Receiver).Receiver
                : place.Receiver;
        }

        // 终极宿主表达式（place 链尽头；复合赋值的宿主单次求值共享用——
        // 读路径与写路径共用同一物化引用）
        public static BoundExpression UltimateHostExpression(BoundWrapperAccessExpression place)
        {
            BoundExpression host = place;
            while (host is BoundWrapperAccessExpression current)
            {
                host = HostOf(current);
            }
            return host;
        }

        // ===== 赋值目标拦截（StatementRewriters/CompoundAssignmentRewriter 共用）=====
        // 目标为 wrapper place 直接字段（place.field）时返回该字段访问——
        // 走 embedded 写；更深层级写入（place.a.b，写穿中间拷贝会丢失）与
        // 索引写（BIL 无 embedded 索引指令）经 ContainsPlaceInTarget 归口
        public static BoundFieldAccessExpression? DirectPlaceFieldTarget(BoundExpression target)
        {
            return target is BoundFieldAccessExpression access
                && access.Receiver is BoundWrapperAccessExpression ? access : null;
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

        // 写穿/索引写归口（消息一处收口）
        public static void UnsupportedWrite(BoundNode node, LowerEnvironment env)
        {
            env.Error(node.Syntax.Span,
                "P4: writes through wrapper place member or index chains are not " +
                "supported yet (S11)");
        }
    }
}
