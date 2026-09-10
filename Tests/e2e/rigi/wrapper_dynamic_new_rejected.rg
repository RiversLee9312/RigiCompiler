// 泛型隐藏具体类型后也不能构造脱离宿主的 wrapper。
// expect-exit: 0
@WrapperTarget(.Entity)
wrapper Borrowed {
    pub var value: i32
    pub init(_ -> value)
}
func rejected\<T>(kind: Type\<T>): bool {
    try { var instance = new kind(1) }
    catch (e: core.NoSuchMethodException) { return true }
    return false
}
pub func main(): i32 {
    if (rejected(typeOf(Borrowed))) { return 0 }
    return 1
}
