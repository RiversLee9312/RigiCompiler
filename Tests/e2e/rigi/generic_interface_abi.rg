// 泛型接口槽的参数与返回必须使用声明 ABI，具体实现经适配保持自身 ABI。
// expect-output: 19
// expect-output: 23
// expect-output: reference
// expect-output: rich
// expect-output: 37
// expect-output: stored
// expect-output: updated
// expect-output: 41
// expect-output: mixed
// expect-output: mixed-rich
// expect-output: caught
// expect-output: direct
interface Reader\<T> { func read(value:T):T }
class ReaderImpl\<T> implements Reader\<T> {
    pub override func read(value:T):T { return value }
}
class ScalarReader implements Reader\<u32> {
    pub override func read(value:u32):u32 { return value + 1U }
}
class Item {
    pub const text:String
    pub init(_ -> text)
}
rich struct Packet {
    pub const item:Item
    pub init(_ -> item)
}
interface Access\<T> {
    func set(value:T)
    func get():T
}
class Storage\<T> implements Access\<T> {
    priv var value:T
    pub init(_ -> value)
    pub override func set(value:T) { this.value = value }
    pub override func get():T { return value }
}
class ItemStorage implements Access\<Item> {
    priv var value:Item
    pub init(_ -> value)
    pub override func set(value:Item) { this.value = value }
    pub override func get():Item { return value }
}
class PacketStorage implements Access\<Packet> {
    priv var value:Packet
    pub init(_ -> value)
    pub override func set(value:Packet) { this.value = value }
    pub override func get():Packet { return value }
}
interface Mixed\<T> { func apply(before:i32, value:T, after:String):T }
class MixedPacket implements Mixed\<Packet> {
    pub override func apply(before:i32, value:Packet, after:String):Packet {
        if (before < 0) { throw new core.RuntimeException("expected") }
        core.io.Console.println(before.toString())
        core.io.Console.println(after)
        return value
    }
}
pub func main():i32 {
    const reader:Reader\<u32> = new ReaderImpl\<u32>()
    core.io.Console.println(reader.read(19U).toString())
    const scalar:Reader\<u32> = new ScalarReader()
    core.io.Console.println(scalar.read(22U).toString())
    const refs:Reader\<Item> = new ReaderImpl\<Item>()
    core.io.Console.println(refs.read(new Item("reference")).text)
    const packetReader:Reader\<Packet> = new ReaderImpl\<Packet>()
    core.io.Console.println(packetReader.read(new Packet(new Item("rich"))).item.text)
    const storage:Access\<u32> = new Storage\<u32>(1U)
    storage.set(37U)
    core.io.Console.println(storage.get().toString())
    const items:Access\<Item> = new ItemStorage(new Item("old"))
    items.set(new Item("stored"))
    core.io.Console.println(items.get().text)
    const packets:Access\<Packet> = new PacketStorage(new Packet(new Item("old")))
    packets.set(new Packet(new Item("updated")))
    core.io.Console.println(packets.get().item.text)
    const mixed:Mixed\<Packet> = new MixedPacket()
    const result = mixed.apply(41, new Packet(new Item("mixed-rich")), "mixed")
    core.io.Console.println(result.item.text)
    try { mixed.apply(-1, new Packet(new Item("discarded")), "unused") }
    catch(e:core.RuntimeException) { core.io.Console.println("caught") }
    // 具体实现直接调用仍沿真实签名，不能被接口槽的适配 ABI 污染。
    const direct = new PacketStorage(new Packet(new Item("direct")))
    core.io.Console.println(direct.get().item.text)
    return 0
}
