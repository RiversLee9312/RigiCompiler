// WRAP-001：扩大 wrapper 覆盖不能改变 class/struct 既有初值与覆盖顺序。
// expect-output: class-struct-init-ok
// expect-exit: 0
var ticks: i64 = 0L
var wrong: i64 = 0L
func seed(): i64 { ticks = ticks + 1L
 return 7L }
class DefaultClass { pub var reads: i64 = seed() }
struct DefaultStruct { pub var reads: i64 = seed() }
class ExplicitClass {
    pub var reads: i64 = seed()
    pub init() { if (reads != 7L) { wrong = wrong + 1L }
        reads = 9L }
}
struct ExplicitStruct {
    pub var reads: i64 = seed()
    pub init() { if (reads != 7L) { wrong = wrong + 1L }
        reads = 9L }
}
pub func main(): i32 {
    var a = new DefaultClass()
    var b = new DefaultStruct()
    var c = new ExplicitClass()
    var d = new ExplicitStruct()
    if ((a.reads != 7L) or (b.reads != 7L)) { return 1 }
    if ((c.reads != 9L) or (d.reads != 9L)) { return 2 }
    if ((ticks != 4L) or (wrong != 0L)) { return 3 }
    a.reads = 9L
    b.reads = 9L
    if ((a.reads != 9L) or (b.reads != 9L)) { return 4 }
    core.io.Console.println("class-struct-init-ok")
    return 0
}
