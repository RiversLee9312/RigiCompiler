using System.Text;
using LLVMSharp.Interop;

namespace RigiCompiler.Middleware.Layout
{
    /// <summary>
    /// String ABI（MW7 定稿：不可变 + ARC 缓冲，字面量永生）：
    /// 值表示 { i8* data, i64 len } 不变；堆/字面量块 = { rc u32, reserved u32,
    /// data[] }，槽内 data = 块基址 + 8。C 边界一律经 rigi_string* 传递
    ///（StringIn = const rigi_string*，StringOut = rigi_string* 出参、置于
    /// 首参），避免 16 字节 struct 按值传递的 win-x64/SysV ABI 分歧。本类是
    /// 该表示的唯一事实源。
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

        // 字面量 ARC 块：{ i32 0xFFFFFFFF, i32 0, [N x i8] }；槽内 data =
        // 全局基址 + 8。空串 data=null（rigi_rt 对 len==0 不解引用 data）
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
                var blockType = context.GetStructType(new[]
                {
                    LLVMTypeRef.Int32,
                    LLVMTypeRef.Int32,
                    arrayType,
                }, false);
                var global = module.AddGlobal(blockType, "str." + globalName);
                global.Linkage = LLVMLinkage.LLVMInternalLinkage;
                global.IsGlobalConstant = true;
                global.Initializer = context.GetConstStruct(new[]
                {
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0xFFFFFFFF, false),
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false),
                    LLVMValueRef.CreateConstArray(LLVMTypeRef.Int8, elements),
                }, false);
                dataPointer = LLVMValueRef.CreateConstInBoundsGEP2(LLVMTypeRef.Int8, global,
                    new[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 8, false) });
            }
            var length = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)bytes.Length, false);
            return context.GetConstStruct(new[] { dataPointer, length }, false);
        }
    }
}
