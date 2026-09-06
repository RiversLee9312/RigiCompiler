// Rigi 标准库：序列化修饰器（MW11d Phase A）。
// SerializationBase 标 @Internal：仅 core.serialization 内可应用；
// 编译器对内建标量/String/Array 的登记不受此限（Phase A4）。
// Serializable 的 toParcel/fromParcel 暴露面属 Phase B。
// Temporary 是字段 Value wrapper，懒恢复全源码实现，零编译器魔法。
namespace core.serialization

// 编译器递归入口专用；顶层 private 限定为本标准库文件可见，用户不能
// 构造或传递此上下文。Place<Object> 统一引用类型身份，不暴露地址或哈希。
priv class SerializationGraphContext {
    pub const enabled: bool
    priv var places: core.collections.List\<core.Place\<Object>>
    priv var decoded: core.collections.List\<Object>

    pub init(_ -> enabled) {
        places = new core.collections.List\<core.Place\<Object>>()
        decoded = new core.collections.List\<Object>()
    }

    // 正数为已存在节点，负数为刚登记节点。树模式只保存活动路径。
    pub func enter(value: Any): i64 {
        // 开放 Serializable 型参也可具化为值类型，值本身不参与身份表。
        if (value is ValueType) { return 0L }
        const reference = value as Object
        const candidate = placeOf reference
        var i: i64 = 0L
        while (i < places.length) {
            if (candidate == (places.getAtIndex(i) as core.Place\<Object>)) {
                candidate.dispose()
                if (not enabled) {
                    throw new core.IllegalStateException("检测到环引用，需显式启用 loopedRefEnabled")
                }
                return i + 1L
            }
            i = i + 1L
        }
        places.add(candidate)
        return 0L - places.length
    }

    pub func leave(id: i64) {
        if ((not enabled) and (id < 0L)) {
            const last = places.length - 1L
            (places.getAtIndex(last) as core.Place\<Object>).dispose()
            places.removeAt(last)
        }
    }

    pub func isNewNode(id: i64): bool { return enabled and (id < 0L) }

    pub func isGraphParcel(parcel: Parcel, marker: String): bool {
        if (not enabled) { return false }
        var i: i64 = 0L
        while (i < parcel.elementCount()) {
            if ((parcel.keyAtIndex(i) as String) == marker) { return true }
            i = i + 1L
        }
        return false
    }

    pub func remember(id: i64, value: Object) {
        if (id != (decoded.length + 1L)) {
            throw new core.IllegalStateException("无效或重复的序列化节点编号")
        }
        decoded.add(value)
    }

    pub func resolve(id: i64): Object {
        if ((id < 1L) or (id > decoded.length)) {
            throw new core.IllegalStateException("序列化引用指向不存在的节点")
        }
        return decoded.getAtIndex(id - 1L) as Object
    }

    pub func referenceId(id: i64): i64 {
        if (id < 0L) { throw new core.IllegalStateException("无效的序列化引用编号") }
        return id
    }

    pub func dispose() {
        var i: i64 = 0L
        while (i < places.length) {
            (places.getAtIndex(i) as core.Place\<Object>).dispose()
            i = i + 1L
        }
    }
}

@WrapperTarget(.Entity)
@Internal
pub shared wrapper SerializationBase {
    pub init()
}

/**
 * 可序列化实体修饰器（MW11d-B2）。
 * 暴露面（归属本 wrapper，源码层 toParcel 经编译器改写转发宿主 ..toParcel）：
 *   obj:Serializable.toParcel(loopedRefEnabled: bool = false) → Parcel
 *   fromParcel\<T with Serializable\>(parcel, loopedRefEnabled: bool = false) → T
 *   deepCopy\<T with Serializable\>(value, loopedRefEnabled: bool = false) → T
 *   obj:Serializable.deepCopy(loopedRefEnabled: bool = false) → 宿主类型
 * 默认树模式将重复引用分别复制，活动路径遇环抛 IllegalStateException。
 * true 图模式通过节点/引用记录保留复制图内部的别名和环，值类型不登记身份。
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
pub func fromParcel\<T with Serializable>(parcel: Parcel, loopedRefEnabled: bool = false): T {
    throw new core.IllegalStateException("fromParcel 应由编译器填充")
}

/**
 * 深复制：toParcel 快照再 fromParcel 重建。标量不走本通道（Entity wrapper 不能挂标量）。
 */
pub func deepCopy\<T with Serializable>(value: T, loopedRefEnabled: bool = false): T {
    return fromParcel\<T>(value:Serializable.toParcel(loopedRefEnabled), loopedRefEnabled)
}
