import core.collections.*
import core.serialization.*
// expect-output: sb-views-ok
// expect-exit: 0
// 块 4-2 B 面：格式层视角的容器动态遍历/构造冒烟（§4.6.1 / D3，擦除
// SB 视图）。sbKind 标量宽度分类与拒绝、sbLength、sbElementAt 活值上抛
// 与元素 null 原样、sbKeyAt/sbValueAt 插入序、sbBuild* 双拼写名称分发
// 与严格核验（类型不符 CastException、未登记名 IllegalArgumentException）。

class Biz { pub var n: i32 = 1 }

pub func main(): i32 {
    const boxedI: Any = (42 as i32)
    const boxedU: Any = (1 as u64)
    const boxedS: Any = "text"
    const boxedC: Any = 'c'
    const boxedF: Any = float.parse("1.5")
    const boxedD: Any = double.parse("2.5")
    const boxedB: Any = true
    const p = new Parcel("V")
    p.setDynamic("k", (1 as i32))
    const boxedP: Any = p
    const li = new List\<i32>()
    li.add(7)
    li.add(8)
    const boxedL: Any = li
    const nl = new List\<i32?>()
    nl.add(null)
    nl.add(9)
    const boxedN: Any = nl
    const ma = new Map\<String, i32>()
    ma.set("a", 1)
    ma.set("b", 2)
    const boxedM: Any = ma
    const arr = arrayOf\<String>(2)
    arr[0] = "x"
    arr[1] = "y"
    const boxedA: Any = arr

    // ---- sbKind：标量宽度 / String / Parcel / 容器 / 业务对象拒绝 ----
    if (sbKind(boxedI) != 7) { return 1 }
    if (sbKind(boxedU) != 10) { return 2 }
    if (sbKind(boxedS) != 13) { return 3 }
    if (sbKind(boxedC) != 2) { return 4 }
    if (sbKind(boxedF) != 11) { return 5 }
    if (sbKind(boxedD) != 12) { return 6 }
    if (sbKind(boxedB) != 1) { return 7 }
    if (sbKind(boxedP) != 14) { return 8 }
    if (sbKind(boxedL) != 16) { return 9 }
    if (sbKind(boxedN) != 16) { return 10 }
    if (sbKind(boxedM) != 17) { return 11 }
    if (sbKind(boxedA) != 15) { return 12 }
    const boxedZ: Any = new Biz()
    if (sbKind(boxedZ) != 0) { return 13 }

    // ---- sbLength：元素数 / 条目数 / 业务字段数 ----
    if (sbLength(boxedL) != (2 as i64)) { return 14 }
    if (sbLength(boxedM) != (2 as i64)) { return 15 }
    if (sbLength(boxedA) != (2 as i64)) { return 16 }
    if (sbLength(boxedP) != (1 as i64)) { return 17 }

    // ---- sbElementAt：活值上抛；元素 null 原样出现 ----
    const e0 = sbElementAt(boxedL, (0 as i64))
    if ((e0 as i32) != (7 as i32)) { return 18 }
    const n0 = sbElementAt(boxedN, (0 as i64))
    if (n0 != null) { return 19 }
    const n1 = sbElementAt(boxedN, (1 as i64))
    if ((n1 as i32) != (9 as i32)) { return 20 }
    const a1 = sbElementAt(boxedA, (1 as i64))
    if ((a1 as String) != "y") { return 21 }

    // ---- sbKeyAt/sbValueAt：按插入序 ----
    const k0 = sbKeyAt(boxedM, (0 as i64))
    if ((k0 as String) != "a") { return 22 }
    const v1 = sbValueAt(boxedM, (1 as i64))
    if ((v1 as i32) != (2 as i32)) { return 23 }

    // ---- sbBuildList：双拼写名称分发（VM ".i32" / native "core::i32"）----
    const src = core.collections.arrayOf\<Any?>(2)
    src[0] = (5 as i32)
    src[1] = (6 as i32)
    const built1 = sbBuildList(".i32", src)
    if (not (built1 is List\<i32>)) { return 24 }
    const bl1 = built1 as List\<i32>
    if (bl1.length != (2 as i64)) { return 25 }
    if ((bl1.getAtIndex((1 as i64)) if? (0 as i32)) != (6 as i32)) { return 26 }
    const built2 = sbBuildList("core::i32", src)
    if (not (built2 is List\<i32>)) { return 27 }

    // ---- sbBuildMap：键值类型名分发 ----
    const ks = core.collections.arrayOf\<Any?>(2)
    ks[0] = "x"
    ks[1] = "y"
    const vs = core.collections.arrayOf\<Any?>(2)
    vs[0] = (1 as i32)
    vs[1] = (2 as i32)
    const builtM = sbBuildMap("core::String", "core::i32", ks, vs)
    if (not (builtM is Map\<String, i32>)) { return 28 }
    const bm = builtM as Map\<String, i32>
    if (bm.count != (2 as i64)) { return 29 }
    if ((bm.tryGet("y") if? (0 as i32)) != (2 as i32)) { return 30 }

    // ---- sbBuildArray ----
    const ss = core.collections.arrayOf\<Any?>(2)
    ss[0] = "m"
    ss[1] = "n"
    const builtA = sbBuildArray(".string", ss)
    if (not (builtA is Array\<String>)) { return 31 }
    const ba = builtA as Array\<String>
    if ((ba[1] as String) != "n") { return 32 }

    // ---- 构造严格性：元素类型不符抛 CastException ----
    // 先在本模块登记 List<i64> 实参（否则名称分发在 CastException 之前
    // 就以「未登记」拒绝——两种失败的分界正是本断言要锁的行为）。
    const warm = new List\<i64>()
    warm.add((0 as i64))
    var threw = false
    try { const bad = sbBuildList("core::i64", src) }
    catch (e: core.CastException) { threw = true }
    if (not threw) { return 33 }
    // 未登记类型名抛 IllegalArgumentException
    threw = false
    try { const bad2 = sbBuildList("NoSuchType", src) }
    catch (e: core.IllegalArgumentException) { threw = true }
    if (not threw) { return 34 }

    // ---- sbTypeName：非空且可被格式层消费（双宿主拼写不同，只锁头部）----
    const tn = sbTypeName(boxedL)
    if (tn.characterCount == (0 as i64)) { return 35 }
    if ((tn.indexOf("List") if? ((0 as i64) - (1 as i64))) < (0 as i64)) { return 36 }

    core.io.Console.println("sb-views-ok")
    return 0
}
