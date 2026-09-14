// expect-output: gennew ready
// expect-output: mutate ready
// ============================================================================
// pollalarm_generic_new.rg —— 遗留1回归：isReady 内泛型闭合 new
// 根因：ConstructedCallCollector 缺 YieldInstruction 边——polling 探测回调
//（$mw.poll_probe 虚派发 isReady）BIL 级不可见，仅在 isReady 内构造/实例化
// 的泛型进不了构造收集，发射期 sheet select miss 烧 null，运行期
// rigi_alloc(NULL) 硬错。修复后双宿主 EXIT=0。
// 覆盖：GenNewPoll=untainted 廉价路径（isReady 内 new 泛型 + load）；
//       MutatePoll=tainted 恢复路径（isReady 内经 AtomicStruct.mutate 挂起，
//       第 2 次探测就绪）。
// ============================================================================
import core.io.Console
import core.coroutine.*

pub shared class GenNewPoll : PollingAlarm {
    pub override func isReady(): bool {
        const c = new core.AtomicStruct\<i64>(0L)
        return (c.load() == 0L)
    }
}

pub shared class MutatePoll : PollingAlarm {
    priv const gate: core.AtomicStruct\<i64>

    pub init() {
        gate = new core.AtomicStruct\<i64>(0L)
    }

    pub override func isReady(): bool {
        gate.mutate{ (v: i64): i64 -> (v + 1L) }
        return (gate.load() >= 2L)
    }
}

async func run() {
    yield new GenNewPoll()
    Console.println("gennew ready")
    const p = new MutatePoll()
    yield p
    Console.println("mutate ready")
}

pub func main(): i32 {
    run()
    return 0
}
