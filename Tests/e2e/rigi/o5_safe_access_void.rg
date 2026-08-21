// bug O5（正例）：?. 调 void 方法（§3.4 + §15.1/§21.3）——then 块发
// invoke.noret；receiver 为 null 时整体不调用。修复前恒走赋值管线发
// invoke，BilVerifier §21.3 拒。
// expect-output: bang
// expect-exit: 0
import core.io.Console
pub interface Hit { func bang() }
pub class Boom implements Hit {
    pub init()
    pub override func bang() { Console.println("bang") }
}
pub func main(): i32 {
    var h: Hit? = new Boom()
    h?.bang()
    h = null
    h?.bang()
    return 0
}
