// ============================================================================
// accept_data_pipeline.rg —— 施工块 8-1：MVP 应用验收（STDLIB §8）场景 2
// 「数据处理」：从内存流读取远大于内部缓冲的 UTF-8 输入，字节层逐行
// 切分与数值解析 → 集合转换与聚合（map/filter/fold/sorted）→ 数学 API
// 计算 → 输出；另以小样演示 TextReader 逐行（readLine）与整体
// （readToEnd）形态。NativeE2E「验收场景2 数据处理对拍」Case 复用本
// 语料（VM/native 双宿主一致）。
//
//   ① 输入大于缓冲区：整体输入约 22 KiB（> 5 倍常见 4 KiB 内部缓冲），
//      经 InputStream.readAll 整体读取——其内部即「4 KiB 有界缓冲循环
//      逐块读入」通道（§4.7.1 readAllBytes 同款表述），块边界必然切在
//      多字节 UTF-8 序列与行之间。
//   ② 逐行处理（字节层）：UTF-8 序列内部不出现 0x0A，字节 10 恰为行界
//      ——单遍字节扫描逐行切分，数值字段按 ASCII 数字直接累加解析，
//      采样行（注释行/emoji 行/末行等）再经行级小段严格解码逐字断言
//      （emoji 行内容即跨块多字节序列完整性的证据）。行级 String API
//      （split/parse）的形态演示收敛到 TextReader 小样（解释执行下
//      每行 String 切片/解码的常数过大；语义等价性由「同一样本两种
//      路径同值」承担——小样 join 与原文一致 + 字节层解析值与闭式
//      预期一致）。
//   ③ 保留全部数据的步骤声明：readAll 的结果 Span 即全部输入字节的
//      整体内存形态（readToEnd/readAll「一次性消费剩余全部内容并持有
//      完整结果串」的性质，由小样 readToEnd 可执行验证）。
//   ④ 集合转换与聚合：全量 count/sum/min/max 经字节层逐行聚合；map
//      （加倍再 fold）/filter（阈值）/fold/sorted（升序首末）四种回调式
//      算法在前 24 行数值的确定性样本上覆盖（预期值闭式可算）。
//   ⑤ 数学 API：均值经全量和与 math.round（×1000 定标避开浮点文本
//      格式依赖）、sqrt、pow、clamp；全部断言为确定值。
//   ⑥ 不依赖路径或命令行参数 API（输入来自内存流，§8 场景 2 形态）。
//
// 数据模式（确定性，预期值可闭式计算）：数据行 i=1..800，数值 = i%7：
//   v=0:114 个、v=1:115、v=2:115、v=3:114、v=4:114、v=5:114、v=6:114
//   → count=800、sum=2397、min=0、max=6、样本(前 24 行) sum=69、
//     filter(≥5)=6、sorted 首末 5/6、round(mean×1000)=2996。
// expect-output: accept-pipeline count=800 sum=2397 min=0 max=6
// expect-output: accept-pipeline sample24 sum=69 doubled-sum=138 filtered=6 first=5 last=6
// expect-output: accept-pipeline mean-x1000=2996 sqrt6-x1000=2449 pow2p10=1024 clamped=5
// expect-output: accept-pipeline small-lines-ok
// expect-output: accept-pipeline-ok
// expect-exit: 0
// ============================================================================
import core.collections.*
import core.io.*
import core.math.*
import core.text.*

// 首败即返。
func adFail(code: i32, msg: String): i32 {
    Console.println("FAIL ${code}: ${msg}")
    return code
}

func adExpect(cond: bool, code: i32, msg: String): i32 {
    if (cond) { return 0 }
    return adFail(code, msg)
}

// 字节段 [start, end) → 行文本（行级小段严格解码；段为完整 UTF-8 序列）。
func adRowOf(all: Span\<u8>, start: i32, end: i32): String {
    const seg = spanOf\<u8>((end - start))
    var k: i32 = 0
    while (k < seg.length) {
        seg[k] = (all[(start + k)] as u8)
        k = (k + 1)
    }
    return new Utf8Decoder().decode(seg, true)
}

pub func main(): i32 {
    // ── 构造整体输入（约 22 KiB，远大于 4 KiB 内部缓冲；末行无换行；
    //    emoji 行落在三处，多字节序列必然被内部块边界切开）──
    const sb = new StringBuilder()
    sb.append("# rigi 数据管道验收样例\n")
    var i: i64 = 1L
    while (i <= 800L) {
        var name = "数据行${i}号"
        if (((i == 200L) or (i == 400L)) or (i == 600L)) {
            name = "数据行${i}号😀"
        }
        const v = i - ((i / 7L) * 7L)
        sb.append("${name},${v},批次${i}")
        if (i < 800L) {
            sb.append("\n")
        }
        i = (i + 1L)
    }
    const wholeText = sb.toString()
    // Span<u8>.length 为 i32：与 i32 字面量比较（5 倍内部缓冲下限）。
    const inputBytes = wholeText.toUtf8Span().length
    var rc = adExpect(inputBytes > 20480, 11,
        "输入未远大于 4 KiB 内部缓冲：${inputBytes}")
    if (rc != 0) { return rc }

    // ── ① 整体读取（readAll：内部 4 KiB 有界缓冲循环逐块读入并拼为
    //    完整结果 Span——「一次性消费剩余全部内容」的整体内存形态，
    //    全部输入字节在此保留）──
    const input = new MemoryInputStream(wholeText.toUtf8Span())
    const all = input.readAll()
    input.dispose()
    rc = adExpect(all.length == inputBytes, 12,
        "读取字节数不符：${all.length}")
    if (rc != 0) { return rc }

    // ── ② 字节层逐行切分 + 数值字段解析（状态机单遍扫描；行级 String
    //    API 只用于采样行）──
    var count: i64 = 0L
    var sum: i64 = 0L
    var minV: i64 = 7L
    var maxV: i64 = 0L - 1L
    const sample = new List\<i64>()
    var start: i32 = 0
    var idx: i32 = 0
    var rowNo: i32 = 0
    var emojiAt: i32 = 0 - 1
    var emojiEnd: i32 = 0 - 1
    var commentAt: i32 = 0 - 1
    var commentEnd: i32 = 0 - 1
    var lastStart: i32 = 0 - 1
    // 单行 [start, idx) 的数值字段处理：第 1 个逗号后到第 2 个逗号/行尾
    // 的 ASCII 数字直接累加（名称段与批次段中的数字不参与——以逗号计数
    // 定界）
    while (idx < all.length) {
        const b: i32 = ((all[idx] if? (0 as u8)) as i32)
        if (b == 10) {
            rowNo = (rowNo + 1)
            if (rowNo == 1) {
                commentAt = start
                commentEnd = idx
            }
            if (rowNo == 401) {
                emojiAt = start
                emojiEnd = idx
            }
            var p: i32 = start
            var commas: i32 = 0
            var value: i64 = 0L
            var hasDigits: bool = false
            while (p < idx) {
                const c: i32 = ((all[p] if? (0 as u8)) as i32)
                if (c == 44) {
                    commas = (commas + 1)
                }
                if (((commas == 1) and (c >= 48)) and (c <= 57)) {
                    value = ((value * 10L) + ((c - 48) as i64))
                    hasDigits = true
                }
                p = (p + 1)
            }
            if (hasDigits and (commas == 2)) {
                count = (count + 1L)
                sum = (sum + value)
                if (value < minV) { minV = value }
                if (value > maxV) { maxV = value }
                if (count <= 24L) {
                    sample.add(value)
                }
            }
            start = (idx + 1)
        }
        idx = (idx + 1)
    }
    // 末行：末尾无换行的最后一行正常成行
    if (start < all.length) {
        rowNo = (rowNo + 1)
        lastStart = start
        var p2: i32 = start
        var commas2: i32 = 0
        var value2: i64 = 0L
        var hasDigits2: bool = false
        while (p2 < all.length) {
            const c2: i32 = ((all[p2] if? (0 as u8)) as i32)
            if (c2 == 44) {
                commas2 = (commas2 + 1)
            }
            if (((commas2 == 1) and (c2 >= 48)) and (c2 <= 57)) {
                value2 = ((value2 * 10L) + ((c2 - 48) as i64))
                hasDigits2 = true
            }
            p2 = (p2 + 1)
        }
        if (hasDigits2 and (commas2 == 2)) {
            count = (count + 1L)
            sum = (sum + value2)
            if (value2 < minV) { minV = value2 }
            if (value2 > maxV) { maxV = value2 }
            if (count <= 24L) {
                sample.add(value2)
            }
        }
    }

    // ── 聚合断言（与闭式预期一致）──
    rc = adExpect(count == 800L, 21, "count=${count}")
    if (rc != 0) { return rc }
    rc = adExpect(sum == 2397L, 22, "sum=${sum}")
    if (rc != 0) { return rc }
    rc = adExpect(minV == 0L, 23, "min=${minV}")
    if (rc != 0) { return rc }
    rc = adExpect(maxV == 6L, 24, "max=${maxV}")
    if (rc != 0) { return rc }
    // emoji 行（第 401 行，i=400）跨块多字节序列逐字无损；注释行同证。
    rc = adExpect((emojiAt >= 0) and (commentAt >= 0), 25, "采样行未定位")
    if (rc != 0) { return rc }
    const emojiLine = adRowOf(all, emojiAt, emojiEnd)
    rc = adExpect(emojiLine == "数据行400号😀,1,批次400", 26,
        "emoji 行损坏：[${emojiLine}]")
    if (rc != 0) { return rc }
    const commentLine = adRowOf(all, commentAt, commentEnd)
    rc = adExpect(commentLine.startsWith("# rigi"), 27,
        "注释行损坏：[${commentLine}]")
    if (rc != 0) { return rc }
    const lastLine = adRowOf(all, lastStart, all.length)
    rc = adExpect(lastLine == "数据行800号,2,批次800", 28,
        "末行损坏：[${lastLine}]")
    if (rc != 0) { return rc }
    Console.println("accept-pipeline count=${count} sum=${sum} min=${minV} max=${maxV}")

    // ── 集合转换算法覆盖（map/filter/fold/sorted，24 元素确定性样本，
    //    预期值闭式可算：i=1..24 的 i%7 → sum=69、filter(≥5)=6、
    //    sorted 首末 5/6）──
    const sampleSum = fold\<i64, i64>(sample, 0L, func{(acc: i64, x: i64): i64 -> (acc + x)})
    const doubled = map\<i64, i64>(sample, func{(x: i64): i64 -> (x * 2L)})
    const doubledSum = fold\<i64, i64>(doubled, 0L, func{(acc: i64, x: i64): i64 -> (acc + x)})
    const big = filter\<i64>(sample, func{(x: i64): bool -> (x >= 5L)})
    const ascCmp = func{(a: i64, b: i64): core.ComparisonResult -> compare(a, b)}
    const ordered = sorted\<i64>(big, ascCmp)
    const first = (ordered.getAtIndex(0L) if? (0L - 1L))
    const last = (ordered.getAtIndex((ordered.length - 1L)) if? (0L - 1L))
    rc = adExpect(sampleSum == 69L, 31, "sample-sum=${sampleSum}")
    if (rc != 0) { return rc }
    rc = adExpect(doubledSum == 138L, 32, "doubled-sum=${doubledSum}")
    if (rc != 0) { return rc }
    rc = adExpect(big.length == 6L, 33, "filtered=${big.length}")
    if (rc != 0) { return rc }
    rc = adExpect((first == 5L) and (last == 6L), 34, "sorted 首末=${first}/${last}")
    if (rc != 0) { return rc }
    Console.println("accept-pipeline sample24 sum=${sampleSum} doubled-sum=${doubledSum} filtered=${big.length} first=${first} last=${last}")

    // ── 数学 API：均值/sqrt/pow/clamp（确定值断言）──
    const meanRounded = round(((sum * 1000L) as double) / (count as double))
    const sqrt6x1000 = round(sqrt((6 as double)) * 1000.0)
    const pow2p10 = pow((2 as double), (10 as double))
    const clampedHi = clamp(maxV, 0L, 5L)
    const meanOut = (meanRounded as i64)
    const sqrtOut = (sqrt6x1000 as i64)
    const powOut = (pow2p10 as i64)
    rc = adExpect(meanOut == 2996L, 41, "mean-x1000=${meanOut}")
    if (rc != 0) { return rc }
    rc = adExpect(sqrtOut == 2449L, 42, "sqrt6-x1000=${sqrtOut}")
    if (rc != 0) { return rc }
    rc = adExpect(powOut == 1024L, 43, "pow2p10=${powOut}")
    if (rc != 0) { return rc }
    rc = adExpect(clampedHi == 5L, 44, "clamped=${clampedHi}")
    if (rc != 0) { return rc }
    Console.println("accept-pipeline mean-x1000=${meanOut} sqrt6-x1000=${sqrtOut} pow2p10=${powOut} clamped=${clampedHi}")

    // ── TextReader 小样（readLine 逐行 + readToEnd 整体读取对照）──
    const smallText = "行一,甲\n行二,乙\n数据行3号,3,批次丙\n末行,丁"
    const sr = new TextReader(new MemoryInputStream(smallText.toUtf8Span()))
    const l1 = (sr.readLine() if? "?")
    const l2 = (sr.readLine() if? "?")
    const l3 = (sr.readLine() if? "?")
    const l4 = (sr.readLine() if? "?")
    const l5 = sr.readLine()
    sr.dispose()
    rc = adExpect(((l1 == "行一,甲") and (l2 == "行二,乙")) and ((l3 == "数据行3号,3,批次丙") and (l4 == "末行,丁")),
        51, "逐行内容不符")
    if (rc != 0) { return rc }
    rc = adExpect(l5 == null, 52, "EOF 应为 null")
    if (rc != 0) { return rc }
    const se = new TextReader(new MemoryInputStream(smallText.toUtf8Span()))
    const smallWhole = se.readToEnd()
    se.dispose()
    rc = adExpect(smallWhole == smallText, 53, "readToEnd 与原文不符")
    if (rc != 0) { return rc }
    const four = new List\<String>()
    four.add(l1)
    four.add(l2)
    four.add(l3)
    four.add(l4)
    rc = adExpect("\n".join(four) == smallText, 54, "保留行 join 与原文不符")
    if (rc != 0) { return rc }
    Console.println("accept-pipeline small-lines-ok")

    Console.println("accept-pipeline-ok")
    return 0
}
