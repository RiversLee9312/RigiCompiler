// Rigi 标准库：core.io 文本流适配器（STDLIB §4.4「已确定的文本流契约」
// +「已确定的关闭与刷新契约」+「已确定的所有权契约」，施工块 3-6）。
//   - TextReader：InputStream → 文本的读取适配器——readChar/readLine/
//     readToEnd 三入口，内部经 core.text.Utf8Decoder 增量解码；
//   - TextWriter：OutputStream ← 文本的写出适配器——write/writeLine/
//     flush，经 core.text.Utf8Encoder 编码。
//
// 语义裁决（§4.4 文本流段逐条落实）：
//   - 首版内置 UTF-8 一种编码：decoder/encoder 为构造参数，null 即默认
//     （decoder 默认严格模式，替换模式由调用者显式构造
//     Utf8Decoder(replacementOnError: true) 传入）；不自动猜测其他编码；
//     编码配置固定于对象生命周期，不在中途切换；
//   - 读取默认识别并跳过流开头的一个 UTF-8 BOM（EF BB BF）——字节级
//     判定后丢弃，不交给解码器；写入默认不生成 BOM；
//   - readChar 一次一个完整 Unicode 标量（char 32 位标量，§4.3.1），
//     EOF 返回 null；readLine 返回不含换行符的 String——空行返回空串、
//     没有任何剩余内容才返回 null、末尾无换行的最后一行正常返回；
//     行尾识别 LF、CRLF、单独 CR（CRLF 在解码后的标量级判定，跨缓冲块
//     /跨底层读切分天然正确；单个 CR 后预读一个标量，非 LF 则推回）；
//   - readToEnd 读取剩余全部文本；maxBytes 上限按结果 String 的 UTF-8
//     字节数计（逐标量宽度累计，先查后加），超限抛
//     core.OutOfBoundException，不静默截断；maxBytes 为负同样抛范围异常
//     （上限声明本身非法）；默认重载无上限（仍受 StringBuilder 的
//     i64 累计与 i32 单结果容量守卫约束）；
//   - write 完整 String 按配置编码写出，不隐含 flush/关闭/截断/追加
//     定位（定位是下层流打开模式的事）；writeLine 默认输出 LF，
//     newLineCrlf=true 切 CRLF；不因写了一行就自动刷新；
//   - flush() 提交本层并逐层调 host.flush() 等其完成（本层无待输出
//     缓冲，提交即直达 host 写出，刷新语义由 host 链承担）；
//   - 所有权：默认 .Borrowed，Borrowed 的 dispose 不关闭 host；显式
//     .Owned 才在收尾时接管 host 关闭责任；只有构造成功才接管；
//   - 收尾幂等：dispose 先置关闭态再收尾，收尾失败仍进入关闭态、
//     后续 dispose 无操作；正常态 dispose 结束编码（本块编码器
//     Utf8Encoder 无状态、无尾部字节，「结束编码」为空操作——注释
//     钉死此事实）并刷新下层流后清理自身；故障态（broken 投影）
//     不重写可能已部分提交的数据，跳过收尾刷新但仍清理自身并释放
//     Owned 的下层资源，失败向调用者原样重抛；
//   - 编解码或下层 I/O 实际失败 → 故障态，此后只允许清理（读/写/
//     flush 抛 core.IllegalStateException）；严格模式非法 UTF-8 抛
//     core.text.TextFormatException，替换模式得 U+FFFD（decoder 语义）。
//
// 边界与形态：
//   - 文本适配器不接受 Path、不负责打开/创建/截断文件或选择追加
//     （§4.4：便利能力不接受 Path——由下层流及其打开模式决定）；
//   - local 类型，同一实例不支持并发或重入操作；关闭后操作抛
//     core.IllegalStateException；
//   - 读取允许预读：内部 4 KiB 字节缓冲 + 解码标量队列使 host 位置
//     可领先于调用者已消费位置；dispose 丢弃未消费缓冲（不回退
//     host 预读位置，§4.4 缓冲包装器同款契约），Borrowed 不关闭
//     host，Owned 关闭 host；
//   - 状态三态 i32 投影（0 正常/1 故障/2 关闭，与 stream.rg 基类
//     同款；本类不继承流基类——文本适配器不是字节流，只实现
//     core.IDisposable 并自带同构支架）。
namespace core.io

// 文本读取适配器（§4.4）：InputStream → String/char 的解码读取面。
// local 类型，不支持并发或重入。
pub class TextReader implements core.IDisposable {
    // 下层输入流（借用或按所有权接管）
    priv const host: InputStream
    // 所有权投影：true 仅当显式 .Owned（构造时比较一次，enum struct
    // 直比须带类型注解中间量——buffered.rg 同款）
    priv const owned: bool
    // UTF-8 解码器（构造固定：null 即默认严格模式；替换模式由调用者
    // 显式构造传入；本对象生命周期内不切换）
    priv const decoder: core.text.Utf8Decoder
    // 字节预读缓冲（4 KiB 固定；var：disposeCore 收尾时释放引用）
    priv var buf: Span\<u8>
    // 有效数据区间 [bufPos, bufEnd)（首块后恒从 0 起——解码窗口是
    // 按有效长度新建的右尺寸 Span，见 refill）
    priv var bufPos: i32
    priv var bufEnd: i32
    // BOM 判定是否已完成（首读时惰性做一次）
    priv var bomChecked: bool
    // 底层 EOF 已确认（含 isFinal=true 的最终解码已执行）
    priv var eofSeen: bool
    // 状态三态 i32 投影：0 正常 / 1 故障 / 2 关闭
    priv var state_: i32 = 0
    // 已解码待消费标量串与下一个待消费标量索引（i64 标量序；
    // 解码按块产出整串，逐标量分发）
    priv var pendingText: String
    priv var pendingIdx: i64
    // 单标量推回槽（readLine 的 CR 预读用：CR 后预读一个标量，非
    // LF 则推回留给下一次读取）
    priv var hasPushback: bool
    priv var pushbackChar: char

    // 构造（§4.4 所有权契约）：默认借用 host；decoderOpt 为 null 即默认
    // 严格 UTF-8 解码器。构造只做引用绑定与状态清零——无内部资源
    // 建立，失败（无失败路径）不接管；编码配置自此固定
    pub init(source: InputStream, decoderOpt: core.text.Utf8Decoder? = null,
             ownership: StreamWrapperOwnership = .Borrowed) {
        host = source
        decoder = (decoderOpt if? new core.text.Utf8Decoder())
        const ownedCase: StreamWrapperOwnership = .Owned
        owned = (ownership == ownedCase)
        buf = core.collections.spanOf\<u8>(4096)
        bufPos = 0
        bufEnd = 0
        bomChecked = false
        eofSeen = false
        pendingText = ""
        pendingIdx = (0 as i64)
        hasPushback = false
        pushbackChar = (0 as char)
    }

    // ── 状态助手（与 stream.rg 基类同构；本类自带支架）──

    // 正常态才允许读取；关闭或故障一律抛 core.IllegalStateException
    //（§4.4：关闭或故障状态使用该异常；故障后只允许清理）
    priv func ensureUsable() {
        if (state_ == 2) {
            throw new core.IllegalStateException("文本读取器已关闭")
        }
        if (state_ == 1) {
            throw new core.IllegalStateException("文本读取器已进入故障状态，只允许清理")
        }
    }

    // 编解码或下层 I/O 实际失败后调用：进入故障态，此后只允许清理
    priv func markFaulted() {
        state_ = 1
    }

    // 单标量读取（§4.4）：一次一个完整 Unicode 标量，EOF 返回 null。
    // 严格模式非法/截断 UTF-8 抛 core.text.TextFormatException（故障
    // 化后传播）；替换模式得 U+FFFD
    pub func readChar(): char? {
        ensureUsable()
        if (hasPushback) {
            hasPushback = false
            return pushbackChar
        }
        return nextScalar()
    }

    // 单行读取（§4.4）：返回不含换行符的 String；空行返回空串；没有
    // 任何剩余内容时才返回 null；末尾没有换行的最后一行正常返回。
    // 行尾识别 LF、CRLF、单独 CR——判定在解码后的标量级进行，CRLF
    // 跨块/跨底层读切分天然正确；CR 后预读一个标量确认为 LF 才消费，
    // 否则推回
    pub func readLine(): String? {
        return readLineCore((0 as i64), false)
    }

    // 上限按返回正文的 UTF-8 字节数计；非法负上限先拒绝，不能消费输入。
    pub func readLine(maxBytes: i64): String? {
        if (maxBytes < (0 as i64)) {
            throw new core.OutOfBoundException(
                "readLine 上限非法：maxBytes ${maxBytes} 为负")
        }
        return readLineCore(maxBytes, true)
    }

    // 无参和带上限共用换行/EOF 路径；超限为范围异常，不使读取器故障化。
    // 换行先于宽度检查，LF、CRLF、CR 均不属于结果正文。
    priv func readLineCore(limit: i64, enforceLimit: bool): String? {
        ensureUsable()
        const sb = new core.text.StringBuilder()
        var scanning = true
        while (scanning) {
            const got = readChar()
            if (got == null) {
                scanning = false
            } else {
                const c: char = got
                if (c == (10 as char)) {
                    return sb.toString()
                }
                if (c == (13 as char)) {
                    const nx = readChar()
                    if (nx != null) {
                        const nc: char = nx
                        if (nc != (10 as char)) {
                            // 单独 CR：预读标量非 LF，推回留给后续读取
                            hasPushback = true
                            pushbackChar = nc
                        }
                    }
                    return sb.toString()
                }
                if (enforceLimit) {
                    const w: i64 = (textScalarWidth(c) as i64)
                    // limit 非负且 w 至多 4；减法先于累计，避免 i64 加法溢出。
                    if (sb.length > (limit - w)) {
                        throw new core.OutOfBoundException(
                            "readLine 累计超过声明上限 ${limit} 字节")
                    }
                }
                sb.append(c)
            }
        }
        // EOF：累计了内容 → 末尾无换行的最后一行；空 → 没有任何
        // 剩余内容，返回 null（空行返回空串由「读到行尾符即返回」
        // 分支承担，不会落到这里）
        if (sb.length == (0 as i64)) {
            return null
        }
        return sb.toString()
    }

    // 整体读取（§4.4）：读取剩余全部文本，无上限重载（受
    // StringBuilder 累计 i64 与单结果 i32 容量守卫约束——与 readAll
    // 「不承诺任意大结果都能整体装入内存」同口径）
    pub func readToEnd(): String {
        return readToEndCore((0 as i64), false)
    }

    // 整体读取带上限重载（§4.4：上限按结果 String 的 UTF-8 字节数
    // 计，超限抛 core.OutOfBoundException，不静默截断；maxBytes 不是
    // 截断长度——恰等于结果字节数时精确读全）。maxBytes 为负即上限
    // 声明非法，同样抛范围异常
    pub func readToEnd(maxBytes: i64): String {
        if (maxBytes < (0 as i64)) {
            throw new core.OutOfBoundException(
                "readToEnd 上限非法：maxBytes ${maxBytes} 为负")
        }
        return readToEndCore(maxBytes, true)
    }

    // readToEnd 公共路径：逐标量读取追加到 StringBuilder，enforceLimit
    // 为真时按标量 UTF-8 宽度先查后加，累计超限即抛（超出段不进入
    // 结果；标量宽度按码点直接计算，与 String.length 字节口径一致）
    priv func readToEndCore(limit: i64, enforceLimit: bool): String {
        ensureUsable()
        const sb = new core.text.StringBuilder()
        var scanning = true
        while (scanning) {
            const got = readChar()
            if (got == null) {
                scanning = false
            } else {
                const c: char = got
                if (enforceLimit) {
                    const w: i64 = (textScalarWidth(c) as i64)
                    if ((sb.length + w) > limit) {
                        throw new core.OutOfBoundException(
                            "readToEnd 累计超过声明上限 ${limit} 字节")
                    }
                }
                sb.append(c)
            }
        }
        return sb.toString()
    }

    // dispose（§4.4：dispose 幂等——首次调用完成后对象进入关闭状态，
    // 后续无操作）：丢弃未消费缓冲（含字节预读与已解码标量队列），
    // 不回退 host 预读位置、不再读 host；Borrowed 不关闭 host，Owned
    // 接管 host 关闭责任（借用不因 dispose 转为拥有）
    pub override func dispose() {
        if (state_ == 2) {
            return
        }
        state_ = 2
        buf = core.collections.spanOf\<u8>(0)
        bufPos = 0
        bufEnd = 0
        pendingText = ""
        pendingIdx = (0 as i64)
        hasPushback = false
        if (owned) {
            host.dispose()
        }
    }

    // ── 内部：标量供给 ──

    // 下一个解码标量（无推回路径）：从标量队列取，队空则补充解码块；
    // 底层 EOF 后执行 isFinal=true 最终解码（严格模式截断残段在此抛
    // TextFormatException；替换模式得最后一个 U+FFFD），随后恒 null
    priv func nextScalar(): char? {
        var scanning = true
        while (scanning) {
            if (pendingIdx < pendingText.characterCount) {
                const ch = (pendingText.characterAt(pendingIdx) if? (0 as char))
                pendingIdx = (pendingIdx + (1 as i64))
                return ch
            }
            if (eofSeen) {
                scanning = false
            } else {
                refill()
            }
        }
        return null
    }

    // 补充一个解码块：从 host 读入字节（首块先做 BOM 判定），按当前
    // 有效长度建右尺寸窗口 Span 交给解码器增量解码。host 实际 I/O
    // 失败或解码失败先 markFaulted 再原样重抛（§4.4 错误边界：实际
    // I/O 或编解码失败进入故障态；普通 EOF 不置故障）
    priv func refill() {
        var n: i32 = 0
        if (bomChecked == false) {
            bomChecked = true
            n = firstFill()
        } else {
            try {
                n = host.read(buf, 0, buf.length)
            } catch (e: core.Exception) {
                markFaulted()
                throw e
            }
        }
        if (n == 0) {
            // 底层 EOF：执行最终解码（截断残段处置），此后恒 EOF
            eofSeen = true
            try {
                pendingText = decoder.decode(core.collections.spanOf\<u8>(0), true)
            } catch (e: core.Exception) {
                markFaulted()
                throw e
            }
            pendingIdx = (0 as i64)
            return
        }
        // 解码窗口：有效字节拷入右尺寸 Span（decoder 消费整 Span，
        // 基类缓冲不支持窗口视图——按有效长度新建，解码器不保留它）
        const window = core.collections.spanOf\<u8>(n)
        var i: i32 = 0
        while (i < n) {
            window[i] = (buf[bufPos + i] as u8)
            i = (i + 1)
        }
        bufPos = 0
        bufEnd = 0
        try {
            pendingText = decoder.decode(window, false)
        } catch (e: core.Exception) {
            markFaulted()
            throw e
        }
        pendingIdx = (0 as i64)
    }

    // 首块填充 + BOM 判定（§4.4：读取默认识别并跳过流开头的一个
    // UTF-8 BOM）：先至多取 3 字节判定 EF BB BF（恰好 3 字节且匹配
    // 才跳过——不足 3 字节按无 BOM 处理，残段照常交给解码器），剩余
    // 字节落入缓冲后再续读首块。BOM 判定只在流开头做一次
    priv func firstFill(): i32 {
        const stage = core.collections.spanOf\<u8>(3)
        var got: i32 = 0
        while (got < 3) {
            var n: i32 = 0
            try {
                n = host.read(stage, got, (3 - got))
            } catch (e: core.Exception) {
                markFaulted()
                throw e
            }
            if (n == 0) {
                break
            }
            got = (got + n)
        }
        var skip: i32 = 0
        if (got == 3) {
            const b0 = (stage[0] as u8)
            const b1 = (stage[1] as u8)
            const b2 = (stage[2] as u8)
            if (((b0 == 239UB) and (b1 == 187UB)) and (b2 == 191UB)) {
                skip = 3
            }
        }
        var i: i32 = skip
        while (i < got) {
            buf[(i - skip)] = (stage[i] as u8)
            i = (i + 1)
        }
        const kept: i32 = (got - skip)
        // 续读首块（允许短读——预读非必需；读取允许少于请求数）
        var n2: i32 = 0
        try {
            n2 = host.read(buf, kept, (buf.length - kept))
        } catch (e: core.Exception) {
            markFaulted()
            throw e
        }
        return (kept + n2)
    }
}

// 标量 UTF-8 宽度（纯函数）：按码点直接计算 1–4 字节（与
// String.length 的 UTF-8 字节口径一致；readToEnd 的 maxBytes 上限
// 逐标量先查后加的依据）
priv func textScalarWidth(c: char): i32 {
    const cp: i32 = (c as i32)
    if (cp < 128) {
        return 1
    }
    if (cp < 2048) {
        return 2
    }
    if (cp < 65536) {
        return 3
    }
    return 4
}

// 文本写出适配器（§4.4）：String → OutputStream 的编码写出面。
// local 类型，不支持并发或重入。本层无待输出缓冲——write 编码后
// 直达 host 写出；flush/收尾的逐层提交语义由 host 链承担。
pub class TextWriter implements core.IDisposable {
    // 下层输出流（借用或按所有权接管）
    priv const host: OutputStream
    // 所有权投影：true 仅当显式 .Owned
    priv const owned: bool
    // UTF-8 编码器（构造固定：null 即默认；无状态可共享，本类为
    // 引用一致性仍自持一份）
    priv const encoder: core.text.Utf8Encoder
    // 换行配置投影：true = CRLF，false = 默认 LF（构造时比较一次）
    priv const crlf: bool
    // 状态三态 i32 投影：0 正常 / 1 故障 / 2 关闭
    priv var state_: i32 = 0
    // 故障投影：基类外置支架（本类 dispose 先置 state_ = 2 再收尾，
    // 钩子内无法经 state_ 区分正常/故障）——与 buffered.rg 同款
    priv var broken: bool = false

    // 构造（§4.4 所有权契约）：默认借用 host、默认 LF 换行、默认
    // UTF-8 编码器、默认不生成 BOM。构造只做引用绑定——无内部资源
    // 建立；编码与换行配置自此固定，不在中途切换
    pub init(target: OutputStream, encoderOpt: core.text.Utf8Encoder? = null,
             newLineCrlf: bool = false,
             ownership: StreamWrapperOwnership = .Borrowed) {
        host = target
        encoder = (encoderOpt if? new core.text.Utf8Encoder())
        crlf = newLineCrlf
        const ownedCase: StreamWrapperOwnership = .Owned
        owned = (ownership == ownedCase)
    }

    // ── 状态助手（与 TextReader 同构）──

    priv func ensureUsable() {
        if (state_ == 2) {
            throw new core.IllegalStateException("文本写出器已关闭")
        }
        if (state_ == 1) {
            throw new core.IllegalStateException("文本写出器已进入故障状态，只允许清理")
        }
    }

    priv func markFaulted() {
        state_ = 1
    }

    // 完整 String 写出（§4.4）：按配置编码（本块 UTF-8）把 text 全部
    // 字节写出；不隐含 flush/关闭/截断/追加定位——需要下层完成时由
    // 调用者显式 flush() 或经正常 dispose 收尾。编码本身的容量守卫
    // （结果超 i32 Span 容量）属参数/容量错误，不置故障
    pub func write(text: String) {
        ensureUsable()
        const bytes = encoder.encode(text)
        try {
            host.write(bytes)
        } catch (e: core.Exception) {
            markFaulted()
            throw e
        }
    }

    // 单行写出（§4.4）：write(text) 后追加换行——默认 LF，
    // newLineCrlf=true 时 CRLF；不因写了一行就自动刷新
    pub func writeLine(text: String) {
        write(text)
        if (crlf) {
            write("\r\n")
        } else {
            write("\n")
        }
    }

    // 刷新（§4.4 关闭与刷新契约）：本层无待输出缓冲，提交即调
    // host.flush() 并等其完成（逐层语义由 host 链各层承担）；成功
    // 后仍可继续写入，不结束编码也不关闭流
    pub func flush() {
        ensureUsable()
        try {
            host.flush()
        } catch (e: core.Exception) {
            markFaulted()
            throw e
        }
    }

    // dispose（§4.4：dispose 幂等——首次调用完成后对象进入关闭状态，
    // 即使收尾失败也不重试）。结束编码：本块编码器 Utf8Encoder 无
    // 状态、无尾部字节，此步为空操作（注释钉死；若未来引入有状态
    // 编码器，在此追加收尾序列写出）。正常态：刷新下层流（完成剩余
    // 写出）后清理自身；故障态：不重写可能已部分提交的数据，跳过
    // 收尾刷新但仍清理自身；Borrowed 不关闭 host，Owned 接管 host
    // 关闭责任。首次收尾失败不得跳过其余必要清理——自身清理与
    // Owned 的 host 关闭照常执行后向调用者原样重抛
    pub override func dispose() {
        if (state_ == 2) {
            return
        }
        state_ = 2
        if (broken == false) {
            try {
                host.flush()
            } catch (e: core.Exception) {
                broken = true
                if (owned) {
                    host.dispose()
                }
                throw e
            }
        }
        if (owned) {
            host.dispose()
        }
    }
}
