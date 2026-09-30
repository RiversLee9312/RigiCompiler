// Rigi 标准库：core.io AutoBuffer（STDLIB §4.4「已确定的内存流与缓冲
// 包装器」，施工块 2-2）。动态扩容的 u8 存储——纯托管内存的 local 类，
// 不需要 IDisposable；其输入/输出流视图是流式访问入口：
//   - getInputStream()：返回的流实现 ISeekableStream，初始位置 0，
//     每次创建独立游标；
//   - getOutputStream()：返回的流实现 ISeekableStream，初始位置在
//     创建当时的末尾（默认从此追加）；显式 seek 后按新位置写入——
//     覆盖已有范围或扩展内容；定位到末尾之后不立即扩容，写入时空隙
//     补零；负位置抛 core.OutOfBoundException；
//   - 视图直接访问 AutoBuffer 当前内容（非创建时固定的内容快照）：
//     按顺序追加后，已有输入视图可继续读到新增数据；读到当前末尾即
//     返回 EOF，不等待未来写入；
//   - 关闭视图不清空 AutoBuffer；各视图及对 AutoBuffer 的访问之间
//     不支持并发读写（同一对象不支持并发或重入操作的契约与流相同，
//     由调用者遵守）。
// 与普通内存流导出单个 Span 的便利入口分开；不把 AutoBuffer 作为
// readAll 的另一种直接返回类型。容量溢出（超出 i32 可表示范围）抛
// core.OutOfBoundException（与 List 同款约束）；视图 dispose 幂等由
// 基类支架保证，dispose 后操作抛 core.IllegalStateException。
namespace core.io

// 动态扩容字节缓冲区（§4.4）。内部存储与写入面对同模块流视图开放
//（internal：编译单元内恒可见，不对外承诺——List.modCount 同款）。
pub class AutoBuffer {
    // 当前内容存储：前 length 字节为有效内容，容量恒 >= length
    internal var storage: Array\<u8>
    // 当前有效字节数（内部用 i32 与 Array 索引配套；对外以 count 属性
    // 提供 i64 视图）
    internal var length: i32

    pub init() {
        // 初始容量 8（与 List 首容量同款）；写入按需倍增
        storage = core.collections.arrayOf\<u8>(8)
        length = 0
    }

    // 当前字节数（i64 只读）。契约未钉死属性名，本实现取 count 基线
    //（与 Map/Queue 的 count 属性同款）
    pub var count: i64 {
        pub get(_: _) { return (length as i64) }
    }

    // 输入视图（§4.4）：初始位置 0，每次创建独立游标；视图直接访问
    // AutoBuffer 当前内容
    pub func getInputStream(): InputStream {
        return new AutoBufferInputStream(this)
    }

    // 输出视图（§4.4）：初始位置在创建当时的末尾（默认从此追加）
    pub func getOutputStream(): OutputStream {
        return new AutoBufferOutputStream(this)
    }

    // 确保容量至少 need 字节（need 以 i64 表达，规避 i32 加法回绕）。
    // 倍增策略：新容量取 max(旧容量*2, need)，首次从 8 起；need 超出
    // i32 可表示范围即超出 Array<u8> 存储上限，抛 core.OutOfBoundException
    //（与 List.grow 同款约束，不窄化不回绕）
    internal func ensureCapacity(need: i64) {
        if (need <= (storage.length as i64)) {
            return
        }
        if (need > (2147483647 as i64)) {
            throw new core.OutOfBoundException(
                "AutoBuffer 容量超过 i32 可表示范围")
        }
        var cap: i64 = ((storage.length as i64) * (2 as i64))
        if (cap < (8 as i64)) {
            cap = (8 as i64)
        }
        if (cap < need) {
            cap = need
        }
        const bigger = core.collections.arrayOf\<u8>((cap as i32))
        var i: i32 = 0
        while (i < length) {
            bigger[i] = (storage[i] as u8)
            i = (i + 1)
        }
        storage = bigger
    }

    // 视图写出路径：把 src[offset..offset+count) 写到绝对位置 pos。
    // 定位越过末尾后写入时先扩容到写入终点（超 i32 容量上限即抛），
    // 空隙 [length..pos) 补零（显式执行，不依赖新槽零初始化的实现
    // 细节）；有效长度推进到写入终点（取较大者）
    internal func writeAt(pos: i64, src: Span\<u8>, offset: i32, count: i32) {
        const end: i64 = pos + (count as i64)
        if (end > (length as i64)) {
            ensureCapacity(end)
        }
        var z: i32 = length
        while ((z as i64) < pos) {
            storage[z] = (0 as u8)
            z = (z + 1)
        }
        var i: i32 = 0
        while (i < count) {
            storage[(pos as i32) + i] = (src[offset + i] as u8)
            i = (i + 1)
        }
        if (end > (length as i64)) {
            length = (end as i32)
        }
    }
}

// AutoBuffer 输入视图（§4.4）：可定位输入流，初始位置 0，独立游标。
// 视图直接访问宿主当前内容（非快照）：宿主追加后可继续读到新增数据，
// 读到创建后任意时刻的当前末尾即 EOF。local 类型，与宿主及其他视图
// 之间不支持并发读写。
priv class AutoBufferInputStream : InputStream implements ISeekableStream {
    // 宿主缓冲区（借用：视图不清空、不缩容宿主内容）
    priv const host: AutoBuffer
    // 当前读位置（i64，零基字节偏移）
    priv var pos: i64

    pub init(target: AutoBuffer) {
        host = target
        // 输入视图初始位于开头（§4.4）
        pos = (0 as i64)
    }

    // 单次读取（基类契约）：状态闸门先于范围校验；count == 0 校验后
    // 返回 0（不消费输入、不证明 EOF）；位置在当前末尾及之后即 EOF
    pub override func read(buffer: Span\<u8>, offset: i32, count: i32): i32 {
        ensureOpen()
        checkRange(buffer, offset, count)
        if (count == 0) {
            return 0
        }
        // 读时取宿主当前长度：顺序追加后已有视图读到新增数据（§4.4）
        const avail: i64 = ((host.length as i64) - pos)
        if (avail <= (0 as i64)) {
            return 0
        }
        var want: i32 = count
        if ((want as i64) > avail) {
            want = (avail as i32)
        }
        var i: i32 = 0
        while (i < want) {
            buffer[offset + i] = (host.storage[(pos as i32) + i] as u8)
            i = (i + 1)
        }
        pos = pos + (want as i64)
        return want
    }

    // 定位（§4.4 ISeekableStream）：负位置抛 core.OutOfBoundException；
    // 允许定位到当前末尾与之后（其后读即 EOF——输入侧不等待未来写入）
    pub override func seek(position: i64) {
        ensureOpen()
        if (position < (0 as i64)) {
            throw new core.OutOfBoundException(
                "AutoBuffer 输入视图位置非法：${position} 为负")
        }
        pos = position
    }

    // 当前字节位置（i64）
    pub override func getPosition(): i64 {
        ensureOpen()
        return pos
    }

    // 收尾钩子（基类 dispose 支架保证幂等）：只作废自身游标——关闭
    // 视图不清空 AutoBuffer（§4.4），宿主内容保留、可继续开新视图
    protected override func disposeCore() {
        pos = (0 as i64)
    }
}

// AutoBuffer 输出视图（§4.4）：可定位输出流，初始位置在创建当时的
// 末尾（默认从此追加）；显式 seek 后按新位置写入——覆盖已有范围或
// 扩展内容，越过末尾写入时空隙补零。local 类型，与宿主及其他视图
// 之间不支持并发读写。
priv class AutoBufferOutputStream : OutputStream implements ISeekableStream {
    // 宿主缓冲区（借用：视图的写出直接落到宿主当前内容上）
    priv const host: AutoBuffer
    // 当前写位置（i64，零基字节偏移）
    priv var pos: i64

    pub init(target: AutoBuffer) {
        host = target
        // 输出视图初始位于创建当时的末尾（§4.4：默认从此追加）
        pos = (target.length as i64)
    }

    // 单次写出（基类契约）：状态闸门先于范围校验；count == 0 校验后
    // 直接成功；写入按当前游标位置落盘（覆盖/扩展/补零由宿主
    // writeAt 承担），成功返回即全部字节已写入宿主
    pub override func write(buffer: Span\<u8>, offset: i32, count: i32) {
        ensureOpen()
        checkRange(buffer, offset, count)
        if (count == 0) {
            return
        }
        host.writeAt(pos, buffer, offset, count)
        pos = pos + (count as i64)
    }

    // 刷新（§4.4「内存目标完成内容写入」）：写入即完成，无待输出
    // 缓冲，flush 为无操作成功返回；仍先过状态闸门，成功后可继续写入
    pub override func flush() {
        ensureOpen()
    }

    // 定位（§4.4 ISeekableStream）：负位置抛 core.OutOfBoundException；
    // 允许定位到当前末尾与之后（定位本身不扩容，写入时空隙补零，
    // 仍受宿主 i32 容量上限约束）
    pub override func seek(position: i64) {
        ensureOpen()
        if (position < (0 as i64)) {
            throw new core.OutOfBoundException(
                "AutoBuffer 输出视图位置非法：${position} 为负")
        }
        pos = position
    }

    // 当前字节位置（i64）
    pub override func getPosition(): i64 {
        ensureOpen()
        return pos
    }

    // 收尾钩子（基类 dispose 支架保证幂等）：只作废自身游标——关闭
    // 视图不清空 AutoBuffer（§4.4），已写内容全部保留
    protected override func disposeCore() {
        pos = (0 as i64)
    }
}
