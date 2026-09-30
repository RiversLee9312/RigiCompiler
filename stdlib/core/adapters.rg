// Rigi 标准库：core.collections 的 Array/Span 借用适配器（STDLIB §4.2.1
// 第二段 / §4.2.4 末段）。
//
// 契约要点：
// - 适配器**借用**原存储：枚举器经持有的 Array/Span 引用直读底层当前
//   内容，不是快照；遍历期间经任意别名写入该存储属于不支持的用法，
//   首版不承诺检测（§4.2.4 末段）——因此适配器**不加修改计数**，枚举器
//   也不做失效检测（与 List/Map/Set/Queue 的宿主计数失效检测相区分）。
// - 每次 iterate() 产生独立枚举器（各自独立游标，双接口可重入语义，
//   SYNTAX.md §7.3），不把适配器隐式变为快照（§4.2.1）。
// - Span 元素仍须满足既有 ValueType 约束（§4.2.1）：适配器与枚举器的
//   泛型参数均带 `T extends ValueType`。
// - 不应用 @SerializationBase/@Serializable（适配器是借用视图而非容器，
//   无序列化语义）。
namespace core.collections

// Array 借用适配器：把 Array<T> 接入 IEnumerable<T>（§4.2.1）。返回的
// 可枚举体仅持有原数组引用；每次 iterate() 复用 pub ListEnumerator<T> 的
// 裸 Array 构造契约（AtomicSnapshot 复用路径，行为保持现状）——该枚举器
// 直读数组当前内容且不做任何状态/失效检测，正是借用语义的既有形态
//（§4.2.4 末段：不加修改计数）。
pub func asEnumerable\<T>(array: Array\<T>): IEnumerable\<T> {
    return new ArrayEnumerable\<T>(array)
}

// Span 借用适配器：把 Span<T> 接入 IEnumerable<T>（§4.2.1；Span 元素
// 仍须满足既有 ValueType 约束）。每次 iterate() 产生独立 SpanEnumerator。
pub func asEnumerable\<T extends ValueType>(span: Span\<T>): IEnumerable\<T> {
    return new SpanEnumerable\<T>(span)
}

// Array 借用适配的可枚举体：仅持有原数组引用（借用，不复制）。类头无
// 序列化注解（§4.2.4 末段：借用视图不是 SB 容器）
priv class ArrayEnumerable\<T> implements IEnumerable\<T> {
    priv const storage: Array\<T>

    pub init(_ -> storage) {}

    pub override func iterate(): IEnumerator\<T> {
        // 每次 iterate 产生独立枚举器（§4.2.1）；复用 ListEnumerator 裸
        // Array 构造契约——直读当前内容、无失效/状态检测（借用语义，
        // §4.2.4 末段不加修改计数）
        return new ListEnumerator\<T>(storage, storage.length)
    }
}

// Span 借用适配的可枚举体：仅持有原 Span 引用（借用，不复制；Span 本身
// 是引用语义——经别名写入对枚举器可见）。类头无序列化注解
priv class SpanEnumerable\<T extends ValueType> implements IEnumerable\<T> {
    priv const storage: Span\<T>

    pub init(_ -> storage) {}

    pub override func iterate(): IEnumerator\<T> {
        // 每次 iterate 产生独立枚举器（§4.2.1）
        return new SpanEnumerator\<T>(storage)
    }
}

// Span 借用枚举器：对齐 §4.2.4 的状态机但不接失效检测（借用适配器不加
// 修改计数，§4.2.4 末段）——
// - 未开始（index==-1）或正常结束（index>=length）后 current 抛
//   core.NoSuchElementException；首次 moveNext 前 current 抛错不改游标，
//   随后的 moveNext 照常开始枚举；
// - 正常结束后的 moveNext 持续返回 false；
// - 读取经 getAtIndex 直达底层当前内容（借用，非快照）；游标状态闸门
//   保证读取时 index 恒在界内，as 解包不会命中越界 null。
priv class SpanEnumerator\<T extends ValueType> implements IEnumerator\<T> {
    priv const storage: Span\<T>
    // 游标：-1 未开始，0..length-1 有效，>= length 已结束
    priv var index: i32

    pub init(_ -> storage) {
        index = (0 - 1)
    }

    pub override func moveNext(): bool {
        index = (index + 1)
        return (index < storage.length)
    }

    pub override func current(): T {
        // 仅状态闸门（无失效检测）：index==-1 未开始、>=length 已结束
        if ((index < 0) or (index >= storage.length)) {
            throw new core.NoSuchElementException("Span 枚举器无当前元素（未开始或已结束）")
        }
        // 槽非 null（状态闸门保证界内；Span 元素为 ValueType，null 只在
        // 越界读出现）；泛型参数的 Nullable<T> 不参与 smart cast 收窄
        //（§3.5/S9a），as 恒必要
        return (storage[index] as T)
    }
}
