// bug S3 对照：单接口默认方法正常隐式继承；双接口场景显式 override
// 后双视图（A/B 静态类型）都派发到类实现。运行输出 B / C / C
import core.io.Console

pub interface A {
    func id(): i32
    func tag(): String { return "A" }
}

pub interface B {
    func id(): i32
    func tag(): String { return "B" }
}

pub class Single implements A {
    pub init()
    pub override func id(): i32 { return 1 }
}

pub class C implements A, B {
    pub init()
    pub override func id(): i32 { return 1 }
    pub override func tag(): String { return "C" }
}

pub func main(): i32 {
    const s: A = new Single()
    Console.println(s.tag())
    const a: A = new C()
    const b: B = new C()
    Console.println(a.tag())
    Console.println(b.tag())
    return 0
}
