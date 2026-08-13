namespace RigiCompiler
{
    // 访问器体绑定状态（M65 Bind 侧组件化拆分，自 BindContext 迁出）：
    // backing 形态访问器体内裸名 value 是 backing 字段的别名
    // （S8e，SYNTAX §9.4.1——PathVisitors 裸名解析拦截；getter 体内
    // 只读、setter 体内可读写）。null = 非访问器体上下文；仅 backing
    // 形态置位（computed 访问器的 value 走普通参数解析）
    internal sealed class AccessorBodyState
    {
        public FieldSymbol? Field { get; private set; }

        // Field 非空时有效：true = setter 体（value 可读写），
        // false = getter 体（value 只读）
        public bool IsSetter { get; private set; }

        // 仅 BindingDriver 绑定访问器体时调用（backing 形态置位）
        public void Set(FieldSymbol field, bool isSetter)
        {
            Field = field;
            IsSetter = isSetter;
        }
    }
}
