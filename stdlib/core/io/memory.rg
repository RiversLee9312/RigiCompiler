// Rigi 标准库：core.io 普通内存流（STDLIB §4.4「已确定的内存流与缓冲
// 包装器」第一段，施工块 2-2）。
//   - MemoryInputStream：从已有 Span<u8> 构造，默认复制输入数据形成
//     固定快照——构造后调用者改原 Span 不影响流内容；实现
//     ISeekableStream（位置 i64、零基字节偏移；seek 允许到末尾，越过
//     末尾后读即 EOF；负位置抛 core.OutOfBoundException）。
//   - MemoryOutputStream：内部可扩容 u8 存储（倍增，容量溢出抛
//     core.OutOfBoundException，与 List 同款 i32 约束）；toSpan()
//     导出独立副本（修改返回值不影响流内容），输出结果须在 dispose()
//     前取出。首版不暴露内部可变缓冲区；首版取舍：不实现
//     ISeekableStream——普通内存输出流以顺序写出为主，定位写入
//     （覆盖/扩展/补零）走 AutoBuffer 的输出视图。
// flush/dispose 语义（§4.4）：内存目标 flush 为无操作成功返回——成功
// 写入即内容完成；dispose 只进关闭态（幂等支架在基类，disposeCore
// 清理自身：释放内部存储引用）。关闭后 read/seek/getPosition 与
// write/toSpan 抛 core.IllegalStateException。
// 两类均为 local 类型（§4.4：普通流与包装器使用 local 类型），同一
// 实例不支持并发或重入操作。
namespace core.io

// 普通内存输入流（§4.4）：快照语义的可定位输入流。
pub class MemoryInputStream : InputStream implements ISeekableStream {
    // 固定快照（构造时一次性复制）；var：disposeCore 收尾时释放引用
    priv var snapshot: Array\<u8>
    // 当前读位置（i64，零基字节偏移——累计位置与单次缓冲区索引 i32 分开）
    priv var pos: i64

    // 从已有 Span<u8> 构造：默认复制输入数据形成固定快照（§4.4），
    // 构造后调用者修改原 Span 不影响本流
    pub init(source: Span\<u8>) {
        snapshot = core.collections.arrayOf\<u8>(source.length)
        var i: i32 = 0
        while (i < snapshot.length) {
            // Span/Array 索引读按 Q6（§13.2）返回可空，界内恒非空，as 解包
            snapshot[i] = (source[i] as u8)
            i = (i + 1)
        }
        pos = (0 as i64)
    }

    // 单次读取（基类契约）：状态闸门先于范围校验；count == 0 校验后
    // 返回 0（不消费输入、不证明 EOF）；位置越过末尾后读即 EOF
    pub override func read(buffer: Span\<u8>, offset: i32, count: i32): i32 {
        ensureOpen()
        checkRange(buffer, offset, count)
        if (count == 0) {
            return 0
        }
        // 剩余可用字节用 i64 计（快照长度 <= i32 上限，差值 cast 回 i32 安全）
        const avail: i64 = ((snapshot.length as i64) - pos)
        if (avail <= (0 as i64)) {
            return 0
        }
        var want: i32 = count
        if ((want as i64) > avail) {
            want = (avail as i32)
        }
        var i: i32 = 0
        while (i < want) {
            buffer[offset + i] = (snapshot[(pos as i32) + i] as u8)
            i = (i + 1)
        }
        pos = pos + (want as i64)
        return want
    }

    // 定位（§4.4 ISeekableStream）：负位置抛 core.OutOfBoundException；
    // 允许定位到末尾（其后读即 EOF）与末尾之后（定位本身不校验上界，
    // 越过末尾后读即 EOF——输入侧没有「空隙写」问题）
    pub override func seek(position: i64) {
        ensureOpen()
        if (position < (0 as i64)) {
            throw new core.OutOfBoundException(
                "内存输入流位置非法：${position} 为负")
        }
        pos = position
    }

    // 当前字节位置（i64）
    pub override func getPosition(): i64 {
        ensureOpen()
        return pos
    }

    // 收尾钩子（基类 dispose 支架保证幂等）：释放快照引用。local 托管
    // 对象无系统资源，关闭后 read/seek/getPosition 由 ensureOpen 拒绝
    protected override func disposeCore() {
        snapshot = core.collections.arrayOf\<u8>(0)
        pos = (0 as i64)
    }
}

// 普通内存输出流（§4.4）：内部可扩容 u8 存储，导出独立副本。首版取舍：
// 不实现 ISeekableStream（见文件头）
pub class MemoryOutputStream : OutputStream {
    // 可扩容存储（倍增）；var：disposeCore 收尾时释放引用
    priv var storage: Array\<u8>
    // 当前有效字节数（0..storage.length；内部用 i32 与 Array 索引配套，
    // 对外累计字节数语义由容量上限 i32 约束兜底）
    priv var len: i32

    pub init() {
        // 初始容量 8（与 List 首容量同款）；写入按需倍增
        storage = core.collections.arrayOf\<u8>(8)
        len = 0
    }

    // 单次写出（基类契约）：状态闸门先于范围校验；count == 0 校验后
    // 直接成功；成功返回即全部字节已进入内部存储（内存目标无短写）
    pub override func write(buffer: Span\<u8>, offset: i32, count: i32) {
        ensureOpen()
        checkRange(buffer, offset, count)
        if (count == 0) {
            return
        }
        ensureCapacity((len as i64) + (count as i64))
        var i: i32 = 0
        while (i < count) {
            storage[len + i] = (buffer[offset + i] as u8)
            i = (i + 1)
        }
        len = (len + count)
    }

    // 刷新（§4.4「内存目标完成内容写入」）：成功写入即内容完成，本
    // 实现无待输出缓冲，flush 为无操作成功返回；仍先过状态闸门，
    // 成功后可继续写入
    pub override func flush() {
        ensureOpen()
    }

    // 导出独立副本（§4.4：普通内存输出流导出的 Span 是独立副本）：
    // 修改返回值不影响流内容，可重复导出；输出结果须在 dispose() 前
    // 取出——关闭后抛 core.IllegalStateException
    pub func toSpan(): Span\<u8> {
        ensureOpen()
        const result = core.collections.spanOf\<u8>(len)
        var i: i32 = 0
        while (i < len) {
            result[i] = (storage[i] as u8)
            i = (i + 1)
        }
        return result
    }

    // 收尾钩子（基类 dispose 支架保证幂等）：只进关闭态并清理自身——
    // 释放内部存储引用；内存内容无需提交，关闭后 toSpan 由 ensureOpen
    // 拒绝
    protected override func disposeCore() {
        storage = core.collections.arrayOf\<u8>(0)
        len = 0
    }

    // 确保容量至少 need 字节（need 以 i64 表达，规避 i32 加法回绕）。
    // 倍增策略：新容量取 max(旧容量*2, need)，首次从 8 起；need 超出
    // i32 可表示范围即超出 Array<u8> 存储上限，抛 core.OutOfBoundException
    //（与 List.grow 同款约束，不窄化不回绕）
    priv func ensureCapacity(need: i64) {
        if (need <= (storage.length as i64)) {
            return
        }
        if (need > (2147483647 as i64)) {
            throw new core.OutOfBoundException(
                "内存输出流容量超过 i32 可表示范围")
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
        while (i < len) {
            bigger[i] = (storage[i] as u8)
            i = (i + 1)
        }
        storage = bigger
    }
}
