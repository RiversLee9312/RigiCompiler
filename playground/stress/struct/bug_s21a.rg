// bug S5 复现（文件 A）：文件私有类型经 pub 返回值泄漏。
// 顶层默认 private 的 Hidden 被 pub func make 的返回类型送出文件——
// 修复后本文件声明点报签名泄漏（§16.1 签名可见性单调性）。
// 与 bug_s21b.rg 一起编译应失败（两条诊断：声明点 + 推断使用点）。
class Hidden {
    pub init()
    pub func n(): i32 { return 1 }
}

pub func make(): Hidden { return new Hidden() }
