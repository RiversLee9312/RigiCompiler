// 工厂、读写及快照均隔离可变元素；安全调用无需 unsafe。
// expect-output: 1
// expect-output: 2
// expect-output: 1
// expect-output: 3
// expect-output: 1
import core.serialization.Serializable
@Serializable
shared class ContainerItem {
    pub var n: i32
    pub init(value: i32) { n = value }
    pub override func toString():String { return n.toString() }
}
var checkNumber:i32 = 0
func require(value:bool) {
    checkNumber = (checkNumber + 1)
    if (value == false) { throw new core.RuntimeException("容器断言失败 " + checkNumber.toString()) }
}
pub func main(): i32 {
    const item = new ContainerItem(1)
    const source = core.collections.arrayOfElements\<ContainerItem>(item)
    const array = AtomicArray.fromArray\<ContainerItem>(source)
    require((await array.length()) == 1)
    item.n = 9
    core.io.Console.println(((await array.getAtIndex(0)) as ContainerItem).n.toString())
    await array.setAtIndex(0, new ContainerItem(2))
    require((source[0] as ContainerItem).n == 9)
    const read = (await array.getAtIndex(0)) as ContainerItem
    read.n = 8
    require((await array.getAtIndex(8)) == null)
    const arraySnapshot = await array.iterate()
    await array.setAtIndex(0, new ContainerItem(6))
    for (value in arraySnapshot) { require(value.n == 2) }
    await array.setAtIndex(0, new ContainerItem(2))
    core.io.Console.println(((await array.getAtIndex(0)) as ContainerItem).n.toString())
    const listSource = new core.collections.List\<ContainerItem>()
    listSource.add(new ContainerItem(1))
    const list = AtomicList.fromList(listSource)
    (listSource.getAtIndex((0 as i64)) as ContainerItem).n = 8
    const snapshot = await list.iterate()
    await list.setAtIndex((0 as i64), new ContainerItem(3))
    for (value in snapshot) { core.io.Console.println(value.n.toString()) }
    core.io.Console.println(((await list.getAtIndex((0 as i64))) as ContainerItem).n.toString())
    for (value in snapshot) { core.io.Console.println(value.n.toString()) }
    const first = snapshot.iterate()
    const second = snapshot.iterate()
    require(first.moveNext())
    first.current().n = 19
    require(second.moveNext())
    require(second.current().n == 1)
    await list.add(new ContainerItem(4))
    require((await list.length()) == (2 as i64))
    await list.removeAt((0 as i64))
    require(((await list.getAtIndex((0 as i64))) as ContainerItem).n == 4)
    require((await list.getAtIndex((8 as i64))) == null)
    const mapSource = new core.collections.Map\<ContainerItem,ContainerItem>()
    const key = new ContainerItem(1)
    const value = new ContainerItem(2)
    mapSource.set(key,value)
    const map = AtomicMap.fromMap\<ContainerItem,ContainerItem>(mapSource)
    key.n = 9
    value.n = 9
    require((await map.count()) == (1 as i64))
    require((await map.containsKey(new ContainerItem(1))))
    require(((await map.tryGet(new ContainerItem(1))) as ContainerItem).n == 2)
    const mapSnapshot = await map.iterate()
    await map.set(new ContainerItem(1),new ContainerItem(3))
    require((mapSource.tryGet(key) as ContainerItem).n == 9)
    const mapRead = (await map.tryGet(new ContainerItem(1))) as ContainerItem
    mapRead.n = 90
    require(((await map.valueAtIndex((0 as i64))) as ContainerItem).n == 3)
    require(((await map.keyAtIndex((0 as i64))) as ContainerItem).n == 1)
    require((await map.tryGet(new ContainerItem(7))) == null)
    for (pair in mapSnapshot) {
        require(pair.value.n == 2)
        pair.value.n = 99
    }
    for (pair in mapSnapshot) { require(pair.value.n == 2) }
    require((await map.remove(new ContainerItem(1))))
    require((await map.count()) == (0 as i64))
    require((await map.remove(new ContainerItem(1))) == false)
    require((await map.keyAtIndex((0 as i64))) == null)
    require((await map.valueAtIndex((0 as i64))) == null)
    try { await list.removeAt((8 as i64)) }
    catch(e:core.OutOfBoundException) {}
    require((await list.length()) == (1 as i64))
    return 0
}
