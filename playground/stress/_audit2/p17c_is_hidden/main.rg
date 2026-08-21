// V-D（负例）：is 试探命中私有类型 Hidden——修复前零检查静默通过，
// 修复后补只读可见性检查（as 走 TypeReferences 本就拦截）
pub func main(): i32 {
    const a = make()
    if (a is Hidden) {
        return 1
    }
    return 0
}
