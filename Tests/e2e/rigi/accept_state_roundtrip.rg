// ============================================================================
// accept_state_roundtrip.rg —— 施工块 8-1：MVP 应用验收（STDLIB §8）场景 3
// 「对象状态往返」：在一致的应用状态点取得 Parcel → 将 JSON 写入
// AutoBuffer → 通过其输入流读回并恢复对象 → 继续处理消息。NativeE2E
// 「验收场景3 对象状态往返对拍」Case 复用本语料（VM/native 双宿主一致）。
//
//   ① 保留 Rigi 对象类型：write 默认形态（keepTypeInfo=true）带
//      .rigi.type-identifier；readAs 按目标声明 + 类型标识严格恢复出强
//      类型 AppState/WorkItem 对象；恢复对象再次 toParcel 写出的文本与
//      首次逐字一致（类型标识往返稳定）。
//   ② 字段按目标声明解释：readAs 通道 i32 字段按声明宽度直接构造（数值
//      不经 i64 中转——严格恢复成功即证，§4.7.5 已知目标走 readAs）。
//   ③ 不支持环形引用：Parcel 自环（setDynamic 指回自身）写出抛
//      JsonException（写出错误 offset = -1，无虚构输入偏移）。
//   ④ 无环重复引用展开为独立副本：current 与 backup 两字段引用同一
//      WorkItem 实例 → 写出展开为两份对象文本 → 恢复后是两个独立对象
//      （修改 current.priority 不影响 backup.priority，也不影响原对象）。
//   ⑤ 继续处理消息：恢复的对象经 Messenger 发送、Reader 接收验证；
//      send 完成点即队列已接受深复制快照——修改发送方对象不影响已接收
//      副本。注释声明：JSON 不是 Messenger 的必要中间步骤（消息路径的
//      快照语义由队列自身深复制承担，与本场景前半的 JSON 文本无关；
//      写出的 AutoBuffer 在发送前即不再使用）。
//   ⑥ 保留全部数据的步骤声明：JSON 写出面完整构建文本后一次性交给
//      AutoBuffer（整体内存形态，§4.7.2）；AutoBuffer 为动态扩容字节
//      存储，读回经其输入流整体 readAll——无流式中间态。
//   ⑦ 不依赖路径或命令行参数 API。
// expect-output: accept-state restored session=sess-01 tick=42 title=校准任务 priority=3
// expect-output: accept-state spread current=9 backup=3
// expect-output: accept-state selfloop-ok
// expect-output: accept-state message session=sess-01 current=9 backup=3 independent=true
// expect-output: accept-state-ok
// expect-exit: 0
// ============================================================================
import core.collections.*
import core.io.*
import core.messaging.*
import core.serialization.*
import core.serialization.json.*
import core.text.*

// ── 业务状态类型（shared：经 Messenger 传递要求）──

@Serializable
pub shared class AwWorkItem {
    pub var id: i32
    pub var title: String
    pub var priority: i32
    pub init(_ -> id, _ -> title, _ -> priority)
}

@Serializable
pub shared class AwAppState {
    pub var session: String
    pub var tick: i64
    pub var current: AwWorkItem
    pub var backup: AwWorkItem
    pub init(_ -> session, _ -> tick, _ -> current, _ -> backup)
}

// ── 助手 ──

func awFail(code: i32, msg: String): i32 {
    Console.println("FAIL ${code}: ${msg}")
    return code
}

func awExpect(cond: bool, code: i32, msg: String): i32 {
    if (cond) { return 0 }
    return awFail(code, msg)
}

// 期望 JSON 写出抛 JsonException（自环/图模式拒绝通道）。
func awExpectWriteError(ser: JsonSerializer, value: Any?, code: i32): i32 {
    const sink = new MemoryOutputStream()
    var threw = false
    var off: i64 = 0L
    try {
        ser.write(value, sink)
    } catch (e: JsonException) {
        threw = true
        off = e.offset
    } finally(closer) {
        sink.dispose()
    }
    if (not threw) { return awFail(code, "写出未抛 JsonException") }
    if (off != (0L - 1L)) { return awFail((code + 100), "offset=${off} 应为 -1") }
    return 0
}

pub func main(): i32 {
    const ser = new JsonSerializer()

    // ── 一致的应用状态点：current 与 backup 引用同一 WorkItem 实例 ──
    const sharedItem = new AwWorkItem(7, "校准任务", 3)
    const state = new AwAppState("sess-01", 42L, sharedItem, sharedItem)

    // ── 取得 Parcel → JSON 写入 AutoBuffer（保留类型信息默认形态）──
    const parcel = state:Serializable.toParcel()
    const buf = new AutoBuffer()
    const out = buf.getOutputStream()
    ser.write(parcel, out)
    var rc = awExpect(buf.count > 0L, 11, "AutoBuffer 未收到字节")
    if (rc != 0) { return rc }
    const inView = buf.getInputStream()
    const writtenBytes = inView.readAll()
    const decoder = new Utf8Decoder()
    const text1 = decoder.decode(writtenBytes, true)
    inView.dispose()
    // 逐字期望：类型标识保留、重复引用已展开为两份独立对象文本。
    rc = awExpect(text1 == "{\".rigi.type-identifier\":\"AwAppState\",\"session\":\"sess-01\",\"tick\":42,\"current\":{\".rigi.type-identifier\":\"AwWorkItem\",\"id\":7,\"title\":\"校准任务\",\"priority\":3},\"backup\":{\".rigi.type-identifier\":\"AwWorkItem\",\"id\":7,\"title\":\"校准任务\",\"priority\":3}}",
        12, "首写文本不符：[${text1}]")
    if (rc != 0) { return rc }

    // ── 通过 AutoBuffer 输入流读回并恢复（readAs 按目标声明解释）──
    const restored = ser.readAs\<AwAppState>(buf.getInputStream())
    rc = awExpect(restored.session == "sess-01", 21, "session")
    if (rc != 0) { return rc }
    rc = awExpect(restored.tick == 42L, 22, "tick=${restored.tick}")
    if (rc != 0) { return rc }
    rc = awExpect(restored.current.id == 7, 23, "current.id")
    if (rc != 0) { return rc }
    rc = awExpect(restored.current.title == "校准任务", 24, "current.title")
    if (rc != 0) { return rc }
    rc = awExpect(restored.current.priority == 3, 25, "current.priority")
    if (rc != 0) { return rc }
    rc = awExpect(restored.backup.title == "校准任务", 26, "backup.title")
    if (rc != 0) { return rc }
    Console.println("accept-state restored session=${restored.session} tick=${restored.tick} title=${restored.current.title} priority=${restored.current.priority}")

    // 类型标识往返稳定：恢复对象再写出与首文本逐字一致。
    const out2 = new MemoryOutputStream()
    ser.write(restored:Serializable.toParcel(), out2)
    const text2 = new Utf8Decoder().decode(out2.toSpan(), true)
    out2.dispose()
    rc = awExpect(text2 == text1, 27, "恢复对象再写出与首文本不符")
    if (rc != 0) { return rc }

    // ── 无环重复引用 → 恢复后为两个独立对象 ──
    restored.current.priority = 9
    rc = awExpect(restored.backup.priority == 3, 31,
        "current/backup 未展开为独立副本（backup=${restored.backup.priority}）")
    if (rc != 0) { return rc }
    rc = awExpect(sharedItem.priority == 3, 32, "原对象被恢复路径污染")
    if (rc != 0) { return rc }
    Console.println("accept-state spread current=${restored.current.priority} backup=${restored.backup.priority}")

    // ── 不支持环形引用：Parcel 自环写出报错 ──
    const cyc = new Parcel("AwCyc")
    cyc.setDynamic("self", cyc)
    rc = awExpectWriteError(ser, cyc, 41)
    if (rc != 0) { return rc }
    Console.println("accept-state selfloop-ok")

    // ── 继续处理消息：恢复对象经 Messenger 发送、Reader 接收 ──
    //（JSON 不是 Messenger 的必要中间步骤：send 完成点 = 队列已接受
    // 深复制快照，快照语义由队列自身承担；AutoBuffer/text1 在此之前
    // 已完成使命，不再参与消息路径。）
    const mq = new Messenger\<AwAppState>()
    const receiver = mq.createReader()
    await mq.send(restored)
    const item = await receiver.next()
    rc = awExpect(not item.isEos, 51, "消息意外 EOS")
    if (rc != 0) { return rc }
    const received = (item.item as AwAppState)
    rc = awExpect(received.session == "sess-01", 52, "received.session")
    if (rc != 0) { return rc }
    rc = awExpect(received.tick == 42L, 53, "received.tick")
    if (rc != 0) { return rc }
    rc = awExpect(received.current.priority == 9, 54,
        "received.current=${received.current.priority}")
    if (rc != 0) { return rc }
    rc = awExpect(received.backup.priority == 3, 55, "received.backup")
    if (rc != 0) { return rc }
    // 发送方后续修改不外溢到已接收副本（send 深复制快照语义）。
    restored.current.priority = 11
    const independent = received.current.priority == 9
    rc = awExpect(independent, 56, "消息副本未独立于发送方")
    if (rc != 0) { return rc }
    mq.dispose()
    Console.println("accept-state message session=${received.session} current=${received.current.priority} backup=${received.backup.priority} independent=${independent}")

    Console.println("accept-state-ok")
    return 0
}
