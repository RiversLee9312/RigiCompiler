// core.text StringBuilder 与 UTF-8 增量编解码端到端（STDLIB §4.3.4，
// 施工块 3-4）：StringBuilder 分块追加/length 字节数/clear/toString
// 独立性/容量增长；Utf8Decoder 单块与跨块增量解码、isFinal 截断、
// 非法序列严格抛错与替换模式 U+FFFD（最大子部分割）、reset 复用；
// Utf8Encoder 编码入口与 toUtf8Span 一致性、encodeChar 各长度、
// encodeTo 容量守卫。预期值全部来自契约文档与 UTF-8 标准编码规则，
// 不以宿主编解码输出为预期。
// expect-output: sb-empty: 0 []
// expect-output: sb-len1: 4
// expect-output: sb-len2: 8
// expect-output: sb-len3: 9 cc: 4
// expect-output: sb-eq: true
// expect-output: sb-snap: true true true
// expect-output: sb-clear: 0 [] true true
// expect-output: sb-reuse: 6 true
// expect-output: sb-multi: 4 true
// expect-output: sb-grow: 4016 4016 4004
// expect-output: sb-grow-spot: true true true true
// expect-output: sb-grow-ends: true true
// expect-output: sb-len-4byte: 8 2
// expect-output: dec-single: true 9 4
// expect-output: dec-empty: []
// expect-output: dec-cross1: true 8 3
// expect-output: dec-cross2: true 8
// expect-output: dec-trunc-strict: true true
// expect-output: dec-trunc-rep: true 3 1
// expect-output: dec-trunc-rep2: true
// expect-output: dec-trunc-rep4: true 1
// expect-output: dec-bad-strict: true true true true true true true
// expect-output: dec-bad-msg: true true true true
// expect-output: dec-cross-strict: true true true
// expect-output: dec-trunc-pos: true true
// expect-output: dec-rep-c080: true
// expect-output: dec-rep-surrogate: true true
// expect-output: dec-rep-subpart: true
// expect-output: dec-rep-isolated: true
// expect-output: dec-rep-mixed: true 8 4
// expect-output: dec-rep-resync: true 3
// expect-output: dec-reset-rep: true true
// expect-output: dec-reset-strict: true true
// expect-output: enc-eq: true 9
// expect-output: enc-bytes: true
// expect-output: enc-char-len: 1 2 2 3 4
// expect-output: enc-char-bytes: true true true true
// expect-output: enc-roundtrip: true true
// expect-output: enc-to: 4 5 true
// expect-output: enc-to-edge: 0 1 true
// expect-output: enc-to-cap: true true true
// expect-output: enc-to-partial: true true
// expect-output: enc-copy: true
// expect-exit: 0
import core.io.Console
import core.collections.*
import core.text.*

pub func main(): i32 {
    stageBuilder()
    stageDecoder()
    stageEncoder()
    return 0
}

// ── §4.3.4 StringBuilder ──
priv func stageBuilder() {
    // 基础追加：String + char（含补充平面 char），length 是 UTF-8 字节数
    //（§4.3.4；"A中" 4 字节 + 😀 4 字节 + "B" 1 字节 = 9，与 §4.3.1
    // 固定样例 A中😀B 的 length 一致）
    const sb = new StringBuilder()
    Console.println("sb-empty: ${sb.length} [${sb.toString()}]")
    sb.append("A中")
    Console.println("sb-len1: ${sb.length}")
    const emojiC: char = (0x1F600 as char)
    sb.append(emojiC)
    Console.println("sb-len2: ${sb.length}")
    sb.append("B")
    Console.println("sb-len3: ${sb.length} cc: ${sb.toString().characterCount}")
    Console.println("sb-eq: ${sb.toString() == "A中${emojiC}B"}")

    // toString 独立性：toString 后继续 append/clear 不改变已有结果
    //（§4.3.4 明文）
    const sb2 = new StringBuilder()
    sb2.append("A中")
    const s1 = sb2.toString()
    sb2.append(emojiC)
    sb2.append("B")
    const s2 = sb2.toString()
    Console.println("sb-snap: ${s1 == "A中"} ${s2 == "A中${emojiC}B"} ${s1 == "A中"}")
    sb2.clear()
    Console.println("sb-clear: ${sb2.length} [${sb2.toString()}] ${s1 == "A中"} ${s2 == "A中${emojiC}B"}")

    // clear 后可继续使用
    sb2.append("x${emojiC}y")
    Console.println("sb-reuse: ${sb2.length} ${sb2.toString() == "x${emojiC}y"}")

    // 多次 append 内容正确：逐段与插值拼接对照（含空段与 U+0000 内容）
    const sb3 = new StringBuilder()
    sb3.append("")
    sb3.append("ab")
    sb3.append("")
    const nulC: char = (0 as char)
    sb3.append("${nulC}")
    sb3.append("c")
    Console.println("sb-multi: ${sb3.length} ${sb3.toString() == "ab${nulC}c"}")

    // 容量增长路径：2000 段（每段 "ab"，每 500 段追加一个补充平面
    // char）——分块持有路径跨 List 扩容；length 与字节数精确核对，
    // 补充平面 char 落点抽查（字节偏移 2/1006/2010/3014）
    const sb4 = new StringBuilder()
    var i: i32 = 0
    while (i < 2000) {
        sb4.append("ab")
        if ((i % 500) == 0) {
            sb4.append(emojiC)
        }
        i = (i + 1)
    }
    const grown = sb4.toString()
    Console.println("sb-grow: ${sb4.length} ${grown.length} ${grown.characterCount}")
    Console.println("sb-grow-spot: ${grown.slice((2 as i64), (4 as i64)) == "${emojiC}"} ${grown.slice((1006 as i64), (4 as i64)) == "${emojiC}"} ${grown.slice((2010 as i64), (4 as i64)) == "${emojiC}"} ${grown.slice((3014 as i64), (4 as i64)) == "${emojiC}"}")
    Console.println("sb-grow-ends: ${grown.slice((0 as i64), (2 as i64)) == "ab"} ${grown.slice((4014 as i64), (2 as i64)) == "ab"}")

    // length 只读属性（无 setter，编译期约束）；U+0000 是普通内容
    //（§4.3.1）——上面 sb-multi 已核；此处补 builder 混合长度的字节数
    const sb5 = new StringBuilder()
    sb5.append(emojiC)
    sb5.append(emojiC)
    Console.println("sb-len-4byte: ${sb5.length} ${sb5.toString().characterCount}")
}

// ── §4.3.4 Utf8Decoder：增量解码 ──
priv func stageDecoder() {
    // U+FFFD 替换符（65533）；中 = E4 B8 AD = 228/184/173；
    // 😀 = F0 9F 98 80 = 240/159/152/128（UTF-8 标准编码规则）
    const f: char = (65533 as char)

    // 单块整解码：整段字节一次喂入（isFinal=true），内容/字节长/标量数
    const strict = new Utf8Decoder()
    const whole = strict.decode(makeBytes(65UB, 228UB, 184UB, 173UB, 240UB, 159UB, 152UB, 128UB, 66UB), true)
    Console.println("dec-single: ${whole == "A中😀B"} ${whole.length} ${whole.characterCount}")

    // 空输入：无残段时 isFinal=true 得空串
    Console.println("dec-empty: [${strict.decode(core.collections.spanOf\<u8>(0), true)}]")

    // 跨块（1）：多字节序列按单字节切碎喂入（isFinal=false 保残段），
    // 收尾空块 isFinal=true——结果完整（"中A😀" 8 字节 3 标量）；
    // 解码结果经 StringBuilder 累计（跨调用结果按序拼接即完整解码）
    const chunker = new Utf8Decoder()
    var acc = new StringBuilder()
    acc.append(chunker.decode(makeBytes(228UB), false))
    acc.append(chunker.decode(makeBytes(184UB), false))
    acc.append(chunker.decode(makeBytes(173UB), false))
    acc.append(chunker.decode(makeBytes(65UB), false))
    acc.append(chunker.decode(makeBytes(240UB), false))
    acc.append(chunker.decode(makeBytes(159UB), false))
    acc.append(chunker.decode(makeBytes(152UB), false))
    acc.append(chunker.decode(makeBytes(128UB), false))
    acc.append(chunker.decode(core.collections.spanOf\<u8>(0), true))
    const cross = acc.toString()
    Console.println("dec-cross1: ${cross == "中A😀"} ${cross.length} ${cross.characterCount}")

    // 跨块（2）：2 字节一批，3/4 字节序列各跨批切开，收尾 isFinal
    const chunker2 = new Utf8Decoder()
    var acc2 = new StringBuilder()
    acc2.append(chunker2.decode(makeBytes(65UB, 228UB), false))
    acc2.append(chunker2.decode(makeBytes(184UB, 173UB), false))
    acc2.append(chunker2.decode(makeBytes(240UB, 159UB), false))
    acc2.append(chunker2.decode(makeBytes(152UB, 128UB), true))
    Console.println("dec-cross2: ${acc2.toString() == "A中😀"} ${acc2.toString().length}")

    // isFinal 截断：严格模式抛 TextFormatException，消息带原因与位置
    const trunc = new Utf8Decoder()
    const t1 = catchTextMsg(func{(): String -> trunc.decode(makeBytes(228UB, 184UB), true)})
    Console.println("dec-trunc-strict: ${hasText(t1, "截断序列")} ${hasText(t1, "于字节位置 0")}")

    // isFinal 截断（替换模式）：截断残段恰为一个最大子部分（完整前缀）
    // → 一个 U+FFFD；残段已清，可继续解码
    const rep = new Utf8Decoder(true)
    const r1 = rep.decode(makeBytes(228UB, 184UB), true)
    Console.println("dec-trunc-rep: ${r1 == "${f}"} ${r1.length} ${r1.characterCount}")
    Console.println("dec-trunc-rep2: ${rep.decode(makeBytes(65UB), true) == "A"}")
    const rep4 = new Utf8Decoder(true)
    const r4 = rep4.decode(makeBytes(240UB, 159UB, 152UB), true)
    Console.println("dec-trunc-rep4: ${r4 == "${f}"} ${r4.characterCount}")

    // 非法序列严格模式：0xFF 首字节 / 孤立续字节 0x80 / 过长编码 C0 80 /
    // 过长编码 E0 80 80 / 代理区 ED A0 80 / 越界 F4 90 80 80 / 最大子
    // 部分样例 F0 82 82 AC——逐个抛 TextFormatException（各用独立实例，
    // 抛出后状态不保证一致）
    Console.println("dec-bad-strict: ${thrownText(func{(): String -> strictDec(makeBytes(255UB))})} ${thrownText(func{(): String -> strictDec(makeBytes(128UB))})} ${thrownText(func{(): String -> strictDec(makeBytes(192UB, 128UB))})} ${thrownText(func{(): String -> strictDec(makeBytes(224UB, 128UB, 128UB))})} ${thrownText(func{(): String -> strictDec(makeBytes(237UB, 160UB, 128UB))})} ${thrownText(func{(): String -> strictDec(makeBytes(244UB, 144UB, 128UB, 128UB))})} ${thrownText(func{(): String -> strictDec(makeBytes(240UB, 130UB, 130UB, 172UB))})}")

    // 严格消息内容抽查：原因词与零基字节位置（孤立续字节在 0；序列
    // 非法的子序列起点在 0）
    const mi = catchTextMsg(func{(): String -> strictDec(makeBytes(128UB))})
    const ms = catchTextMsg(func{(): String -> strictDec(makeBytes(237UB, 160UB, 128UB))})
    Console.println("dec-bad-msg: ${hasText(mi, "孤立的续字节")} ${hasText(mi, "于字节位置 0")} ${hasText(ms, "编码序列非法")} ${hasText(ms, "于字节位置 0")}")

    // 跨块严格报错的位置连续性：先解码 "A"（位置 0），残段 ED 后下一块
    // 的 A0 使序列非法——子序列起点为跨块累计位置 1
    const cc = new Utf8Decoder()
    const pre = cc.decode(makeBytes(65UB), false)
    cc.decode(makeBytes(237UB), false)
    const m2 = catchTextMsg(func{(): String -> cc.decode(makeBytes(160UB), false)})
    Console.println("dec-cross-strict: ${pre == "A"} ${hasText(m2, "编码序列非法")} ${hasText(m2, "于字节位置 1")}")

    // isFinal 截断位置：解码 "A" + 残段 E4 后，下一块单字节收尾——截断
    // 序列起点为位置 1
    const tp = new Utf8Decoder()
    tp.decode(makeBytes(65UB, 228UB), false)
    const m3 = catchTextMsg(func{(): String -> tp.decode(makeBytes(184UB), true)})
    Console.println("dec-trunc-pos: ${hasText(m3, "截断序列")} ${hasText(m3, "于字节位置 1")}")

    // 替换模式最大子部分割：C0 80 → 2 个 U+FFFD（各自孤立子部分）
    const repC = new Utf8Decoder(true)
    Console.println("dec-rep-c080: ${repC.decode(makeBytes(192UB, 128UB), true) == "${f}${f}"}")

    // 代理区 ED A0 80 → 3 个（ED 单独一个，A0/80 各一个）；同一实例
    // 继续喂越界 F4 90 80 80 → 4 个
    const repS = new Utf8Decoder(true)
    Console.println("dec-rep-surrogate: ${repS.decode(makeBytes(237UB, 160UB, 128UB), true) == "${f}${f}${f}"} ${repS.decode(makeBytes(244UB, 144UB, 128UB, 128UB), true) == "${f}${f}${f}${f}"}")

    // F0 82 82 AC → 4 个（F0 单独一个——82 越出 F0 的首续字节范围）
    const repM = new Utf8Decoder(true)
    Console.println("dec-rep-subpart: ${repM.decode(makeBytes(240UB, 130UB, 130UB, 172UB), true) == "${f}${f}${f}${f}"}")

    // 连续孤立续字节 → 每字节一个
    const repI = new Utf8Decoder(true)
    Console.println("dec-rep-isolated: ${repI.decode(makeBytes(128UB, 128UB, 128UB), true) == "${f}${f}${f}"}")

    // 替换模式混合合法/非法：A C0 80 B → A + 2 FFFD + B（8 字节 4 标量）
    const repX = new Utf8Decoder(true)
    const mixed = repX.decode(makeBytes(65UB, 192UB, 128UB, 66UB), true)
    Console.println("dec-rep-mixed: ${mixed == "A${f}${f}B"} ${mixed.length} ${mixed.characterCount}")

    // 替换模式回退重处理：E0 80 A → FFFD（E0）+ FFFD（80 孤立）+ A
    const repR = new Utf8Decoder(true)
    const re = repR.decode(makeBytes(224UB, 128UB, 65UB), true)
    Console.println("dec-rep-resync: ${re == "${f}${f}A"} ${re.characterCount}")

    // reset 复用（替换模式）：替换解码后 reset，位置与新流一致
    const rr = new Utf8Decoder(true)
    const before = rr.decode(makeBytes(237UB, 160UB, 128UB), true)
    rr.reset()
    Console.println("dec-reset-rep: ${before.characterCount == (3 as i64)} ${rr.decode(makeBytes(65UB, 228UB, 184UB, 173UB), true) == "A中"}")

    // reset 复用（严格模式）：抛错后 reset 恢复可用
    const rs = new Utf8Decoder()
    const tThrow = thrownText(func{(): String -> rs.decode(makeBytes(255UB), true)})
    rs.reset()
    Console.println("dec-reset-strict: ${tThrow} ${rs.decode(makeBytes(66UB), true) == "B"}")
}

// ── §4.3.4 Utf8Encoder：编码入口 ──
priv func stageEncoder() {
    const enc = new Utf8Encoder()
    const text = "A中😀B"

    // encode：与 String.toUtf8Span 逐字节一致（同一编码底座），独立副本
    const e1 = enc.encode(text)
    const t1 = text.toUtf8Span()
    Console.println("enc-eq: ${spanEq(e1, t1)} ${e1.length}")
    Console.println("enc-bytes: ${spanBytes(e1, 65UB, 228UB, 184UB, 173UB, 240UB, 159UB, 152UB, 128UB, 66UB)}")

    // encodeChar 各长度：ASCII 1 字节；U+0080 2 字节下界；U+07FF 2 字节
    // 上界；U+0800 3 字节下界；U+10FFFF 4 字节上界
    const cE: char = (0x80 as char)
    const cZ: char = (0x7FF as char)
    const c3a: char = (0x800 as char)
    const c4b: char = (0x10FFFF as char)
    Console.println("enc-char-len: ${(enc.encodeChar('A')).length} ${(enc.encodeChar(cE)).length} ${(enc.encodeChar(cZ)).length} ${(enc.encodeChar(c3a)).length} ${(enc.encodeChar(c4b)).length}")
    // encodeChar 逐字节标准编码：中 E4 B8 AD；U+10000 F0 90 80 80；
    // U+10FFFF F4 8F BF BF；U+0080 C2 80
    const c4a: char = (0x10000 as char)
    Console.println("enc-char-bytes: ${spanBytes(enc.encodeChar('中'), 228UB, 184UB, 173UB)} ${spanBytes(enc.encodeChar(c4a), 240UB, 144UB, 128UB, 128UB)} ${spanBytes(enc.encodeChar(c4b), 244UB, 143UB, 191UB, 191UB)} ${spanBytes(enc.encodeChar(cE), 194UB, 128UB)}")

    // 解码往返：decode(encode(x)) 还原原串（含补充平面 char 通道）
    const rt = new Utf8Decoder()
    const rt2 = new Utf8Decoder()
    Console.println("enc-roundtrip: ${rt.decode(enc.encode("A中😀B"), true) == "A中😀B"} ${rt2.decode(enc.encodeChar('😀'), true) == "😀"}")

    // encodeTo：定点写入（offset 0 与 offset 4 各一次拼满 9 字节缓冲）
    const dest = core.collections.spanOf\<u8>(9)
    const n1 = enc.encodeTo("A中", dest, 0)
    const n2 = enc.encodeTo("😀B", dest, 4)
    Console.println("enc-to: ${n1} ${n2} ${spanBytes(dest, 65UB, 228UB, 184UB, 173UB, 240UB, 159UB, 152UB, 128UB, 66UB)}")

    // 边界：空串在 offset==length 处写入零字节返回 0；单字符写到末位
    const edge = core.collections.spanOf\<u8>(3)
    const z = enc.encodeTo("", edge, 3)
    const one = enc.encodeTo("B", edge, 2)
    Console.println("enc-to-edge: ${z} ${one} ${(edge[2] if? (0 as u8)) == (66 as u8)}")

    // 容量守卫：需要 13 字节写入 10 容量 / offset 越过末尾后写非空 /
    // 负 offset——均抛 core.OutOfBoundException，不截断
    const cap = core.collections.spanOf\<u8>(10)
    Console.println("enc-to-cap: ${thrownOob(func{(): i32 -> enc.encodeTo("A中😀B中", cap, 0)})} ${thrownOob(func{(): i32 -> enc.encodeTo("B", cap, 10)})} ${thrownOob(func{(): i32 -> enc.encodeTo("B", cap, -1)})}")

    // 失败不部分写入：预写 X 后容量不足的 encodeTo 抛错，原内容不动
    const cap2 = core.collections.spanOf\<u8>(10)
    enc.encodeTo("X", cap2, 0)
    const failed = thrownOob(func{(): i32 -> enc.encodeTo("A中😀B中", cap2, 0)})
    Console.println("enc-to-partial: ${failed} ${(cap2[0] if? (0 as u8)) == (88 as u8)}")

    // encode 返回独立副本：修改上一次结果不影响新一次编码
    const e2 = enc.encode("AB")
    e2[0] = (88 as u8)
    const e3 = enc.encode("AB")
    Console.println("enc-copy: ${(e3[0] if? (0 as u8)) == (65 as u8)}")
}

// ── 探针辅助 ──

// 变参字节段：i32 无变参展开风险——直接收 u8 变参数组写入 Span
priv func makeBytes(parts: u8...): Span\<u8> {
    const s = core.collections.spanOf\<u8>(parts.length)
    var i: i32 = 0
    while (i < parts.length) {
        s[i] = (parts[i] if? (0 as u8))
        i = (i + 1)
    }
    return s
}

// 字节段与期望字节序列逐字节比较（全长一致才 true）
priv func spanBytes(s: Span\<u8>, parts: u8...): bool {
    if (parts.length != s.length) {
        return false
    }
    var i: i32 = 0
    while (i < s.length) {
        if ((s[i] if? (0 as u8)) != (parts[i] if? (0 as u8))) {
            return false
        }
        i = (i + 1)
    }
    return true
}

// 两字节段等长逐字节比较
priv func spanEq(a: Span\<u8>, b: Span\<u8>): bool {
    if (a.length != b.length) {
        return false
    }
    var i: i32 = 0
    while (i < a.length) {
        if ((a[i] if? (0 as u8)) != (b[i] if? (0 as u8))) {
            return false
        }
        i = (i + 1)
    }
    return true
}

// 独立严格解码器单块整解（非法输入负例辅助：各负例互不共享实例）
priv func strictDec(bytes: Span\<u8>): String {
    const d = new Utf8Decoder()
    return d.decode(bytes, true)
}

// 捕获 TextFormatException 并回填消息（null = 未抛出）
priv func catchTextMsg(body: core.Func\<String>): String? {
    try {
        body.call()
        return null
    } catch (e: core.text.TextFormatException) {
        const m: String? = e.getMessage()
        return m
    }
}

// 消息非空且包含指定子串（严格失败消息恒以固定前缀开头，空串即未抛）
priv func hasText(m: String?, needle: String): bool {
    const s: String = (m if? "")
    if (s == "") {
        return false
    }
    return s.contains(needle)
}

// 捕获 TextFormatException 的负例辅助（true = 已抛出）
priv func thrownText(body: core.Func\<String>): bool {
    try {
        body.call()
        return false
    } catch (e: core.text.TextFormatException) {
        return true
    }
}

// 捕获 core.OutOfBoundException 的负例辅助（true = 已抛出）
priv func thrownOob(body: core.Func\<i32>): bool {
    try {
        body.call()
        return false
    } catch (e: core.OutOfBoundException) {
        return true
    }
}
