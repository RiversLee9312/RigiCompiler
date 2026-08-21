// c7b（审计 F3）：委托/callable 字段作链式中间段直接调用——binder 曾把
// `hd.f()` 误绑成对宿主 hd 的 $$call（cast Holder → core::Func 运行期
// InvalidCast）；修复后与「先取局部再调」同语义（invoke.indirect）
// expect-output: via_local=7
// expect-output: via_field=7
// expect-output: via_chain=7
import core.io.Console
pub class Box {
    pub var v: i32
    pub init(x: i32) { v = x }
    pub func n(): i32 { return v }
}
pub class Holder {
    pub var f: Func\<Box>
    pub init(g: Func\<Box>) { f = g }
}
pub func main(): i32 {
    const hd = new Holder(func{(): Box -> new Box(7)})
    const g = hd.f
    Console.println("via_local=${g().n()}")
    Console.println("via_field=${hd.f().n()}")
    const direct = hd.f()
    Console.println("via_chain=${direct.n()}")
    return 0
}
