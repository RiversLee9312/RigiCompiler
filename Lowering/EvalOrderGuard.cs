namespace RigiCompiler
{
    // 求值序保护设施（P4a 兄弟子表达式降级）：修复「半 ANF」前置流机制
    // 的兄弟求值序颠倒 bug——表达式降级可向当前块输出列表追加前置语句，
    // 而自身返回的 LoweredExpression 留在表达式树里；P4b 发射时所有前置
    // 语句先于属主语句的表达式树执行。同一表达式的多个子表达式按求值序
    // 降级时，若靠后的兄弟把副作用物化成前置语句（短路 and/or、if/switch
    // 表达式、安全访问、复合赋值、seq 等），而更早求值的兄弟仍留在树里，
    // 则后置兄弟的前置会先于前置兄弟执行——求值序颠倒（如
    // funcA().methodB(funcC() and funcD()) 中 funcC 先于 funcA 执行）。
    //
    // 机制（需求驱动 effect-aware 组合）：每个有多子表达式的表达式
    // rewriter 在降级期间逐子表达式登记其前置流末尾位置（Track）；全部
    // 降级完成后 Seal——若前置流有增长（任一兄弟产过前置），把「非稳定
    // 值」的非末位槽位物化为合成局部，前置赋值插回该槽位登记的前置流
    // 末尾位置（即该兄弟自身的降级产物之前），后续兄弟的前置自然排在其
    // 后；表达式树内该槽位改读合成局部，发射序与登记序对齐。末位槽位
    // 之后无兄弟前置，树内求值序天然正确，不物化。
    //
    // 插入索引数学：按槽位序处理，索引 = preludeEnd + inserted（此前
    // 所有插入都在不大于当前 preludeEnd 的位置，后续槽位的登记位置
    // 自动随插入后移，无需调整）。
    internal sealed class EvalOrderGuard
    {
        // 槽位登记（Origin/最终形态/登记时前置流末尾）；Track 序 = 求值序
        private readonly List<(BoundNode Origin, LoweredExpression Value, int PreludeEnd)> slots =
            new List<(BoundNode, LoweredExpression, int)>();
        private readonly LowerContext ctx;
        private readonly int startCount;

        public EvalOrderGuard(LowerContext ctx)
        {
            this.ctx = ctx;
            startCount = ctx.Output.Current.Count;
        }

        public int Count => slots.Count;

        public LoweredExpression this[int index] => slots[index].Value;

        // 子表达式降级并登记（求值序位置 = 登记时前置流末尾）；失败返回
        // null 不登记（调用方短路返回 null，本实例随之废弃）
        public LoweredExpression? Lower(BoundExpression expression, LowerEnvironment env)
        {
            var value = LowerExpressionDispatcher.Visit(expression, ctx, env);
            if (value != null) Track(expression, value);
            return value;
        }

        // 登记调用方已降级/包装好的最终形态（如 EnsureDeclaredType 之后）
        public void Track(BoundNode origin, LoweredExpression value)
        {
            slots.Add((origin, value, ctx.Output.Current.Count));
        }

        // 收口：前置流增长过时，物化非稳定值的非末位槽位（前置赋值插回
        // 该槽位前置流末尾）；返回槽位最终表达式序列（索引与 Track 序
        // 一致——外层 Seal 场景须改用本结果而非 Track 时的返回值）
        public IReadOnlyList<LoweredExpression> Seal()
        {
            var result = new LoweredExpression[slots.Count];
            // 单子表达式无兄弟问题；前置流零增长 = 无兄弟前置，天然正确
            if (slots.Count < 2 || ctx.Output.Current.Count == startCount)
            {
                for (var i = 0; i < slots.Count; i++) result[i] = slots[i].Value;
                return result;
            }
            var statements = ctx.Output.Current;
            var inserted = 0;
            for (var i = 0; i < slots.Count; i++)
            {
                var (origin, value, preludeEnd) = slots[i];
                if (i < slots.Count - 1 && !IsStable(value))
                {
                    var local = ctx.Synth.NewSynthLocal(value.Type);
                    statements.Insert(preludeEnd + inserted,
                        new LoweredAssignmentStatement(origin,
                            SynthLocalFactory.ReferenceTo(origin, local), value));
                    inserted++;
                    value = SynthLocalFactory.ReferenceTo(origin, local);
                }
                result[i] = value;
            }
            return result;
        }

        // 稳定值判定（前置流推移后重读仍得同值）：编译期常量、this、合成
        // 局部（.sN/.bN/.c.* 编译器保留名，一次写入不被兄弟前置改写）、
        // cell 引用（对象身份不变）。用户局部/参数/字段读取不在其列——
        // 兄弟前置可能改写
        private static bool IsStable(LoweredExpression value) => value switch
        {
            LoweredLiteralExpression or LoweredConstantExpression
                or LoweredThisExpression => true,
            LoweredCellReferenceExpression => true,
            LoweredValueReferenceExpression reference => reference.Symbol.Name.StartsWith('.'),
            _ => false,
        };
    }
}
