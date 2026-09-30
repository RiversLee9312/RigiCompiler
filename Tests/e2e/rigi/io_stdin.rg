// STDLIB §4.4（施工块 2-4b2）：core.io 标准输入流
//（StandardStreams.standardInput()）EOF 路径语料。
// 测试进程 stdin 为空/已关闭 → read 挂起后立即被 EOF 唤醒（§4.4：
// EOF = stdin 关闭/空输入，返回 0；暂时无数据才挂起等待）：
//   1. 三参 read 返回 0（EOF），流不进入故障态（EOF 不是故障——
//      随后仍可继续 read 到下一次 EOF）；
//   2. count == 0 校验通过后直接返回 0，不消费输入也不证明 EOF；
//   3. 范围校验（offset 越界抛 OutOfBoundException，不置故障）；
//   4. EOF 粘滞共享：第二个包装对象 read 同样返回 0（多个包装对象
//      共享同一输入位置——stdin fd 全局序，不能当独立输入源）；
//   5. dispose 幂等；关闭后 read 抛 core.IllegalStateException（状态
//      闸门先于范围校验）；关闭一个包装对象不影响新包装对象继续读；
//   6. 挂起路径（有数据可读）的唤醒在 e2e 无法喂输入，由 playground
//      手工探测（pipe 喂 stdin，native 产物与 VM 行为一致）覆盖。
// 注：EOF 短路（stdin 已 EOF 的粘滞旗标）发生在宿主面，VM 与 native
// 双宿主同语义；read 内部为普通 func 挂起（§4.4 函数文档 §4.5），本
// 用例 EOF 即醒，无数据可用性依赖。
// expect-output: STDIN-EOF:ok
// expect-exit: 0
import core.collections.*
import core.io.*

var step: i32 = 0
func require(value: bool) {
    step = (step + 1)
    if (value == false) { throw new core.RuntimeException("io_stdin 断言失败 " + step.toString()) }
}

pub func main(): i32 {
    const buf = spanOf\<u8>(8)
    // 1. 空/已关闭 stdin：read 返回 0（EOF）；EOF 不置故障——随后
    //    再读仍是 EOF（粘滞），流保持正常态
    const in1 = StandardStreams.standardInput()
    require(in1.read(buf, 0, 8) == 0)
    require(in1.read(buf, 0, 8) == 0)
    // 2. count == 0：范围校验通过后直接返回 0，不消费输入也不证明 EOF
    require(in1.read(buf, 8, 0) == 0)
    // 3. 范围校验：offset 越界抛 OutOfBoundException，参数校验失败
    //    不使流进入故障态
    var rejected = false
    try { in1.read(buf, 9, 0) } catch (e: core.OutOfBoundException) { rejected = true }
    require(rejected)
    rejected = false
    try { in1.read(buf, -1, 2) } catch (e: core.OutOfBoundException) { rejected = true }
    require(rejected)
    // EOF 后仍可正常读（故障态才会拒绝）——再次确认粘滞 EOF 返回 0
    require(in1.read(buf, 0, 1) == 0)
    // 4. EOF 粘滞共享：新包装对象从同一 stdin 读取，同样立即 EOF
    const in2 = StandardStreams.standardInput()
    require(in2.read(buf, 0, 8) == 0)
    // 5. dispose 幂等；关闭后 read 抛 IllegalStateException（状态闸门
    //    先于范围校验——非法范围同样抛状态异常）；关闭 in2 不影响
    //    其他包装对象（in1 继续可读到 EOF）
    in2.dispose()
    in2.dispose()
    rejected = false
    try { in2.read(buf, 0, 8) } catch (e: core.IllegalStateException) { rejected = true }
    require(rejected)
    rejected = false
    try { in2.read(buf, 99, 0) } catch (e: core.IllegalStateException) { rejected = true }
    require(rejected)
    require(in1.read(buf, 0, 8) == 0)
    in1.dispose()
    rejected = false
    try { in1.read(buf, 0, 1) } catch (e: core.IllegalStateException) { rejected = true }
    require(rejected)
    // 关闭后再开新包装对象仍可读（stdin 不归包装对象所有）
    const in3 = StandardStreams.standardInput()
    require(in3.read(buf, 0, 8) == 0)
    in3.dispose()
    Console.println("STDIN-EOF:ok")
    return 0
}
