// F2/V5a 跨文件使用点：Cage 的 like 合成 forwarder 把私有接口成员
// privTaste 以 pub 暴露到本文件（声明侧已拦截，本文件不再到达）
pub func main(): i32 {
    const c = new Cage()
    return c.privTaste()
}
