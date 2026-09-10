// expect-output: handle-identity-ok
// expect-exit: 0
// 类型身份不得因共享底层存储而合并；负例不调用错误载荷，避免依赖崩溃验收。
pub unsafe func check\<T>(handle: Handle\<T>): bool {
    const boxed: Any = handle
    return (boxed is Handle\<T>) and (not (boxed is MutableHandle\<T>))
}
pub func main(): i32 {
    var number: i32 = 3
    var text: String = "hello"
    unsafe seq using(const p = placeOf number) using(const q = placeOf text) {
        const h = p.expose()
        const s = q.expose()
        const m = h.asMutable()
        p.dispose()
        q.dispose()
        if ((not check\<i32>(h)) or (not check\<String>(s))) { return 1 }
        const boxed: Any = h
        const mutable: Any = m
        if ((boxed is Handle\<String>) or (boxed is Handle\<Any>)) { return 2 }
        if ((mutable is Handle\<i32>) or (mutable is MutableHandle\<String>)) { return 3 }
        var rejected = 0
        try { const wrong = boxed as Handle\<String> }
        catch (e: CastException) { rejected += 1 }
        try { const wrong = boxed as MutableHandle\<i32> }
        catch (e: CastException) { rejected += 1 }
        try { const wrong = mutable as MutableHandle\<String> }
        catch (e: CastException) { rejected += 1 }
        try { const wrong = mutable as Handle\<i32> }
        catch (e: CastException) { rejected += 1 }
        if (rejected != 4) { return 4 }
        var i = 0
        while (i < 96) {
            (mutable as MutableHandle\<i32>).store(i)
            if (((boxed as Handle\<i32>).load() != i) or (number != i)) { return 5 }
            if (s.load() != "hello") { return 6 }
            i += 1
        }
    }
    core.io.Console.println("handle-identity-ok")
    return 0
}
