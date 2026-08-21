// bug A1 复现（§9.2.1）：接口 sync 签名被 async override 实现「满足」
// 的类型洞——修复前本文件通过编译，w.run(10) 静态类型 i32 而运行期
// 实得 Task<i32>；修复后 OverrideChecker 按 async 不一致专项诊断拦截
import core.io.Console
import core.coroutine.*

pub interface Worker {
    func run(x: i32): i32
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
    const r = w.run(10)
    Console.println(r.toString())
    return 0
}
