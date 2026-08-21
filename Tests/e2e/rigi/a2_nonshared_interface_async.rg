// bug A2（负例）：非 shared 接口声明 async 成员——声明点 fail-fast
//（§3.1.1/§4.5：含 async 成员的接口必须 shared）。
// expect-error: 'Worker': an interface declaring 'async' members must be 'shared'
pub interface Worker {
    async func run(x: i32): i32
}
pub func main(): i32 {
    return 0
}
