// STDLIB §4.4：readLine(maxBytes) UTF-8 正文字节上限独立回归。
// expect-output: readline-limit-ok
// expect-exit: 0
import core.io.*

var checks: i32 = 0
func require(ok: bool) {
    checks = (checks + 1)
    if (ok == false) {
        throw new core.RuntimeException("io_readline_limit 断言失败 " + checks.toString())
    }
}

pub func main(): i32 {
    // 恰限、零上限空行、EOF null、末尾无换行正文；旧无参行为仍可用。
    const normal = new TextReader(new MemoryInputStream("ab\n\n末".toUtf8Span()))
    require(((normal.readLine(2L) if? "?") == "ab"))
    require(((normal.readLine(0L) if? "?") == ""))
    require(((normal.readLine(3L) if? "?") == "末"))
    require((normal.readLine(0L) == null))
    normal.dispose()

    // 中 = 3 字节，不得按一个标量误放行 2 字节上限；超限不是故障态。
    const chinese = new TextReader(new MemoryInputStream("中\n后".toUtf8Span()))
    var rejected = false
    try {
        chinese.readLine(2L)
    } catch (e: core.OutOfBoundException) {
        rejected = true
    }
    require(rejected)
    // 只核验后续读取仍可调用且正常返回，不约束未规定的余部丢弃策略。
    chinese.readLine()
    chinese.dispose()
    const exactChinese = new TextReader(new MemoryInputStream("中\n".toUtf8Span()))
    require(((exactChinese.readLine(3L) if? "?") == "中"))
    exactChinese.dispose()

    // 补充平面单标量宽度 4 字节。
    const emoji = new TextReader(new MemoryInputStream("😀\n".toUtf8Span()))
    rejected = false
    try {
        emoji.readLine(3L)
    } catch (e: core.OutOfBoundException) {
        rejected = true
    }
    require(rejected)
    emoji.dispose()
    const exactEmoji = new TextReader(new MemoryInputStream("😀\n".toUtf8Span()))
    require(((exactEmoji.readLine(4L) if? "?") == "😀"))
    exactEmoji.dispose()

    // CRLF、CR、LF 都不计入正文；CR 的推回沿用无参路径。
    const endings = new TextReader(new MemoryInputStream("a\r\nb\rc\n".toUtf8Span()))
    require(((endings.readLine(1L) if? "?") == "a"))
    require(((endings.readLine(1L) if? "?") == "b"))
    require(((endings.readLine(1L) if? "?") == "c"))
    require((endings.readLine(0L) == null))
    endings.dispose()

    // 负上限必须在任何读取之前拒绝，包括首次 BOM 判定之前。
    const negative = new TextReader(new MemoryInputStream("start\n".toUtf8Span()))
    rejected = false
    try {
        negative.readLine(-1L)
    } catch (e: core.OutOfBoundException) {
        rejected = true
    }
    require(rejected)
    require(((negative.readLine() if? "?") == "start"))
    negative.dispose()

    // 流首 BOM 不属于结果；零上限在仅有 BOM 和空行时仍可返回空串。
    const bom = core.collections.spanOf\<u8>(4)
    bom[0] = 239UB
    bom[1] = 187UB
    bom[2] = 191UB
    bom[3] = 10UB
    const bomReader = new TextReader(new MemoryInputStream(bom))
    require(((bomReader.readLine(0L) if? "?") == ""))
    require((bomReader.readLine(0L) == null))
    bomReader.dispose()
    core.io.Console.println("readline-limit-ok")
    return 0
}
