// V5a（负例）：pub 类 implements 文件级私有接口 + like 委托——声明侧
// 单调性缺失放行，合成的 pub forwarder 晚于 S5 永不参检，跨文件
// c.privTaste() 可调。修复后 implements 子句单调性检查在此拦截
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
