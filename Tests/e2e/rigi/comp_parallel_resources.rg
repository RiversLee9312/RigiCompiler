// expect-output: 12
// expect-output: 1
// expect-output: 2
// expect-output: 7
// expect-output: 8
import core.io.Console
var order: i32 = 0
func record(value: i32): i32 { order = (order * 10) + value
 return value }
var first: i32 = record(1)
var second: i32 = record(2)
func left(value: i32): i32 {
    switch (value) {
        (1) -> { return 1 }
        (2) -> { return 2 }
        default -> { return 0 }
    }
}
func right(value: i32): i32 {
    switch (value) {
        (1) -> { return 1 }
        (2) -> { return 2 }
        default -> { return 0 }
    }
}
func caughtLeft(): i32 {
    try { throw new core.RuntimeException("same") }
    catch (_: core.RuntimeException) { return 7 }
    return 0
}
func caughtRight(): i32 {
    try { throw new core.RuntimeException("same") }
    catch (_: core.RuntimeException) { return 8 }
    return 0
}
pub func main(): i32 {
    Console.println(order.toString())
    Console.println(left(first).toString())
    Console.println(right(second).toString())
    Console.println(caughtLeft().toString())
    Console.println(caughtRight().toString())
    return 0
}
