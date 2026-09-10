namespace core

// Place 只保留目标身份。普通字段 ARC 同时覆盖遗漏 dispose 时的析构，
// dispose 清空字段可提前释放；不会代替用户调用 dispose。
pub class Place\<T> implements IDisposable {
    priv var target: Any?
    priv var targetKind: i32 = 0

    // 只由 placeOf 的降级路径调用，用户不能把临时值伪装成稳定存储。
    priv init(value: Any, kind: i32) {
        target = value
        targetKind = kind
    }

    // 泛型/接口静态类型可能装对象或值：对象仍按自身身份，值使用原存储 Cell。
    priv init(storage: Any, value: Any, kind: i32) {
        // null 没有对象身份；泛型 nullable 的空值仍由原稳定 Cell 保存。
        if ((value == null) or (value is ValueType)) { target = storage
            targetKind = kind }
        else { target = value }
    }

    pub operator equals(other: Place\<T>): bool {
        if ((target == null) or (other.target == null)) { return false }
        return place_same_target((target as Any), (other.target as Any))
    }

    pub override func dispose() {
        target = null
    }

    pub unsafe func expose(): Handle\<T> {
        const storage = handle_make\<ObjectHandleStorage>((target as Any), targetKind, false) as ObjectHandleStorage
        return new Handle\<T>(storage)
    }
}

// 只有内部存储使用固定 ABI；公开 Handle 的类型身份与泛型实参完整保留。
// 这是 Rigi 对象的共享持有，不是 NativeRcHandle，也不使用弱 Carrige。
priv unsafe shared class ObjectHandleStorage {
    priv init() {}
}

pub unsafe shared class Handle\<T> {
    priv const capability: ObjectHandleStorage
    protected init(capability: Any) { this.capability = capability as ObjectHandleStorage }
    pub func load(): T { unsafe seq { return handle_load\<T>(capability) } }
    pub func asMutable(): MutableHandle\<T> { unsafe seq { return handle_asMutable\<T>(capability) } }
}

pub unsafe shared class MutableHandle\<T> {
    priv const capability: ObjectHandleStorage
    protected init(capability: Any) { this.capability = capability as ObjectHandleStorage }
    pub func load(): T { unsafe seq { return handle_load\<T>(capability) } }
    pub func asMutable(): MutableHandle\<T> { unsafe seq { return handle_asMutable\<T>(capability) } }
    pub func store(value: T) { unsafe seq { handle_store\<T>(capability, value) } }
}

priv unsafe func handle_load\<T>(capability: Any): T {
    const target = handle_target(capability)
    const kind = handle_kind(capability)
    if (kind == 1) { return (target as Cell\<T>).getValue() }
    if (kind == 2) { return (target as ReadonlyCell\<T>).getValue() }
    return target as T
}

priv unsafe func handle_asMutable\<T>(capability: Any): MutableHandle\<T> {
    if ((handle_kind(capability) == 1) and handle_type_is_value\<T>()) {
        const storage = handle_make\<ObjectHandleStorage>(handle_target(capability), 1, true) as ObjectHandleStorage
        return new MutableHandle\<T>(storage)
    }
    throw new ImmutablePlaceException()
}

priv unsafe func handle_store\<T>(capability: Any, value: T) {
    if (handle_is_mutable(capability) and handle_type_is_value\<T>()) {
        const target = handle_target(capability)
        if (target is Cell\<T>) {
            (target as Cell\<T>).setValue(value)
            return
        }
    }
    throw new ImmutablePlaceException()
}

@NativeLibrary("rigi_rt")
@NativeSymbol("handle_make")
priv unsafe native func handle_make\<THandle>(target: Any, kind: i32, mutable: bool): Any

@NativeLibrary("rigi_rt")
@NativeSymbol("handle_target")
priv unsafe native func handle_target(capability: Any): Any

@NativeLibrary("rigi_rt")
@NativeSymbol("handle_is_mutable")
priv unsafe native func handle_is_mutable(capability: Any): bool

@NativeLibrary("rigi_rt")
@NativeSymbol("handle_kind")
priv unsafe native func handle_kind(capability: Any): i32

@NativeLibrary("rigi_rt")
@NativeSymbol("handle_type_is_value")
priv unsafe native func handle_type_is_value\<T>(): bool

// 只返回引用身份是否相同。不提供地址；整数身份原语仍不提供（对象身份
// 的可观测通道是 Any.hash 的默认实现——payload 身份哈希，Map 键判等）。
@NativeLibrary("rigi_rt")
@NativeSymbol("place_same_target")
priv native func place_same_target(left: Any, right: Any): bool
