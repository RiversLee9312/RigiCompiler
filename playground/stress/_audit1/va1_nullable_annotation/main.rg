// V-A（c1c）：标注 `Hidden?` 只查顶层 Nullable，实参 Hidden 漏检——应报 inaccessible
pub func main(): i32 {
    const hd = new Holder()
    var h: Hidden? = hd.hm
    return 0
}
