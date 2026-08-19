using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    // init 运行期匹配：按可赋值性选择重载；精确类型不匹配但可赋给形参
    // 时仍命中；无关类型拒绝。
    public static partial class BilVmDispatchTests
    {
        private static void TestInitOverloadAssignability()
        {
            var result = Run(
                "pub open class Animal { pub var n: i32 }\n" +
                "pub class Dog : Animal { }\n" +
                "pub class Cat : Animal { }\n" +
                "pub class Box {\n" +
                "    pub var tag: i32\n" +
                "    pub init(a: Animal) { tag = 1 }\n" +
                "    pub init() { tag = 0 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const d = new Dog()\n" +
                "    const b = new Box(d)\n" +
                "    const empty = new Box()\n" +
                "    return ((b.tag * 10) + empty.tag)\n" +
                "}\n");
            CheckOk("init 重载：子类实参命中基类形参，零参另一重载仍在", result);
            CheckI32("tag 1 与 0", result, 10);
        }

        private static void TestInitRejectsUnrelatedType()
        {
            var result = Run(
                "pub class Account { pub var id: i32 }\n" +
                "pub class Other { pub var id: i32 }\n" +
                "pub class Node { pub init(a: Account) { } }\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(new Node(new Account()))\n" +
                "    var o = new Other()\n" +
                "    var n = new t(o)\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckTrue("动态 new 无关类型不得命中 Account init",
                result.Exception != null,
                result.Exception?.ToString() ?? "未抛异常");
        }
    }
}
