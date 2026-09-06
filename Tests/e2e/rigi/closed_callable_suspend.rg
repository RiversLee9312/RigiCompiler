// 闭合 Func 基类的间接派生挂起实现必须优先于基类默认实现。
// expect-output: 11
// expect-output: 22
// expect-output: 30
// expect-output: 7
// expect-output: 40
open class Unary : core.Func\<i32, i32> {
    pub override operator call(value: i32): i32 { return value + 10 }
    pub func ordinary(): i32 { return 40 }
}
class SuspendedUnary : Unary {
    pub override operator call(value: i32): i32 {
        yield
        return value + 20
    }
}
func invoke\<T>(body: core.Func\<T, T>, value: T): T { return body(value) }
pub func main(): i32 {
    core.io.Console.println(invoke\<i32>(new Unary(), 1).toString())
    const derived = new SuspendedUnary()
    core.io.Console.println(invoke\<i32>(derived, 2).toString())
    const zero: core.Func\<i32> = func{ (): i32 -> { yield
        return@_ 30 } }
    const pair: core.Func\<i32, i32, i32> = func{ (a: i32, b: i32): i32 -> { yield
        return@_ a + b } }
    core.io.Console.println(zero().toString())
    core.io.Console.println(pair(3, 4).toString())
    core.io.Console.println(derived.ordinary().toString())
    return 0
}
