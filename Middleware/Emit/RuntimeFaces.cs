using System.Collections.Generic;

namespace RigiCompiler.Middleware
{
    // rigi_rt 运行时面的 C 符号与调用形状（MIDDLEWARE_ARCHITECTURE §4.8）。
    // 面表是 Emit 对 rigi_rt ABI 的唯一知识点；形状与 rigi_rt/*.c 逐一对应，
    // 新增面时两侧同步。String 的 C 边界传递约定（rigi_string* 出入参）由
    // Layout/StringAbi 定稿（唯一事实源），本表只描述参数位形态。
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

        public static IReadOnlyList<RuntimeFaceParam> ShapeOf(string faceSymbol)
        {
            return faceSymbol switch
            {
                Print or PrintErr => new[] { RuntimeFaceParam.StringIn },
                StringConcat => new[] { RuntimeFaceParam.StringOut, RuntimeFaceParam.StringIn, RuntimeFaceParam.StringIn },
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
