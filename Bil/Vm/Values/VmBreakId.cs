namespace RigiCompiler.Bil.Vm
{
    // .breakid 结构化控制 capability（BIL_VM_DESIGN §3.2 / BIL_STANDARD §16.5）：
    // 不透明句柄，指向 loop/loop.rev/switch 的块执行栈区域帧。
    // 不得跨函数、存入字段/数组或传给普通方法；Copy 返回自身。

    public sealed class VmBreakId : VmValue
    {
        public VmBlockFrame Region { get; }
        public bool AllowsContinue { get; }
        public override string TypeRef => ".breakid";

        public VmBreakId(VmBlockFrame region, bool allowsContinue)
        {
            Region = region;
            AllowsContinue = allowsContinue;
        }

        public override VmValue Copy() => this;

        public override string ToStandardText() => ".breakid";
    }
}
