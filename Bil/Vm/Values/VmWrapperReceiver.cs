namespace RigiCompiler.Bil.Vm
{
    // wrapper place 的调用实参束，不是实例存储：状态与宿主独立传递。
    // 仅临时接收者及调用帧持有它，不能写进宿主的 Hidden 槽或普通字段。
    // 宿主自身为 wrapper place 时保留整条绑定链，嵌套 self 不丢失上层上下文。
    internal sealed class VmWrapperReceiver : VmValue, IVmFieldHost
    {
        internal VmValue State { get; }
        internal VmValue SelfArgument { get; }
        private IVmFieldHost Fields => (IVmFieldHost)State;

        internal VmWrapperReceiver(VmValue state, VmValue selfArgument)
        {
            if (state is not IVmFieldHost || state is VmWrapperReceiver)
                throw new VmException("wrapper receiver 必须绑定独立的实例状态");
            State = state;
            SelfArgument = selfArgument;
        }

        public override string TypeRef => State.TypeRef;
        public override VmValue Copy() => new VmWrapperReceiver(State.Copy(), SelfArgument);
        public override string ToStandardText() => State.ToStandardText();
        public bool TryReadField(string fieldSymbol, out VmValue value) =>
            Fields.TryReadField(fieldSymbol, out value);
        public void WriteField(string fieldSymbol, VmValue value) => Fields.WriteField(fieldSymbol, value);
        public bool TryReadHidden(string key, out VmValue value) => Fields.TryReadHidden(key, out value);
        public void WriteHidden(string key, VmValue value) => Fields.WriteHidden(key, value);
        public IReadOnlyList<string> HiddenKeysInOrder => Fields.HiddenKeysInOrder;
        public IVmFieldHost DeepCopySlots() => Fields.DeepCopySlots();
    }
}
