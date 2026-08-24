using LLVMSharp.Interop;

namespace RigiCompiler.Middleware
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

        // String 的 MW1 过渡表示（RUNTIME §4 留白由本层定稿）：
        // { i8* data, i64 len } UTF-8 裸缓冲区，按值语义；MW7 胖值化时迁移
        public static LLVMTypeRef StringType(LLVMContextRef context)
        {
            return context.GetStructType(new[]
            {
                LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
                LLVMTypeRef.Int64,
            }, false);
        }

        public static LLVMTypeRef FatReferenceType(LLVMContextRef context)
        {
            return context.GetStructType(new[] { LLVMTypeRef.Int64, LLVMTypeRef.Int64 }, false);
        }

        // BIL 类型 → LLVM 类型。用户引用类型一律胖引用槽；用户值类型（struct/
        // enum struct）的展开布局随 MW4 对象系统落地
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
                case "String": return StringType(context);
                default:
                    if (type.Canonical.Contains('<'))
                    {
                        throw new MwNotSupportedException($"MW1 不支持构造类型: {type.Canonical}");
                    }
                    // 任意引用类型（class/interface/Any/Object…）→ 胖引用槽
                    return FatReferenceType(context);
            }
        }
    }
}
