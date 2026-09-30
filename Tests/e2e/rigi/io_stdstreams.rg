// STDLIB §4.4（施工块 2-4b1）：core.io 标准输出/错误输出流
//（StandardStreams.standardOutput()/standardError()）语料。
// 覆盖：经标准输出包装对象写已知字节行（三参 write + writeByte +
// flush + 整 Span 重载）由 expect-output 断言（VM stdout 捕获）；
// count == 0 范围校验通过后直接成功；dispose 幂等；关闭后 write/
// writeByte/flush 抛 core.IllegalStateException（状态闸门先于范围
// 校验——非法范围同样抛状态异常）；关闭一个包装对象不影响新包装
// 对象继续写；标准错误输出对象生命周期行为（写/刷新/关闭后拒绝/
// 新对象可写）——stderr 内容不经 expect-output 断言（驱动只校
// stdout），故 e2e 只测 stdout 写 + stderr 对象生命周期。
// expect-output: SS-OUT:Hello!
// expect-output: SS-OUT:second
// expect-exit: 0
import core.collections.*
import core.io.*

var step: i32 = 0
func require(value: bool) {
    step = (step + 1)
    if (value == false) { throw new core.RuntimeException("io_stdstreams 断言失败 " + step.toString()) }
}

pub func main(): i32 {
    // 1. 标准输出：三参 write 写 12 字节 "SS-OUT:Hello"，随后
    //    writeByte '!' 与换行，flush 完成刷新
    const out = StandardStreams.standardOutput()
    const hello = spanOf\<u8>(12)
    // 'S'83 'S'83 '-'45 'O'79 'U'85 'T'84 ':'58 'H'72 'e'101 'l'108 'l'108 'o'111
    hello[0] = 83UB
    hello[1] = 83UB
    hello[2] = 45UB
    hello[3] = 79UB
    hello[4] = 85UB
    hello[5] = 84UB
    hello[6] = 58UB
    hello[7] = 72UB
    hello[8] = 101UB
    hello[9] = 108UB
    hello[10] = 108UB
    hello[11] = 111UB
    out.write(hello, 0, 12)
    out.writeByte(33UB)   // '!'
    out.writeByte(10UB)   // '\n'
    out.flush()
    // 2. count == 0：范围校验通过后直接成功，不写出任何字节
    out.write(hello, 12, 0)
    // 3. 关闭（dispose 幂等）后写抛 IllegalStateException——状态
    //    闸门先于范围校验（非法范围同样抛状态异常而非范围异常），
    //    flush 同样被拒绝
    out.dispose()
    out.dispose()
    var rejected = false
    try { out.writeByte(65UB) } catch (e: core.IllegalStateException) { rejected = true }
    require(rejected)
    rejected = false
    try { out.write(hello, 99, 0) } catch (e: core.IllegalStateException) { rejected = true }
    require(rejected)
    rejected = false
    try { out.flush() } catch (e: core.IllegalStateException) { rejected = true }
    require(rejected)
    // 4. 关闭一个包装对象不影响新包装对象继续写（整 Span 重载 +
    //    flush + dispose）
    const out2 = StandardStreams.standardOutput()
    const second = spanOf\<u8>(14)
    // "SS-OUT:second" 13 字节 + '\n'
    // 'S'83 'S'83 '-'45 'O'79 'U'85 'T'84 ':'58 's'115 'e'101 'c'99 'o'111 'n'110 'd'100
    second[0] = 83UB
    second[1] = 83UB
    second[2] = 45UB
    second[3] = 79UB
    second[4] = 85UB
    second[5] = 84UB
    second[6] = 58UB
    second[7] = 115UB
    second[8] = 101UB
    second[9] = 99UB
    second[10] = 111UB
    second[11] = 110UB
    second[12] = 100UB
    second[13] = 10UB
    out2.write(second)
    out2.flush()
    out2.dispose()
    // 5. 标准错误输出对象生命周期：写/刷新/关闭后拒绝/新对象可写
    //   （stderr 字节不经 expect-output 断言）
    const err = StandardStreams.standardError()
    err.writeByte(69UB)   // 'E'
    err.flush()
    err.dispose()
    rejected = false
    try { err.writeByte(65UB) } catch (e: core.IllegalStateException) { rejected = true }
    require(rejected)
    const err2 = StandardStreams.standardError()
    err2.writeByte(70UB)  // 'F'
    err2.flush()
    err2.dispose()
    return 0
}
