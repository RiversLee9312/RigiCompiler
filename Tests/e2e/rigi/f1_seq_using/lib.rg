// F1/V4 e2e 配套库（负例·多文件同组）：priv IDisposable 资源经 pub 字段
// 泄出。
class HiddenRes implements core.IDisposable {
    pub init()
    pub override func dispose() { }
    pub func use(): i32 { return 1 }
}
pub class ResHolder {
    pub init()
    pub var hd: HiddenRes = new HiddenRes()
}
