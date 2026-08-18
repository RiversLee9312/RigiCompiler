// 基元类型自举辅助成员（SYNTAX §15.3）：内建数值类型无法在自己的
// 声明处携带这些实现，经 ext 以 Rigi 自举（§13.2 枚举运算符）。
// this 即区间起点（start），end 为终点（不含）——半开区间 [this, end)。
// core.Pair 同批自举（M52，SYNTAX §18）：解构声明 var (a, b) = pair
// 要求 pair 类型是 core.Pair\<TKey, TValue\> 的子类——P3 按 canonical
// 名硬编码参照（同 M48 core.collections 协议先例），字段读取即解构产物。
namespace core

pub ext operator i32.EnumerateInRange(end: i32): core.collections.IEnumerable\<i32> {
    return new core.collections.RangeI32(this, end)
}

pub open class Pair\<TKey, TValue> {
    pub const key: TKey
    pub const value: TValue
    // S9d：显式 init（§9.3）——kwargs 打包的 .pair<.string, .any> 特权
    // 构造经此匹配（§14.1 严格匹配唯一 init）
    pub init(_ -> key, _ -> value) { }
}

// lambda 对象模型基类与捕获 Cell（SYNTAX §5.2）：每个 lambda 编译期
// 生成隐藏类，继承下列四家族之一；捕获经 Cell/ReadonlyCell 传入。
// 各家族预生成 0–32 参数元数变种（共 132 个 abstract 声明）。
// Async* 为 shared（async 边界共享安全闸门）；Func/Action/Cell 非 shared
// （Cell 的 shared 安全性由编译器 passthrough 特殊判定）。

pub abstract class Func\<TRet> {
    pub abstract operator call(): TRet
}

pub abstract class Func\<TRet, T0> {
    pub abstract operator call(arg0: T0): TRet
}

pub abstract class Func\<TRet, T0, T1> {
    pub abstract operator call(arg0: T0, arg1: T1): TRet
}

pub abstract class Func\<TRet, T0, T1, T2> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27, arg28: T28): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28, T29> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27, arg28: T28, arg29: T29): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28, T29, T30> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27, arg28: T28, arg29: T29, arg30: T30): TRet
}

pub abstract class Func\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28, T29, T30, T31> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27, arg28: T28, arg29: T29, arg30: T30, arg31: T31): TRet
}

pub abstract class Action {
    pub abstract operator call()
}

pub abstract class Action\<T0> {
    pub abstract operator call(arg0: T0)
}

pub abstract class Action\<T0, T1> {
    pub abstract operator call(arg0: T0, arg1: T1)
}

pub abstract class Action\<T0, T1, T2> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2)
}

pub abstract class Action\<T0, T1, T2, T3> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3)
}

pub abstract class Action\<T0, T1, T2, T3, T4> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27, arg28: T28)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28, T29> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27, arg28: T28, arg29: T29)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28, T29, T30> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27, arg28: T28, arg29: T29, arg30: T30)
}

pub abstract class Action\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28, T29, T30, T31> {
    pub abstract operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27, arg28: T28, arg29: T29, arg30: T30, arg31: T31)
}

pub shared abstract class AsyncFunc\<TRet> {
    pub abstract async operator call(): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0> {
    pub abstract async operator call(arg0: T0): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1> {
    pub abstract async operator call(arg0: T0, arg1: T1): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27, arg28: T28): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28, T29> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27, arg28: T28, arg29: T29): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28, T29, T30> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27, arg28: T28, arg29: T29, arg30: T30): TRet
}

pub shared abstract class AsyncFunc\<TRet, T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28, T29, T30, T31> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27, arg28: T28, arg29: T29, arg30: T30, arg31: T31): TRet
}

pub shared abstract class AsyncAction {
    pub abstract async operator call()
}

pub shared abstract class AsyncAction\<T0> {
    pub abstract async operator call(arg0: T0)
}

pub shared abstract class AsyncAction\<T0, T1> {
    pub abstract async operator call(arg0: T0, arg1: T1)
}

pub shared abstract class AsyncAction\<T0, T1, T2> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27, arg28: T28)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28, T29> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27, arg28: T28, arg29: T29)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28, T29, T30> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27, arg28: T28, arg29: T29, arg30: T30)
}

pub shared abstract class AsyncAction\<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, T21, T22, T23, T24, T25, T26, T27, T28, T29, T30, T31> {
    pub abstract async operator call(arg0: T0, arg1: T1, arg2: T2, arg3: T3, arg4: T4, arg5: T5, arg6: T6, arg7: T7, arg8: T8, arg9: T9, arg10: T10, arg11: T11, arg12: T12, arg13: T13, arg14: T14, arg15: T15, arg16: T16, arg17: T17, arg18: T18, arg19: T19, arg20: T20, arg21: T21, arg22: T22, arg23: T23, arg24: T24, arg25: T25, arg26: T26, arg27: T27, arg28: T28, arg29: T29, arg30: T30, arg31: T31)
}

// 捕获单元：lambda 捕获的外层变量与被 wrapper 修饰的值统一经 Cell 盛装
//（§5.2/§14.3）。var → Cell\<T\>；const → ReadonlyCell\<T\>；this 不套 Cell。
// 基类恒为抽象——不定义 value 存储；每个 cell 化变量由编译器逐变量合成
// 隐藏子类（..cell..UUID），子类自行声明 pub value 字段（wrapper 应用经
// 该字段的 wrapped(W) 标记承载）并 override getValue/setValue，Middleware
// 据「继承 Cell 族 + 字段 wrapper 标记」识别烘焙与优化对象。
pub abstract class Cell\<T> {
    pub abstract func getValue(): T
    pub abstract func setValue(v: T)
}

pub abstract class ReadonlyCell\<T> {
    pub abstract func getValue(): T
}

// String.length（与 Array\<T\>.length 同一内建通道，用户裁定 i64）：
// 声明只给符号与类型，无 backing 存储、无编译期初值——运行期按实例求值；
// BIL VM 的 get.field 对 core::String#length@.i64 直读宿主字符串长度
//（core::Array#length@.i32 的 VM 直读先例）。const 保证不可写入。
pub ext const String.length: i64

// toString 机制的 native 触达点（SYNTAX §3.8，用户裁定）：Any/Object 的
// toString 不再是 native 成员——它们的默认实现体由编译器合成为调用本
// 函数的小 fn。文件级私有全局形态把用户挡在访问控制外（§16.1），只经
// 合成体触达；VM hook（BIL §22.5）对任意胖值取标准文本。
@NativeLibrary("rigi_rt")
@NativeSymbol("any_to_string")
priv native func any_to_string(value: Any): String
