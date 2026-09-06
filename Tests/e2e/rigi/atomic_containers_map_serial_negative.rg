// expect-error: does not satisfy the 'With Serializable'
import core.serialization.Serializable
shared class Missing {}
@Serializable
shared class Item { pub var n:i32 = 0 }
func badKey(value:AtomicMap\<Missing,Item>) {}
func badValue(value:AtomicMap\<Item,Missing>) {}
