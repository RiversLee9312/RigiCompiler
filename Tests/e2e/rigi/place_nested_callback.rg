// 嵌套开放构造类型必须按已知 Place 宿主派发 equals。
// expect-output: true
// expect-output: false
// expect-output: true
// expect-output: true
// expect-output: false
// expect-output: true
// expect-output: false
// expect-output: true
class CallbackHolder\<T> {
    pub const value: T
    pub init(_ -> value)
}
func sameHolder\<T>(x: CallbackHolder\<AsyncAction\<T>>, y: CallbackHolder\<AsyncAction\<T>>): bool {
    seq using(const a = placeOf x) using(const b = placeOf y) {
        return a == b
    }
}
func sameCallback\<T>(x: AsyncAction\<T>, y: AsyncAction\<T>): bool {
    seq using(const a = placeOf x) using(const b = placeOf y) {
        return a == b
    }
}
func differentCallback\<T>(x: AsyncAction\<T>, y: AsyncAction\<T>): bool {
    seq using(const a = placeOf x) using(const b = placeOf y) {
        return a != b
    }
}
func samePlace\<P extends Place\<i32>>(x:P, y:P):bool { return x == y }
open class Ordinary {
    pub operator equals(other:Ordinary):bool { return true }
}
func sameOrdinary\<P extends Ordinary>(x:P, y:P):bool { return x == y }
pub func main(): i32 {
    const a: AsyncAction\<i32> = func{async (x:i32) -> {}}
    const b: AsyncAction\<i32> = func{async (x:i32) -> {}}
    core.io.Console.println(sameCallback\<i32>(a, a).toString())
    core.io.Console.println(sameCallback\<i32>(a, b).toString())
    core.io.Console.println(differentCallback\<i32>(a, b).toString())
    const h = new CallbackHolder\<AsyncAction\<i32>>(a)
    const k = new CallbackHolder\<AsyncAction\<i32>>(a)
    core.io.Console.println(sameHolder\<i32>(h, h).toString())
    core.io.Console.println(sameHolder\<i32>(h, k).toString())
    var x:i32 = 1
    var y:i32 = 1
    seq using(const p = placeOf x) using(const q = placeOf x) using(const r = placeOf y) {
        core.io.Console.println(samePlace(p, q).toString())
        core.io.Console.println(samePlace(p, r).toString())
    }
    core.io.Console.println(sameOrdinary(new Ordinary(), new Ordinary()).toString())
    return 0
}
