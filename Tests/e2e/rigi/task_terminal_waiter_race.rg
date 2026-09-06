// expect-output: task-terminal-race-ok
// 多 Worker 上短任务可在调用方登记 await 前后完成；成功和失败都重复观察。
import core.coroutine.*
import core.collections.*
import core.io.Console

async func finish(value: i32, fail: bool): i32 {
    if (fail) { throw new core.RuntimeException("预期任务失败") }
    return value
}

async func exercise(): i32 {
    var i: i32 = 0
    while (i < 64) {
        const good = finish(i, false)
        if ((await good) != i) { throw new core.RuntimeException("成功结果丢失") }
        if ((await good) != i) { throw new core.RuntimeException("重复观察结果改变") }
        const bad = finish(i, true)
        var caught: i32 = 0
        try { await bad } catch (e: core.RuntimeException) { caught = caught + 1 }
        try { await bad } catch (e: core.RuntimeException) { caught = caught + 1 }
        if (caught != 2) { throw new core.RuntimeException("失败观察丢失") }
        i = i + 1
    }
    return i
}

pub func main(): i32 {
    const tasks = arrayOf\<Task\<i32>>(16)
    var i: i32 = 0
    while (i < tasks.length) {
        const task = new Task\<i32>(func{async (): i32 -> await exercise()})
        task.run(new ComputeExecutor())
        tasks[i] = task
        i = i + 1
    }
    i = 0
    while (i < tasks.length) {
        if ((await (tasks[i] as Task\<i32>)) != 64) { throw new core.RuntimeException("任务未完整结束") }
        i = i + 1
    }
    Console.println("task-terminal-race-ok")
    return 0
}
