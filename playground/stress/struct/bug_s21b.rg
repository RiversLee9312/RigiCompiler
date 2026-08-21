// bug S5 复现（文件 B）：推断路径消费泄漏类型。
// `const h = make()` 无类型标注——h 的类型经推断得到 Hidden（修复前
// 不触发任何使用点检查）；`h.n()` 只查成员 n 的 pub，不查 Hidden 本身。
// 与 bug_s21a.rg 一起编译应失败。
pub func main(): i32 {
    const h = make()
    return h.n()
}
