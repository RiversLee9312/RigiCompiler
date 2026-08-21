// V-B（p12c）：(c.hn if? c.h).n()——if? 结果 Hidden 无使用点钩子，应报 inaccessible
pub func main(): i32 {
    const c = new PairHolder()
    return (c.hn if? c.h).n()
}
