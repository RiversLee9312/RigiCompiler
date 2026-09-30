// Array<T> 泛型占位元素 >8B 内联 struct 读写回归（RUNTIME §5：数组元素
// 槽布局由数组头 elemSheet 唯一决定；泛型共享体（typeid 擦除）下内联
// 判定必须按 elemSheet 的 FlagInlineValue，禁止 size≤8 启发式——>8B 的
// struct（如 TimeSpan 72B）槽布局是按值内联字节，曾因此被误判 16B 胖
// 引用槽：arrayOfElements<struct> 写入把 {sheet指针, box指针} 当元素
// 值落槽（native 元素损坏/VM 正常），泛型 Array<T> 形参读写 >8B struct
// 同样在 native 侧读崩于 rigi_check_fat_ref）。覆盖矩阵：
//   1) arrayOfElements 包 16B 自定义 struct（i64+i32）×1/3/5 元素逐字段
//      断言（修复前 x 恒为 typeid 指针、y 为 box 堆指针）；
//   2) 72B 级 struct（TimeSpan，@Serializable 隐藏存储）三元素同款；
//   3) 带 String 引用字段的 >8B struct（refMap/acquire/release rich
//      copy 路径：写槽 acquire 源盒内嵌引用、release 旧槽内容）；
//   4) 泛型 Array<T> 形参读（readBack）/ 写（writeAt）>8B struct；
//   5) 负向不回归：i32 / String / class 元素经同一泛型写路径仍正确。
// expect-output: pair[0]: x=100 y=7
// expect-output: pair[1]: x=200 y=8
// expect-output: pair[2]: x=300 y=9
// expect-output: single[0]: x=999 y=5
// expect-output: five[0]: x=1 y=1
// expect-output: five[1]: x=2 y=2
// expect-output: five[2]: x=3 y=3
// expect-output: five[3]: x=4 y=4
// expect-output: five[4]: x=5 y=5
// expect-output: ts[0]: ms=1000
// expect-output: ts[1]: ms=2000
// expect-output: ts[2]: ms=3000
// expect-output: withstr[0]: hello 11
// expect-output: withstr[1]: world 22
// expect-output: gen-read: x=111 y=11
// expect-output: after-gen-write: x=333 y=33
// expect-output: neg-i32: 11 22 33
// expect-output: neg-str: aa bb cc
// expect-output: neg-class: 1 2 3
// expect-output: inline-struct-ok
// expect-exit: 0
import core.time.*
import core.collections.*
import core.io.Console

pub struct Pair2 {
    pub var x: i64
    pub var y: i32
    pub init(_ -> x, _ -> y) { }
}

pub struct WithStr {
    pub var s: String
    pub var v: i64
    pub init(_ -> s, _ -> v) { }
}

pub class BoxC {
    pub var v: i64
    pub init(_ -> v) { }
}

func dumpPair2(tag: String, arr: Array\<Pair2>) {
    var i: i32 = 0
    while (i < arr.length) {
        var t = (arr[i] as Pair2)
        Console.println("${tag}[${i}]: x=${t.x} y=${t.y}")
        i = i + 1
    }
}

// 4) 泛型读：泛型函数内经数组头 elemSheet 运行时读回 T（>8B struct）
func readBack\<T>(arr: Array\<T>): T {
    return (arr[0] as T)
}

// 4) 泛型写：泛型函数内写 Array<T> 槽（.generic.T 运行时内联判定）
func writeAt\<T>(arr: Array\<T>, v: T) {
    arr[0] = v
}

@EntryPoint
func main(): i32 {
    // 1) 16B struct × 3
    var ap = arrayOfElements\<Pair2>(new Pair2(100L, 7), new Pair2(200L, 8), new Pair2(300L, 9))
    dumpPair2("pair", ap)
    // 1) 16B struct × 1（单元素）
    var s1 = arrayOfElements\<Pair2>(new Pair2(999L, 5))
    dumpPair2("single", s1)
    // 1) 16B struct × 5（多元素）
    var s5 = arrayOfElements\<Pair2>(new Pair2(1L, 1), new Pair2(2L, 2), new Pair2(3L, 3), new Pair2(4L, 4), new Pair2(5L, 5))
    dumpPair2("five", s5)
    // 2) 72B 级 struct（TimeSpan）× 3
    var ts = arrayOfElements\<TimeSpan>(
        TimeSpan.fromMilliseconds(1000L),
        TimeSpan.fromMilliseconds(2000L),
        TimeSpan.fromMilliseconds(3000L))
    var ti: i32 = 0
    while (ti < ts.length) {
        Console.println("ts[${ti}]: ms=${(ts[ti] as TimeSpan).totalMilliseconds}")
        ti = ti + 1
    }
    // 3) 带 String 引用字段的 >8B struct（refMap rich copy 路径）× 2
    var ws = arrayOfElements\<WithStr>(new WithStr("hello", 11L), new WithStr("world", 22L))
    var wi: i32 = 0
    while (wi < ws.length) {
        var t = (ws[wi] as WithStr)
        Console.println("withstr[${wi}]: ${t.s} ${t.v}")
        wi = wi + 1
    }
    // 4) 泛型 Array<T> 形参读/写（>8B struct）
    var arr = arrayOf\<Pair2>(2)
    arr[0] = new Pair2(111L, 11)
    arr[1] = new Pair2(222L, 22)
    var r = readBack\<Pair2>(arr)
    Console.println("gen-read: x=${r.x} y=${r.y}")
    writeAt\<Pair2>(arr, new Pair2(333L, 33))
    Console.println("after-gen-write: x=${(arr[0] as Pair2).x} y=${(arr[0] as Pair2).y}")
    // 5) 负向：i32 / String / class 经同一泛型写路径不回归
    var ai = arrayOfElements\<i32>(11, 22, 33)
    Console.println("neg-i32: ${ai[0]} ${ai[1]} ${ai[2]}")
    var as_ = arrayOfElements\<String>("aa", "bb", "cc")
    Console.println("neg-str: ${as_[0]} ${as_[1]} ${as_[2]}")
    var ac = arrayOfElements\<BoxC>(new BoxC(1L), new BoxC(2L), new BoxC(3L))
    Console.println("neg-class: ${(ac[0] as BoxC).v} ${(ac[1] as BoxC).v} ${(ac[2] as BoxC).v}")
    Console.println("inline-struct-ok")
    return 0
}
