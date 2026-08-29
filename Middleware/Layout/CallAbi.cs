using RigiCompiler.Middleware.Mir;

namespace RigiCompiler.Middleware.Layout
{
    /// <summary>
    /// 调用约定描述符（无 LLVM）：内联值类型返回走隐藏 out 首参；
    /// C 边界标量按值、String/胖引用走 out 首参。Emit 只填 LLVM 类型与指针。
    /// typeid 隐藏槽位置在 <see cref="TypeLayoutPlan.HiddenTypeIdSlots"/>。
    /// </summary>
    public static class CallAbi
    {
        // C 边界按值传递的标量（不含 String；String 走 rigi_string*）
        public static bool IsNativeByValue(MirType type) => type.Key is
            "bool" or "char" or "i8" or "u8" or "i16" or "u16"
            or "i32" or "u32" or "i64" or "u64" or "float" or "double";

        // native 返回走 out 首参：String → rigi_string*；用户引用 → 16B 胖槽指针
        public static bool NeedsNativeOutSlot(MirType returnType)
        {
            if (returnType.IsString)
            {
                return true;
            }
            if (returnType.IsVoid || IsNativeByValue(returnType) || TypeLayout.IsTypeId(returnType))
            {
                return false;
            }
            return true;
        }

        // 本地 struct/enum 返回：隐藏 out 首参（Emit 映射为指针 + void）
        public static bool ReturnsViaOutPointer(TypeLayoutPlan? plan) =>
            plan is { Kind: TypeLayoutKind.Struct or TypeLayoutKind.Enum or TypeLayoutKind.Wrapper };
    }
}
