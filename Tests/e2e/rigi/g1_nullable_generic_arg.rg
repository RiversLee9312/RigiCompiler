// bug g1（正例）：泛型实参内嵌可空 Holder\<i32?> 必须同解为
// Holder<Nullable<i32>>。修复前类型注解路径把 ? 误挂到外层 Holder，
// 报 Expected '=' or line break。
// expect-output: -1
// expect-exit: 0
import core.io.Console
pub class Holder\<T> {
    pub var v: T
    pub init(_ -> v)
}
pub func main(): i32 {
    var h: Holder\<i32?> = new Holder\<i32?>(null)
    Console.println("${(h.v if? -1)}")
    return 0
}
