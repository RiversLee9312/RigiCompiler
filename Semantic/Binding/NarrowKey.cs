namespace LatteCompiler
{
    // smart cast 收窄键（SYNTAX §3.5）：可被收窄表达式的身份。
    // 形态：根（局部/参数/this）+ const 字段链（空 = 根本身）。
    // 值相等语义（Dictionary 键）：根引用相等 + 段序列逐元素引用相等。
    internal sealed class NarrowKey : IEquatable<NarrowKey>
    {
        // 根符号（LocalSymbol/ParameterSymbol）；null = this
        private readonly SemanticSymbol? root;

        // const 字段链（null/空 = 根本身——`x` 的键；`x.f` 的键 = root(x) + [f]）
        private readonly FieldSymbol[]? fields;

        private NarrowKey(SemanticSymbol? root, FieldSymbol[]? fields)
        {
            this.root = root;
            this.fields = fields;
        }

        // 根符号键（局部/参数）
        public static NarrowKey ForSymbol(SemanticSymbol symbol)
        {
            return new NarrowKey(symbol, null);
        }

        // this 键
        public static NarrowKey ForThis()
        {
            return new NarrowKey(null, null);
        }

        // 字段访问键构造（稳定链判定，SYNTAX §3.5）：
        // receiver 链每层递归——this / 局部 / 参数 / const 字段访问；
        // 每层字段必须满足 ConstFieldRules.IsNarrowable（const + 非 init 体内
        // this 字段）；var 根（var 局部/参数）允许——其被赋值时由失效规则
        // 清除根键。链上出现不稳定环节（调用结果/非 const 字段/…）即不可收窄
        public static NarrowKey? TryFromFieldAccess(BoundExpression receiver, FieldSymbol field,
            BindContext ctx)
        {
            if (!ConstFieldRules.IsNarrowable(field, ctx)) return null;
            switch (receiver)
            {
                case BoundSmartCastExpression smartCast:
                    // 收窄包装不改变底层引用身份——剥壳递归
                    return TryFromFieldAccess(smartCast.Operand, field, ctx);
                case BoundThisExpression:
                    return new NarrowKey(null, new[] { field });
                case BoundValueReferenceExpression reference:
                    return new NarrowKey(reference.Symbol, new[] { field });
                case BoundFieldAccessExpression access:
                    var parent = TryFromFieldAccess(access.Receiver, access.Field, ctx);
                    if (parent == null) return null;
                    return new NarrowKey(parent.root,
                        parent.fields == null
                            ? new[] { field }
                            : parent.fields.Concat(new[] { field }).ToArray());
                default:
                    return null;
            }
        }

        // 根符号（失效规则用：var 局部/参数被赋值时清除根为该符号的全部键；
        // null 根 = this——this 不可赋值，永不失效）
        public SemanticSymbol? Root => root;

        public bool Equals(NarrowKey? other)
        {
            if (other == null) return false;
            if (!ReferenceEquals(root, other.root)) return false;
            if ((fields == null) != (other.fields == null)) return false;
            if (fields == null) return true;
            if (fields.Length != other.fields!.Length) return false;
            for (int i = 0; i < fields.Length; i++)
            {
                if (!ReferenceEquals(fields[i], other.fields[i])) return false;
            }
            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as NarrowKey);

        public override int GetHashCode()
        {
            var hash = root?.GetHashCode() ?? 0;
            if (fields != null)
            {
                foreach (var field in fields)
                {
                    hash = (hash * 31) ^ field.GetHashCode();
                }
            }
            return hash;
        }
    }
}
