// expect-output: failure-lifecycle-ok
import core.coroutine.*
import core.io.Console
pub shared class Holder {
    pub var task: Task\<i32>?
    pub var value: i32 = 1
}
func cycle() {
    const holder = new Holder()
    const task = new Task\<i32>(func{async (): i32 -> {
        if (holder.value == 1) { throw new core.RuntimeException("cycle") }
        return@_ 0
    }})
    holder.task = task
    try { await task } catch (e: core.RuntimeException) {}
    try { await task } catch (e: core.RuntimeException) {}
}
pub func main(): i32 {
    var i: i32 = 0
    while (i < 200) { cycle()
        i = i + 1 }
    Console.println("failure-lifecycle-ok")
    return 0
}
