// 方法约束里的宿主型参先代入，不能把未知边界作为豁免。
// expect-error: does not satisfy the 'Extends String'
class Host\<T> {
    pub init()
    pub func accept\<U extends T>(value: U) {}
}
pub func main(): i32 {
    var host = new Host\<String>()
    host.accept(17)
    return 0
}
