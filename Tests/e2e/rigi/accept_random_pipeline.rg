// ============================================================================
// accept_random_pipeline.rg —— 施工块 8-2：MVP 应用验收（STDLIB §8）场景 6
// 「可复现随机数据」：固定种子的 Random 生成有界整数（minInclusive/
// maxExclusive）、浮点（nextDouble/nextFloat）和字节数据（fillBytes）→
// 交给集合（map/filter/fold/sorted）与数学 API（聚合/round/clamp/floor）
// 处理 → 确定性输出。NativeE2E「验收场景6 可复现随机数据对拍」Case
// 复用本语料（VM/native 双宿主逐位一致）。
//
//   ① 已知向量锚点：种子 42 的首发向量与 math_random.rg 同口径（独立
//      参考实现 SplitMix64 + xoshiro256** 一次性算出后写死）——有界
//      i32 [-5,5)、全宽 u64、单值区间 u64 [0,100)、nextFloat/nextDouble
//      首发值；钉死位消费契约的应用面锚点。
//   ② 相同调用序列产出相同输入数据：同一程序内建两个同种子（20260924）
//      Random 实例走同一混合调用序列（24 发有界 i32 [-1000,1000)、
//      12 发 nextDouble、8 发 nextFloat、fillBytes 整 Span 16 字节、
//      8 发有界 u8 [10,20)），逐值断言相等；并断言简单不变量：有界值
//      全部落在 [min,max)、浮点全部落在 [0,1) 且按位宽网格对齐
//     （×2^53 / ×2^24 恒为整数，math_random.rg 口径）。
//   ③ 集合处理：fold 聚合与手写循环同值、filter 计数与手写一致、map
//     （加倍）和为原和两倍、sorted 升序非降且首=手写 min、末=手写
//      max、总和保持——处理结果确定性（同输入必同输出）。
//   ④ 数学 API：均值 round 定标（×1000）、clamp 有界锚定（min/max 夹
//      进 [-5,5)、浮点 max 夹进 [0.0,0.5]）、floor 网格——全部确定值。
//   ⑤ 字节管线：fillBytes 数据按 %4 分桶计数 + 总和聚合，双实例一致；
//   ⑥ 浮点聚合：double 列表 fold 与手写累加同值（同序 IEEE 加法）、
//      float 逐个升宽累加，定标 round 后整数输出（输出面全为整数，
//      不依赖浮点文本格式）。
// expect-output: accept-random vectors-ok
// expect-output: accept-random replay-ok
// expect-output: accept-random agg count=24 sum=-384 pos=10 mean-x1000=-16000
// expect-output: accept-random ordered first=-924 last=891 doubled-sum=-768
// expect-output: accept-random bytes sum=2035 buckets=2/4/7/3
// expect-output: accept-random floats dsum-x1e6=5360961 fsum-x1e6=3515249 hi=5 clamp05-x1000=500
// expect-output: accept-random-ok
// expect-exit: 0
// ============================================================================
import core.collections.*
import core.io.*
import core.math.*

// 失败计数（fs_* 先例：多宿主输出只钉确定行；check 失败即打印）。
var fails: i32 = 0

func check(name: String, cond: bool) {
    if (not cond) {
        Console.println("check-FAIL ${name}")
        fails = (fails + 1)
    }
}

pub func main(): i32 {
    const seed = 20260924 as u64

    // ── ① 已知向量锚点（math_random.rg 口径：种子 42 首发向量）──
    var r = new Random(42 as u64)
    check("vec-i32", r.nextI32(-5, 5) == 3)
    r = new Random(42 as u64)
    check("vec-u64", r.nextU64() == 0x15780B2E0C2EC716UL)
    r = new Random(42 as u64)
    check("vec-u64-bounded", r.nextU64(0 as u64, 100 as u64) == (42 as u64))
    r = new Random(42 as u64)
    check("vec-f32", r.nextFloat() == (0.08386296033859253 as float))
    r = new Random(42 as u64)
    check("vec-f64", r.nextDouble() == 0.08386297105988216)
    if (fails == 0) { Console.println("accept-random vectors-ok") }

    // ── ② 双实例同序列逐值相等 + 区间/网格不变量 ──
    const a = new Random(seed)
    const b = new Random(seed)
    const ints = new List\<i64>()
    var replayOk = true
    var rangeOk = true
    var i: i32 = 0
    while (i < 24) {
        const va = a.nextI32(-1000, 1000)
        const vb = b.nextI32(-1000, 1000)
        if (va != vb) { replayOk = false }
        if ((va < -1000) or (va >= 1000)) { rangeOk = false }
        ints.add(va as i64)
        i = (i + 1)
    }
    const dbls = new List\<double>()
    var dOk = true
    i = 0
    while (i < 12) {
        const da = a.nextDouble()
        const db = b.nextDouble()
        if (da != db) { replayOk = false }
        if ((da < 0.0) or (da >= 1.0)) { dOk = false }
        if (floor(da * 9007199254740992.0) != (da * 9007199254740992.0)) {
            dOk = false
        }
        dbls.add(da)
        i = (i + 1)
    }
    const flts = new List\<float>()
    var fOk = true
    i = 0
    while (i < 8) {
        const fa = a.nextFloat()
        const fb = b.nextFloat()
        if (fa != fb) { replayOk = false }
        if ((fa < (0.0 as float)) or (fa >= (1.0 as float))) { fOk = false }
        if (floor(fa * (16777216.0 as float)) != (fa * (16777216.0 as float))) {
            fOk = false
        }
        flts.add(fa)
        i = (i + 1)
    }
    const ba = spanOf\<u8>(16)
    const bb = spanOf\<u8>(16)
    a.fillBytes(ba)
    b.fillBytes(bb)
    var bytesOk = true
    i = 0
    while (i < 16) {
        if ((ba[i] if? (0 as u8)) != (bb[i] if? (0 as u8))) { bytesOk = false }
        i = (i + 1)
    }
    var u8Ok = true
    i = 0
    while (i < 8) {
        const ua = a.nextU8((10 as u8), (20 as u8))
        const ub = b.nextU8((10 as u8), (20 as u8))
        if (ua != ub) { replayOk = false }
        if ((ua < (10 as u8)) or (ua >= (20 as u8))) { u8Ok = false }
        i = (i + 1)
    }
    check("replay-sequence", replayOk)
    check("range-bounded", rangeOk)
    check("range-double", dOk)
    check("range-float", fOk)
    check("replay-bytes", bytesOk)
    check("range-u8", u8Ok)
    if (fails == 0) { Console.println("accept-random replay-ok") }

    // ── ③ 集合处理（预期值与手写循环对照，处理结果确定性）──
    var sum: i64 = 0L
    var posCount: i64 = 0L
    var minV: i64 = 0L
    var maxV: i64 = 0L
    i = 0
    while (i < 24) {
        const v = (ints.getAtIndex(i as i64) if? 0L)
        if (i == 0) {
            minV = v
            maxV = v
        }
        sum = (sum + v)
        if (v >= 0L) { posCount = (posCount + 1L) }
        if (v < minV) { minV = v }
        if (v > maxV) { maxV = v }
        i = (i + 1)
    }
    const sumFold = fold\<i64, i64>(ints, 0L,
        func{(acc: i64, x: i64): i64 -> (acc + x)})
    const positives = filter\<i64>(ints, func{(x: i64): bool -> (x >= 0L)})
    const doubled = map\<i64, i64>(ints, func{(x: i64): i64 -> (x * 2L)})
    const doubledSum = fold\<i64, i64>(doubled, 0L,
        func{(acc: i64, x: i64): i64 -> (acc + x)})
    const ascCmp = func{(x: i64, y: i64): core.ComparisonResult -> compare(x, y)}
    const ordered = sorted\<i64>(ints, ascCmp)
    const first = (ordered.getAtIndex(0L) if? (0L - 1L))
    const last = (ordered.getAtIndex((ordered.length - 1L)) if? (0L - 1L))
    var nonDecreasing = true
    var sortedSum: i64 = 0L
    var k: i64 = 0L
    while (k < ordered.length) {
        const v = (ordered.getAtIndex(k) if? 0L)
        sortedSum = (sortedSum + v)
        if (k > 0L) {
            const prev = (ordered.getAtIndex((k - 1L)) if? 0L)
            if (v < prev) { nonDecreasing = false }
        }
        k = (k + 1L)
    }
    check("agg-fold-eq-loop", sumFold == sum)
    check("agg-count", ints.length == 24L)
    check("agg-filter", positives.length == posCount)
    check("agg-map-double", doubledSum == (sum * 2L))
    check("sorted-non-decreasing", nonDecreasing)
    check("sorted-extremes", (first == minV) and (last == maxV))
    check("sorted-sum-kept", sortedSum == sum)

    // ── ④ 数学 API：均值定标 + 有界锚定（确定值输出）──
    const meanScaled = round(((sum as double) / 24.0) * 1000.0)
    const meanOut = (meanScaled as i64)
    const clampedLo = clamp(minV, (0L - 5L), 5L)
    const clampedHi = clamp(maxV, (0L - 5L), 5L)
    Console.println("accept-random agg count=${ints.length} sum=${sum} pos=${posCount} mean-x1000=${meanOut}")
    Console.println("accept-random ordered first=${first} last=${last} doubled-sum=${doubledSum}")
    check("clamp-lo", ((clampedLo == minV) or (clampedLo == (0L - 5L))) and
        (clampedLo >= (0L - 5L)))
    check("clamp-hi", ((clampedHi == maxV) or (clampedHi == 5L)) and
        (clampedHi <= 5L))

    // ── ⑤ 字节管线：分桶 + 总和（双实例一致的数据已由②逐字节断言）──
    var byteSum: i64 = 0L
    var b0: i64 = 0L
    var b1: i64 = 0L
    var b2: i64 = 0L
    var b3: i64 = 0L
    i = 0
    while (i < 16) {
        const v = ((ba[i] if? (0 as u8)) as i64)
        byteSum = (byteSum + v)
        const m = v - ((v / 4L) * 4L)
        if (m == 0L) {
            b0 = (b0 + 1L)
        } else {
            if (m == 1L) {
                b1 = (b1 + 1L)
            } else {
                if (m == 2L) {
                    b2 = (b2 + 1L)
                } else {
                    b3 = (b3 + 1L)
                }
            }
        }
        i = (i + 1)
    }
    Console.println("accept-random bytes sum=${byteSum} buckets=${b0}/${b1}/${b2}/${b3}")

    // ── ⑥ 浮点聚合：fold 与手写累加同值（同序 IEEE 加法）、float 升宽
    //    累加；round 定标整数输出；浮点 clamp 锚定 [0,0.5] ──
    const dsumFold = fold\<double, double>(dbls, 0.0,
        func{(acc: double, x: double): double -> (acc + x)})
    var dsum: double = 0.0
    i = 0
    while (i < 12) {
        dsum = (dsum + (dbls.getAtIndex(i as i64) if? 0.0))
        i = (i + 1)
    }
    var fsum: double = 0.0
    i = 0
    while (i < 8) {
        fsum = (fsum + ((flts.getAtIndex(i as i64) if? (0.0 as float)) as double))
        i = (i + 1)
    }
    var hi: i64 = 0L
    i = 0
    while (i < 12) {
        if ((dbls.getAtIndex(i as i64) if? 0.0) >= 0.5) { hi = (hi + 1L) }
        i = (i + 1)
    }
    var maxD: double = 0.0
    i = 0
    while (i < 12) {
        const v = (dbls.getAtIndex(i as i64) if? 0.0)
        if (v > maxD) { maxD = v }
        i = (i + 1)
    }
    const clamp05 = clamp(maxD, 0.0, 0.5)
    check("dsum-fold-eq-loop", dsumFold == dsum)
    check("clamp05-bounded", (clamp05 >= 0.0) and (clamp05 <= 0.5))
    const dScaled = round(dsum * 1000000.0)
    const fScaled = round(fsum * 1000000.0)
    const cScaled = round(clamp05 * 1000.0)
    const dOut = (dScaled as i64)
    const fOut = (fScaled as i64)
    const cOut = (cScaled as i64)
    Console.println("accept-random floats dsum-x1e6=${dOut} fsum-x1e6=${fOut} hi=${hi} clamp05-x1000=${cOut}")

    if (fails == 0) {
        Console.println("accept-random-ok")
        return 0
    }
    return 1
}
