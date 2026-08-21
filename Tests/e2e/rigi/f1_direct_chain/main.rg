// F1/V-B（负例·多文件同组）：语句位直链 hd.h.n()——中间结果 Hidden 经
// 路径段级统一收口（BindInstanceChain/实例调用形态）拦截（§16.1）。
// expect-error: 'Hidden' is inaccessible due to its accessibility level
pub func main(): i32 {
    const hd = new DirectHolder()
    hd.h.n()
    return 0
}
