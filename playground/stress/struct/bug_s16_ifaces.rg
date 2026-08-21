// bug S3 复现（§11）：两个接口各有同签名默认方法 tag()，类 C 不显式
// 解决冲突——修复前静默编译通过且派发错乱（B 视图打印 "A"）；修复后
// 类声明点编译错误，强制显式 override
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
}

pub func main(): i32 {
    const b: B = new C()
    Console.println(b.tag())
    return 0
}
