// 泛型class反序列化必须恢复闭合实际类型，而不是只写隐藏typeid字段。
// expect-output: 3
import core.serialization.Serializable
import core.io.Console
@Serializable
pub shared class Item {
    pub var n: i32
    pub init(_ -> n)
}
@Serializable
pub shared class Envelope\<T with Serializable> {
    pub var item: T?
    pub init(_ -> item)
}
pub func main(): i32 {
    const copy = core.serialization.deepCopy\<Envelope\<Item>>(new Envelope\<Item>(new Item(3)))
    Console.println((copy.item as Item).n.toString())
    return 0
}
