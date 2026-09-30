import core.serialization.*
import core.collections.*
// expect-output: strict-fromparcel-ok
// expect-exit: 0
// §4.6.3 / D3 严格恢复契约（块 4-3）：无值类型转换（核验先于 cast）、
// 集合逐层检查、对象多态须已登记且可赋、业务字段集合完全匹配（meta
// 槽不计入）、null 只可用于可空声明；覆盖 deepCopy/图模式公共路径。
// rich 枚举载荷用例见 strict_fromparcel_enum.rg（与本文件的多态用例
// 分文件：二者同单元组合会触发一个与本块无关的既有绑定器解析缺陷）。

@Serializable()
pub class StrictBox {
    pub var num: i32
    pub var text: String
    pub var note: String?
    pub init(_ -> num, _ -> text, _ -> note)
}

@Serializable()
pub class StrictArr {
    pub var items: Array\<i32>
    pub var list: List\<String>
    pub var table: Map\<String, i32>
    pub init(_ -> items, _ -> list, _ -> table)
}

@Serializable()
open class PolyBase {
    pub var id: i32
    pub init(_ -> id)
}

@Serializable()
class PolyDerived : PolyBase {
    pub var extra: String
    pub init(newId: i32, newExtra: String) {
        super(newId)
        extra = newExtra
    }
}

@Serializable()
class PolyHolder {
    pub var member: PolyBase
    pub init(_ -> member)
}

@Serializable()
pub enum struct StrictMark {} [Low -> 1, High -> 2]

@Serializable()
class StrictCache {
    pub var seed: i32
    @Temporary(resume=(func{(): i32 -> seed * 2} as core.Func\<i32>))
    pub var cache: i32 = 0
    pub init(_ -> seed)
}

@Serializable()
class StrictCycle {
    pub var tag: i32
    pub var next: StrictCycle?
    pub var alias: StrictCycle?
    pub init(_ -> tag)
}

@Serializable()
class StrictNest {
    pub var parcel: Parcel
    pub init(_ -> parcel)
}

var checks: i32 = 0
func check(value: bool) {
    checks = checks + 1
    if (not value) { throw new core.RuntimeException("strict check " + checks.toString()) }
}

// 对象身份比较（serialization_graph_mixed 同惯例）。
func same(a: Object, b: Object): bool {
    const left = placeOf a
    const right = placeOf b
    try { return left == right }
    finally (_) { left.dispose()
        right.dispose() }
}

// 负例统一断言：动作必须抛 SerializationException。
func expectStrict(action: core.Func\<StrictBox>): bool {
    try {
        action()
        return false
    } catch (e: SerializationException) {
        return true
    }
}

func buildBox(): StrictBox {
    return new StrictBox(7, "seven", null)
}

pub func main(): i32 {
    // ---- 正例 1：普通往返（可空字段显式 null 存在）不被严格化误伤 ----
    const box = buildBox()
    const boxWire = box:Serializable.toParcel()
    if (boxWire.elementCount() != 3L) { return 1 }
    const boxCopy = fromParcel\<StrictBox>(boxWire)
    check(boxCopy.num == 7)
    check(boxCopy.text == "seven")
    check(boxCopy.note == null)
    // 可空字段显式非 null 同样合法
    const box2 = new StrictBox(8, "eight", "note")
    const box2Copy = deepCopy(box2)
    check((box2Copy.note if? "") == "note")

    // ---- 正例 2：空容器往返（集合形状 Array<Any> 条目序列）----
    const empty = new StrictArr(arrayOf\<i32>(0), new List\<String>(),
        new Map\<String, i32>())
    const emptyCopy = deepCopy(empty)
    check(emptyCopy.items.length == 0)
    check(emptyCopy.list.length == 0L)
    check(emptyCopy.table.count == 0L)

    // ---- 正例 3：合法多态（实际类型已登记、可赋给声明）----
    const holder = new PolyHolder(new PolyDerived(3, "d"))
    const holderCopy = deepCopy(holder)
    check(holderCopy.member is PolyDerived)
    check(holderCopy.member.id == 3)
    check((holderCopy.member as PolyDerived).extra == "d")

    // ---- 正例 4：无载荷枚举（..case 只进 meta 槽，不计入业务字段集合）----
    const markWire = StrictMark.High:Serializable.toParcel()
    if (markWire.elementCount() != 0L) { return 2 }
    const markCopy = deepCopy(StrictMark.High)
    check(markCopy is .High)

    // ---- 正例 5：Parcel 字段（typeName/data/meta 经业务通道正常往返，
    //         其 wire 记录业务字段集合固定三元组，严格核验不误伤）----
    const payload = new Parcel("inner")
    payload.setElement\<i32>("width", 4)
    const nestCopy = deepCopy(new StrictNest(payload))
    check(nestCopy.parcel.typeName == "inner")
    check((nestCopy.parcel.getElement\<i32>("width") as i32) == 4)
    check((payload.getElement\<i32>("width") as i32) == 4)

    // ---- 正例 6：@Temporary 排除与懒恢复 ----
    const cached = deepCopy(new StrictCache(21))
    check(cached.seed == 21)
    check(cached.cache == 42)

    // ---- 正例 7：图模式环与别名 ----
    const a = new StrictCycle(1)
    const b = new StrictCycle(2)
    a.next = b
    b.next = a
    a.alias = b
    const g = deepCopy(a, true)
    check(g.tag == 1)
    check((g.next as StrictCycle).tag == 2)
    check(same((g.next as StrictCycle).next as Object, g as Object))
    check(same(g.alias as Object, g.next as Object))

    // ---- 负例 1：i64→i32 宽度不符 ----
    var wire = buildBox():Serializable.toParcel()
    wire.setDynamic("num", (3L as i64))
    check(expectStrict((func{(): StrictBox -> fromParcel\<StrictBox>(wire)}
        as core.Func\<StrictBox>)))

    // ---- 负例 2：double→i32 截断拒绝 ----
    wire = buildBox():Serializable.toParcel()
    wire.setDynamic("num", (3.0 as double))
    check(expectStrict((func{(): StrictBox -> fromParcel\<StrictBox>(wire)}
        as core.Func\<StrictBox>)))

    // ---- 负例 3：String→i32 互转拒绝 ----
    wire = buildBox():Serializable.toParcel()
    wire.setDynamic("num", ("3" as String))
    check(expectStrict((func{(): StrictBox -> fromParcel\<StrictBox>(wire)}
        as core.Func\<StrictBox>)))

    // ---- 负例 4：多余字段 ----
    wire = buildBox():Serializable.toParcel()
    wire.setDynamic("extra", (1 as i32))
    check(expectStrict((func{(): StrictBox -> fromParcel\<StrictBox>(wire)}
        as core.Func\<StrictBox>)))

    // ---- 负例 5：缺失字段（业务字段集合必须完全一致）----
    const partial = new Parcel(boxWire.typeName)
    partial.setDynamic("num", (7 as i32))
    check(expectStrict((func{(): StrictBox -> fromParcel\<StrictBox>(partial)}
        as core.Func\<StrictBox>)))

    // ---- 负例 6：可空字段也必须显式存在 ----
    const partial2 = new Parcel(boxWire.typeName)
    partial2.setDynamic("num", (7 as i32))
    partial2.setDynamic("text", ("t" as String))
    check(expectStrict((func{(): StrictBox -> fromParcel\<StrictBox>(partial2)}
        as core.Func\<StrictBox>)))

    // ---- 负例 7：非可空字段收到 null ----
    wire = buildBox():Serializable.toParcel()
    wire.setElement\<String>("text", null)
    check(expectStrict((func{(): StrictBox -> fromParcel\<StrictBox>(wire)}
        as core.Func\<StrictBox>)))

    // ---- 负例 8：List 当 Array ----
    const arrSource = new StrictArr(arrayOfElements\<i32>(1, 2),
        new List\<String>(), new Map\<String, i32>())
    var arrWire = arrSource:Serializable.toParcel()
    const liveList = new List\<i32>()
    liveList.add(9)
    arrWire.setDynamic("items", liveList)
    var rejected = false
    try {
        const bad = fromParcel\<StrictArr>(arrWire)
    } catch (e: SerializationException) {
        rejected = true
    }
    check(rejected)

    // ---- 负例 9：集合元素类型不符 ----
    arrWire = arrSource:Serializable.toParcel()
    const items = arrWire.getElement\<Array\<Any>>("items") as Array\<Any>
    items[0] = "not-a-number"
    rejected = false
    try {
        const bad = fromParcel\<StrictArr>(arrWire)
    } catch (e: SerializationException) {
        rejected = true
    }
    check(rejected)

    // ---- 负例 10：图/树模式混用仍按 IllegalStateException ----
    // 树 wire 以图模式恢复：根开放 T 无标记按树分支，但嵌套具名对象
    // 字段的 validateParcelMode 发现模式不匹配即拒绝。
    const treeWire2 = new PolyHolder(new PolyDerived(1, "x")):Serializable.toParcel()
    rejected = false
    try {
        const bad = fromParcel\<PolyHolder>(treeWire2, true)
    } catch (e: IllegalStateException) {
        rejected = true
    }
    check(rejected)
    const graphWire = buildBox():Serializable.toParcel(true)
    rejected = false
    try {
        const bad = fromParcel\<StrictBox>(graphWire)
    } catch (e: IllegalStateException) {
        rejected = true
    }
    check(rejected)

    // ---- 负例 11：未登记 wire 类型 ----
    const unknown = new Parcel("no.such.WireType")
    unknown.setDynamic("num", (1 as i32))
    unknown.setDynamic("text", ("x" as String))
    unknown.setElement\<String>("note", null)
    rejected = false
    try {
        const bad = fromParcel\<StrictBox>(unknown)
    } catch (e: IllegalStateException) {
        rejected = true
    }
    check(rejected)

    // ---- 正例 8：消息复制路径（deepCopy 组合）同规则收紧后仍合法 ----
    const message = deepCopy(buildBox())
    check(message.num == 7)

    core.io.Console.println("strict-fromparcel-ok")
    return 0
}
