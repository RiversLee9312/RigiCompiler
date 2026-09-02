// Rigi 标准库：序列化修饰器（MW11d Phase A）。
// SerializationBase 标 @Internal：仅 core.serialization 内可应用；
// 编译器对内建标量/String/Array 的登记不受此限（Phase A4）。
// Serializable 的 toParcel/fromParcel 暴露面属 Phase B。
// Temporary 是字段 Value wrapper，懒恢复全源码实现，零编译器魔法。
namespace core.serialization

@WrapperTarget(.Entity)
@Internal
pub shared wrapper SerializationBase {
    pub init()
}

/**
 * 可序列化实体修饰器（MW11d-B2）。
 * 暴露面（归属本 wrapper，源码层 toParcel 经编译器改写转发宿主 ..toParcel）：
 *   obj:Serializable.toParcel() → Parcel
 *   fromParcel\<T with Serializable\>(parcel) → T（顶层函数，见文件末）
 *   deepCopy\<T with Serializable\>(value) → T
 * 深复制语义：Parcel 树天然无别名；往返后共享子对象的兄弟字段是两个独立对象。
 * toParcel 快照不别名源图任何可变部分（数组/列表/Map/对象全部新建）。
 */
@WrapperTarget(.Entity)
pub shared wrapper Serializable {
    pub init()
}

@WrapperTarget(.Value)
@Terminal
pub wrapper Temporary\<TField> {
    priv var materialized: bool = false
    priv var cached: TField?
    priv var resumeStub: core.Func\<TField>

    pub init(resume: core.Func\<TField>) {
        resumeStub = resume
        materialized = false
    }

    operator .proxy.get\<TValue>(value: TValue): TValue {
        if (not materialized) {
            cached = resumeStub()
            materialized = true
        }
        return ((cached if? resumeStub()) as TValue)
    }

    operator .proxy.set\<TValue>(value: TValue) {
        cached = (value as TField)
        materialized = true
        inner(value)
    }
}

/**
 * 序列化对象的通用中间表示（MW11d-B1）。
 * getElement：absent key 抛 NoSuchElementException；存入的 null 返回 null。
 * 取回值从 Any 槽 cast 到 T——类型不符抛 CastException（预期）。
 * 嵌套 Parcel 合法：本类标 @SerializationBase，Parcel/Parcel? 可作 element。
 */
@SerializationBase()
pub class Parcel implements core.collections.IEnumerable\<core.Pair\<String, Any>> {
    priv class NullSentinel {
        pub init()
    }

    pub const typeName: String
    priv var data: core.collections.Map\<String, Any>

    pub init(_ -> typeName) {
        data = new core.collections.Map\<String, Any>()
    }

    pub func getElement\<T with SerializationBase>(key: String): T? {
        if (data.containsKey(key)) {
            const boxed = data.tryGet(key)
            if (boxed is NullSentinel) {
                return null
            }
            if (boxed == null) {
                return null
            }
            return (boxed as T)
        } else {
            throw new core.NoSuchElementException("Parcel 中不存在键：${key}")
        }
    }

    pub func setElement\<T with SerializationBase>(key: String, element: T?) {
        if (element == null) {
            data.set(key, (new NullSentinel() as Any))
        } else {
            data.set(key, (element as Any))
        }
    }

    pub func elementCount(): i64 {
        return data.count
    }

    pub func keyAtIndex(index: i64): String? {
        return data.keyAtIndex(index)
    }

    pub func valueAtIndex(index: i64): Any? {
        return data.valueAtIndex(index)
    }

    pub override func iterate(): core.collections.IEnumerator\<core.Pair\<String, Any>> {
        return data.iterate()
    }
}

/**
 * Serializable 暴露面：由 parcel 重建 T。
 * 体由编译器填充（构造通道建 T 实例 + 调 ..fromParcel 覆盖）。
 */
pub func fromParcel\<T with Serializable>(parcel: Parcel): T {
    throw new core.IllegalStateException("fromParcel 应由编译器填充")
}

/**
 * 深复制：toParcel 快照再 fromParcel 重建。标量不走本通道（Entity wrapper 不能挂标量）。
 */
pub func deepCopy\<T with Serializable>(value: T): T {
    return fromParcel\<T>(value:Serializable.toParcel())
}
