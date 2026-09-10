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
    // 连续跨开放泛型边界装箱、复制、销毁嵌套值，覆盖副本独立获取引用。
    // 分别覆盖 graph 与非 graph，防止只修复其中一条序列化所有权路径。
    var round: i32 = 0
    while (round < 32) {
        const nestedPair = new SerialPair\<SerialValue, SerialOther>(new SerialValue(round), new SerialOther(round + 1))
        const boxedPair = new SerialBox\<SerialPair\<SerialValue, SerialOther>>(nestedPair)
        const copied = clone(boxedPair, (round < 16))
        const again = clone(copied, true)
        if ((again.value.first.n != round) or (again.value.second.n != (round + 1))) {
            throw new core.RuntimeException("嵌套泛型值副本损坏")
        }
        round = round + 1
    }
    return 0
}
