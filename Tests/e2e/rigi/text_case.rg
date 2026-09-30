// core.text 大小写映射端到端（STDLIB §4.3.2，施工块 3-3a）：契约固定样例
//（straße→STRASSE、İ→i+U+0307、I→i、ΟΣ/Σ→σ——上下文无关）、无映射保持、
// ASCII 全量大写↔小写往返、幂等性、补充平面标量、长度变化（一对多映射）。
// 预期值全部来自契约文档与 UCD 17.0.0 原文（SpecialCasing/UnicodeData），
// 不以宿主大小写函数输出为预期（§7.2）。
// expect-output: upper-ss: STRASSE
// expect-output: lower-idot cc: 2 len: 3 ok: true
// expect-output: lower-i: i ok: true
// expect-output: lower-os: οσ ok: true
// expect-output: lower-sigma: σ ok: true
// expect-output: upper-mix: ASSC
// expect-output: ascii: 26 26
// expect-output: idem: true true true true
// expect-output: keep: true true true
// expect-output: fraktur: true true len: 12
// expect-output: ffi: FFI
// expect-output: len-chg: idot 2->3 ffi 3->3 sharp 3->2
// expect-output: sharp: ß ok: true
// expect-output: empty: 0 0
// expect-output: greek: αβγδ ok: true
// expect-output: cyrillic: абв ok: true
// expect-exit: 0
import core.io.Console
import core.text.*

pub func main(): i32 {
    stageContractSamples()
    stageAsciiRoundtrip()
    stageIdempotent()
    stageNoMapping()
    stageLengthChange()
    stageAlphabetRuns()
    return 0
}

// ── 契约固定样例组（§4.3.2 大小写固定样例，逐条对应 UCD 无条件映射）──
priv func stageContractSamples() {
    // "straße".toUpper() = "STRASSE"：ß(U+00DF) → SS 一对二
    //（SpecialCasing 00DF upper = 0053 0053）
    Console.println("upper-ss: ${"straße".toUpper()}")
    // "İ".toLower() = i + U+0307 两个标量（SpecialCasing 0130 lower =
    // 0069 0307）；经标量插值构造，避免源码字面量的分解形态歧义
    const iDotC: char = (0x130 as char)
    const dotAboveC: char = (0x307 as char)
    const idotStr = "${iDotC}"
    const idotLower = idotStr.toLower()
    Console.println("lower-idot cc: ${idotLower.characterCount} len: ${idotLower.length} ok: ${idotLower == "i${dotAboveC}"}")
    // 普通 "I".toLower() = "i"（UnicodeData 0049 simple lower = 0069），
    // 不因系统语言为土耳其语而改变（tr/az 条件被排除）
    Console.println("lower-i: ${"I".toLower()} ok: ${"I".toLower() == "i"}")
    // "ΟΣ".toLower() = "οσ"：词尾 Σ 仍映射为 σ（U+03C3）而非 ς（U+03C2）
    // ——Final_Sigma 上下文条件被排除，映射与周围字符无关
    const omicronCapC: char = (0x39F as char)
    const sigmaCapC: char = (0x3A3 as char)
    const omicronLowC: char = (0x3BF as char)
    const sigmaLowC: char = (0x3C3 as char)
    const osStr = "${omicronCapC}${sigmaCapC}"
    Console.println("lower-os: ${osStr.toLower()} ok: ${osStr.toLower() == "${omicronLowC}${sigmaLowC}"}")
    // 单独 "Σ".toLower() 同样得到 "σ"（词尾同形、上下文无关）
    Console.println("lower-sigma: ${"${sigmaCapC}".toLower()} ok: ${"${sigmaCapC}".toLower() == "${sigmaLowC}"}")
    // 混合串一对二展开在上下文中生效：a→A、ß→SS、c→C
    Console.println("upper-mix: ${"aßc".toUpper()}")
}

// ── ASCII 全量大写↔小写往返（A-Z/a-z delta 区间，内容逐一断言）──
priv func stageAsciiRoundtrip() {
    var up: i64 = (0 as i64)
    var down: i64 = (0 as i64)
    var k: i64 = (0 as i64)
    while (k < (26 as i64)) {
        const lc: char = (((97 as i64) + k) as char)
        const uc: char = (((65 as i64) + k) as char)
        if ("${lc}".toUpper() == "${uc}") {
            up = (up + (1 as i64))
        }
        if ("${uc}".toLower() == "${lc}") {
            down = (down + (1 as i64))
        }
        k = (k + (1 as i64))
    }
    Console.println("ascii: ${up} ${down}")
}

// ── 幂等性：已是大写/小写的串再映射内容不变（无映射标量保持不变）──
priv func stageIdempotent() {
    const iDotC: char = (0x130 as char)
    Console.println("idem: ${"STRASSE".toUpper() == "STRASSE"} ${"straße".toLower() == "straße"} ${"${iDotC}".toUpper() == "${iDotC}"} ${"i".toLower() == "i"}")
}

// ── 无映射保持：中文/emoji/数字原样；补充平面数学字母（UCD 无映射）
// 原样保持；无映射与有映射标量混排时各自正确 ──
priv func stageNoMapping() {
    Console.println("keep: ${"中😀123".toUpper() == "中😀123"} ${"中😀123".toLower() == "中😀123"} ${"a中b".toUpper() == "A中B"}")
    // U+1D41E..U+1D420 数学粗体哥特小写字母：UCD 无大小写映射（表未收录）
    // → 按表保持不变（契约以表为准）
    const frakturA: char = (0x1D41E as char)
    const frakturB: char = (0x1D41F as char)
    const frakturC: char = (0x1D420 as char)
    const fk = "${frakturA}${frakturB}${frakturC}"
    Console.println("fraktur: ${fk.toUpper() == fk} ${fk.toLower() == fk} len: ${fk.length}")
}

// ── 长度变化：一对多映射使字节数变化，不截断；空串映射仍为空串 ──
priv func stageLengthChange() {
    const iDotC: char = (0x130 as char)
    const ffiC: char = (0xFB03 as char)
    const idotStr = "${iDotC}"
    const ffiUpper = "${ffiC}".toUpper()
    // ﬃ(U+FB03) → "FFI"（SpecialCasing FB03 upper = 0046 0046 0049）
    Console.println("ffi: ${ffiUpper}")
    // ẞ(U+1E9E，3 字节) → ß(U+00DF，2 字节)：UnicodeData 1E9E simple lower
    // = 00DF——映射使长度收缩的案例
    const sharpCapC: char = (0x1E9E as char)
    const sharpLowC: char = (0xDF as char)
    const sharpStr = "${sharpCapC}"
    Console.println("len-chg: idot ${idotStr.length}->${idotStr.toLower().length} ffi ${"${ffiC}".length}->${ffiUpper.length} sharp ${sharpStr.length}->${sharpStr.toLower().length}")
    Console.println("sharp: ${sharpStr.toLower()} ok: ${sharpStr.toLower() == "${sharpLowC}"}")
    Console.println("empty: ${"".toUpper().length} ${"".toLower().length}")
}

// ── 希腊/西里尔 delta 区间（区间压缩命中）：逐字母正确映射 ──
priv func stageAlphabetRuns() {
    // ΑΒΓΔ(U+0391..0394) → αβγδ(U+03B1..03B4)，+32 delta 区间
    const aCapC: char = (0x391 as char)
    const bCapC: char = (0x392 as char)
    const gCapC: char = (0x393 as char)
    const dCapC: char = (0x394 as char)
    const aLowC: char = (0x3B1 as char)
    const bLowC: char = (0x3B2 as char)
    const gLowC: char = (0x3B3 as char)
    const dLowC: char = (0x3B4 as char)
    const greekUp = "${aCapC}${bCapC}${gCapC}${dCapC}"
    Console.println("greek: ${greekUp.toLower()} ok: ${greekUp.toLower() == "${aLowC}${bLowC}${gLowC}${dLowC}"}")
    // АБВ(U+0410..0412) → абв(U+0430..0432)
    const cyrCap0: char = (0x410 as char)
    const cyrCap1: char = (0x411 as char)
    const cyrCap2: char = (0x412 as char)
    const cyrLow0: char = (0x430 as char)
    const cyrLow1: char = (0x431 as char)
    const cyrLow2: char = (0x432 as char)
    const cyrUp = "${cyrCap0}${cyrCap1}${cyrCap2}"
    Console.println("cyrillic: ${cyrUp.toLower()} ok: ${cyrUp.toLower() == "${cyrLow0}${cyrLow1}${cyrLow2}"}")
}
