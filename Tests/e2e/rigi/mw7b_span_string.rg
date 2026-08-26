// MW7b：spanOf<String> 读写
// expect-output: Hello, world!
// expect-exit: 0
import core.collections.*
pub func main(): i32 {
    var a = spanOf\<String>(2)
    a[0] = "Hello, "
    a[1] = "world!"
    core.io.Console.println(((a[0] if? "") + (a[1] if? "")))
    return 0
}
