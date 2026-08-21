// bug A2（负例·传染矩阵）：shared 接口沿 implements 单向传染——
// 非 shared class 不得实现 shared 接口（§3.1.1）。
// expect-error: 'W': interface 'Worker' is 'shared', so the implementing type must also be 'shared'
pub shared interface Worker { func run(x: i32): i32
 }
pub class W implements Worker {
    pub override func run(x: i32): i32 { return (x + 1) }
}
pub func main(): i32 {
    return 0
}
