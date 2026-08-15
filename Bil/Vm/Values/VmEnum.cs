namespace RigiCompiler.Bil.Vm
{
    // enum case 身份 + payload（BIL_VM_DESIGN §3.2 / BIL_STANDARD §22.2 / §14.3）：
    // 身份按 case 符号引用相等；payload 为 case 洞实参。
    // enum-struct 是值类型，Copy 深拷贝字段与 payload。

    public sealed class VmEnum : VmValue, IVmFieldHost
    {
        private readonly IVmFieldHost _slots;
        private readonly VmValue[] _payload;

        public string CaseSymbol { get; }
        public IReadOnlyList<VmValue> Payload => _payload;
        public override string TypeRef => _slots.TypeRef;
        public VmValue? Host
        {
            get => _slots.Host;
            set => _slots.Host = value;
        }

        public VmEnum(string enumType, string caseSymbol, IReadOnlyList<VmValue> payload)
        {
            _slots = new VmInstanceSlots(enumType);
            CaseSymbol = caseSymbol;
            _payload = new VmValue[payload.Count];
            for (var i = 0; i < payload.Count; i++)
            {
                _payload[i] = payload[i];
            }
        }

        private VmEnum(IVmFieldHost slots, string caseSymbol, VmValue[] payload)
        {
            _slots = slots;
            CaseSymbol = caseSymbol;
            _payload = payload;
        }

        public override VmValue Copy()
        {
            var copied = new VmValue[_payload.Length];
            for (var i = 0; i < _payload.Length; i++)
            {
                copied[i] = _payload[i].Copy();
            }
            return new VmEnum(_slots.DeepCopySlots(), CaseSymbol, copied);
        }

        public override string ToStandardText() => CaseSymbol;

        public bool SameCase(VmEnum other) => CaseSymbol == other.CaseSymbol;

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

        public IReadOnlyList<string> HiddenKeysInOrder => _slots.HiddenKeysInOrder;

        IVmFieldHost IVmFieldHost.DeepCopySlots() => _slots.DeepCopySlots();
    }
}
