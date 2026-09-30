// Rigi 标准库：core.text UTF-8 增量编解码器（STDLIB §4.3.4 编码段，
// 施工块 3-4）。
//
// 覆盖（公共行为以 docs/STDLIB/04-text.md §4.3.4 为契约）：
//   - Utf8Decoder：String 与字节缓冲区之间的增量解码。跨块保留未完成
//     序列（内部状态 ≤ 4 字节残段，有界），区分「还需要输入」（完整
//     前缀留在残段里等待后续块）与「最终输入已截断」（isFinal=true 时
//     仍有残段）。不依赖文件或流——流适配器是 core.io 的事（施工块
//     3-6）；不跟随操作系统代码页（首版只内置 UTF-8）。
//   - Utf8Encoder：String/char → 字节缓冲区的编码入口（无状态）。encode
//     与 String.toUtf8Span 同通道（独立副本）；encodeTo 定点写入调用者
//     缓冲区，容量不足抛 core.OutOfBoundException，不截断。
//   - TextFormatException（新异常）：解码失败的严格模式报告载体。
//
// 语义裁决（§4.3.4 明文）：
//   - 默认严格拒绝非法编码（init(replacementOnError: bool = false)），
//     替换模式须显式选择——宽松不是默认。
//   - 非法编码覆盖：孤立的续字节、非法首字节（过长编码 0xC0/0xC1、
//     ≥0xF5）、序列内续字节越出该首字节的允许范围（从而排除过长编码、
//     代理区编码与超出 U+10FFFF 的编码）。
//   - 替换模式按 Unicode 推荐的「最大子部分」规则输出 U+FFFD：非法
//     子序列的每个最大子部分（是某个合法 UTF-8 序列前缀的最长字节段）
//     各产出一个 U+FFFD（U+FFFD 本身即替换符）。由此截断序列（isFinal
//     时的完整前缀残段）产出恰一个 U+FFFD；"ED A0 80"（代理区）产出
//     3 个；"F4 90 80 80"（越界）产出 4 个。
//   - 严格模式抛 TextFormatException，消息带失败原因与出错子序列的
//     零基字节位置（跨 decode 调用累计的输入流位置；抛出后实例状态
//     不保证一致，继续使用前须调用 reset()）。
//   - char 的 32 位标量存储不改变 String 的 UTF-8 表示（§4.3.1/§4.3.4）：
//     编码器按标量码点产出标准 UTF-8（1–4 字节），补充平面标量产出
//     4 字节序列；char→String 通道（插值）不做代理区拆分。
//
// 实现底座：解码侧输出串经标量插值逐个重建（String 不可变值语义，
// 与 text.rg caseMapAll 同口径）；编码侧触达 text_copy_out 字节原语
//（rigi_rt/text.c，VM hook TextCopyOut 双宿主同语义；@NativeSymbol 必
// 带防同命名空间符号拼接落空——native 不可重载，连接按 C 符号进行）。
// local 类型（非 shared）：Utf8Decoder 持可变残段，不支持同实例并发。
namespace core.text

// String 的 UTF-8 字节段拷出（C 符号 text_copy_out，rigi_rt/text.c）。
// 与 text.rg/builder.rg 触达同一 native 符号的本文件私有声明：Rigi 级
// 函数名加 utf8 中缀避免同命名空间同名（native 不可重载），VM 与
// native 两侧均按 @NativeSymbol 连接，不受本层命名影响
@NativeLibrary("rigi_rt")
@NativeSymbol("text_copy_out")
priv native func rigi_text_utf8_copy_out(src: String, srcOffset: i64, dest: Span\<u8>, destOffset: i32, count: i32): i32

// 解码失败异常（§4.3.4 编码段）：严格模式下的非法/截断 UTF-8 输入
// 报告载体，携带原因与出错子序列的零基字节位置。open 供派生细化。
pub open class TextFormatException : core.RuntimeException {
    pub init(text: String) { message = text }
    pub override func getMessage(): String { return message }
}

// UTF-8 增量解码器（§4.3.4）。跨 decode 调用保留未完成序列残段
//（Span\<u8> 4 字节槽，实际至多 3 字节待续——第 4 字节到达即完成），
// 内部状态有界；local 类型，不支持同实例并发。
pub class Utf8Decoder {
    // 替换模式开关（init 显式选择；默认 false = 严格拒绝）
    priv const replacement: bool
    // 未完成序列残段（至多 4 字节；pendingCount 为有效字节数）
    priv const pendingBuf: Span\<u8>
    // 残段有效字节数（0 = 无未完成序列；1..3 = 等待后续续字节）
    priv var pendingCount: i32
    // 当前序列期望总长（2/3/4；随首字节确定）
    priv var pendingLen: i32
    // 下一个字节允许的闭区间（首字节后按首字节约束；续字节后一律
    // 128..191）——越出区间即构成非法子序列的判定点
    priv var nextLo: i32
    priv var nextHi: i32
    // 累计已消费输入字节数（跨 decode 调用；reset 归零）——错误消息中
    // 的字节位置以它为基准，跨块保持全局一致
    priv var fed: i64

    // replacementOnError=false（默认）：严格模式——任何非法/截断输入
    // 抛 core.text.TextFormatException；true：替换模式——按最大子部分
    // 规则输出 U+FFFD 继续
    pub init(replacementOnError: bool = false) {
        replacement = replacementOnError
        pendingBuf = core.collections.spanOf\<u8>(4)
        pendingCount = 0
        pendingLen = 0
        nextLo = 128
        nextHi = 191
        fed = (0 as i64)
    }

    // 增量解码（§4.3.4）：消费 bytes 的全部字节，返回本块解出的内容
    //（跨块多调用结果按调用序拼接即完整解码）。isFinal=false 时未完成
    // 序列留在残段里（「还需要输入」）；isFinal=true 时残段视为最终
    // 输入已截断——严格模式抛 TextFormatException，替换模式产出一个
    // U+FFFD。单个非法子序列按最大子部分规则各产出/各报告一次。
    pub func decode(bytes: Span\<u8>, isFinal: bool): String {
        var acc = ""
        var i: i32 = 0
        const n: i32 = bytes.length
        while (i < n) {
            const b: i32 = (bytes[i] as i32)
            if (pendingCount == 0) {
                if (b < 128) {
                    // ASCII 标量直出
                    const cp: i64 = (b as i64)
                    const ch: char = (cp as char)
                    acc = "${acc}${ch}"
                    i = (i + 1)
                } else if ((b < 194) or (b > 244)) {
                    // 128..191 孤立续字节；192/193 过长编码首字节；
                    // 245..255 永不合法首字节
                    if (replacement) {
                        acc = utf8ReplacementOf(acc)
                        i = (i + 1)
                    } else if (b < 192) {
                        const pos: i64 = fed + (i as i64)
                        throw new TextFormatException("UTF-8 严格解码失败：孤立的续字节（字节值 ${b}）于字节位置 ${pos}")
                    } else {
                        const pos: i64 = fed + (i as i64)
                        throw new TextFormatException("UTF-8 严格解码失败：非法的序列首字节（字节值 ${b}）于字节位置 ${pos}")
                    }
                } else {
                    // 合法首字节区间 194..244：入残段并确定期望长度与
                    // 首续字节允许范围（范围下界排除过长编码，上界排除
                    // 代理区/越界编码——非法在第二字节即被判定）
                    pendingBuf[0] = (b as u8)
                    pendingCount = 1
                    if (b < 224) {
                        pendingLen = 2
                        nextLo = 128
                        nextHi = 191
                    } else if (b < 240) {
                        pendingLen = 3
                        if (b == 224) {
                            nextLo = 160
                        } else if (b == 237) {
                            nextLo = 128
                            nextHi = 159
                        } else {
                            nextLo = 128
                            nextHi = 191
                        }
                    } else {
                        pendingLen = 4
                        if (b == 240) {
                            nextLo = 144
                            nextHi = 191
                        } else if (b == 244) {
                            nextLo = 128
                            nextHi = 143
                        } else {
                            nextLo = 128
                            nextHi = 191
                        }
                    }
                    i = (i + 1)
                }
            } else {
                if ((b >= nextLo) and (b <= nextHi)) {
                    pendingBuf[pendingCount] = (b as u8)
                    pendingCount = (pendingCount + 1)
                    if (pendingCount == pendingLen) {
                        const ch = utf8DecodePending()
                        acc = "${acc}${ch}"
                        pendingCount = 0
                    } else {
                        // 后续续字节一律普通续字节范围
                        nextLo = 128
                        nextHi = 191
                    }
                    i = (i + 1)
                } else {
                    // 已缓存字节是某个合法序列的前缀（最大子部分），当前
                    // 字节越出允许范围使序列非法：残段产出/报告一次，当前
                    // 字节回退为新的子序列起点重新处理（替换模式语义）
                    if (replacement) {
                        acc = utf8ReplacementOf(acc)
                        pendingCount = 0
                    } else {
                        const at: i64 = fed + (i as i64)
                        const seqPos: i64 = at - (pendingCount as i64)
                        throw new TextFormatException("UTF-8 严格解码失败：编码序列非法（长度 ${pendingCount}）起始于字节位置 ${seqPos}")
                    }
                }
            }
        }
        fed = fed + (n as i64)
        if (isFinal) {
            if (pendingCount > 0) {
                if (replacement) {
                    // 截断残段恰为一个最大子部分（完整前缀）：一个 U+FFFD
                    acc = utf8ReplacementOf(acc)
                    pendingCount = 0
                } else {
                    const seqPos: i64 = fed - (pendingCount as i64)
                    throw new TextFormatException("UTF-8 严格解码失败：输入在序列完成前结束（截断序列，长度 ${pendingCount}）起始于字节位置 ${seqPos}")
                }
            }
        }
        return acc
    }

    // reset()：清空残段并归零位置计数，解码器可整体复用（换新输入流
    // 或从严格错误中恢复）
    pub func reset() {
        pendingCount = 0
        fed = (0 as i64)
    }

    // 从残段解出单个标量（残段是完整合法序列——首字节范围约束 + 续
    // 字节范围约束已排除过长/代理区/越界，as char 不可能越值域）
    priv func utf8DecodePending(): char {
        const lead: i64 = (pendingBuf[0] as i64)
        var cp: i64 = (0 as i64)
        if (pendingLen == 2) {
            cp = (lead & (31 as i64))
        } else if (pendingLen == 3) {
            cp = (lead & (15 as i64))
        } else {
            cp = (lead & (7 as i64))
        }
        var k: i32 = 1
        while (k < pendingLen) {
            cp = ((cp << (6 as i64)) | ((pendingBuf[k] as i64) & (63 as i64)))
            k = (k + 1)
        }
        return (cp as char)
    }
}

// U+FFFD 替换符追加（纯函数）：替换模式下每个非法子部分追加一个
//（U+FFFD = 65533，替换符是普通标量，经 char 插值入串）
priv func utf8ReplacementOf(acc: String): String {
    const r: char = (65533 as char)
    return "${acc}${r}"
}

// UTF-8 编码器（§4.3.4）：String/char → 字节缓冲区的转换入口，无状态
//（不持字段，可任意共享使用）。不依赖文件或流；不跟随操作系统代码页。
pub class Utf8Encoder {
    pub init() {
    }

    // encode(text)：整串 UTF-8 导出（独立副本，修改缓冲区不影响原
    // String）——与 String.toUtf8Span 同通道同约束（结果超出单个 Span
    // 的 i32 容量抛 core.OutOfBoundException，不截断，§4.3.1/§4.3.4）
    pub func encode(text: String): Span\<u8> {
        return text.toUtf8Span()
    }

    // encodeChar(ch)：单标量编码，返回 1–4 字节独立副本（ASCII 1 字节、
    // U+00007F 以下同；U+000080..U+0007FF 2 字节；U+000800..U+00FFFF
    // 3 字节；U+010000..U+10FFFF 4 字节）。char 的 32 位存储不改变
    // String 的 UTF-8 表示（§4.3.4）：按标量码点标准编码，不经代理区
    pub func encodeChar(ch: char): Span\<u8> {
        const s = "${ch}"
        return s.toUtf8Span()
    }

    // encodeTo(text, dest, offset)：把 text 的 UTF-8 字节写入
    // dest[offset .. offset+字节数)，返回写入字节数。offset 为负或容量
    // 不足（offset + 需要字节数超出 dest）抛 core.OutOfBoundException，
    // 不截断不部分写入（§4.3.4）；空串写入零字节返回 0
    pub func encodeTo(text: String, dest: Span\<u8>, offset: i32): i32 {
        if (offset < 0) {
            throw new core.OutOfBoundException("encodeTo 目标偏移为负：offset=${offset}")
        }
        const avail: i64 = ((dest.length as i64) - (offset as i64))
        if (text.length > avail) {
            throw new core.OutOfBoundException("encodeTo 容量不足：需要 ${text.length} 字节，可用 ${avail}（offset=${offset}，dest.length=${dest.length}）")
        }
        const count: i32 = (text.length as i32)
        const n = rigi_text_utf8_copy_out(text, (0 as i64), dest, offset, count)
        if (n < 0) {
            throw new core.OutOfBoundException("encodeTo 内部拷出失败（错误码 ${n}）")
        }
        return count
    }
}
