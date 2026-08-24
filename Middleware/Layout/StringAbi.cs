using System.Text;
using LLVMSharp.Interop;

namespace RigiCompiler.Middleware
{
    /// <summary>
    /// String 的 MW1 过渡 ABI（RUNTIME §4 留白由 Layout 层定稿）：
    /// 值表示 { i8* data, i64 len } UTF-8 裸缓冲区，按值语义；C 边界一律经
    /// rigi_string* 传递（StringIn = const rigi_string*，StringOut =
    /// rigi_string* 出参、置于首参），避免 16 字节 struct 按值传递的
    /// win-x64/SysV ABI 分歧。本类是该表示的唯一事实源：TypeLayout 的类型
    /// 映射、RuntimeFaces 的面形状、Emit 的常量构建全部经此；MW7 胖值化时
    /// 迁移点即本类与 RuntimeFaces 面表（MIDDLEWARE_ARCHITECTURE §7）。
    /// </summary>
    public static class StringAbi
    {
        // { i8* data, i64 len }（Rigi 内部按值持有）
        public static LLVMTypeRef ValueType(LLVMContextRef context)
        {
            return context.GetStructType(new[]
            {
                LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
                LLVMTypeRef.Int64,
            }, false);
        }

        // rigi_string*（C 边界传参与出参槽共用形态）
        public static LLVMTypeRef PointerType(LLVMContextRef context)
        {
            return LLVMTypeRef.CreatePointer(ValueType(context), 0);
        }

        // 字符串常量：UTF-8 字节入内部全局，指针经 ConstGEP 取；空串 data
        // 为 null（rigi_rt 对 len==0 不解引用 data）
        public static LLVMValueRef BuildConstant(LLVMModuleRef module, string text, string globalName)
        {
            var context = module.Context;
            var bytes = Encoding.UTF8.GetBytes(text);
            var bytePtrType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
            LLVMValueRef dataPointer;
            if (bytes.Length == 0)
            {
                dataPointer = LLVMValueRef.CreateConstPointerNull(bytePtrType);
            }
            else
            {
                var arrayType = LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)bytes.Length);
                var elements = new LLVMValueRef[bytes.Length];
                for (var i = 0; i < bytes.Length; i++)
                {
                    elements[i] = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, bytes[i], false);
                }
                var global = module.AddGlobal(arrayType, "str." + globalName);
                global.Linkage = LLVMLinkage.LLVMInternalLinkage;
                global.IsGlobalConstant = true;
                global.Initializer = LLVMValueRef.CreateConstArray(LLVMTypeRef.Int8, elements);
                var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false);
                dataPointer = LLVMValueRef.CreateConstInBoundsGEP2(arrayType, global, new[] { zero, zero });
            }
            var length = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)bytes.Length, false);
            return context.GetConstStruct(new[] { dataPointer, length }, false);
        }
    }
}
