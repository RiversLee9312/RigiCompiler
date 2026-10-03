// WRAP-001：wrapper 自身先安装嵌套 wrapper，再赋字段初值，最后进入 init。
// expect-output: nested-init-ok
// expect-exit: 0
var ticks: i64 = 0L
var wrong: i64 = 0L
func seed(): i64 { ticks = ticks + 1L
 return 7L }
@WrapperTarget(.Entity)
wrapper Nested { pub var reads: i64 = seed() }
@WrapperTarget(.Entity)
@Nested
wrapper Outer {
    pub var reads: i64 = seed()
    pub init() {
        if ((reads != 7L) or (this:Nested.reads != 7L)) { wrong = wrong + 1L }
        reads = 9L
    }
}
@Outer
class Host { }
pub func main(): i32 {
    var first = new Host()
    var second = new Host()
    if ((ticks != 4L) or (wrong != 0L)) { return 1 }
    if ((first:Outer.reads != 9L) or (second:Outer.reads != 9L)) { return 2 }
    if ((first:Outer:Nested.reads != 7L) or (second:Outer:Nested.reads != 7L)) { return 3 }
    first:Outer:Nested.reads = 9L
    if (first:Outer:Nested.reads != 9L) { return 4 }
    if (second:Outer:Nested.reads != 7L) { return 5 }
    core.io.Console.println("nested-init-ok")
    return 0
}
