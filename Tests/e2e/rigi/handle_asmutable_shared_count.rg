// expect-output: 5
// expect-output: 12
// expect-output: 12
// expect-output: 31
// 3b-β 对拍：asMutable 双 capability 同壳计数与释放序——asMutable 经
// 壳指针 increment（不新建壳、不二次 acquire）派生同壳新 capability；
// 双方 load/store 可见同一目标（value 语义下 store 经共享 Cell），多
// 层派生与乱序丢弃后计数归零、目标由最后一个 capability 释放。
import core.io.Console
pub func main(): i32 {
    var value: i32 = 5
    unsafe seq using(const place = placeOf value) {
        const handle = place.expose()
        Console.println(handle.load().toString())
        const mutable = handle.asMutable()
        mutable.store(12)
        // 同壳同目标：原 capability 立即可见
        Console.println(handle.load().toString())
        Console.println(mutable.load().toString())
        // 多层派生：MutableHandle.asMutable 再派生第三份同壳 capability
        const derived = mutable.asMutable()
        derived.store(31)
        Console.println(handle.load().toString())
    }
    return 0
}
