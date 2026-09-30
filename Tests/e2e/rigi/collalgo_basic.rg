// STDLIB §4.2.3 / §7.2「集合算法」（施工块 1-7）：core.collections 通用
// 算法 map/filter/fold/any/all/contains/find/findIndex/toArray/toList 语料。
// 覆盖：基本行为与立即求值（回调对每元素恰好一次——计数 lambda 验证单次
// 遍历）；短路（any/all/find/findIndex 以计数 predicate、contains 以计数
// equals 验证命中后不再访问后续元素）；空输入（fold=初始值、any=false、
// all=true，回调零调用）；find 命中 null 与未命中由首项 bool 区分、
// findIndex 零基与未命中 null；toArray/toList 容器独立性（浅复制：元素
// 对象引用同实例、容器互不影响）；回调重入修改 List 输入 → 经既有失效
// 检测抛 core.IllegalStateException（§4.2.4 既定行为）；算法对 Array 借用
// 适配器输入同样工作（§4.2.1 IEnumerable 协议一致）。
// expect-output: algo-map-filter-fold-ok
// expect-output: algo-any-all-shortcircuit-ok
// expect-output: algo-contains-shortcircuit-ok
// expect-output: algo-find-findindex-ok
// expect-output: algo-empty-ok
// expect-output: algo-find-null-ok
// expect-output: algo-array-independence-ok
// expect-output: algo-list-independence-ok
// expect-output: algo-shallow-copy-ok
// expect-output: algo-adapter-input-ok
// expect-output: algo-reentrant-ise-ok
// expect-exit: 0
import core.collections.*

// equals 调用计数（contains 短路验证用）：全局 var，Probe.equals 内累加
var equalsCalls: i32 = 0

// 自定义 equals 探针：同 tag 视为相等（== 经运行期最派生 operator
// equals），每次判等计一次数——contains 命中即停时后续元素不参与判等
class Probe {
    pub var tag: i32
    pub init(t: i32) { tag = t }
    pub operator equals(other: Probe): bool {
        equalsCalls = (equalsCalls + 1)
        return (tag == other.tag)
    }
}

// 可变字段对象（浅复制验证用）：默认 equals/hash（身份），不参与判等
class Holder {
    pub var v: i32
    pub init(x: i32) { v = x }
}

var step: i32 = 0
func require(value: bool) {
    step = (step + 1)
    if (value == false) { throw new core.RuntimeException("collalgo 断言失败 " + step.toString()) }
}

// i32/i64 解包回退哨兵（if? 未命中侧）
var missI32: i32 = (0 - 1)
var missI64: i64 = ((0 as i64) - (1 as i64))

pub func main(): i32 {
    // ── map/filter/fold 基本行为 + 回调调用次数（每元素恰好一次）──
    const src = new List\<i32>()
    src.add(1)
    src.add(2)
    src.add(3)

    // map：每个元素各调一次 selector，结果按序装入新 List（立即求值）
    var selCalls: i32 = 0
    const mapped = map\<i32, i32>(src, func{(x: i32): i32 -> {
        selCalls = (selCalls + 1)
        return@_ (x * 10)
    }})
    require((mapped.length == (3 as i64)))
    require(((mapped.getAtIndex((0 as i64)) if? missI32) == 10))
    require(((mapped.getAtIndex((1 as i64)) if? missI32) == 20))
    require(((mapped.getAtIndex((2 as i64)) if? missI32) == 30))
    // 单次遍历：3 个元素恰好 3 次回调（不预计数后再遍历的重复访问）
    require((selCalls == 3))
    // 输入未被修改（map 返回新容器）
    require((src.length == (3 as i64)))

    // filter：全部元素各调一次 predicate（无短路），命中项按原序收集
    var predCalls: i32 = 0
    const big = filter\<i32>(src, func{(x: i32): bool -> {
        predCalls = (predCalls + 1)
        return@_ (x > 1)
    }})
    require((big.length == (2 as i64)))
    require(((big.getAtIndex((0 as i64)) if? missI32) == 2))
    require(((big.getAtIndex((1 as i64)) if? missI32) == 3))
    require((predCalls == 3))

    // fold：显式初始值按序折叠（1+2+3=6），每元素恰好一次 folder
    var foldCalls: i32 = 0
    const sum = fold\<i32, i32>(src, 0, func{(acc: i32, x: i32): i32 -> {
        foldCalls = (foldCalls + 1)
        return@_ (acc + x)
    }})
    require((sum == 6))
    require((foldCalls == 3))
    core.io.Console.println("algo-map-filter-fold-ok")

    // ── any/all 短路：命中/失败后计数不再增长 ──
    var anyCalls: i32 = 0
    const anyHit = any\<i32>(src, func{(x: i32): bool -> {
        anyCalls = (anyCalls + 1)
        return@_ (x == 2)
    }})
    require(anyHit)
    // 1 判 false、2 判 true 即停：3 未访问（计数停在此处）
    require((anyCalls == 2))

    var allCalls: i32 = 0
    const allSmall = all\<i32>(src, func{(x: i32): bool -> {
        allCalls = (allCalls + 1)
        return@_ (x < 2)
    }})
    require((allSmall == false))
    // 1 过、2 败即停：3 未访问
    require((allCalls == 2))
    core.io.Console.println("algo-any-all-shortcircuit-ok")

    // ── contains 短路（无回调：以计数 equals 验证）──
    const probes = new List\<Probe>()
    probes.add(new Probe(1))
    probes.add(new Probe(2))
    probes.add(new Probe(3))
    equalsCalls = 0
    const hasTwo = contains\<Probe>(probes, new Probe(2))
    require(hasTwo)
    // 元素 1、元素 2 各判等一次即命中：元素 3 未参与判等
    require((equalsCalls == 2))
    // 未命中：全部元素各判等一次（3 次）后返回 false
    equalsCalls = 0
    require((contains\<Probe>(probes, new Probe(9))) == false)
    require((equalsCalls == 3))
    core.io.Console.println("algo-contains-shortcircuit-ok")

    // ── find/findIndex：短路、零基、未命中 ──
    var findCalls: i32 = 0
    const found = find\<i32>(src, func{(x: i32): bool -> {
        findCalls = (findCalls + 1)
        return@_ (x >= 2)
    }})
    // 命中：首项 true、次项为命中值（i32? 经 if? 解包比较，P3 同型要求）
    require((found.key == true))
    require(((found.value if? missI32) == 2))
    // 1 判 false、2 判 true 即停
    require((findCalls == 2))

    var fiCalls: i32 = 0
    const at = findIndex\<i32>(src, func{(x: i32): bool -> {
        fiCalls = (fiCalls + 1)
        return@_ (x == 1)
    }})
    // 零基：首个元素位置是 0（i64）；首元素即命中，计数 1
    require(((at if? missI64) == (0 as i64)))
    require((fiCalls == 1))

    // 未命中：findIndex 返回 null（i64? 与 null 判等）
    require((findIndex\<i32>(src, func{(x: i32): bool -> false}) == null))
    // find 未命中：Pair(false, null)
    const missed = find\<i32>(src, func{(x: i32): bool -> false})
    require((missed.key == false))
    require((missed.value == null))
    core.io.Console.println("algo-find-findindex-ok")

    // ── 空输入：fold=初始值、any=false、all=true，回调零调用 ──
    const empty = new List\<i32>()
    var emptyCalls: i32 = 0
    require((fold\<i32, i32>(empty, 42, func{(acc: i32, x: i32): i32 -> {
        emptyCalls = (emptyCalls + 1)
        return@_ acc
    }}) == 42))
    require((any\<i32>(empty, func{(x: i32): bool -> {
        emptyCalls = (emptyCalls + 1)
        return@_ true
    }})) == false)
    require((all\<i32>(empty, func{(x: i32): bool -> {
        emptyCalls = (emptyCalls + 1)
        return@_ false
    }})) == true)
    // 全程回调零调用（短路/空序列契约）
    require((emptyCalls == 0))
    require((findIndex\<i32>(empty, func{(x: i32): bool -> true}) == null))
    require((toArray\<i32>(empty).length == 0))
    require((toList\<i32>(empty).length == (0 as i64)))
    core.io.Console.println("algo-empty-ok")

    // ── find 命中 null 与未命中的区分（T 实例化为可空类型）──
    const lsn = new List\<String?>()
    lsn.add("a")
    lsn.add(null)
    lsn.add("b")
    // 命中 null 元素：Pair(true, null)——首项 true 与未命中区分
    const hitNull = find\<String?>(lsn, func{(x: String?): bool -> (x == null)})
    require((hitNull.key == true))
    require((hitNull.value == null))
    // 遍历到第二个元素（null 槽）才命中：首个 "a" 已判 false
    const firstIsNotNull = find\<String?>(lsn, func{(x: String?): bool -> (x != null)})
    require((firstIsNotNull.key == true))
    require(((firstIsNotNull.value as String) == "a"))
    // 未命中：Pair(false, null)——与命中 null 由首项裁决
    const none = find\<String?>(lsn, func{(x: String?): bool -> false})
    require((none.key == false))
    require((none.value == null))
    // findIndex 对可空元素同样工作（命中 null 槽的位置 1）
    const nullAt = findIndex\<String?>(lsn, func{(x: String?): bool -> (x == null)})
    require(((nullAt if? missI64) == (1 as i64)))
    core.io.Console.println("algo-find-null-ok")

    // ── toArray 独立性：源变化不影响结果，写结果不影响源 ──
    const srcArr = toArray\<i32>(src)
    require((srcArr.length == 3))
    // Array 索引读得 T?（Q6）；界内槽非 null，as 解包后比较
    require(((srcArr[0] as i32) == 1))
    require(((srcArr[2] as i32) == 3))
    src.add(4)
    // 源追加不改变已生成的 Array（独立容器，非视图）
    require((srcArr.length == 3))
    // 写结果不回灌源
    srcArr[0] = 99
    require(((src.getAtIndex((0 as i64)) if? missI32) == 1))
    core.io.Console.println("algo-array-independence-ok")

    // ── toList 独立性：双向互不影响 ──
    const copied = toList\<i32>(src)
    require((copied.length == (4 as i64)))
    copied.setAtIndex((0 as i64), 77)
    require(((src.getAtIndex((0 as i64)) if? missI32) == 1))
    src.add(5)
    require((copied.length == (4 as i64)))
    core.io.Console.println("algo-list-independence-ok")

    // ── 浅复制：元素对象引用同一实例，容器互不影响 ──
    const objs = new List\<Holder>()
    const h1 = new Holder(1)
    objs.add(h1)
    const objsCopy = toList\<Holder>(objs)
    // 经副本读到的元素与源同一实例（浅复制不克隆对象）
    const viaCopy = (objsCopy.getAtIndex((0 as i64)) as Holder)
    viaCopy.v = 99
    const viaSrc = (objs.getAtIndex((0 as i64)) as Holder)
    require((viaSrc.v == 99))
    // 容器本身独立：源新增不进入副本
    objs.add(new Holder(2))
    require((objsCopy.length == (1 as i64)))
    require((objs.length == (2 as i64)))
    core.io.Console.println("algo-shallow-copy-ok")

    // ── Array 借用适配器输入：算法对任意 IEnumerable<T> 工作（§4.2.1）──
    const raw = arrayOfElements\<i32>(7, 8, 9)
    const arrSum = fold\<i32, i32>(asEnumerable(raw), 0, func{(acc: i32, x: i32): i32 -> (acc + x)})
    require((arrSum == 24))
    const arrList = toList\<i32>(asEnumerable(raw))
    require((arrList.length == (3 as i64)))
    require(((arrList.getAtIndex((2 as i64)) if? missI32) == 9))
    core.io.Console.println("algo-adapter-input-ok")

    // ── 回调重入修改 List 输入 → 既有失效检测抛 IllegalStateException ──
    //（§4.2.3：不得在回调中重入修改正在操作的输入或目标集合；本算法不
    // 额外设防，由 List 枚举器的修改计数自然抛错——既定行为）
    const reent = new List\<i32>()
    reent.add(1)
    reent.add(2)
    var iseCaught = false
    try {
        map\<i32, i32>(reent, func{(x: i32): i32 -> {
            // 回调内重入 add：下一次 moveNext 时失效
            reent.add(99)
            return@_ x
        }})
        // 未抛即违约
        require(false)
    } catch (e: core.IllegalStateException) {
        iseCaught = true
    }
    require(iseCaught)
    core.io.Console.println("algo-reentrant-ise-ok")

    return 0
}
