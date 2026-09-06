// expect-error: must be shared-safe
import core.serialization.Serializable
@Serializable
class Key { pub var n:i32 = 0 }
@Serializable
shared class Value { pub var n:i32 = 0 }
func bad(value:AtomicMap\<Key,Value>) {}
