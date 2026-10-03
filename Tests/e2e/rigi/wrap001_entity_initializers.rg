// WRAP-001：三目标×四种 init 形态矩阵的一行；每个实例初值恰执行一次。
// 首读为7、代理显式覆盖为9后保持9；有参 init 在体内先读7，再选择覆盖。
// expect-output: entity-init-ok
// expect-exit: 0
var ticks: i64 = 0L
var wrong: i64 = 0L
func seed(): i64 { ticks = ticks + 1L
 return 7L }
@WrapperTarget(.Entity)
wrapper Omitted {
    pub var reads: i64 = seed()
    operator .proxy.read(): i64 {
        var saved = reads
        reads = 9L
        return saved + inner()
    }
}
@Omitted
class OmittedHost { pub func read(): i64 { return 0L } }
@WrapperTarget(.Entity)
wrapper Bodiless {
    pub var reads: i64 = seed()
    pub init()
    operator .proxy.read(): i64 {
        var saved = reads
        reads = 9L
        return saved + inner()
    }
}
@Bodiless
class BodilessHost { pub func read(): i64 { return 0L } }
@WrapperTarget(.Entity)
wrapper Empty {
    pub var reads: i64 = seed()
    pub init() { }
    operator .proxy.read(): i64 {
        var saved = reads
        reads = 9L
        return saved + inner()
    }
}
@Empty
class EmptyHost { pub func read(): i64 { return 0L } }
@WrapperTarget(.Entity)
wrapper Param {
    pub var reads: i64 = seed()
    pub init(next: i64) {
        if (reads != 7L) { wrong = wrong + 1L }
        reads = next
    }
    operator .proxy.read(): i64 {
        var saved = reads
        reads = 9L
        return saved + inner()
    }
}
@Param(7L)
class ParamHost7 { pub func read(): i64 { return 0L } }
@Param(9L)
class ParamHost9 { pub func read(): i64 { return 0L } }
pub func main(): i32 {
    var v0a = new OmittedHost()
    if (v0a.read() != 7L) { return 1 }
    if (v0a.read() != 9L) { return 2 }
    if (v0a.read() != 9L) { return 3 }
    if (ticks != 1L) { return 4 }
    var v0b = new OmittedHost()
    if (v0b.read() != 7L) { return 5 }
    if (v0b.read() != 9L) { return 6 }
    if (v0b.read() != 9L) { return 7 }
    if (ticks != 2L) { return 8 }
    var v1a = new BodilessHost()
    if (v1a.read() != 7L) { return 9 }
    if (v1a.read() != 9L) { return 10 }
    if (v1a.read() != 9L) { return 11 }
    if (ticks != 3L) { return 12 }
    var v1b = new BodilessHost()
    if (v1b.read() != 7L) { return 13 }
    if (v1b.read() != 9L) { return 14 }
    if (v1b.read() != 9L) { return 15 }
    if (ticks != 4L) { return 16 }
    var v2a = new EmptyHost()
    if (v2a.read() != 7L) { return 17 }
    if (v2a.read() != 9L) { return 18 }
    if (v2a.read() != 9L) { return 19 }
    if (ticks != 5L) { return 20 }
    var v2b = new EmptyHost()
    if (v2b.read() != 7L) { return 21 }
    if (v2b.read() != 9L) { return 22 }
    if (v2b.read() != 9L) { return 23 }
    if (ticks != 6L) { return 24 }
    var v3a = new ParamHost7()
    if (v3a.read() != 7L) { return 25 }
    if (v3a.read() != 9L) { return 26 }
    if (v3a.read() != 9L) { return 27 }
    if (ticks != 7L) { return 28 }
    var v3b = new ParamHost7()
    if (v3b.read() != 7L) { return 29 }
    if (v3b.read() != 9L) { return 30 }
    if (v3b.read() != 9L) { return 31 }
    if (ticks != 8L) { return 32 }
    var v4a = new ParamHost9()
    if (v4a.read() != 9L) { return 33 }
    if (v4a.read() != 9L) { return 34 }
    if (v4a.read() != 9L) { return 35 }
    if (ticks != 9L) { return 36 }
    var v4b = new ParamHost9()
    if (v4b.read() != 9L) { return 37 }
    if (v4b.read() != 9L) { return 38 }
    if (v4b.read() != 9L) { return 39 }
    if (ticks != 10L) { return 40 }
    if (wrong != 0L) { return 41 }
    core.io.Console.println("entity-init-ok")
    return 0
}
