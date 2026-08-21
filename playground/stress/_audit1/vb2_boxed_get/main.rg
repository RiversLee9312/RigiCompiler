// V-B（c4）：b.get() 代入后返回 Hidden——表达式直链无钩子，应报 inaccessible
pub func main(): i32 {
    const b = new HBox()
    b.get().n()
    return 0
}
