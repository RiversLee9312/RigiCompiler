// Any/Object 视图各自持有引用；释放临时视图后原对象仍可用。
// expect-output: one
// expect-output: one
// expect-output: created
// expect-output: added
// expect-output: one
import core.io.Console
import core.serialization.Serializable
@Serializable
pub shared class Blob {
    pub var id: i32
    pub var text: String
    pub init(_ -> id, _ -> text)
}
func inspect(value: Any) {
    const view = value as Object
    Console.println((view as Blob).text)
}
pub func main(): i32 {
    const original = new Blob(1, "one")
    inspect(original)
    Console.println(original.text)
    const list = core.AtomicList.fromList\<Blob>(new core.collections.List\<Blob>())
    Console.println("created")
    await list.add(original)
    Console.println("added")
    Console.println(((await list.getAtIndex(0 as i64)) as Blob).text)
    // 默认 Object 父类型必须留在运行期祖先链；连续扩容的泛型体内
    // 仍逐元素核验身份，不能靠跳过 Nullable<T> 的检查通过。
    const objects = new core.collections.List\<Object>()
    var index = 0
    while (index < 20) {
        const dynamic: Any = new Blob(index, "root")
        if (not (dynamic is Object)) { throw new core.RuntimeException("缺少默认 Object 父类型") }
        objects.add(dynamic as Object)
        index = index + 1
    }
    index = 0
    while (index < 20) {
        const element = objects.getAtIndex(index as i64) as Blob
        if (element.id != index) { throw new core.RuntimeException("Object 容器扩容损坏") }
        index = index + 1
    }
    return 0
}
