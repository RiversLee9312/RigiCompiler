// 安全代码不能构造 unsafe Atomic。
// expect-error: requires an unsafe context
pub func main(): i32 {
    const value = new Atomic\<i32>(1)
    return 0
}
