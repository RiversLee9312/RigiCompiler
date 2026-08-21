// F1/V-B（负例·多文件同组）：(await c.t).n()——await 解包结果 HiddenS 经
// ExpressionDispatcher 统一收口拦截（§16.1）。
// expect-error: 'HiddenS' is inaccessible due to its accessibility level
pub func main(): i32 {
    const c = new TaskHolder()
    return (await c.t).n()
}
