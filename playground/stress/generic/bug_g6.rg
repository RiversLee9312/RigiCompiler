// bug g6：无约束 T 的 T() —— Any 无可访问零参 init，编译期应报「没有该方法」（§3.7）
import core.io.Console

pub func make\<T>(): T {
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
    const x = make\<i32>()
    Console.println(x.toString())
    const f = makeFoo\<Foo>()
    Console.println(f.tag)
    return 0
}
