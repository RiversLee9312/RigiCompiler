// DA（§9.3）：do-while 体内 break 跳过赋值——出环点与体尾取交，
// 非空字段 x 并非全路径定值，前端必须拒绝（禁止以零值出厂）。
// expect-error: Field 'x' of 'C' is not definitely assigned on all paths
pub class C {
    pub var x: i32
    pub init(b: bool) {
        do {
            if (b) { break }
            x = 1
        } while (false)
    }
}
pub func main(): i32 {
    return new C(true).x
}
