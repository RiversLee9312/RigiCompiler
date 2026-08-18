using System.Globalization;

namespace RigiCompiler.Bil.Vm
{
    // 抽象值模型（BIL_VM_DESIGN §3 / BIL_STANDARD §22.1 / §22.3）：
    // 每种内建精确类型独立子类，运算查询键依赖可分辨的精确类型。
    // V1 标量 + void + null；V2 增 VmObject / VmEnum / VmArray；
    // V3 增 VmAny / VmNullable / VmFieldId。
    // 值语义拷贝由 Copy() 表达；标量不可变 Copy 返回自身。

    public abstract class VmValue
    {
        public abstract string TypeRef { get; }
        public abstract VmValue Copy();
        public abstract string ToStandardText();
    }

    public sealed class VmI8 : VmValue
    {
        public sbyte Value { get; }
        public VmI8(sbyte value) { Value = value; }
        public override string TypeRef => ".i8";
        public override VmValue Copy() => this;
        public override string ToStandardText() => Value.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class VmI16 : VmValue
    {
        public short Value { get; }
        public VmI16(short value) { Value = value; }
        public override string TypeRef => ".i16";
        public override VmValue Copy() => this;
        public override string ToStandardText() => Value.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class VmI32 : VmValue
    {
        public int Value { get; }
        public VmI32(int value) { Value = value; }
        public override string TypeRef => ".i32";
        public override VmValue Copy() => this;
        public override string ToStandardText() => Value.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class VmI64 : VmValue
    {
        public long Value { get; }
        public VmI64(long value) { Value = value; }
        public override string TypeRef => ".i64";
        public override VmValue Copy() => this;
        public override string ToStandardText() => Value.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class VmU8 : VmValue
    {
        public byte Value { get; }
        public VmU8(byte value) { Value = value; }
        public override string TypeRef => ".u8";
        public override VmValue Copy() => this;
        public override string ToStandardText() => Value.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class VmU16 : VmValue
    {
        public ushort Value { get; }
        public VmU16(ushort value) { Value = value; }
        public override string TypeRef => ".u16";
        public override VmValue Copy() => this;
        public override string ToStandardText() => Value.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class VmU32 : VmValue
    {
        public uint Value { get; }
        public VmU32(uint value) { Value = value; }
        public override string TypeRef => ".u32";
        public override VmValue Copy() => this;
        public override string ToStandardText() => Value.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class VmU64 : VmValue
    {
        public ulong Value { get; }
        public VmU64(ulong value) { Value = value; }
        public override string TypeRef => ".u64";
        public override VmValue Copy() => this;
        public override string ToStandardText() => Value.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class VmF32 : VmValue
    {
        public float Value { get; }
        public VmF32(float value) { Value = value; }
        public override string TypeRef => ".f32";
        public override VmValue Copy() => this;
        public override string ToStandardText() => Value.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class VmF64 : VmValue
    {
        public double Value { get; }
        public VmF64(double value) { Value = value; }
        public override string TypeRef => ".f64";
        public override VmValue Copy() => this;
        public override string ToStandardText() => Value.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class VmBool : VmValue
    {
        public bool Value { get; }
        public VmBool(bool value) { Value = value; }
        public override string TypeRef => ".bool";
        public override VmValue Copy() => this;
        public override string ToStandardText() => Value ? "true" : "false";
    }

    public sealed class VmChar : VmValue
    {
        public char Value { get; }
        public VmChar(char value) { Value = value; }
        public override string TypeRef => ".char";
        public override VmValue Copy() => this;
        public override string ToStandardText() => Value.ToString();
    }

    public sealed class VmString : VmValue
    {
        // length 是 .bootstrap.rg 的 ext const 内建字段（core::String#length@.i64），
        // VM 直读（同 VmArray.LengthFieldSymbol 先例）
        internal const string LengthFieldSymbol = "core::String#length@.i64";

        public string Value { get; }
        public VmString(string value) { Value = value; }
        public override string TypeRef => ".string";
        public override VmValue Copy() => this;
        public override string ToStandardText() => Value;
    }

    public sealed class VmVoid : VmValue
    {
        public static readonly VmVoid Instance = new VmVoid();
        private VmVoid() { }
        public override string TypeRef => ".void";
        public override VmValue Copy() => this;
        public override string ToStandardText() => "";
    }

    public sealed class VmNull : VmValue
    {
        public static readonly VmNull Instance = new VmNull();
        private VmNull() { }
        public override string TypeRef => ".null";
        public override VmValue Copy() => this;
        public override string ToStandardText() => "null";
    }

    // .any 胖值（BIL_VM_DESIGN §3.2 / BIL_STANDARD §12.1）：自描述 typeid + payload。
    public sealed class VmAny : VmValue
    {
        public VmTypeId TypeId { get; }
        public VmValue Payload { get; }
        public override string TypeRef => ".any";

        public VmAny(VmValue payload)
        {
            Payload = payload.Copy();
            TypeId = new VmTypeId(payload.TypeRef);
        }

        public VmAny(VmTypeId typeId, VmValue payload)
        {
            TypeId = typeId;
            Payload = payload.Copy();
        }

        public override VmValue Copy() => new VmAny(TypeId, Payload);

        public override string ToStandardText() => Payload.ToStandardText();
    }

    // 值类型 .nullable<T>（BIL_VM_DESIGN §3.2）：存在位包装。
    // 引用类型 nullable 仍用 VmNull；cast.safe 失败一律 VmNull。
    public sealed class VmNullable : VmValue
    {
        public string InnerType { get; }
        public VmValue? Value { get; }
        public bool HasValue => Value != null && Value is not VmNull;
        public override string TypeRef => ".nullable<" + InnerType + ">";

        public VmNullable(string innerType, VmValue? value)
        {
            InnerType = innerType;
            Value = value;
        }

        public override VmValue Copy() =>
            new VmNullable(InnerType, Value?.Copy());

        public override string ToStandardText() =>
            HasValue ? Value!.ToStandardText() : "null";
    }
}
