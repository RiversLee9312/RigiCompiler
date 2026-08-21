// c7b 复现 v2：委托字段直调后链式 .n()
import core.io.Console
pub class Box {
    pub var v: i32
    pub init(x: i32) { v = x }
    pub func n(): i32 { return v }
}
pub class Holder {
    pub var f: core.Func\<Box>
    pub init(g: core.Func\<Box>) { f = g }
}

pub func main(): i32 {
    const hd = new Holder(func{(): Box -> new Box(7)})
    const g = hd.f
    Console.println("via_local=${g().n()}")
    Console.println("via_field=${hd.f().n()}")
    return 0
}
