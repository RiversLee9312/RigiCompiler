namespace RigiCompiler.Bil.Vm
{
    // 语言级异常的 VM 承载（BIL_VM_DESIGN §4.3 / BIL_STANDARD §16.9 / §22.2）：
    // 包装异常对象 VmValue；沿块执行栈与调用帧链按 catch-table 展开（V4）。
    // 无 ExceptionObject 的同族实例表达基础设施错误（未实现指令、未知 hook）。

    public class VmException : Exception
    {
        public VmValue? ExceptionObject { get; }

        public VmException(string message, VmValue? exceptionObject = null, Exception? inner = null)
            : base(message, inner)
        {
            ExceptionObject = exceptionObject;
        }
    }

    public sealed class VmUnimplementedInstructionException : VmException
    {
        public string Opcode { get; }

        public VmUnimplementedInstructionException(string opcode)
            : base("BIL 指令未实现：" + opcode)
        {
            Opcode = opcode;
        }
    }

    public sealed class VmNativeHookException : VmException
    {
        public string Library { get; }
        public string NativeSymbol { get; }

        public VmNativeHookException(string library, string nativeSymbol)
            : base("未知 native hook：(" + library + ", " + nativeSymbol + ")")
        {
            Library = library;
            NativeSymbol = nativeSymbol;
        }
    }
}
