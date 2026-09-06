// expect-error: must be shared-safe
import core.serialization.Serializable
@Serializable
class Item { pub var n:i32 = 0 }
class Holder\<T> {}
func bad(value:Holder\<AtomicList\<Item>>) {}
