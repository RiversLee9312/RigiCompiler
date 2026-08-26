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

        // 数组对象固定前缀：头 16B + i32 length@16 + i32 填充@20（与 VM
        // core::Array#length@.i32 直读偏移对齐）。元素从 24 起按元素
        // ABI 步长排列——值类型（标量/String/本地 struct）按自身尺寸
        // 内联；引用/nullable/数组元素为 16B 胖槽。RUNTIME 普通泛型
        // 16B 胖值槽是长期 Array ABI；MW4 用元素原生步长对齐 VM 可观察
        // 语义（get/set 值、越界、零值）。变长元素引用不进静态 refMap
        //（MW7 扫描需按 length×stride 特判）。
        public const int ArrayPrefixSize = 24;
        public const int ArrayLengthOffset = 16;
        public const string ArrayLengthField = "core::Array#length@.i32";
        public const string ArrayTypeCanonical = "core::Array";

        // 值类型 Nullable 装箱的 typeid 哨兵（非对齐地址，不与 TypeSheet
        // 指针碰撞）：payload 对标量是零扩展位型，对 String/struct 是堆盒指针
        public const ulong NullableSentinel = 1UL;

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
                    if (IsNullable(type) || IsArray(type))
                    {
                        return FatReferenceType(context);
                    }
                    // 构造引用类型（Func\<TRet, T0\> 等）按胖引用槽；值类型
                    // 泛型具化未单独建布局计划，与 LayoutEngine 字段口径一致
                    return FatReferenceType(context);
            }
        }

        public static bool IsArray(MirType type) =>
            BilVerificationContext.StripTypeArguments(type.Canonical) == ArrayTypeCanonical;

        public static bool IsNullable(MirType type) =>
            BilVerificationContext.StripTypeArguments(type.Canonical) == "core::Nullable";

        public static bool IsTypeId(MirType type) =>
            BilVerificationContext.StripTypeArguments(type.Canonical) == "core::Type";

        public static bool IsGenericPlaceholder(MirType type) =>
            type.Canonical.Contains(".generic<", System.StringComparison.Ordinal)
            || type.Canonical.Contains("$.generic.", System.StringComparison.Ordinal);

        public static bool TryGetArrayElement(MirType type, out MirType element)
        {
            element = null!;
            return IsArray(type) && TryGetConstructorArgument(type.Canonical, 0, out element);
        }

        public static bool TryGetNullableInner(MirType type, out MirType inner)
        {
            inner = null!;
            return IsNullable(type) && TryGetConstructorArgument(type.Canonical, 0, out inner);
        }

        public static bool IsLengthField(string fieldSymbol) =>
            fieldSymbol == ArrayLengthField
            || (fieldSymbol.StartsWith(ArrayTypeCanonical + "<", System.StringComparison.Ordinal)
                && fieldSymbol.EndsWith("#length@.i32", System.StringComparison.Ordinal));

        public static ArrayElementAbi ClassifyElement(MirType element, TypeLayoutPlan? plan)
        {
            if (IsGenericPlaceholder(element))
            {
                throw new MwNotSupportedException(
                    $"泛型数组元素布局随单态化: {element.Canonical}");
            }
            if (IsArray(element) || IsNullable(element) || IsTypeId(element))
            {
                return ArrayElementAbi.Reference();
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

        public static (int Size, uint Flags) BuiltinSheetLayout(string canonical)
        {
            return canonical switch
            {
                "core::bool" or "core::i8" or "core::u8" => (1, TypeLayoutPlan.FlagInlineValue),
                "core::char" or "core::i16" or "core::u16" => (2, TypeLayoutPlan.FlagInlineValue),
                "core::i32" or "core::u32" or "core::float" => (4, TypeLayoutPlan.FlagInlineValue),
                "core::i64" or "core::u64" or "core::double" => (8, TypeLayoutPlan.FlagInlineValue),
                "core::String" => (16, TypeLayoutPlan.FlagInlineValue),
                "core::Any" or "core::Object" => (ReferenceSlotSize, 0u),
                ArrayTypeCanonical => (ArrayPrefixSize, 0u),
                _ => (0, 0u),
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
