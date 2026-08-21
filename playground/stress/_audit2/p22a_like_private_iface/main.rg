// V5a 跨文件使用点：Cage 的 like 合成 forwarder 把私有接口成员
// privTaste 以 pub 暴露到本文件
pub func main(): i32 {
    const c = new Cage()
    return c.privTaste()
}
