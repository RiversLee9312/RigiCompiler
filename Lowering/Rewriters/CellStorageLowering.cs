using System.Collections.Generic;

namespace RigiCompiler
{
    // 静态/全局字段的 cell 存储降级设施（统一 cell 存储，SYNTAX §14.3）：
    // 被 wrapper 修饰的静态/全局字段，存储是逐字段合成的 cell 隐藏子类
    // （P3 BindingDriver 阶段 1.6），值读写经 getValue/setValue、wrapper
    // place 寻址以 cell 对象为宿主（field(value)+wrapper(W) 链，与
    // 字段-Value 应用同构——WrapperPlaceLowering 消费同一对象引用）。
    // 局部的 cell 存储由 ClosureStoragePlan 承担，本设施只覆盖静态/全局。
    internal static class CellStorageLowering
    {
        // cell 对象引用：get.field.static 取 cell（Type 覆盖为隐藏子类——
        // 值读须再经 getValue，本表达式只代表 cell 对象本身）
        public static LoweredExpression CellObjectOf(BoundNode origin, FieldSymbol field,
            CellStorageInfo storage)
        {
            return new LoweredFieldReferenceExpression(origin, field, storage.CellType);
        }

        // 值读取改写：静态/全局 cell 化字段的引用 → getValue 调用；
        // 未 cell 化返回 null（调用方走默认 LoweredFieldReferenceExpression）
        public static LoweredExpression? TryRewriteStaticRead(
            BoundFieldReferenceExpression reference, LowerEnvironment env)
        {
            if (reference.Field.CellStorage is not { } storage) return null;
            var getValue = CallableModel.FindCellGetValue(env.Unit, storage.IsReadOnly);
            if (getValue == null)
            {
                throw new CompilerInternalException("stdlib core::Cell 族缺失（getValue）");
            }
            return new LoweredInstanceCallExpression(reference,
                CellObjectOf(reference, reference.Field, storage), getValue,
                new List<LoweredExpression>(), reference.Type);
        }

        // 写入改写：目标是静态/全局 cell 化字段的引用 → setValue 调用语句
        //（const 字段无写通道——P3 已拦截，到达此处属内部错误）；
        // 未 cell 化返回 null。value 由调用方先行降级
        public static LoweredStatement? TryRewriteStaticWrite(BoundNode origin,
            BoundExpression target, LoweredExpression value, LowerEnvironment env)
        {
            if (target is not BoundFieldReferenceExpression reference
                || reference.Field.CellStorage is not { } storage)
            {
                return null;
            }
            if (storage.IsReadOnly)
            {
                throw new CompilerInternalException(
                    "P3 已拦截的 const 字段写入到达 P4: " + reference.Field.Name);
            }
            var setValue = CallableModel.FindCellSetValue(env.Unit)
                ?? throw new CompilerInternalException("stdlib core::Cell 族缺失（setValue）");
            return new LoweredCallStatement(origin, setValue,
                new List<LoweredExpression> { value },
                CellObjectOf(origin, reference.Field, storage));
        }
    }
}
