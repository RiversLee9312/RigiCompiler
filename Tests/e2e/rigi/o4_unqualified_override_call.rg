// bug O4（正例）：无限定裸名调用 override 方法按最派生槽单候选绑定
//（与 this.m() 同口径）——修复前基类 open + 子类 override 双候选报歧义。
// C.own() 命中 C 版；D 未 override，D.own() 命中中间层 B 版。
// expect-output: C
// expect-output: B
// expect-exit: 0
import core.io.Console
pub open class A {
    pub open func tag(): String { return "A" }
}
pub open class B : A {
    pub override func tag(): String { return "B" }
}
pub class C : B {
    pub override func tag(): String { return "C" }
    pub func own(): String { return tag() }
}
pub class D : B {
    pub func own(): String { return tag() }
}
pub func main(): i32 {
    Console.println(new C().own())
    Console.println(new D().own())
    return 0
}
