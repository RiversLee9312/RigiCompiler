// F2/V5a 配套库（负例·多文件同组）：pub 类 implements 文件级私有接口
// + like 委托——继承单调性在声明点拦截；同时 like 字段（pub var it:
// ITaste）触字段签名闸。修复前合成的 pub forwarder 晚于 S5 永不参检，
// 跨文件 c.privTaste() 可调。
// expect-error: Inconsistent accessibility: base interface 'ITaste' is less accessible than class 'Cage'
// expect-error: Inconsistent accessibility: field type 'ITaste' is less accessible than field 'it'
interface ITaste {
    func privTaste(): i32
}
class TasteImpl implements ITaste {
    pub init()
    pub override func privTaste(): i32 { return 7 }
}
pub class Cage implements ITaste like it {
    pub var it: ITaste = new TasteImpl()
}
