// 诊断去重（c6）：多步推断链同一泄漏只报一次——const a 报、const b 不再报
pub func main(): i32 {
    const hd = new ChainHolder()
    const a = hd.hm
    const b = a
    return 0
}
