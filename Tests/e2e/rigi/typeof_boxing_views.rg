import core.collections.*
import core.serialization.*
// expect-output: typeof-boxing-ok
// expect-exit: 0
// b4-2 typeOf 装箱视图完备性回归（native 缺陷修复）：getid.var 的运行时
// 结果是操作数实际类型的 TypeSheet*；Type<X> 值再装箱为 Any（toString/
// 存 Any 槽/传 Any 形参/`is Type值`）需要 `.typeid<X>` 视图 sheet。
// 早前 ConstructedTypeCollector 只为闭合泛型实参建视图，非泛型类/标量
// 等 typeof 可达类型无视图 → native 抛 CastException「无法将 .typeid<…>
// 转换为 <实际类型>」（VM 正常）。本用例锁定：可空形参收窄后取 typeOf、
// typeOf 直接取可空形参、Type 值装箱进 Any 容器、is 右侧 Type 值。

class Probe { pub var n: i32 = 7 }

// typeOf 直接作用于 Any? 形参
func describeDirect(value: Any?): String { return typeOf(value).toString() }

// 可空判定收窄后再取 typeOf（smart-cast + typeof 形态）
func describeNarrowed(value: Any?): String {
    if (value != null) {
        return typeOf(value).toString()
    }
    return "null"
}

pub func main(): i32 {
    const p = new Probe()
    // 非泛型用户类的 typeOf + 装箱 toString（原缺陷形态）
    if (describeDirect(p) != "Probe") { return 1 }
    if (describeNarrowed(p) != "Probe") { return 2 }
    if (describeNarrowed(null) != "null") { return 3 }
    // 标量/String 经 Any? 槽的 typeOf
    if (describeDirect("s") != ".string") { return 4 }
    if (describeDirect((1 as i32)) != ".i32") { return 5 }
    if (describeDirect(true) != ".bool") { return 6 }
    // Type<X> 值装箱进 Any 容器再读出（BoxFromSlot TypeId 路径 + Any→
    // Type<X> 拆箱路径；断言语义行为而非名称文本——Type 值的 toString
    // 文本两种宿主本来就不同：VM TypeId.ToStandardText = BIL 别名形，
    // native 走 TypeInfo.name canonical）。
    const t = typeOf(p)
    const boxed = core.collections.arrayOf\<Any>(1)
    boxed[0] = t
    const back = boxed[0] as Any
    const t2 = back as core.Type\<Probe>
    if (not (p is t2)) { return 7 }
    // is 右侧直接使用 Type 值（TypeCheckEmitter 视图路径）
    if (not (p is t)) { return 8 }
    const other = new Probe()
    if (not (other is t)) { return 9 }
    const s = "text"
    if (s is t) { return 10 }
    core.io.Console.println("typeof-boxing-ok")
    return 0
}
