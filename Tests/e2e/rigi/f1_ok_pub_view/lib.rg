// F1 合法对照配套库（正例·多文件同组）：priv 实现经 pub Base 视图对外。
pub open class Base {
    pub init()
    pub open func n(): i32 { return 0 }
}
class HiddenImpl : Base {
    pub init() { super() }
    pub override func n(): i32 { return 7 }
}
pub func mkView(): Base { return new HiddenImpl() }
