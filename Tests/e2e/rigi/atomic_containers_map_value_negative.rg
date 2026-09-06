// expect-error: must be shared-safe
import core.serialization.Serializable
@Serializable
shared class Key { pub var n:i32 = 0 }
@Serializable
class Value { pub var n:i32 = 0 }
func bad(value:AtomicMap\<Key,Value>) {}
