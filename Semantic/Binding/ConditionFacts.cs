namespace LatteCompiler
{
    // 条件收窄事实对（SYNTAX §3.5）：条件表达式为真/为假
    // 两条控制流边各自的收窄事实（键 → 收窄类型）
    internal sealed class ConditionFacts
    {
        public Dictionary<NarrowKey, TypeSymbol> True { get; } =
            new Dictionary<NarrowKey, TypeSymbol>();

        public Dictionary<NarrowKey, TypeSymbol> False { get; } =
            new Dictionary<NarrowKey, TypeSymbol>();

        public static readonly ConditionFacts Empty = new ConditionFacts();
    }

    // 条件事实提取器（S8b，SYNTAX §3.5 的代码化）：
    // 遍历**绑定后**的 Bound 条件表达式（类型与产物形态已知），产出真/假边
    // 收窄事实。纯函数式提取（只读 FlowState 之外的状态——键构造经
    // ConstFieldRules.IsNarrowable 判定，需要 BindFunctionFrame）。
    //
    // 提取形态：
    // - x is T（静态目标）→ 真边 x→T（蕴含非空）；假边 ∅
    // - x != null（x: T?）→ 真边 x→T；x == null → 假边 x→T
    // - A and B → 真边 = 左真 ∪ 右真；假边 = 左假 ∩ 右假
    // - A or B  → 真边 = 左真 ∩ 右真；假边 = 左假 ∪ 右假
    // - not A   → 真边 = A 假边；假边 = A 真边
    // 其余形态（supers/with/动态 is/普通比较……）→ 空事实
    internal static class ConditionFactsExtractor
    {
        public static ConditionFacts Extract(BoundExpression? condition, BindFunctionFrame frame)
        {
            var facts = new ConditionFacts();
            if (condition == null) return facts;
            ExtractInto(condition, frame, facts.True, facts.False);
            return facts;
        }

        private static void ExtractInto(BoundExpression condition, BindFunctionFrame frame,
            Dictionary<NarrowKey, TypeSymbol> whenTrue, Dictionary<NarrowKey, TypeSymbol> whenFalse)
        {
            switch (condition)
            {
                case BoundTypeCheckExpression
                {
                    Kind: BoundTypeCheckKind.Is, TargetType: { } targetType
                } typeCheck:
                    // 静态 is（真边收窄为 T；T 本身非空即蕴含非空）；
                    // S9a：收窄目标为泛型参数时不收窄（判型跳过）
                    var checkKey = TryKeyOf(typeCheck.Operand, frame);
                    if (checkKey != null && targetType is TypeSymbol narrowed)
                    {
                        UnionOne(whenTrue, checkKey, narrowed);
                    }
                    return;
                case BoundBinaryExpression binary:
                    switch (binary.Op)
                    {
                        case BilIntrinsicOp.And:
                            // 真边 = 左真 ∪ 右真；假边 = 左假 ∩ 右假
                            var leftAnd = Extract(binary.Left, frame);
                            var rightAnd = Extract(binary.Right, frame);
                            UnionAll(whenTrue, leftAnd.True);
                            UnionAll(whenTrue, rightAnd.True);
                            IntersectAll(whenFalse, leftAnd.False, rightAnd.False);
                            return;
                        case BilIntrinsicOp.Or:
                            // 真边 = 左真 ∩ 右真；假边 = 左假 ∪ 右假
                            var leftOr = Extract(binary.Left, frame);
                            var rightOr = Extract(binary.Right, frame);
                            IntersectAll(whenTrue, leftOr.True, rightOr.True);
                            UnionAll(whenFalse, leftOr.False);
                            UnionAll(whenFalse, rightOr.False);
                            return;
                        case BilIntrinsicOp.CmpEq:
                        case BilIntrinsicOp.CmpNe:
                            ExtractNullEquality(binary, frame, whenTrue, whenFalse);
                            return;
                        default:
                            return;
                    }
                case BoundUnaryExpression { Op: BilIntrinsicOp.Not } unary:
                    // not 翻转：真边 ← 操作数假边；假边 ← 操作数真边
                    ExtractInto(unary.Operand, frame, whenFalse, whenTrue);
                    return;
                case BoundSmartCastExpression smartCast:
                    // 收窄包装透明（嵌套条件中的引用可能已被外层收窄）
                    ExtractInto(smartCast.Operand, frame, whenTrue, whenFalse);
                    return;
            }
        }

        // null 判等提取：CmpNe 真边/CmpEq 假边收窄为元素类型 T0
        // （一侧 null 字面量 + 一侧类型 Nullable\<T0\> 的可收窄目标）
        private static void ExtractNullEquality(BoundBinaryExpression binary,
            BindFunctionFrame frame,
            Dictionary<NarrowKey, TypeSymbol> whenTrue, Dictionary<NarrowKey, TypeSymbol> whenFalse)
        {
            var (target, isCmpNe) = binary.Op == BilIntrinsicOp.CmpNe
                ? (UnnullOperand(binary.Left, binary.Right), true)
                : (UnnullOperand(binary.Left, binary.Right), false);
            if (target == null) return;
            // S9a：泛型参数目标无静态构造展开，判型后不参与收窄
            if (target.Type is not TypeSymbol { TypeArguments: { } arguments } targetType
                || targetType.ConstructedFrom == null
                || arguments.Count != 1
                || arguments[0] is not TypeSymbol element)
            {
                return;
            }
            // 要求目标是 Nullable<T0> 构造（P3 已定型——null 判等特例保证）
            var key = TryKeyOf(target, frame);
            if (key == null) return;
            if (isCmpNe)
            {
                UnionOne(whenTrue, key, element);
            }
            else
            {
                UnionOne(whenFalse, key, element);
            }
        }

        // null 判等的可收窄操作数：一侧是 null 字面量（类型 Nullable 构造）时
        // 返回另一侧，否则 null
        private static BoundExpression? UnnullOperand(BoundExpression left, BoundExpression right)
        {
            if (IsNullLiteral(left)) return right;
            if (IsNullLiteral(right)) return left;
            return null;
        }

        private static bool IsNullLiteral(BoundExpression expression)
        {
            return expression is BoundLiteralExpression literal
                && literal.Syntax is LiteralExpressionASTNode { Literal: NullLiteralASTNode };
        }

        // 可收窄目标的键构造（剥 SmartCast 壳；字段链经稳定链判定）
        public static NarrowKey? TryKeyOf(BoundExpression expression, BindFunctionFrame frame)
        {
            switch (expression)
            {
                case BoundSmartCastExpression smartCast:
                    return TryKeyOf(smartCast.Operand, frame);
                case BoundValueReferenceExpression reference:
                    return NarrowKey.ForSymbol(reference.Symbol);
                case BoundThisExpression:
                    return NarrowKey.ForThis();
                case BoundFieldAccessExpression access:
                    return NarrowKey.TryFromFieldAccess(access.Receiver, access.Field, frame);
                default:
                    return null;
            }
        }

        // 并集单条：同键冲突时类型相同保留、不同类型丢弃（保守）
        private static void UnionOne(Dictionary<NarrowKey, TypeSymbol> target, NarrowKey key,
            TypeSymbol type)
        {
            if (target.TryGetValue(key, out var existing))
            {
                if (!ReferenceEquals(existing, type)) target.Remove(key);
                return;
            }
            target[key] = type;
        }

        private static void UnionAll(Dictionary<NarrowKey, TypeSymbol> target,
            Dictionary<NarrowKey, TypeSymbol> source)
        {
            foreach (var (key, type) in source)
            {
                UnionOne(target, key, type);
            }
        }

        // 交集：两集同键且类型相同才保留进 target
        private static void IntersectAll(Dictionary<NarrowKey, TypeSymbol> target,
            Dictionary<NarrowKey, TypeSymbol> left, Dictionary<NarrowKey, TypeSymbol> right)
        {
            foreach (var (key, type) in left)
            {
                if (right.TryGetValue(key, out var other) && ReferenceEquals(type, other))
                {
                    UnionOne(target, key, type);
                }
            }
        }
    }
}
