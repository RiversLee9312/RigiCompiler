// V-A（p15a）：推断类型 Array\<Hidden> 顶层 Array 可见、实参 Hidden 漏检——应报 inaccessible
pub func main(): i32 {
    const c = new ArrHolder()
    var a = c.arr
    return 0
}
