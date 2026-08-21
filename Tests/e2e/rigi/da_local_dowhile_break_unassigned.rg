// 局部 DA（§7.3 / §21.4）：do-while 体内 break 跳过赋值——出环点与体尾取交，
// 循环后读取 x 必须拒绝（与 §9.3 字段 DA 同口径）。
// expect-error: Use of unassigned local variable 'x'
pub func main(): i32 {
    var x: i32
    var b = true
    do {
        if (b) { break }
        x = 1
    } while (false)
    return x
}
