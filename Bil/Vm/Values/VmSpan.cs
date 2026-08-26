namespace RigiCompiler.Bil.Vm
{
    // core::Span<T> / core::SharedSpan<T>（RUNTIME §5）：
    // 内建 class，引用语义——Copy 返回自身（别名写入互相可见）。
    // TypeRef 走 canonical（无 BIL 标准构造头）：core::Span<.i32> /
    // core::SharedSpan<Point>。length 是 bootstrap const 字段，VM 直读。

    public sealed class VmSpan : VmValue, IVmIndexBuffer
    {
        internal const string LengthFieldSymbol = "core::Span#length@.i32";
        internal const string SharedLengthFieldSymbol = "core::SharedSpan#length@.i32";

        public List<VmValue> Elements { get; }
        public string ElementType { get; }
        public bool IsShared { get; }
        public override string TypeRef { get; }
        public int Length => Elements.Count;

        public VmSpan(string elementType, int length, VmValue fill, bool isShared)
        {
            ElementType = elementType;
            IsShared = isShared;
            TypeRef = (isShared ? "core::SharedSpan<" : "core::Span<") + elementType + ">";
            Elements = new List<VmValue>(length);
            for (var i = 0; i < length; i++)
            {
                Elements.Add(fill.Copy());
            }
        }

        // 引用语义：复制共享同一 buffer 对象
        public override VmValue Copy() => this;

        public override string ToStandardText() => TypeRef;

        public VmValue GetAt(int index)
        {
            if (index < 0 || index >= Elements.Count)
            {
                throw new VmException("数组下标越界：" + index
                    + "（长度 " + Elements.Count + "）");
            }
            return Elements[index];
        }

        public void SetAt(int index, VmValue value)
        {
            if (index < 0 || index >= Elements.Count)
            {
                throw new VmException("数组下标越界：" + index
                    + "（长度 " + Elements.Count + "）");
            }
            Elements[index] = value;
        }

        internal static bool IsLengthField(string fieldSymbol)
        {
            return fieldSymbol == LengthFieldSymbol
                || fieldSymbol == SharedLengthFieldSymbol;
        }
    }
}
