import core.serialization.*
// expect-output: 7
// expect-output: 7
// expect-output: 7
// expect-output: 9
// expect-output: 16
// expect-output: SerialReference
@Serializable()
rich struct SerialValue {
    pub var n: i32
    pub init(value: i32) { n = value }
}
@Serializable()
rich struct SerialBox\<T with Serializable> {
    pub var value: T
    pub init(item: T) { value = item }
}
@Serializable()
rich struct SerialOther {
    pub var n: i32
    pub init(value: i32) { n = value }
}
@Serializable()
rich struct SerialPair\<A with Serializable, B with Serializable> {
    pub var first: A
    pub var second: B
    pub init(a: A, b: B) { first = a
        second = b }
}
@Serializable()
class SerialReference {
    pub var n: i32
    pub init(value: i32) { n = value }
}
func clone\<T with Serializable>(value: T, graph: bool): T { return deepCopy(value, graph) }
func parcel\<T with Serializable>(value: T): Parcel { return value:Serializable.toParcel(true) }
pub func main(): i32 {
    const value = new SerialValue(7)
    const plain = clone(value, false)
    const graph = clone(value, true)
    core.io.Console.println(plain.n.toString())
    core.io.Console.println(graph.n.toString())
    const box = new SerialBox\<SerialValue>(value)
    const boxed = clone(box, true)
    core.io.Console.println(boxed.value.n.toString())
    const other = new SerialOther(9)
    const alternate = clone(new SerialBox\<SerialOther>(other), true)
    core.io.Console.println(alternate.value.n.toString())
    const pair = clone(new SerialPair\<SerialValue, SerialOther>(value, other), true)
    core.io.Console.println((pair.first.n + pair.second.n).toString())
    core.io.Console.println(parcel(new SerialReference(11)).typeName)
    return 0
}
