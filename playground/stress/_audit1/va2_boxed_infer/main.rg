// V-A（c6b）：推断类型 Box\<Hidden> 顶层 Box 可见、实参 Hidden 漏检——应报 inaccessible
pub func main(): i32 {
    const h2 = new Holder2()
    const bx = h2.bx
    return 0
}
