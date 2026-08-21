// V4 probe p18a 配套库：priv Disposable 资源经 seq using 推断泄出
class HiddenRes implements core.IDisposable {
    pub init()
    pub override func dispose() { }
    pub func use(): i32 { return 1 }
}
pub class ResHolder {
    pub init()
    pub var hd: HiddenRes = new HiddenRes()
}
