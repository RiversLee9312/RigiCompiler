import core.serialization.*
import core.io.Console
// 真正的256节点闭环：逐节点比较编号、重复边、回指与两份拷贝的隔离。
// expect-output: long-cycle-ok
@Serializable()
class LongNode {
    pub var number: i32
    pub var next: LongNode?
    pub var alias: LongNode?
    pub init(n:i32) { number = n }
}
func same(a:Object, b:Object):bool {
    seq using(const left = placeOf a) using(const right = placeOf b) {
        return left == right
    }
}
func check(ok:bool) {
    if (not ok) { throw new core.RuntimeException("long cycle check") }
}
func verify(root:LongNode, other:LongNode, offset:i32) {
    var node = root
    var original = other
    var i = 0
    while (i < 256) {
        check(node.number == (i + offset))
        check(not same(node, original))
        const next = node.next as LongNode
        check(same(next, node.alias as LongNode))
        node = next
        original = original.next as LongNode
        i = i + 1
    }
    check(same(node, root))
    check(same(original, other))
}
pub func main():i32 {
    const head = new LongNode(0)
    var tail = head
    var i = 1
    while (i < 256) {
        const next = new LongNode(i)
        tail.next = next
        tail.alias = next
        tail = next
        i = i + 1
    }
    tail.next = head
    tail.alias = head
    const parcel = head:Serializable.toParcel(loopedRefEnabled=true)
    const loaded = fromParcel\<LongNode>(parcel, loopedRefEnabled=true)
    const copied = head:Serializable.deepCopy(loopedRefEnabled=true)
    verify(loaded, head, 0)
    verify(copied, head, 0)
    verify(copied, loaded, 0)
    // 修改反序列化结果的全部节点，源与deepCopy结果仍保持原有顺序与值。
    var node = loaded
    i = 0
    while (i < 256) {
        node.number = node.number + 1000
        node = node.next as LongNode
        i = i + 1
    }
    verify(loaded, head, 1000)
    verify(head, loaded, 0)
    verify(copied, loaded, 0)
    Console.println("long-cycle-ok")
    return 0
}