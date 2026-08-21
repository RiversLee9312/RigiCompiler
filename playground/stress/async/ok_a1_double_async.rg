// 对照正例（非 bug）：双 async 合法 override——shared 接口 async 成员由
// shared 类 async override 实现，经具体类调用结果为 Task<i32>
// （A2 起含 async 成员的接口必须 shared）
import core.io.Console
import core.coroutine.*

pub shared interface Worker {
    async func run(x: i32): i32
}

pub shared class W implements Worker {
    pub init()
    pub async override func run(x: i32): i32 {
        yield sleep(1)
        return (x + 1)
    }
}

pub func main(): i32 {
    const w = new W()
    const t = w.run(10)
    const r = await t
    Console.println(r.toString())
    return 0
}
