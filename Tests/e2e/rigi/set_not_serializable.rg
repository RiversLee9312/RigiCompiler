// STDLIB §4.2.5（施工块 1-4）：Set 不属于 SB、也不直接提供 Serializable——
// Serializable 对象不能把 Set 作为应序列化字段（应显式用 Array/List 表示，
// 或按 @Temporary 规则排除相关字段）。本负例断言带 Set 字段的 @Serializable
// 类在声明点报错。
// expect-error: 类型 'Set' 未声明 Serializable
import core.collections.*

@core.serialization.Serializable
class Holder {
    pub var s: Set\<i32> = new Set\<i32>()
}

pub func main(): i32 {
    return 0
}
