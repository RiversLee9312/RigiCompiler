import core.serialization.*
import core.collections.*
// expect-output: parcel-dynamic-ok
// expect-exit: 0
// 施工块 4-2 阶段1：Parcel 动态访问面（§4.6.1 / D3）。
// getDynamic/contains/setDynamic 三面；真实 null 不泄漏内部 NullSentinel；
// 迭代项 Pair<String, Any?> 且 null 原样出现；elementCount/迭代只含业务
// 字段（meta 不漏）；动态写字段名合法性与 SB 值检查；动态写后类型化
// getElement 读回。

class PlainBiz { pub var n: i32 = 0 }

pub func main(): i32 {
    const p = new Parcel("Dyn")

    // ---- contains：区分「键不存在」与「键存在且值为 null」----
    if (p.contains("missing")) { return 1 }
    p.setElement\<String>("nil", null)
    if (not p.contains("nil")) { return 2 }

    // ---- getDynamic：键不存在抛 NoSuchElementException ----
    var threw = false
    try { const v = p.getDynamic("missing") }
    catch (e: core.NoSuchElementException) { threw = true }
    if (not threw) { return 3 }

    // ---- getDynamic 返回真实 null（不是内部哨兵对象）----
    // 哨兵是内部类实例、非 null：判等即可区分真实 null。注意勿对 Any?
    // 槽直接取 typeOf——native 后端该形态有收窄缺陷（b4-2 实测）。
    const back = p.getDynamic("nil")
    if (back != null) { return 4 }

    // ---- setDynamic：非法字段名拒绝（与类型化 setElement 同校验）----
    threw = false
    try { p.setDynamic("a.b", (1 as i32)) }
    catch (e: core.IllegalArgumentException) { threw = true }
    if (not threw) { return 6 }
    threw = false
    try { p.setDynamic("", "x") }
    catch (e: core.IllegalArgumentException) { threw = true }
    if (not threw) { return 7 }
    threw = false
    try { p.setDynamic("1abc", "x") }
    catch (e: core.IllegalArgumentException) { threw = true }
    if (not threw) { return 8 }

    // ---- setDynamic：非 SB 值（普通业务对象 / Pair）拒绝 ----
    threw = false
    try { p.setDynamic("biz", new PlainBiz()) }
    catch (e: core.IllegalArgumentException) { threw = true }
    if (not threw) { return 9 }
    threw = false
    try { p.setDynamic("pair", new core.Pair\<String, i32>("k", 1)) }
    catch (e: core.IllegalArgumentException) { threw = true }
    if (not threw) { return 10 }
    // 拒绝路径不落业务表
    if (p.contains("biz")) { return 11 }
    if (p.contains("pair")) { return 12 }

    // ---- setDynamic：合法 SB 值（标量/容器/Parcel/嵌套/可空元素）----
    p.setDynamic("i", (42 as i32))
    p.setDynamic("s", "text")
    p.setDynamic("c", 'z')
    p.setDynamic("f", float.parse("1.5"))
    p.setDynamic("d", double.parse("2.5"))
    p.setDynamic("b", true)
    const li = new List\<i32>()
    li.add(7)
    p.setDynamic("list", li)
    const nli = new List\<i32?>()
    nli.add(null)
    p.setDynamic("nlist", nli)
    const nested = new List\<List\<i32>>()
    nested.add(li)
    p.setDynamic("nested", nested)
    const ma = new Map\<String, i32>()
    ma.set("k", 3)
    p.setDynamic("map", ma)
    const arr = arrayOf\<String>(1)
    arr[0] = "a"
    p.setDynamic("arr", arr)
    const subParcel = new Parcel("Inner")
    subParcel.setElement\<String>("x", "y")
    p.setDynamic("parcel", subParcel)

    // ---- 动态写后类型化 getElement 读回 ----
    if ((p.getElement\<i32>("i") if? (0 as i32)) != (42 as i32)) { return 13 }
    if ((p.getElement\<String>("s") if? "") != "text") { return 14 }
    const listBack = p.getElement\<List\<i32>>("list") as List\<i32>
    if (listBack.length != (1 as i64)) { return 15 }
    if ((listBack.getAtIndex((0 as i64)) if? (0 as i32)) != (7 as i32)) { return 16 }
    const parcelBack = p.getElement\<Parcel>("parcel") as Parcel
    if ((parcelBack.getElement\<String>("x") if? "") != "y") { return 17 }

    // ---- 动态读回容器保持名义身份 ----
    const dynList = p.getDynamic("list")
    if (not (dynList is List\<i32>)) { return 18 }
    const dynNested = p.getDynamic("nested")
    if (not (dynNested is List\<List\<i32>>)) { return 19 }
    const dynMap = p.getDynamic("map")
    if (not (dynMap is Map\<String, i32>)) { return 20 }

    // ---- elementCount/contains 只含业务字段；meta 不漏 ----
    // internal 元数据面在同编译单元可见（§16.1），直接塞保留键验证隔离。
    p.setMetaElement\<String>("..value", "meta")
    p.setMetaElement\<i64>("..id", (1 as i64))
    if (p.elementCount() != (13 as i64)) { return 21 }
    if (p.contains("..value")) { return 22 }
    if (p.contains("..id")) { return 23 }
    if (p.metaElementCount() != (2 as i64)) { return 24 }
    if ((p.getMetaElement\<String>("..value") if? "") != "meta") { return 25 }

    // ---- valueAtIndex 返回真实 null ----
    var nilIndex: i64 = ((0 as i64) - (1 as i64))
    var i: i64 = (0 as i64)
    while (i < p.elementCount()) {
        if ((p.keyAtIndex(i) if? "") == "nil") { nilIndex = i }
        i = (i + (1 as i64))
    }
    if (nilIndex < (0 as i64)) { return 26 }
    const atNil = p.valueAtIndex(nilIndex)
    if (atNil != null) { return 27 }

    // ---- iterate：项为 Pair<String, Any?>，null 原样出现，meta 不参与 ----
    var seen: i64 = (0 as i64)
    var nullSeen: i64 = (0 as i64)
    var metaLeak = false
    const en = p.iterate()
    while (en.moveNext()) {
        const entry = en.current()
        if (entry.key == "..value") { metaLeak = true }
        if (entry.key == "..id") { metaLeak = true }
        if (entry.value == null) { nullSeen = (nullSeen + (1 as i64)) }
        seen = (seen + (1 as i64))
    }
    if (metaLeak) { return 29 }
    if (seen != p.elementCount()) { return 30 }
    if (nullSeen != (1 as i64)) { return 31 }

    core.io.Console.println("parcel-dynamic-ok")
    return 0
}
