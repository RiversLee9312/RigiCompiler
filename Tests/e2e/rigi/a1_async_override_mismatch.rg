// bug A1（负例）：override 签名匹配纳入 async 一致性（§9.2.1）——
// 接口 sync 成员不得由 async override 实现「满足」（否则经接口调用时
// 静态类型 T 而运行期实得 Task\<T\> 的类型洞）。
// expect-error: 'run': 'async' modifier does not match the inherited member
pub interface Worker {
    func run(x: i32): i32
}
pub shared class W implements Worker {
    pub init()
    pub async override func run(x: i32): i32 { return (x + 1) }
}
pub func main(): i32 {
    return 0
}
