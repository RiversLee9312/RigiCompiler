import core.collections.*
import core.serialization.*
// expect-output: typeid-view-array-string-ok
// expect-exit: 0
// 最小独立回归：已物化闭合数组的 typeOf 装箱视图（`.typeid<.array<X>>`）。
// setDynamic → putDynamicChecked → isSbRepresentable → typeOf(值) 装箱，
// BoxEmitter 需按值实际类型（Array<String>）选 `.typeid<...>` 视图做身份；
// 视图收集不得依赖无关表达式里的 getid.type 使用点碰巧覆盖（曾因
// decodeAnyValue 分发条件的 typeof 比较被替换为字面量而丢失
// `.array<.string>` 视图，native 运行时抛
// CastException(.typeid<.any> → core::Array<core::String>)）。本语料不
// 依赖 JSON/decodeAnyValue 的任何分发条件形态，锁定视图收集的物料边界。

pub func main(): i32 {
    const p = new Parcel("V")
    const arr = arrayOf\<String>(1)
    arr[(0 as i32)] = "a"
    p.setDynamic("arr", arr)
    const back = p.getDynamic("arr")
    if (not (back is Array\<String>)) { return 1 }
    const s0 = (back as Array\<String>)[(0 as i32)] if? ""
    if (s0 != "a") { return 2 }
    core.io.Console.println("typeid-view-array-string-ok")
    return 0
}
