// V4（p18a）：seq using(const r = c.hd) 推断资源类型 HiddenRes 无检查——应报 inaccessible
pub func main(): i32 {
    const c = new ResHolder()
    seq using(const r = c.hd) { r.use() }
    return 0
}
