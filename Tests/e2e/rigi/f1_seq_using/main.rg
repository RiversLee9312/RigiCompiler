// F1/V4（负例·多文件同组）：seq using(const r = c.hd) 推断资源类型
// HiddenRes——UsingBindingBinder 补同一推断使用点检查（§16.1）。
// expect-error: 'HiddenRes' is inaccessible due to its accessibility level
pub func main(): i32 {
    const c = new ResHolder()
    seq using(const r = c.hd) { r.use() }
    return 0
}
