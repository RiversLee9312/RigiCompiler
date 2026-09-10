import core.collections.*
// expect-output: array-identity-ok
// expect-exit: 0
// 负例只判断/转换，不以错误类型访问元素；泛型开放体也必须保留身份。
func check\<T>(value: Array\<T>): bool {
    const boxed: Any = value
    return (boxed is Array\<T>) and (not (boxed is Array\<Any>))
}
pub func main(): i32 {
    var i = 0
    while (i < 64) {
        const numbers = arrayOf\<i32>(3)
        const strings = arrayOf\<String>(3)
        numbers[0] = i
        strings[0] = "值-" + i.toString()
        const nested = arrayOf\<Array\<i32>>(1)
        nested[0] = numbers
        const a: Any = numbers
        const b: Any = strings
        const c: Any = nested
        if ((not check(numbers)) or (not check(strings))) { return 1 }
        if ((a is Array\<String>) or (b is Array\<i32>)) { return 2 }
        if ((c is Array\<Array\<String>>) or (c is Array\<Any>)) { return 3 }
        if (not (c is Array\<Array\<i32>>)) { return 4 }
        var rejected = 0
        try { const wrong = a as Array\<String> }
        catch (e: CastException) { rejected += 1 }
        try { const wrong = b as Array\<i32> }
        catch (e: CastException) { rejected += 1 }
        try { const wrong = c as Array\<Array\<String>> }
        catch (e: CastException) { rejected += 1 }
        if (rejected != 3) { return 5 }
        if (((a as Array\<i32>)[0] as i32) != i) { return 6 }
        if (((b as Array\<String>)[0] as String) != ("值-" + i.toString())) { return 7 }
        i += 1
    }
    core.io.Console.println("array-identity-ok")
    return 0
}
