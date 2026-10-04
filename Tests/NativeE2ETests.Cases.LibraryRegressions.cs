using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Middleware.Cli;
using RigiCompiler.Middleware.Emit;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests
{
    public static partial class NativeE2ETests
    {
        // 原编号 461..599 的有序用例；与主文件共享同一类型、字段及生命周期。
        private static (string Label, Action Run)[] CreateLibraryRegressionsCases() => new (string Label, Action Run)[]
        {
            Case("同步 callable 开放封闭多参数与Action", CallableSlotSource),
            Case("Nullable 泛型cast与is元素约束", NullableCastSource),
            Case("Atomic 回调挂起与锁竞争", AtomicContentionSource),
            Case("安全Atomic容器工厂方法与快照隔离", AtomicContainersSource),
            Case("安全Atomic数组仅工厂", AtomicContainerProbePrefix + "pub func main():i32 { const a = AtomicArray.fromArray\\<Item>(core.collections.arrayOfElements\\<Item>(new Item(1)))\n return 0 }"),
            Case("安全Atomic数组长度", AtomicContainerProbePrefix + "pub func main():i32 { const a = AtomicArray.fromArray\\<Item>(core.collections.arrayOfElements\\<Item>(new Item(1)))\n return await a.length() }"),
            Case("共享消息序列化往返", AtomicContainerProbePrefix + "pub func main():i32 { const item = new Item(1)\n const p = item:Serializable.toParcel()\n const x = core.serialization.fromParcel\\<Item>(p)\n return x.n }"),
            Case("共享消息泛型深复制", AtomicContainerProbePrefix + "pub func main():i32 { const x = core.serialization.deepCopy\\<Item>(new Item(1))\n return x.n }"),
            Case("普通Map自定义对象键相等", AtomicMapKeySource),
            Case("泛型对象文本覆写可挂起", AtomicMapKeySuspendingSource),
            Case("序列化类自环与异常调用隔离", SerializationGraphCorpus("serialization_graph_class")),
            Case("序列化混合容器图与Temporary", SerializationGraphCorpus("serialization_graph_mixed")),
            Case("序列化值类型开放泛型兼容", SerializationGraphCorpus("serialization_graph_value")),
            Case("闭环动态字符串回收不重入fence", SerializationGraphCorpus("serialization_graph_gc_strings")),
            Case("序列化非法引用与异常后上下文隔离", SerializationGraphCorpus("serialization_graph_errors")),
            Case("MQ OOP 复杂生命周期与分段回收", MessagingLifecycleSource),
            // review-20260910 #07 回归：手写 AsyncAction<TMessage> 子类覆写
            // async operator call（含挂起点 → CoroutineSplit 拆 stub+resume），
            // 经 Receiver 冷 Task 派发走 invoke.indirect——具化收集必须沿间接
            // 调用闭包深入 resume 体内的泛型 await（AtomicMap set 的 lambda/cell
            // 具化），缺失时 native 侧 dynamic new 拿到空 sheet 触发
            // rigi_alloc 协议错误。聚合按消息唯一键写入，双端确定性对拍。
            Case("MQ 手写AsyncAction覆写含await经Receiver冷Task派发聚合",
                "import core.io.Console\n" +
                "import core.collections.*\n" +
                "import core.coroutine.*\n" +
                "import core.messaging.*\n" +
                "import core.serialization.Serializable\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v)\n" +
                "}\n" +
                "priv shared class CountAction : core.AsyncAction\\<Msg> {\n" +
                "    pub const agg: core.AtomicMap\\<String, i64>\n" +
                "    pub init() {\n" +
                "        agg = core.AtomicMap.fromMap\\<String, i64>(new Map\\<String, i64>())\n" +
                "    }\n" +
                "    pub override async operator call(m: Msg) {\n" +
                "        const key = \"k${m.v}\"\n" +
                "        const current = await agg.tryGet(key)\n" +
                "        await agg.set(key, ((current if? 0L) + 1L))\n" +
                "    }\n" +
                "}\n" +
                "pub async func produce(sender: Messenger\\<Msg>, id: i32, rounds: i32) {\n" +
                "    var i = 0\n" +
                "    while (i < rounds) {\n" +
                "        await sender.send(new Msg(((id * 100) + i)))\n" +
                "        yield\n" +
                "        i += 1\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const sender = new Messenger\\<Msg>()\n" +
                "    const receiver = new Receiver\\<Msg>(sender.createReader())\n" +
                "    const listener = new CountAction()\n" +
                "    receiver.addListener(listener)\n" +
                "    receiver.setExecutor(listener, new ComputeExecutor())\n" +
                "    const producers = arrayOf\\<Task>(2)\n" +
                "    var i = 0\n" +
                "    while (i < 2) {\n" +
                "        const id = i\n" +
                "        const t = new Task(func{async () -> { await produce(sender, id, 2) }})\n" +
                "        t.run(new ComputeExecutor())\n" +
                "        producers[i] = t\n" +
                "        i += 1\n" +
                "    }\n" +
                "    i = 0\n" +
                "    while (i < 2) {\n" +
                "        await (producers[i] as Task)\n" +
                "        i += 1\n" +
                "    }\n" +
                "    sender.dispose()\n" +
                "    var spins = 0\n" +
                "    while (((await listener.agg.count()) < 4L) and (spins < 2000)) {\n" +
                "        yield\n" +
                "        spins += 1\n" +
                "    }\n" +
                "    var total = 0L\n" +
                "    var id = 0\n" +
                "    while (id < 2) {\n" +
                "        var s = 0\n" +
                "        while (s < 2) {\n" +
                "            total += ((await listener.agg.tryGet(\"k${((id * 100) + s)}\")) if? 0L)\n" +
                "            s += 1\n" +
                "        }\n" +
                "        id += 1\n" +
                "    }\n" +
                "    Console.println(\"agg=${total}\")\n" +
                "    receiver.dispose()\n" +
                "    yield sleep(200)\n" +
                "    return 0\n" +
                "}\n"),
            // review-20260910 #回调定时器 回归：listener 回调体内含定时器
            // 挂起（yield sleep）经 Receiver 冷 Task 派发后必须全部恢复并
            // 全量投递。到达检测按消息唯一 cell 一次性写入——共享计数器
            // RMW（load 与 store 隔着 AtomicStruct 异步 Mutex 挂起点）在
            // 并行回调下丢更新，曾被误诊为「协程不恢复」。
            Case("MQ listener回调定时器挂起恢复全量投递",
                ReceiverListenerTimerResumeSource),
            // b4-1/constfix：字段键校验已切回字节级扫描（toUtf8Span 单次
            // 拷贝 + Span 下标；初值绑定失败的级联诊断经毒化静默补齐后
            // 解除阻断），步数 rail 从 40M 回落 20M（characterAt 版实测
            // ~27M，字节版回到默认 rail 以内）。D3 字段键契约要求
            // setElement 运行时校验键合法性，messaging 经 deepCopy 每消息
            // 数十次走该校验；对拍语义不变，native 路径无 rail 不受影响。
            // 步数 rail 60M（paramfix 核查）：本用例 4 生产者并发 + 真实
            // 定时器挂起，VM 总步数随真实时钟驱动的交错**本质波动**——
            // 同一棵树实测至少 20M～31M+（CLI vm 单进程二分 (20.0, 20.5]M；
            // NativeE2E 子进程环境多次 >20M、一次 >30M 后 <31.4M 收敛），
            // 20M rail 的历史失败均为时序不利侧的采样而非语义退步
            //（b4-1/constfix 字节级字段键校验基线 ≈20M 边缘）。native 路
            // 径无 rail 不受影响（VM 超限轮 native 仍正确输出并完成）。
            // 60M = 观测上限约 2 倍熔断余量；真失控（死循环）仍在分钟级
            // 被拦截。
            Case("MQ 四生产者跨执行器广播与封存排空", SerializationGraphCorpus("mq_oop_concurrent"),
                maxSteps: 60_000_000),
            Case("纯RigiMQ水位部分compact与全部drain", SerializationGraphCorpus("mq_pure_watermark")),
            Case("纯RigiMQ重复唤醒release与异常解锁", SerializationGraphCorpus("mq_pure_wakeup_release")),
            Case("泛型new隐式typeid跨挂起恢复", SerializationGraphCorpus("generic_new_after_suspend")),
            Case("泛型class序列化闭合对象头", SerializationGraphCorpus("serialization_generic_envelope")),
            Case("双executor候选swap与最后release竞争", MqConcurrentReleaseCorpus(), maxSteps: 100_000_000),
            NativeErrCase("普通挂起dispose与同名元数回调布局", SerializationGraphCorpus("mq_dispose_after_suspend"),
                "UndisposedResourceException", needlePresent: false),
            NativeErrCase("Reader跨executor重复dispose幂等", SerializationGraphCorpus("mq_reader_dispose_race"),
                "UndisposedResourceException", needlePresent: false),
            Case("Compute池yield与Polling迁移单执行", SerializationGraphCorpus("compute_pool_resume")),
            // Phase 2.6（§19.2 语义纠偏）：恢复式探测语义双宿主对拍——探测
            // 中途挂起（await/裸 yield）→ 唤醒 → 完成探测 → false 退回等待/
            // true 续行；探测抛出落 yield 点被 catch。探测计数确定性断言
            //（3/1/2）同时锁定 untainted（YieldOnce）与 tainted（SlowPoll/
            // BoomPoll，经 AtomicStruct.load 的 Mutex 链）两路径
            Case("PollingAlarm恢复式探测挂起与就绪判定", SerializationGraphCorpus("pollalarm_isready_semantics")),
            // 遗留1回归：isReady 内泛型闭合 new——ConstructedCallCollector 补
            // YieldInstruction 边（poll_probe 虚派发 isReady 的构造收集），
            // 修复前 native 发射期 sheet select miss 烧 null、运行期
            // rigi_alloc(NULL) 硬错；双路径（untainted 廉价 + tainted 恢复）对拍
            Case("PollingAlarm探测内泛型闭合new双路径", SerializationGraphCorpus("pollalarm_generic_new")),
            Case("Compute池终态与await登记竞争及重复观察", SerializationGraphCorpus("task_terminal_waiter_race")),
            Case("MQ跨段缓存与积压branch及清空后复用", SerializationGraphCorpus("mq_segment_cursor")),
            Case("值块lambda混合return与throw执行finally", SerializationGraphCorpus("lambda_return_throw_finally")),
            NativeErrCase("序列化256节点长环重复引用与独立拷贝", SerializationGraphCorpus("serialization_graph_long_cycle"),
                "UndisposedResourceException", needlePresent: false),
            Case("Place嵌套泛型回调身份", SerializationGraphCorpus("place_nested_callback")),
            Case("泛型Cell构造只读与未调用扩张成员", SerializationGraphCorpus("capability_generic_cells")),
            Case("泛型接口参数返回与具体实现ABI", SerializationGraphCorpus("generic_interface_abi")),
            Case("普通子类闭合泛型基类身份与字段", SerializationGraphCorpus("closed_generic_base_identity")),
            Case("Any对象视图持有与AtomicList引用消息", SerializationGraphCorpus("any_object_view_ownership")),
            Case("闭合Func间接继承与不同元数挂起", SerializationGraphCorpus("closed_callable_suspend")),
            EnvCase("Alarm粘滞重复跨属主与环回收", SerializationGraphCorpus("alarm_lifecycle"),
                new Dictionary<string, string> { ["RIGI_RT_GC_THRESHOLD"] = "128" }),
            NativeOnlyCase("Alarm原生底座反复创建释放有界", NativeResourceCorpus("alarm_resources"),
                "void alarm_resource_test_marker(void) {}", "alarm-resources-ok\n", 0),
            NativeOnlyCase("已观察失败Task节点与异常资源有界", NativeResourceCorpus("failure_resources"),
                "void failure_resource_test_marker(void) {}", "failure-resources-ok\n", 0,
                new Dictionary<string, string> { ["RIGI_RT_GC_THRESHOLD"] = "128" }),
            EnvCase("失败Task环的内部资源析构", SerializationGraphCorpus("failure_lifecycle"),
                new Dictionary<string, string> { ["RIGI_RT_GC_THRESHOLD"] = "128" }),
            Case("同一失败Task多Compute观察者重抛", SerializationGraphCorpus("failure_shared_waiters")),
            // GC Phase 3c 异常图 promotion 压力对拍：失败异常图（local 嵌套
            // + 环 + 数组共享子图）被 16 个 Executor 的观察协程各 await 128
            // 次深读字段——waiter 侧每次 failure_get 独立 acquire/release；
            // 低 GC 门槛叠加 macroGC 扫描压力。promotion 是 native 实现细节
            //（VM 无会计位），双宿主对拍语义必须一致
            EnvCase("失败异常图多Executor并发读取一致", SerializationGraphCorpus("failure_graph_mt_readers"),
                new Dictionary<string, string> { ["RIGI_RT_GC_THRESHOLD"] = "128" }),
            NativeOnlyCase("普通容器删除及时释放尾槽", NativeResourceCorpus("collection_remove_resources"),
                "void collection_remove_resource_test_marker(void) {}", "7\ncollection-remove-resources-ok\n", 0),
            NativeOnlyCase("NativeRc 并发弱票据与恰好一次析构", NativeRcLifecycleSource,
                NativeRcLifecycleC, "native-rc-ok\n", 0),
            Case("显式 shared 泛型约束", SerializationGraphCorpus("shared_generic_constraint")),
            Case("具名参数 Pair 重建而非泛型转换", SerializationGraphCorpus("kwargs_typed_pair")),
            Case("类型句柄来源与泛型可空转换守卫", SerializationGraphCorpus("cast_provenance_guards")),
            Case("非 rich wrapper 的 self 与值宿主复制", SerializationGraphCorpus("wrapper_nonrich_self")),
            Case("wrapper 借用成员与独立闭包", SerializationGraphCorpus("wrapper_this_members")),
            Case("wrapper 动态构造拒绝", SerializationGraphCorpus("wrapper_dynamic_new_rejected")),
            Case("core SB 公开约束", SerializationGraphCorpus("sb_core_constraint")),
            Case("泛型约束声明处证明", SerializationGraphCorpus("generic_proof_positive")),
            Case("wrapper 独立宿主参数与原地写入", SerializationGraphCorpus("wrapper_self_parameter")),
            Case("this 捕获拥有宿主并跨挂起保活", SerializationGraphCorpus("lambda_this_owned")),
            Case("Handle 泛型身份与可写能力严格隔离", SerializationGraphCorpus("handle_nominal_identity")),
            // 3b-β 壳模型语义改线对拍（VM/native 同语料）：跨协程最后释放、
            // 属主终止过户、asMutable 同壳计数
            Case("Handle 跨协程传递且最后释放在非属主线程", SerializationGraphCorpus("handle_cross_coroutine_release")),
            Case("属主协程终止过户后他线程 load", SerializationGraphCorpus("handle_owner_teardown")),
            Case("asMutable 双 capability 同壳计数与释放序", SerializationGraphCorpus("handle_asmutable_shared_count")),
            // 3b-δ2 壳释放属主化对拍：属主 park（EventAlarm 停靠）期间
            // 他线程归零转移入挂起栈，属主恢复槽消化（延迟有界）+ 段内
            // 高频 expose/归零的段尾消化
            Case("Handle 属主 park 跨线程归零与唤醒段消化", SerializationGraphCorpus("handle_owner_parked_drain")),
            Case("enum 序列化判别与 rich 载荷快照", SerializationGraphCorpus("serialization_enum_snapshot")),
            Case("源码 SB 与 Serializable 双重应用及值编解码", SerializationGraphCorpus("serialization_sb_explicit")),
            Case("数组闭合泛型身份与嵌套转换拒绝", SerializationGraphCorpus("array_nominal_identity")),
            Case("SB 集合使用 Serializable 深复制元素", SerializationGraphCorpus("serialization_sb_collections")),
            Case("Parcel 内嵌容器图快照与解码隔离", SerializationGraphCorpus("serialization_parcel_snapshot")),
            Case("循环复用托管槽后的异常清理", SerializationGraphCorpus("arc_reused_slot_exception")),
            Case("Any 参数的动态 Type 视图精确物化", SerializationGraphCorpus("type_of_dynamic_view")),
            NativeOnlyCase("GC 债务 64 位计量与重复登记摘除", GcDebtSource,
                GcDebtC(), "gc-debt-ok\n", 0),
            // B2-4a：Span<u8> native ABI 对拍——stdlib core.native 的
            // spanU8Echo 包装（priv native rigi_span_u8_echo）：XOR 回声
            // 原语读+写+返回三面双宿主一致（复用 e2e 语料 span_native_abi.rg）
            Case("Span<u8> native ABI 回声原语对拍", SerializationGraphCorpus("span_native_abi")),
            // B2-4b1：标准流原语对拍——StandardStreams.standardOutput()/
            // standardError() 经 stdout_write/stderr_write 写通道与
            // stdout_flush 刷新助手（shim.c）写已知字节行，双宿主 stdout
            // 一致（复用 e2e 语料 io_stdstreams.rg：含关闭态拒绝与新
            // 包装对象可写的生命周期断言；stderr 字节走 nativeErr，
            // 不参与 stdout 比对）
            Case("标准流原语 stdout 对拍", SerializationGraphCorpus("io_stdstreams")),
            // UTF-8 分片续写与 Console 文本交错：两路输出按原始字节各自精确断言。
            ("标准流分片 UTF-8 与文本交错", RunSplitUtf8StdstreamsCase),
            ("标准输入原始字节往返", RunBinaryStdinCase),
            // B2-4b2：标准输入 EOF 对拍——StandardStreams.standardInput()
            // 的 stdin_read_start/stdin_read_take「启动即返」卸载原语，
            // 测试进程/native 产物 stdin 均为空/已关闭 → read 挂起后被
            // EOF 唤醒返回 0（§4.4 EOF 路径确定性；含 EOF 粘滞共享、
            // count==0、范围校验、dispose 生命周期断言；复用 e2e 语料
            // io_stdin.rg。挂起有数据路径由 playground 手工探测覆盖——
            // 对拍框架无法喂输入）
            Case("标准输入 EOF 对拍", SerializationGraphCorpus("io_stdin")),
            // char 32 位标量对拍——补充平面字面量标量值、整数→char 值域
            // 检查（越界/代理区/负值 cast 失败抛 CastException）、比较、
            // Span<char> 读写保值、char_to_string 插值输出、Serializable
            // 序列化往返与 String UTF-8 长度语义，双宿主 stdout 一致
            //（复用 e2e 语料 char_scalar32.rg）
            Case("char 32 位标量对拍", SerializationGraphCorpus("char_scalar32")),
            // 块 3-2：core.text 字符串核心操作对拍——切片/标量遍历/查找/
            // 分割/替换/裁剪/拼接（stdlib core/text/text.rg，字节原语
            // text_copy_out/text_from_bytes 双宿主同语义），固定样例组
            //（A中😀B）与边界负例，双宿主 stdout 一致
            //（复用 e2e 语料 text_basic.rg）
            Case("text 核心操作对拍", SerializationGraphCorpus("text_basic")),
            // Array<String> 元素槽 ABI 归一对拍——静态 .string 写与泛型
            // 共享体读经数组头 elemSheet 归一（RUNTIME §5）：asEnumerable
            // for-in（修复前 native 0xC0000005 主路径，含零槽 null 元素
            // 放行）、arrayOfElements 泛型写、while 索引读、手动
            // ListEnumerator current、AtomicSnapshot 快照遍历，双宿主
            // stdout 一致（复用 e2e 语料 array_string_abi.rg）
            Case("Array<String> 元素槽 ABI 归一对拍", SerializationGraphCorpus("array_string_abi")),
            // Array<T> 泛型占位 >8B 内联 struct 元素读写对拍——泛型共享体
            // （typeid 擦除）下内联判定按 elemSheet 的 FlagInlineValue，
            // 禁止 size≤8 启发式：16B/72B struct（含 @Serializable 隐藏
            // 存储的 TimeSpan）经 arrayOfElements 泛型写槽与泛型形参
            // 读/写（修复前 native 把 {sheet指针, box指针} 当元素值落槽
            // /读崩于 rigi_check_fat_ref，VM 正常）；带 String 引用字段
            // struct 覆盖 refMap rich copy（acquire 源盒内嵌引用/release
            // 旧槽）；i32/String/class 负向不回归，双宿主 stdout 一致
            //（复用 e2e 语料 array_inline_struct.rg）
            Case("Array<T> >8B 内联 struct 元素对拍", SerializationGraphCorpus("array_inline_struct")),
            // jsonfix：泛型胖引用形态守卫放行面——装箱标量 tag0 胖值经调用
            // 方静态路径写入 Any-sheet 引用槽（.array<.any>），泛型共享体
            // 构造环（读-写-读）按守卫自证（tid 低 56 位 sheet 带
            // FlagInlineValue）放行（修复前误判「tag0 且 payload 非零即
            // ABI 错配」abort，json_write G3 形态最小定点）；unbox 保真、
            // 深度上限写出、默认深度值语义不回归，双宿主 stdout 一致
            //（复用 e2e 语料 array_boxed_scalar.rg）
            Case("Array<Any?> 装箱标量形态对拍", SerializationGraphCorpus("array_boxed_scalar")),
            // 块 3-3a：core.text Unicode 大小写映射对拍——toLower/toUpper 逐
            // 标量查 case_data.rg 固定表（Unicode 17.0.0 无条件映射，Rigi 层
            // 实现即双宿主同源），契约固定样例（straße→STRASSE、İ→i+U+0307、
            // I→i、ΟΣ/Σ→σ 上下文无关）、无映射保持、ASCII 往返、幂等性、
            // 补充平面、长度变化（一对多），双宿主 stdout 一致
            //（复用 e2e 语料 text_case.rg）
            Case("text 大小写映射对拍", SerializationGraphCorpus("text_case")),
            // 块 3-3b：compare(String, String) 标量字典序对拍——algorithms.rg
            // 新增 String 比较辅助重载（§4.2.3 + §4.3.2 次序统一）：UTF-8
            // 无符号字节字典序（Rigi 层经 toUtf8Span 逐字节比较，双宿主
            // 同源），基本序/前缀短串在前/空串最小、U+E000 vs U+10000 的
            // 标量序 vs UTF-16 码元序差异用例、sorted 联用（含补充平面
            // 字符），双宿主 stdout 一致
            //（复用 e2e 语料 collalgo_string_compare.rg）
            Case("String compare 标量字典序对拍", SerializationGraphCorpus("collalgo_string_compare")),
            // 块 3-4：core.text StringBuilder 与 UTF-8 增量编解码对拍——
            // builder.rg 分块追加/length 字节数/clear/toString 独立性/容量
            // 增长（字节原语 text_copy_out/text_from_bytes 双宿主同语义）；
            // utf8.rg Utf8Decoder 单块与跨块增量解码、isFinal 截断、非法
            // 序列严格抛 TextFormatException 与替换模式 U+FFFD 最大子部分
            // 割（Rigi 层状态机，双宿主同源）、reset 复用；Utf8Encoder
            // encode/encodeChar/encodeTo 与 toUtf8Span 一致性及容量守卫，
            // 双宿主 stdout 一致（复用 e2e 语料 text_builder_codec.rg）
            Case("text StringBuilder 与 UTF-8 编解码对拍", SerializationGraphCorpus("text_builder_codec")),
            // 块 3-5a：core.text 整数 parse/tryParse 对拍——parse.rg 八种
            // 整数（i8/i16/i32/i64/u8/u16/u32/u64）×IntegerRadix 四进制：
            // 进制完全由参数决定（无前缀自动识别/无 Auto）、0x/0X 前缀
            // 必需（hex）、有效数字前缀读取（123abc→123、12 34→12、
            // 1_000→1）、无符号拒负号（-0/-0x0）、范围按完整前缀数学值
            //（128abc 失败、i8 0xFF 不解释补码 -1、i64/u64 极值）、
            // NumberParseException 两类失败（isOutOfRange+position）与
            // tryParse 一致性（失败 null、0 正常）——纯 Rigi 层实现即
            // 双宿主同源，固定样例与失败分类，双宿主 stdout 一致
            //（复用 e2e 语料 text_parse_int.rg）
            Case("text 整数解析对拍", SerializationGraphCorpus("text_parse_int")),
            // 块 3-5b：core.text 浮点 parse/tryParse 对拍——parse.rg
            // float/double 四入口：完整消费语法（.5/1./1e/空白/0x1p3/
            // 1f/尾随垃圾均失败）、区分大小写特殊值四文本（NaN/
            // Infinity/+Infinity/-Infinity）、正中取偶固定向量（2^52/2^53
            // 刻度中点、double 0.1 上邻中点、float 0.1 上邻中点——float
            // 走独立 24 位任意精度路径，不经 double 中转）、最大/最小
            // 正规与次正规边界（含 2.2250738585072011e-308 著名向量）、
            // 负零符号位（1.0/x = -Infinity）、超范围不自动产生 Infinity、
            // 中下溢到带符号零、tryParse 一致性——FltBig（u32 limb 任意
            // 精度整数）+ 逐位长除 guard/sticky 取偶，物化走精确 2 幂
            // 乘法（与舍入模式无关），纯 Rigi 层即双宿主同源，双宿主
            // stdout 一致（复用 e2e 语料 text_parse_float.rg）
            Case("text 浮点解析对拍", SerializationGraphCorpus("text_parse_float")),
            // 块 3-6：core.io 文本流适配器对拍——text.rg 的 TextReader
            //（readChar 逐标量/EOF null、readLine LF/CRLF/单独 CR 与跨
            // 底层读块切分/空行/末尾无换行/空输入 null、BOM 跳过与仅
            // BOM 得空、readToEnd 与 maxBytes 按结果 UTF-8 字节数超限
            // 抛 OutOfBoundException、严格模式非法 UTF-8 抛
            // TextFormatException 且故障化后只允许清理、替换模式
            // U+FFFD）与 TextWriter（write 字节正确、writeLine LF 默认
            // 与 CRLF 配置、写后不自动 flush 与显式 flush 计数、dispose
            // Borrowed 不关 host 但收尾 flush/Owned 关 host、
            // Memory/AutoBuffer 双向 roundtrip）——逐标量解码按码点
            // 宽度计上限（与 String.length 字节口径一致）纯 Rigi 层
            // 状态机即双宿主同源，双宿主 stdout 一致
            //（复用 e2e 语料 io_text_streams.rg）
            Case("io 文本流适配器对拍", SerializationGraphCorpus("io_text_streams")),
            // 块 4-1：Parcel 元数据隔离 + Map 统一键值条目序列 + 字段键
            // 收紧对拍（§4.6.3/D3）——业务字段表只含业务字段（树/图），
            // 保留键（..value/..case/..id/..ref/..data）只进受控元数据槽；
            // 所有 Map（含 String 键）统一为有序键值条目序列，'.rigi.type-
            // identifier'/'content'/'a.b'/空串等业务键往返原样保留、非
            // String 键（i32）真实类型往返；setElement 对非法字段键抛
            // IllegalArgumentException；enum 载荷 case 名不进业务字段表
            //（复用 e2e 语料 parcel_metadata_isolation.rg）
            Case("Parcel 元数据隔离与 Map 条目序列对拍", SerializationGraphCorpus("parcel_metadata_isolation")),
            // 块 4-2：Parcel 动态访问面对拍（§4.6.1/D3）——getDynamic 真实
            // null 且不泄漏 NullSentinel、contains 区分「键不存在」与「键存
            // 在且值为 null」、setDynamic 与 setElement 同款字段名合法性
            // 校验（非法键/非 SB 值抛 IllegalArgumentException，含普通
            // 业务对象与 Pair）且合法 SB 值（标量/容器/Parcel/嵌套/可空
            // 元素）写入后类型化 getElement 读回、iterate 项为
            // Pair<String, Any?> 且 null 原样出现、elementCount/迭代只含
            // 业务字段（meta 不漏）。纯 Rigi 层即双宿主同源，双宿主
            // stdout 一致（复用 e2e 语料 parcel_dynamic_access.rg）
            Case("Parcel 动态访问对拍", SerializationGraphCorpus("parcel_dynamic_access")),
            // 字段名 Unicode 固定 BMP 分类双宿主对拍；与普通动态访问
            // 分开按名执行，非法键抛错不落业务表。
            Case("Parcel Unicode 字段键固定分类对拍", SerializationGraphCorpus("parcel_unicode_key")),
            // 块 4-2：typeOf 装箱视图完备性对拍（native 缺陷修复回归）——
            // getid.var 运行时结果是操作数实际类型的 TypeSheet*，Type<X>
            // 值再装箱为 Any（toString/存 Any 槽/is 右侧 Type 值）需要
            // `.typeid<X>` 视图 sheet；早前收集器只为闭合泛型实参建视图，
            // 非泛型类/标量的 typeOf(x).toString() 在 native 抛 CastException
            // （VM 正常）。本对拍锁定可空形参收窄后取 typeOf、Type 值装箱
            // 进 Any 容器、is 右侧 Type 值三面双宿主一致
            //（复用 e2e 语料 typeof_boxing_views.rg）
            Case("TypeOf 装箱视图对拍", SerializationGraphCorpus("typeof_boxing_views")),
            // typefix：协程帧编组 ABI 同构判定——被协程切分的泛型方法收
            // Type\<T> 值形参，调用点闭合 typeOf(x) 实参在 tainted 臂
            // frame 打包时按 ABI 同构白名单恒等拷贝（早前误 MirBoxAny
            // 装箱、16B 写穿 8B 槽截断第 0 字段 = 视图 sheet，下游
            // typeNameOf 输出 "core::Type\<X>" 抛未登记）；DONE 返回臂
            // 同口径。挂起点 frame 保存对 Type\<T> 局部的 Nullable
            // wrap/unwrap 走 8B 裸指针支（早前按胖引用 extractvalue，
            // native 0xC0000005）。三面双宿主一致
            //（复用 e2e 语料 typeid_frame_marshal.rg）
            Case("TypeId 协程帧编组对拍", SerializationGraphCorpus("typeid_frame_marshal")),
            // 块 4-2 B 面：格式层最小动态面（擦除 SB 视图）对拍——sbKind
            // 标量宽度分类与业务对象拒绝、sbLength、sbElementAt 活值上抛与
            // 元素 null 原样、sbKeyAt/sbValueAt 插入序、sbBuild* 双拼写名称
            // 分发（VM ".i32" / native "core::i32"）与严格核验（名义 is 先
            // 于 cast——rigi_try_cast 带数值宽展，cast 不是类型检查）。分支
            // 只用双宿主共有运行时运算，stdout 一致（复用 e2e 语料
            // sb_container_views.rg）
            Case("SB 容器动态视图对拍", SerializationGraphCorpus("sb_container_views")),
            // 块 4-3：严格恢复契约（§4.6.3 / D3）对拍——标量 cast 前置名义
            // 核验（i64→i32 / double→i32 / String→i32 拒绝）、字段集合完全
            // 匹配（缺失/多余/可空缺项/null→非可空）、集合形状与元素逐层
            // 核验、合法多态/空容器/图模式环与别名/@Temporary 懒恢复往返；
            // 异常类型双宿主一致（复用 e2e 语料 strict_fromparcel.rg）
            Case("严格恢复对拍", SerializationGraphCorpus("strict_fromparcel")),
            // 块 4-3 rich 枚举载荷面：..case 只进 meta 槽、载荷字段集合
            // （含 const）完全匹配、载荷类型互换拒绝；与多态用例分文件
            // （rich 枚举 case 构造与继承多态同单元组合触发既有绑定器
            // 解析缺陷，见 strict_fromparcel.rg 头注）（复用
            // e2e 语料 strict_fromparcel_enum.rg）
            Case("严格恢复枚举对拍", SerializationGraphCorpus("strict_fromparcel_enum")),
            // 块 4-4：Serializer 抽象基类两层职责（§4.6.2/D3）对拍——
            // 用户继承 Serializer 实现 ToySerializer（SB 视图遍历的自定义
            // 标签格式）：抽象 read/write 被基类 serialize/deserialize
            // 便利层复用；传入流借用语义（成功/失败后均未关闭未 flush，
            // 探测计数）；serializeToString/deserializeFromString 往返
            // （嵌套对象、List/Map 字段、null 字段）；null 根值格式层
            // 走通、deserialize 遇 null 根值抛 SerializationException；
            // 严格性衔接（字段集合不匹配/非 Parcel 根值抛
            // SerializationException）；同一实例顺序复用。纯 Rigi 层
            // 状态机 + Parcel 动态访问，双宿主 stdout 一致（复用
            // e2e 语料 serializer_base.rg）
            Case("Serializer 基类对拍", SerializationGraphCorpus("serializer_base")),
            // 块 5-1b：通用字段反射 native 面对拍（§4.6.3「反射与实现
            // 边界」）——typeNameOf / isSerializable / fieldsOf / casesOf
            // 分发按 typeid 装箱 toString 双拼写（VM BIL 别名形 / native
            // canonical 形）并收匹配，返回文本恒 VM 别名形；字段闭包
            // （继承并入、@Temporary / static 排除、可空结构化）、泛型
            // 闭合实参代入、容器键值/元素类型保留、enum case 载荷，
            // 双宿主 stdout 一致（复用 e2e 语料 reflection_fields.rg）
            Case("字段反射对拍", SerializationGraphCorpus("reflection_fields")),
            // 块 5-2a：JsonSerializer 写出面对拍（§4.7 写侧）——SB 表示
            // → JSON 文本全值域（标量/char 补充平面/容器/嵌套对象/可空
            // 字段）；类型信息两形式（.rigi.type-identifier /
            // .rigi.enum-case 与互操作表示，含 RequestResult.Failed 与
            // Map<String, i32> 契约固定示例逐字）；Map content 条目数组
            // 与互操作 String 键对象（非 String 键报错边界含空 Map）；
            // 环/图模式 Parcel 拒绝、无环重复引用展开副本；数字边界
            // （i64/u64 最大、浮点最短往返、负零、NaN/±Infinity 报错）；
            // 缩进配置与深度上限；借用语义（不关闭不 flush 探测计数）；
            // serialize/serializeToString 便利层；浮点 toString 经
            // Ryu 最短往返双宿主呈现一致（复用 e2e 语料 json_write.rg）
            Case("JSON 写侧对拍", SerializationGraphCorpus("json_write")),
            // 最小核心：Serializable 单 Map 字段的内部交替键值 wire
            // 在 typed/plain 两模式均写成精确 JSON；复杂容器组合在
            // SlowCases 中按名显式对拍，不占默认 native 套件。
            Case("JSON 嵌套 Map wire 边界对拍", SerializationGraphCorpus("json_nested_map_wire")),
            // 块 5-2b：JsonSerializer 读取面对拍（§4.7 读侧）——动态读
            // 全值域（对象→Map 键序保留、数组→Array、空对象/空数组区
            // 分、i64/u64 边界与超 u64 报错、小数/指数→double）；语法
            // 拒绝（空文档/第二根值/注释/尾随逗号/重复成员名含转义
            // 还原判重/非法转义/未配对代理/控制字符/数字词法前导零
            // 等）；深度 255/256/257 边界与可配置上限、总字节/字符串/
            // 数字 token 限制；BOM 跳过与错误偏移精确（含 BOM 计数）；
            // 借用语义（不关闭探测）；写读往返（Map content 形态恢复
            // 活 Map、数值键重复判重、枚举两形态恢复 Parcel 且原样再
            // 写出、类型标识恢复 Parcel）；readAs 类型引导（i32 不经
            // i64、char 恰一标量含补充平面、List/Map/嵌套对象/可空
            // 字段、枚举两形态与未知 case、Map 业务键原样、Type\<T>
            // 值形态）；deserialize 走通与宽度不兼容负例（复用 e2e
            // 语料 json_read.rg）
            Case("JSON 读侧对拍", SerializationGraphCorpus("json_read")),
            // 块 5-2c：readAs 嵌套对象类型引导（§4.7.1「递归到每个成员
            // 时重复这一判断」）+ 按名反射重载对拍——嵌套 i32/i16 宽度
            // 保真（经 toParcel 声明宽度槽二次确认）、嵌套嵌套、嵌套
            // 枚举（无载荷裸 case 名/对象形态、带载荷 case 对象、保留
            // 类型信息形态、未知 case/形状错误负例）、List/Map 成员的
            // 自定义对象元素引导与顶层 List/Map 目标通道、可空嵌套
            // 对象显式 null 与存在值、嵌套字段缺失/多余/null 进非可空
            // （严格恢复）与类型不匹配/小数词法（读取面）负例、自引用
            // 类型 256 层链正常恢复/257 层深度上限拒绝；fieldsOf/
            // casesOf/isSerializable 按名重载直接断言（字段闭包文本、
            // case 载荷、未登记名抛 IllegalArgumentException）（复用
            // e2e 语料 json_read_nested.rg）
            Case("JSON 读侧嵌套引导对拍", SerializationGraphCorpus("json_read_nested")),
            // jsonnullfix：可空集合元素的裸 null（§4.7.1 按目标声明可空性
            // 读取 / §4.7.5 null 对应空值）对拍——readAs 对象内
            // Array<i32?> / List<String?> / Map<String, i32?> 显式 null
            // 元素/值（混排/空容器/嵌套容器/String "null" 与 null 区分）
            // 经 wire null 哨兵记录受控桥恢复；写出两模式（默认/互操作）
            // null 元素逐位输出 JSON null、不泄漏 NullSentinel 内部表示；
            // 非可空元素遇 null 仍拒绝（复用 e2e 语料
            // json_nullable_elements.rg）
            Case("JSON 可空集合元素裸 null 对拍", SerializationGraphCorpus("json_nullable_elements")),
            // wb-5-2d：readAs 顶层自定义对象容器恢复对拍（§4.7.1 顶层
            // 对象容器无免责）——手写普通 JSON 固定样例的
            // Array<RcPoint> / List<RcPoint> / Map<String, RcPoint> 根
            // （普通字典与 §4.7.3 content 包装两形态）、i32/String 字段
            // 精确恢复（toParcel 宽度槽二次确认）、空容器依声明恢复、
            // 可空对象元素/值（null 混排）、嵌套容器 List<List<RcPoint>>
            // （子 envelope 递归）、合法派生类型（类型标识恢复进基类声
            // 明）与不兼容标识/未登记标识拒绝、Type<T> 值重载入口、
            // 元素字段缺失/多余/null 进非可空/标量类型不匹配/重复键/
            // 形状错误负例、write 两模式产物 readAs 往返补充（Map
            // envelope 内部载荷转为对外 Map 对象表示）（复用
            // e2e 语料 json_root_object_containers.rg）
            Case("JSON 顶层对象容器对拍", SerializationGraphCorpus("json_root_object_containers")),
            // 已物化闭合数组的 typeOf 装箱视图对拍：setDynamic(Array<String>
            // 活值) → isSbRepresentable → typeOf(值) 装箱需 `.typeid<.array<
            // .string>>` 视图做身份选择；视图收集按 GuaranteedSheet 物化
            // 边界覆盖内建 Array 闭合具化，不依赖任何无关表达式的
            // getid.type 使用点（缺失即抛 CastException(.typeid<.any> →
            // Array<String>)）（复用 e2e 语料 typeid_view_array_string.rg）
            Case("TypeId 数组视图装箱对拍", SerializationGraphCorpus("typeid_view_array_string")),
            // 块 6-1（§4.9.1 / §4.9.4 / §4.9.5，D6）：core.time 时间值
            // 纳秒化与运算对拍——TimeSpan 规范化分解（-1ns = -1ms +
            // 999999ns、零唯一表示）、七单位换算与溢出、total 属性族
            // 向零截断与超 i64 报错、加减进位借位、取负（最小值报错）、
            // 乘除整数的 base-100 limb 精确宽中间形态（不足一纳秒向零
            // 截断、除零报错）、比较含纳秒；DateTime ±TimeSpan、两时刻
            // 相减保完整纳秒精度、UTC 公历范围构造与运算校验；
            // Timer.schedule 正持续时间向上取整（<1ms 余量不提前到期，
            // 墙上时钟断言）与过去时刻立即触发；Serializable 往返与
            // 恢复路径不变量校验（纳秒 0..999999、DateTime 范围越界
            // Parcel 拒绝）（复用 e2e 语料 time_values.rg）
            Case("时间值对拍", SerializationGraphCorpus("time_values")),
            // DateTime 全公历跨度不经 i64 总纳秒；双方向与纳秒边界独立对拍。
            Case("DateTime-wide 对拍", SerializationGraphCorpus("time_wide_datetime")),
            // 块 6-2（§4.9.2 / §4.9.3 / §4.9.5，D6）：core.time 文本格式
            // 对拍——DateTime RFC 3339 收窄子集（Z/±偏移/fraction 1～9 位
            // 直接形成纳秒、严格全文消费、拒绝缺偏移/非法日期/秒 60/24:00/
            // -00:00/超九位/尾随垃圾/空白、±偏移换算 UTC、当地与 UTC 界内、
            // 往返恒等含纳秒、显示偏移超范围报错、tryParse 一致性、
            // TimeParseException 非法文本与超范围两类可区分）；TimeSpan
            // ISO 8601 Duration 日时分秒子集（固定样例 P1DT2H3M4.5S /
            // -PT0.000000001S / PT90M→PT1H30M、P/PT/P1DT/P1M/P1W/混合符号/
            // 前导加号/超九位/空白拒绝、零统一 PT0S 含负零归一、范围溢出
            // 不截断不回绕、向零截断衔接、往返恒等）（复用 e2e 语料
            // time_text.rg）
            Case("时间文本对拍", SerializationGraphCorpus("time_text")),
            // §4.9.1 UTC 六分量：紧凑固定样例，独立 VM/native 对拍。
            Case("DateTime UTC 分量对拍", SerializationGraphCorpus("time_utc_components")),
            // 块 6-3（§4.9.4 / §4.9.5，D6）：core.time 单调时钟与 Stopwatch
            // 对拍——MonotonicClock.now() 顺序采样不倒退（允许相等）、
            // 采样差非负 TimeSpan、反向求差负 TimeSpan（有符号表示）、
            // MonotonicInstant compareTo/equals；Stopwatch 初始停止且零、
            // start/stop 幂等、start→sleep→stop 累计 ≥ 睡眠量级、再次
            // start 不丢前段、reset 清零停止、restart 清零开始、运行中
            // elapsed 含当前区间并随等待增长；跨 Worker（ComputeExecutor
            // 挂起/恢复）读数不倒退（同一进程时钟域）。MonotonicInstant/
            // Stopwatch 无 Serializable（§4.9.5 注释级）。VM/native 原语
            // 同语义（排除整机睡眠；Windows QueryUnbiasedInterruptTime
            // Precise / Linux CLOCK_MONOTONIC）（复用 e2e 语料
            // time_clock.rg）
            Case("单调时钟对拍", SerializationGraphCorpus("time_clock")),
            // 块 6-4（§4.11.1–§4.11.3 / D7）：core.math 函数与常量对拍——
            // abs 全宽度（有符号最小值抛 OutOfBoundException、无符号原样、
            // 浮点 -0→+0、NaN 传播）；min/max/clamp（NaN 传播、±0 规则、
            // clamp 无效区间与 NaN 边界抛错、无穷边界）；floor/ceil/trunc/
            // round 两模式（负数、±0 符号保持、NaN/无穷）；sqrt 正确舍入
            // （√2/√(1/2)/√10/√(1e-300) 与 float √2 等值断言、
            // sqrt(-0)=-0、负有限→NaN）；
            // pow 固定组合表（0^0/NaN^0/1^NaN=1、负底非整数指→NaN、零/
            // 无穷/奇偶整数指/±0 组合、上溢±inf、下溢±0——VM/native 经
            // fdlibm e_pow/e_powf 同款移植实现，不依赖宿主库差异）；
            // exp/ln/log2/log10 定义域与特殊值；三角/反三角（弧度）；
            // atan2 四象限 ±0 与 NaN 传播；超越函数 4 ULP 验证（独立
            // mpmath 高精度真值正确舍入到目标格式的参考值，间距按 |ref|
            // 规格化度量，向量参考值全在正规区；阈值 4.0，不叠加舍入
            // 余量）；NaN/无穷/±0 分类
            // 单独核对；常量 pi/e/tau 与已知 double 最近值逐文本一致
            // （字面量经编译器前端单一路径正确舍入，VM/native 位表示
            // 相同）（复用 e2e 语料 math_basic.rg）
            Case("数学函数对拍", SerializationGraphCorpus("math_basic")),
            // 施工块 6-5：core.math Random 对拍——SplitMix64 展开 +
            // xoshiro256** 1.0 已知向量（种子 0/1/0xDEADBEEF/u64max）、
            // 全宽整数最高位消费、有界拒绝采样（非法区间不消耗、单元素
            // 区间消耗一次、跨零点宽度、u8 [0,10) 20000 发逐桶计数）、
            // 浮点网格（24/53 位 / 2^24 / 2^53 向量 + [0,1) 与网格对齐）、
            // bool 最高位、fillBytes 小端/尾块丢弃/零长度/越界不消耗、
            // 混合调用序列可复现、无参构造系统随机源两次序列不同
            // （VM 侧 RandomNumberGenerator / native 侧 BCryptGenRandom，
            // 只要求材料质量不要求同种子；失败路径无法常规测试）。
            // 显式种子序列由算法契约钉死，VM/native 与 Windows/Linux
            // 逐位一致（复用 e2e 语料 math_random.rg）
            Case("Random 已知向量与范围语义对拍", SerializationGraphCorpus("math_random")),
            // 施工块 7-1（STDLIB §4.5.1 / §4.5.2 / §4.5.9，D5）：core.fs
            // Path 词法对拍——构造拒绝（空文本/内嵌 NUL/C:foo/\foo/设备
            // 命名空间/保留设备名/末尾空格点/ADS 冒号）、盘符与 UNC 接受、
            // equals/hash 文本精确（大小写敏感）、normalizeLexically（配对
            // 消解/相对开头 .. 保留/绝对不越根/分隔符统一）、join（绝对
            // 参数报错携双路径）、toAbsolute/relativeTo（根不同报错）、
            // root/parent/name/extension/nameWithoutExtension/isAbsolute、
            // Serializable 往返与恢复路径构造校验。平台判定经私有原生
            // 原语 host_is_windows（VM 宿主判定/native _WIN32 编译期判定，
            // 两形态同语义）；①②④⑤⑥ 平台分歧断言按宿主分支——Windows
            // 侧保留本机实测原断言，Linux 侧对称覆盖（'/' 根组合推导、
            // Windows 盘符文本在 Linux 是普通相对名称：构造接受、
            // toAbsolute/relativeTo 拒其为非绝对 base/异根，§4.5.2
            //「语法遵循当前平台」）
            //（复用 e2e 语料 fs_path.rg）
            Case("路径词法对拍", SerializationGraphCorpus("fs_path")),
            // 施工块 7-2（STDLIB §4.5.6 / §4.5.9 + §3.2/§3.3，D5）：
            // core.fs native 原语层对拍——挂起写/flush（后台线程 + 事件
            // 唤醒，stdin 先例两段式）与同步 seek/tell/getLength/
            // setLength（缩短截断/增长补零/游标保持，§4.5.6 明文）/
            // stat/lstat（kind/length/时间原始对，非普通文件不伪造长
            // 度）/mkdir/rmdir/unlink/rename（NoReplace 系统不替换保证
            // 与 Replace 文件覆盖，§4.5.7）/diropen/dirread（计数 + 名
            // 称命中 + 结束后持续 null）/realpath（解析为绝对路径）双
            // 宿主往返；错误映射 NotFound/IsDirectory/AlreadyExists/
            // NotDirectory 经 FileSystemErrorKind 分类（不解析错误消
            // 息，§4.5.9）。目录名取系统随机源，e2e 并行与双宿主各自
            // 唯一；末尾自清理。VM 侧 Windows 实测，Linux 分支按契约
            // 实现未实测（fs_path.rg 已转双平台分支断言口径）
            //（复用 e2e 语料 fs_primitives.rg）
            Case("fs 原语对拍", SerializationGraphCorpus("fs_primitives")),
            // nullablefix（SYNTAX §3.4 可空判等条）：同型 T? 的 ==/!= =
            // nullness 短路 + 解包内层判等——内层矩阵（String 引用内建/
            // i32 标量/EqClass 引用/PlainStruct 值类型无 hash override/
            // core.fs.Path 富 struct 双覆写）× 值矩阵（同值/异值/单空/
            // 双空）× 来源矩阵（局部/形参/字段）× ==/!=；Any?→Any 的
            // null 解包放行（修复前 native 抛 CastException、Map<String,
            // Any> 非空解包路径不变）。修复前双宿主各自错误：native 胖值
            // 位比同值内容恒 false；VM 覆写 hash 的 struct 崩「字段访问
            // 目标不是对象」、无 hash override 值类型身份哈希误判
            //（复用 e2e 语料 nullable_equality.rg）
            Case("可空判等对拍", SerializationGraphCorpus("nullable_equality")),
            // richretrfix 回归：函数返回含 Nullable 装箱（tag1 盒）字段的
            // rich struct——交付即移动契约（TerminatorEmitter 纯 memcpy，
            // 修复前 acquire 回写副作用使 out 拿到已释放块）。复用 e2e 目
            // 录组 rich_return_nullable（跨命名空间 + 同文件两形态；多文
            // 件组走 RunCaseFiles），MEMTRACK 零泄漏口径同全局
            MultiFileCase("rich struct 返回 Nullable 字段对拍",
                CorpusGroup("rich_return_nullable")),
            // WRAP-001：复用永久语料，三目标×四 init 形态对拍真实初始化路径。
            Case("WRAP-001 Value 字段初始化矩阵", SerializationGraphCorpus("wrap001_value_initializers")),
            Case("WRAP-001 Method 字段初始化矩阵", SerializationGraphCorpus("wrap001_method_initializers")),
            Case("WRAP-001 Entity 字段初始化矩阵", SerializationGraphCorpus("wrap001_entity_initializers")),
            Case("WRAP-001 wrapper 嵌套初始化顺序", SerializationGraphCorpus("wrap001_nested_initializers")),
            Case("WRAP-001 class struct 初值保持", SerializationGraphCorpus("wrap001_class_struct_initializers")),
            Case("WRAP-001 泛型参数字段与前向构造", SerializationGraphCorpus("wrap001_review_generic_forward")),
            // 以下完整覆盖说明对应 SlowCases 的「fs 文件流对拍（完整慢例）」；
            // 核心与完整语料均已慢门控；默认仅留独立字节预期的小烟测。
            // 施工块 7-3（STDLIB §4.5.6 + §4.4，D5）：core.fs 文件流对拍
            // ——FileWriteMode 五模式全表（OpenExisting 不截断/CreateNew
            // 系统仅创建不覆盖/CreateOrTruncate 截断/OpenOrCreate 不截断/
            // Append 不存在则创建 + 系统追加机制每次写到达当时末尾，含
            // 另一写入者并发增长后仍接其尾部）；读流 EOF 不粘滞（另一
            // 写入者追加后再次读取可见新增）、seek 末尾之后读即 EOF、负
            // 位置范围错误、getLength 实时性；输出流 setLength 缩短截断
            // /增长补零/游标保持（含游标已在新末尾之后）、越末写入空隙
            // 补零、负长度范围错误；追加流 as ISeekableStream 被拒（接
            // 口能力由具体返回类型体现）；flush 持久化（写-flush-close
            // 重开读回，FlushFileBuffers/fsync 语义由原语层保证）；dispose
            // 关闭后各操作 IllegalStateException + 幂等 + using 组合；
            // pipe/MemoryOutputStream/AutoBuffer/readAll/TextReader/
            // TextWriter 组合。错误映射 NotFound/AlreadyExists 经
            // FileSystemErrorKind（不解析错误消息，§4.5.9）。目录名取系
            // 统随机源各自唯一，产物限唯一目录内、末尾自清理；VM 侧
            // Windows 实测，Linux 分支按契约实现未实测（fs_primitives.rg
            // 同口径）；默认仅复用独立烟测 fs_stream_smoke.rg；两大例在慢组。
            ("fs 文件流对拍", RunFileStreamSmoke),
            // 施工块 7-4（STDLIB §4.5.3 + §4.5.9 序列化条目 + §4.5.5
            // removeLink，D5）：core.fs 信息查询与链接对拍——getInfo/
            // tryGetInfo/exists（不存在 NotFound→抛/null/false 一致；
            // followLinks=false 末段链接形态经既有 junction 只读探测，
            // kind=.Link、length null）；非 NotFound 失败不伪装成不存
            // 在——库层转换口径 = 宿主归一类别（先原语探测超长末段名
            // 的宿主类别：native 侧 \\?\ 长路径折叠 NotFound、VM 侧
            // .NET 归非 NotFound 类别的双端原始错误差异，再断言非
            // NotFound 原样抛、NotFound 才转
            // null/false；中间分量是文件原样失败，只钉不吞面）；FileInfo
            // 组装（kind
            // 映射/length 非普通文件 null/时间亚毫秒保留与合理量级、
            // createdAt ≤ modifiedAt）；getRealPath 绝对化解析（结果
            // exists 为真）与不存在 NotFound；removeLink 非链接
            // WrongType（条目保留）、不存在 NotFound；FileInfo deepCopy
            // /Serializable 往返（不查盘不刷新——记录后追加文件，记录
            // length 不变、实时查询得新值）与恢复校验（非空字段收
            // null 拒绝）。既有链接探测只钉 followLinks=false 识别面
            //（系统既有 junction 属系统过滤保护条目：VM 侧 ResolveLink
            // Target(final) 返回 NotFound、native 侧可正常跟随——链接
            // 跟随面由普通链接承担，创建入口 §4.5.3 后置且 Windows 需
            // 权限，环境限制见块报告）；删链接不删目标的删除面由
            // fsUnlink 系统语义与 7-2 原语对拍承担；目录名
            // 取系统随机源各自唯一，末尾自清理；VM 侧 Windows 实测，
            // Linux 分支按契约实现未实测（fs_filestream.rg 同口径）
            //（复用 e2e 语料 fs_info.rg）
            Case("fs 信息查询与链接对拍", SerializationGraphCorpus("fs_info")),
            ("Linux getRealPath 链接前点点独立预期", RunRealpathDotDotLinux),
            // 施工块 7-5（STDLIB §4.5.4 + §4.5.5，D5）：core.fs 目录读取
            // + 创建/删除对拍——create/createAll（父级缺失 NotFound、目
            // 标已存在 AlreadyExists、createAll 逐级补建并允许已有目录、
            // 中途遇普通文件 AlreadyExists 且不回滚既有子树、失败目标
            // 未创建）；DirectoryReader 流式读取（恰好 5 条目逐一读出、
            // 不排序不递归、无 `.`/`..`、含隐藏项、条目 path 为基于打开
            // 基准的绝对路径且末段=条目名称、kindHint 正确或 null 容忍
            // ——Linux readdir d_type=DT_UNKNOWN 时提示不可得）、结束后
            // 持续 null（重扫须重新打开）、提前离开 seq using 收尾、重
            // 复 dispose 幂等、关闭后 read IllegalStateException；故障
            // 态 I/O 失败面无法在合法名称环境内构造（名称解码失败
            // InvalidNameEncoding 由 7-2 原语对拍承担）；Directory.list
            // 整体收集、maxEntries 超限报错不截断（-1 不设上限、其他负
            // 值非法、空目录空列表）；删除族 File.delete 文件/目录
            // IsDirectory、Directory.delete 空目录/普通文件
            // NotDirectory/非空 DirectoryNotEmpty（VM 侧 rmdir 非空归一
            // 类别与 native 不同，由公共层判空统一）/不存在 NotFound、
            // deleteIfExists 只把不存在转 false 不吞其他失败。断链与指
            // 向目录链接的删除形态：无创建链接入口（Windows 需权限）
            // 无法构造，由 7-2 unlink/rmdir 系统调用语义与 lstat 判别承
            // 担。createAll 中途遇普通文件的错误类别按平台分支断言
            //（Windows AlreadyExists / Linux NotDirectory——「文件/
            // 子路径」的 stat 错误类别是宿主系统差异：Windows
            // ERROR_PATH_NOT_FOUND → NotFound、Linux ENOTDIR →
            // NotDirectory，§4.5.3 平台语义注记）。目录名取系统随机源
            // 各自唯一，末尾自清理；双平台分支断言，各侧按平台语义实测
            //（fs_info.rg 同口径）。契约拼写注：§4.5.4 Directory.open 的 open 是语言
            // 保留修饰符关键字（M31 拦截声明名位），实现用 openReader
            // 过渡拼写（见块报告待裁决问题）
            //（复用 e2e 语料 fs_directory.rg）
            Case("fs 目录读取与创建删除对拍",
                SerializationGraphCorpus("fs_directory")),
            ("文件断链删除 VM/native", RunDanglingDeleteCase),
            ("junction 删除 VM/native", RunJunctionDeleteCase),
            ("Windows junction Replace 原生公共 move 与原字节", RunJunctionReplaceNativeCase),
            ("文件断链 CreateNew VM/native", RunDanglingCreateNewCase),
            // chainfix：值类型 receiver 写穿接管的 setter 使用点可见性口径
            // 对拍——priv set 链直调/语句位 void 直调/深链混合可见性/pub set
            // 突变外溢/只读 place 突变不外溢（复用 e2e 语料
            // accessor_chain_writeback.rg）
            Case("访问器链直调写穿对拍",
                SerializationGraphCorpus("accessor_chain_writeback")),
            // 施工块 7-6（STDLIB §4.5.7 + §4.5.8，D5）：core.fs 复制/
            // 移动/临时资源对拍——File.copy 两模式全表（CreateNew 默认
            // 与显式：新目标内容一致、已有目标 AlreadyExists 且内容不
            // 动；Overwrite 打开或创建+写入前截断——旧目标更长时截到
            // 源长度；均不自动创建父目录 NotFound、复制后源不变、源缺
            // 失 NotFound、源为目录 IsDirectory）；自复制拒绝按实际打
            // 开句柄的系统文件身份（fs_same_file 原语：Windows 卷序列
            // 号+文件索引 / Linux st_dev+st_ino，7-6 双宿主同语义——同
            // 路径 CreateNew AlreadyExists、同路径 Overwrite Other 且
            // 源未被截断即「截断前拒绝」的直接证据；经硬链接/不同路径
            // 访问同一文件的形态本机无创建链接入口无法构造，由原语承
            // 担）；move（§4.5.7 系统移动/重命名能力：文件/目录移动、
            // NoReplace 遇已有目标 AlreadyExists 两侧不动、Replace 覆
            // 盖文件、Replace 对目录目标 IsDirectory 不覆盖不合并、目
            // 录移动要求目标不存在两模式均成且条目随迁、跨文件系统
            // CrossDevice 由原语 EXDEV 归一映射承担——单卷宿主无法稳
            // 定构造）；临时文件/目录（§4.5.8：显式目录+prefix、原子
            // 创建返回已打开输出流、同 prefix 多次互异不冲突、关闭后
            // 不自动删除、prefix 含 '/'/NUL 报 InvalidPath——'\' 仅
            // Windows 拒绝不钉平台）。目录名取系统随机源各自唯一，产
            // 物限唯一目录内、末尾自清理；VM 侧 Windows 实测，Linux 分
            // 支按契约实现未实测（fs_directory.rg 同口径）
            //（复用 e2e 语料 fs_copymove.rg）
            Case("fs 复制移动临时资源对拍",
                SerializationGraphCorpus("fs_copymove")),
            // Linux fs_rename 原生符号机制回归：不通过 VM 替代 native，
            // 稳定真实目录源 Replace 空目录/文件/断链目标、文件与链接覆盖、
            // Barrier 并发争用；Windows 路径用现有 VM/语料测试。
            NativeOnlyCase("Linux native fs_rename Replace 目录不覆盖",
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"fs_replace_native_probe\")\n" +
                "native func probe(): i32\n" +
                "pub func main(): i32 { return probe() }\n",
                FsReplaceNativeProbe, "fs-replace-native-ok\n", 0,
                useFixtureRoot: true),
            NativeOnlyCase("Linux native fs_stat statx 创建时间",
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"fs_birth_native_probe\")\n" +
                "native func probe(): i32\n" +
                "pub func main(): i32 { return probe() }\n",
                FsBirthNativeProbe, "fs-birth-native-ok\n", 0,
                useFixtureRoot: true),
            // 施工块 8-1（STDLIB §8 应用验收场景 1-4）：MVP 应用闭环对拍
            // ——四类组合场景各复用一份完整 e2e 语料，VM/native 双宿主
            // stdout/退出码一致（时间相关断言均为不变量与量级，不依赖
            // 墙钟具体值与平台分辨率）：
            //   场景 1 配置转换：内存流 JSON → readAs 类型引导严格恢复
            //     （嵌套对象/List<i32>/String/bool）→ 改两字段 →
            //     keepTypeInfo=false 互操作写回逐字断言；坏 JSON 解析错误
            //     原样传播（JsonException 携非负 offset）；类型不匹配严格
            //     恢复报错；借用流读/写两路不关闭不 flush（探测计数）+
            //     调用方 seq using/dispose 各层收尾（复用 e2e 语料
            //     accept_config_transform.rg）
            Case("验收场景1 配置转换对拍", SerializationGraphCorpus("accept_config_transform")),
            //   场景 2 数据处理：约 17 KiB 整体输入（>4 倍 TextReader
            //     4 KiB 预读缓冲，中文/补充平面标量跨块）逐行 readLine →
            //     保留全部行与 readToEnd/join 双路径逐字对照 → parse →
            //     fold 聚合（count/sum/min/max）→ map/filter/sorted →
            //     math（round 定标均值、sqrt、pow、clamp）确定值输出
            //     （复用 e2e 语料 accept_data_pipeline.rg）
            Case("验收场景2 数据处理对拍", SerializationGraphCorpus("accept_data_pipeline")),
            //   场景 3 对象状态往返：一致状态点 Parcel → JSON 写入
            //     AutoBuffer（保留类型信息逐字断言，重复引用展开两份）→
            //     输入流 readAs 恢复强类型对象（i32 按声明宽度）→ 再写出
            //     文本与首写一致（类型标识往返稳定）→ current/backup 恢复
            //     后为独立副本（修改互不影响）→ 自环 Parcel 写出报错
            //     （offset -1）→ 恢复对象经 Messenger 发送/Reader 接收，
            //     send 深复制快照独立于发送方（JSON 非消息路径必要中间
            //     步骤）（复用 e2e 语料 accept_state_roundtrip.rg）
            Case("验收场景3 对象状态往返对拍", SerializationGraphCorpus("accept_state_roundtrip")),
            //   场景 4 时间与耗时：DateTime RFC 3339 子集解析/输出（偏移
            //     换算 UTC、9 位纳秒）与 ISO Duration（P1DT2H3M4.5S、负值
            //     整体负号）；纳秒运算（TimeSpan 乘除/加减/取负、DateTime
            //     ±TimeSpan 跨日进借位、时刻相减保纳秒）；Stopwatch 与
            //     MonotonicClock 测量 32 次处理循环（elapsed ≥ 0 且
            //     < 60 s 量级、采样不倒退、reset/start/stop 幂等、
            //     sleep(20) 累计 ≥ 15 ms 下限）（复用 e2e 语料
            //     accept_time_measure.rg）
            Case("验收场景4 时间与耗时对拍", SerializationGraphCorpus("accept_time_measure")),
            // 施工块 8-2（STDLIB §8 应用验收场景 5-7）：MVP 应用闭环对拍
            // 下半——三类组合场景各复用一份完整 e2e 语料，VM/native 双
            // 宿主 stdout/退出码一致（文件系统产物限程序内组装的相对唯
            // 一目录、末尾整体删除自清理，fs_copymove.rg 同口径；随机数
            // 全部固定种子，无墙钟/系统随机源依赖）：
            //   场景 5 文件保存恢复：显式模式文件流（openWrite
            //     .CreateOrTruncate / openRead）+ JsonSerializer 两代写读
            //     往返（keepTypeInfo 默认逐字精确断言、readAs 严格恢复、
            //     同路径截断重写无残留）；借用探测流证 using 逆序清理
            //     （内层先收尾、序列化器不 flush 不关闭、外层收尾即持久
            //     化刷新点——关闭重开逐字完整）+ dispose 后操作
            //     IllegalStateException + 重复 dispose 幂等；文本文件双
            //     层嵌套 using 组合 TextWriter/TextReader（逐行 + EOF +
            //     readToEnd 对照）；不存在 NotFound、坏 JSON
            //     JsonException（非负 offset）、类型不匹配严格恢复报错；
            //     临时目录自清理（复用 e2e 语料 accept_file_roundtrip.rg）
            Case("验收场景5 文件保存恢复对拍", SerializationGraphCorpus("accept_file_roundtrip")),
            //   场景 6 可复现随机数据：固定种子 Random 已知向量锚点
            //     （math_random.rg 口径的种子 42 首发有界/全宽/单值区间/
            //     浮点向量）+ 同程序双实例同调用序列逐值相等（24 发有界
            //     i32、12 发 double、8 发 float、fillBytes 16 字节、8 发
            //     有界 u8）+ 区间/网格不变量（[min,max)、[0,1)、×2^53/
            //     ×2^24 整数）→ 集合 map/filter/fold/sorted（与手写循环
            //     对照：聚合同值、计数一致、加倍和、非降+首末极值+总和
            //     保持）→ math round 定标/clamp/floor 确定值输出（输出
            //     面全为整数，不依赖浮点文本格式）（复用 e2e 语料
            //     accept_random_pipeline.rg）
            Case("验收场景6 可复现随机数据对拍", SerializationGraphCorpus("accept_random_pipeline")),
            //   场景 7 目录与文件管理：显式给定相对唯一目录内 create/
            //     createAll 逐级补建 + 临时文件/临时目录（前缀命中、原子
            //     创建流写后关闭读回、关闭不自动删除）→ DirectoryReader
            //     恰好 4 条目遍历（name 命中、无 ./..、条目 path 绝对且
            //     末段=名称、kindHint 正确或 null 容忍）+ 提前停止读取
            //     （读 2 条即离开 using，重扫须重新打开）→ 复制规则
            //     （CreateNew 内容一致/冲突 AlreadyExists 旧内容不动/
            //     Overwrite 截断）→ 移动规则（NoReplace 归档应用流/
            //     冲突两侧不动/Replace 覆盖文件/Replace 目录目标
            //     IsDirectory）→ 删除规则（deleteIfExists 真/假、缺失
            //     NotFound、非空 DirectoryNotEmpty、目录 IsDirectory、
            //     空目录成功、文件 NotDirectory）→ 失败后的部分结果
            //     （已建子树不回滚、失败目标未创建；createAll 遇普通
            //     文件挡路的类别平台分支：Windows AlreadyExists /
            //     Linux NotDirectory，fs_directory.rg ① 同口径）；链接
            //     条目无创建
            //     入口（Windows 需权限）无法构造，由 7-2 原语语义承担
            //     （语料注释说明）；产物限唯一目录、末尾整体删除自清理
            //    （复用 e2e 语料 accept_dir_management.rg）
            Case("验收场景7 目录与文件管理对拍", SerializationGraphCorpus("accept_dir_management")),

        };

    }
}
