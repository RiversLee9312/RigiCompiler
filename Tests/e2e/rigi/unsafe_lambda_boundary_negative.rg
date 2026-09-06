// lambda 是独立的函数边界，不能隐式继承外层 unsafe 权限。
// expect-error: requires an unsafe context
unsafe func danger(): i32 { return 7 }
pub func main(): i32 {
    unsafe seq {
        const callback = func{(): i32 -> danger()}
    }
    return 0
}
