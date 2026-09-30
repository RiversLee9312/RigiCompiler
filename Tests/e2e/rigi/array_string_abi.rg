// Array<String> 元素槽 ABI 归一回归（RUNTIME §5：数组元素槽布局由数组
// 头 elemSheet 唯一决定；静态 .string 上下文与泛型共享体上下文访问同一
// 数组必须经 elemSheet 归一，禁止假设 16B 槽位都是 {typeid,payload}
// 胖引用形态——String 特化槽是 {data,len}。修复前泛型枚举器 current 读
// 出槽后把 data 指针当 typeid 解引用，native 0xC0000005、VM 正常）。
// 覆盖矩阵：
//   1) arrayOf 零值构造 + 静态 .string 写 + asEnumerable for-in（修复前
//      主崩路径；零槽 null 元素按 VM 口径放行，借用适配直读当前内容）；
//   2) arrayOfElements 泛型写 + for-in 逐元素打印（泛型↔泛型 + 打印）；
//   3) 静态 while 索引读（静态读方↔静态写方配对）；
//   4) 手动 new ListEnumerator + moveNext/current（borrow 直读当前值）；
//   5) AtomicSnapshot<String> 快照遍历（deepCopy 泛型写读路径）。
// expect-output: x
// expect-output: abi-enumerate-ok
// expect-output: p
// expect-output: q
// expect-output: abi-elemstr-ok
// expect-output: abi-while-ok
// expect-output: m
// expect-output: abi-manual-ok
// expect-output: abi-snapshot-ok
// expect-exit: 0
import core.collections.*
import core.io.Console

// 1) 修复前主崩路径：静态写 Array<String> 后泛型枚举器读出
func enumerateZeroFilled(): i32 {
    const a = arrayOf\<String>(3)
    a[0] = "x"
    var n: i32 = 0
    for (q in asEnumerable(a)) {
        n = (n + 1)
        if (n == 1) { Console.println(q) }
    }
    if (n != 3) { throw new core.RuntimeException("遍历次数不是 3") }
    Console.println("abi-enumerate-ok")
    return n
}

// 2) 泛型构造 + 泛型枚举读，逐元素打印（非零槽全长）
func enumerateElemStr(): i32 {
    const a = arrayOfElements\<String>("p", "q")
    var n: i32 = 0
    for (q in asEnumerable(a)) {
        Console.println(q)
        n = (n + 1)
    }
    if (n != 2) { throw new core.RuntimeException("遍历次数不是 2") }
    Console.println("abi-elemstr-ok")
    return n
}

// 3) 静态 while 索引读（Array<String> 闭合具化 get.array 路径；
//    索引读按空安全语义返回 Nullable<String>，非空拆包后拼接）
func whileRead(): i32 {
    const a = arrayOf\<String>(2)
    a[0] = "w"
    a[1] = "z"
    const s0 = (a[0] as String)
    const s1 = (a[1] as String)
    if ((s0 + s1) != "wz") { throw new core.RuntimeException("索引读不一致") }
    Console.println("abi-while-ok")
    return a.length
}

// 4) 手动 ListEnumerator（pub 构造收裸 Array<T>；borrow 直读当前值，
//    与 AtomicSnapshot 共用的泛型枚举器路径）
func manualEnumerator(): i32 {
    const a = arrayOf\<String>(2)
    a[0] = "m"
    const it = new ListEnumerator\<String>(a, a.length)
    var n: i32 = 0
    while (it.moveNext()) {
        const s = it.current()
        if (n == 0) { Console.println(s) }
        n = (n + 1)
    }
    if (n != 2) { throw new core.RuntimeException("手动枚举次数不是 2") }
    Console.println("abi-manual-ok")
    return n
}

// 5) AtomicSnapshot 快照遍历（iterate 内 deepCopy 泛型写读同一数组）
func snapshotWalk(): i32 {
    const a = arrayOfElements\<String>("s1", "s2")
    const snap = new AtomicSnapshot\<String>(a)
    var n: i32 = 0
    for (q in snap) {
        n = (n + 1)
    }
    if (n != 2) { throw new core.RuntimeException("快照遍历次数不是 2") }
    Console.println("abi-snapshot-ok")
    return n
}

pub func main(): i32 {
    var r = enumerateZeroFilled()
    r = (r + enumerateElemStr())
    r = (r + whileRead())
    r = (r + manualEnumerator())
    r = (r + snapshotWalk())
    if (r != 11) { throw new core.RuntimeException("合计异常") }
    return 0
}
