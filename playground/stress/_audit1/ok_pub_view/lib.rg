// 合法对照 c10 配套库：priv 实现经 pub Base 视图对外
pub open class Base {
    pub init()
    pub open func n(): i32 { return 0 }
}
class HiddenImpl : Base {
    pub init() { super() }
    pub override func n(): i32 { return 7 }
}
pub func mkView(): Base { return new HiddenImpl() }
