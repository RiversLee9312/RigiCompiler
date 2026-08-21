// F1/c6（负例·多文件同组）：多步推断链同一泄漏只报一次——const a 的
// init 经统一收口报一次（驻留类型去重），const b = a 与下游使用不再
// 级联重复报（§16.1）。
// expect-error: 'Hidden' is inaccessible due to its accessibility level
pub func main(): i32 {
    const hd = new ChainHolder()
    const a = hd.hm
    const b = a
    return 0
}
