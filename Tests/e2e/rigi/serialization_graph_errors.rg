import core.serialization.*
import core.collections.*
// expect-output: tree-cycle
// expect-output: bad-ref
// expect-output: negative-ref
// expect-output: bad-id
// expect-output: duplicate-id
// expect-output: isolated
@Serializable()
class ErrorNode {
    pub var items: Array\<ErrorNode>
    pub init() { items = arrayOf\<ErrorNode>(0) }
}
pub func main(): i32 {
    const source = new ErrorNode()
    source.items = arrayOfElements\<ErrorNode>(source)
    try { const ignored = source:Serializable.toParcel() }
    catch (_: core.IllegalStateException) { core.io.Console.println("tree-cycle") }
    const reference = new Parcel("ErrorNode")
    reference.setElement\<i64>("..ref", 99L)
    try { const ignored = fromParcel\<ErrorNode>(reference, true) }
    catch (_: core.IllegalStateException) { core.io.Console.println("bad-ref") }
    const negative = source:Serializable.toParcel(true)
    negative.setElement\<i64>("..ref", -1L)
    try { const ignored = fromParcel\<ErrorNode>(negative, true) }
    catch (_: core.IllegalStateException) { core.io.Console.println("negative-ref") }
    const malformed = source:Serializable.toParcel(true)
    malformed.setElement\<i64>("..id", 2L)
    try { const ignored = fromParcel\<ErrorNode>(malformed, true) }
    catch (_: core.IllegalStateException) { core.io.Console.println("bad-id") }
    const duplicate = source:Serializable.toParcel(true)
    const payload = duplicate.getElement\<Parcel>("..data") as Parcel
    const arrayNode = payload.getElement\<Parcel>("items") as Parcel
    arrayNode.setElement\<i64>("..id", 1L)
    try { const ignored = fromParcel\<ErrorNode>(duplicate, true) }
    catch (_: core.IllegalStateException) { core.io.Console.println("duplicate-id") }
    const copy = deepCopy(source, loopedRefEnabled=true)
    const child = copy.items[0] as ErrorNode
    const left = placeOf copy
    const right = placeOf child
    try {
        if (not (left == right)) { throw new core.RuntimeException("失败污染下一次调用") }
    } finally (_) { left.dispose()
        right.dispose() }
    core.io.Console.println("isolated")
    return 0
}
