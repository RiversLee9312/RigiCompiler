// expect-output: compute-resume-ok
// 高频 yield 与退避 PollingAlarm 共同覆盖池内迁移和同一协程单执行。
import core.coroutine.*
import core.collections.*
import core.io.Console
pub shared class Flip : PollingAlarm {
    priv var probes: i32 = 0
    pub override func isReady(): bool {
        probes = probes + 1
        return probes >= 4
    }
}
async func exercise(): i32 {
    var i: i32 = 0
    var sum: i32 = 0
    while (i < 400) {
        sum = sum + i
        yield
        if ((i & 31) == 0) { yield new Flip() }
        i = i + 1
    }
    yield sleep(1)
    return sum
}
pub func main(): i32 {
    const tasks = arrayOf\<Task\<i32>>(16)
    var i: i32 = 0
    while (i < 16) {
        const task = new Task\<i32>(func{async (): i32 -> await exercise()})
        task.run(new ComputeExecutor())
        tasks[i] = task
        i = i + 1
    }
    i = 0
    while (i < 16) {
        if ((await (tasks[i] as Task\<i32>)) != 79800) { throw new core.RuntimeException("重复恢复") }
        i = i + 1
    }
    Console.println("compute-resume-ok")
    return 0
}
