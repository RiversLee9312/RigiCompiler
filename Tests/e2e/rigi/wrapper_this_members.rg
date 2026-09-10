// 接收者的成员、索引和独立闭包合法，闭包只持有显式读取的普通值。
// expect-exit: 0
@WrapperTarget(.Entity)
wrapper Counter {
    pub var value: i32
    pub init() { this.value = 7 }
    pub func read(): i32 { return this.value }
    pub operator getAtIndex(index: i32): i32? { return this.value + index }
    pub func indexed(): i32 { return this[3] as i32 }
    pub func snapshot(): Func\<i32> {
        var saved = this.read()
        return func{(): i32 -> saved}
    }
    pub func constant(): Func\<i32> { return func{(): i32 -> 11} }
}
@Counter
class Owner { pub init() }
pub func main(): i32 {
    var owner = new Owner()
    var callback = owner:Counter.snapshot()
    owner:Counter.value = 19
    if (callback() != 7) { return 1 }
    if (owner:Counter.read() != 19) { return 2 }
    if (owner:Counter.indexed() != 22) { return 3 }
    var constant = owner:Counter.constant()
    if (constant() != 11) { return 4 }
    return 0
}
