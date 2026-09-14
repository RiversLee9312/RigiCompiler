// expect-output: failure-graph-mt-readers-ok
// GC Phase 3c 压力对拍：失败 Task 异常图（local 嵌套 + 环 + 数组共享子图）
// 被多 Executor 的观察协程并发 await 反复读取。waiter 每次快路径重抛经
// rigi_failure_get 独立 acquire/release 异常引用——native 在失败发布点
// 已对整图翻 shared 会计（promotion 为 native 实现细节，VM 无会计位），
// 双宿主对拍语义必须一致；观察计数确定性断言 16 × 128 全数命中。
import core.coroutine.*
import core.collections.*
import core.io.Console

// local 载荷子图：嵌套 + 环 + 数组（walker visited 防环/数组分支压力面）
pub class Payload {
    pub var name: String = "p"
    pub var next: Payload?
    pub var kids: Array\<Payload>
    pub init() {
        kids = arrayOf\<Payload>(0)
    }
}

pub class GraphFailure : core.RuntimeException {
    pub var head: Payload
    pub init() {
        message = "graph-failure"
        head = new Payload()
        head.name = "head"
        const a = new Payload()
        const b = new Payload()
        head.next = a
        a.next = b
        b.next = head
        a.kids = arrayOf\<Payload>(2)
        a.kids[0] = new Payload()
        a.kids[1] = head
    }
}

async func fail(): i32 { throw new GraphFailure() }

// 单观察者：await 同一失败 Task 128 次（已失败快路径重抛，每次独立取
// 异常引用并深读图字段），全字段一致才计数
async func observe(task: Task\<i32>): i32 {
    var count: i32 = 0
    var i: i32 = 0
    while (i < 128) {
        try { await task }
        catch (e: GraphFailure) {
            const nested = e.head.next as Payload
            const tail = nested.next as Payload
            const back = tail.next as Payload
            const aliasHead = nested.kids[1] as Payload
            if ((((((e.getMessage() == "graph-failure") and (e.head.name == "head"))
                and (nested.name == "p")) and (back.name == "head"))
                and (aliasHead.name == "head")) and (nested.kids.length == 2)) {
                count = count + 1
            }
        }
        i = i + 1
    }
    return count
}

pub func main(): i32 {
    const failed = fail()
    try { await failed } catch (e: core.RuntimeException) {}
    const readers = arrayOf\<Task\<i32>>(16)
    var i: i32 = 0
    while (i < readers.length) {
        const task = new Task\<i32>(func{async (): i32 -> await observe(failed)})
        task.run(new ComputeExecutor())
        readers[i] = task
        i = i + 1
    }
    var total: i32 = 0
    i = 0
    while (i < readers.length) {
        total = total + (await (readers[i] as Task\<i32>))
        i = i + 1
    }
    if (total != 2048) {
        throw new core.RuntimeException("异常图并发观察丢失")
    }
    Console.println("failure-graph-mt-readers-ok")
    return 0
}
