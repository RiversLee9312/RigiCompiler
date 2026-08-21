// F1/V-A（负例·多文件同组）：推断类型 Box\<Hidden\> 顶层 Box 可见、实参
// Hidden 不可见——IsTypeAccessible 递归构造实参后在推断使用点拦截，
// 诊断命名最深不可见者 Hidden（§16.1）。
// expect-error: 'Hidden' is inaccessible due to its accessibility level
pub func main(): i32 {
    const h = new Holder()
    const bx = h.bx
    return 0
}
