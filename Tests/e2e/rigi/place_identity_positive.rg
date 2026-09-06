// expect-output: true
// expect-output: false
// expect-output: true
// expect-output: 9
// expect-output: false
class Box { pub var value: i32 = 1 }
pub func main(): i32 {
    const box = new Box()
    const another = new Box()
    var n: i32 = 1
    seq using(const a = placeOf box) using(const b = placeOf box) using(const c = placeOf another) {
        core.io.Console.println((a == b).toString())
        core.io.Console.println((a == c).toString())
    }
    seq using(const a = placeOf n) using(const b = placeOf n) {
        n = 9
        core.io.Console.println((a == b).toString())
        core.io.Console.println(n.toString())
        a.dispose()
        core.io.Console.println((a == b).toString())
    }
    return 0
}
