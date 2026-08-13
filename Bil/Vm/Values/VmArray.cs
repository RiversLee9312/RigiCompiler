namespace RigiCompiler.Bil.Vm
{
    // .array<T>（BIL_VM_DESIGN §3.2 / BIL_STANDARD §6.3 / §13.6）：
    // 元素精确类型 + VmValue[]；Array 是 class，Copy 返回自身。
    // length 是 bootstrap const 字段（core::Array#length@.i32），VM 直读。

    public sealed class VmArray : VmValue
    {
        internal const string LengthFieldSymbol = "core::Array#length@.i32";

        private readonly VmValue[] _items;

        public string ElementType { get; }
        public int Length => _items.Length;
        public override string TypeRef => ".array<" + ElementType + ">";

        public VmArray(string elementType, int length, VmValue fill)
        {
            ElementType = elementType;
            _items = new VmValue[length];
            for (var i = 0; i < length; i++)
            {
                _items[i] = fill.Copy();
            }
        }

        public override VmValue Copy() => this;

        public override string ToStandardText() => TypeRef;

        public VmValue GetAt(int index)
        {
            if (index < 0 || index >= _items.Length)
            {
                throw new VmException("数组下标越界：" + index
                    + "（长度 " + _items.Length + "）");
            }
            return _items[index];
        }

        public void SetAt(int index, VmValue value)
        {
            if (index < 0 || index >= _items.Length)
            {
                throw new VmException("数组下标越界：" + index
                    + "（长度 " + _items.Length + "）");
            }
            _items[index] = value;
        }
    }
}
