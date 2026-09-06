// 非泛型子类必须保留闭合泛型祖先身份，同时维持字段与 typeid 布局。
// expect-output: true
// expect-output: false
// expect-output: 27
// expect-output: 9
// expect-output: rejected
open class Base\<T> {
    pub var value:T
    pub init(_ -> value)
    pub func get():T { return value }
}
class Derived:Base\<i32> {
    pub var marker:i32 = 9
    pub init(value:i32) { super(value) }
}
pub func main():i32 {
    const d = new Derived(27)
    const object:Any = d
    core.io.Console.println((object is Base\<i32>).toString())
    core.io.Console.println((object is Base\<String>).toString())
    const base = object as Base\<i32>
    core.io.Console.println(base.get().toString())
    core.io.Console.println(d.marker.toString())
    try {
        const wrong = object as Base\<String>
        core.io.Console.println(wrong.toString())
    }
    catch(e:core.CastException) { core.io.Console.println("rejected") }
    return 0
}
