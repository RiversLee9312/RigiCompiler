// 标准流是逐通道字节序列：E4 | B8 AD 0A 两次写入最终仍是“中\n”。
// stdout 与 stderr 的 UTF-8 续写状态互不干扰；文本 print 与字节写同序。
// expect-output: 前
// expect-output: 中
// expect-output: 后
// expect-exit: 0
import core.collections.*
import core.io.*

pub func main(): i32 {
    const out = StandardStreams.standardOutput()
    const err = StandardStreams.standardError()
    const bytes = spanOf\<u8>(4)
    bytes[0] = 228UB
    bytes[1] = 184UB
    bytes[2] = 173UB
    bytes[3] = 10UB

    // “前”先于字节写，“后”跟在完整“中”之后，次序必须保持。
    Console.println("前")
    out.write(bytes, 0, 1)
    // stderr 相同首字节不可与 stdout 拼接，须另起解码状态。
    err.write(bytes, 0, 1)
    out.write(bytes, 1, 3)
    err.write(bytes, 1, 3)
    Console.println("后")
    out.flush()
    err.flush()
    out.dispose()
    err.dispose()
    return 0
}
