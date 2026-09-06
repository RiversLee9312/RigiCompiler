// 对象不能作为 AtomicStruct 的值类型参数。
// expect-error: constraint
class AtomicLocal {}
pub func main(): i32 {
    const value = new AtomicStruct\<AtomicLocal>(new AtomicLocal())
    return 0
}
