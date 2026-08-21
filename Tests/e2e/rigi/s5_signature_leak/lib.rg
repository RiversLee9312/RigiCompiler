// bug S5 配套库文件（负例·多文件同组）：文件级私有（默认 private）
// 类型 Hidden 经 pub 函数 make 的返回值对外泄漏。
class Hidden {
    pub init()
    pub func n(): i32 { return 1 }
}
pub func make(): Hidden { return new Hidden() }
