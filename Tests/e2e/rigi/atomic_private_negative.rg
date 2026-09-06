// 安全门面不能泄漏底层 capability。
// expect-error: is inaccessible
pub func main(): i32 {
    const value = new AtomicStruct\<i32>(1)
    unsafe seq { const leaked = value.atomic }
    return 0
}
