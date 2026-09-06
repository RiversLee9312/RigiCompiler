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
    return 0
}
