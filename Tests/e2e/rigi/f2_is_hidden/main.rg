// F2/V-D（负例·多文件同组）：is 试探命中私有类型——试探命中即使用点，
// 补只读可见性检查（修复前 is/supers/with 试探命中零检查静默通过）。
// expect-error: 'Hidden' is inaccessible due to its accessibility level
pub func main(): i32 {
    const a = make()
    if (a is Hidden) {
        return 1
    }
    return 0
}
