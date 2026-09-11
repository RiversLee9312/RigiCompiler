namespace RigiCompiler.Tests
{
    public static partial class NativeE2ETests
    {
        // 同源分别作 VM 字面量断言与 native 对拍，且 native 默认启用泄漏检测。
        internal static string MessagingLifecycleSource =>
            SerializationGraphCorpus("mq_oop_lifecycle");

        // review-20260910 #回调定时器 回归源：手写 AsyncAction 子类回调体内
        // 含定时器挂起（yield sleep 80ms），六条消息经 Receiver→冷 Task→
        // ListenerCall.invoke→invoke.indirect 派发到 ComputeExecutor 并行
        // 执行。到达标记按消息唯一 cell 一次性写入（不可用共享计数器
        // count.store(count.load()+1)：load 与 store 之间隔着 AtomicStruct
        // 内部异步 Mutex 的挂起点，并行回调交错即丢失更新——该竞争曾被
        // 误诊为「定时器唤醒后协程不恢复」，插桩实证六协程全部恢复且 DONE）。
        // 主协程轮询六格求和，全量到达输出固定字面量，双端确定性对拍。
        internal static string ReceiverListenerTimerResumeSource
        {
            get
            {
                var lines = new System.Collections.Generic.List<string>
                {
                    "import core.io.Console",
                    "import core.collections.*",
                    "import core.coroutine.*",
                    "import core.messaging.*",
                    "import core.serialization.Serializable",
                    "@Serializable",
                    "pub shared class Msg {",
                    "    pub var v: i32",
                    "    pub init(_ -> v)",
                    "}",
                    "priv shared class SleepyListener : core.AsyncAction\\<Msg> {",
                    "    pub const arrived: Array\\<core.AtomicStruct\\<i64>>",
                    "    pub init() {",
                    "        arrived = core.collections.arrayOf\\<core.AtomicStruct\\<i64>>(6)",
                    "        var i = 0",
                    "        while (i < 6) {",
                    "            arrived[i] = new core.AtomicStruct\\<i64>(0L)",
                    "            i += 1",
                    "        }",
                    "    }",
                    "    pub override async operator call(m: Msg) {",
                    "        yield sleep(80)",
                    "        (arrived[m.v] as core.AtomicStruct\\<i64>).store(1L)",
                    "    }",
                    "}",
                    "priv func sumArrived(listener: SleepyListener): i64 {",
                    "    var sum: i64 = 0L",
                    "    var i = 0",
                    "    while (i < 6) {",
                    "        sum += (listener.arrived[i] as core.AtomicStruct\\<i64>).load()",
                    "        i += 1",
                    "    }",
                    "    return sum",
                    "}",
                    "pub func main(): i32 {",
                    "    const sender = new Messenger\\<Msg>()",
                    "    const receiver = new Receiver\\<Msg>(sender.createReader())",
                    "    const listener = new SleepyListener()",
                    "    receiver.addListener(listener)",
                    "    var i = 0",
                    "    while (i < 6) {",
                    "        await sender.send(new Msg(i))",
                    "        i += 1",
                    "    }",
                    "    sender.dispose()",
                    "    var spins = 0",
                    "    while ((sumArrived(listener) < 6L) and (spins < 200)) {",
                    "        yield sleep(20)",
                    "        spins += 1",
                    "    }",
                    "    if (sumArrived(listener) == 6L) {",
                    "        Console.println(\"listener-sleep-ok\")",
                    "    } else {",
                    "        Console.println(\"listener-sleep-missing\")",
                    "    }",
                    "    receiver.dispose()",
                    "    return 0",
                    "}",
                };
                return string.Join("\n", lines) + "\n";
            }
        }
    }
}
