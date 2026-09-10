// expect-output: cast-guards-ok
import core.io.Console
pub class Box\<T> { pub var value: T
    pub init(_ -> value) }
func rejectFakeType(value: Any) {
    if ((value as? Type\<Any>) != null) {
        throw new core.RuntimeException("普通值被当成类型指针")
    }
}
pub func main(): i32 {
    rejectFakeType(42)
    rejectFakeType(1.5)
    rejectFakeType("not-a-type")
    rejectFakeType(new Box\<i32>(7))
    const exact = typeOf(i32)
    const erased = exact as Any
    const broad = erased as Type\<Any>
    if (not (7 is broad)) { return 1 }
    if ((erased as? Type\<String>) != null) { return 2 }
    if (("not-an-int" as? i32) != null) { return 3 }
    const box = new Box\<i32>(7)
    if ((box as? Box\<String>) != null) { return 4 }
    var rejected = false
    try { const wrong = 7 as String? }
    catch (e: core.CastException) { rejected = true }
    if (not rejected) { return 5 }
    Console.println("cast-guards-ok")
    return 0
}
