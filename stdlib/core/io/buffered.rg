// Rigi 标准库：core.io 缓冲流包装器（STDLIB §4.4「已确定的内存流与缓冲
// 包装器」缓冲段 + 所有权/关闭刷新/错误边界三段契约，施工块 2-3）。
//   - BufferedInputStream：预读输入包装器——read 先消费内部缓冲，缓冲
//     空时从下层流（inner）大块补读，允许预读（inner 位置可领先于调用
//     者已消费位置）；EOF 经缓冲空 + inner 返回 0 传递；
//   - BufferedOutputStream：缓冲输出包装器——write 先复制进内部缓冲
//    （§4.4：缓冲输出流延后写出须先复制到自己的缓冲区），缓冲满自动
//     提交 inner；flush() 提交本层缓冲并逐层调 host.flush() 等其完成；
//     dispose 经基类支架先置关闭态，再由 disposeCore 完成剩余写出与
//     刷新（正常态）或跳过收尾写出（故障态不重写可能已部分提交的数据）。
// 缓冲容量可配置，默认 16 KiB（16384）。范围非法（capacity <= 0）抛
// core.OutOfBoundException——与流参数错误风格一致（负位置/范围同款）。
// 所有权（§4.4）：默认 .Borrowed，借用不因 dispose 转为拥有，inner 由
// 创建/打开的一方负责关闭；只有显式 .Owned 才在 disposeCore 中接管
// host 的关闭责任；只有构造成功才接管关闭责任——构造只做参数校验与
// 缓冲分配，先校验再分配，失败时未建立任何内部资源、无残留可泄漏。
// 使用期间不得绕过包装器操作 inner（§4.4：包装器使用期间不能绕过它
// 操作下层流——由调用者遵守的设计契约，否则预读/缓冲数据失去一致性）。
// 销毁输入包装器不自动回退预读位置，未消费的缓冲数据丢弃；关闭 reader
// 不继续读取或校验未读完的内容。
// 故障（§4.4）：inner 读写或 flush 实际失败 → markFaulted 进入故障态
// 后原样重抛；故障流只允许清理——后续 read/write/flush 由 ensureOpen
// 抛 core.IllegalStateException；故障输出流的 disposeCore 不重写可能
// 已部分提交的数据，但仍清理自身并释放拥有的下层资源。
// dispose 幂等由基类封闭支架保证：先置关闭态再调 disposeCore，收尾/
// 刷新失败也不影响对象已进入关闭态，后续 dispose 无操作、不重试。
// 两类均为 local 类型（§4.4：普通流与包装器使用 local 类型），同一
// 实例不支持并发或重入操作；传入缓冲区只在调用期间借用——延后写出的
// 部分已复制进本层缓冲，调用返回后不再访问调用者缓冲区。
// 内部缓冲用 Span<u8> 字段（spanOf 分配的连续存储，跨方法持有；Array
// 实参不能传给 Span 形参，Span 直传避免提交时的二次拷贝）。所有权用
// bool 投影保存——enum struct 等值比较须带类型注解的中间量，构造时
// 一次比较后即不再依赖枚举直比。
namespace core.io

// 缓冲输入流包装器（§4.4）：允许预读——底层流位置可能领先于调用者
// 已经消费的位置。首版不暴露定位（§4.4：缓冲流与文本流首版不直接
// 暴露定位）。
pub class BufferedInputStream : InputStream {
    // 下层输入流（借用或按所有权接管；inner 引用在 disposeCore 后不再
    // 使用——所有入口先经 ensureOpen 拒绝，引用本身随对象托管释放）
    priv const host: InputStream
    // 所有权投影：true 仅当显式 .Owned（构造时比较一次）
    priv const owned: bool
    // 预读缓冲与有效区间 [bufPos, bufEnd)；var：disposeCore 收尾时
    // 释放缓冲引用（置换为空 Span，与 memory.rg 存储释放同款）
    priv var buf: Span\<u8>
    // 下一个待消费索引（0..bufEnd）
    priv var bufPos: i32
    // 有效数据末端（0..buf.length）
    priv var bufEnd: i32

    // 构造（§4.4 所有权契约）：默认容量 16 KiB、默认借用 host。
    // 先参数校验再分配缓冲——校验失败时未建立任何内部资源，inner 仍
    // 由调用者负责（构造失败不接管关闭责任）
    pub init(target: InputStream, capacity: i32 = 16384,
             ownership: StreamWrapperOwnership = .Borrowed) {
        if (capacity <= 0) {
            throw new core.OutOfBoundException(
                "缓冲流容量非法：capacity ${capacity} 必须为正")
        }
        host = target
        // enum struct 直比须带类型注解中间量（== 右侧 .Owned 无法推断）
        const ownedCase: StreamWrapperOwnership = .Owned
        owned = (ownership == ownedCase)
        buf = core.collections.spanOf\<u8>(capacity)
        bufPos = 0
        bufEnd = 0
    }

    // 单次读取（基类契约）：先消费缓冲；缓冲空时从 inner 大块补读后
    // 再消费；inner 返回 0（EOF）且缓冲空 → 返回 0。允许返回少于
    // 请求数（§4.4：读取允许少于请求数）；count == 0 校验后返回 0，
    // 不消费输入、不证明 EOF
    pub override func read(buffer: Span\<u8>, offset: i32, count: i32): i32 {
        ensureOpen()
        checkRange(buffer, offset, count)
        if (count == 0) {
            return 0
        }
        if (bufPos == bufEnd) {
            // 缓冲空：从 inner 预读补满（EOF 时缓冲保持空）
            fill()
            if (bufPos == bufEnd) {
                return 0
            }
        }
        // 消费缓冲中现有字节（不超过请求数——允许少于请求数）
        var n: i32 = count
        if (n > (bufEnd - bufPos)) {
            n = (bufEnd - bufPos)
        }
        var i: i32 = 0
        while (i < n) {
            buffer[offset + i] = (buf[bufPos + i] as u8)
            i = (i + 1)
        }
        bufPos = (bufPos + n)
        return n
    }

    // 从 inner 大块补读一次（§4.4：缓冲输入流允许预读，底层流位置可能
    // 领先于调用者已消费位置）。允许短读——单次 read 少于剩余容量即
    // 接受，不强行读满；inner 返回 0 即 EOF（缓冲保持空）。剩余数据
    // 先压到缓冲头部腾出尾部空间。inner 实际失败 → markFaulted 后
    // 原样重抛（普通 EOF 不置故障）
    priv func fill() {
        if (bufPos > 0) {
            // 压缩：[bufPos, bufEnd) 移到 [0, bufEnd - bufPos)
            var i: i32 = 0
            while (i < (bufEnd - bufPos)) {
                buf[i] = (buf[bufPos + i] as u8)
                i = (i + 1)
            }
            bufEnd = (bufEnd - bufPos)
            bufPos = 0
        }
        if (bufEnd >= buf.length) {
            return
        }
        var n: i32 = 0
        try {
            n = host.read(buf, bufEnd, (buf.length - bufEnd))
        } catch (e: core.Exception) {
            // 实际 I/O 失败先进入故障态再抛出（§4.4 错误边界）
            markFaulted()
            throw e
        }
        if (n > 0) {
            bufEnd = (bufEnd + n)
        }
    }

    // 收尾钩子（基类 dispose 支架保证幂等——先置关闭态再调本钩子）：
    // 清理自身状态、释放缓冲引用；未消费的缓冲数据直接丢弃，不回退
    // host 的预读位置、也不再读 inner（§4.4：销毁输入包装器不自动
    // 回退预读位置）。Owned 接管 host 关闭责任；Borrowed 的 inner
    // 由创建/打开的一方负责关闭（借用不因 dispose 转为拥有）
    protected override func disposeCore() {
        bufPos = 0
        bufEnd = 0
        buf = core.collections.spanOf\<u8>(0)
        if (owned) {
            host.dispose()
        }
    }
}

// 缓冲输出流包装器（§4.4）：write 先复制进内部缓冲，缓冲满自动提交
// inner；flush() 提交本层缓冲并逐层调 host.flush() 等其完成；dispose
// 完成剩余写出与刷新后清理自身。首版不暴露定位。
pub class BufferedOutputStream : OutputStream {
    // 下层输出流（借用或按所有权接管）
    priv const host: OutputStream
    // 所有权投影：true 仅当显式 .Owned
    priv const owned: bool
    // 待输出缓冲与已缓冲字节数 [0, bufLen)；var：disposeCore 收尾时
    // 释放缓冲引用
    priv var buf: Span\<u8>
    priv var bufLen: i32
    // 故障投影：基类 dispose 支架先置 state_ = 2 再调 disposeCore，
    // 钩子内无法经 state_ 区分「正常关闭」与「故障态收尾」——用本
    // 标记记录是否发生过实际 I/O 失败（与 state_ 同步维护）
    priv var broken: bool = false

    // 构造（§4.4 所有权契约）：默认容量 16 KiB、默认借用 host。
    // 先参数校验再分配缓冲——构造失败不接管（无内部资源残留）
    pub init(target: OutputStream, capacity: i32 = 16384,
             ownership: StreamWrapperOwnership = .Borrowed) {
        if (capacity <= 0) {
            throw new core.OutOfBoundException(
                "缓冲流容量非法：capacity ${capacity} 必须为正")
        }
        host = target
        const ownedCase: StreamWrapperOwnership = .Owned
        owned = (ownership == ownedCase)
        buf = core.collections.spanOf\<u8>(capacity)
        bufLen = 0
    }

    // 单次写出（基类契约）：count == 0 校验后直接成功。缓冲装不下时
    // 自动提交已有内容再继续缓冲（先复制到自己的缓冲区再延后写出，
    // §4.4）；成功返回只表示字节已进入本层缓冲或已提交 inner，不隐含
    // flush——需要下层完成时显式 flush()
    pub override func write(buffer: Span\<u8>, offset: i32, count: i32) {
        ensureOpen()
        checkRange(buffer, offset, count)
        if (count == 0) {
            return
        }
        if (count >= buf.length) {
            // 大块直传：先提交已缓冲内容，再绕过缓冲直接写 inner——
            // 避免大请求的双重复制（直传部分无延后写出，无需复制）
            if (bufLen > 0) {
                submit()
            }
            try {
                host.write(buffer, offset, count)
            } catch (e: core.Exception) {
                broken = true
                markFaulted()
                throw e
            }
            return
        }
        // 小块：装不下时先提交腾空（count < buf.length，提交后必装得下）
        if ((bufLen + count) > buf.length) {
            submit()
        }
        var i: i32 = 0
        while (i < count) {
            buf[bufLen + i] = (buffer[offset + i] as u8)
            i = (i + 1)
        }
        bufLen = (bufLen + count)
    }

    // 刷新（§4.4 关闭与刷新契约）：提交本层缓冲，再逐层调用
    // host.flush() 并等待其完成（不排队即返回）；成功后仍可继续写入，
    // 不结束流也不关闭流。本层提交失败时已置故障并传播，不再调
    // host.flush（本层内容未落地，下层刷新无意义）
    pub override func flush() {
        ensureOpen()
        submit()
        try {
            host.flush()
        } catch (e: core.Exception) {
            broken = true
            markFaulted()
            throw e
        }
    }

    // 提交本层缓冲到 inner（不含 inner 的 flush——层次职责分离，逐层
    // 刷新由 flush/dispose 路径负责）。成功即清空缓冲；失败先
    // markFaulted 再原样重抛，缓冲内容保留不重试（故障后只允许清理，
    // 可能已部分提交的数据不重写）
    priv func submit() {
        if (bufLen == 0) {
            return
        }
        const n = bufLen
        try {
            host.write(buf, 0, n)
            bufLen = 0
        } catch (e: core.Exception) {
            broken = true
            markFaulted()
            throw e
        }
    }

    // 收尾钩子（基类 dispose 支架保证幂等——支架已先置关闭态，即使
    // 本钩子抛出，后续 dispose 也无操作）。正常态（无故障投影）：完成
    // 剩余写出与刷新（提交本层缓冲 + host.flush 逐层完成）后清理自身；
    // 故障态：不重写可能已部分提交的数据，跳过收尾写出但仍清理自身
    //（§4.4：故障输出流的 dispose 不重写数据、不免除清理责任）。首次
    // 收尾的写出或刷新失败不得跳过其余必要清理——先完成自身清理与
    // Owned 的 host 关闭，再原样重抛向调用者报告失败；关闭状态不表示
    // 失败的写出已成功
    protected override func disposeCore() {
        if (broken == false) {
            try {
                submit()
                host.flush()
            } catch (e: core.Exception) {
                // 收尾失败：置故障投影，其余清理照常执行后重抛
                broken = true
                markFaulted()
                releaseSelf()
                if (owned) {
                    host.dispose()
                }
                throw e
            }
        }
        releaseSelf()
        if (owned) {
            host.dispose()
        }
    }

    // 清理自身：释放缓冲引用并清空计数（幂等，收尾失败路径与正常路径
    // 均会调用）
    priv func releaseSelf() {
        buf = core.collections.spanOf\<u8>(0)
        bufLen = 0
    }
}
