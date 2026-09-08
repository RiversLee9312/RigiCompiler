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
        // 裸指针直传（无 String ABI 编组；MW9a 异常三面）
        Ptr,
    }

    public static class RuntimeFaces
    {
        public const string Print = "rigi_print";
        public const string PrintErr = "rigi_print_err";
        public const string StringConcat = "rigi_string_concat";
        // 唯一带返回值的面：i32 三态结果（<0/0/>0）；不走 EmitFaceCall 的
        // 出参槽形态，调用与次序判定归 CallEmitter/ScalarEmitter 专线
        public const string StringCompare = "rigi_string_compare";
        // i64 MIN/-1 基础设施溢出失败（VM 基准非语言级异常）：void(void)
        // noreturn（stderr 文本 + 退出码对齐 VM 未捕获异常出口），调用方
        // 在调用后补 unreachable。MW9b-G：除零/cast/拆箱/new.indirect/
        // 越界写三占位 abort 面已退场（换抛真异常，ExceptionEmitter）
        public const string AbortArithmeticOverflow = "rigi_abort_arithmetic_overflow";
        // typeOf 值形态：胖引用 → 实际 TypeSheet*（tag2 对象头 / tag0·tag1 掩码）
        public const string TypeOf = "rigi_typeof";
        // 动态 cast（占位目标）：is 命中改写视图 typeid；数值互转；失败返 0
        public const string TryCast = "rigi_try_cast";
        // 浮点→整数（NaN→0，溢出饱和到 32/64 位宽再截断；对齐 C# unchecked conv）
        public const string CastF64ToInt = "rigi_cast_f64_to_int";
        public const string AllocArray = "rigi_alloc_array";
        public const string SpanAlloc = "rigi_span_alloc";
        public const string AbortArrayNegativeLength = "rigi_abort_array_negative_length";
        public const string Malloc = "rigi_malloc";
        // ownership region + 值语义四面族 + String ARC（裸 i64/指针，
        // 不走 StringIn/StringOut）
        public const string RegionEnter = "rigi_region_enter";
        public const string RegionExit = "rigi_region_exit";
        public const string RefAcquire = "rigi_ref_acquire";
        public const string RefRelease = "rigi_ref_release";
        public const string ValueAcquire = "rigi_value_acquire";
        public const string ValueRelease = "rigi_value_release";
        public const string StringAcquire = "rigi_string_acquire";
        public const string StringRelease = "rigi_string_release";
        // MW9a 异常传输三面（线程局部 pending 槽）：raise void(ptr) 写槽，
        // pending ptr() 借用查询，take ptr() 移动取走（归还形状不由 ShapeOf 表达）
        public const string ExcRaise = "rigi_exc_raise";
        public const string ExcPending = "rigi_exc_pending";
        public const string ExcTake = "rigi_exc_take";
        // MW12b §25.2：dispose 进入置位（void(ptr)，Emit 经 DeclareHelperFace
        // 直用）与全局异常通道出队（i32(rigi_string*)，entry stub drain 用；
        // 同族的 report/flush 只被 C 内部与 shim atexit 引用，不占本表）
        public const string MarkDisposed = "rigi_mark_disposed";
        public const string GexcTake = "rigi_gexc_take";
        // MW9a 第 C 棒顶层 reporter 两面：诊断名取回 void(ptr, rigi_string*)
        // （obj→sheet→TypeInfo.name 借用拷出）；未捕获出口 void(void)
        // noreturn（exit 1，调用方补 unreachable）
        public const string TypeNameOf = "rigi_type_name_of";
        public const string ExcHalt = "rigi_exc_halt";
        // MW11c 棒5a：MW11a/b 旧协程面族（rigi_root_begin/end、
        // rigi_executor_run、rigi_take_unobserved_failure、rigi_spawn、
        // rigi_task_current/wait/complete/fail、rigi_yield、
        // rigi_yield_alarm）已随转向删除——运行时交互点改为 Rigi 世界
        // 方法调（Task/Dispatcher，普通 MirCall）+ 最小原语指令
        //（MirCoroutineCreate/MirFailureLoad，Emit 内字面量声明），
        // 不占用本表
        // type.is / supers / with（含 .indirect）：目标 sheet 判定，i32 三态
        public const string TypeIs = "rigi_type_is";
        public const string TypeIsIndirect = "rigi_type_is_indirect";
        public const string TypeSupers = "rigi_type_supers";
        public const string TypeSupersIndirect = "rigi_type_supers_indirect";
        public const string TypeWith = "rigi_type_with";
        public const string TypeWithIndirect = "rigi_type_with_indirect";

        public static IReadOnlyList<RuntimeFaceParam> ShapeOf(string faceSymbol)
        {
            return faceSymbol switch
            {
                Print or PrintErr => new[] { RuntimeFaceParam.StringIn },
                StringConcat => new[] { RuntimeFaceParam.StringOut, RuntimeFaceParam.StringIn, RuntimeFaceParam.StringIn },
                StringCompare => new[] { RuntimeFaceParam.StringIn, RuntimeFaceParam.StringIn },
                AbortArithmeticOverflow or AbortArrayNegativeLength =>
                    System.Array.Empty<RuntimeFaceParam>(),
                ExcRaise => new[] { RuntimeFaceParam.Ptr },
                ExcPending or ExcTake => System.Array.Empty<RuntimeFaceParam>(),
                TypeNameOf => new[] { RuntimeFaceParam.Ptr, RuntimeFaceParam.Ptr },
                ExcHalt => System.Array.Empty<RuntimeFaceParam>(),
                _ => throw new MwNotSupportedException($"未知 rigi_rt 运行时面: {faceSymbol}"),
            };
        }

        // 消息队列不占原生面：单份 Rigi 标准库仅组合安全容器与协程基础设施。
        // native 声明 (lib, symbol) → C 符号（RUNTIME §26 的库解析留白在
        // Middleware 定稿）：rigi_rt 面 C 名 = rigi_ + symbol（既有约定）；
        // 任意用户库（L6 起放行）C 名 = symbol 原文，链接输入经
        // native --link <路径...> 追加（NativeCommand）。C 边界 ABI 与
        // rigi_rt 面同一套（String → rigi_string* / out 首参 / bool→i8，
        // 见 NativeCallEmitter），用户 C 函数按此形状书写。
        public static string MapNativeSymbol(string library, string symbol)
        {
            return library == "rigi_rt" ? "rigi_" + symbol : symbol;
        }
    }
}
