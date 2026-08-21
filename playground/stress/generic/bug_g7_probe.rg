import core.io.Console

pub class Box\<T> {
    pub var v: T
    pub init(_ -> v)
    pub static var zero: T
    pub static func wrap(x: T): Box\<T> { return new Box\<T>(x) }
    pub static func pick\<U>(x: T, u: U): U { return u }
}

pub func main(): i32 {
    Box\<i32>.zero = 41
    const z = Box\<i32>.zero
    Console.println("zero=" + z.toString())
    const b = Box\<i32>.wrap(8)
    Console.println("wrap=" + b.v.toString())
    const p = Box\<i32>.pick\<String>(5, "hi")
    Console.println("pick=" + p)
    return 0
}
