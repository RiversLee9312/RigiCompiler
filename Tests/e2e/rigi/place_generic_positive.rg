// expect-output: true
// expect-output: true
// expect-output: true
class Item { }
func same\<T>(x: T, y: T): bool {
    seq using(const a = placeOf x) using(const b = placeOf y) {
        return a == b
    }
}
func stable\<T>(x: T): bool {
    seq using(const a = placeOf x) using(const b = placeOf x) {
        return a == b
    }
}
pub func main(): i32 {
    const x = new Item()
    core.io.Console.println(same(x, x).toString())
    core.io.Console.println(stable(1).toString())
    const a: Any = x
    const b: Any = x
    seq using(const p = placeOf a) using(const q = placeOf b) {
        core.io.Console.println((p == q).toString())
    }
    return 0
}
