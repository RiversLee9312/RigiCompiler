// Rigi 标准库：core.collections 的 Set<T>（STDLIB §4.2.2 Set 条目 /
// §4.2.4 遍历修改检测 / §4.2.5 标准序列化行为）。
// 首版按线性查找实现：内部直接组合 List<T>，判等严格沿用既有 ==（List
// 的 equals-or-hash 判等链），不宣称哈希表复杂度（§4.2.2：未来换哈希桶
// 也必须先证明不改变既有命中语义）。保持插入顺序：iterate 按插入序，
// 删除后重新添加排在末尾（remove 真删、add 尾追的自然结果）。
// §4.2.5：Set 不属于 SB，也不直接提供 Serializable——本类**不**应用
// @SerializationBase/@Serializable；序列化由调用者显式转换为 Array/List。
namespace core.collections

// Set<T>（§4.2.2）：add/remove 返回是否实际增删；重复添加或删除不存在的
// 元素不改变集合。class 形态（SYNTAX.md §10：struct 不得实现接口）。
pub class Set\<T> implements IEnumerable\<T> {
    // 内部表示：插入序 List<T>。判等通道全部委托 List（contains/remove
    // 走其 slotEquals：== 即 equals-or-hash 判等链，绝不涉 toString）。
    // NaN 不等于自身是 == 的自然结果：同一 Set 可出现多个 NaN，按 NaN
    // 值查找不匹配；±0 按既有数值相等规则判等。不做任何特判。
    priv var items: List\<T>
    // §4.2.4 修改计数：成功的 add（实际新增）、remove（命中删除）、
    // clear（含空表 clear，成功即计）各 +1；重复 add、未命中 remove 不计。
    // 元素对象内部字段变化不属于容器修改，不承诺检测（容器只存引用，
    // 元素自身变化不经过容器 API）。（internal：仅本模块 SetEnumerator
    // 读取做失效检测，不对模块外承诺；溢出不设防，实用即可）
    internal var modCount: i64 = (0 as i64)

    pub init() {
        items = new List\<T>()
    }

    // 实际新增返回 true；集合已含相等元素时返回 false 且不修改、不计修改
    pub func add(item: T): bool {
        if (items.contains(item)) {
            return false
        }
        items.add(item)
        // §4.2.4：新增元素计修改
        modCount = (modCount + (1 as i64))
        return true
    }

    // 命中删除返回 true（删除后重新添加排在末尾——真删 + 尾追）；不存在
    // 返回 false，不修改、不计修改
    pub func remove(item: T): bool {
        if (items.remove(item)) {
            // §4.2.4：命中删除计修改（未命中走下方 return false，不计）
            modCount = (modCount + (1 as i64))
            return true
        }
        return false
    }

    // 是否含相等元素——判等通道见 items.contains（== 即 equals-or-hash 链）
    pub func contains(item: T): bool {
        return items.contains(item)
    }

    // §4.2.2：清空全部元素——及时释放不再持有的引用（§4.2.1，List.clear
    // 界内槽位逐一置 null）；容量保留不缩水，count 回零，容器可继续使用。
    // §4.2.4：成功 clear 一律计修改（空表 clear 亦然）
    pub func clear() {
        items.clear()
        modCount = (modCount + (1 as i64))
    }

    // §4.2.1/§4.2.2：元素数（i64 只读属性，与 List.length/Map.count 同宽）
    pub var count: i64 {
        pub get(_: _) { return items.length }
    }

    pub override func iterate(): IEnumerator\<T> {
        // §4.2.4：传入宿主引用与当前修改计数——枚举器此后每次
        // moveNext/current 先校验计数，不一致即失效
        return new SetEnumerator\<T>(items, this, modCount)
    }
}

// §4.2.4：本枚举器由 Set.iterate() 创建，持有宿主 Set 引用与创建时修改
// 计数——moveNext/current 先校验计数（失效抛 core.IllegalStateException，
// 且失效优先于正常结束），再校验游标状态（未开始/已结束抛
// core.NoSuchElementException；正常结束后的 moveNext 持续返回 false）。
// 每次 iterate() 产生独立枚举器（双接口可重入语义，SYNTAX.md §7.3）。
priv class SetEnumerator\<T> implements IEnumerator\<T> {
    priv const items: List\<T>
    priv const host: Set\<T>
    priv const stamp: i64
    priv var index: i64

    pub init(_ -> items, _ -> host, _ -> stamp) {
        index = ((0 as i64) - (1 as i64))
    }

    // 失效检测（§4.2.4）：集合创建枚举器后被修改即抛
    // core.IllegalStateException——失效优先于状态推进与正常结束
    priv func checkValid() {
        if (host.modCount != stamp) {
            throw new core.IllegalStateException("Set 在枚举期间被修改")
        }
    }

    pub override func moveNext(): bool {
        checkValid()
        index = (index + (1 as i64))
        return (index < items.length)
    }

    pub override func current(): T {
        // 先校验失效，再校验状态：index==-1 未开始、>=count 已结束
        checkValid()
        if ((index < (0 as i64)) or (index >= items.length)) {
            throw new core.NoSuchElementException("Set 枚举器无当前元素（未开始或已结束）")
        }
        // List 槽非 null 除非 T 实例化为可空类型（null 元素是合法值）；
        // 泛型参数的 Nullable<T> 不参与 smart cast 收窄（§3.5/S9a），as 恒必要
        return (items.getAtIndex(index) as T)
    }
}
