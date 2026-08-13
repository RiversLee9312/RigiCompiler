// Rigi 标准库：core.collections 迭代协议（SYNTAX.md §7.3/§15.3）
// 与 Array\<T\> 构造入口（RUNTIME.md §26）。
// IEnumerable\<T\>/IEnumerator\<T\> 是 C# 风格双接口（可重入，每次
// iterate() 产生独立枚举器）。RangeEnumerator\<T\> 是范围循环枚举器的
// 泛型抽象基类（S9f）：共享状态机骨架（value_/end_/started_ 字段与
// current() 实现），比较/步进逻辑按具体类型实现（moveNext 抽象——
// 运算指令由各具体类型在自身类型上下文中书写，Middleware 按 typeid
// 选择精确实现；BIL §11.1 运算键与 source-level intrinsic 无关）。
// RangeEnumeratorI32 是 i32 范围循环（半开区间 [start, end)、步长恒
// +1，SYNTAX.md §7.3）的具体实现——class 形态（SYNTAX.md §10：
// struct 不得实现接口）。
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

// Array\<T\> 合法构造入口（RUNTIME.md §26 / BIL_STANDARD.md §22.5）：
// 用户代码只走 arrayOf / arrayOfElements；alloc_array 是私有 native，
// 经泛型 hidden .generic.T 物化 typeid，VM hook 分配零值数组。
// arrayOfElements 体内视角 elements 已是 Array\<T\>（M78）。
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
        result[i] = elements[i]
        i = i + 1
    }
    return result
}
