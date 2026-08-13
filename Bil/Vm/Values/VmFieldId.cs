namespace RigiCompiler.Bil.Vm
{
    // fieldid 值（BIL_STANDARD §12.6 / §13.5；BIL_VM_DESIGN §3.2 / §6）：
    // 字段符号引用，携带 owner、值类型与 instance/static 类别。
    // getid.field 产物；get/set.field(.static).indirect 解引用此值。

    public sealed class VmFieldId : VmValue
    {
        public string FieldSymbol { get; }
        public string Owner { get; }
        public string FieldType { get; }
        public bool IsStatic { get; }

        public override string TypeRef =>
            ".fieldid<" + Owner + ", " + FieldType + ", "
            + (IsStatic ? "static" : "instance") + ">";

        public VmFieldId(string fieldSymbol)
        {
            FieldSymbol = fieldSymbol;
            if (BilVerificationContext.TryParseFieldSymbol(fieldSymbol,
                    out var owner, out var isStatic, out var fieldType))
            {
                Owner = owner;
                FieldType = fieldType;
                IsStatic = isStatic;
            }
            else
            {
                Owner = "";
                FieldType = ".any";
                IsStatic = false;
            }
        }

        public override VmValue Copy() => this;

        public override string ToStandardText() => FieldSymbol;
    }
}
