// bug A2（负例·传染矩阵）：shared 接口沿接口继承单向传染——
// 派生接口继承 shared 基接口必须同样标 shared（§3.1.1）。
// expect-error: 'IChild': base interface 'IBase' is 'shared', so the derived interface must also be 'shared'
pub shared interface IBase { }
pub interface IChild : IBase { }
pub func main(): i32 {
    return 0
}
