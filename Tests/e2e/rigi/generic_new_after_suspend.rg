// 构造/受检cast隐式使用的类级typeid必须跨挂起保存，并选择闭合对象头。
// expect-output: 7
// expect-output: true
import core.io.Console
import core.coroutine.sleep
pub shared class Item { pub init() }
pub shared class Holder\<T> {
    pub const value: T
    pub init(_ -> value)
    pub func copy(): Holder\<T> {
        yield sleep(1)
        return new Holder\<T>(value)
    }
}
pub func main(): i32 {
    const a = (new Holder\<i32>(7)).copy()
    const b = (new Holder\<Item>(new Item())).copy()
    Console.println(a.value.toString())
    Console.println((b.value is Item).toString())
    return 0
}
