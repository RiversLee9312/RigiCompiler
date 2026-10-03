// WRAP-001：三目标×四种 init 形态矩阵的一行；每个实例初值恰执行一次。
// 首读为7、代理显式覆盖为9后保持9；有参 init 在体内先读7，再选择覆盖。
// expect-output: value-init-ok
// expect-exit: 0
var ticks: i64 = 0L
var wrong: i64 = 0L
func seed(): i64 { ticks = ticks + 1L
 return 7L }
@WrapperTarget(.Value)
wrapper Omitted {
    pub var reads: i64 = seed()
    operator .proxy.get\<TValue>(value: TValue): TValue {
        var saved = reads
        reads = 9L
        return saved as TValue
    }
}
class OmittedHost {
    @Omitted
    pub const value: i64 = 0L
}
@WrapperTarget(.Value)
wrapper Bodiless {
    pub var reads: i64 = seed()
    pub init()
    operator .proxy.get\<TValue>(value: TValue): TValue {
        var saved = reads
        reads = 9L
        return saved as TValue
    }
}
class BodilessHost {
    @Bodiless
    pub const value: i64 = 0L
}
@WrapperTarget(.Value)
wrapper Empty {
    pub var reads: i64 = seed()
    pub init() { }
    operator .proxy.get\<TValue>(value: TValue): TValue {
        var saved = reads
        reads = 9L
        return saved as TValue
    }
}
class EmptyHost {
    @Empty
    pub const value: i64 = 0L
}
@WrapperTarget(.Value)
wrapper Param {
    pub var reads: i64 = seed()
    pub init(next: i64) {
        if (reads != 7L) { wrong = wrong + 1L }
        reads = next
    }
    operator .proxy.get\<TValue>(value: TValue): TValue {
        var saved = reads
        reads = 9L
        return saved as TValue
    }
}
class ParamHost7 {
    @Param(7L)
    pub const value: i64 = 0L
}
class ParamHost9 {
    @Param(9L)
    pub const value: i64 = 0L
}
pub func main(): i32 {
    var v0a = new OmittedHost()
    if (v0a.value != 7L) { return 1 }
    if (v0a.value != 9L) { return 2 }
    if (v0a.value != 9L) { return 3 }
    if (ticks != 1L) { return 4 }
    var v0b = new OmittedHost()
    if (v0b.value != 7L) { return 5 }
    if (v0b.value != 9L) { return 6 }
    if (v0b.value != 9L) { return 7 }
    if (ticks != 2L) { return 8 }
    var v1a = new BodilessHost()
    if (v1a.value != 7L) { return 9 }
    if (v1a.value != 9L) { return 10 }
    if (v1a.value != 9L) { return 11 }
    if (ticks != 3L) { return 12 }
    var v1b = new BodilessHost()
    if (v1b.value != 7L) { return 13 }
    if (v1b.value != 9L) { return 14 }
    if (v1b.value != 9L) { return 15 }
    if (ticks != 4L) { return 16 }
    var v2a = new EmptyHost()
    if (v2a.value != 7L) { return 17 }
    if (v2a.value != 9L) { return 18 }
    if (v2a.value != 9L) { return 19 }
    if (ticks != 5L) { return 20 }
    var v2b = new EmptyHost()
    if (v2b.value != 7L) { return 21 }
    if (v2b.value != 9L) { return 22 }
    if (v2b.value != 9L) { return 23 }
    if (ticks != 6L) { return 24 }
    var v3a = new ParamHost7()
    if (v3a.value != 7L) { return 25 }
    if (v3a.value != 9L) { return 26 }
    if (v3a.value != 9L) { return 27 }
    if (ticks != 7L) { return 28 }
    var v3b = new ParamHost7()
    if (v3b.value != 7L) { return 29 }
    if (v3b.value != 9L) { return 30 }
    if (v3b.value != 9L) { return 31 }
    if (ticks != 8L) { return 32 }
    var v4a = new ParamHost9()
    if (v4a.value != 9L) { return 33 }
    if (v4a.value != 9L) { return 34 }
    if (v4a.value != 9L) { return 35 }
    if (ticks != 9L) { return 36 }
    var v4b = new ParamHost9()
    if (v4b.value != 9L) { return 37 }
    if (v4b.value != 9L) { return 38 }
    if (v4b.value != 9L) { return 39 }
    if (ticks != 10L) { return 40 }
    if (wrong != 0L) { return 41 }
    core.io.Console.println("value-init-ok")
    return 0
}
