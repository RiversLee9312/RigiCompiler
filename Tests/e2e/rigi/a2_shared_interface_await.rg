// bug A2（正例）：shared interface 声明 async 成员，经接口类型 await
// 调用（§3.1.1/§4.5）。修复前闸门 1 误拒：'Worker' 不在共享安全白名单。
// expect-output: 11
// expect-exit: 0
import core.io.Console
pub shared interface Worker {
    async func run(x: i32): i32
}
pub shared class W implements Worker {
    pub init()
    pub async override func run(x: i32): i32 { return (x + 1) }
}
pub func main(): i32 {
    const w: Worker = new W()
    const r = await w.run(10)
    Console.println("${r}")
    return 0
}
