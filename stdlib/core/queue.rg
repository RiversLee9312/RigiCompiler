// Rigi 标准库：core.collections 的 FIFO Queue<T>（STDLIB §4.2.1/§4.2.2
// Queue 条目 / §4.2.4 遍历修改检测 / §4.2.5 标准序列化行为）。
// 内部采用可扩容环形缓冲区（§4.2.2 契约明确要求）：head 指向队首槽位，
// count_ 为元素数，队尾物理槽 = (head + count_) 对容量取模——出队只前移
// head，入队只占用尾部空槽，均不搬移既有元素。出队槽位及时置空释放
// 引用（§4.2.1）；扩容倍增并按 FIFO 逻辑序重排到 0..count_，容量运算
// 检查溢出（i32 存储上限与 List.grow 同款，触顶抛 core.OutOfBoundException，
// 不窄化不回绕）。
// 元素可为 null（T 实例化为可空类型时）：dequeue/peek 正常返回 null 值，
// 与「空队抛 core.NoSuchElementException」区分；tryDequeue/tryPeek 返回
// core.Pair\<bool, T?>，首项表成功（成功值可以是 null），失败为 (false, null)。
// §4.2.5：Queue 不属于 SB，也不直接提供 Serializable——本类**不**应用
// @SerializationBase/@Serializable；序列化由调用者显式转换为 Array/List
//（保持出队顺序）。注意与 core.messaging 内部 priv MessageQueue 无关。
namespace core.collections

// Queue<T>（§4.2.2）：按 FIFO 出队和遍历。class 形态（SYNTAX.md §10：
// struct 不得实现接口）。普通集合为 local（不加 shared），不支持同实例并发。
pub class Queue\<T> implements IEnumerable\<T> {
    // 内部表示：可空槽环形缓冲区（槽可空仅在 T 实例化为可空类型时承载
    // null 元素；其余时候可空槽只为「出队后及时置空释放引用」服务）。
    priv var items: Array\<T?>
    // 队首物理下标（出队后 +1 并对容量回绕）；扩容后复位 0
    priv var head: i32
    // 元素数（i32 存储宽度；公开 count 为 i64，见 §4.2.1 宽度约定）。
    // 字段名带下划线后缀，避免与同名只读属性 count 冲突
    priv var count_: i32
    // §4.2.4 修改计数：成功的 enqueue、dequeue（含 tryDequeue 成功路径）、
    // clear（含空队 clear，成功即计）各 +1；空队 dequeue/peek 抛异常属
    // 失败操作不计；peek/tryPeek（含 tryPeek 失败）不计修改。元素对象
    // 内部字段变化不属于容器修改，不承诺检测。（internal：仅本模块
    // QueueEnumerator 读取做失效检测，不对模块外承诺；溢出不设防，实用即可）
    internal var modCount: i64 = (0 as i64)

    pub init() {
        items = arrayOf\<T?>(8)
        head = 0
        count_ = 0
    }

    // 入队（队尾追加）：容量不足时扩容；成功即计修改（§4.2.4）
    pub func enqueue(item: T) {
        if (count_ == items.length) { grow() }
        // 队尾物理槽 = (head + count_) 对容量取模（契约要求环形复用出队
        // 腾出的槽位；容量经 grow 保证 > count_，槽位必不冲突）
        var slot = (head + count_)
        if (slot >= items.length) { slot = (slot - items.length) }
        items[slot] = item
        count_ = (count_ + 1)
        // §4.2.4：入队计修改
        modCount = (modCount + (1 as i64))
    }

    // FIFO 出队：移除并返回队首元素；空队抛 core.NoSuchElementException
    //（属失败操作，不计修改，容器结构不变）。出队槽位及时置空释放引用
    //（§4.2.1；T 实例化为可空类型时 null 元素与置空槽同形，均不持有引用）
    pub func dequeue(): T {
        if (count_ == 0) {
            throw new core.NoSuchElementException("Queue 为空，无法 dequeue")
        }
        const value = items[head]
        items[head] = null
        head = (head + 1)
        if (head == items.length) { head = 0 }
        count_ = (count_ - 1)
        // §4.2.4：出队计修改
        modCount = (modCount + (1 as i64))
        // 槽非 null 除非 T 实例化为可空类型（null 元素是合法值）；泛型参数
        // 的 Nullable<T> 不参与 smart cast 收窄（§3.5/S9a），as 恒必要
        return (value as T)
    }

    // 看队首不出队；空队抛 core.NoSuchElementException。不修改容器
    pub func peek(): T {
        if (count_ == 0) {
            throw new core.NoSuchElementException("Queue 为空，无法 peek")
        }
        return (items[head] as T)
    }

    // 尝试出队：首项表成功（成功走 dequeue 完整路径，计修改）；成功值可以
    // 是 null（T 实例化为可空类型时存了 null），失败为 Pair(false, null)——
    // 二者由首项 bool 区分
    pub func tryDequeue(): core.Pair\<bool, T?> {
        if (count_ == 0) {
            return new core.Pair\<bool, T?>(false, null)
        }
        // T → T? 恒可赋值（§3.4）：dequeue 的 T 结果经可空视图装入 Pair
        const value: T? = dequeue()
        return new core.Pair\<bool, T?>(true, value)
    }

    // 尝试看队首：不修改容器、不计修改；语义与 tryDequeue 同构
    pub func tryPeek(): core.Pair\<bool, T?> {
        if (count_ == 0) {
            return new core.Pair\<bool, T?>(false, null)
        }
        return new core.Pair\<bool, T?>(true, items[head])
    }

    // §4.2.2：清空全部元素——及时释放不再持有的引用（§4.2.1，界内槽位
    // 逐一置 null）；容量保留不缩水，head 复位 0，count 回零，容器可继续
    // 使用。§4.2.4：成功 clear 一律计修改（空队 clear 亦然）
    pub func clear() {
        var i: i32 = 0
        while (i < count_) {
            var slot = (head + i)
            if (slot >= items.length) { slot = (slot - items.length) }
            items[slot] = null
            i = (i + 1)
        }
        head = 0
        count_ = 0
        // §4.2.4：成功 clear 计修改
        modCount = (modCount + (1 as i64))
    }

    // §4.2.1/§4.2.2：元素数（i64 只读属性，与 List.length/Map.count/Set.count
    // 同宽；不承诺底层容量超过 i32 存储限制）
    pub var count: i64 {
        pub get(_: _) { return (count_ as i64) }
    }

    pub override func iterate(): IEnumerator\<T> {
        // §4.2.4：传入宿主引用与当前修改计数——枚举器此后每次
        // moveNext/current 先校验计数，不一致即失效
        return new QueueEnumerator\<T>(items, head, count_, this, modCount)
    }

    // 扩容（容量不足时倍增）：按 FIFO 逻辑序把 head..tail 搬到新数组
    // 0..count_（扩容保留已有元素顺序），head 复位 0。容量运算检查溢出：
    // 倍增越 i32 可表示范围即抛 core.OutOfBoundException（上限与 List.grow
    // 同款 1073741823，不窄化不回绕）；失败时旧 items 原样保留，容器结构
    // 仍有效（§4.2.1）。槽间搬运按 T? 直赋，无需解包（T 实例化为可空类型
    // 时的 null 元素原样平移）
    priv func grow() {
        if (items.length > 1073741823) {
            throw new core.OutOfBoundException(
                "Queue 容量超过 i32 可表示范围")
        }
        const bigger = arrayOf\<T?>((items.length * 2))
        var i: i32 = 0
        while (i < count_) {
            var slot = (head + i)
            if (slot >= items.length) { slot = (slot - items.length) }
            bigger[i] = items[slot]
            i = (i + 1)
        }
        items = bigger
        head = 0
    }
}

// §4.2.4：本枚举器由 Queue.iterate() 创建，持有宿主 Queue 引用与创建时
// 修改计数——moveNext/current 先校验计数（失效抛 core.IllegalStateException，
// 且失效优先于正常结束），再校验游标状态（未开始/已结束抛
// core.NoSuchElementException；正常结束后的 moveNext 持续返回 false）。
// 遍历按 FIFO 出队顺序（队首→队尾）：逻辑序 index 经环形映射到物理槽
// (head + index) 对容量取模。捕获的 items/head/count 快照只在计数一致时
// 读取——任何增删/clear（含扩容）都先使计数失配而失效，快照不会失步。
// 每次 iterate() 产生独立枚举器（双接口可重入语义，SYNTAX.md §7.3）。
priv class QueueEnumerator\<T> implements IEnumerator\<T> {
    priv const items: Array\<T?>
    priv const head: i32
    priv const count: i32
    priv const host: Queue\<T>
    priv const stamp: i64
    // 已消费元素数语义的游标：-1 未开始，0..count-1 有效，>= count 已结束
    priv var index: i32

    pub init(_ -> items, _ -> head, _ -> count, _ -> host, _ -> stamp) {
        index = (0 - 1)
    }

    // 失效检测（§4.2.4）：队列创建枚举器后被修改即抛
    // core.IllegalStateException——失效优先于状态推进与正常结束
    priv func checkValid() {
        if (host.modCount != stamp) {
            throw new core.IllegalStateException("Queue 在枚举期间被修改")
        }
    }

    pub override func moveNext(): bool {
        checkValid()
        index = (index + 1)
        return (index < count)
    }

    pub override func current(): T {
        // 先校验失效，再校验状态：index==-1 未开始、>=count 已结束
        checkValid()
        if ((index < 0) or (index >= count)) {
            throw new core.NoSuchElementException("Queue 枚举器无当前元素（未开始或已结束）")
        }
        // FIFO 逻辑序 → 环形物理槽映射（head + index 对容量取模）
        var slot = (head + index)
        if (slot >= items.length) { slot = (slot - items.length) }
        // 槽非 null 除非 T 实例化为可空类型（null 元素是合法值）；泛型参数
        // 的 Nullable<T> 不参与 smart cast 收窄（§3.5/S9a），as 恒必要
        return (items[slot] as T)
    }
}
