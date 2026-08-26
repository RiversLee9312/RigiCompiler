using System.Collections.Generic;

namespace RigiCompiler.Middleware.Runtime
{
    // rigi_rt 运行时面的 C 符号与调用形状（MIDDLEWARE_ARCHITECTURE §4.8）。
    // 面表是 Middleware 对 rigi_rt ABI 的唯一知识点（本表驻 Runtime 模块，与
    // RigiRtBuilder 同属对 C 运行时的契约；Binding/Emit 消费）；形状与
    // rigi_rt/*.c 逐一对应，新增面时两侧同步。String 的 C 边界传递约定
    //（rigi_string* 出入参）由 Layout/StringAbi 定稿（唯一事实源），本表
    // 只描述参数位形态。
    public enum RuntimeFaceParam
    {
        StringIn,
        StringOut,
    }

    public static class RuntimeFaces
    {
        public const string Print = "rigi_print";
        public const string PrintErr = "rigi_print_err";
        public const string StringConcat = "rigi_string_concat";
        // 唯一带返回值的面：i32 三态结果（<0/0/>0）；不走 EmitFaceCall 的
        // 出参槽形态，调用与次序判定归 CallEmitter/ScalarEmitter 专线
        public const string StringCompare = "rigi_string_compare";
        // MW2 占位检查面：void(void) noreturn（stderr 文本 + 退出码对齐
        // VM 未捕获异常出口），调用方在调用后补 unreachable；MW9 换真异常
        // 时由标量检查策略注入点整体替换
        public const string AbortDividedByZero = "rigi_abort_divided_by_zero";
        public const string AbortArithmeticOverflow = "rigi_abort_arithmetic_overflow";
        // 拆箱不符：void(TypeSheet*) noreturn，消息含目标 TypeInfo.name
        public const string AbortInvalidCast = "rigi_abort_invalid_cast";
        public const string AllocArray = "rigi_alloc_array";
        public const string AbortArrayOob = "rigi_abort_array_oob";
        public const string AbortArrayNegativeLength = "rigi_abort_array_negative_length";
        public const string Malloc = "rigi_malloc";

        public static IReadOnlyList<RuntimeFaceParam> ShapeOf(string faceSymbol)
        {
            return faceSymbol switch
            {
                Print or PrintErr => new[] { RuntimeFaceParam.StringIn },
                StringConcat => new[] { RuntimeFaceParam.StringOut, RuntimeFaceParam.StringIn, RuntimeFaceParam.StringIn },
                StringCompare => new[] { RuntimeFaceParam.StringIn, RuntimeFaceParam.StringIn },
                AbortDividedByZero or AbortArithmeticOverflow or AbortArrayNegativeLength
                    or AbortInvalidCast =>
                    System.Array.Empty<RuntimeFaceParam>(),
                _ => throw new MwNotSupportedException($"未知 rigi_rt 运行时面: {faceSymbol}"),
            };
        }

        // native 声明 (lib, symbol) → C 符号（RUNTIME §26：lib 恒 rigi_rt，
        // C 名 = rigi_ + symbol）
        public static string MapNativeSymbol(string library, string symbol)
        {
            if (library != "rigi_rt")
            {
                throw new MwNotSupportedException($"MW1 不支持 native 库: {library}");
            }
            return "rigi_" + symbol;
        }
    }
}
