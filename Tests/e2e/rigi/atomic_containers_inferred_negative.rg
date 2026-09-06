// expect-error: must be shared-safe
import core.serialization.Serializable
@Serializable
class Item { pub var n:i32 = 0 }
pub func main():i32 {
    const source = new core.collections.List\<Item>()
    const result = AtomicList.fromList(source)
    return 0
}
