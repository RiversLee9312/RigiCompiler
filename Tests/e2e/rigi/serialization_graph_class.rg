import core.serialization.*
import core.io.Console

// 图模式保留自环；默认模式确定抛错，下一次调用不受失败污染。
// expect-output: true
// expect-output: tree-cycle
// expect-output: true
// expect-output: true
@Serializable()
class GraphNode {
    pub var next: GraphNode?
    pub var number: i32
    pub init(n: i32) { number = n }
}

pub func main(): i32 {
    const node = new GraphNode(7)
    node.next = node
    const copy = node:Serializable.deepCopy(loopedRefEnabled=true)
    const copiedNext = copy.next as GraphNode
    Console.println(((placeOf copy) == (placeOf copiedNext)).toString())
    try { const ignored = deepCopy(node) }
    catch (_: core.IllegalStateException) { Console.println("tree-cycle") }
    const again = fromParcel\<GraphNode>(node:Serializable.toParcel(true), true)
    const againNext = again.next as GraphNode
    Console.println(((placeOf again) == (placeOf againNext)).toString())
    Console.println((again.number == 7).toString())
    return 0
}
