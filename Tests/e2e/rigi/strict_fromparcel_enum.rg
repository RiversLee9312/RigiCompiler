import core.serialization.*
import core.collections.*
// expect-output: strict-fromparcel-enum-ok
// expect-exit: 0
// §4.6.3 / D3 严格恢复契约（块 4-3）rich 枚举载荷面：..case 只进 meta
// 槽、不计入业务字段集合；载荷字段集合完全匹配（含 const）；类型互换
// 拒绝。与 strict_fromparcel.rg 分文件：rich 枚举 case 构造与继承多态
// 同单元组合会触发一个与本块无关的既有绑定器解析缺陷（待裁决问题）。

@Serializable()
pub class EnumBox {
    pub var num: i32
    pub var text: String
    pub init(_ -> num, _ -> text)
}

@Serializable()
pub rich enum struct StrictEvent {
    pub var payload: EnumBox
    pub const code: i32
    pub init(_ -> payload, _ -> code)
}[Tick(payload = _, code = _) -> 5, Tock(payload = _, code = _) -> 6]

@Serializable()
class EventHolder {
    pub var event: StrictEvent
    pub init(_ -> event)
}

var checks: i32 = 0
func check(value: bool) {
    checks = checks + 1
    if (not value) { throw new core.RuntimeException("enum strict check " + checks.toString()) }
}

func hasKey(p: Parcel, want: String): bool {
    var i: i64 = 0L
    while (i < p.elementCount()) {
        if ((p.keyAtIndex(i) if? "") == want) { return true }
        i = i + 1L
    }
    return false
}

pub func main(): i32 {
    // ---- 正例：rich 枚举往返；..case 在 meta 槽、业务字段集合与
    //         载荷字段（含 const code）完全一致 ----
    var iteration = 0
    while (iteration < 2) {
        const graph = iteration == 1
        const source = new EventHolder(
            StrictEvent.Tock(new EnumBox(9, "nine"), 6))
        const wire = source:Serializable.toParcel(graph)
        var business = wire
        if (graph) { business = wire.getMetaElement\<Parcel>("..data") as Parcel }
        // 业务字段表只有 event 一个字段；..case 不进业务表
        if (business.elementCount() != 1L) { return 1 }
        const eventWire = business.getElement\<Parcel>("event") as Parcel
        if (hasKey(eventWire, "..case")) { return 2 }
        if (not hasKey(eventWire, "payload")) { return 3 }
        if (not hasKey(eventWire, "code")) { return 4 }
        if (eventWire.elementCount() != 2L) { return 5 }
        if ((eventWire.getMetaElement\<String>("..case") if? "") != "Tock") { return 6 }
        const copy = fromParcel\<EventHolder>(wire, graph)
        if (not (copy.event is .Tock)) { return 7 }
        if (copy.event.code != 6) { return 8 }
        if (copy.event.payload.num != 9) { return 9 }
        if (copy.event.payload.text != "nine") { return 10 }
        iteration += 1
    }

    // ---- 负例 1：载荷字段类型互换（i64→i32）拒绝 ----
    var wire = (new EventHolder(
        StrictEvent.Tick(new EnumBox(1, "a"), 5))):Serializable.toParcel()
    const eventWire = wire.getElement\<Parcel>("event") as Parcel
    eventWire.setDynamic("code", (5L as i64))
    var rejected = false
    try {
        const bad = fromParcel\<EventHolder>(wire)
    } catch (e: SerializationException) {
        rejected = true
    }
    check(rejected)

    // ---- 负例 2：载荷字段缺失（code 未显式存在）拒绝 ----
    wire = (new EventHolder(
        StrictEvent.Tick(new EnumBox(1, "a"), 5))):Serializable.toParcel()
    const eventWire2 = wire.getElement\<Parcel>("event") as Parcel
    const partial = new Parcel(eventWire2.typeName)
    // 合法 payload wire 记录（缺 code）：字段集合计数不匹配拒绝
    const payloadWire = (new EnumBox(1, "a")):Serializable.toParcel()
    partial.setDynamic("payload", payloadWire)
    wire.setDynamic("event", partial)
    rejected = false
    try {
        const bad = fromParcel\<EventHolder>(wire)
    } catch (e: SerializationException) {
        rejected = true
    }
    check(rejected)

    // ---- 负例 3：未知 case 名仍按 IllegalStateException ----
    wire = (new EventHolder(
        StrictEvent.Tick(new EnumBox(1, "a"), 5))):Serializable.toParcel()
    const eventWire3 = wire.getElement\<Parcel>("event") as Parcel
    eventWire3.setMetaElement\<String>("..case", "Bogus")
    rejected = false
    try {
        const bad = fromParcel\<EventHolder>(wire)
    } catch (e: IllegalStateException) {
        rejected = true
    }
    check(rejected)

    core.io.Console.println("strict-fromparcel-enum-ok")
    return 0
}
