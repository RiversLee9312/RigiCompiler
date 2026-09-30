// Rigi 标准库：core.collections 迭代协议（SYNTAX.md §7.3/§15.3）
// 与 Array\<T> 构造入口（RUNTIME.md §26）。
// IEnumerable\<T>/IEnumerator\<T> 是 C# 风格双接口（可重入，每次
// iterate() 产生独立枚举器）。RangeEnumerator\<T> 是范围循环枚举器的
// 泛型抽象基类（S9f）：共享状态机骨架（value_/end_/started_ 字段与
// current() 实现），比较/步进逻辑按具体类型实现（moveNext 抽象——
// 运算指令由各具体类型在自身类型上下文中书写，Middleware 按 typeid
// 选择精确实现；BIL §11.1 运算键与 source-level intrinsic 无关）。
// RangeEnumeratorI32 是内建 i32 的 EnumerateInRange 实现（半开区间
// [start, end)、步长恒 +1，§15.3 / SDK 自举；语言通用语义见 §7.3）
// ——class 形态（SYNTAX.md §10：struct 不得实现接口）。
namespace core.collections

pub interface IEnumerator\<T> {
    func moveNext(): bool
    func current(): T
}

pub interface IEnumerable\<T> {
    func iterate(): IEnumerator\<T>
}

// 范围循环枚举器的泛型抽象基类（S9f）：协议级状态机骨架——当前值/
// 终点/门控字段与 current() 由基类提供，moveNext（含类型相关运算与
// 区间起点重置策略）由具体类型实现（protected 字段子类可读写，§16.1；
// 抽象类不可实例化，无 super 构造调用语法，基类不写 init——字段由
// 具体类 init 初始化）
pub abstract class RangeEnumerator\<T> implements IEnumerator\<T> {
    protected var value_: T
    protected var end_: T
    protected var started_: bool

    pub override func current(): T {
        return value_
    }

    pub abstract override func moveNext(): bool
}

pub class RangeEnumeratorI32 : RangeEnumerator\<i32> {
    priv var start_: i32

    pub init(start: i32, end: i32) {
        start_ = start
        end_ = end
        started_ = false
        // P18/S2（§9.3 DA）：非空字段须全路径定值赋值——value_ 语义上
        // 由 moveNext 首步覆写（started_ 门控），此处先赋哨兵满足 DA
        value_ = start
    }

    pub override func moveNext(): bool {
        if (started_) {
            value_ += 1
        } else {
            value_ = start_
            started_ = true
        }
        return (value_ < end_)
    }
}

// i32 范围的可枚举包装（半开区间 [start, end)）：每次 iterate() 产生
// 独立枚举器（双接口可重入语义，SYNTAX.md §7.3）。EnumerateInRange
// 运算符的返回形态（§13.2）
pub class RangeI32 implements IEnumerable\<i32> {
    priv var start_: i32
    priv var end_: i32

    pub init(start: i32, end: i32) {
        start_ = start
        end_ = end
    }

    pub override func iterate(): IEnumerator\<i32> {
        return new RangeEnumeratorI32(start_, end_)
    }
}

// Array\<T> 合法构造入口（RUNTIME.md §26 / BIL_STANDARD.md §22.5）：
// 用户代码只走 arrayOf / arrayOfElements；alloc_array 是私有 native，
// 经泛型 hidden .generic.T 物化 typeid，VM hook 分配零值数组。
// arrayOfElements 体内视角 elements 已是 Array\<T>（M78）。
@NativeLibrary("rigi_rt")
@NativeSymbol("alloc_array")
priv native func alloc_array\<T>(size: i32): Array\<T>

pub func arrayOf\<T>(size: i32): Array\<T> {
    if (size < 0) {
        throw new core.OutOfBoundException((size as i64), (0 as i64))
    }
    return alloc_array\<T>(size)
}

pub func arrayOfElements\<T>(elements: T...): Array\<T> {
    var result = alloc_array\<T>(elements.length)
    var i: i32 = elements.length
    i = i - elements.length
    while (i < elements.length) {
        // Q6（§13.2）：索引读取得 T?；此下标恒在界内（非空），as 解包
        // （泛型参数的 Nullable\<T\> 不参与 smart cast 收窄，§3.5/S9a）
        result[i] = (elements[i] as T)
        i = i + 1
    }
    return result
}

// Span\<T> / SharedSpan\<T> 合法构造入口（RUNTIME.md §5）：
// 用户代码只走 spanOf / sharedSpanOf；span_alloc / shared_span_alloc 是
// 私有 native。二者 @NativeSymbol 均为 "span_alloc"（同一 rigi 面；
// TypeSheet 由隐藏 typeid 与 callee 返回类型区分 Span vs SharedSpan）。
// 包装入口必须显式证明 Span/SharedSpan 要求的值类型约束，native 不豁免。
@NativeLibrary("rigi_rt")
@NativeSymbol("span_alloc")
priv native func span_alloc\<T extends ValueType>(size: i32): Span\<T>

pub func spanOf\<T extends ValueType>(size: i32): Span\<T> {
    return span_alloc\<T>(size)
}

@NativeLibrary("rigi_rt")
@NativeSymbol("span_alloc")
priv native func shared_span_alloc\<T extends ValueType>(size: i32): SharedSpan\<T>

pub func sharedSpanOf\<T extends ValueType>(size: i32): SharedSpan\<T> {
    return shared_span_alloc\<T>(size)
}

// 动态数组使用可空内部槽，删除时清空尾槽以释放引用，扩容倍增。
// getAtIndex 越界读 null（与语言索引协议 §13.2 对齐，方法面非 [] 运算符）；
// removeAt 越界抛 core.OutOfBoundException。
// STDLIB §4.2.2 补充方法：clear/contains/indexOf/insert/remove——判等一律
// 走既有 ==（equals-or-hash 判等链，绝不涉 toString）；insert 接受
// 0..length（length 处即追加），非法位置与容量触顶均抛
// core.OutOfBoundException。
// STDLIB §4.2.4 遍历修改检测：增（add/insert）、删（removeAt/remove 命中/
// clear）、赋值（setAtIndex——写相同值也计）每次成功修改使内部计数
// modCount +1；未成功的操作（remove 无匹配、越界抛错）不计。iterate()
// 创建的枚举器记下创建时计数，此后 moveNext/current 校验不一致即抛
// core.IllegalStateException（失效优先于正常结束）。
@SerializationBase
@core.serialization.Serializable
pub class List\<T> implements IEnumerable\<T> {
    priv var items: Array\<T?>
    priv var count: i32
    // §4.2.4 修改计数（internal：仅本模块 ListStorageEnumerator 读取做
    // 失效检测，不对模块外承诺；溢出不设防，实用即可）
    internal var modCount: i64 = (0 as i64)

    pub init() {
        items = arrayOf\<T?>(8)
        count = 0
    }

    pub func add(item: T) {
        if (count == items.length) { grow() }
        items[count] = item
        count = (count + 1)
        // §4.2.4：新增元素计修改
        modCount = (modCount + (1 as i64))
    }

    pub func getAtIndex(index: i64): T? {
        if ((index < (0 as i64)) or (index >= (count as i64))) {
            return null
        }
        return items[(index as i32)]
    }

    pub var length: i64 {
        pub get(_: _) { return (count as i64) }
    }

    pub func setAtIndex(index: i64, item: T) {
        if ((index < (0 as i64)) or (index >= (count as i64))) {
            throw new core.OutOfBoundException(index, (count as i64))
        }
        items[(index as i32)] = item
        // §4.2.4：成功赋值即使写入相同值也计修改
        modCount = (modCount + (1 as i64))
    }

    // 空表无槽位可写回，排序比较成功后由 sortInPlace 显式标记失效。
    internal func invalidateAfterEmptySort() {
        modCount = (modCount + (1 as i64))
    }

    pub func removeAt(index: i64) {
        if ((index < (0 as i64)) or (index >= (count as i64))) {
            throw new core.OutOfBoundException(index, (count as i64))
        }
        var i: i32 = (index as i32)
        while (i < (count - 1)) {
            items[i] = (items[(i + 1)] as T)
            i = (i + 1)
        }
        count = (count - 1)
        items[count] = null
        // §4.2.4：越界检查已过、删除落定后计修改（remove 命中路径经此共用）
        modCount = (modCount + (1 as i64))
    }

    // §4.2.2：清空列表——及时释放不再持有的引用（§4.2.1），界内槽位逐一
    // 置 null；容量保留不缩水，length 回零
    pub func clear() {
        var i: i32 = 0
        while (i < count) {
            items[i] = null
            i = (i + 1)
        }
        count = 0
        // §4.2.4：成功 clear 同样计修改（空表 clear 亦然）
        modCount = (modCount + (1 as i64))
    }

    // §4.2.2：是否含相等元素——判等通道见 slotEquals（== 即 equals-or-hash
    // 链）；NaN 不等于自身是 == 的自然结果，不做特判
    pub func contains(item: T): bool {
        return (findSlot(item) >= 0)
    }

    // §4.2.2：首个相等元素的零基位置（i64?），未命中返回 null
    pub func indexOf(item: T): i64? {
        const at = findSlot(item)
        if (at < 0) {
            return null
        }
        return (at as i64)
    }

    // §4.2.2：在 index 处插入，接受 0..length（length 处即追加）。非法位置
    // （负、大于 length）抛 core.OutOfBoundException——消息模板与
    // setAtIndex/removeAt 同款；容量触顶由 grow 的 i32 容量上限检查兜底
    // （同为 OutOfBoundException，不窄化不回绕）
    pub func insert(index: i64, item: T) {
        if ((index < (0 as i64)) or (index > (count as i64))) {
            throw new core.OutOfBoundException(index, (count as i64))
        }
        if (count == items.length) { grow() }
        // 从尾向左平移腾位；槽间搬运按 T? 直赋，无需解包（T 实例化为可空
        // 类型时的 null 槽原样平移）
        var i: i32 = count
        while (i > (index as i32)) {
            items[i] = items[(i - 1)]
            i = (i - 1)
        }
        items[(index as i32)] = item
        count = (count + 1)
        // §4.2.4：插入（含末尾追加位）计修改
        modCount = (modCount + (1 as i64))
    }

    // §4.2.2：删除首个相等元素并返回 true；无匹配不修改、返回 false
    //（删除复用 removeAt 的整体左移 + 尾槽置 null 释放引用）
    pub func remove(item: T): bool {
        const at = findSlot(item)
        if (at < 0) {
            return false
        }
        removeAt((at as i64))
        return true
    }

    pub override func iterate(): IEnumerator\<T> {
        // §4.2.4：传入宿主引用与当前修改计数——枚举器此后每次
        // moveNext/current 先校验计数，不一致即失效
        return new ListStorageEnumerator\<T>(items, count, this, modCount)
    }

    // 线性扫描首个相等元素的槽位（§4.2.2：首版允许线性查找）；未命中
    // 返回 -1。contains/indexOf/remove 三面共用
    priv func findSlot(item: T): i32 {
        var i: i32 = 0
        while (i < count) {
            if (slotEquals(items[i], item)) {
                return i
            }
            i = (i + 1)
        }
        return (0 - 1)
    }

    // 判等通道（§4.2.2）：槽位与元素按既有 == 判等——运行期最派生的
    // operator equals，未声明则走 Any 默认体（双虚调 hash 比较，
    // equals-or-hash 链），绝不涉 toString。槽 null 仅在 T 实例化为可空
    // 类型时出现：此时经实参的 T? 装箱视图（T → T? 恒可赋值，§3.4）做
    // null 判等——null 字面量定型为 Nullable\<T>（S9：元素可为泛型参数），
    // 不对裸泛型 T 直接写 `== null`（null 无法定型为裸 T，emitter P4）。
    // 非空槽按既有 as 通道解包后 ==（泛型参数的 Nullable\<T\> 不参与
    // smart cast 收窄，§3.5/S9a，as 恒必要）
    priv func slotEquals(slot: T?, item: T): bool {
        const boxed: T? = item
        if (slot == null) {
            return (boxed == null)
        }
        return ((slot as T) == item)
    }

    priv func grow() {
        if (items.length > 1073741823) {
            throw new core.OutOfBoundException(
                "List 容量超过 i32 可表示范围")
        }
        const bigger = arrayOf\<T?>((items.length * 2))
        var i: i32 = 0
        while (i < count) {
            bigger[i] = (items[i] as T)
            i = (i + 1)
        }
        items = bigger
    }
}

pub class ListEnumerator\<T> implements IEnumerator\<T> {
    priv const items: Array\<T>
    priv const count: i32
    priv var index: i32

    pub init(_ -> items, _ -> count) {
        index = (0 - 1)
    }

    pub override func moveNext(): bool {
        index = (index + 1)
        return (index < count)
    }

    pub override func current(): T {
        return (items[index] as T)
    }
}

// MW11d-B1：关联数组（内部 Pair\<K,V> 动态数组 + 线性查找）。哈希桶结构
// 仍不引入——线性扫描语义不变，判等通道已升级（Map 键判等再升级，用户
// 裁定）：K 相等 = `==`（equals-or-hash 判等链）——键类型声明了
// operator equals 则运行期最派生命中走它；未声明的类型走 Any 承诺的
// 默认 equals（双虚调 hash 比较，SYNTAX §13.2），hash 碰撞即判等（默认
// hash 对对象是身份哈希，不同身份的键互不覆盖；String/标量键的内容
// 判等语义不变）。**绝不涉 toString**。`==` 于无约束 K 合法：Any 承诺
// operator equals（§13.3 有效成员类型 Any 的承诺清单含 toString/hash/
// equals）。不硬编码 String 特化。
@SerializationBase
@core.serialization.Serializable
pub class Map\<K, V> implements IEnumerable\<core.Pair\<K, V>> {
    priv var ks: List\<K>
    priv var vs: List\<V>
    // §4.2.4 修改计数：set（新增键与覆盖已有键——写相同值也计）、remove
    // 命中、clear 各 +1；未命中的 remove 不计。（internal：仅本模块
    // MapEnumerator 读取做失效检测，不对模块外承诺）
    internal var modCount: i64 = (0 as i64)

    pub init() {
        ks = new List\<K>()
        vs = new List\<V>()
    }

    pub func set(key: K, value: V) {
        var i: i64 = (0 as i64)
        while (i < ks.length) {
            const k = ks.getAtIndex(i)
            if (keysEqual((k as K), key)) {
                vs.setAtIndex(i, value)
                // §4.2.4：覆盖已有键也是成功修改——写入相同值同样计
                modCount = (modCount + (1 as i64))
                return
            }
            i = (i + (1 as i64))
        }
        ks.add(key)
        vs.add(value)
        // §4.2.4：新增键计修改
        modCount = (modCount + (1 as i64))
    }

    pub func tryGet(key: K): V? {
        var i: i64 = (0 as i64)
        while (i < ks.length) {
            const k = ks.getAtIndex(i)
            if (keysEqual((k as K), key)) {
                return vs.getAtIndex(i)
            }
            i = (i + (1 as i64))
        }
        return null
    }

    pub func containsKey(key: K): bool {
        var i: i64 = (0 as i64)
        while (i < ks.length) {
            const k = ks.getAtIndex(i)
            if (keysEqual((k as K), key)) {
                return true
            }
            i = (i + (1 as i64))
        }
        return false
    }

    pub func remove(key: K): bool {
        var i: i64 = (0 as i64)
        while (i < ks.length) {
            const k = ks.getAtIndex(i)
            if (keysEqual((k as K), key)) {
                ks.removeAt(i)
                vs.removeAt(i)
                // §4.2.4：删除命中计修改（未命中走循环外的 return false，
                // 不计）
                modCount = (modCount + (1 as i64))
                return true
            }
            i = (i + (1 as i64))
        }
        return false
    }

    // §4.2.2：清空全部键值——及时释放不再持有的引用（§4.2.1），委托内部
    // 两条平行 List 的 clear（界内槽位置 null + 计数回零），清空后容器
    // 可继续使用；无新增容量运算面（List 层已检查溢出）
    pub func clear() {
        ks.clear()
        vs.clear()
        // §4.2.4：成功 clear 计修改（空表 clear 亦然；内部平行 List 的
        // 各自计数与本计数独立，Map 失效检测只看本计数）
        modCount = (modCount + (1 as i64))
    }

    // §4.2.2：按插入顺序返回全部键的**独立**新 List——逐槽复制而非暴露
    // 内部 ks，调用者随后修改或 clear Map 均不影响已返回结果；复制侧
    // 容量增长由 List.grow 的 i32 上限检查兜底（不窄化不回绕）
    pub func keys(): List\<K> {
        const result = new List\<K>()
        var i: i64 = (0 as i64)
        while (i < ks.length) {
            result.add((ks.getAtIndex(i) as K))
            i = (i + (1 as i64))
        }
        return result
    }

    // §4.2.2：按插入顺序返回全部值的**独立**新 List（与 keys 同构复制）
    pub func values(): List\<V> {
        const result = new List\<V>()
        var i: i64 = (0 as i64)
        while (i < vs.length) {
            result.add((vs.getAtIndex(i) as V))
            i = (i + (1 as i64))
        }
        return result
    }

    pub var count: i64 {
        pub get(_: _) { return ks.length }
    }

    pub func keyAtIndex(index: i64): K? {
        return ks.getAtIndex(index)
    }

    pub func valueAtIndex(index: i64): V? {
        return vs.getAtIndex(index)
    }

    pub override func iterate(): IEnumerator\<core.Pair\<K, V>> {
        // §4.2.4：传入宿主引用与当前修改计数做遍历修改检测；MapEnumerator
        // 同时保留裸平行 List 的旧构造契约（快照复用路径不经本面）
        return new MapEnumerator\<K, V>(ks, vs, this, modCount)
    }

    priv func keysEqual(a: K, b: K): bool {
        // equals-or-hash 判等链（用户裁定）：`==` 经运行期最派生的
        // operator equals——声明了 equals 的键走它，否则命中 Any 默认体
        //（双虚调 hash 比较）。绝不涉 toString
        return (a == b)
    }
}

pub class MapEnumerator\<K, V> implements IEnumerator\<core.Pair\<K, V>> {
    priv const ks: List\<K>
    priv const vs: List\<V>
    // §4.2.4：宿主 Map 引用与创建时修改计数——仅由 Map.iterate() 经 4 参
    // 构造传入；裸平行 List 旧构造的 host 为 null（借用快照语义，行为同
    // 现状，不做失效/状态检测）
    priv const host: Map\<K, V>?
    priv const stamp: i64
    priv var index: i64

    // 旧构造契约（裸平行 List，AtomicMapSnapshot 复用）：行为保持现状
    pub init(_ -> ks, _ -> vs) {
        index = ((0 as i64) - (1 as i64))
        host = null
        stamp = (0 as i64)
    }

    // Map.iterate() 专用构造：携带宿主与创建时计数
    pub init(_ -> ks, _ -> vs, _ -> host, _ -> stamp) {
        index = ((0 as i64) - (1 as i64))
    }

    // 失效检测（§4.2.4，宿主路径独有）：集合创建枚举器后被修改即抛
    // core.IllegalStateException——失效优先于状态推进与正常结束
    priv func checkValid() {
        if (host == null) {
            return
        }
        if (((host as Map\<K, V>).modCount != stamp)) {
            throw new core.IllegalStateException("Map 在枚举期间被修改")
        }
    }

    pub override func moveNext(): bool {
        checkValid()
        index = (index + (1 as i64))
        return (index < ks.length)
    }

    pub override func current(): core.Pair\<K, V> {
        checkValid()
        if (host != null) {
            // §4.2.4（宿主路径）：未开始（index==-1）或已结束（>=长度）
            // 无当前元素；裸 List 旧契约不经此闸，行为同现状
            if ((index < (0 as i64)) or (index >= ks.length)) {
                throw new core.NoSuchElementException("Map 枚举器无当前元素（未开始或已结束）")
            }
        }
        return new core.Pair\<K, V>((ks.getAtIndex(index) as K), (vs.getAtIndex(index) as V))
    }
}

// 内部可空槽不改变公开 ListEnumerator 的 Array<T> 构造契约。§4.2.4：
// 本枚举器由 List.iterate() 创建，持有宿主 List 引用与创建时修改计数——
// moveNext/current 先校验计数（失效抛 core.IllegalStateException，且失效
// 优先于正常结束），再校验游标状态（未开始/已结束抛
// core.NoSuchElementException）。公开 ListEnumerator（裸 Array 构造的
// 借用适配器，AtomicSnapshot 复用）不做上述检测，行为保持现状。
priv class ListStorageEnumerator\<T> implements IEnumerator\<T> {
    priv const items: Array\<T?>
    priv const count: i32
    priv const host: List\<T>
    priv const stamp: i64
    priv var index: i32

    pub init(_ -> items, _ -> count, _ -> host, _ -> stamp) { index = (0 - 1) }

    pub override func moveNext(): bool {
        // 失效检测优先：集合被修改后即使已到末尾也不得伪装成正常 false
        if (host.modCount != stamp) {
            throw new core.IllegalStateException("List 在枚举期间被修改")
        }
        index = (index + 1)
        return (index < count)
    }

    pub override func current(): T {
        // 先校验失效，再校验状态：index==-1 未开始、>=count 已结束
        if (host.modCount != stamp) {
            throw new core.IllegalStateException("List 在枚举期间被修改")
        }
        if ((index < 0) or (index >= count)) {
            throw new core.NoSuchElementException("List 枚举器无当前元素（未开始或已结束）")
        }
        return (items[index] as T)
    }
}
