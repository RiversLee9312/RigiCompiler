// ============================================================================
// math_basic.rg —— 施工块 6-4（STDLIB §4.11.1–§4.11.3 / D7）
// core.math 函数与常量：VM 与 native 双宿主对拍
// （NativeE2E「数学函数对拍」Case 复用本语料）。
//
//   ① abs 全宽度：i8/i16/i32/i64 最小值抛 OutOfBoundException、无符号
//      原样、浮点 -0→+0、NaN 传播。
//   ② min/max/clamp：NaN 传播、±0 规则（min 取 -0、max 取 +0）、clamp
//      无效区间与 NaN 边界抛错、NaN value 传播、无穷边界。
//   ③ floor/ceil/trunc/round 两模式固定样例：负数、±0 符号保持、
//      NaN/无穷特殊值、大值。
//   ④ sqrt 正确舍入：完美平方精确、√2/√(1/2)/√10/√(1e-300) 位级（最近值正中
//      取偶）断言、sqrt(-0)=-0、负有限→NaN、+inf→+inf。
//   ⑤ pow 固定组合表（IEEE 风格，独立预期）：0^0/NaN^0/1^NaN=1、负底
//      非整数指→NaN、零/无穷/奇偶整数指/±0 组合、上溢±inf、下溢±0。
//   ⑥ exp/ln/log2/log10 定义域与特殊值；三角与反三角（弧度）；
//      atan2 四象限 ±0 与 NaN 传播。
//   ⑦ 4 ULP 验证：超越函数向量与独立高精度参考值（离线 mpmath 50 位
//      精度算好写死，字面量为目标格式正确舍入值的往返十进制）比较，
//      两端结果与参考值之差 ≤ 4 ULP。ULP 间距度量：|ref| 规格化到
//      [1,2) 后的 2^-52（double）/ 2^-23（float）×2^e；语料向量全部
//      位于正规区（次正规固定最小间距条款不适用，见注释）；阈值取
//      4.0 = 契约 4 ULP；参考值已经正确舍入，不额外加余量。NaN/无穷/±0
//      分类与符号单独断言，不依赖 ULP 比较。
//   ⑧ 常量 pi/e/tau：与已知 double 最近值逐文本一致（字面量经编译器
//      前端单一路径正确舍入，VM/native 位表示相同）。
// expect-output: sec-abs-ok
// expect-output: sec-minmax-ok
// expect-output: sec-round-ok
// expect-output: sec-sqrt-ok
// expect-output: sec-pow-ok
// expect-output: sec-expln-ok
// expect-output: sec-trig-ok
// expect-output: sec-atan2-ok
// expect-output: sec-ulp-f64-ok
// expect-output: sec-ulp-f32-ok
// expect-output: sec-const-ok
// expect-output: math-basic-ok
// expect-exit: 0
// ============================================================================
import core.math.*
import core.io.Console

// 失败计数（0 = 通过）；失败时打印名字定位（expect-output 只钉通过形态）
func check(name: String, cond: bool): i32 {
    if (cond) { return 0 }
    Console.println("check-FAIL ${name}")
    return 1
}

// x 是否为 -0（x == 0 且 1/x 为负无穷）
func isNegZero(x: double): bool {
    if (x != 0.0) { return false }
    return (1.0 / x) < 0.0
}

func isNegZeroF(x: float): bool {
    if (x != (0.0 as float)) { return false }
    return ((1.0 as float) / x) < (0.0 as float)
}

// ---- 4 ULP 比较（§4.11.3 口径）----
// ulp 间距：|ref| 规格化 m×2^k（m ∈ [1,2)）后 double 取 2^(k-52)、
// float 取 2^(k-23)。向量全部位于正规区；ref 有限非零前提由向量表
// 保证。ref 已是正确舍入到目标格式的值，阈值 4.0 不加舍入余量。

func ulpCloseF64(got: double, ref: double, limit: double): bool {
    var m = abs(ref)
    var pow2 = 1.0
    while (m >= 2.0) {
        m = m * 0.5
        pow2 = pow2 * 2.0
    }
    while (m < 1.0) {
        m = m * 2.0
        pow2 = pow2 * 0.5
    }
    const ulp = 2.220446049250313e-16 * pow2
    return (abs(got - ref) / ulp) <= limit
}

func ulpCloseF32(got: float, ref: float, limit: double): bool {
    // float 值在 double 中精确可表；间距按 2^-23 目标格式度量
    const gd = got as double
    const rd = ref as double
    var m = abs(rd)
    var pow2 = 1.0
    while (m >= 2.0) {
        m = m * 0.5
        pow2 = pow2 * 2.0
    }
    while (m < 1.0) {
        m = m * 2.0
        pow2 = pow2 * 0.5
    }
    const ulp = 1.1920928955078125e-7 * pow2
    return (abs(gd - rd) / ulp) <= limit
}

pub func main(): i32 {
    var fails = 0
    const nanD = 0.0 / 0.0
    const infD = 1.0 / 0.0
    const negInfD = (-1.0) / 0.0
    const nz = (-1.0) * 0.0
    const nanF = nanD as float
    const infF = infD as float
    const nzF = nz as float

    // ---- ① abs 全宽度 ----
    fails = fails + check("abs-i8", (abs((-5 as i8)) == (5 as i8))
        and (abs((5 as i8)) == (5 as i8)))
    fails = fails + check("abs-i16", abs((-12345 as i16)) == (12345 as i16))
    fails = fails + check("abs-i32", abs(-2147483645) == 2147483645)
    fails = fails + check("abs-i64", abs(-9223372036854775807L) == 9223372036854775807L)
    fails = fails + check("abs-u8", abs((250 as u8)) == (250 as u8))
    fails = fails + check("abs-u16", abs((65000 as u16)) == (65000 as u16))
    fails = fails + check("abs-u32", abs((4000000000UL) as u32) == ((4000000000UL) as u32))
    fails = fails + check("abs-u64", abs(18446744073709551615UL) == 18446744073709551615UL)
    fails = fails + check("abs-i8-min", (tryAbsI8Min() == 1))
    fails = fails + check("abs-i16-min", (tryAbsI16Min() == 1))
    fails = fails + check("abs-i32-min", (tryAbsI32Min() == 1))
    fails = fails + check("abs-i64-min", (tryAbsI64Min() == 1))
    fails = fails + check("abs-neg0-d", not isNegZero(abs(nz)))
    fails = fails + check("abs-neg0-f", not isNegZeroF(abs(nzF)))
    fails = fails + check("abs-nan-d", isNaN(abs(nanD)))
    fails = fails + check("abs-nan-f", isNaN(abs(nanF)))
    fails = fails + check("abs-inf-d", abs(negInfD) == infD)
    fails = fails + check("abs-float-val", abs((-3.5 as float)) == (3.5 as float))
    if (fails > 0) { Console.println("sec-abs-FAIL") } else { Console.println("sec-abs-ok") }

    // ---- ② min/max/clamp ----
    fails = fails + check("min-i32", min((-3 as i32), (7 as i32)) == (-3 as i32))
    fails = fails + check("max-i64", max((-3L), (7L)) == (7L))
    fails = fails + check("min-u8", min((3 as u8), (7 as u8)) == (3 as u8))
    fails = fails + check("max-u64", max((3 as u64), (7 as u64)) == (7 as u64))
    fails = fails + check("min-nan-a", isNaN(min(nanD, 1.0)))
    fails = fails + check("min-nan-b", isNaN(min(1.0, nanD)))
    fails = fails + check("min-nan-f", isNaN(min(nanF, (1.0 as float))))
    fails = fails + check("max-nan-a", isNaN(max(nanD, 1.0)))
    fails = fails + check("max-nan-b", isNaN(max(1.0, nanD)))
    fails = fails + check("min-neg0", isNegZero(min(0.0, nz)))
    fails = fails + check("min-neg0-swapped", isNegZero(min(nz, 0.0)))
    fails = fails + check("max-pos0", not isNegZero(max(nz, 0.0)))
    fails = fails + check("max-pos0-swapped", not isNegZero(max(0.0, nz)))
    fails = fails + check("min-neg0-f", isNegZeroF(min((0.0 as float), nzF)))
    fails = fails + check("max-pos0-f", not isNegZeroF(max(nzF, (0.0 as float))))
    fails = fails + check("min-inf", min(negInfD, 1.0) == negInfD)
    fails = fails + check("max-inf", max(infD, 1.0) == infD)
    fails = fails + check("clamp-int", clamp((5 as i32), (0 as i32), (3 as i32)) == (3 as i32))
    fails = fails + check("clamp-int-lo", clamp((-5 as i32), (0 as i32), (3 as i32)) == (0 as i32))
    fails = fails + check("clamp-inv", (tryClampInv() == 1))
    fails = fails + check("clamp-inv-f", (tryClampInvF() == 1))
    fails = fails + check("clamp-nan-bound-a", (tryClampNanA() == 1))
    fails = fails + check("clamp-nan-bound-b", (tryClampNanB() == 1))
    fails = fails + check("clamp-nan-value", isNaN(clamp(nanD, 0.0, 1.0)))
    fails = fails + check("clamp-inf-upper", clamp(5.0, 0.0, infD) == 5.0)
    fails = fails + check("clamp-inf-lower", clamp(5.0, negInfD, 1.0) == 1.0)
    fails = fails + check("clamp-equal-bounds", clamp(7.0, 3.0, 3.0) == 3.0)
    if (fails > 0) { Console.println("sec-minmax-FAIL") } else { Console.println("sec-minmax-ok") }

    // ---- ③ floor/ceil/trunc/round ----
    fails = fails + check("floor-pos", floor(2.75) == 2.0)
    fails = fails + check("floor-neg", floor((-2.25)) == (-3.0))
    fails = fails + check("floor-big", floor(4503599627370496.5) == 4503599627370496.0)
    fails = fails + check("floor-f", floor((2.75 as float)) == (2.0 as float))
    fails = fails + check("ceil-neg", ceil((-2.75)) == (-2.0))
    fails = fails + check("ceil-pos", ceil(2.25) == 3.0)
    fails = fails + check("trunc-neg", trunc((-2.75)) == (-2.0))
    fails = fails + check("trunc-pos", trunc(2.75) == 2.0)
    fails = fails + check("floor-neg0", isNegZero(floor(nz)))
    fails = fails + check("ceil-neg0", isNegZero(ceil(nz)))
    fails = fails + check("trunc-neg0", isNegZero(trunc((-0.5))))
    fails = fails + check("round-neg0", isNegZero(round((-0.4))))
    fails = fails + check("re-2.5", round(2.5) == 2.0)
    fails = fails + check("re-3.5", round(3.5) == 4.0)
    fails = fails + check("re-neg2.5", round((-2.5)) == (-2.0))
    fails = fails + check("re-neg3.5", round((-3.5)) == (-4.0))
    fails = fails + check("re-2.4", round(2.4) == 2.0)
    fails = fails + check("re-f", round((1.5 as float)) == (2.0 as float))
    fails = fails + check("ra-2.5", round(2.5, RoundingMode.AwayFromZero) == 3.0)
    fails = fails + check("ra-neg2.5", round((-2.5), RoundingMode.AwayFromZero) == (-3.0))
    fails = fails + check("ra-f", round((2.5 as float), RoundingMode.AwayFromZero) == (3.0 as float))
    fails = fails + check("round-nan", isNaN(round(nanD)))
    fails = fails + check("round-inf", round(infD) == infD)
    fails = fails + check("floor-inf", floor(infD) == infD)
    fails = fails + check("ceil-neginf", ceil(negInfD) == negInfD)
    fails = fails + check("trunc-nan", isNaN(trunc(nanD)))
    if (fails > 0) { Console.println("sec-round-FAIL") } else { Console.println("sec-round-ok") }

    // ---- ④ sqrt 正确舍入 ----
    fails = fails + check("sqrt-0", sqrt(0.0) == 0.0)
    fails = fails + check("sqrt-neg0", isNegZero(sqrt(nz)))
    fails = fails + check("sqrt-neg0-f", isNegZeroF(sqrt(nzF)))
    fails = fails + check("sqrt-frac", sqrt(0.25) == 0.5)
    fails = fails + check("sqrt-big", sqrt(16777216.0) == 4096.0)
    // 完美平方以外：最近值正中取偶位级断言（√2/√(1/2)/√10 参考字面量
    // 为正确舍入结果的往返十进制）
    fails = fails + check("sqrt-2", sqrt(2.0) == 1.4142135623730951)
    fails = fails + check("sqrt-half", sqrt(0.5) == 0.7071067811865476)
    fails = fails + check("sqrt-10", sqrt(10.0) == 3.1622776601683795)
    // 独立高精度验证：sqrt(目标 double 的 1e-300) 舍入为 0x20ca2fe76a3f9475。
    fails = fails + check("sqrt-1e-300", sqrt(1.0e-300) == 1.0e-150)
    fails = fails + check("sqrt-3-f", sqrt((3.0 as float)) == (1.7320508075688772 as float))
    // sqrtf(2) 的最近偶数 f32 位形为 0x3fb504f3。
    fails = fails + check("sqrt-2-f", sqrt((2.0 as float)) == (1.4142135381698608 as float))
    fails = fails + check("sqrt-neg", isNaN(sqrt((-4.0))))
    fails = fails + check("sqrt-neg-f", isNaN(sqrt((-4.0 as float))))
    fails = fails + check("sqrt-inf", sqrt(infD) == infD)
    fails = fails + check("sqrt-nan", isNaN(sqrt(nanD)))
    if (fails > 0) { Console.println("sec-sqrt-FAIL") } else { Console.println("sec-sqrt-ok") }

    // ---- ⑤ pow 固定组合表（独立预期，IEEE 风格）----
    fails = fails + check("pow-0-0", pow(0.0, 0.0) == 1.0)
    fails = fails + check("pow-nan-0", pow(nanD, 0.0) == 1.0)
    fails = fails + check("pow-1-nan", pow(1.0, nanD) == 1.0)
    fails = fails + check("pow-neg-nonint", isNaN(pow((-2.0), 0.5)))
    fails = fails + check("pow-neg-nonint-f", isNaN(pow((-2.0 as float), (0.5 as float))))
    fails = fails + check("pow-neg-int-odd", pow((-2.0), 3.0) == (-8.0))
    fails = fails + check("pow-neg-int-even", pow((-2.0), 2.0) == 4.0)
    fails = fails + check("pow-neg-int-negexp", pow((-2.0), (-3.0)) == (-0.125))
    fails = fails + check("pow-pos0-pos", not isNegZero(pow(0.0, 2.0)))
    fails = fails + check("pow-neg0-even", not isNegZero(pow(nz, 2.0)))
    fails = fails + check("pow-neg0-odd", isNegZero(pow(nz, 3.0)))
    fails = fails + check("pow-pos0-neg", pow(0.0, (-2.0)) == infD)
    fails = fails + check("pow-neg0-neg-odd", pow(nz, (-3.0)) == negInfD)
    fails = fails + check("pow-neg0-neg-even", pow(nz, (-2.0)) == infD)
    fails = fails + check("pow-inf-pos", pow(infD, 2.0) == infD)
    fails = fails + check("pow-inf-neg", not isNegZero(pow(infD, (-2.0))))
    fails = fails + check("pow-neginf-odd", pow(negInfD, 3.0) == negInfD)
    fails = fails + check("pow-neginf-even", pow(negInfD, 2.0) == infD)
    fails = fails + check("pow-neginf-negodd", pow(negInfD, (-3.0)) == nz)
    fails = fails + check("pow-neg1-inf", pow((-1.0), infD) == 1.0)
    fails = fails + check("pow-neg1-neginf", pow((-1.0), negInfD) == 1.0)
    fails = fails + check("pow-gt1-inf", pow(2.0, infD) == infD)
    fails = fails + check("pow-lt1-inf", not isNegZero(pow(0.5, infD)))
    fails = fails + check("pow-gt1-neginf", not isNegZero(pow(2.0, negInfD)))
    fails = fails + check("pow-lt1-neginf", pow(0.5, negInfD) == infD)
    fails = fails + check("pow-nan-exp", isNaN(pow(2.0, nanD)))
    fails = fails + check("pow-nan-base", isNaN(pow(nanD, 2.0)))
    fails = fails + check("pow-overflow", pow(10.0, 400.0) == infD)
    fails = fails + check("pow-neg-overflow", pow((-10.0), 401.0) == negInfD)
    fails = fails + check("pow-underflow", not isNegZero(pow(10.0, (-400.0))))
    fails = fails + check("pow-basic", (pow(2.0, 10.0) == 1024.0) and (pow(2.0, (-1.0)) == 0.5))
    fails = fails + check("pow-f", pow((1.5 as float), (3.0 as float)) == (3.375 as float))
    if (fails > 0) { Console.println("sec-pow-FAIL") } else { Console.println("sec-pow-ok") }

    // ---- ⑥ exp/ln/log2/log10 定义域与特殊值 ----
    fails = fails + check("exp-0", exp(0.0) == 1.0)
    fails = fails + check("exp-1-e", exp(1.0) == 2.718281828459045)
    fails = fails + check("exp-neginf", not isNegZero(exp(negInfD)))
    fails = fails + check("exp-inf", exp(infD) == infD)
    fails = fails + check("exp-overflow", exp(1000.0) == infD)
    fails = fails + check("exp-underflow", not isNegZero(exp((-1000.0))))
    fails = fails + check("exp-nan", isNaN(exp(nanD)))
    fails = fails + check("ln-1-pos0", (ln(1.0) == 0.0) and (not isNegZero(ln(1.0))))
    fails = fails + check("ln-0-neginf", ln(0.0) == negInfD)
    fails = fails + check("ln-neg0-neginf", ln(nz) == negInfD)
    fails = fails + check("ln-neg", isNaN(ln((-5.0))))
    fails = fails + check("ln-inf", ln(infD) == infD)
    fails = fails + check("ln-nan", isNaN(ln(nanD)))
    fails = fails + check("log2-1-pos0", (log2(1.0) == 0.0) and (not isNegZero(log2(1.0))))
    fails = fails + check("log2-2", log2(2.0) == 1.0)
    fails = fails + check("log2-half", log2(0.5) == (-1.0))
    fails = fails + check("log2-1024", log2(1024.0) == 10.0)
    fails = fails + check("log2-0", log2(0.0) == negInfD)
    fails = fails + check("log2-neg", isNaN(log2((-1.0))))
    fails = fails + check("log2-inf", log2(infD) == infD)
    fails = fails + check("log10-1-pos0", (log10(1.0) == 0.0) and (not isNegZero(log10(1.0))))
    fails = fails + check("log10-10", log10(10.0) == 1.0)
    fails = fails + check("log10-1000", log10(1000.0) == 3.0)
    fails = fails + check("log10-frac", log10(0.001) == (-3.0))
    fails = fails + check("log10-0", log10(0.0) == negInfD)
    fails = fails + check("log10-neg", isNaN(log10((-5.0))))
    fails = fails + check("log10-inf", log10(infD) == infD)
    if (fails > 0) { Console.println("sec-expln-FAIL") } else { Console.println("sec-expln-ok") }

    // ---- ⑥' 三角与反三角（弧度）----
    fails = fails + check("sin-0", (sin(0.0) == 0.0) and (not isNegZero(sin(0.0))))
    fails = fails + check("sin-neg0", isNegZero(sin(nz)))
    fails = fails + check("cos-0", cos(0.0) == 1.0)
    fails = fails + check("tan-0", (tan(0.0) == 0.0) and (not isNegZero(tan(0.0))))
    fails = fails + check("tan-neg0", isNegZero(tan(nz)))
    fails = fails + check("sin-inf", isNaN(sin(infD)))
    fails = fails + check("sin-neginf", isNaN(sin(negInfD)))
    fails = fails + check("cos-inf", isNaN(cos(infD)))
    fails = fails + check("tan-inf", isNaN(tan(infD)))
    fails = fails + check("sin-nan", isNaN(sin(nanD)))
    fails = fails + check("asin-0", (asin(0.0) == 0.0) and (not isNegZero(asin(0.0))))
    fails = fails + check("asin-neg0", isNegZero(asin(nz)))
    fails = fails + check("asin-1", ulpCloseF64(asin(1.0), 1.5707963267948966, 4.0))
    fails = fails + check("asin-neg1", ulpCloseF64(asin((-1.0)), (-1.5707963267948966), 4.0))
    fails = fails + check("asin-2", isNaN(asin(2.0)))
    fails = fails + check("asin-neg2", isNaN(asin((-2.0))))
    fails = fails + check("acos-1-pos0", (acos(1.0) == 0.0) and (not isNegZero(acos(1.0))))
    fails = fails + check("acos-neg1-pi", ulpCloseF64(acos((-1.0)), 3.141592653589793, 4.0))
    fails = fails + check("acos-2", isNaN(acos(2.0)))
    fails = fails + check("acos-neg2", isNaN(acos((-2.0))))
    fails = fails + check("atan-0", (atan(0.0) == 0.0) and (not isNegZero(atan(0.0))))
    fails = fails + check("atan-neg0", isNegZero(atan(nz)))
    fails = fails + check("atan-inf", ulpCloseF64(atan(infD), 1.5707963267948966, 4.0))
    fails = fails + check("atan-neginf", ulpCloseF64(atan(negInfD), (-1.5707963267948966), 4.0))
    fails = fails + check("asin-nan", isNaN(asin(nanD)))
    fails = fails + check("acos-nan", isNaN(acos(nanD)))
    fails = fails + check("atan-nan", isNaN(atan(nanD)))
    // isNaN/isInfinite/isFinite 分类
    fails = fails + check("cls-nan-d-a", isNaN(nanD))
    fails = fails + check("cls-nan-d-b", not isInfinite(nanD))
    fails = fails + check("cls-nan-d-c", not isFinite(nanD))
    fails = fails + check("cls-inf-d-a", not isNaN(infD))
    fails = fails + check("cls-inf-d-b", isInfinite(infD))
    fails = fails + check("cls-inf-d-c", not isFinite(infD))
    fails = fails + check("cls-fin-d", (not isNaN(1.0e300)) and (not isInfinite(1.0e300)))
    fails = fails + check("cls-fin-d-b", isFinite(1.0e300))
    fails = fails + check("cls-tiny-d-a", not isInfinite(1.0e-300))
    fails = fails + check("cls-tiny-d-b", isFinite(1.0e-300))
    fails = fails + check("cls-nan-f", isNaN(nanF))
    fails = fails + check("cls-inf-f", isInfinite(infF))
    fails = fails + check("cls-fin-f", isFinite((3.25 as float)))
    if (fails > 0) { Console.println("sec-trig-FAIL") } else { Console.println("sec-trig-ok") }

    // ---- ⑥'' atan2 四象限与特殊值（参数顺序 y, x）----
    fails = fails + check("atan2-pzpz", (atan2(0.0, 0.0) == 0.0) and (not isNegZero(atan2(0.0, 0.0))))
    fails = fails + check("atan2-nzpz", isNegZero(atan2(nz, 0.0)))
    fails = fails + check("atan2-pznz", ulpCloseF64(atan2(0.0, nz), 3.141592653589793, 4.0))
    fails = fails + check("atan2-nznz", ulpCloseF64(atan2(nz, nz), (-3.141592653589793), 4.0))
    fails = fails + check("atan2-inf-inf", ulpCloseF64(atan2(infD, infD), 0.7853981633974483, 4.0))
    fails = fails + check("atan2-inf-ninf", ulpCloseF64(atan2(infD, negInfD), 2.356194490192345, 4.0))
    fails = fails + check("atan2-ninf-ninf", ulpCloseF64(atan2(negInfD, negInfD), (-2.356194490192345), 4.0))
    fails = fails + check("atan2-ninf-inf", ulpCloseF64(atan2(negInfD, infD), (-0.7853981633974483), 4.0))
    fails = fails + check("atan2-nan-y", isNaN(atan2(nanD, 1.0)))
    fails = fails + check("atan2-nan-x", isNaN(atan2(1.0, nanD)))
    fails = fails + check("atan2-0-posx", atan2(0.0, 5.0) == 0.0)
    fails = fails + check("atan2-f", ulpCloseF32(atan2((3.0 as float), (4.0 as float)),
        (0.6435011029243469 as float), 4.0))
    if (fails > 0) { Console.println("sec-atan2-FAIL") } else { Console.println("sec-atan2-ok") }

    // ---- ⑦ 超越函数 4 ULP 向量（double；参考值离线 mpmath 50 位精度计算，
    //        字面量为正确舍入结果的往返十进制；sqrt 在上面按等值验证）----
    // ref=1 的下方间距是 2^-53，上方及 ref 处 ULP 是 2^-52；
    // 8 格恰为 4 ULP，9 格是 4.5 ULP。跨 binade 也须按 ref 处度量。
    const halfUlpD = 1.1102230246251565e-16
    fails = fails + check("ulp64-4-at-1", ulpCloseF64(1.0 - (8.0 * halfUlpD), 1.0, 4.0))
    fails = fails + check("ulp64-4.5-at-1", not ulpCloseF64(1.0 - (9.0 * halfUlpD), 1.0, 4.0))
    fails = fails + check("ulp64-4.5-at-2", not ulpCloseF64(2.0 - (18.0 * halfUlpD), 2.0, 4.0))
    fails = fails + check("v-exp05", ulpCloseF64(exp(0.5), 1.6487212707001282, 4.0))
    fails = fails + check("v-exp-20", ulpCloseF64(exp((-20.0)), 2.061153622438558e-09, 4.0))
    fails = fails + check("v-exp3", ulpCloseF64(exp(3.0), 20.085536923187668, 4.0))
    fails = fails + check("v-exp-05", ulpCloseF64(exp((-0.5)), 0.6065306597126334, 4.0))
    fails = fails + check("v-ln2", ulpCloseF64(ln(2.0), 0.6931471805599453, 4.0))
    fails = fails + check("v-ln10", ulpCloseF64(ln(10.0), 2.302585092994046, 4.0))
    fails = fails + check("v-ln05", ulpCloseF64(ln(0.5), (-0.6931471805599453), 4.0))
    fails = fails + check("v-ln3", ulpCloseF64(ln(3.0), 1.0986122886681098, 4.0))
    fails = fails + check("v-log2-3", ulpCloseF64(log2(3.0), 1.584962500721156, 4.0))
    fails = fails + check("v-log2-10", ulpCloseF64(log2(10.0), 3.321928094887362, 4.0))
    fails = fails + check("v-log2-075", ulpCloseF64(log2(0.75), (-0.4150374992788438), 4.0))
    fails = fails + check("v-log2-7", ulpCloseF64(log2(7.0), 2.807354922057604, 4.0))
    fails = fails + check("v-log10-2", ulpCloseF64(log10(2.0), 0.3010299956639812, 4.0))
    fails = fails + check("v-log10-3", ulpCloseF64(log10(3.0), 0.47712125471966244, 4.0))
    fails = fails + check("v-log10-7", ulpCloseF64(log10(7.0), 0.8450980400142568, 4.0))
    fails = fails + check("v-log10-05", ulpCloseF64(log10(0.5), (-0.3010299956639812), 4.0))
    fails = fails + check("v-sin1", ulpCloseF64(sin(1.0), 0.8414709848078965, 4.0))
    fails = fails + check("v-sin05", ulpCloseF64(sin(0.5), 0.479425538604203, 4.0))
    fails = fails + check("v-sin25", ulpCloseF64(sin(2.5), 0.5984721441039565, 4.0))
    fails = fails + check("v-sin-15", ulpCloseF64(sin((-1.5)), (-0.9974949866040544), 4.0))
    fails = fails + check("v-sin100", ulpCloseF64(sin(100.0), (-0.5063656411097588), 4.0))
    fails = fails + check("v-cos1", ulpCloseF64(cos(1.0), 0.5403023058681398, 4.0))
    fails = fails + check("v-cos05", ulpCloseF64(cos(0.5), 0.8775825618903728, 4.0))
    fails = fails + check("v-cos25", ulpCloseF64(cos(2.5), (-0.8011436155469337), 4.0))
    fails = fails + check("v-cos100", ulpCloseF64(cos(100.0), 0.8623188722876839, 4.0))
    fails = fails + check("v-tan05", ulpCloseF64(tan(0.5), 0.5463024898437905, 4.0))
    fails = fails + check("v-tan12", ulpCloseF64(tan(1.2), 2.5721516221263188, 4.0))
    fails = fails + check("v-tan-2", ulpCloseF64(tan((-2.0)), 2.185039863261519, 4.0))
    fails = fails + check("v-asin05", ulpCloseF64(asin(0.5), 0.5235987755982989, 4.0))
    fails = fails + check("v-asin-09", ulpCloseF64(asin((-0.9)), (-1.1197695149986342), 4.0))
    fails = fails + check("v-acos025", ulpCloseF64(acos(0.25), 1.318116071652818, 4.0))
    fails = fails + check("v-acos-075", ulpCloseF64(acos((-0.75)), 2.4188584057763776, 4.0))
    fails = fails + check("v-atan075", ulpCloseF64(atan(0.75), 0.6435011087932844, 4.0))
    fails = fails + check("v-atan10", ulpCloseF64(atan(10.0), 1.4711276743037347, 4.0))
    fails = fails + check("v-atan-25", ulpCloseF64(atan((-25.0)), (-1.5308176396716067), 4.0))
    fails = fails + check("v-atan2-34", ulpCloseF64(atan2(3.0, 4.0), 0.6435011087932844, 4.0))
    fails = fails + check("v-atan2-n34", ulpCloseF64(atan2((-3.0), 4.0), (-0.6435011087932844), 4.0))
    fails = fails + check("v-atan2-1-1000", ulpCloseF64(atan2(1.0, 1000.0), 0.0009999996666668666, 4.0))
    fails = fails + check("v-atan2-tiny", ulpCloseF64(atan2(1.0e-300, (-1.0e-300)), 2.356194490192345, 4.0))
    fails = fails + check("v-pow-2537", ulpCloseF64(pow(2.5, 3.7), 29.67413253642086, 4.0))
    fails = fails + check("v-pow-0355", ulpCloseF64(pow(0.3, 5.5), 0.0013309658147375534, 4.0))
    fails = fails + check("v-pow-1510", ulpCloseF64(pow(1.5, 10.0), 57.6650390625, 4.0))
    fails = fails + check("v-pow-099", ulpCloseF64(pow(0.99, 1000.0), 4.317124741065786e-05, 4.0))
    fails = fails + check("v-pow-sqrt2", ulpCloseF64(pow(2.0, 0.5), 1.4142135623730951, 4.0))
    if (fails > 0) { Console.println("sec-ulp-f64-FAIL") } else { Console.println("sec-ulp-f64-ok") }

    // ---- ⑦' 超越函数 4 ULP 向量（float；f32 独立精度路径）----
    const halfUlpF = 5.9604644775390625e-8
    fails = fails + check("ulp32-4-at-1", ulpCloseF32((1.0 - (8.0 * halfUlpF)) as float,
        (1.0 as float), 4.0))
    fails = fails + check("ulp32-4.5-at-1", not ulpCloseF32((1.0 - (9.0 * halfUlpF)) as float,
        (1.0 as float), 4.0))
    fails = fails + check("vf-exp05", ulpCloseF32(exp((0.5 as float)), (1.6487212181091309 as float), 4.0))
    fails = fails + check("vf-ln2", ulpCloseF32(ln((2.0 as float)), (0.6931471824645996 as float), 4.0))
    fails = fails + check("vf-sin1", ulpCloseF32(sin((1.0 as float)), (0.8414709568023682 as float), 4.0))
    fails = fails + check("vf-cos05", ulpCloseF32(cos((0.5 as float)), (0.8775825500488281 as float), 4.0))
    fails = fails + check("vf-atan2", ulpCloseF32(atan2((3.0 as float), (4.0 as float)), (0.6435011029243469 as float), 4.0))
    fails = fails + check("vf-pow", ulpCloseF32(pow((1.5 as float), (3.0 as float)), (3.375 as float), 4.0))
    fails = fails + check("vf-log2-10", ulpCloseF32(log2((10.0 as float)), (3.321928024291992 as float), 4.0))
    fails = fails + check("vf-tan075", ulpCloseF32(tan((0.75 as float)), (0.9315964579582214 as float), 4.0))
    fails = fails + check("vf-asin08", ulpCloseF32(asin((0.8 as float)), (0.9272952675819397 as float), 4.0))
    if (fails > 0) { Console.println("sec-ulp-f32-FAIL") } else { Console.println("sec-ulp-f32-ok") }

    // ---- ⑧ 常量：与已知 double 最近值逐文本一致 ----
    fails = fails + check("const-pi", pi == 3.141592653589793)
    fails = fails + check("const-e", e == 2.718281828459045)
    fails = fails + check("const-tau", tau == 6.283185307179586)
    fails = fails + check("const-tau-2pi", tau == (2.0 * pi))
    fails = fails + check("const-e-exp1", e == exp(1.0))
    if (fails > 0) { Console.println("sec-const-FAIL") } else { Console.println("sec-const-ok") }

    if (fails > 0) {
        Console.println("math-basic-FAIL count=${fails}")
        return 1
    }
    Console.println("math-basic-ok")
    return 0
}

func tryAbsI8Min(): i32 {
    try {
        const m = abs(((-127 as i8) - (1 as i8)))
        return 0
    } catch (e: core.OutOfBoundException) {
        return 1
    }
}

func tryAbsI16Min(): i32 {
    try {
        const m = abs(((-32767 as i16) - (1 as i16)))
        return 0
    } catch (e: core.OutOfBoundException) {
        return 1
    }
}

func tryAbsI32Min(): i32 {
    try {
        const m = abs((-2147483647) - 1)
        return 0
    } catch (e: core.OutOfBoundException) {
        return 1
    }
}

func tryAbsI64Min(): i32 {
    try {
        const m = abs(-9223372036854775807L - 1L)
        return 0
    } catch (e: core.OutOfBoundException) {
        return 1
    }
}

func tryClampInv(): i32 {
    try {
        const m = clamp((1 as i32), (5 as i32), (3 as i32))
        return 0
    } catch (e: core.OutOfBoundException) {
        return 1
    }
}

func tryClampInvF(): i32 {
    try {
        const m = clamp(1.0, 5.0, 3.0)
        return 0
    } catch (e: core.OutOfBoundException) {
        return 1
    }
}

func tryClampNanA(): i32 {
    try {
        const m = clamp(1.0, 0.0 / 0.0, 1.0)
        return 0
    } catch (e: core.OutOfBoundException) {
        return 1
    }
}

func tryClampNanB(): i32 {
    try {
        const m = clamp(1.0, 0.0, 0.0 / 0.0)
        return 0
    } catch (e: core.OutOfBoundException) {
        return 1
    }
}
