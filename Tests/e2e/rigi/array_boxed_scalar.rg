import core.collections.*
import core.io.*
import core.serialization.*
import core.serialization.json.*
import core.text.*
// expect-output: array-boxed-scalar-ok
// expect-exit: 0
// jsonfix 施工：泛型胖引用形态守卫放行面语料（RUNTIME §3「元素槽布局
// 铁律」修订：引用擦除容器槽允许 tag0 装箱标量形态）。覆盖：
//   - arrayOfElements<Any?> 装箱标量：变参数组的调用方静态路径把 tag0
//     胖值（{tag0|标量 sheet, 位形}）写入 Any-sheet 引用槽，泛型共享体
//     构造环（读-写-读）必须放行——守卫前提收窄的定点面（曾误判
//     「tag0 且 payload 非零即 ABI 错配」abort）；
//   - 装箱标量读回-写出保真（tag==0 && sheet 匹配的 unbox 口径，值不
//     变即证）；
//   - 三层嵌套 Any? 容器构造 + 深度上限写出（json_write.rg G3 形态剥
//     离版，case#552 的最小定点）；
//   - 默认深度下同一嵌套容器照常写出（守卫放行不改变值语义）。

// MemoryOutputStream → 文本（toSpan 导出后严格 UTF-8 解码；dispose 前取）。
func absToText(out: MemoryOutputStream): String {
    const decoder = new Utf8Decoder()
    return decoder.decode(out.toSpan(), true)
}

func absJson(ser: JsonSerializer, value: Any?): String {
    const out = new MemoryOutputStream()
    ser.write(value, out)
    const text = absToText(out)
    out.dispose()
    return text
}

func absExpect(actual: String, want: String, code: i32): i32 {
    if (actual == want) { return 0 }
    Console.println("FAIL ${code}: got [${actual}] want [${want}]")
    return code
}

pub func main(): i32 {
    var rc: i32 = 0

    // 1) 装箱标量进 Any? 变参数组：构造即触发泛型共享体读-写-读环
    //（曾在此 abort：泛型读回把装箱标量 tag0 胖值当 ABI 错配）。
    const inr = arrayOfElements\<Any?>((1 as i32))
    if (inr.length != (1 as i32)) { return 1 }

    // 2) 装箱标量读回-写出保真（tag0 自证放行 + unbox 口径，值不变即证）。
    rc = absExpect(absJson(new JsonSerializer(), inr), "[1]", (2 as i32))
    if (rc != 0) { return rc }

    // 3) 三层嵌套 Any?（json_write.rg G3 同形态构造）。
    const mdl = arrayOfElements\<Any?>(inr)
    const deep = arrayOfElements\<Any?>(mdl)
    if (deep.length != (1 as i32)) { return 3 }

    // 4) 深度上限写出：根=1，三层嵌套 > 2 → JsonException（写出路径在
    // 碰元素前抛出——与 json_write G3 同口径）。
    const ser3 = new JsonSerializer("", (2 as i32))
    var threw = false
    try {
        ser3.write(deep, new MemoryOutputStream())
    } catch (e: JsonException) {
        threw = true
    }
    if (not threw) { return 4 }

    // 5) 默认深度下同一嵌套容器照常写出（守卫放行不改变值语义）。
    rc = absExpect(absJson(new JsonSerializer(), deep), "[[[1]]]", (5 as i32))
    if (rc != 0) { return rc }

    Console.println("array-boxed-scalar-ok")
    return 0
}
