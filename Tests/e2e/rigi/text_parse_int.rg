// core.text 整数 parse/tryParse 端到端（STDLIB §4.3.5 整数段，施工块
// 3-5a）：固定示例组（进制/前缀/符号/有效数字前缀/停止位置）、失败
// 分类（非法文本 vs 超范围）与位置携带、tryParse 一致性（失败 null、
// 0 正常）、各宽度上下界（i64 ±极值、十六进制不解释补码位模式）。
// expect-output: d: 10 0 0 123 12 1 42 -42 0
// expect-output: r: 8 15 10 255 0 0 2 255 255 -128 31 255
// expect-output: rf: text text text text text
// expect-output: pf: text text text text false false
// expect-output: pfx: true true
// expect-output: stop: 1 3
// expect-output: t: 123 true 0 0 true 123
// expect-output: tk: true@11
// expect-output: tk2: false@1
// expect-output: l: 9223372036854775807 -9223372036854775808
// expect-output: lf: range range range
// expect-output: lt: -9223372036854775808 true
// expect-output: n: -128 127 range range -128 range range
// expect-output: u: 255 range text text 255 7 10
// expect-output: w: 32767 -32768 range 65535 range 4294967295 range 1
// expect-output: q: 18446744073709551615 range text 18446744073709551615
// expect-output: qt: true 255 true
// expect-exit: 0
import core.io.Console
import core.text.*

pub func main(): i32 {
    stageDecimal()
    stageRadix()
    stagePrefix()
    stageTryParse()
    stageI64()
    stageNarrow()
    stageWide()
    stageU64()
    return 0
}

// ── 默认十进制（§4.3.5 固定示例：010→10、0xFF→0、123abc→123、
// 12 34→12、1_000→1；符号；零）──
priv func stageDecimal() {
    const a: i32 = i32.parse("010")
    const b: i32 = i32.parse("0xFF")
    const c: i32 = i32.parse("0xFF", .Decimal)
    const d: i32 = i32.parse("123abc")
    const e: i32 = i32.parse("12 34")
    const f: i32 = i32.parse("1_000")
    const g: i32 = i32.parse("+42")
    const h: i32 = i32.parse("-42")
    const k: i32 = i32.parse("0")
    Console.println("d: ${a} ${b} ${c} ${d} ${e} ${f} ${g} ${h} ${k}")
}

// ── 进制（§4.3.5 固定示例：010.Octal→8、17.Octal→15、1010.Binary→10、
// 0xFF.Hexadecimal→255、0b1010.Binary→0、0o17.Octal→0、102.Binary→2、
// 0xFFG.Hexadecimal→255；大写 X 前缀；-0x80；+0x1F；小写数字）──
priv func stageRadix() {
    const a: i32 = i32.parse("010", .Octal)
    const b: i32 = i32.parse("17", .Octal)
    const c: i32 = i32.parse("1010", .Binary)
    const d: i32 = i32.parse("0xFF", .Hexadecimal)
    const e: i32 = i32.parse("0b1010", .Binary)
    const f: i32 = i32.parse("0o17", .Octal)
    const g: i32 = i32.parse("102", .Binary)
    const h: i32 = i32.parse("0xFFG", .Hexadecimal)
    const k: i32 = i32.parse("0XFF", .Hexadecimal)
    const l: i32 = i32.parse("-0x80", .Hexadecimal)
    const m: i32 = i32.parse("+0x1F", .Hexadecimal)
    const o: i32 = i32.parse("0xff", .Hexadecimal)
    Console.println("r: ${a} ${b} ${c} ${d} ${e} ${f} ${g} ${h} ${k} ${l} ${m} ${o}")
    // 失败组：FF 无前缀（hex）、8 非八进制数字、0x 缺数字、0x-1
    //（前缀后无有效数字）、空串——统一经 failI32 输出失败种类
    Console.println("rf: ${failI32("FF", .Hexadecimal)} ${failI32("8", .Octal)} ${failI32("0x", .Hexadecimal)} ${failI32("0x-1", .Hexadecimal)} ${failI32("", .Decimal)}")
}

// ── 前导空白/仅符号失败；-0 与 -0x0 对有符号目标是合法的零 ──
priv func stagePrefix() {
    Console.println("pf: ${failI32(" 1", .Decimal)} ${failI32("-", .Decimal)} ${failI32("+", .Decimal)} ${failI32("-0x", .Hexadecimal)} ${failI32("-0", .Decimal) == "never"} ${failI32("-0x0", .Hexadecimal) == "never"}")
    // 上一行后两项恒 false（-0/-0x0 是合法零）：直接输出成功值验证
    const z1: i32 = i32.parse("-0")
    const z2: i32 = i32.parse("-0x0", .Hexadecimal)
    Console.println("pfx: ${z1 == (0 as i32)} ${z2 == (0 as i32)}")
    // 数字后缀只是停止位置：逗号/小数点（§4.3.5「不因后缀使已读
    // 数字失效」）
    const s1: i32 = i32.parse("1,000")
    const s2: i32 = i32.parse("3.14")
    Console.println("stop: ${s1} ${s2}")
}

// ── tryParse：成功（含 0）返回值、失败 null；异常种类与位置 ──
priv func stageTryParse() {
    const a: i32? = i32.tryParse("123")
    // "0x" 在十六进制模式缺数字失败；十进制模式则是合法的 0（在 x 停）
    const b: i32? = i32.tryParse("0x", .Hexadecimal)
    const b2: i32? = i32.tryParse("0x")
    const c: i32? = i32.tryParse("0")
    const d: i32? = i32.tryParse("99999999999")
    const e: i32? = i32.tryParse("123abc")
    Console.println("t: ${a if? (-1 as i32)} ${isnullI32(b)} ${b2 if? (-1 as i32)} ${c if? (-1 as i32)} ${isnullI32(d)} ${e if? (-1 as i32)}")
    // 超范围：99999999999（11 位）完整前缀数学值超 i32——前缀止于
    // 位置 11，isOutOfRange=true（不因溢出提前成功返回更短前缀）
    try {
        const v: i32 = i32.parse("99999999999")
        Console.println("tk: NOT-CAUGHT ${v}")
    } catch (ex: core.text.NumberParseException) {
        Console.println("tk: ${ex.isOutOfRange}@${ex.position if? (-1 as i64)}")
    }
    // 非法文本：仅符号——isOutOfRange=false、位置 1
    try {
        const v: i32 = i32.parse("-")
        Console.println("tk2: NOT-CAUGHT ${v}")
    } catch (ex: core.text.NumberParseException) {
        Console.println("tk2: ${ex.isOutOfRange}@${ex.position if? (-1 as i64)}")
    }
}

// ── i64 上下界与补码位模式排除 ──
priv func stageI64() {
    const maxV: i64 = i64.parse("9223372036854775807")
    const minV: i64 = i64.parse("-9223372036854775808")
    Console.println("l: ${maxV} ${minV}")
    // 超范围：+1、-1、十六进制位模式 0x8000000000000000（幅值 2^63
    // 超 i64 正范围——不解释为 i64 最小值）
    Console.println("lf: ${failI64("9223372036854775808", .Decimal)} ${failI64("-9223372036854775809", .Decimal)} ${failI64("0x8000000000000000", .Hexadecimal)}")
    const minT: i64? = i64.tryParse("-9223372036854775808")
    const badT: i64? = i64.tryParse("0xFFFFFFFFFFFFFFFF", .Hexadecimal)
    Console.println("lt: ${minT if? (0 as i64)} ${isnullI64(badT)}")
}

// ── 窄型（i8/u8）：±边界、无符号拒负号（含 -0/-0x0）、十六进制
// 不解释补码位模式（0xFF 失败而非 -1）、完整前缀范围检查
//（128abc 超范围失败而非返回更短前缀）──
priv func stageNarrow() {
    const a: i8 = i8.parse("-128")
    const b: i8 = i8.parse("127")
    // 契约固定示例：-0x80 → -128；0xFF 超范围失败
    const c: i8 = i8.parse("-0x80", .Hexadecimal)
    Console.println("n: ${a} ${b} ${failI8("128", .Decimal)} ${failI8("-129", .Decimal)} ${c} ${failI8("0xFF", .Hexadecimal)} ${failI8("128abc", .Decimal)}")
    const d: u8 = u8.parse("255")
    const e: u8 = u8.parse("0xFF", .Hexadecimal)
    const f: u8 = u8.parse("+7")
    const g: u8 = u8.parse("010")
    Console.println("u: ${d} ${failU8("256", .Decimal)} ${failU8("-0", .Decimal)} ${failU8("-0x0", .Hexadecimal)} ${e} ${f} ${g}")
}

// ── 宽型（i16/u16/u32）上下界与 + 号 ──
priv func stageWide() {
    const a: i16 = i16.parse("32767")
    const b: i16 = i16.parse("-32768")
    const c: u16 = u16.parse("65535")
    const d: u32 = u32.parse("4294967295")
    const e: u32 = u32.parse("+1")
    Console.println("w: ${a} ${b} ${failI16("32768", .Decimal)} ${c} ${failU16("65536", .Decimal)} ${d} ${failU32("4294967296", .Decimal)} ${e}")
}

// ── u64 极值（最大值文本、超 u64、负号拒绝、hex 位模式恒等）──
priv func stageU64() {
    const maxV: u64 = u64.parse("18446744073709551615")
    const hexV: u64 = u64.parse("0xFFFFFFFFFFFFFFFF", .Hexadecimal)
    Console.println("q: ${maxV} ${failU64("18446744073709551616", .Decimal)} ${failU64("-1", .Decimal)} ${hexV}")
    const negZero: u64? = u64.tryParse("-0")
    const okU8: u8? = u8.tryParse("255")
    const badU8: u8? = u8.tryParse("256")
    Console.println("qt: ${isnullU64(negZero)} ${okU8 if? (9 as u8)} ${isnullU8(badU8)}")
}

// ── 探针辅助 ──

// i32.parse 失败种类投影：ok:<v> / range / text
priv func failI32(text: String, radix: IntegerRadix): String {
    try {
        const v: i32 = i32.parse(text, radix)
        return "ok:${v}"
    } catch (ex: core.text.NumberParseException) {
        if (ex.isOutOfRange) {
            return "range"
        }
        return "text"
    }
}

// i64.parse 失败种类投影：ok:<v> / range / text
priv func failI64(text: String, radix: IntegerRadix): String {
    try {
        const v: i64 = i64.parse(text, radix)
        return "ok:${v}"
    } catch (ex: core.text.NumberParseException) {
        if (ex.isOutOfRange) {
            return "range"
        }
        return "text"
    }
}

priv func isnullI32(v: i32?): bool {
    return (v if? (1 as i32)) == (1 as i32)
}

priv func isnullI64(v: i64?): bool {
    return (v if? (1 as i64)) == (1 as i64)
}

// 各窄/宽型失败种类投影：ok:<v> / range / text
priv func failI8(text: String, radix: IntegerRadix): String {
    try {
        const v: i8 = i8.parse(text, radix)
        return "ok:${v}"
    } catch (ex: core.text.NumberParseException) {
        if (ex.isOutOfRange) {
            return "range"
        }
        return "text"
    }
}

priv func failU8(text: String, radix: IntegerRadix): String {
    try {
        const v: u8 = u8.parse(text, radix)
        return "ok:${v}"
    } catch (ex: core.text.NumberParseException) {
        if (ex.isOutOfRange) {
            return "range"
        }
        return "text"
    }
}

priv func failI16(text: String, radix: IntegerRadix): String {
    try {
        const v: i16 = i16.parse(text, radix)
        return "ok:${v}"
    } catch (ex: core.text.NumberParseException) {
        if (ex.isOutOfRange) {
            return "range"
        }
        return "text"
    }
}

priv func failU16(text: String, radix: IntegerRadix): String {
    try {
        const v: u16 = u16.parse(text, radix)
        return "ok:${v}"
    } catch (ex: core.text.NumberParseException) {
        if (ex.isOutOfRange) {
            return "range"
        }
        return "text"
    }
}

priv func failU32(text: String, radix: IntegerRadix): String {
    try {
        const v: u32 = u32.parse(text, radix)
        return "ok:${v}"
    } catch (ex: core.text.NumberParseException) {
        if (ex.isOutOfRange) {
            return "range"
        }
        return "text"
    }
}

priv func failU64(text: String, radix: IntegerRadix): String {
    try {
        const v: u64 = u64.parse(text, radix)
        return "ok:${v}"
    } catch (ex: core.text.NumberParseException) {
        if (ex.isOutOfRange) {
            return "range"
        }
        return "text"
    }
}

priv func isnullU64(v: u64?): bool {
    return (v if? (1 as u64)) == (1 as u64)
}

priv func isnullU8(v: u8?): bool {
    return (v if? (1 as u8)) == (1 as u8)
}
