// V-B（c2a）：语句位直链 hd.h.n()——中间结果 Hidden 无使用点钩子，应报 inaccessible
pub func main(): i32 {
    const hd = new DirectHolder()
    hd.h.n()
    return 0
}
