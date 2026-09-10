// self 的 rich 豁免不允许 local wrapper 接收者跨协程。
// expect-error: async
@WrapperTarget(.Entity)
wrapper LocalWrapper {
    pub init()
    pub async func run(): i32 { return 1 }
}
@LocalWrapper
class Owner { pub init() }
pub func main(): i32 {
    var owner = new Owner()
    return await owner:LocalWrapper.run()
}
