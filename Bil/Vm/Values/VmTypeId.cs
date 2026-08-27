namespace RigiCompiler.Bil.Vm
{
    // typeid 值（BIL_STANDARD §12.5 / §7.1；BIL_VM_DESIGN §3.2）：
    // 最小表示 = 类型符号引用字符串。V3 会扩展为 TypeSheet 句柄。
    // getid.type 产物；泛型 hidden `.generic.T` 槽位同构。

    public sealed class VmTypeId : VmValue
    {
        public string TypeSymbol { get; }
        // A(b)：值类型 .typeid<X>，X = 所指类型；is Type<X> 可判别边界
        public override string TypeRef => ".typeid<" + TypeSymbol + ">";

        public VmTypeId(string typeSymbol)
        {
            TypeSymbol = typeSymbol;
        }

        public override VmValue Copy() => this;

        public override string ToStandardText() => TypeSymbol;
    }
}
