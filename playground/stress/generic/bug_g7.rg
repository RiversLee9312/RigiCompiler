import core.io.Console

pub class Box\<T> {
    pub var v: T
    pub init(_ -> v)
    pub static func wrap(x: T): Box\<T> { return new Box\<T>(x) }
}

pub func main(): i32 {
    const b = Box\<i32>.wrap(8)
    Console.println("wrap=" + b.v.toString())
    return 0
}
