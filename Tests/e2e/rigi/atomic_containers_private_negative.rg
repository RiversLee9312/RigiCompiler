// expect-error: is inaccessible
import core.serialization.Serializable
@Serializable
shared class Item { pub var n:i32 = 0 }
pub func main():i32 {
    const source = new core.collections.List\<Item>()
    const result = AtomicList.fromList(source)
    result.atomic
    return 0
}
