// W2 正例：方法级泛型工厂替代构造类型静态成员；裸名访问不碰 T 的静态成员合法。
// expect-output: 7:8:0:hi
// expect-exit: 8
import core.io.Console
pub class Box\<T> {
    pub var v: T
    pub init(_ -> v)
    pub static func count(): i32 { return 7 }
}
pub class BoxFactory {
    pub static func wrap\<T>(x: T): Box\<T> { return new Box\<T>(x) }
    pub static func zeroOf\<T extends i32>(): Box\<T> { return new Box\<T>(T()) }
    pub static func pick\<U>(u: U): U { return u }
}
pub func main(): i32 {
    const b = BoxFactory.wrap\<i32>(8)
    const z = BoxFactory.zeroOf\<i32>()
    const p = BoxFactory.pick\<String>("hi")
    Console.println("${Box.count()}:${b.v}:${z.v}:${p}")
    return b.v
}
