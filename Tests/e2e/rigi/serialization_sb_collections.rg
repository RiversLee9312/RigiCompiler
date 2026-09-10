import core.serialization.*
import core.collections.*
// expect-output: sb-collections-ok
// expect-exit: 0
@Serializable
class Item {
    pub var n: i32
    pub init(_ -> n)
}
pub func main(): i32 {
    var i = 0
    while (i < 8) {
        const graph = (i < 4)
        const original = new Item(i)
        const list = new List\<Item>()
        list.add(original)
        const listCopy = deepCopy(list, graph)
        (listCopy.getAtIndex(0 as i64) as Item).n = 99
        if (original.n != i) { return 1 }
        const array = arrayOf\<Item>(1)
        array[0] = original
        const arrayCopy = deepCopy(array, graph)
        (arrayCopy[0] as Item).n = 88
        if (original.n != i) { return 2 }
        const map = new Map\<String, Item>()
        map.set("item", original)
        const mapCopy = deepCopy(map, graph)
        (mapCopy.tryGet("item") as Item).n = 77
        if (original.n != i) { return 3 }
        const nested = new List\<Array\<Item>>()
        nested.add(array)
        const nestedCopy = deepCopy(nested, graph)
        const nestedArray = nestedCopy.getAtIndex(0 as i64) as Array\<Item>
        (nestedArray[0] as Item).n = 66
        if (original.n != i) { return 4 }
        i += 1
    }
    core.io.Console.println("sb-collections-ok")
    return 0
}
