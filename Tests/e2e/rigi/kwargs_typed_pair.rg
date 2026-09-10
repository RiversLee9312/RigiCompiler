// expect-output: kwargs-pair-ok
import core.io.Console
func inspect(options: named i32...) {
    const first = options[0] as core.Pair\<String, i32>
    if ((first.key != "first") or (first.value != 7)) { throw new core.RuntimeException("具名包读取失败") }
    options[1] = new core.Pair\<String, i32>("changed", 42)
    const second = options[1] as core.Pair\<String, i32>
    if ((second.key != "changed") or (second.value != 42)) { throw new core.RuntimeException("具名包写回失败") }
    if (options[2] != null) { throw new core.RuntimeException("越界读取不是 null") }
}
pub func main(): i32 {
    inspect(first = 7, second = 9)
    Console.println("kwargs-pair-ok")
    return 0
}
