// 原生引用计数资源的语言侧契约。
//
// NativeRcHandle 是 local object：每个实例只属于当前 Coroutine，并由
// IDisposable/using 确定性释放。跨 Coroutine 只能传递 shared ICarrige；
// Carrige 是不拥有资源操作能力的弱票据，不实现 IDisposable。接收方调用
// retain()，对空结果做检查，再按具体实现类型作运行期检查转换。
namespace core.native

pub shared interface ICarrige {
    func retain(): Any?
}

// 基类只规定生命周期与搬运协议，不保存或暴露原生 token。具体实现必须
// 把 token 留在私有字段中，并通过 rigi_rt 的 NativeRc 注册表完成 retain /
// release；这样用户派生类不能凭一个整数获得其它原生资源的 authority。
pub abstract class NativeRcHandle\<TCarrige extends ICarrige> implements core.IDisposable {
    pub abstract func carry(): TCarrige
    pub abstract override func dispose()
}
