// bug S3（负例）：两接口同签名默认方法冲突——接口闭包中 ≥2 个不同符号
// 的同签名默认方法且类未显式 override，类声明点编译错误（§11）。
// expect-error: interface default method 'tag' conflicts between
pub interface A {
    func id(): i32
    func tag(): String { return "A" }
}
pub interface B {
    func id(): i32
    func tag(): String { return "B" }
}
pub class C implements A, B {
    pub init()
    pub override func id(): i32 { return 1 }
}
pub func main(): i32 {
    return 0
}
