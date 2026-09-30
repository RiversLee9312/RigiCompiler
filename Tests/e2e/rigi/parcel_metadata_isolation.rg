import core.serialization.*
import core.collections.*
// expect-output: parcel-metadata-isolation-ok
// expect-exit: 0
// §4.6.3 / D3：Parcel 元数据隔离、Map 统一键值条目序列、字段键合法性收紧。
@Serializable
pub enum struct PlainMarker {} [Cold -> 3, Hot -> 91]

@Serializable
pub class IsoItem {
    pub var v: i32
    pub init(_ -> v)
}

@Serializable
pub rich enum struct IsoEvent {
    pub var payload: IsoItem
    pub const code: i32
    pub init(_ -> payload, _ -> code)
}[First(payload = _, code = _) -> 17, Second(payload = _, code = _) -> 900]

@Serializable
class IsoRecord {
    pub var tags: Map\<String, String>
    pub var event: IsoEvent
    pub var note: String?
    pub var counts: Map\<i32, String>
    pub var owners: Map\<String, IsoItem?>
    pub init(_ -> tags, _ -> event, _ -> note, _ -> counts, _ -> owners)
}

@Serializable
class AliasRecord {
    pub var item: IsoItem
    pub var tags: Map\<String, IsoItem>
    pub init(_ -> item, _ -> tags)
}

// 经函数返回值拿到非空 Map 值再 placeOf：可空解包（as）来源的临时
// 无法通过 BIL §21.8 Place Object 可证明引用校验（OriginType 追到
// Nullable 来源即拒），函数返回值的声明类型是具体 class，链路干净。
func onlyItemOf(m: Map\<String, IsoItem>): IsoItem {
    return m.tryGet("only") as IsoItem
}

func hasKey(p: Parcel, want: String): bool {
    var i: i64 = 0L
    while (i < p.elementCount()) {
        if ((p.keyAtIndex(i) if? "") == want) { return true }
        i = i + 1L
    }
    return false
}

func buildRecord(): IsoRecord {
    const tags = new Map\<String, String>()
    tags.set(".rigi.type-identifier", "typed")
    tags.set("content", "body")
    tags.set("a.b", "dotted")
    tags.set("", "empty-key")
    const counts = new Map\<i32, String>()
    counts.set(7, "seven")
    counts.set(-3, "minus")
    const owners = new Map\<String, IsoItem?>()
    owners.set("present", new IsoItem(5))
    owners.set("missing", null)
    return new IsoRecord(tags, IsoEvent.Second(new IsoItem(42), 900), null, counts, owners)
}

pub func main(): i32 {
    // ---- 1. 公开业务字段表只含业务字段（树模式与图模式）----
    var iteration = 0
    while (iteration < 2) {
        const graph = iteration == 1
        const wire = buildRecord():Serializable.toParcel(graph)
        // 图模式根是引用 envelope：业务字段全在 ..data 元数据载荷 Parcel 中，
        // envelope 自身业务表为空；树模式根直接是业务记录。
        var expected: i64 = 5L
        if (graph) { expected = 0L }
        if (wire.elementCount() != expected) { return 1 }
        var business = wire
        if (graph) { business = wire.getMetaElement\<Parcel>("..data") as Parcel }
        if (business.elementCount() != 5L) { return 2 }
        if (not hasKey(business, "tags")) { return 3 }
        if (not hasKey(business, "event")) { return 4 }
        if (not hasKey(business, "note")) { return 5 }
        if (not hasKey(business, "counts")) { return 6 }
        if (not hasKey(business, "owners")) { return 7 }
        // 内部保留键与元数据一律不进业务字段表
        if (hasKey(business, "..case")) { return 8 }
        if (hasKey(business, "..value")) { return 9 }
        if (hasKey(business, "..id")) { return 10 }
        if (hasKey(business, "..ref")) { return 11 }
        if (hasKey(business, "..data")) { return 12 }
        if (hasKey(business, "meta")) { return 13 }
        if (hasKey(business, "data")) { return 14 }
        // valueAtIndex 与迭代同表：按插入序枚举键值对，计数一致
        var iterated: i64 = 0L
        const en = business.iterate()
        while (en.moveNext()) {
            iterated = iterated + 1L
        }
        if (iterated != 5L) { return 15 }
        // 可空字段以 null 显式存在于业务表
        const noteBack = business.getElement\<String>("note")
        if (noteBack != null) { return 16 }
        // 枚举字段的 wire 记录：case 名在元数据槽，载荷字段是业务字段
        const eventWire = business.getElement\<Parcel>("event") as Parcel
        if (hasKey(eventWire, "..case")) { return 17 }
        if (not hasKey(eventWire, "payload")) { return 18 }
        if (not hasKey(eventWire, "code")) { return 19 }
        if (eventWire.elementCount() != 2L) { return 20 }
        // 元数据槽受控可审计：enum case 经元数据面可读
        const caseName = eventWire.getMetaElement\<String>("..case")
        if ((caseName if? "") != "Second") { return 21 }
        iteration += 1
    }

    // ---- 2. String 键 Map（含保留字样式键）树/图往返原样保留 ----
    iteration = 0
    while (iteration < 2) {
        const graph = iteration == 1
        const source = buildRecord()
        const copy = deepCopy(source, graph)
        if (copy.tags.count != 4L) { return 30 }
        if ((copy.tags.tryGet(".rigi.type-identifier") if? "") != "typed") { return 31 }
        if ((copy.tags.tryGet("content") if? "") != "body") { return 32 }
        if ((copy.tags.tryGet("a.b") if? "") != "dotted") { return 33 }
        if ((copy.tags.tryGet("") if? "") != "empty-key") { return 34 }
        // 源图不被别名污染
        copy.tags.set("content", "mutated")
        if ((source.tags.tryGet("content") if? "") != "body") { return 35 }
        // 可空值
        if (copy.owners.count != 2L) { return 36 }
        if ((copy.owners.tryGet("present") as IsoItem).v != 5) { return 37 }
        if (copy.owners.tryGet("missing") != null) { return 38 }
        // 非 String 键 Map 往返（i32 键，真实类型保留）
        if (copy.counts.count != 2L) { return 39 }
        if ((copy.counts.tryGet(7) if? "") != "seven") { return 40 }
        if ((copy.counts.tryGet(-3) if? "") != "minus") { return 41 }
        // 枚举载荷往返：case 判别 + 载荷字段值
        if (not (copy.event is .Second)) { return 42 }
        if (copy.event.code != 900) { return 43 }
        if (copy.event.payload.v != 42) { return 44 }
        iteration += 1
    }

    // ---- 3. 图模式别名：同一对象作普通字段与 Map 值时仍共享身份 ----
    const aliasItem = new IsoItem(77)
    const aliasTags = new Map\<String, IsoItem>()
    aliasTags.set("only", aliasItem)
    const aliased = deepCopy(new AliasRecord(aliasItem, aliasTags), true)
    const onlyItem = onlyItemOf(aliased.tags)
    seq using(const a = placeOf aliased.item) using(const b = placeOf onlyItem) {
        if (a != b) { return 50 }
    }

    // ---- 4. setElement 非法字段键抛 IllegalArgumentException ----
    const probe = new Parcel("probe")
    var rejected = false
    try { probe.setElement\<String>("", "x") }
    catch (e: IllegalArgumentException) { rejected = true }
    if (not rejected) { return 60 }
    rejected = false
    try { probe.setElement\<String>("a.b", "x") }
    catch (e: IllegalArgumentException) { rejected = true }
    if (not rejected) { return 61 }
    rejected = false
    try { probe.setElement\<String>(".rigi.type-identifier", "x") }
    catch (e: IllegalArgumentException) { rejected = true }
    if (not rejected) { return 62 }
    rejected = false
    try { probe.setElement\<String>("9lives", "x") }
    catch (e: IllegalArgumentException) { rejected = true }
    if (not rejected) { return 63 }
    // 合法键不受影响（含下划线与非 ASCII）
    probe.setElement\<String>("_ok", "v")
    probe.setElement\<String>("字段", "v2")
    if ((probe.getElement\<String>("_ok") if? "") != "v") { return 64 }
    if ((probe.getElement\<String>("字段") if? "") != "v2") { return 65 }
    if (probe.elementCount() != 2L) { return 66 }
    // 非法键未污染业务表（抛异常前不写）
    rejected = false
    try { probe.setElement\<String>("bad.key", "x") }
    catch (e: IllegalArgumentException) { rejected = true }
    if (not rejected) { return 67 }
    if (probe.elementCount() != 2L) { return 68 }

    core.io.Console.println("parcel-metadata-isolation-ok")
    return 0
}
