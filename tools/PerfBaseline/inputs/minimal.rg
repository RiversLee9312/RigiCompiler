// 性能代表输入：固定入口与整数循环，便于区分编译、LLVM 和执行开销。
pub func main(): i32 {
    var sum: i32 = 0
    var index: i32 = 0
    while (index < 1000) {
        sum = sum + index
        index = index + 1
    }
    if (sum == 499500) { return 0 }
    return 1
}
