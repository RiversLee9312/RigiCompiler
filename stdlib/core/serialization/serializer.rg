// Rigi 标准库：Serializer 抽象基类（STDLIB §4.6.2 两层职责 / D3，施工块 4-4）。
// 两层签名（已确定公共契约，逐字）：
//   - 抽象格式层：read(input): Any? / write(value: Any?, output)——从
//     InputStream 建立 SB 表示 / 将 SB 表示写入 OutputStream，报告格式
//     错误、表示不支持、限制超出；read/write 支持 null 根值（Any? 动态
//     根，§4.6.1）。格式实现区分对象字段、基元载荷和集合载荷，不把
//     内部载荷字段（..value/..case/..id/..ref/..data 等受控元数据）
//     当成业务成员输出。
//   - 对象便利层（本基类实现）：serialize<T with Serializable> /
//     deserialize<T with Serializable>——组合既有对象编解码
//     （value:Serializable.toParcel() 与严格 fromParcel<T>），格式层
//     由子类经抽象方法接入。
//
// 借用与刷新边界（§4.6.2，逐条钉死）：
//   - 调用者传入的流始终借用：Serializer 不因序列化成功或失败而关闭
//     该流——本基类全部入口都不对传入流调用 dispose()；
//   - 序列化成功返回前完成本次编码并将自身缓冲全部交给传入的
//     OutputStream，但不主动调用该流的 flush()——最终刷新时机由调用
//     者决定；本基类实现不包装调用者传入的流（直接经抽象 write 交给
//     子类），故不存在内部临时包装器逐层 flush 意外触发调用者流刷新
//     的问题；子类实现格式层时同样不得在 read/write 内关闭或刷新
//     传入流；
//   - 便利入口内部创建的临时流（serializeToString/deserializeFromString
//     的内存流）由该入口负责清理（finally dispose），与调用者流无关。
//
// 实例状态：每次调用有独立解析状态的语义由子类保证（格式层实现的
// 解析器/token 状态不得跨调用残留）；同一 Serializer 实例可顺序复用，
// 但不支持并发或重入调用（与 §4.4 流实例的设计契约同口径）。
//
// 基类不提供编码选择 API，也不提供 loopedRefEnabled 参数（D3）：不能
// 通过继承让子类获得该参数；既有 toParcel/fromParcel/deepCopy 的图
// 模式参数继续保留在原 API 上。对象入口固定默认树模式（toParcel()
// 不传 loopedRefEnabled）。
namespace core.serialization

/**
 * 序列化器抽象基类（§4.6.2 / D3）：抽象格式方法 + 对象便利层的两层
 * 职责划分。可由用户继承实现具体格式（签名即契约）。
 */
pub abstract class Serializer {
    // ── 抽象格式层（签名即契约，逐字）──

    // 从 InputStream 建立 SB 通用表示（§4.6.1 动态根值：null 合法；
    // 非 null 值必须是格式支持的 SB 表示）。报告格式错误、表示不支持、
    // 限制超出。传入流按 §4.4 借用：本方法不关闭、不刷新它。
    pub abstract func read(input: core.io.InputStream): Any?

    // 将 SB 通用表示写入 OutputStream（value 为 null 时写 null 根值，
    // 行为按具体格式定义）。格式实现区分对象字段、基元载荷和集合
    // 载荷，不把内部载荷字段（..value/..case/..id/..ref/..data 等
    // 受控元数据）当成业务成员。传入流按 §4.4 借用：成功返回前完成
    // 本次编码并将自身缓冲全部交给它，但不主动调用其 flush()——最终
    // 刷新时机由调用者决定；本方法不关闭该流。
    pub abstract func write(value: Any?, output: core.io.OutputStream)

    // ── 对象便利层（基类实现，组合既有对象编解码）──

    // 序列化 T with Serializable：调 value:Serializable.toParcel()
    // （默认树模式，不传 loopedRefEnabled——基类不提供该参数），再交给
    // 格式层 write。toParcel 快照不别名源图任何可变部分（数组/列表/
    // Map/对象全部新建）。调用者流借用：本方法不关闭、不刷新 output。
    pub func serialize\<T with Serializable>(value: T, output: core.io.OutputStream) {
        write(value:Serializable.toParcel(), output)
    }

    // 反序列化 T with Serializable：先经格式层 read 取得通用表示，
    // 验证是 Parcel（对象入口要求对象表示）；遇 null 根值或非 Parcel
    // 表示抛 SerializationException——返回非可空 T 的对象入口遇 null
    // 根值报错，不构造默认对象（§4.6.2）。再调严格 fromParcel<T>
    // （§4.6.3：无值类型转换 + 业务字段集合完全匹配，wire 类型互换/
    // 字段缺失/多余/可空缺项抛 SerializationException）。
    pub func deserialize\<T with Serializable>(input: core.io.InputStream): T {
        const representation = read(input)
        if (representation == null) {
            throw new SerializationException(
                "deserialize 遇到 null 根值：对象入口不构造默认对象")
        }
        return deserializeChecked\<T>(representation)
    }

    // 非 null 根值的表示核验路径。独立为非可空 Any 形参方法：native
    // 后端对可空 Any 槽直接取 typeof 存在收窄缺陷（b4-2 实测抛
    // CastException），null 收窄后的值以非可空 Any 形参传递则双宿主
    // 一致（同 Parcel.setDynamic 的 putDynamicChecked 分拆先例）。
    priv func deserializeChecked\<T with Serializable>(representation: Any): T {
        if (not (representation is Parcel)) {
            throw new SerializationException(
                "deserialize 需要 Parcel 对象表示，实际为：${typeOf(representation).toString()}")
        }
        return fromParcel\<T>(representation as Parcel)
    }

    // ── String/内存缓冲区便利入口（§4.6.2：经编码器/内存流组合，复用
    //    同一实现）──

    // 序列化到 String：UTF-8 编码 + 内存输出流组合 serialize。内部临时
    // 内存流由本入口负责清理（finally dispose，与调用者无关）；基类
    // 不主动 flush 任何流——内存目标 flush 本就为无操作，内容以
    // toSpan 导出。默认实现按文本 UTF-8 组合；若具体格式不是文本
    // 格式，子类可 override 本方法抛 Unsupported（基类不为此另设
    // 配置开关，编码选择 API 不在基类上）。
    pub func serializeToString\<T with Serializable>(value: T): String {
        const stream = new core.io.MemoryOutputStream()
        try {
            serialize\<T>(value, stream)
            const decoder = new core.text.Utf8Decoder()
            return decoder.decode(stream.toSpan(), true)
        } finally(e) {
            stream.dispose()
        }
    }

    // 从 String 反序列化：UTF-8 编码 + 内存输入流组合 deserialize。
    // 内部临时内存流由本入口负责清理（finally dispose）。override 约定
    // 与 serializeToString 对称。
    pub func deserializeFromString\<T with Serializable>(text: String): T {
        const encoder = new core.text.Utf8Encoder()
        const stream = new core.io.MemoryInputStream(encoder.encode(text))
        try {
            return deserialize\<T>(stream)
        } finally(e) {
            stream.dispose()
        }
    }
}
