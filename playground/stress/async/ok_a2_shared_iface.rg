// bug A2 修复正例（§3.1.1/§4.5）：pub shared interface 声明 async 成员，
// shared class 实现后经接口类型 await 调用——接口共享安全白名单覆盖
// shared interface（IsSharedSafe），闸门 1 放行；运行输出 r=11
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
    const w: Worker = new W()
    const r = await w.run(10)
    Console.println("r=" + r.toString())
    return 0
}
