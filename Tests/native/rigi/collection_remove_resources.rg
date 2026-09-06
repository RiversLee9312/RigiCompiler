// 容器保持存活时检查删除释放，避免进程退出回收掩盖尾槽残留。
import core.collections.*
import core.io.Console
@NativeLibrary("rigi_rt")
@NativeSymbol("mem_live_bytes")
priv native func liveBytes(): i64
class Payload {
    pub const data: Array\<i32>
    pub init() { data = arrayOf\<i32>(8192) }
}
func fill(list: List\<Payload>, map: Map\<i32, Payload>) {
    var i: i32 = 0
    while (i < 8) {
        list.add(new Payload())
        map.set(i, new Payload())
        i = (i + 1)
    }
}
func drain(list: List\<Payload>, map: Map\<i32, Payload>) {
    var i: i32 = 0
    while (i < 8) {
        list.removeAt((0 as i64))
        if (map.remove(i) == false) { throw new core.RuntimeException("删除失败") }
        i = (i + 1)
    }
}
pub func main(): i32 {
    const list = new List\<Payload>()
    const map = new Map\<i32, Payload>()
    const baseline = liveBytes()
    fill(list, map)
    drain(list, map)
    if (liveBytes() > (baseline + (4096 as i64))) {
        throw new core.RuntimeException("删除后容器仍持有大对象")
    }
    if ((list.length != (0 as i64)) or (map.count != (0 as i64))) {
        throw new core.RuntimeException("容器非空")
    }
    const nullable = new List\<i32?>()
    nullable.add(null)
    nullable.add(7)
    nullable.removeAt((0 as i64))
    for (v in nullable) { Console.println((v if? 0).toString()) }
    nullable.add(null)
    nullable.removeAt((1 as i64))
    Console.println("collection-remove-resources-ok")
    return 0
}
