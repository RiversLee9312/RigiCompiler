// V-B（c11a）：const m = hd.hm 推断 Hidden?（V-A 递归命中）+ m?.n() ?. 链——应报 inaccessible
pub func main(): i32 {
    const hd = new SafeHolder()
    const m = hd.hm
    m?.n()
    return 0
}
