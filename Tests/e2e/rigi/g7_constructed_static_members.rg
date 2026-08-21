// bug g7（正例）：构造类型上的静态成员——静态方法（含方法自有泛型实参
// 组合）经宿主 typeid 显式传递解析 T；静态字段读写走 companion cell。
// expect-output: 7:8:hi
// expect-exit: 8
import core.io.Console
pub class Box\<T> {
    pub var v: T
    pub init(_ -> v)
    pub static var zero: T
    pub static func wrap(x: T): Box\<T> { return new Box\<T>(x) }
    pub static func pick\<U>(x: T, u: U): U { return u }
    pub static func reset(x: T) { zero = x }
}
pub func main(): i32 {
    Box\<i32>.zero = 41
    Box\<i32>.reset(7)
    const z = Box\<i32>.zero
    const b = Box\<i32>.wrap(8)
    const p = Box\<i32>.pick\<String>(5, "hi")
    Console.println("${z}:${b.v}:${p}")
    return b.v
}
