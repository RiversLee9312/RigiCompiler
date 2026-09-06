// expect-error: must be shared-safe
import core.serialization.Serializable
@Serializable
class Item { pub var n:i32 = 0 }
pub func main():i32 {
    const source = core.collections.arrayOf\<Item>(0)
    AtomicArray.fromArray\<Item>(source)
    return 0
}
