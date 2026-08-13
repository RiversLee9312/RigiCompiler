namespace RigiCompiler.Bil.Vm
{
    // 引用/值类型实例（BIL_VM_DESIGN §3.2 / BIL_STANDARD §22.1）：
    // 运行时类型以 canonical 符号为身份；字段字典以字段符号为键。
    // class 引用语义（Copy 返回自身）；struct/wrapper 深拷贝。
    // wrapper 隐藏存储（§5.3）不进 BIL 字段表，由 Hidden 槽承载。

    internal interface IVmFieldHost
    {
        string TypeRef { get; }
        bool TryReadField(string fieldSymbol, out VmValue value);
        void WriteField(string fieldSymbol, VmValue value);
        bool TryReadHidden(string key, out VmValue value);
        void WriteHidden(string key, VmValue value);
        VmValue? Host { get; set; }
        IVmFieldHost DeepCopySlots();
    }

    internal sealed class VmInstanceSlots : IVmFieldHost
    {
        private readonly Dictionary<string, VmValue> _fields = new Dictionary<string, VmValue>();
        private readonly Dictionary<string, VmValue> _hidden = new Dictionary<string, VmValue>();

        public string TypeRef { get; }
        public VmValue? Host { get; set; }

        public VmInstanceSlots(string typeRef)
        {
            TypeRef = typeRef;
        }

        public bool TryReadField(string fieldSymbol, out VmValue value)
        {
            return _fields.TryGetValue(fieldSymbol, out value!);
        }

        public void WriteField(string fieldSymbol, VmValue value)
        {
            _fields[fieldSymbol] = value;
        }

        public bool TryReadHidden(string key, out VmValue value)
        {
            return _hidden.TryGetValue(key, out value!);
        }

        public void WriteHidden(string key, VmValue value)
        {
            _hidden[key] = value;
        }

        public IVmFieldHost DeepCopySlots()
        {
            var copy = new VmInstanceSlots(TypeRef) { Host = Host };
            foreach (var pair in _fields)
            {
                copy._fields[pair.Key] = pair.Value.Copy();
            }
            foreach (var pair in _hidden)
            {
                copy._hidden[pair.Key] = pair.Value.Copy();
            }
            return copy;
        }
    }

    public sealed class VmObject : VmValue, IVmFieldHost
    {
        private readonly IVmFieldHost _slots;
        private readonly bool _valueType;

        public override string TypeRef => _slots.TypeRef;
        public bool IsValueType => _valueType;
        public VmValue? Host
        {
            get => _slots.Host;
            set => _slots.Host = value;
        }

        public VmObject(string typeRef, bool valueType)
        {
            _slots = new VmInstanceSlots(typeRef);
            _valueType = valueType;
        }

        private VmObject(IVmFieldHost slots, bool valueType)
        {
            _slots = slots;
            _valueType = valueType;
        }

        public override VmValue Copy()
        {
            if (!_valueType)
            {
                return this;
            }
            return new VmObject(_slots.DeepCopySlots(), valueType: true);
        }

        public override string ToStandardText() => TypeRef;

        public bool TryReadField(string fieldSymbol, out VmValue value)
        {
            return _slots.TryReadField(fieldSymbol, out value);
        }

        public void WriteField(string fieldSymbol, VmValue value)
        {
            _slots.WriteField(fieldSymbol, value);
        }

        public bool TryReadHidden(string key, out VmValue value)
        {
            return _slots.TryReadHidden(key, out value);
        }

        public void WriteHidden(string key, VmValue value)
        {
            _slots.WriteHidden(key, value);
        }

        IVmFieldHost IVmFieldHost.DeepCopySlots() => _slots.DeepCopySlots();

        internal static bool TryAsHost(VmValue value, out IVmFieldHost host)
        {
            if (value is IVmFieldHost fieldHost)
            {
                host = fieldHost;
                return true;
            }
            host = null!;
            return false;
        }
    }
}
