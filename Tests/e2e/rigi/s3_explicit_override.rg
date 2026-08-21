// bug S3（正例·显式 override）：双接口同签名默认冲突由类显式 override
// 解决后，A/B 双视图都必须派发到类实现，不再出现「B 视图打到 A 默认体」。
// expect-output: C
// expect-output: C
// expect-exit: 2
import core.io.Console
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
    pub override func tag(): String { return "C" }
}
pub func main(): i32 {
    var a: A = new C()
    var b: B = new C()
    Console.println(a.tag())
    Console.println(b.tag())
    return (a.id() + b.id())
}
