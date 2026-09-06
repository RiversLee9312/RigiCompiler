// expect-error: cannot access static member 'fromArray' via constructed type
import core.serialization.Serializable
@Serializable
shared class Item { pub var n:i32 = 0 }
pub func main():i32 {
    const source = core.collections.arrayOf\<Item>(0)
    AtomicArray\<Item>.fromArray(source)
    return 0
}
