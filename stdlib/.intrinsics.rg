// 内建声明面。编译器只按名称连接固定 ABI 和指令；成员与能力由本源码声明。
namespace core

pub interface Any {
    pub func toString(): String { return any_to_string(this) }
    pub func hash(): i64 { return any_hash(this) }
    pub operator equals(other: Any): bool { return this.hash() == other.hash() }
    pub func call???(symbol: String, namedArgs: Array\<Pair\<String, Any>>, unnamedArgs: Array\<Any>): Any {
        throw new NoSuchMethodException()
    }
}
pub open class Object {
    pub open override func toString(): String { return any_to_string(this) }
    pub open override func hash(): i64 { return any_hash(this) }
    pub operator equals(other: Any): bool { return this.hash() == other.hash() }
}
pub struct ValueType {}
pub enum struct Enum {}[]
pub wrapper Wrapper {}

@SerializationBase
@core.serialization.Serializable
pub struct i8 {}
@SerializationBase
@core.serialization.Serializable
pub struct i16 {}
@SerializationBase
@core.serialization.Serializable
pub struct i32 {}
@SerializationBase
@core.serialization.Serializable
pub struct i64 {}
@SerializationBase
@core.serialization.Serializable
pub struct u8 {}
@SerializationBase
@core.serialization.Serializable
pub struct u16 {}
@SerializationBase
@core.serialization.Serializable
pub struct u32 {}
@SerializationBase
@core.serialization.Serializable
pub struct u64 {}
@SerializationBase
@core.serialization.Serializable
pub struct float {}
@SerializationBase
@core.serialization.Serializable
pub struct double {}
@SerializationBase
@core.serialization.Serializable
pub struct bool {}
@SerializationBase
@core.serialization.Serializable
pub struct char {}
@SerializationBase
@core.serialization.Serializable
pub struct String {}

pub struct Type\<T> {}
pub class Span\<T extends ValueType> {
    pub const length: i32
    pub operator getAtIndex(index: i32): T? { return this[index] }
    pub operator setAtIndex(index: i32, element: T) { this[index] = element }
}
pub shared class SharedSpan\<T extends ValueType> {
    pub const length: i32
    pub operator getAtIndex(index: i32): T? { return this[index] }
    pub operator setAtIndex(index: i32, element: T) { this[index] = element }
}
pub class Nullable\<T> {}
pub class Box\<T extends ValueType> {}
@SerializationBase
@core.serialization.Serializable
pub class Array\<T> {
    pub const length: i32
    pub operator getAtIndex(index: i32): T? { return this[index] }
    pub operator setAtIndex(index: i32, element: T) { this[index] = element }
}
pub class Map\<TKey, TValue> {}

pub open class Pair\<TKey, TValue> {
    pub const key: TKey
    pub const value: TValue
    pub init(_ -> key, _ -> value) {}
    internal static func convertArgument\<TFrom, TTo>(source: Pair\<String, TFrom>?): Pair\<String, TTo>? {
        if (source == null) { return null }
        const pair = source as Pair\<String, TFrom>
        return new Pair\<String, TTo>(pair.key, pair.value as TTo)
    }
}

@NativeLibrary("rigi_rt")
@NativeSymbol("any_hash")
priv native func any_hash(value: Any): i64
@NativeLibrary("rigi_rt")
@NativeSymbol("any_to_string")
priv native func any_to_string(value: Any): String
