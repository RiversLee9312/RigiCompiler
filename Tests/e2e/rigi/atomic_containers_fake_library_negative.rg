// expect-error: must be shared-safe
namespace core
import core.serialization.Serializable
shared class AtomicMapSnapshot\<T with Serializable> {
    pub func bad(value:AtomicArray\<T>) {}
}
