// bug S5 对照（显式写名）：与 bug_s21a.rg 一起编译。
// 显式写出 `: Hidden` 类型名——名字检查路径（TypeReferences）始终
// 正确报 inaccessible；修复前后均报错（修复后 a 文件另有签名泄漏诊断）。
pub func main(): i32 {
    const h: Hidden = make()
    return h.n()
}
