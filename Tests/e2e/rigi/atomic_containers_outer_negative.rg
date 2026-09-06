// expect-error: must be shared-safe
import core.serialization.Serializable
func bad\<T with Serializable>(value:AtomicArray\<T>) {}
