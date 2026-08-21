// V-A（p16b）：推断类型 Task\<HiddenS> 顶层 Task 可见、实参 HiddenS 漏检——应报 inaccessible
pub func main(): i32 {
    const c = new TaskHolder()
    var t2 = c.t
    return 0
}
