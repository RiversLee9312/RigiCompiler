// g6 正例：标量界例外 + extends 界有可访问零参 init 放行（§3.7）
import core.io.Console

pub func makeScalar\<T extends i32>(): T {
    return T()
}

pub class Foo {
    pub var tag: String = "foo"
    pub init()
}

pub func makeFoo\<T extends Foo>(): T {
    return T()
}

pub func main(): i32 {
    const x = makeScalar\<i32>()
    Console.println(x.toString())
    const f = makeFoo\<Foo>()
    Console.println(f.tag)
    return 0
}
