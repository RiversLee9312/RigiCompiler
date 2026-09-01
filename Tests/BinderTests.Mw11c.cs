using System.Linq;

namespace RigiCompiler.Tests
{
    // MW11c 棒2 编译级正例（§4.5/§18.2/§19.5/§19.6/§19.7/§20.1）：
    // 新 stdlib 形状（冷 Task 构造、TaskState、singleton Executor、Mutex、
    // Timer/RepeatOption、core.time 三类型与运算符）经 P1–P3 类型检查通过。
    // 运行语义已由棒3–5a 落地（本文件仍只断言编译级形状）。
    public static partial class BinderTests
    {
        private static void TestMw11cCoroutineShapes()
        {
            TestHarness.Section("P3 MW11c coroutine 形状（编译级正例）");

            // 冷 Task 两元数构造（SYNTAX §4.5）：只存 body 不执行
            var coldTask = BindUnitWithStdlib(
                "import core.coroutine.Task\n" +
                "func main() {\n" +
                "    const t = new Task(func{async () -> { }})\n" +
                "    const ti = new core.coroutine.Task\\<i32>(func{async (): i32 -> 42})\n" +
                "    const s = t.state\n" +
                "}\n");
            CheckNoErrors("new Task/new Task\\<i32> 冷构造与 state 读取无诊断", coldTask.Unit);

            // run 两变体 + executor 读写（预设通道）
            var runShapes = BindUnitWithStdlib(
                "import core.coroutine.Task\n" +
                "import core.coroutine.ComputeExecutor\n" +
                "func main() {\n" +
                "    const t = new Task(func{async () -> { }})\n" +
                "    t.run()\n" +
                "    t.run(new ComputeExecutor())\n" +
                "    t.executor = new ComputeExecutor()\n" +
                "    const e = t.executor\n" +
                "}\n");
            CheckNoErrors("run 两变体与 executor 读写无诊断", runShapes.Unit);

            // singleton 构造表达式：三内置 Executor 各取进程内唯一实例（§20.1）
            var singletons = BindUnitWithStdlib(
                "import core.coroutine.MainExecutor\n" +
                "import core.coroutine.ComputeExecutor\n" +
                "import core.coroutine.IOExecutor\n" +
                "func main() {\n" +
                "    const a = new MainExecutor()\n" +
                "    const b = new ComputeExecutor()\n" +
                "    const c = new IOExecutor()\n" +
                "}\n");
            CheckNoErrors("三 singleton Executor 构造无诊断", singletons.Unit);

            // TaskState 六 case + isRunning（§18.2）
            var taskState = BindUnitWithStdlib(
                "import core.coroutine.TaskState\n" +
                "func main(): bool {\n" +
                "    const s: TaskState = .Runnable\n" +
                "    return s.isRunning\n" +
                "}\n");
            CheckNoErrors("TaskState case 与 isRunning 无诊断", taskState.Unit);

            // Mutex 全成员形状（§19.6）：acquire/release/runSynchronously 两变体
            var mutex = BindUnitWithStdlib(
                "import core.coroutine.Mutex\n" +
                "async func work(m: Mutex) {\n" +
                "    const lock = await m.acquire()\n" +
                "    m.release(lock)\n" +
                "    await m.runSynchronously(func{async () -> { }})\n" +
                "    const r = await m.runSynchronously\\<i32>(func{async (): i32 -> 1})\n" +
                "}\n");
            CheckNoErrors("Mutex acquire/release/runSynchronously 两变体无诊断", mutex.Unit);

            // Timer + RepeatOption 三 case（§19.5）：构造即排程、默认 .NoRepeat、
            // 参数洞 Repeat 与嵌套全形/省略两形
            var timer = BindUnitWithStdlib(
                "import core.coroutine.Timer\n" +
                "func main() {\n" +
                "    const once = new Timer((100 as i64))\n" +
                "    const opt: Timer.RepeatOption = .Repeat(3)\n" +
                "    const looping = new Timer((50 as i64), .InfiniteRepeat)\n" +
                "    const n = opt.repeatCount\n" +
                "}\n");
            CheckNoErrors("Timer 构造与 RepeatOption 三 case 无诊断", timer.Unit);

            // core.time 三类型与运算符（§19.7）：构造/访问器/减法/比较
            var timeShapes = BindUnitWithStdlib(
                "import core.time.TimeStamp\n" +
                "import core.time.TimeSpan\n" +
                "import core.time.DateTime\n" +
                "func main(): i64 {\n" +
                "    var stamp = new TimeStamp((1000 as i64), 500)\n" +
                "    stamp.nanoseconds = 999999\n" +
                "    const span = TimeSpan.fromMilliseconds((250 as i64))\n" +
                "    const later = new DateTime(stamp)\n" +
                "    const earlier = new DateTime(new TimeStamp((500 as i64), 0))\n" +
                "    const diff = later - earlier\n" +
                "    if ((later > earlier) and (span == TimeSpan.fromMilliseconds((250 as i64)))) {\n" +
                "        return diff.totalMilliseconds\n" +
                "    }\n" +
                "    return span.totalMilliseconds\n" +
                "}\n");
            CheckNoErrors("TimeStamp/TimeSpan/DateTime 与运算符无诊断", timeShapes.Unit);

            // Timer.schedule 静态入口（§19.5）
            var schedule = BindUnitWithStdlib(
                "import core.coroutine.Timer\n" +
                "import core.time.DateTime\n" +
                "import core.time.TimeStamp\n" +
                "func main() {\n" +
                "    const alarm = Timer.schedule(new DateTime(new TimeStamp((0 as i64), 0)))\n" +
                "}\n");
            CheckNoErrors("Timer.schedule 静态入口无诊断", schedule.Unit);

            // CoroutineLocal：具体类、双 init、withValue 作用域绑定、get 可空
            var coroLocal = BindUnitWithStdlib(
                "import core.coroutine.CoroutineLocal\n" +
                "async func work() {\n" +
                "    const id = new CoroutineLocal\\<String>()\n" +
                "    const def = new CoroutineLocal\\<String>(\"x\")\n" +
                "    const unbound = id.get()\n" +
                "    await id.withValue(\"v\", func{async () -> { }})\n" +
                "    const n = await def.withValue\\<i32>(\"y\", func{async (): i32 -> 1})\n" +
                "}\n");
            CheckNoErrors("CoroutineLocal 双 init / get / withValue 两变体无诊断",
                coroLocal.Unit);

            // 负例：Repeat 参数洞取非正值的**类型面**仍通过（运行期才抛
            // IllegalStateException，棒3/棒4）；但 state 的用户写入必须被拒
            //（pub get + priv set）
            var stateWrite = BindUnitWithStdlib(
                "import core.coroutine.Task\n" +
                "import core.coroutine.TaskState\n" +
                "func main() {\n" +
                "    const t = new Task(func{async () -> { }})\n" +
                "    t.state = .Runnable\n" +
                "}\n");
            TestHarness.CheckSemanticError("Task.state 用户写入被拒（priv set）",
                stateWrite.Unit.Diagnostics, "state");
        }
    }
}
