// CPU 执行段屏障：达到目标数量前没有 yield/await，因此只能由真实多 Worker 达成。
import core.coroutine.*
import core.collections.*
import core.io.Console
@NativeLibrary("rigi_rt")
@NativeSymbol("sync_mutex_create")
priv native func makeGate(): i64
@NativeLibrary("rigi_rt")
@NativeSymbol("sync_mutex_acquire")
priv native func enter(gate: i64)
@NativeLibrary("rigi_rt")
@NativeSymbol("sync_mutex_release")
priv native func leave(gate: i64)
@NativeLibrary("rigi_rt")
@NativeSymbol("tls_current_context")
priv native func worker(): i64
@NativeLibrary("rigi_rt")
@NativeSymbol("worker_parallelism")
priv native func parallelism(): i32
pub shared class Probe {
    priv const gate: i64 = makeGate()
    priv var arrived: i32 = 0
    priv const target: i32
    pub init(_ -> target)
    pub async func compute(): i64 {
        const identity = worker()
        enter(gate)
        arrived = arrived + 1
        leave(gate)
        while (true) {
            enter(gate)
            const ready = arrived == target
            leave(gate)
            if (ready) { break }
        }
        var sum: i64 = (identity & (2147483647 as i64)) + (1 as i64)
        var i: i32 = 0
        while (i < 100000) {
            sum = sum ^ (sum << (13 as i64))
            sum = sum ^ (sum >>> (17 as i64))
            sum = sum ^ (sum << (5 as i64))
            i = i + 1
        }
        if (sum == (0 as i64)) { throw new core.RuntimeException("CPU校验失败") }
        Console.println("checksum=" + sum.toString())
        return identity
    }
}
pub func main(): i32 {
    var count = parallelism()

    const probe = new Probe(count)
    const tasks = arrayOf\<Task\<i64>>(count)
    var i: i32 = 0
    while (i < count) {
        const task = new Task\<i64>(func{async (): i64 -> await probe.compute()})
        task.run(new ComputeExecutor())
        tasks[i] = task
        i = i + 1
    }
    const ids = arrayOf\<i64>(count)
    i = 0
    while (i < count) {
        ids[i] = await (tasks[i] as Task\<i64>)
        var j: i32 = 0
        while (j < i) {
            if ((ids[i] as i64) == (ids[j] as i64)) { throw new core.RuntimeException("执行段未并行") }
            j = j + 1
        }
        i = i + 1
    }
    Console.println("cpu-workers=" + count.toString())
    return 0
}
