// 原生引用计数资源的语言侧契约。
//
// NativeRcHandle 是 local object：每个实例只属于当前 Coroutine，并由
// IDisposable/using 确定性释放。跨 Coroutine 只能传递 shared ICarriage；
// Carriage 是不拥有资源操作能力的弱票据，不实现 IDisposable。接收方调用
// retain()，对空结果做检查，再按具体实现类型作运行期检查转换。
namespace core.native

pub shared interface ICarriage {
    func retain(): Any?
}

// 基类只规定生命周期与搬运协议，不保存或暴露原生 token。具体实现必须
// 把 token 留在私有字段中，并通过 rigi_rt 的 NativeRc 注册表完成 retain /
// release；这样用户派生类不能凭一个整数获得其它原生资源的 authority。
pub abstract class NativeRcHandle\<TCarriage extends ICarriage> implements core.IDisposable {
    pub abstract func carry(): TCarriage
    pub abstract override func dispose()
}

// ===== B2-4a：Span<u8> native ABI 验证原语（临时性质）=====
// STDLIB 05-io §4.4 标准流扩建的前置通路验证：native 参数/返回支持
// Span<u8>（C 侧表示 = 16B 胖引用，D6：C 边界胖值一律指针；缓冲区本体
// 与数组同构 32B 前缀）。后续标准流块的 read/write 原语将真正消费这条
// ABI，本原语仅供 e2e / NativeE2E 对拍使用。
// 语义：buffer[offset .. offset+count) 逐字节 XOR 0xFF 写回原位置，返回
// 处理字节数——一个同时验证「读 + 写 + 返回」的回声原语（XOR 两次 = 原值）。
@NativeLibrary("rigi_rt")
@NativeSymbol("span_u8_echo")
priv native func rigi_span_u8_echo(buffer: Span\<u8>, offset: i32, count: i32): i32

// 公开包装（ABI 验证用途的最小转发）：priv native 不直接对用户代码可见
pub func spanU8Echo(buffer: Span\<u8>, offset: i32, count: i32): i32 {
    return rigi_span_u8_echo(buffer, offset, count)
}
