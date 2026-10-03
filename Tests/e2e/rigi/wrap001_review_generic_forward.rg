// WRAP-001：泛型参数字段真实初值与后声明类型的默认构造联合回归。
// 省略/显式 init 均先求初值，每实例 seed 恰一次；显式覆盖9保持9。
// expect-output: generic-forward-init-ok
// expect-exit: 0
var ticks: i64 = 0L
var wrong: i64 = 0L
func seed(): i64 { ticks = ticks + 1L
 return 7L }
@WrapperTarget(.Entity)
rich wrapper Omitted\<TTarget> {
    pub var reads: TTarget = seed() as TTarget
    operator .proxy.read(): i64 { return (reads as i64) + inner() }
}
@Omitted\<i64>
class OmittedHost { pub func read(): i64 { return 0L } }
@WrapperTarget(.Entity)
rich wrapper Explicit\<TTarget> {
    pub var reads: TTarget = seed() as TTarget
    pub init(value: TTarget) {
        if ((reads as i64) != 7L) { wrong = wrong + 1L }
        reads = value
    }
    operator .proxy.read(): i64 { return (reads as i64) + inner() }
}
@Explicit\<i64>(9L)
class ExplicitHost { pub func read(): i64 { return 0L } }
@WrapperTarget(.Entity)
rich wrapper Forward {
    pub var payload: Payload = new Payload()
    operator .proxy.read(): i64 { return payload.n + inner() }
}
@Forward
class ForwardHost { pub func read(): i64 { return 0L } }
class Early { pub var payload: Payload = new Payload() }
rich struct EarlyValue { pub var payload: Payload = new Payload() }
class EarlyDerived { pub var payload: Derived = new Derived() }
class Derived : Base { }
open class Base { pub var n: i64
 pub init() { n = 7L } }
class Payload { pub var n: i64 = 7L }
pub func main(): i32 {
    var a = new OmittedHost()
    if (a.read() != 7L) { return 1 }
    if (a.read() != 7L) { return 2 }
    if (ticks != 1L) { return 3 }
    var b = new OmittedHost()
    if (b.read() != 7L) { return 4 }
    if (ticks != 2L) { return 5 }
    var c = new ExplicitHost()
    if (c.read() != 9L) { return 6 }
    if (c.read() != 9L) { return 7 }
    if (ticks != 3L) { return 8 }
    var d = new ExplicitHost()
    if (d.read() != 9L) { return 9 }
    if (ticks != 4L) { return 10 }
    if (wrong != 0L) { return 11 }
    var f = new ForwardHost()
    if (f.read() != 7L) { return 12 }
    var e = new Early()
    if (e.payload.n != 7L) { return 13 }
    var v = new EarlyValue()
    if (v.payload.n != 7L) { return 14 }
    var inherited = new EarlyDerived()
    if (inherited.payload.n != 7L) { return 15 }
    core.io.Console.println("generic-forward-init-ok")
    return 0
}
