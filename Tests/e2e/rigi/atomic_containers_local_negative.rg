// expect-error: must be shared-safe
import core.serialization.Serializable
@Serializable
class LocalItem { pub var n:i32 = 0 }
func bad(value:AtomicList\<LocalItem>) {}
