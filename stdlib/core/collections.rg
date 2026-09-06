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
// 约束写法与 alloc_array 对齐：不在函数上写 T extends ValueType——
// Span/SharedSpan 定义自身携带该约束，构造点强制。
@NativeLibrary("rigi_rt")
@NativeSymbol("span_alloc")
priv native func span_alloc\<T>(size: i32): Span\<T>

pub func spanOf\<T>(size: i32): Span\<T> {
    return span_alloc\<T>(size)
}

@NativeLibrary("rigi_rt")
@NativeSymbol("span_alloc")
priv native func shared_span_alloc\<T>(size: i32): SharedSpan\<T>

pub func sharedSpanOf\<T>(size: i32): SharedSpan\<T> {
    return shared_span_alloc\<T>(size)
}

// 动态数组使用可空内部槽，删除时清空尾槽以释放引用，扩容倍增。
// getAtIndex 越界读 null（与语言索引协议 §13.2 对齐，方法面非 [] 运算符）；
// removeAt 越界抛 core.OutOfBoundException。
pub class List\<T> implements IEnumerable\<T> {
    priv var items: Array\<T?>
    priv var count: i32

    pub init() {
        items = arrayOf\<T?>(8)
        count = 0
    }

    pub func add(item: T) {
        if (count == items.length) { grow() }
        items[count] = item
        count = (count + 1)
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
    }

    pub override func iterate(): IEnumerator\<T> {
        return new ListStorageEnumerator\<T>(items, count)
    }

    priv func grow() {
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
pub class Map\<K, V> implements IEnumerable\<core.Pair\<K, V>> {
    priv var ks: List\<K>
    priv var vs: List\<V>

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
                return
            }
            i = (i + (1 as i64))
        }
        ks.add(key)
        vs.add(value)
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
                return true
            }
            i = (i + (1 as i64))
        }
        return false
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
        return new MapEnumerator\<K, V>(ks, vs)
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
    priv var index: i64

    pub init(_ -> ks, _ -> vs) {
        index = ((0 as i64) - (1 as i64))
    }

    pub override func moveNext(): bool {
        index = (index + (1 as i64))
        return (index < ks.length)
    }

    pub override func current(): core.Pair\<K, V> {
        return new core.Pair\<K, V>((ks.getAtIndex(index) as K), (vs.getAtIndex(index) as V))
    }
}

// 内部可空槽不改变公开 ListEnumerator 的 Array<T> 构造契约。
priv class ListStorageEnumerator\<T> implements IEnumerator\<T> {
    priv const items: Array\<T?>
    priv const count: i32
    priv var index: i32

    pub init(_ -> items, _ -> count) { index = (0 - 1) }

    pub override func moveNext(): bool {
        index = (index + 1)
        return (index < count)
    }

    pub override func current(): T { return (items[index] as T) }
}
