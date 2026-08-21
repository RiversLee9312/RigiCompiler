// V-B（c3b/p16c）：(await c.t).n()——await 解包结果 HiddenS 无使用点钩子，应报 inaccessible
pub func main(): i32 {
    const c = new TaskHolder()
    return (await c.t).n()
}
