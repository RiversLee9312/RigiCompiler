// expect-output: failure-shared-waiters-ok
import core.coroutine.*
import core.collections.*
import core.io.Console
pub class Detail {
    pub var child: Detail?
    pub var text: String = "detail"
}
pub class SharedFailure : core.RuntimeException {
    pub var detail: Detail
    pub init() {
        message = "shared-failure"
        detail = new Detail()
        detail.child = new Detail()
    }
}
async func fail(): i32 { throw new SharedFailure() }
async func observe(task: Task\<i32>): i32 {
    var count: i32 = 0
    var i: i32 = 0
    while (i < 256) {
        try { await task }
        catch (e: SharedFailure) {
            const detail = e.detail
            const nested = detail.child as Detail
            if ((e.getMessage() == "shared-failure") and (nested.text == "detail")) { count = count + 1 }
        }
        i = i + 1
    }
    return count
}
pub func main(): i32 {
    const failed = fail()
    try { await failed } catch (e: core.RuntimeException) {}
    const tasks = arrayOf\<Task\<i32>>(16)
    var i: i32 = 0
    while (i < tasks.length) {
        const task = new Task\<i32>(func{async (): i32 -> await observe(failed)})
        task.run(new ComputeExecutor())
        tasks[i] = task
        i = i + 1
    }
    i = 0
    while (i < tasks.length) {
        if ((await (tasks[i] as Task\<i32>)) != 256) {
            throw new core.RuntimeException("共享失败观察丢失")
        }
        i = i + 1
    }
    Console.println("failure-shared-waiters-ok")
    return 0
}
