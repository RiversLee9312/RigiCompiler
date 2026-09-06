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
        return handle_make\<Handle\<T>>((target as Any), targetKind, false) as Handle\<T>
    }
}

// 这两个声明仅保留源码类型与 unsafe 检查；编译器将成员调用投影到下列
// 私有泛型函数，运行时统一为无泛型、无用户字段的 .handle。
pub unsafe shared class Handle\<T> {
    priv init() {}
    pub func load(): T { throw new RuntimeException("Handle 编译器投影缺失") }
    pub func asMutable(): MutableHandle\<T> { throw new RuntimeException("Handle 编译器投影缺失") }
}

pub unsafe shared class MutableHandle\<T> {
    priv init() {}
    pub func load(): T { throw new RuntimeException("Handle 编译器投影缺失") }
    pub func asMutable(): MutableHandle\<T> { throw new RuntimeException("Handle 编译器投影缺失") }
    pub func store(value: T) { throw new RuntimeException("Handle 编译器投影缺失") }
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
        return handle_make\<Handle\<T>>(handle_target(capability), 1, true) as MutableHandle\<T>
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
