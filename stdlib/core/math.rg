// ============================================================================
// math.rg —— 施工块 6-4：core.math 函数与常量
// （STDLIB §4.11.1–§4.11.3 / 维护契约 D7）
//
//   - 普通函数 + 具体类型重载（i8/i16/i32/i64/u8/u16/u32/u64 + float/
//     double），不引入 Numeric 泛型协议（§4.11.1）。各重载按自身目标
//     类型运算和返回，不先统一成 double。
//   - 常量 pi/e/tau 固定为相应数学常数在 double 中的最近值、正中取偶
//     表示。字面量由编译器前端 double.Parse（InvariantCulture，IEEE
//     正确舍入）单一路径解析后分发 VM/native 两侧，位表示天然相同，
//     不受宿主地区或运行时浮点环境影响（§4.11.1）。需要 float 时由
//     调用者显式 `as float` 转换（如 `core.math.pi as float`）。
//   - abs 保持输入宽度；无符号 abs 返回自身；有符号最小值抛
//     core.OutOfBoundException（§4.11.2——abs 的库契约，不改变内建
//     整数运算的回绕语义）。浮点 abs(-0)=+0、NaN 原样传播（§4.11.3）。
//   - 浮点 min/max：任一参数 NaN 返回 NaN；±0 并列时 min 取 -0、max
//     取 +0；其他相等值按相同数值处理（§4.11.2）。
//   - clamp 先校验边界：lower > upper 或任一边界 NaN 抛
//     OutOfBoundException（不自动交换）；value 为 NaN 返回 NaN；其他按
//     min(max(value, lower), upper) 本库规则；允许合法无穷边界与
//     相等边界（§4.11.2）。
//   - isNaN/isInfinite/isFinite 为纯 Rigi 实现：NaN 的自反性（x != x）、
//     ±inf 折半不变性（x*0.5 == x，有限值折半必变）判定，双端同为
//     IEEE 语义，结果确定。
//   - 浮点特殊值不把宿主 errno / 硬件浮点状态变成 Rigi 异常（§4.11.3）；
//     不把宿主默认结果直接定义为规范（超越函数双端精度口径与特殊值
//     规则见本文件取整族与原语族注释及 Tests/e2e/rigi/math_basic.rg）。
// ============================================================================
namespace core.math

// ---- 常量（§4.11.1：double 中的最近值、正中取偶表示）----
// 完整精度十进制字面量（39 位有效数字）——double.Parse 正确舍入到
// 0x400921FB54442D18 / 0x4005BF0A8B145769 / 0x401921FB54442D18；
// VM/native 经编译器同一字面量节点分发，位表示相同。

pub const pi: double = 3.141592653589793238462643383279502884197

pub const e: double = 2.718281828459045235360287471352662497757

pub const tau: double = 6.283185307179586476925286766559005768394

// ---- RoundingMode（§4.11.2）----
// round 的模式参数：.ToEven = 最近值、中点取偶（默认）；.AwayFromZero =
// 最近值、中点远离零。

pub enum struct RoundingMode {} [ToEven -> 0, AwayFromZero -> 1]

// ---- abs（§4.11.2：保持输入宽度；有符号最小值抛 OutOfBoundException）----

pub func abs(x: i8): i8 {
    if (x < (0 as i8)) {
        // -128（(-127 as i8) - (1 as i8)，避开直接书写最小值字面量）
        if (x == ((-127 as i8) - (1 as i8))) {
            throw new core.OutOfBoundException("abs(i8) 最小值 -128 的绝对值不可表示")
        }
        return -x
    }
    return x
}

pub func abs(x: i16): i16 {
    if (x < (0 as i16)) {
        if (x == ((-32767 as i16) - (1 as i16))) {
            throw new core.OutOfBoundException("abs(i16) 最小值 -32768 的绝对值不可表示")
        }
        return -x
    }
    return x
}

pub func abs(x: i32): i32 {
    if (x < 0) {
        if (x == ((-2147483647) - 1)) {
            throw new core.OutOfBoundException("abs(i32) 最小值 -2147483648 的绝对值不可表示")
        }
        return -x
    }
    return x
}

pub func abs(x: i64): i64 {
    if (x < 0L) {
        if (x == (-9223372036854775807L - 1L)) {
            throw new core.OutOfBoundException("abs(i64) 最小值 -9223372036854775808 的绝对值不可表示")
        }
        return -x
    }
    return x
}

// 无符号 abs 返回自身（§4.11.2）

pub func abs(x: u8): u8 { return x }

pub func abs(x: u16): u16 { return x }

pub func abs(x: u32): u32 { return x }

pub func abs(x: u64): u64 { return x }

// 浮点 abs：-0 → +0（IEEE：x + +0 把 -0 归一为 +0，有限值加法精确）；
// NaN 比较恒假走归一支，+0.0 加法不改变 NaN 载荷分类（本库不承诺
// NaN 载荷往返，§4.11.3）。

pub func abs(x: float): float {
    if (x < (0.0 as float)) { return -x }
    return x + (0.0 as float)
}

pub func abs(x: double): double {
    if (x < 0.0) { return -x }
    return x + 0.0
}

// ---- min/max（§4.11.2）----
// 整数：按比较结果，无特殊值。

pub func min(a: i8, b: i8): i8 { if (a <= b) { return a } return b }

pub func min(a: i16, b: i16): i16 { if (a <= b) { return a } return b }

pub func min(a: i32, b: i32): i32 { if (a <= b) { return a } return b }

pub func min(a: i64, b: i64): i64 { if (a <= b) { return a } return b }

pub func min(a: u8, b: u8): u8 { if (a <= b) { return a } return b }

pub func min(a: u16, b: u16): u16 { if (a <= b) { return a } return b }

pub func min(a: u32, b: u32): u32 { if (a <= b) { return a } return b }

pub func min(a: u64, b: u64): u64 { if (a <= b) { return a } return b }

pub func max(a: i8, b: i8): i8 { if (a >= b) { return a } return b }

pub func max(a: i16, b: i16): i16 { if (a >= b) { return a } return b }

pub func max(a: i32, b: i32): i32 { if (a >= b) { return a } return b }

pub func max(a: i64, b: i64): i64 { if (a >= b) { return a } return b }

pub func max(a: u8, b: u8): u8 { if (a >= b) { return a } return b }

pub func max(a: u16, b: u16): u16 { if (a >= b) { return a } return b }

pub func max(a: u32, b: u32): u32 { if (a >= b) { return a } return b }

pub func max(a: u64, b: u64): u64 { if (a >= b) { return a } return b }

// 浮点 min：NaN 传播（返回既有的 NaN 操作数，不承诺载荷）；±0 并列取
// -0——并列分支经 1/a 的符号判定（1/+0=+inf、1/-0=-inf）；其他并列值
// 任取（数值相同）。

pub func min(a: float, b: float): float {
    if (isNaN(a)) { return a }
    if (isNaN(b)) { return b }
    if (a < b) { return a }
    if (b < a) { return b }
    if (a == (0.0 as float)) {
        if (((1.0 as float) / a) < (0.0 as float)) { return a }
        return b
    }
    return a
}

pub func min(a: double, b: double): double {
    if (isNaN(a)) { return a }
    if (isNaN(b)) { return b }
    if (a < b) { return a }
    if (b < a) { return b }
    if (a == 0.0) {
        if ((1.0 / a) < 0.0) { return a }
        return b
    }
    return a
}

// 浮点 max：NaN 传播；±0 并列取 +0。

pub func max(a: float, b: float): float {
    if (isNaN(a)) { return a }
    if (isNaN(b)) { return b }
    if (a > b) { return a }
    if (b > a) { return b }
    if (a == (0.0 as float)) {
        if (((1.0 as float) / a) > (0.0 as float)) { return a }
        return b
    }
    return a
}

pub func max(a: double, b: double): double {
    if (isNaN(a)) { return a }
    if (isNaN(b)) { return b }
    if (a > b) { return a }
    if (b > a) { return b }
    if (a == 0.0) {
        if ((1.0 / a) > 0.0) { return a }
        return b
    }
    return a
}

// ---- clamp（§4.11.2：先校验边界，不自动交换）----

pub func clamp(value: i8, lower: i8, upper: i8): i8 {
    if (lower > upper) {
        throw new core.OutOfBoundException("clamp 边界非法：lower > upper（不自动交换）")
    }
    return min(max(value, lower), upper)
}

pub func clamp(value: i16, lower: i16, upper: i16): i16 {
    if (lower > upper) {
        throw new core.OutOfBoundException("clamp 边界非法：lower > upper（不自动交换）")
    }
    return min(max(value, lower), upper)
}

pub func clamp(value: i32, lower: i32, upper: i32): i32 {
    if (lower > upper) {
        throw new core.OutOfBoundException("clamp 边界非法：lower > upper（不自动交换）")
    }
    return min(max(value, lower), upper)
}

pub func clamp(value: i64, lower: i64, upper: i64): i64 {
    if (lower > upper) {
        throw new core.OutOfBoundException("clamp 边界非法：lower > upper（不自动交换）")
    }
    return min(max(value, lower), upper)
}

pub func clamp(value: u8, lower: u8, upper: u8): u8 {
    if (lower > upper) {
        throw new core.OutOfBoundException("clamp 边界非法：lower > upper（不自动交换）")
    }
    return min(max(value, lower), upper)
}

pub func clamp(value: u16, lower: u16, upper: u16): u16 {
    if (lower > upper) {
        throw new core.OutOfBoundException("clamp 边界非法：lower > upper（不自动交换）")
    }
    return min(max(value, lower), upper)
}

pub func clamp(value: u32, lower: u32, upper: u32): u32 {
    if (lower > upper) {
        throw new core.OutOfBoundException("clamp 边界非法：lower > upper（不自动交换）")
    }
    return min(max(value, lower), upper)
}

pub func clamp(value: u64, lower: u64, upper: u64): u64 {
    if (lower > upper) {
        throw new core.OutOfBoundException("clamp 边界非法：lower > upper（不自动交换）")
    }
    return min(max(value, lower), upper)
}

// 浮点 clamp：边界先校验——lower > upper 或任一边界 NaN 抛
// OutOfBoundException；value 为 NaN 返回 NaN；其余按本库
// min(max(value, lower), upper) 规则（含 ±0 并列语义）；允许合法
// 无穷边界与相等边界。

pub func clamp(value: float, lower: float, upper: float): float {
    if (isNaN(lower) or isNaN(upper)) {
        throw new core.OutOfBoundException(
            "clamp 边界非法：lower > upper 或边界为 NaN（不自动交换）")
    }
    if (lower > upper) {
        throw new core.OutOfBoundException(
            "clamp 边界非法：lower > upper 或边界为 NaN（不自动交换）")
    }
    if (isNaN(value)) { return value }
    return min(max(value, lower), upper)
}

pub func clamp(value: double, lower: double, upper: double): double {
    if (isNaN(lower) or isNaN(upper)) {
        throw new core.OutOfBoundException(
            "clamp 边界非法：lower > upper 或边界为 NaN（不自动交换）")
    }
    if (lower > upper) {
        throw new core.OutOfBoundException(
            "clamp 边界非法：lower > upper 或边界为 NaN（不自动交换）")
    }
    if (isNaN(value)) { return value }
    return min(max(value, lower), upper)
}

// ---- 分类（§4.11.1；纯 Rigi，IEEE 语义双端一致）----

// NaN 的自反性：NaN != NaN 为真，其余值为假。
pub func isNaN(x: float): bool { return x != x }

pub func isNaN(x: double): bool { return x != x }

// ±inf 折半不变：inf * 0.5 == inf；有限值折半必变（最小次正规下溢为
// 0 亦不等于原值）；NaN 先行排除；±0 经 x == 0.0 排除（-0 == +0）。
pub func isInfinite(x: float): bool {
    if (x != x) { return false }
    if (x == (0.0 as float)) { return false }
    return (x * (0.5 as float)) == x
}

pub func isInfinite(x: double): bool {
    if (x != x) { return false }
    if (x == 0.0) { return false }
    return (x * 0.5) == x
}

pub func isFinite(x: float): bool {
    return (not isNaN(x)) and (not isInfinite(x))
}

pub func isFinite(x: double): bool {
    return (not isNaN(x)) and (not isInfinite(x))
}

// ============================================================================
// 施工块 6-4 阶段 2：取整族（STDLIB §4.11.2）
//
//   - floor 向负无穷、ceil 向正无穷、trunc 向零；结果保持原浮点类型，
//     不隐式转整数；已为零的输入保持零的符号（floor(-0)=-0、
//     trunc(-0.5)=-0）；NaN/无穷返回相应特殊值（§4.11.3）。
//   - round(value) / round(value, mode)：默认 .ToEven（最近值、中点取
//     偶，round(2.5)=2、round(3.5)=4）；.AwayFromZero 中点远离零
//     （round(-2.5, .AwayFromZero)=-3）。round(-0.4) 类结果保持
//     -0 符号。
//   - 取整是 IEEE 确定运算：经 rigi_rt math.c 原语（C floor/ceil/trunc/
//     rint/round，f32/f64 独立精度路径）+ VM hook（C# Math.Floor/
//     Ceiling/Truncate/Round 对应）双端落地，两端同为精确 IEEE 结果，
//     无精度容差问题。
// ============================================================================

pub func floor(x: float): float { return rigi_math_floor_f32(x) }

pub func floor(x: double): double { return rigi_math_floor_f64(x) }

pub func ceil(x: float): float { return rigi_math_ceil_f32(x) }

pub func ceil(x: double): double { return rigi_math_ceil_f64(x) }

pub func trunc(x: float): float { return rigi_math_trunc_f32(x) }

pub func trunc(x: double): double { return rigi_math_trunc_f64(x) }

pub func round(x: float): float { return rigi_math_round_even_f32(x) }

pub func round(x: double): double { return rigi_math_round_even_f64(x) }

pub func round(x: float, mode: RoundingMode): float {
    if (mode is .ToEven) { return rigi_math_round_even_f32(x) }
    return rigi_math_round_away_f32(x)
}

pub func round(x: double, mode: RoundingMode): double {
    if (mode is .ToEven) { return rigi_math_round_even_f64(x) }
    return rigi_math_round_away_f64(x)
}

// ---- 取整原语（§17.4 形态；@NativeSymbol 短名 → C 导出 rigi_<短名>；
//       VM hook 键 = 短名。f32/f64 各自独立精度路径，不许先 double 再截）----

@NativeLibrary("rigi_rt")
@NativeSymbol("math_floor_f32")
priv native func rigi_math_floor_f32(x: float): float

@NativeLibrary("rigi_rt")
@NativeSymbol("math_floor_f64")
priv native func rigi_math_floor_f64(x: double): double

@NativeLibrary("rigi_rt")
@NativeSymbol("math_ceil_f32")
priv native func rigi_math_ceil_f32(x: float): float

@NativeLibrary("rigi_rt")
@NativeSymbol("math_ceil_f64")
priv native func rigi_math_ceil_f64(x: double): double

@NativeLibrary("rigi_rt")
@NativeSymbol("math_trunc_f32")
priv native func rigi_math_trunc_f32(x: float): float

@NativeLibrary("rigi_rt")
@NativeSymbol("math_trunc_f64")
priv native func rigi_math_trunc_f64(x: double): double

@NativeLibrary("rigi_rt")
@NativeSymbol("math_round_even_f32")
priv native func rigi_math_round_even_f32(x: float): float

@NativeLibrary("rigi_rt")
@NativeSymbol("math_round_even_f64")
priv native func rigi_math_round_even_f64(x: double): double

@NativeLibrary("rigi_rt")
@NativeSymbol("math_round_away_f32")
priv native func rigi_math_round_away_f32(x: float): float

@NativeLibrary("rigi_rt")
@NativeSymbol("math_round_away_f64")
priv native func rigi_math_round_away_f64(x: double): double

// ============================================================================
// 施工块 6-4 阶段 3：sqrt/pow 与超越函数（STDLIB §4.11.1 / §4.11.3）
//
//   - sqrt 按目标精度最近值、正中取偶正确舍入（C sqrt/sqrtf 与 C#
//     Math.Sqrt/MathF.Sqrt 均为 IEEE 正确舍入；float 走 f32 独立精度
//     路径，不先 double 再截）。sqrt(-0) 保留 -0；负有限数 → NaN；
//     +inf → +inf。
//   - pow 首版只提供浮点重载。固定规则（§4.11.3）：pow(0,0)=1、
//     pow(NaN,0)=1、pow(1,NaN)=1；负有限底数 + 非整数有限指数 = NaN；
//     零/无穷/奇偶整数指数/正负零组合按 IEEE 风格明确结果，VM/native
//     不因宿主库差异改变分类和符号（独立预期样例覆盖，见
//     Tests/e2e/rigi/math_basic.rg；不直接以宿主默认结果定义规范）。
//   - exp/ln/log2/log10、sin/cos/tan（弧度）、asin/acos/atan、
//     atan2(y, x)（参数顺序 y、x）。特殊值（§4.11.3）：负数 ln 族 →
//     NaN、零输入 → -inf；|x|>1 的 asin/acos → NaN；inf 输入的
//     sin/cos/tan → NaN；有限结果溢出得相应符号无穷；下溢允许次正规
//     及带符号零；atan2 保留有符号零象限——(+0,+0)=+0、(-0,+0)=-0、
//     (+0,-0)=pi、(-0,-0)=-pi；NaN 输入传播 NaN。
//   - 精度口径：超越函数允许跨平台末位不同，有限结果误差上限
//     4 ULP（以目标格式正确舍入参考值处间距度量，次正规区固定最小
//     间距）；NaN/无穷/±0 分类与符号单独验证；不承诺 NaN 载荷或
//     符号往返。
//   - 不把宿主 errno / 硬件浮点状态变成 Rigi 异常；宿主库（C# Math
//     vs C libm）不满足既定规则时修正底层实现，不放宽契约。
// ============================================================================

pub func sqrt(x: float): float { return rigi_math_sqrt_f32(x) }

pub func sqrt(x: double): double { return rigi_math_sqrt_f64(x) }

pub func pow(base: float, exponent: float): float {
    return rigi_math_pow_f32(base, exponent)
}

pub func pow(base: double, exponent: double): double {
    return rigi_math_pow_f64(base, exponent)
}

pub func exp(x: float): float { return rigi_math_exp_f32(x) }

pub func exp(x: double): double { return rigi_math_exp_f64(x) }

pub func ln(x: float): float { return rigi_math_ln_f32(x) }

pub func ln(x: double): double { return rigi_math_ln_f64(x) }

pub func log2(x: float): float { return rigi_math_log2_f32(x) }

pub func log2(x: double): double { return rigi_math_log2_f64(x) }

pub func log10(x: float): float { return rigi_math_log10_f32(x) }

pub func log10(x: double): double { return rigi_math_log10_f64(x) }

pub func sin(x: float): float { return rigi_math_sin_f32(x) }

pub func sin(x: double): double { return rigi_math_sin_f64(x) }

pub func cos(x: float): float { return rigi_math_cos_f32(x) }

pub func cos(x: double): double { return rigi_math_cos_f64(x) }

pub func tan(x: float): float { return rigi_math_tan_f32(x) }

pub func tan(x: double): double { return rigi_math_tan_f64(x) }

pub func asin(x: float): float { return rigi_math_asin_f32(x) }

pub func asin(x: double): double { return rigi_math_asin_f64(x) }

pub func acos(x: float): float { return rigi_math_acos_f32(x) }

pub func acos(x: double): double { return rigi_math_acos_f64(x) }

pub func atan(x: float): float { return rigi_math_atan_f32(x) }

pub func atan(x: double): double { return rigi_math_atan_f64(x) }

pub func atan2(y: float, x: float): float { return rigi_math_atan2_f32(y, x) }

pub func atan2(y: double, x: double): double { return rigi_math_atan2_f64(y, x) }

// ---- 超越函数原语（键 = @NativeSymbol 短名；C 导出 rigi_<短名>；
//       VM hook 同键。f32/f64 独立精度路径）----

@NativeLibrary("rigi_rt")
@NativeSymbol("math_sqrt_f32")
priv native func rigi_math_sqrt_f32(x: float): float

@NativeLibrary("rigi_rt")
@NativeSymbol("math_sqrt_f64")
priv native func rigi_math_sqrt_f64(x: double): double

@NativeLibrary("rigi_rt")
@NativeSymbol("math_pow_f32")
priv native func rigi_math_pow_f32(base: float, exponent: float): float

@NativeLibrary("rigi_rt")
@NativeSymbol("math_pow_f64")
priv native func rigi_math_pow_f64(base: double, exponent: double): double

@NativeLibrary("rigi_rt")
@NativeSymbol("math_exp_f32")
priv native func rigi_math_exp_f32(x: float): float

@NativeLibrary("rigi_rt")
@NativeSymbol("math_exp_f64")
priv native func rigi_math_exp_f64(x: double): double

@NativeLibrary("rigi_rt")
@NativeSymbol("math_ln_f32")
priv native func rigi_math_ln_f32(x: float): float

@NativeLibrary("rigi_rt")
@NativeSymbol("math_ln_f64")
priv native func rigi_math_ln_f64(x: double): double

@NativeLibrary("rigi_rt")
@NativeSymbol("math_log2_f32")
priv native func rigi_math_log2_f32(x: float): float

@NativeLibrary("rigi_rt")
@NativeSymbol("math_log2_f64")
priv native func rigi_math_log2_f64(x: double): double

@NativeLibrary("rigi_rt")
@NativeSymbol("math_log10_f32")
priv native func rigi_math_log10_f32(x: float): float

@NativeLibrary("rigi_rt")
@NativeSymbol("math_log10_f64")
priv native func rigi_math_log10_f64(x: double): double

@NativeLibrary("rigi_rt")
@NativeSymbol("math_sin_f32")
priv native func rigi_math_sin_f32(x: float): float

@NativeLibrary("rigi_rt")
@NativeSymbol("math_sin_f64")
priv native func rigi_math_sin_f64(x: double): double

@NativeLibrary("rigi_rt")
@NativeSymbol("math_cos_f32")
priv native func rigi_math_cos_f32(x: float): float

@NativeLibrary("rigi_rt")
@NativeSymbol("math_cos_f64")
priv native func rigi_math_cos_f64(x: double): double

@NativeLibrary("rigi_rt")
@NativeSymbol("math_tan_f32")
priv native func rigi_math_tan_f32(x: float): float

@NativeLibrary("rigi_rt")
@NativeSymbol("math_tan_f64")
priv native func rigi_math_tan_f64(x: double): double

@NativeLibrary("rigi_rt")
@NativeSymbol("math_asin_f32")
priv native func rigi_math_asin_f32(x: float): float

@NativeLibrary("rigi_rt")
@NativeSymbol("math_asin_f64")
priv native func rigi_math_asin_f64(x: double): double

@NativeLibrary("rigi_rt")
@NativeSymbol("math_acos_f32")
priv native func rigi_math_acos_f32(x: float): float

@NativeLibrary("rigi_rt")
@NativeSymbol("math_acos_f64")
priv native func rigi_math_acos_f64(x: double): double

@NativeLibrary("rigi_rt")
@NativeSymbol("math_atan_f32")
priv native func rigi_math_atan_f32(x: float): float

@NativeLibrary("rigi_rt")
@NativeSymbol("math_atan_f64")
priv native func rigi_math_atan_f64(x: double): double

@NativeLibrary("rigi_rt")
@NativeSymbol("math_atan2_f32")
priv native func rigi_math_atan2_f32(y: float, x: float): float

@NativeLibrary("rigi_rt")
@NativeSymbol("math_atan2_f64")
priv native func rigi_math_atan2_f64(y: double, x: double): double

// ============================================================================
// 施工块 6-5：core.math Random（STDLIB §4.11.4–§4.11.5 / 维护契约 D7）
//
//   - local 类（普通类，无 shared/Serializable 标注）：每实例独立持有
//     状态，不设全局共享随机状态，不支持同实例并发或重入；生成方法为
//     普通函数。不提供 IDisposable、自动重播种或随机状态的 Serializable
//     表示——重放使用种子和调用序列（§4.11.4）。
//   - 标准生成序列固定为 xoshiro256** 1.0（prng.di.unimi.it 参考实现）：
//     以 seed 为 SplitMix64 初始状态，连续四个输出依次初始化 xoshiro
//     四个状态字；所有 u64 运算按模 2^64（内建整数回绕语义），移位/
//     旋转及输出在状态更新前后的顺序严格依参考算法。全部 u64 种子
//     包括零均合法。
//   - 可复现契约（§4.11.4）：相同种子、相同 API 调用及参数序列，在
//     VM/native 和 Windows/Linux 产生相同结果；不依赖宿主 Random 类
//     或当前时间决定显式种子的序列。核心算法、种子展开及各 API 的
//     位消费方式共同构成兼容契约，发布后改变序列视为兼容性变更。
//     该生成器用于通用伪随机用途，不承诺密码学安全。
//   - 无参构造由 priv native 原语 sys_random_u64 取得完整 8 字节系统
//     随机材料（Windows BCryptGenRandom / Linux getrandom，C 侧；
//     VM 侧 RandomNumberGenerator），按小端解释为种子后进入同一初始化
//     路径；获取失败抛 core.IOException，不静默回退为时间戳/零/固定
//     种子。此内部能力不要求新增 core.system、不对外提供密码学随机
//     API（§4.11.4）。VM/native 只要求材料质量，不要求两端同种子。
//   - 位消费规则（§4.11.5）：全宽整数每次消耗一个新核心输出、取最高
//     目标宽度位；有符号按二进制补码解释（as 位重解释）；有界取样为
//     固定拒绝采样（阈值 2^w mod r，x < 阈值丢弃重取，单元素区间也
//     消耗一次核心输出）；nextFloat 取最高 24 位 / 2^24、nextDouble 取
//     最高 53 位 / 2^53（[0,1) 网格，可为 0 不能为 1）；nextBool 取
//     最高位；fillBytes 每核心输出按小端拆 8 字节、尾块丢弃。不同
//     API 或不同 fillBytes 分块方式可消耗不同核心输出数量，不承诺
//     相同总字节数但不同调用序列结果一致。不提供正态分布或统计框架。
// ============================================================================

// ---- 系统随机材料原语（§4.11.4：无参构造种子来源；8 字节小端写入
//       传入 Span，0 成功 / -1 失败换抛 core.IOException）----

@NativeLibrary("rigi_rt")
@NativeSymbol("sys_random_u64")
priv native func rigi_sys_random_u64(buffer: Span\<u8>): i32

// 本地伪随机实例（§4.11.4 local Random 类）
pub class Random {
    // xoshiro256** 四个状态字（每实例独立持有）
    priv var s0: u64
    priv var s1: u64
    priv var s2: u64
    priv var s3: u64

    // 显式种子构造（§4.11.4：全部 u64 种子包括零均合法）。种子展开
    // 内联书写：构造的字段定赋分析不穿透方法调用（§9.3）
    pub init(seed: u64) {
        var z: u64 = seed
        z = z + (0x9E3779B97F4A7C15UL)
        s0 = mix64(z)
        z = z + (0x9E3779B97F4A7C15UL)
        s1 = mix64(z)
        z = z + (0x9E3779B97F4A7C15UL)
        s2 = mix64(z)
        z = z + (0x9E3779B97F4A7C15UL)
        s3 = mix64(z)
    }

    // 系统随机源种子构造（§4.11.4：失败抛 core.IOException，不静默
    // 回退为时间戳/零/固定种子）
    pub init() {
        const tmp = core.collections.spanOf\<u8>(8)
        const rc = rigi_sys_random_u64(tmp)
        if (rc != 0) {
            throw new core.IOException("系统随机源获取失败（sys_random_u64 返回 ${rc}）")
        }
        // 8 字节材料按小端解释为种子，进入与显式种子相同的展开路径
        var seed: u64 = (0 as u64)
        var i: i32 = 0
        while (i < 8) {
            seed = seed | (((tmp[i] if? (0 as u8)) as u64) << ((8 * i) as u64))
            i = i + 1
        }
        var z: u64 = seed
        z = z + (0x9E3779B97F4A7C15UL)
        s0 = mix64(z)
        z = z + (0x9E3779B97F4A7C15UL)
        s1 = mix64(z)
        z = z + (0x9E3779B97F4A7C15UL)
        s2 = mix64(z)
        z = z + (0x9E3779B97F4A7C15UL)
        s3 = mix64(z)
    }

    // SplitMix64 单步混合（seed 展开与各 API 无关，纯函数；z 的递增
    // 由调用方负责——参考实现的 next() 语义）
    priv static func mix64(z: u64): u64 {
        var x: u64 = z
        x = (x ^ (x >> (30 as u64))) * (0xBF58476D1CE4E5B9UL)
        x = (x ^ (x >> (27 as u64))) * (0x94D049BB133111EBUL)
        return x ^ (x >> (31 as u64))
    }

    // 64 位循环左移（xoshiro256** 参考算法的 rotl）
    priv static func rotl64(x: u64, k: u64): u64 {
        return (x << k) | (x >> ((64 as u64) - k))
    }

    // 核心输出（xoshiro256** 1.0 参考算法：result 在状态更新前计算，
    // 更新顺序 s2^=s0; s3^=s1; s1^=s2; s0^=s3; s2^=t; s3=rotl(s3,45)）
    priv func nextCore(): u64 {
        const result: u64 = rotl64(s1 * (5 as u64), (7 as u64)) * (9 as u64)
        const t: u64 = s1 << (17 as u64)
        s2 = s2 ^ s0
        s3 = s3 ^ s1
        s1 = s1 ^ s2
        s0 = s0 ^ s3
        s2 = s2 ^ t
        s3 = rotl64(s3, (45 as u64))
        return result
    }

    // ---- 全宽整数（§4.11.5：每次消耗一个新核心输出，取最高的目标
    //       宽度位；无符号按位值解释，有符号按二进制补码解释）----

    pub func nextI8(): i8 { return ((nextCore() >> (56 as u64)) as i8) }

    pub func nextI16(): i16 { return ((nextCore() >> (48 as u64)) as i16) }

    pub func nextI32(): i32 { return ((nextCore() >> (32 as u64)) as i32) }

    pub func nextI64(): i64 { return (nextCore() as i64) }

    pub func nextU8(): u8 { return ((nextCore() >> (56 as u64)) as u8) }

    pub func nextU16(): u16 { return ((nextCore() >> (48 as u64)) as u16) }

    pub func nextU32(): u32 { return ((nextCore() >> (32 as u64)) as u32) }

    pub func nextU64(): u64 { return nextCore() }

    // ---- 有界整数（§4.11.5：半开区间 [minInclusive, maxExclusive) 均匀
    //       取样；minInclusive < maxExclusive，非法区间抛
    //       OutOfBoundException 且不消耗状态）----
    // 区间宽度用同宽无符号数学表示：边界先 as 成同宽无符号（位重解释），
    // 减法在内建回绕语义下得正确宽度——跨过有符号零点也不溢出为错误
    // 宽度（如 i8 [-128,127)：0x7F - 0x80 = 0xFF = 255）。
    // 拒绝采样固定流程：宽度 w、区间大小 r，阈值 = 2^w mod r——由
    // t0 = (0 - r) 回绕得 2^w - r，再对 r 取余即 2^w mod r（r ≥ 1，
    // 区间合法保证不除零）；每发新核心输出取最高 w 位得 x，x < 阈值丢弃
    // 重取，否则返回 lo + x mod r。不以直接取余引入偏差；单元素区间
    // （r = 1，阈值 = 0）也按此流程消耗一次核心输出。

    pub func nextI8(minInclusive: i8, maxExclusive: i8): i8 {
        if (minInclusive >= maxExclusive) {
            throw new core.OutOfBoundException("nextI8 区间非法：要求 minInclusive < maxExclusive")
        }
        const lo: u8 = (minInclusive as u8)
        const r: u8 = (maxExclusive as u8) - lo
        const threshold: u8 = ((0 as u8) - r) % r
        while (true) {
            const x: u8 = ((nextCore() >> (56 as u64)) as u8)
            if (x >= threshold) {
                return ((lo + (x % r)) as i8)
            }
        }
        // 不可达（while (true) 恒循环）——仅供全路径返回分析落定
        return minInclusive
    }

    pub func nextI16(minInclusive: i16, maxExclusive: i16): i16 {
        if (minInclusive >= maxExclusive) {
            throw new core.OutOfBoundException("nextI16 区间非法：要求 minInclusive < maxExclusive")
        }
        const lo: u16 = (minInclusive as u16)
        const r: u16 = (maxExclusive as u16) - lo
        const threshold: u16 = ((0 as u16) - r) % r
        while (true) {
            const x: u16 = ((nextCore() >> (48 as u64)) as u16)
            if (x >= threshold) {
                return ((lo + (x % r)) as i16)
            }
        }
        // 不可达（while (true) 恒循环）——仅供全路径返回分析落定
        return minInclusive
    }

    pub func nextI32(minInclusive: i32, maxExclusive: i32): i32 {
        if (minInclusive >= maxExclusive) {
            throw new core.OutOfBoundException("nextI32 区间非法：要求 minInclusive < maxExclusive")
        }
        const lo: u32 = (minInclusive as u32)
        const r: u32 = (maxExclusive as u32) - lo
        const threshold: u32 = ((0 as u32) - r) % r
        while (true) {
            const x: u32 = ((nextCore() >> (32 as u64)) as u32)
            if (x >= threshold) {
                return ((lo + (x % r)) as i32)
            }
        }
        // 不可达（while (true) 恒循环）——仅供全路径返回分析落定
        return minInclusive
    }

    pub func nextI64(minInclusive: i64, maxExclusive: i64): i64 {
        if (minInclusive >= maxExclusive) {
            throw new core.OutOfBoundException("nextI64 区间非法：要求 minInclusive < maxExclusive")
        }
        const lo: u64 = (minInclusive as u64)
        const r: u64 = (maxExclusive as u64) - lo
        const threshold: u64 = ((0 as u64) - r) % r
        while (true) {
            const x: u64 = nextCore()
            if (x >= threshold) {
                return ((lo + (x % r)) as i64)
            }
        }
        // 不可达（while (true) 恒循环）——仅供全路径返回分析落定
        return minInclusive
    }

    pub func nextU8(minInclusive: u8, maxExclusive: u8): u8 {
        if (minInclusive >= maxExclusive) {
            throw new core.OutOfBoundException("nextU8 区间非法：要求 minInclusive < maxExclusive")
        }
        const r: u8 = maxExclusive - minInclusive
        const threshold: u8 = ((0 as u8) - r) % r
        while (true) {
            const x: u8 = ((nextCore() >> (56 as u64)) as u8)
            if (x >= threshold) {
                return (minInclusive + (x % r))
            }
        }
        // 不可达（while (true) 恒循环）——仅供全路径返回分析落定
        return minInclusive
    }

    pub func nextU16(minInclusive: u16, maxExclusive: u16): u16 {
        if (minInclusive >= maxExclusive) {
            throw new core.OutOfBoundException("nextU16 区间非法：要求 minInclusive < maxExclusive")
        }
        const r: u16 = maxExclusive - minInclusive
        const threshold: u16 = ((0 as u16) - r) % r
        while (true) {
            const x: u16 = ((nextCore() >> (48 as u64)) as u16)
            if (x >= threshold) {
                return (minInclusive + (x % r))
            }
        }
        // 不可达（while (true) 恒循环）——仅供全路径返回分析落定
        return minInclusive
    }

    pub func nextU32(minInclusive: u32, maxExclusive: u32): u32 {
        if (minInclusive >= maxExclusive) {
            throw new core.OutOfBoundException("nextU32 区间非法：要求 minInclusive < maxExclusive")
        }
        const r: u32 = maxExclusive - minInclusive
        const threshold: u32 = ((0 as u32) - r) % r
        while (true) {
            const x: u32 = ((nextCore() >> (32 as u64)) as u32)
            if (x >= threshold) {
                return (minInclusive + (x % r))
            }
        }
        // 不可达（while (true) 恒循环）——仅供全路径返回分析落定
        return minInclusive
    }

    pub func nextU64(minInclusive: u64, maxExclusive: u64): u64 {
        if (minInclusive >= maxExclusive) {
            throw new core.OutOfBoundException("nextU64 区间非法：要求 minInclusive < maxExclusive")
        }
        const r: u64 = maxExclusive - minInclusive
        const threshold: u64 = ((0 as u64) - r) % r
        while (true) {
            const x: u64 = nextCore()
            if (x >= threshold) {
                return (minInclusive + (x % r))
            }
        }
        // 不可达（while (true) 恒循环）——仅供全路径返回分析落定
        return minInclusive
    }

    // ---- 浮点网格 / bool / 字节填充（§4.11.5）----

    // [0,1) 均匀网格：取新核心输出最高 24 位除以 2^24（float）/ 最高
    // 53 位除以 2^53（double）。最高位数值 < 2^24/2^53，可被目标格式
    // 精确表示，幂次除法亦精确——可为 0、不能为 1；不先把完整 u64 转
    // 浮点再缩放
    pub func nextFloat(): float {
        return (((nextCore() >> (40 as u64)) as float) / (16777216.0 as float))
    }

    pub func nextDouble(): double {
        return ((nextCore() >> (11 as u64)) as double) / 9007199254740992.0
    }

    // 消耗一个核心输出，最高位为 1 返回 true；无隐藏的跨调用位缓存
    pub func nextBool(): bool {
        return (nextCore() >> (63 as u64)) != (0 as u64)
    }

    // 填充指定范围：每个核心输出按小端顺序拆成 8 字节依次填入；
    // 最后不足 8 字节时丢弃剩余字节（不留到下次调用）。合法零长度
    // 不消耗状态；负参数/越界/范围求和溢出先抛 OutOfBoundException，
    // 不修改缓冲区或生成器状态
    pub func fillBytes(buffer: Span\<u8>, offset: i32, count: i32) {
        if (offset < 0) {
            throw new core.OutOfBoundException("fillBytes 参数非法：offset < 0")
        }
        if (count < 0) {
            throw new core.OutOfBoundException("fillBytes 参数非法：count < 0")
        }
        // offset/count 均非负后，length - count 与 offset + count 均
        // 不可能 i32 溢出（length ≤ i32 最大长度）
        if (offset > (buffer.length - count)) {
            throw new core.OutOfBoundException(
                "fillBytes 范围越界：offset + count 超出缓冲区长度")
        }
        var pos: i32 = offset
        var remaining: i32 = count
        while (remaining >= 8) {
            const v: u64 = nextCore()
            var j: i32 = 0
            while (j < 8) {
                buffer[pos + j] = ((v >> ((8 * j) as u64)) as u8)
                j = j + 1
            }
            pos = pos + 8
            remaining = remaining - 8
        }
        if (remaining > 0) {
            // 尾块：只写前 remaining 字节，其余丢弃
            const v: u64 = nextCore()
            var j: i32 = 0
            while (j < remaining) {
                buffer[pos + j] = ((v >> ((8 * j) as u64)) as u8)
                j = j + 1
            }
        }
    }

    // 整个 Span 重载：等价 fillBytes(buffer, 0, buffer.length)
    pub func fillBytes(buffer: Span\<u8>) {
        fillBytes(buffer, 0, buffer.length)
    }
}
