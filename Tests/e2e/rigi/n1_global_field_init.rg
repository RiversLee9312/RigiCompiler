// bug N1（正例，§8.4.1/§9.3）：顶层全局字段（var/const）与静态字段的
// 声明初始值在 main 前执行（..globals.init）。g=42、cg=1、s=100。
// expect-exit: 4210
var g: i32 = 42
const cg: i32 = 1
pub class Holder {
    pub static var s: i32 = 100
}
pub func main(): i32 {
    return (((g * 100) + (cg * 10)) + (Holder.s - 100))
}
