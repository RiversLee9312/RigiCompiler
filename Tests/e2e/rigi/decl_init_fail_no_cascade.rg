// constfix 回归：局部声明初值绑定失败的级联诊断锁死（毒化静默补齐，
// b4-1「字节扫描被编译器循环体常量解析缺陷阻断」的实质根因——初值
// 失败的局部曾级联 N 条 "Undefined name"，位置随使用点漂移）。
// 本用例锁定字段键校验的原始触发形状：循环体内 const 初值实参类型
// 不匹配（i64 下标进内建 Span<i32> getAtIndex）——诊断只含初值的主
// 错误，不再级联未定义名。
// expect-error: Cannot pass 'i64' as 'i32'
pub func main(): i32 {
    const key = "hello"
    const bytes = key.toUtf8Span()
    var i: i64 = (0 as i64)
    while (i < (5 as i64)) {
        const c = bytes[i]
        if ((c >= (97 as u8)) and (c <= (122 as u8))) {
            core.io.Console.println("lower")
        }
        const annotated: u8? = bytes[i]
        if (annotated != null) {
            core.io.Console.println("annotated")
        }
        var mutable = bytes[i]
        if ((mutable >= (97 as u8)) and (mutable <= (122 as u8))) {
            core.io.Console.println("mutable")
        }
        i = (i + (1 as i64))
    }
    const outside = bytes[(0 as i64)]
    if (outside != null) {
        core.io.Console.println("outside")
    }
    return 0
}
