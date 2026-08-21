// bug O3（正例）：like 目标字段为接口类型——转发体调接口方法，
// 运行期对字段值虚派发；显式 override 优先于转发另见 BinderTests。
// expect-output: 4
// expect-output: 5
// expect-exit: 4
import core.io.Console
pub interface Work { func run(x: i32): i32
 }
pub class Impl implements Work {
    pub override func run(x: i32): i32 { return (x + 1) }
}
pub class ViaIface implements Work like sink {
    pub var sink: Work = new Impl()
}
pub func main(): i32 {
    const b = new ViaIface()
    Console.println(b.run(3).toString())
    const w: Work = b
    Console.println(w.run(4).toString())
    return b.run(3)
}
