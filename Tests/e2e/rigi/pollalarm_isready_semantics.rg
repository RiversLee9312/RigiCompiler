// ============================================================================
// pollalarm_isready_semantics.rg —— Phase 2.6（RUNTIME §19.2 语义纠偏）回归语料
// VM 与 native 双宿主对拍（Case 序列化同语料）。验证恢复式探测语义：
//   ① SlowPoll：探测中途真实挂起（AtomicStruct.load → await mutex.acquire），
//      返回 false 退回等待，第 3 次探测就绪续行，总探测次数确定 == 3；
//   ② YieldOnce：isReady 内裸 yield 挂起一次——挂起即继续等待，唤醒后续跑
//      滞留帧完成探测（isReady 只执行一次；修复前重复压帧执行两次）；
//   ③ BoomPoll：第 1 次探测 false 退回；第 2 次探测挂起恢复后抛出——异常
//      落在 yield 点，词法 try/catch 捕获后轮询状态清理、catch 续行。
// 形态约束（历史说明）：isReady 内泛型闭合 new 与 AtomicStruct.store
//（mutate 链）曾是 76e304c 既有 native 缺陷（构造收集漏 poll_probe 边，
// 与探测语义无关）——已修复，回归覆盖见 pollalarm_generic_new.rg；本文件
// 的探测计数仍一律用普通字段，保持对「探测语义本身」的聚焦。
// 验证：双宿主 stdout 逐行一致 + 退出码 0；探测计数确定性（3/1/2）。
// expect-output: slow-probes=3
// expect-output: yield-probes=1
// expect-output: boom-r=2 boom-probes=2
// ============================================================================
import core.coroutine.*
import core.io.Console

pub shared class SlowPoll : PollingAlarm {
    pub var probes: i64 = 0L
    priv const probeCell: core.AtomicStruct\<i64>

    pub init() {
        probeCell = new core.AtomicStruct\<i64>(0L)
    }

    pub override func isReady(): bool {
        probes = probes + 1L
        // probeCell.load 内部 await mutex.acquire：探测中途真实挂起点
        const c = probeCell.load()
        return (probes + c) >= 3L
    }
}

pub shared class YieldOnce : PollingAlarm {
    pub var probes: i64 = 0L
    pub var first: bool = true

    pub override func isReady(): bool {
        probes = probes + 1L
        if (first) {
            first = false
            yield
        }
        return true
    }
}

pub shared class BoomPoll : PollingAlarm {
    pub var probes: i64 = 0L
    priv const probeCell: core.AtomicStruct\<i64>

    pub init() {
        probeCell = new core.AtomicStruct\<i64>(0L)
    }

    pub override func isReady(): bool {
        probes = probes + 1L
        const c = probeCell.load()
        if (probes >= 2L) {
            throw new core.RuntimeException("boom-probe")
        }
        return false
    }
}

pub func main(): i32 {
    const s = new SlowPoll()
    yield s
    Console.println("slow-probes=${s.probes}")
    const y = new YieldOnce()
    yield y
    Console.println("yield-probes=${y.probes}")
    const b = new BoomPoll()
    var r: i32 = 0
    try {
        yield b
        r = 1
    } catch (e: core.RuntimeException) {
        r = 2
    }
    Console.println("boom-r=${r} boom-probes=${b.probes}")
    return 0
}
