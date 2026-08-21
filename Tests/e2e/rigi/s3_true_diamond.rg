// bug S3（正例·真菱形）：A/B 经两条路径继承到 Base 的同一符号默认
// 方法，闭包去重后只有一条，不算冲突（§11）。
// expect-output: base
// expect-exit: 0
import core.io.Console
pub interface Base { func tag(): String { return "base" } }
pub interface A : Base { }
pub interface B : Base { }
pub class C implements A, B {
    pub init()
}
pub func main(): i32 {
    const c = new C()
    Console.println(c.tag())
    return 0
}
