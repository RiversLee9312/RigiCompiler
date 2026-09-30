// ============================================================================
// math_random.rg —— 施工块 6-5（STDLIB §4.11.4–§4.11.5 / D7）
// core.math Random：已知输出向量与范围语义，VM 与 native 双宿主对拍
// （NativeE2E「Random 已知向量与范围语义对拍」Case 复用本语料）。
//
// 已知向量的来源：独立参考实现（SplitMix64 与 xoshiro256** 1.0，
// prng.di.unimi.it 参考算法的逐字移植，playground 一次性算出后写死）。
// 语料钉死可复现契约：相同种子 + 相同调用序列在 VM/native 与
// Windows/Linux 必须逐位一致。
//
//   ① SplitMix64 种子展开 + xoshiro256** 核心：种子 0、1、0xDEADBEEF、
//      u64 最大值的前 4 个核心输出（nextU64 全宽直通）。
//   ② 全宽整数的位消费：种子 42 混合调用序列——每种宽度每次消耗一个
//      新核心输出、取最高目标宽度位，有符号按二进制补码解释。
//   ③ 有界拒绝采样：半开区间均匀向量（u8 [10,20)）、跨有符号零点
//      （i8 [-128,127)、i16 [-300,300)、i32 [-5,5)）、u64 小区间
//      [0,100)；单元素区间消耗恰好一个核心输出；非法区间抛
//      OutOfBoundException 且不消耗状态。
//   ④ 小区间均匀性：种子 7 的 u8 [0,10) 前 20000 发逐桶计数与参考
//      实现逐桶一致（拒绝采样不引入偏差；计数确定性，无统计 flaky）。
//   ⑤ 浮点网格：nextFloat 取最高 24 位 / 2^24、nextDouble 取最高 53
//      位 / 2^53——已知向量钉死位消费；大量采样验证 [0,1)（不为 1）
//      且值 × 2^24 / 2^53 恒为整数（网格对齐——「可为 0」由网格含 0
//      构造性保证，位消费向量钉死该构造）。
//   ⑥ nextBool：消耗一个核心输出、最高位判定（种子 42：0、0、1）。
//   ⑦ fillBytes：小端字节序（core[0] LE）、9 字节尾块只取次核低字节、
//      尾块丢弃后状态推进、整 Span 重载、零长度不消耗状态、越界抛错
//      且缓冲区与生成器状态均不改。
//   ⑧ 可复现性：同一混合调用序列在两个全新实例上逐位一致；无参构造
//      两个实例（极大概率）产生不同序列（系统随机源种子）。
// expect-output: sec-core-ok
// expect-output: sec-width-ok
// expect-output: sec-bounded-ok
// expect-output: sec-uniform-ok
// expect-output: sec-float-ok
// expect-output: sec-bool-ok
// expect-output: sec-fill-ok
// expect-output: sec-replay-ok
// expect-output: math-random-ok
// expect-exit: 0
// ============================================================================
import core.math.*
import core.io.Console
import core.collections.*

func check(name: String, cond: bool): i32 {
    if (cond) { return 0 }
    Console.println("check-FAIL ${name}")
    return 1
}

pub func main(): i32 {
    var fails: i32 = 0

    // ---- ① 核心已知向量：SplitMix64 展开 + xoshiro256** 前 4 发 ----
    var r = new Random(0 as u64)
    fails = fails + check("core-s0-0", r.nextU64() == 11091344671253066420UL)
    fails = fails + check("core-s0-1", r.nextU64() == 13793997310169335082UL)
    fails = fails + check("core-s0-2", r.nextU64() == 1900383378846508768UL)
    fails = fails + check("core-s0-3", r.nextU64() == 7684712102626143532UL)
    r = new Random(1 as u64)
    fails = fails + check("core-s1-0", r.nextU64() == 12966619160104079557UL)
    fails = fails + check("core-s1-1", r.nextU64() == 9600361134598540522UL)
    fails = fails + check("core-s1-2", r.nextU64() == 10590380919521690900UL)
    fails = fails + check("core-s1-3", r.nextU64() == 7218738570589545383UL)
    r = new Random(0xDEADBEEFUL)
    fails = fails + check("core-sd-0", r.nextU64() == 14219364052333592195UL)
    fails = fails + check("core-sd-1", r.nextU64() == 7332719151195188792UL)
    fails = fails + check("core-sd-2", r.nextU64() == 6122488799882574371UL)
    fails = fails + check("core-sd-3", r.nextU64() == 4799409443904522999UL)
    // u64 最大种子（全部 u64 种子包括零均合法）
    r = new Random(18446744073709551615UL)
    fails = fails + check("core-sm-0", r.nextU64() == 10328197420357168392UL)
    fails = fails + check("core-sm-1", r.nextU64() == 14156678507024973869UL)
    fails = fails + check("core-sm-2", r.nextU64() == 9357971779955476126UL)
    fails = fails + check("core-sm-3", r.nextU64() == 13791585006304312367UL)
    if (fails == 0) { Console.println("sec-core-ok") }

    // ---- ② 全宽整数位消费：种子 42 的第 0..7 个核心输出 ----
    var m = new Random(42 as u64)
    fails = fails + check("w-u8", m.nextU8() == (21 as u8))
    fails = fails + check("w-i8", m.nextI8() == (97 as i8))
    fails = fails + check("w-u16", m.nextU16() == (44567 as u16))
    fails = fails + check("w-i16", m.nextI16() == ((-4936) as i16))
    fails = fails + check("w-u32", m.nextU32() == (4259765375UL as u32))
    fails = fails + check("w-i32", m.nextI32() == ((-988961487) as i32))
    fails = fails + check("w-i64", m.nextI64() == (-5178765164775350862L))
    fails = fails + check("w-u64", m.nextU64() == 0xD99A2743EBE60087UL)
    if (fails == 0) { Console.println("sec-width-ok") }

    // ---- ③ 有界拒绝采样 ----
    r = new Random(42 as u64)
    fails = fails + check("b-u8-0", r.nextU8((10 as u8), (20 as u8)) == (11 as u8))
    fails = fails + check("b-u8-1", r.nextU8((10 as u8), (20 as u8)) == (17 as u8))
    fails = fails + check("b-u8-2", r.nextU8((10 as u8), (20 as u8)) == (14 as u8))
    fails = fails + check("b-u8-3", r.nextU8((10 as u8), (20 as u8)) == (16 as u8))
    // 单元素区间消耗恰好一个核心输出（下一发 = core[1]）
    r = new Random(42 as u64)
    fails = fails + check("b-single", r.nextU8((5 as u8), (6 as u8)) == (5 as u8))
    fails = fails + check("b-single-consume", r.nextU64() == 0x6104D9866D113A7EUL)
    // 跨有符号零点：宽度按同宽无符号数学表示
    r = new Random(42 as u64)
    fails = fails + check("b-i8-0", r.nextI8((-128 as i8), (127 as i8)) == ((-107) as i8))
    fails = fails + check("b-i8-1", r.nextI8((-128 as i8), (127 as i8)) == ((-31) as i8))
    fails = fails + check("b-i8-2", r.nextI8((-128 as i8), (127 as i8)) == (46 as i8))
    fails = fails + check("b-i8-3", r.nextI8((-128 as i8), (127 as i8)) == (108 as i8))
    r = new Random(42 as u64)
    fails = fails + check("b-i16-0", r.nextI16((-300) as i16, (300 as i16)) == ((-204) as i16))
    fails = fails + check("b-i16-1", r.nextI16((-300) as i16, (300 as i16)) == ((-64) as i16))
    fails = fails + check("b-i16-2", r.nextI16((-300) as i16, (300 as i16)) == ((-133) as i16))
    fails = fails + check("b-i16-3", r.nextI16((-300) as i16, (300 as i16)) == ((-300) as i16))
    r = new Random(42 as u64)
    fails = fails + check("b-i32-0", r.nextI32(-5, 5) == 3)
    fails = fails + check("b-i32-1", r.nextI32(-5, 5) == (-3))
    fails = fails + check("b-i32-2", r.nextI32(-5, 5) == (-5))
    fails = fails + check("b-i32-3", r.nextI32(-5, 5) == 4)
    r = new Random(42 as u64)
    fails = fails + check("b-i64-0", r.nextI64(-1000000L, 1000000L) == (-441258L))
    fails = fails + check("b-i64-1", r.nextI64(-1000000L, 1000000L) == (-456898L))
    r = new Random(42 as u64)
    fails = fails + check("b-u16-0", r.nextU16((65000 as u16), (65535 as u16)) == (65146 as u16))
    fails = fails + check("b-u16-1", r.nextU16((65000 as u16), (65535 as u16)) == (65226 as u16))
    r = new Random(42 as u64)
    fails = fails + check("b-u32-0", r.nextU32((4000000000UL as u32), (4294967295UL as u32)) == (4065221423UL as u32))
    fails = fails + check("b-u32-1", r.nextU32((4000000000UL as u32), (4294967295UL as u32)) == (4152871307UL as u32))
    r = new Random(42 as u64)
    fails = fails + check("b-u64-0", r.nextU64(0 as u64, 100 as u64) == 42 as u64)
    fails = fails + check("b-u64-1", r.nextU64(0 as u64, 100 as u64) == 2 as u64)
    fails = fails + check("b-u64-2", r.nextU64(0 as u64, 100 as u64) == 9 as u64)
    fails = fails + check("b-u64-3", r.nextU64(0 as u64, 100 as u64) == 93 as u64)
    // 非法区间抛 OutOfBoundException 且不消耗状态
    r = new Random(42 as u64)
    var threw: bool = false
    try {
        r.nextU8((7 as u8), (7 as u8))
    } catch (e: core.OutOfBoundException) {
        threw = true
    }
    fails = fails + check("b-oob-throws", threw)
    fails = fails + check("b-oob-no-consume", r.nextU64() == 0x15780B2E0C2EC716UL)
    if (fails == 0) { Console.println("sec-bounded-ok") }

    // ---- ④ 小区间均匀性：种子 7 的 u8 [0,10) 前 20000 发逐桶计数 ----
    // （与参考实现逐桶一致；确定性语料，拒绝采样无偏的直接证据）
    r = new Random(7 as u64)
    var counts = arrayOf\<i32>(10)
    var i: i32 = 0
    while (i < 20000) {
        const v: u8 = r.nextU8((0 as u8), (10 as u8))
        counts[v as i32] = (counts[v as i32] if? 0) + 1
        i = i + 1
    }
    fails = fails + check("u-c0", (counts[0] if? 0) == 2029)
    fails = fails + check("u-c1", (counts[1] if? 0) == 2053)
    fails = fails + check("u-c2", (counts[2] if? 0) == 1970)
    fails = fails + check("u-c3", (counts[3] if? 0) == 1903)
    fails = fails + check("u-c4", (counts[4] if? 0) == 1922)
    fails = fails + check("u-c5", (counts[5] if? 0) == 2101)
    fails = fails + check("u-c6", (counts[6] if? 0) == 1997)
    fails = fails + check("u-c7", (counts[7] if? 0) == 2072)
    fails = fails + check("u-c8", (counts[8] if? 0) == 1922)
    fails = fails + check("u-c9", (counts[9] if? 0) == 2031)
    if (fails == 0) { Console.println("sec-uniform-ok") }

    // ---- ⑤ 浮点网格：位消费向量 + [0,1) 与网格对齐 ----
    r = new Random(42 as u64)
    fails = fails + check("f32-vec", r.nextFloat() == (0.08386296033859253 as float))
    r = new Random(42 as u64)
    fails = fails + check("f64-vec", r.nextDouble() == 0.08386297105988216)
    // 2000 发：恒在 [0,1)、不为 1，且 × 2^24 / 2^53 恒为整数（网格对齐）
    r = new Random(0xC0FFEE as u64)
    var fOk: bool = true
    var dOk: bool = true
    var j: i32 = 0
    while (j < 2000) {
        const f: float = r.nextFloat()
        const d: double = r.nextDouble()
        if ((f < (0.0 as float)) or (f >= (1.0 as float))) { fOk = false }
        if ((d < 0.0) or (d >= 1.0)) { dOk = false }
        if (floor(f * (16777216.0 as float)) != (f * (16777216.0 as float))) { fOk = false }
        if (floor(d * 9007199254740992.0) != (d * 9007199254740992.0)) { dOk = false }
        j = j + 1
    }
    fails = fails + check("f32-grid", fOk)
    fails = fails + check("f64-grid", dOk)
    if (fails == 0) { Console.println("sec-float-ok") }

    // ---- ⑥ nextBool：消耗一个核心输出、最高位判定 ----
    r = new Random(42 as u64)
    fails = fails + check("bool-0", not r.nextBool())
    fails = fails + check("bool-1", not r.nextBool())
    fails = fails + check("bool-2", r.nextBool())
    if (fails == 0) { Console.println("sec-bool-ok") }

    // ---- ⑦ fillBytes：小端、尾块丢弃、零长度、越界 ----
    r = new Random(42 as u64)
    var buf = spanOf\<u8>(8)
    r.fillBytes(buf)
    fails = fails + check("fb0", (buf[0] if? 0UB) == 22UB)
    fails = fails + check("fb1", (buf[1] if? 0UB) == 199UB)
    fails = fails + check("fb2", (buf[2] if? 0UB) == 46UB)
    fails = fails + check("fb3", (buf[3] if? 0UB) == 12UB)
    fails = fails + check("fb4", (buf[4] if? 0UB) == 46UB)
    fails = fails + check("fb5", (buf[5] if? 0UB) == 11UB)
    fails = fails + check("fb6", (buf[6] if? 0UB) == 120UB)
    fails = fails + check("fb7", (buf[7] if? 0UB) == 21UB)
    // 9 字节：尾块只取次核（core[1]）低字节 0x7E，core[1] 其余字节丢弃
    r = new Random(42 as u64)
    var buf9 = spanOf\<u8>(9)
    r.fillBytes(buf9, 0, 9)
    fails = fails + check("fb8", (buf9[8] if? 0UB) == 126UB)
    // 尾块丢弃后状态推进：下一发 = core[2]
    fails = fails + check("fb-tail-next", r.nextU64() == 0xAE17533239E499A1UL)
    // 部分区间：offset=2 count=5 → 写入 core[0] LE 的第 2..6 字节
    r = new Random(42 as u64)
    var buf8 = spanOf\<u8>(8)
    buf8[0] = 99UB
    buf8[1] = 99UB
    r.fillBytes(buf8, 2, 5)
    fails = fails + check("fb-part0", (buf8[0] if? 0UB) == 99UB)
    fails = fails + check("fb-part1", (buf8[1] if? 0UB) == 99UB)
    fails = fails + check("fb-part2", (buf8[2] if? 0UB) == 22UB)
    fails = fails + check("fb-part3", (buf8[3] if? 0UB) == 199UB)
    fails = fails + check("fb-part4", (buf8[4] if? 0UB) == 46UB)
    fails = fails + check("fb-part5", (buf8[5] if? 0UB) == 12UB)
    fails = fails + check("fb-part6", (buf8[6] if? 0UB) == 46UB)
    fails = fails + check("fb-part7", (buf8[7] if? 0UB) == 0UB)
    // 零长度不消耗状态
    r = new Random(42 as u64)
    r.fillBytes(buf, 0, 0)
    fails = fails + check("fb-zero", r.nextU64() == 0x15780B2E0C2EC716UL)
    // 越界抛错且缓冲区与生成器状态均不改
    r = new Random(42 as u64)
    var buf4 = spanOf\<u8>(4)
    buf4[0] = 77UB
    var fbThrew: bool = false
    try {
        r.fillBytes(buf4, 2, 3)
    } catch (e: core.OutOfBoundException) {
        fbThrew = true
    }
    fails = fails + check("fb-oob-throws", fbThrew)
    fails = fails + check("fb-oob-untouched", (buf4[0] if? 0UB) == 77UB)
    fails = fails + check("fb-oob-no-consume", r.nextU64() == 0x15780B2E0C2EC716UL)
    if (fails == 0) { Console.println("sec-fill-ok") }

    // ---- ⑧ 可复现性：同一调用序列两个全新实例逐位一致 ----
    var a = new Random(20260920 as u64)
    var b = new Random(20260920 as u64)
    var replayOk: bool = true
    var k: i32 = 0
    while (k < 64) {
        if (a.nextU64() != b.nextU64()) { replayOk = false }
        k = k + 1
    }
    fails = fails + check("replay-u64", replayOk)
    a = new Random(20260920 as u64)
    b = new Random(20260920 as u64)
    var mixOk: bool = true
    k = 0
    while (k < 32) {
        if (a.nextI32(-1000, 1000) != b.nextI32(-1000, 1000)) { mixOk = false }
        if (a.nextBool() != b.nextBool()) { mixOk = false }
        if (a.nextDouble() != b.nextDouble()) { mixOk = false }
        k = k + 1
    }
    fails = fails + check("replay-mix", mixOk)
    // fillBytes 分块在两个实例间一致（同序列）
    a = new Random(20260920 as u64)
    b = new Random(20260920 as u64)
    var ab = spanOf\<u8>(13)
    var bb = spanOf\<u8>(13)
    a.fillBytes(ab)
    b.fillBytes(bb)
    var fillSame: bool = true
    k = 0
    while (k < 13) {
        if ((ab[k] if? 0UB) != (bb[k] if? 0UB)) { fillSame = false }
        k = k + 1
    }
    fails = fails + check("replay-fill", fillSame)
    // 无参构造：两个实例（极大概率）产生不同序列（系统随机源种子；
    // 失败路径无法常规测试——需系统随机源不可用环境）
    var n1 = new Random()
    var n2 = new Random()
    fails = fails + check("sys-seed-differs", n1.nextU64() != n2.nextU64())
    if (fails == 0) { Console.println("sec-replay-ok") }

    if (fails == 0) {
        Console.println("math-random-ok")
        return 0
    }
    return 1
}
