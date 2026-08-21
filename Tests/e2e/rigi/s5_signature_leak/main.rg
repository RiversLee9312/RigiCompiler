// bug S5（负例·多文件同组）：priv 类型经 pub 返回值泄漏——声明点
//（P2 签名泄漏，修复1）与推断局部使用点（P3，修复2）各报一次（§16.1）。
// expect-error: Inconsistent accessibility: return type 'Hidden' is less accessible than function 'make'
// expect-error: 'Hidden' is inaccessible due to its accessibility level
pub func main(): i32 {
    const h = make()
    return h.n()
}
