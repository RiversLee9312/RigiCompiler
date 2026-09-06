// expect-output: 3
// expect-output: true
// expect-output: 8
var count: i32 = 3
func increment() { count = 8 }
pub func main(): i32 {
    core.io.Console.println(count.toString())
    seq using(const a = placeOf count) using(const b = placeOf count) {
        increment()
        core.io.Console.println((a == b).toString())
        core.io.Console.println(count.toString())
    }
    return 0
}
