// Rigi 标准库：core.io 流核心抽象（STDLIB §4.4，施工块 2-1）。
// 本文件建抽象层与公共 Helper；具体内存流/AutoBuffer 见同目录
// memory.rg/autobuffer.rg（施工块 2-2），缓冲包装/标准流属后续块，
// TextReader/TextWriter 文本适配器属阶段 3，均不在此。
// InputStream 另含整体读取便利 readAll 双重载（§4.4，施工块 2-2）：
// 经 AutoBuffer 收集后导出单个独立 Span<u8>，超上限抛
// core.OutOfBoundException，不返回静默截断结果。
//   - EndOfStreamException：精确读取提前 EOF 专用异常；
//   - StreamWrapperOwnership：包装器所有权枚举（后续包装器块经构造参数
//     传入，默认 .Borrowed）；
//   - InputStream/OutputStream：可由用户继承实现的抽象类（均实现
//     core.IDisposable）——单次读写的契约由子类实现承担，精确读取/
//     单字节便利/整 Span 重载/pipe 由基类组合底层抽象成员完成（§4.4
//     第一段：「通用精确读取和流传输由基础实现组合底层读写完成」）；
//   - ISeekableStream：按能力实现的定位接口。
//
// §4.4 已确定的通用契约（各成员注释逐条引用，要点汇总）：
// - 传入的缓冲区只在调用期间借用（包括挂起期间）；正常返回或抛出异常
//   后，不再通过后台操作访问该缓冲区；调用者在操作完成前不得修改或
//   复用相关范围；
// - read/write 均为普通 func，不声明 async、不返回 Task；内部等待可
//   挂起当前协程并释放 Worker，恢复后继续同一次调用，不另建协程；
// - 同一流实例不支持并发或重入操作（实现者必须遵循的设计契约，调用
//   挂起期间仍属于同一次操作）；
// - read/write 失败不保证回滚已消费或已写出的字节；
// - 单次操作的 offset/count 与 read 返回的实际读取字节数用 i32；累计
//   字节数与流位置用 i64，与单次缓冲区索引分开；
// - 流区分正常/故障/关闭三态：参数校验失败、普通 EOF、readExactly 的
//   提前 EOF 不使流进入故障状态；实际 I/O 或编解码失败后进入故障状态，
//   只允许清理；关闭后读、写或 flush 抛状态异常。范围错误用
//   core.OutOfBoundException，关闭或故障状态用 core.IllegalStateException。
// 状态机用 i32 投影（0 正常/1 故障/2 关闭；coroutine stateCode 同款——
// enum struct 等值比较开销/形态未定型，不引入状态枚举）。
namespace core.io

// 精确读取提前 EOF（§4.4）：携带本次请求字节数与实际已读字节数（均
// i32），消息模板风格沿用 exceptions.rg（MW9b：动态部分字符串插值）。
// 异常自持显式 init（Rigi 无 super 构造语法——init 体直接赋值继承字段
// message）；getMessage 继承 core.IOException 的实现（返回 message）。
pub class EndOfStreamException : core.IOException {
    // 本次精确读取请求的总字节数
    pub const requested: i32
    // 抛出时实际已读入缓冲区的字节数（部分结果保留在缓冲区，不当成功）
    pub const actual: i32

    pub init(requestedCount: i32, actualCount: i32) {
        message = "已到达流末尾：本次请求 ${requestedCount} 字节，实际已读 ${actualCount} 字节"
        requested = requestedCount
        actual = actualCount
    }
}

// 包装器所有权枚举（§4.4 所有权契约）：后续缓冲流包装器/文本适配器经
// 构造参数传入，默认 .Borrowed；只有显式 .Owned 才接管下层流的关闭
// 责任。只有构造成功才接管关闭责任。本块仅落枚举本身备用。
pub enum struct StreamWrapperOwnership {}[
    Borrowed,
    Owned
]

// 定位接口（§4.4）：字节位置用 i64（累计字节数/流位置与单次缓冲区索引
// i32 分开）。只有 seek 与 getPosition，不把长度查询塞入此接口——通用
// 流及本接口不提供 setLength，文件流按 §4.5 另行提供。基础输入/输出流
// 不强制实现它，文件流和内存流按具体能力实现。可定位流允许定位到末尾
// 之后（定位本身不扩容，后续写入时空隙补零）；负位置或不可表示的位置
// 报错——均由实现负责。
pub interface ISeekableStream {
    // 定位到字节位置 position（i64）
    func seek(position: i64)
    // 当前字节位置（i64）
    func getPosition(): i64
}

// 输入流抽象基类（§4.4）：可由用户继承实现。抽象面只有三参 read 与
// 子类收尾钩子 disposeCore；状态闸门/范围校验/精确读取/单字节与整 Span
// 便利/pipe 由基类提供。dispose 幂等支架在基类（封闭 override，子类不
// 覆写 dispose、只实现 disposeCore）。
//
// 对子类的实现契约（§4.4）：
// - 三参 read 是普通 func（不声明 async、不返回 Task）；内部等待可挂起
//   当前协程并释放 Worker，恢复后继续同一次调用，不另建协程；
// - 实现首先 ensureOpen()，随后 checkRange(buffer, offset, count) 复用
//   基类校验（count == 0 校验通过后返回 0——不消费输入、也不证明 EOF；
//   count > 0 时返回实际读取字节数，允许少于请求数，返回 0 表 EOF；
//   暂时无数据应挂起等待，不用 0 表示「稍后再试」）；
// - 同一流实例不支持并发或重入；read 失败不回滚已消费的字节；
// - 传入缓冲区只在调用期间借用（含挂起期间），实现不得在调用结束后
//   继续持有并访问它；
// - 实际 I/O 失败先 markFaulted() 再抛出（故障后只允许清理，不继续
//   读写）；普通 EOF（返回 0）与参数校验失败不置故障；
// - disposeCore 收尾钩子：清理自身资源；故障态也照常调用（故障流的
//   dispose 只清理，不重写可能已部分提交的数据）；借用下层流的实现
//   不在此关闭下层流。幂等由基类 dispose 支架保证——支架先置关闭态
//   再调钩子，钩子抛异常后后续 dispose 仍无操作。
pub abstract class InputStream implements core.IDisposable {
    // 状态三态 i32 投影：0 正常 / 1 故障 / 2 关闭。带字段初值——抽象
    // 基类无 init（子类无从调用 super 构造），初值免除子类清零负担
    protected var state_: i32 = 0

    // 单次读取：把最多 count 字节写入 buffer[offset..offset+count)，
    // 返回实际读取字节数（i32）；count > 0 时返回 0 表 EOF。完整契约
    // 见类头「对子类的实现契约」。
    pub abstract func read(buffer: Span\<u8>, offset: i32, count: i32): i32

    // ── 状态助手（protected，供所有实现复用）──

    // 流处于正常态才允许继续；关闭或故障一律抛 core.IllegalStateException
    //（§4.4：关闭或故障状态使用该异常；故障后只允许清理）
    protected func ensureOpen() {
        if (state_ == 2) {
            throw new core.IllegalStateException("流已关闭")
        }
        if (state_ == 1) {
            throw new core.IllegalStateException("流已进入故障状态，只允许清理")
        }
    }

    // 实际 I/O 或编解码失败后由实现调用：进入故障态，此后只允许清理，
    // 不继续读写、定位或刷新
    protected func markFaulted() {
        state_ = 1
    }

    // 关闭态标记（dispose 支架内部使用；实现一般不直接调用）
    protected func markClosed() {
        state_ = 2
    }

    // 范围校验（§4.4：offset/count 非负且在范围内，检查加法溢出；零
    // 长度不绕过范围验证）。溢出检查用减法形式：count 非负前提下
    // buffer.length - offset 不溢出，count 大于该差值即涵盖 offset +
    // count 回绕为负的全部情形。校验失败抛 core.OutOfBoundException；
    // 参数校验失败不使流进入故障状态。
    protected func checkRange(buffer: Span\<u8>, offset: i32, count: i32) {
        if ((offset < 0) or (offset > buffer.length)) {
            throw new core.OutOfBoundException(
                "流缓冲区范围非法：offset ${offset} 超出 [0, ${buffer.length}]")
        }
        if ((count < 0) or (count > (buffer.length - offset))) {
            throw new core.OutOfBoundException(
                "流缓冲区范围非法：offset ${offset} + count ${count} 超出缓冲区长度 ${buffer.length}")
        }
    }

    // ── 公共便利面（组合底层 read 完成）──

    // 整 Span 重载 = read(buffer, 0, buffer.length)
    pub func read(buffer: Span\<u8>): i32 {
        return read(buffer, 0, buffer.length)
    }

    // 单字节读取：null 表 EOF。每次取 1 字节有界缓冲（便利入口而非
    // 热路径——抽象基类不引入实例缓冲，避免实现背负状态）
    pub func readByte(): u8? {
        const one = core.collections.spanOf\<u8>(1)
        const n = read(one, 0, 1)
        if (n == 0) {
            return null
        }
        return (one[0] as u8)
    }

    // 精确读取（§4.4）：循环调用三参 read 填满 buffer[offset..
    // offset+count) 后成功返回，无返回值。填满前遇 EOF 抛
    // EndOfStreamException（携带本次请求/实际已读字节数），不把部分
    // 结果当作成功；失败保留已读入缓冲区的数据，不回退流位置；其他
    // I/O 错误原样传播，不改报为 EOF。count == 0 仍校验参数与流状态，
    // 通过后直接成功、不消费输入。提前 EOF 不使流进入故障状态
    //（EOF 不是故障，流随后仍可继续读取到下一次 EOF 或被关闭）。
    pub func readExactly(buffer: Span\<u8>, offset: i32, count: i32) {
        // 状态闸门先于范围校验：关闭/故障后一律拒绝
        ensureOpen()
        checkRange(buffer, offset, count)
        var total: i32 = 0
        while (total < count) {
            const n = read(buffer, offset + total, count - total)
            if (n == 0) {
                throw new EndOfStreamException(count, total)
            }
            total = total + n
        }
    }

    // 精确读取整 Span 重载
    pub func readExactly(buffer: Span\<u8>) {
        readExactly(buffer, 0, buffer.length)
    }

    // 流传输（§4.4：命名为 pipe，不使用 copyTo）：8 KiB 有界缓冲传输
    // 到 EOF，成功返回累计 i64 字节数。不关闭任何一端，也不自动调用
    // target 的 flush()；失败保留已发生的读写，不回滚；不支持源和目标
    // 实际指向同一条流的传输（基类不检测别名，由调用者遵守契约）。
    pub func pipe(target: OutputStream): i64 {
        ensureOpen()
        const buf = core.collections.spanOf\<u8>(8192)
        var total: i64 = (0 as i64)
        while (true) {
            const n = read(buf, 0, buf.length)
            if (n == 0) {
                break
            }
            target.write(buf, 0, n)
            // 累计字节数用 i64（与单次缓冲区索引 i32 分开，§4.4）
            total = total + (n as i64)
        }
        return total
    }

    // 整体读取（§4.4：readAll 直接返回的字节结果只使用 Span<u8>）：
    // 经 AutoBuffer 循环 read 到 EOF 收集，再导出单个独立 Span<u8>。
    // 未指定上限时受返回容器容量上限约束（Span 长度 i32；AutoBuffer
    // 存储同为 i32 约束），超限抛 core.OutOfBoundException——不承诺
    // 任意大结果都能整体装入内存，需要动态扩容时直接用 AutoBuffer。
    // 读取中途 I/O 故障原样传播，不回滚已读（AutoBuffer 与视图均为
    // local 托管对象，无系统资源，异常路径不收尾由内存管理兜底）。
    pub func readAll(): Span\<u8> {
        const buf = new AutoBuffer()
        const out = buf.getOutputStream()
        readAllCollect(out, (0 as i64), false)
        out.dispose()
        return readAllExport(buf)
    }

    // 整体读取带上限重载（§4.4：允许指定 maxBytes，超限抛错，不返回
    // 静默截断结果）：累计超过 maxBytes 即抛 core.OutOfBoundException
    //（超出声明上限按范围错误处理）。maxBytes 为流实际字节数时精确
    // 读全——maxBytes 不是截断长度
    pub func readAll(maxBytes: i32): Span\<u8> {
        const buf = new AutoBuffer()
        const out = buf.getOutputStream()
        readAllCollect(out, (maxBytes as i64), true)
        out.dispose()
        return readAllExport(buf)
    }

    // readAll 公共收集路径：4 KiB 有界缓冲循环 read 到底层 EOF，逐段
    // 写入 target，返回累计 i64 字节数。enforceLimit 为真时累计超过
    // limit 即抛 core.OutOfBoundException（先查后写，超限段不进入
    // 收集缓冲）；count == 0 返回 0 的 EOF 语义由底层 read 契约承担
    priv func readAllCollect(target: OutputStream, limit: i64, enforceLimit: bool): i64 {
        const chunk = core.collections.spanOf\<u8>(4096)
        var total: i64 = (0 as i64)
        while (true) {
            const n = read(chunk, 0, chunk.length)
            if (n == 0) {
                break
            }
            total = total + (n as i64)
            if (enforceLimit) {
                if (total > limit) {
                    throw new core.OutOfBoundException(
                        "readAll 累计 ${total} 字节超过声明上限 ${limit}")
                }
            }
            target.write(chunk, 0, n)
        }
        return total
    }

    // readAll 公共导出路径：AutoBuffer.count 为 i64，Span 容量上限
    // i32——超限抛 core.OutOfBoundException；导出走独立输入视图的
    // 精确读取（填不满即流被并发改写，按 readExactly 契约报错），
    // 结果是与收集内容独立的单个 Span<u8>
    priv func readAllExport(buf: AutoBuffer): Span\<u8> {
        const total = buf.count
        if (total > (2147483647 as i64)) {
            throw new core.OutOfBoundException(
                "readAll 结果 ${total} 字节超过 Span 容量上限 2147483647")
        }
        const result = core.collections.spanOf\<u8>((total as i32))
        const view = buf.getInputStream()
        view.readExactly(result, 0, (total as i32))
        view.dispose()
        return result
    }

    // dispose 幂等支架（§4.4：dispose 幂等——首次调用完成后，即使收尾
    // 抛出异常，对象仍进入关闭状态；后续 dispose 无操作，不重试释放）。
    // 封闭 override：子类不得覆写 dispose，只实现 disposeCore 钩子，
    // 保证幂等语义不被绕过。先置关闭态再调钩子，钩子异常后的第二次
    // dispose 直接返回
    pub override func dispose() {
        if (state_ == 2) {
            return
        }
        state_ = 2
        disposeCore()
    }

    // 子类收尾钩子：清理自身资源（故障态也照常调用）；借用下层流的
    // 实现不在此关闭下层流（§4.4 所有权契约：借用不因 dispose 转为
    // 拥有，下层流由创建/打开的一方负责关闭）
    protected abstract func disposeCore()
}

// 输出流抽象基类（§4.4）：与 InputStream 对称，可由用户继承实现。抽象
// 面只有三参 write、flush 与收尾钩子 disposeCore；状态闸门/范围校验/
// 整 Span 重载/单字节便利由基类提供。
//
// 对子类的实现契约（§4.4）：
// - write(buffer, offset, count) 无返回值，成功返回即写完全部请求范围；
//   底层短写由实现继续完成（内部循环补齐，不得部分返回）；失败不保证
//   回滚已写出的字节；
// - flush() 成功返回表示此前写入已经真正完成——提交本层待输出缓冲，
//   逐层调用下层输出流的 flush() 并等待全部完成，不能仅把任务排入后台
//   队列后返回；成功后仍可继续写入，不结束流，也不关闭流；
// - 三参 write 与 flush 均为普通 func（不声明 async、不返回 Task），
//   内部等待可挂起当前协程，恢复后继续同一次调用；
// - 实现首先 ensureOpen()，随后 checkRange(buffer, offset, count) 复用
//   基类校验（count == 0 校验通过后直接成功，不写出任何字节）；同一
//   流实例不支持并发或重入；
// - 传入缓冲区只在调用期间借用（含挂起期间）；缓冲输出流若延后写出，
//   先复制到自己的缓冲区；
// - 实际 I/O 失败先 markFaulted() 再抛出；参数校验失败不置故障；
// - disposeCore 收尾钩子：完成剩余写出和刷新后清理自身；故障态也照常
//   调用，但不重写可能已经部分提交的数据；借用下层流的实现不在此关闭
//   下层流。幂等由基类 dispose 支架保证（同 InputStream）。
pub abstract class OutputStream implements core.IDisposable {
    // 状态三态 i32 投影：0 正常 / 1 故障 / 2 关闭（与 InputStream 同款）
    protected var state_: i32 = 0

    // 单次写出：把 buffer[offset..offset+count) 全部写出。无返回值，
    // 成功返回即写完全部请求范围；底层短写由实现继续完成。完整契约
    // 见类头「对子类的实现契约」。
    pub abstract func write(buffer: Span\<u8>, offset: i32, count: i32)

    // 刷新：成功返回表示此前写入已经真正完成（含逐层下层 flush 完成）；
    // 成功后仍可继续写入，不结束流，也不关闭流
    pub abstract func flush()

    // ── 状态助手（protected，与 InputStream 对称）──

    // 流处于正常态才允许继续；关闭或故障一律抛 core.IllegalStateException
    protected func ensureOpen() {
        if (state_ == 2) {
            throw new core.IllegalStateException("流已关闭")
        }
        if (state_ == 1) {
            throw new core.IllegalStateException("流已进入故障状态，只允许清理")
        }
    }

    // 实际 I/O 或编解码失败后由实现调用：进入故障态，此后只允许清理，
    // 不继续读写、定位或刷新
    protected func markFaulted() {
        state_ = 1
    }

    // 关闭态标记（dispose 支架内部使用；实现一般不直接调用）
    protected func markClosed() {
        state_ = 2
    }

    // 范围校验（同 InputStream.checkRange：减法形式规避 i32 加法溢出；
    // 校验失败抛 core.OutOfBoundException，不置故障）
    protected func checkRange(buffer: Span\<u8>, offset: i32, count: i32) {
        if ((offset < 0) or (offset > buffer.length)) {
            throw new core.OutOfBoundException(
                "流缓冲区范围非法：offset ${offset} 超出 [0, ${buffer.length}]")
        }
        if ((count < 0) or (count > (buffer.length - offset))) {
            throw new core.OutOfBoundException(
                "流缓冲区范围非法：offset ${offset} + count ${count} 超出缓冲区长度 ${buffer.length}")
        }
    }

    // ── 公共便利面（组合底层 write 完成）──

    // 整 Span 重载 = write(buffer, 0, buffer.length)
    pub func write(buffer: Span\<u8>) {
        write(buffer, 0, buffer.length)
    }

    // 单字节写出（便利入口而非热路径——抽象基类不引入实例缓冲）
    pub func writeByte(b: u8) {
        const one = core.collections.spanOf\<u8>(1)
        one[0] = b
        write(one, 0, 1)
    }

    // dispose 幂等支架（同 InputStream：封闭 override + disposeCore
    // 钩子，先置关闭态再收尾，后续 dispose 无操作）。正常状态的输出流
    // 在 dispose 中完成剩余写出和刷新（由 disposeCore 钩子实现承担）
    pub override func dispose() {
        if (state_ == 2) {
            return
        }
        state_ = 2
        disposeCore()
    }

    // 子类收尾钩子：完成剩余写出和刷新，随后清理自身资源（故障态也
    // 照常调用，但不重写可能已部分提交的数据）；借用下层流的实现不在
    // 此关闭下层流
    protected abstract func disposeCore()
}
