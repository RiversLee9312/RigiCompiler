// 泛型函数、泛型构造和只读存储必须保留真实 Cell 构造类型。
// expect-output: 7
// expect-output: 9
// expect-output: 11
// expect-output: true
// expect-output: 13
// expect-output: readonly
// expect-output: bounded
// expect-output: 17
// expect-output: 19
// expect-output: 23
// expect-output: 29
// expect-output: 31
class Item { pub var n:i32 = 13 }
class Box\<T> {
    pub const value:T
    pub init(_ -> value)
    // 未调用的扩张成员不能触发无限构造类型收集。
    pub func grow():Box\<Box\<T>> { return new Box\<Box\<T>>(this) }
}
class Captured\<T> {
    pub const value:T
    pub unsafe init(v:T) {
        value = seq using(const p = placeOf v) { return@_ p.expose().load() }
    }
}
interface Reader { func read():String }
class ReaderHolder {
    pub const reader:Reader
    pub init(_ -> reader)
}
class ReaderImpl\<T extends ValueType> implements Reader {
    pub const value:T
    pub init(_ -> value)
    pub override func read():String {
        unsafe seq { return replace\<T>(value, value).toString() }
    }
}
unsafe func readConst\<T>(value:T):T {
    const copy:T = value
    seq using(const p = placeOf copy) { return p.expose().load() }
}
unsafe func replace\<T extends ValueType>(value:T, next:T):T {
    var copy:T = value
    seq using(const p = placeOf copy) {
        const m = p.expose().asMutable()
        m.store(next)
        return copy
    }
}
func branchReader(first:bool):String {
    var reader:Reader = new ReaderImpl\<u16>(23US)
    if (first) { reader = new ReaderImpl\<u16>(23US) }
    else { reader = new ReaderImpl\<u64>(29UL) }
    return reader.read()
}
pub func main():i32 {
    unsafe seq {
        core.io.Console.println(readConst\<i32>(7).toString())
        core.io.Console.println(replace\<i32>(1, 9).toString())
        const c = new Captured\<i32>(11)
        core.io.Console.println(c.value.toString())
        core.io.Console.println((readConst\<i32?>(null) == null).toString())
        core.io.Console.println(readConst\<Item>(new Item()).n.toString())
        const n:i32 = 5
        seq using(const p = placeOf n) {
            try { p.expose().asMutable() }
            catch(e:core.ImmutablePlaceException) { core.io.Console.println("readonly") }
        }
    }
    const b = new Box\<i32>(1)
    core.io.Console.println("bounded")
    const callback = func{(value:i64):i64 -> {
        unsafe seq { return@_ replace\<i64>(value, value) }
    }}
    core.io.Console.println(callback(17L).toString())
    const reader:Reader = new ReaderImpl\<u32>(19U)
    core.io.Console.println(reader.read())
    core.io.Console.println(branchReader(true))
    core.io.Console.println(branchReader(false))
    const holder = new ReaderHolder(new ReaderImpl\<i8>(31B))
    core.io.Console.println(holder.reader.read())
    return 0
}
