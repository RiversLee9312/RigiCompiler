// Rigi 标准库最小表层（M43）：core.io::Console。
// print/printErr 是运行时原生方法面（RUNTIME.md §26 的 rigi_rt shim）的
// native 声明（SYNTAX.md §4.6）；println 在 Rigi 层包装，随编译单元一同
// 走 P1/P2/P3/P4 路径。BIL VM 经 BIL_STANDARD.md §22.5 内建 hook 执行。
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
