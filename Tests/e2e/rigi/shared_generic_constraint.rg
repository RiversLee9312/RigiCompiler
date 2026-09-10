// expect-output: shared-generic-ok
import core.io.Console
pub shared class SomeType\<shared T> {
    pub const value: T
    pub init(_ -> value)
}
pub shared class Item { pub const value: i32 = 42 }
func forward\<shared T>(value: T): SomeType\<T> { return new SomeType\<T>(value) }
pub func main(): i32 {
    const first = forward(42)
    const second = forward(new Item())
    const third = new SomeType\<Item?>(null)
    if ((first.value != 42) or (second.value.value != 42)) { return 1 }
    if (third.value != null) { return 2 }
    Console.println("shared-generic-ok")
    return 0
}
