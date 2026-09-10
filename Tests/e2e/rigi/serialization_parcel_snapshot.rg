import core.serialization.*
import core.collections.*
// expect-output: parcel-snapshot-ok
// expect-exit: 0
@Serializable
class SnapshotItem {
    pub const label: String
    pub var number: i32
    pub var next: SnapshotItem?
    pub var parcel: Parcel?
    pub init(_ -> label, _ -> number)
}
@Serializable
class SnapshotEnvelope {
    pub var direct: SnapshotItem
    pub var parcel: Parcel
    pub init(_ -> direct, _ -> parcel)
}
func nestedParcel\<T with Serializable>(item: T): Parcel {
    const array = arrayOf\<T>(1)
    array[0] = item
    const nested = new List\<Array\<T>>()
    nested.add(array)
    const parcel = new Parcel("generic")
    parcel.setElement\<List\<Array\<T>>>("items", nested)
    return parcel
}
pub func main(): i32 {
    var iteration = 0
    while (iteration < 8) {
        const graph = iteration < 4
        const item = new SnapshotItem("original", 7)
        const items = arrayOf\<SnapshotItem>(2)
        items[0] = item
        items[1] = item
        const list = new List\<SnapshotItem?>()
        list.add(item)
        list.add(null)
        const map = new Map\<String, SnapshotItem?>()
        map.set("present", item)
        map.set("empty", null)
        const source = new Parcel("payload")
        source.setElement\<Array\<SnapshotItem>>("items", items)
        source.setElement\<List\<SnapshotItem?>>("list", list)
        source.setElement\<Map\<String, SnapshotItem?>>("map", map)
        source.setElement\<String>("empty", null)
        const enclosing = deepCopy(new SnapshotEnvelope(item, source), graph)
        enclosing.direct.number = 55
        const enclosed = enclosing.parcel.getElement\<Array\<SnapshotItem>>("items") as Array\<SnapshotItem>
        if (graph and ((enclosed[0] as SnapshotItem).number != 55)) { return 13 }
        if ((not graph) and ((enclosed[0] as SnapshotItem).number != 7)) { return 14 }
        const genericCopy = deepCopy(nestedParcel(item), graph)
        if (genericCopy.elementCount() != 1L) { return 15 }
        const wire = source:Serializable.toParcel(graph)
        item.number = 12
        const copy = fromParcel\<Parcel>(wire, graph)
        const copiedItems = copy.getElement\<Array\<SnapshotItem>>("items") as Array\<SnapshotItem>
        const first = copiedItems[0] as SnapshotItem
        const second = copiedItems[1] as SnapshotItem
        if ((first.number != 7) or (first.label != "original")) { return 1 }
        first.number = 99
        if (item.number != 12) { return 2 }
        if (graph and (second.number != 99)) { return 3 }
        if ((not graph) and (second.number != 7)) { return 4 }
        const copiedList = copy.getElement\<List\<SnapshotItem?>>("list") as List\<SnapshotItem?>
        const copiedMap = copy.getElement\<Map\<String, SnapshotItem?>>("map") as Map\<String, SnapshotItem?>
        if (copiedList.getAtIndex(1L) != null) { return 5 }
        if (copiedMap.tryGet("empty") != null) { return 6 }
        if (copy.getElement\<String>("empty") != null) { return 7 }
        if (graph and ((copiedList.getAtIndex(0L) as SnapshotItem).number != 99)) { return 8 }
        if (graph and ((copiedMap.tryGet("present") as SnapshotItem).number != 99)) { return 9 }
        const again = fromParcel\<Parcel>(wire, graph)
        const againItems = again.getElement\<Array\<SnapshotItem>>("items") as Array\<SnapshotItem>
        if ((againItems[0] as SnapshotItem).number != 7) { return 10 }
        iteration += 1
    }
    const cyclic = new SnapshotItem("cycle", 31)
    cyclic.next = cyclic
    const cycleItems = new List\<SnapshotItem>()
    cycleItems.add(cyclic)
    const cycleParcel = new Parcel("cycle")
    cycleParcel.setElement\<List\<SnapshotItem>>("items", cycleItems)
    // 环跨越 Parcel → List → 普通对象 → 原 Parcel，必须共用编号表。
    cyclic.parcel = cycleParcel
    var rejected = false
    try { const bad = deepCopy(cycleParcel) }
    catch (e: IllegalStateException) { rejected = true }
    if (not rejected) { return 11 }
    const cycleCopy = deepCopy(cycleParcel, true)
    const copiedCycleItems = cycleCopy.getElement\<List\<SnapshotItem>>("items") as List\<SnapshotItem>
    const head = copiedCycleItems.getAtIndex(0L) as SnapshotItem
    const tail = head.next as SnapshotItem
    seq using(const a = placeOf head) using(const b = placeOf tail) using(const c = placeOf cyclic) {
        if ((a != b) or (a == c)) { return 12 }
    }
    const back = head.parcel as Parcel
    seq using(const a = placeOf back) using(const b = placeOf cycleCopy) {
        if (a != b) { return 16 }
    }
    core.io.Console.println("parcel-snapshot-ok")
    return 0
}
