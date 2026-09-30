// Rigi 标准库最小表层（M43）：core.io::Console。
// print/printErr 是运行时原生方法面（RUNTIME.md §26 的 rigi_rt shim）的
// native 声明（SYNTAX.md §4.6）；println 在 Rigi 层包装，随编译单元一同
// 走 P1/P2/P3/P4 路径。BIL VM 经 BIL_STANDARD.md §22.5 内建 hook 执行。
// B2-4b1 Console 衔接：C 侧 print/printErr 实现与 core.io 标准流
//（stdstreams.rg 的 stdout_write/stdout_flush 等）走同一写通道与刷新
// 助手（shim.c），经标准库写入同一路标准流的操作语义统一（§4.4：
// 保留 println 外部 API 的行原子性，单次调用完成写出与刷新）。
// 3-6 收尾裁决：println 保持 native 单调用路径而不改经
// StandardStreams.standardOutput() 的 Rigi 层 write+flush——C 侧
// rigi_print 单次调用内完成「UTF-8 字节写出 + 同通道刷新助手」（shim.c
// 与 stdout_write/stdout_flush 共用 rigi_stdio_write_stream/
// rigi_stdio_flush_stream），行原子性与「调用结束前完成刷新」由 VM 对
// 单次 native 调用的原子保证承担；若改 Rigi 层「编码 + write + flush」
// 两次流调用，并发协程可在两次调用之间插入另一行，且在 write 的短写
// 补齐循环内存在交错窗口——原子性会退化。故二选一取「保持现状」，
// 标准流对象路径（StandardStreams.standardOutput()）已可用，语义同通道。
namespace core.io

pub class Console {
    @NativeLibrary("rigi_rt")
    @NativeSymbol("print")
    priv static native func print(text: String)

    @NativeLibrary("rigi_rt")
    @NativeSymbol("printErr")
    priv static native func printErr(text: String)

    // 先拼换行再单次 native print：VM 只保证单次 print 原子，
    // 两次调用在协程并发下会交错（行原子性要求一次调用完成）
    pub static func println(text: String) {
        print(text + "\n")
    }
}
