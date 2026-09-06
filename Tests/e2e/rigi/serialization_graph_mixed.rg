import core.serialization.*
import core.collections.*
// expect-output: graph-ok
@Serializable()
class MixedNode {
    pub var number: i32
    pub var next: MixedNode?
    pub var other: MixedNode?
    pub var array: Array\<MixedNode>
    pub var list: List\<MixedNode>
    pub var map: Map\<String, MixedNode>
    pub var keys: Map\<MixedNode, MixedNode>
    @Temporary(resume=(func{(): i32 -> number + 10} as core.Func\<i32>))
    pub var cache: i32 = 0
    pub init(n: i32) {
        number = n
        array = arrayOf\<MixedNode>(0)
        list = new List\<MixedNode>()
        map = new Map\<String, MixedNode>()
        keys = new Map\<MixedNode, MixedNode>()
    }
    pub override func toString(): String { return number.toString() }
}
func same(a: Object, b: Object): bool {
    const left = placeOf a
    const right = placeOf b
    try { return left == right }
    finally (_) { left.dispose()
        right.dispose() }
}
// 键的 toString 依赖最后才恢复的字段：不能在填图途中按临时值去重。
@Serializable()
class LateMapNode {
    pub var next: LateMapNode?
    pub var map: Map\<LateMapNode, i32>
    pub var number: i32
    pub init(n: i32) { map = new Map\<LateMapNode, i32>()
        number = n }
    pub override func toString(): String { return number.toString() }
}
var checks: i32 = 0
func check(value: bool) {
    checks = checks + 1
    if (not value) { throw new core.RuntimeException("graph check " + checks.toString()) }
}
pub func main(): i32 {
    const a = new MixedNode(1)
    const b = new MixedNode(2)
    a.next = b
    a.other = b
    b.next = a
    a.array = arrayOfElements\<MixedNode>(a, b)
    a.list.add(a)
    a.map.set("..id", a)
    a.keys.set(a, a)
    b.array = a.array
    b.list = a.list
    b.map = a.map
    b.keys = a.keys
    const p = a:Serializable.toParcel(loopedRefEnabled=true)
    check(p.typeName == "MixedNode")
    const c = fromParcel\<MixedNode>(p, loopedRefEnabled=true)
    const d = c.next as MixedNode
    check(not same(a, c))
    check(same(d, c.other as MixedNode))
    check(same(c, d.next as MixedNode))
    check(same(c, c.array[0] as MixedNode))
    check(same(d, c.array[1] as MixedNode))
    check(same(c.array, d.array))
    check(same(c.list, d.list))
    check(same(c.map, d.map))
    check(same(c.keys, d.keys))
    check(same(c, c.list.getAtIndex(0L) as MixedNode))
    check(same(c, c.map.tryGet("..id") as MixedNode))
    check(same(c, c.keys.keyAtIndex(0L) as MixedNode))
    check(same(c, c.keys.valueAtIndex(0L) as MixedNode))
    c.number = 8
    check(c.cache == 18)
    check(a.cache == 11)
    const isolated = deepCopy(a, true)
    check(not same(c, isolated))
    // 默认树模式同一子对象的两条边必须各自复制。
    const tree = new MixedNode(4)
    const child = new MixedNode(5)
    tree.next = child
    tree.other = child
    const treeParcel = tree:Serializable.toParcel()
    check((treeParcel.getElement\<i32>("number") as i32) == 4)
    const treeCopy = tree:Serializable.deepCopy()
    check(not same(treeCopy.next as MixedNode, treeCopy.other as MixedNode))
    const late = new LateMapNode(1)
    const lateChild = new LateMapNode(2)
    late.next = lateChild
    lateChild.next = late
    late.map.set(late, 10)
    late.map.set(lateChild, 20)
    lateChild.map = late.map
    const lateCopy = deepCopy(late, true)
    check(lateCopy.map.count == 2L)
    check((lateCopy.map.tryGet(lateCopy) as i32) == 10)
    check((lateCopy.map.tryGet(lateCopy.next as LateMapNode) as i32) == 20)
    core.io.Console.println("graph-ok")
    return 0
}
