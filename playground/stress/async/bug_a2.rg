// bug A2 复现（§3.1.1/§4.5）：接口可声明 async 成员、shared class 可实现它，
// 但经接口类型调用被闸门 1 拒（'Worker' 不在共享安全白名单）；且 'shared'
// 不能标 interface，async 接口形同虚设。
// 修复后：含 async 成员的接口必须标 shared（声明点 fail-fast），本文件
// 保持非 shared 接口，应报「an interface declaring 'async' members must
// be 'shared'」；正例形态见 ok_a2_shared_iface.rg
import core.io.Console
import core.coroutine.*

pub interface Worker {
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
