// core.text 浮点 parse/tryParse 端到端（STDLIB §4.3.5 浮点段，施工块
// 3-5b）：语法合法/非法全表（完整消费，与整数前缀解析不同）、特殊值
// 四种文本与大小写拒绝、正中取偶固定向量（double 与 float 独立精度）、
// 最大/最小正规与次正规边界、负零（1.0/x 符号位验证）、超范围与中
// 下溢到零、tryParse 一致性与失败位置。舍入真值以 .NET 最短往返
// ToString(InvariantCulture) 为基准（f64/f32_to_string 同格式）。
// expect-output: syn: 1 1.25 0.001 7 -2.5 1000000 0 2.5
// expect-output: sf: text text text text text text text text text text text text text text text text text text text text text
// expect-output: sp: true true Infinity -Infinity false false false
// expect-output: tk: text text text text text text text
// expect-output: tk: 4503599627370496 4503599627370498 9007199254740992
// expect-output: dtie: 0.1 0.1 0.10000000000000002
// expect-output: ftie: 0.1 0.10000001 0.10000001
// expect-output: r: 0.1 0.3333333333333333 true 1E+23 1.2345678901234568E+29 1.2345679E+29
// expect-output: i: 16777216 16777217 0.33333334 true true true
// expect-output: b: 1.7976931348623157E+308 2.2250738585072014E-308 2.225073858507201E-308
// expect-output: b2: 5E-324 0 5E-324 5E-324 1.5E-323 3 true
// expect-output: fb: 3.4028235E+38 1.1754944E-38 1.1754942E-38 1E-45 1E-40 5 true
// expect-output: z: -Infinity -Infinity Infinity 0 -Infinity -0
// expect-output: rf: range range range range range range range
// expect-output: t: false false false false true true false true false true
// expect-output: pos: 0 2 2 3 2 3
// expect-output: q: 0.1 true true true true true Infinity
// expect-exit: 0
import core.io.Console
import core.text.*

pub func main(): i32 {
    stageSyntax()
    stageSyntaxFail()
    stageSpecial()
    stageTieEven()
    stageRound()
    stageIndependent()
    stageBoundary()
    stageFloatBoundary()
    stageZero()
    stageRange()
    stageTryParseConsistency()
    stagePosition()
    stageMisc()
    return 0
}

// ── 语法合法形态（§4.3.5：1 / +01.25 / 1e-3 / 前导零 / 大写 E /
// 负号；2.5 是精确可表示值，非取偶用例）──
priv func stageSyntax() {
    const a: double = double.parse("1")
    const b: double = double.parse("+01.25")
    const c: double = double.parse("1e-3")
    const d: double = double.parse("007")
    const e: double = double.parse("-2.5")
    const f: double = double.parse("1E6")
    const g: double = double.parse("0.000")
    const h: double = double.parse("2.5")
    Console.println("syn: ${a} ${b} ${c} ${d} ${e} ${f} ${g} ${h}")
}

// ── 语法非法形态全表：均非法文本类（.5/1./1e/1e+ 形态、空白、
// 进制前缀/hex 浮点、f/F 后缀、尾随垃圾、仅符号、多点、双 e、
// 下划线/逗号、全角数字非 ASCII）──
priv func stageSyntaxFail() {
    Console.println("sf: ${failD(".5")} ${failD("1.")} ${failD("1e")} ${failD("1e+")} ${failD(" 1")} ${failD("1 ")} ${failD("0x1p3")} ${failD("1f")} ${failD("1.5F")} ${failD("")} ${failD("-")} ${failD("+")} ${failD(".")} ${failD("1.2.3")} ${failD("1ee2")} ${failD("1_0")} ${failD("1,5")} ${failD("0b1")} ${failD("1e3x")} ${failD("--1")} ${failD("1e ")}")
}

// ── 特殊值：NaN/+NaN/-NaN（载荷符号不承诺，经 x!=x 验证）、
// Infinity/+Infinity/-Infinity 与 toString 衔接；大小写变体与
// 缩写全部拒绝（nan/inf/INFINITY/+infinity/Infinity␣/Infi）──
priv func stageSpecial() {
    const nan1: double = double.parse("NaN")
    const nan2: double = double.parse("-NaN")
    const inf1: double = double.parse("Infinity")
    const inf2: double = double.parse("-Infinity")
    const okInf: double? = double.tryParse("+Infinity")
    const okNan: double? = double.tryParse("NaN")
    const fInf: float? = float.tryParse("-Infinity")
    Console.println("sp: ${nan1 != nan1} ${nan2 != nan2} ${inf1} ${inf2} ${isnullD(okInf)} ${isnullD(okNan)} ${isnullF(fInf)}")
    Console.println("tk: ${failD("nan")} ${failD("inf")} ${failD("INFINITY")} ${failD("+infinity")} ${failD("Infinity ")} ${failD("Infi")} ${failD("nan")}")
}

// ── 正中取偶（固定向量，真值经 .NET 基准核对）：
// 2^52/2^53 刻度中点整数化；double 0.1 上邻中点（0.1 为偶、
// 上邻为奇 → tie 归 0.1；下方同；上方进位）──
priv func stageTieEven() {
    Console.println("tk: ${double.parse("4503599627370496.5")} ${double.parse("4503599627370497.5")} ${double.parse("9007199254740993")}")
    Console.println("dtie: ${double.parse("0.100000000000000012490009027033011079765856266021728515625")} ${double.parse("0.100000000000000012490009027033011079765856266021728515624")} ${double.parse("0.100000000000000012490009027033011079765856266021728515626")}")
    // float 0.1 上邻中点：0.1f 尾数为奇、上邻为偶 → tie 归上邻
    Console.println("ftie: ${float.parse("0.1000000052154064178466796874")} ${float.parse("0.1000000052154064178466796875")} ${float.parse("0.1000000052154064178466796876")}")
}

// ── 舍入正确性：0.1、1/3 长十进制（== 1.0/3.0 且最短往返串）、
// 1e23、35 位混合十进制 double/float 各自精度 ──
priv func stageRound() {
    const third: double = double.parse("0.333333333333333333333333333333")
    const thirdLit: double = (1.0 / 3.0)
    Console.println("r: ${double.parse("0.1")} ${third} ${third == thirdLit} ${double.parse("1e23")} ${double.parse("123456789012345678901234567890.12345")} ${float.parse("123456789012345678901234567890.12345")}")
}

// ── float 与 double 独立精度：16777217（float 24 位取偶 →
// 16777216，double 53 位精确 → 16777217）；长十进制 1/3 两宽度
// 不同串；float 次正规 1e-40 成功而 double 同值是正规 ──
priv func stageIndependent() {
    const fi: float = float.parse("16777217")
    const di: double = double.parse("16777217")
    const f3: float = float.parse("0.33333333333333333333333333333")
    const f3l: float = (1.0 as float) / (3.0 as float)
    const f40: float = float.parse("1e-40")
    const f40ok: bool = (f40 != (0.0 as float))
    const fbig: float = float.parse("3.4028235e38")
    const fbigok: bool = (fbig > (0.0 as float))
    Console.println("i: ${fi} ${di} ${f3} ${f3 == f3l} ${f40ok} ${fbigok}")
}

// ── double 边界：最大/最小正规、最大次正规（著名 2.2250738585072011e-308
// 向量）、次正规网格（5E-324/恰下半距归 0/半距上方归最小次正规）、
// 中间值 1.5E-323 与网格比值 3 ──
priv func stageBoundary() {
    const maxN: double = double.parse("1.7976931348623157e308")
    const minN: double = double.parse("2.2250738585072014e-308")
    const maxSub: double = double.parse("2.2250738585072011e-308")
    Console.println("b: ${maxN} ${minN} ${maxSub}")
    const sub0: double = double.parse("4.9e-324")
    const halfDown: double = double.parse("2.4703282292062327e-324")
    const halfUp: double = double.parse("2.4703282292062328e-324")
    const seven: double = double.parse("7e-324")
    const mid: double = double.parse("1.5e-323")
    const unit: double = double.parse("5e-324")
    Console.println("b2: ${sub0} ${halfDown} ${halfUp} ${seven} ${mid} ${mid / unit} ${mid == (unit * (3.0 as double))}")
}

// ── float 边界：最大正规、最小正规（最短串 1.1754944E-38）、
// 最大次正规（min normal 下方的著名紧邻向量）、最小次正规 1E-45、
// 次正规 1E-40、7e-45 网格比值 5 ──
priv func stageFloatBoundary() {
    const maxN: float = float.parse("3.4028235e38")
    const minN: float = float.parse("1.17549435e-38")
    const maxSub: float = float.parse("1.17549421069244107548702944485e-38")
    const sub0: float = float.parse("1.4e-45")
    const f40: float = float.parse("1e-40")
    const seven: float = float.parse("7e-45")
    const unit: float = float.parse("1.4e-45")
    Console.println("fb: ${maxN} ${minN} ${maxSub} ${sub0} ${f40} ${seven / unit} ${maxSub < minN}")
}

// ── 负零与下溢：-0.0/-0 保留符号位（1.0/x = -Infinity）；
// 0e999 为零不溢出；1e-5000 下溢到 +0（成功）、-1e-5000 负零；
// -0.0 的最短往返串为 -0 ──
priv func stageZero() {
    const nz1: double = double.parse("-0.0")
    const nz2: double = double.parse("-0")
    const pz: double = double.parse("0e999")
    const ufl: double = double.parse("1e-5000")
    const ufn: double = double.parse("-1e-5000")
    Console.println("z: ${1.0 / nz1} ${1.0 / nz2} ${1.0 / pz} ${ufl} ${1.0 / ufn} ${nz1}")
}

// ── 超范围（超范围类失败，不自动产生 Infinity）：double 1e400/
// -1e400/1.8e308/1.7976931348623159e308（舍入上进位越界）；float
// 3.5e38/1e39/3.4028236e38 ──
priv func stageRange() {
    Console.println("rf: ${failD("1e400")} ${failD("-1e400")} ${failD("1.8e308")} ${failD("1.7976931348623159e308")} ${failF("3.5e38")} ${failF("1e39")} ${failF("3.4028236e38")}")
}

// ── tryParse 一致性：成功（含 0/次正规/最大正规）返回值；
// 两类失败（非法文本/超范围）均 null；parse 与 tryParse 同规则 ──
priv func stageTryParseConsistency() {
    const a: double? = double.tryParse("0.1")
    const b: double? = double.tryParse("0")
    const c: double? = double.tryParse("5e-324")
    const d: double? = double.tryParse("1.7976931348623157e308")
    const e: double? = double.tryParse("1e400")
    const f: double? = double.tryParse(".5")
    const g: float? = float.tryParse("1.17549435e-38")
    const h: float? = float.tryParse("3.5e38")
    const i: float? = float.tryParse("1e-45")
    const j: float? = float.tryParse(" 1")
    Console.println("t: ${isnullD(a)} ${isnullD(b)} ${isnullD(c)} ${isnullD(d)} ${isnullD(e)} ${isnullD(f)} ${isnullF(g)} ${isnullF(h)} ${isnullF(i)} ${isnullF(j)}")
}

// ── 失败位置（零基 UTF-8 字节偏移）：.5 在 0（整数部分无数字）、
// 1. 在 2（小数点后无数字）、1e 在 2（指数无数字）、1e+ 在 3、
// 12␣ 在 2（尾随字符）、1.2.3 在 3 ──
priv func stagePosition() {
    Console.println("pos: ${posD(".5")} ${posD("1.")} ${posD("1e")} ${posD("1e+")} ${posD("12 ")} ${posD("1.2.3")}")
}

// ── 杂项：tryParse 成功值投影、特殊值与超范围的 tryParse 形态 ──
priv func stageMisc() {
    const a: double = double.parse("0.1")
    const nz: double? = double.tryParse("-0.0")
    const negZero: bool = ((1.0 / (nz if? (1.0 as double))) == (0.0 - (1.0 / 0.0)))
    const b: double? = double.tryParse(".5")
    const c: double? = double.tryParse("1e400")
    const d: float? = float.tryParse("nan")
    const e: float? = float.tryParse("1f")
    const inf: double = double.parse("+Infinity")
    Console.println("q: ${a} ${negZero} ${isnullD(b)} ${isnullD(c)} ${isnullF(d)} ${isnullF(e)} ${inf}")
}

// ── 探针辅助 ──

// double.parse 失败种类投影：ok:<v> / range / text
priv func failD(text: String): String {
    try {
        const v: double = double.parse(text)
        return "ok:${v}"
    } catch (ex: core.text.NumberParseException) {
        if (ex.isOutOfRange) {
            return "range"
        }
        return "text"
    }
}

// float.parse 失败种类投影：ok:<v> / range / text
priv func failF(text: String): String {
    try {
        const v: float = float.parse(text)
        return "ok:${v}"
    } catch (ex: core.text.NumberParseException) {
        if (ex.isOutOfRange) {
            return "range"
        }
        return "text"
    }
}

// double.parse 非法文本失败位置（-1 = 非非法文本类）
priv func posD(text: String): i64 {
    try {
        const v: double = double.parse(text)
        return (-1 as i64)
    } catch (ex: core.text.NumberParseException) {
        if (ex.isOutOfRange) {
            return (-2 as i64)
        }
        return (ex.position if? (-3 as i64))
    }
}

priv func isnullD(v: double?): bool {
    return (v if? (1.0 as double)) == (1.0 as double)
}

priv func isnullF(v: float?): bool {
    return (v if? (1.0 as float)) == (1.0 as float)
}
