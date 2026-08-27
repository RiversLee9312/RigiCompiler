using LLVMSharp.Interop;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Mir;

namespace RigiCompiler.Middleware.Layout
{
    /// <summary>
    /// 类型布局与 ABI（MIDDLEWARE_ARCHITECTURE §3 MW5 层，MW1 最小落地）：
    /// BIL canonical 类型 → LLVM 类型的唯一映射点。MW6 之外不得出现第二处
    /// 类型映射知识。引用槽按 RUNTIME §2 胖引用建模（128-bit，16 字节对齐），
    /// MW1 尚无消费者，接口答案先定稿防 ABI 迁移。
    /// </summary>
    public static class TypeLayout
    {
        // RUNTIME §2：胖引用槽 = { typeid(TypeSheet 地址，首字节复用分类 tag), payload }
        // 槽尺寸/对齐恒 16 字节（RUNTIME §3：对齐是布局要求，非原子性前提）
        public const int ReferenceSlotSize = 16;
        public const int ReferenceSlotAlignment = 16;

        // 数组对象固定前缀 32B：头 16B + elemSheet 指针@16 + i32 length@24
        // + pad@28（与 VM core::Array#length@.i32 及 arc.h 对齐）。元素从
        // 32 起按元素 ABI 步长排列——值类型（标量/String/本地 struct）按
        // 自身尺寸内联；引用/nullable/数组元素为 16B 胖槽。RUNTIME 普通
        // 泛型 16B 胖值槽是长期 Array ABI；MW4 用元素原生步长对齐 VM
        // 可观察语义（get/set 值、越界、零值）。变长元素引用不进静态
        // refMap（MW7 扫描需按 length×stride 特判）。
        public const int ArrayPrefixSize = 32;
        public const int ArrayLengthOffset = 24;
        public const int ArrayElemSheetOffset = 16;
        public const string ArrayLengthField = "core::Array#length@.i32";
        public const string ArrayTypeCanonical = "core::Array";
        // .typeid 构造族主键经 MwTypeKey.Normalize 为 core::Type<X>；
        // 无界成员 ≡ core::Type<core::Any>。不再坍缩单键 ".typeid"。
        public const string TypeIdUnboundedCanonical = "core::Type<core::Any>";
        // typeOf(null) 实际类型 sheet（VM ActualType = ".null"）
        public const string NullSheetCanonical = ".null";
        // Span/SharedSpan：与数组同构的连续缓冲区 class（RUNTIME §5）。
        // 具化构造类型各自出 sheet，不坍缩进 BuiltinSheetCanonicals。
        public const string SpanTypeCanonical = "core::Span";
        public const string SharedSpanTypeCanonical = "core::SharedSpan";
        public const string SpanLengthField = "core::Span#length@.i32";
        public const string SharedSpanLengthField = "core::SharedSpan#length@.i32";

        // refMap 编码（与 arc.h RIGI_REFMAP_* 对齐）：高 2 位 kind | 低 14 位跳数
        public const int RefMapKindShift = 14;
        public const int RefMapHopMask = 0x3FFF;
        public const int RefMapKindFatRef = 0;
        public const int RefMapKindString = 1;

        public static ushort EncodeRefMap(int kind, int hop) =>
            checked((ushort)((kind << RefMapKindShift) | hop));

        public static int RefMapKindOf(ushort entry) => entry >> RefMapKindShift;

        public static int RefMapHopOf(ushort entry) => entry & RefMapHopMask;

        public static LLVMTypeRef FatReferenceType(LLVMContextRef context)
        {
            return context.GetStructType(new[] { LLVMTypeRef.Int64, LLVMTypeRef.Int64 }, false);
        }

        // BIL 类型 → LLVM 类型。用户引用类型一律胖引用槽；用户值类型（struct/
        // enum struct）的展开布局随 MW4 对象系统落地。String 的过渡表示归
        // StringAbi（唯一事实源），此处仅映射
        public static LLVMTypeRef MapType(LLVMContextRef context, MirType type)
        {
            switch (type.Key)
            {
                case "void": return LLVMTypeRef.Void;
                case "bool": return LLVMTypeRef.Int1;
                case "char": return LLVMTypeRef.Int16;   // UTF-16 码元，与 VM 的 C# char 对齐
                case "i8": case "u8": return LLVMTypeRef.Int8;
                case "i16": case "u16": return LLVMTypeRef.Int16;
                case "i32": case "u32": return LLVMTypeRef.Int32;
                case "i64": case "u64": return LLVMTypeRef.Int64;
                case "float": return LLVMTypeRef.Float;
                case "double": return LLVMTypeRef.Double;
                case "String": return StringAbi.ValueType(context);
                default:
                    if (IsTypeId(type))
                    {
                        return LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
                    }
                    if (IsNullable(type) || IsArray(type) || IsSpanLike(type))
                    {
                        return FatReferenceType(context);
                    }
                    // 构造引用类型（Func\<TRet, T0\> 等）按胖引用槽；值类型
                    // 泛型具化未单独建布局计划，与 LayoutEngine 字段口径一致
                    return FatReferenceType(context);
            }
        }

        public static bool IsArray(MirType type) =>
            IsHead(type.Canonical, ArrayTypeCanonical);

        public static bool IsSpan(MirType type) =>
            IsHead(type.Canonical, SpanTypeCanonical);

        public static bool IsSharedSpan(MirType type) =>
            IsHead(type.Canonical, SharedSpanTypeCanonical);

        public static bool IsSpanLike(MirType type) =>
            IsSpan(type) || IsSharedSpan(type);

        // 数组 / Span / SharedSpan：元素按原生 stride 连续存放，访问同构
        public static bool IsContiguousBuffer(MirType type) =>
            IsArray(type) || IsSpanLike(type);

        public static bool IsSpanCanonical(string canonical) =>
            IsHead(canonical, SpanTypeCanonical);

        public static bool IsSharedSpanCanonical(string canonical) =>
            IsHead(canonical, SharedSpanTypeCanonical);

        public static bool IsNullable(MirType type) =>
            BilVerificationContext.StripTypeArguments(type.Canonical) == "core::Nullable";

        public static bool IsTypeId(MirType type) =>
            IsTypeIdCanonical(type.Canonical);

        // 含无界 .typeid / core::Type / core::Type<X>
        public static bool IsTypeIdCanonical(string canonical)
        {
            var stripped = BilVerificationContext.StripTypeArguments(canonical);
            return stripped == "core::Type" || stripped == ".typeid";
        }

        public static bool IsGenericPlaceholder(MirType type) =>
            type.Canonical.Contains(".generic<", System.StringComparison.Ordinal)
            || type.Canonical.Contains("$.generic.", System.StringComparison.Ordinal);

        public static bool TryGetArrayElement(MirType type, out MirType element)
        {
            element = null!;
            return IsArray(type) && TryGetConstructorArgument(type.Canonical, 0, out element);
        }

        public static bool TryGetContiguousElement(MirType type, out MirType element)
        {
            element = null!;
            return IsContiguousBuffer(type)
                && TryGetConstructorArgument(type.Canonical, 0, out element);
        }

        public static bool TryGetNullableInner(MirType type, out MirType inner)
        {
            inner = null!;
            return IsNullable(type) && TryGetConstructorArgument(type.Canonical, 0, out inner);
        }

        public static bool IsLengthField(string fieldSymbol) =>
            fieldSymbol == ArrayLengthField
            || fieldSymbol == SpanLengthField
            || fieldSymbol == SharedSpanLengthField
            || IsConstructedLengthField(fieldSymbol, ArrayTypeCanonical)
            || IsConstructedLengthField(fieldSymbol, SpanTypeCanonical)
            || IsConstructedLengthField(fieldSymbol, SharedSpanTypeCanonical);

        private static bool IsConstructedLengthField(string fieldSymbol, string head) =>
            fieldSymbol.StartsWith(head + "<", System.StringComparison.Ordinal)
            && fieldSymbol.EndsWith("#length@.i32", System.StringComparison.Ordinal);

        private static bool IsHead(string canonical, string head) =>
            BilVerificationContext.StripTypeArguments(canonical) == head;

        public static ArrayElementAbi ClassifyElement(MirType element, TypeLayoutPlan? plan)
        {
            if (IsGenericPlaceholder(element))
            {
                throw new MwNotSupportedException(
                    $"泛型数组元素布局随单态化: {element.Canonical}");
            }
            if (IsArray(element) || IsNullable(element))
            {
                return ArrayElementAbi.Reference();
            }
            if (IsTypeId(element))
            {
                // .typeid = 8B 内联 sheet 指针（sheet 定稿 FlagInlineValue +
                // typeSize=8，与 BuiltinSheetLayout 一致）；rigi_alloc_array
                // 按 sheet 算 stride=8，误按 16B 胖槽写会越出分配堆破坏
                return ArrayElementAbi.Scalar(8, 8);
            }
            switch (element.Key)
            {
                case "bool" or "i8" or "u8":
                    return ArrayElementAbi.Scalar(1, 1);
                case "char" or "i16" or "u16":
                    return ArrayElementAbi.Scalar(2, 2);
                case "i32" or "u32" or "float":
                    return ArrayElementAbi.Scalar(4, 4);
                case "i64" or "u64" or "double":
                    return ArrayElementAbi.Scalar(8, 8);
                case "String":
                    return ArrayElementAbi.StringSlot();
            }
            if (plan is { Kind: TypeLayoutKind.Struct or TypeLayoutKind.Enum })
            {
                return ArrayElementAbi.Inline(plan);
            }
            return ArrayElementAbi.Reference();
        }

        public static string BuiltinSheetCanonical(MirType type)
        {
            return type.Key switch
            {
                "bool" => "core::bool",
                "char" => "core::char",
                "i8" => "core::i8",
                "u8" => "core::u8",
                "i16" => "core::i16",
                "u16" => "core::u16",
                "i32" => "core::i32",
                "u32" => "core::u32",
                "i64" => "core::i64",
                "u64" => "core::u64",
                "float" => "core::float",
                "double" => "core::double",
                "String" => "core::String",
                _ => type.Canonical,
            };
        }

        // 托管槽分类（Layout 层单一知识点；pass 与 ArcEmitter 共用）
        public static ManagedSlotKind ClassifySlot(MwContext context, MirType type) =>
            ClassifySlot(context.Layout, type);

        public static ManagedSlotKind ClassifySlot(LayoutPlanTable? layout, MirType type)
        {
            if (type.Key == "String")
            {
                return ManagedSlotKind.String;
            }
            if (type.IsVoid || IsTypeId(type))
            {
                return ManagedSlotKind.Unmanaged;
            }
            switch (type.Key)
            {
                case "bool" or "char"
                    or "i8" or "u8" or "i16" or "u16"
                    or "i32" or "u32" or "i64" or "u64"
                    or "float" or "double":
                    return ManagedSlotKind.Unmanaged;
            }
            if (layout?.Find(type.Canonical) is { Kind: TypeLayoutKind.Struct or TypeLayoutKind.Enum } plan)
            {
                return plan.RefMapCount > 0
                    ? ManagedSlotKind.RichValue
                    : ManagedSlotKind.Unmanaged;
            }
            return ManagedSlotKind.FatReference;
        }

        public static bool IsManagedSlot(ManagedSlotKind kind) =>
            kind != ManagedSlotKind.Unmanaged;

        public static bool IsManagedSlot(MwContext context, MirType type) =>
            IsManagedSlot(ClassifySlot(context, type));

        public static bool IsManagedSlot(LayoutPlanTable? layout, MirType type) =>
            IsManagedSlot(ClassifySlot(layout, type));

        public static (int Size, uint Flags) BuiltinSheetLayout(string canonical)
        {
            return canonical switch
            {
                "core::bool" or "core::i8" or "core::u8" => (1, TypeLayoutPlan.FlagInlineValue),
                "core::char" or "core::i16" or "core::u16" => (2, TypeLayoutPlan.FlagInlineValue),
                "core::i32" or "core::u32" or "core::float" => (4, TypeLayoutPlan.FlagInlineValue),
                "core::i64" or "core::u64" or "core::double" => (8, TypeLayoutPlan.FlagInlineValue),
                "core::String" => (16, TypeLayoutPlan.FlagInlineValue | TypeLayoutPlan.FlagString),
                "core::Any" or "core::Object" => (ReferenceSlotSize, 0u),
                ArrayTypeCanonical => (ArrayPrefixSize, TypeLayoutPlan.FlagArray),
                NullSheetCanonical => (0, 0u),
                _ => IsTypeIdCanonical(canonical)
                    ? (8, TypeLayoutPlan.FlagInlineValue)
                    : (0, 0u),
            };
        }

        public static readonly string[] BuiltinSheetCanonicals =
        {
            "core::bool", "core::char",
            "core::i8", "core::u8", "core::i16", "core::u16",
            "core::i32", "core::u32", "core::i64", "core::u64",
            "core::float", "core::double", "core::String",
            "core::Any", "core::Object",
            ArrayTypeCanonical,
            NullSheetCanonical,
        };

        private static bool TryGetConstructorArgument(string canonical, int index, out MirType argument)
        {
            argument = null!;
            var angle = canonical.IndexOf('<');
            if (angle < 0 || !canonical.EndsWith(">"))
            {
                return false;
            }
            var inner = canonical.Substring(angle + 1, canonical.Length - angle - 2);
            var parts = BilVerificationContext.SplitTopLevel(inner);
            if (index < 0 || index >= parts.Count)
            {
                return false;
            }
            argument = MirType.Of(parts[index]);
            return true;
        }
    }

    public enum ManagedSlotKind
    {
        Unmanaged,
        FatReference,
        String,
        RichValue,
    }

    public enum ArrayElementKind
    {
        Scalar,
        String,
        Reference,
        InlineValue,
    }

    public readonly struct ArrayElementAbi
    {
        public ArrayElementKind Kind { get; }
        public int Stride { get; }
        public int Alignment { get; }
        public TypeLayoutPlan? Plan { get; }

        private ArrayElementAbi(ArrayElementKind kind, int stride, int alignment, TypeLayoutPlan? plan)
        {
            Kind = kind;
            Stride = stride;
            Alignment = alignment;
            Plan = plan;
        }

        public static ArrayElementAbi Scalar(int stride, int alignment) =>
            new(ArrayElementKind.Scalar, stride, alignment, null);

        public static ArrayElementAbi StringSlot() =>
            new(ArrayElementKind.String, 16, 8, null);

        public static ArrayElementAbi Reference() =>
            new(ArrayElementKind.Reference, TypeLayout.ReferenceSlotSize,
                TypeLayout.ReferenceSlotAlignment, null);

        public static ArrayElementAbi Inline(TypeLayoutPlan plan) =>
            new(ArrayElementKind.InlineValue, plan.Size, plan.Alignment, plan);
    }
}
