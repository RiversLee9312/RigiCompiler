using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // Types 职责；与主文件共享同一类型、字段及生命周期。

        private static void TestNumericCasts()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    var a: i32 = 1000\n" +
                "    var b: i64 = (a as i64)\n" +
                "    var c: i16 = (a as i16)\n" +
                "    var d: u8 = (42 as u8)\n" +
                "    var e: double = (a as double)\n" +
                "    var f: i32 = ((e as i32) + (c as i32))\n" +
                "    return ((f + (d as i32)) + (b as i32))\n" +
                "}\n");
            CheckOk("数值 cast", result);
            CheckI32("widening/narrowing 抽样", result, 3042);
        }

        private static void TestReferenceCasts()
        {
            var up = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub func main(): bool {\n" +
                "    var d: Animal = new Dog()\n" +
                "    return (d is Dog)\n" +
                "}\n");
            CheckOk("引用 is", up);
            CheckBool("Dog is Dog", up, true);
            var down = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub func main(): i32 {\n" +
                "    var a: Animal = new Dog()\n" +
                "    var d = (a as Dog)\n" +
                "    return 7\n" +
                "}\n");
            CheckOk("向下 cast", down);
            CheckI32("Animal→Dog", down, 7);
        }

        private static void TestCastFailureAndSafe()
        {
            var fail = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub func main(): i32 {\n" +
                "    var a: Animal = new Animal()\n" +
                "    var d = (a as Dog)\n" +
                "    return 0\n" +
                "}\n");
            CaseAssertions.CheckTrue("失败 cast 抛 CastException",
                fail.Exception?.ExceptionObject is VmObject obj
                && obj.TypeRef.Contains("CastException"),
                fail.Exception?.ToString() ?? "<null>");
            var safe = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub func main(): bool {\n" +
                "    var a: Animal = new Animal()\n" +
                "    var d = (a as? Dog)\n" +
                "    return (d == null)\n" +
                "}\n");
            CheckOk("cast.safe", safe);
            CheckBool("safe 产 null", safe, true);
        }

        // 同名不同元数的声明以及同一泛型声明的不同构造形态，
        // 都不是可以通过“擦除实参”得到的运行期视图。这两条 cast
        // 必须检查实际 TypeSheet 并失败，否则后续字段/方法 ABI 会错位。
        private static void TestGenericArityAndArgumentsAreNotCastViews()
        {
            var arity = Run(
                "pub class ArityTask { pub init() {} }\n" +
                "pub class ArityTask\\<T> { pub init() {} }\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var generic = new ArityTask\\<i32>()\n" +
                "        var plain = (generic as ArityTask)\n" +
                "        return 0\n" +
                "    } catch (_: core.CastException) { return 7 }\n" +
                "}\n");
            CheckOk("Task<i32> 不可冒充 Task", arity);
            CheckI32("Task<i32> → Task 抛 CastException", arity, 7);

            var arguments = Run(
                "pub class CastBox\\<T> { pub init() {} }\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var numbers = new CastBox\\<i32>()\n" +
                "        var strings = (numbers as CastBox\\<String>)\n" +
                "        return 0\n" +
                "    } catch (_: core.CastException) { return 9 }\n" +
                "}\n");
            CheckOk("Box<i32> 不可冒充 Box<String>", arguments);
            CheckI32("不同泛型实参抛 CastException", arguments, 9);
        }

        private static void TestAnyBoxUnbox()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    var a: Any = 21\n" +
                "    var n = (a as i32)\n" +
                "    return (n + n)\n" +
                "}\n");
            CheckOk("Any 装拆箱", result);
            CheckI32("21+21", result, 42);
        }

        private static void TestTypeIsSupersCase()
        {
            var isCheck = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub func main(): bool {\n" +
                "    var d = new Dog()\n" +
                "    return (d is Animal)\n" +
                "}\n");
            CheckOk("type.is", isCheck);
            CheckBool("Dog is Animal", isCheck, true);
            var supers = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub func main(): bool {\n" +
                "    var a = new Animal()\n" +
                "    return (a supers Dog)\n" +
                "}\n");
            CheckOk("type.supers", supers);
            CheckBool("Animal supers Dog", supers, true);
            var isCase = Run(
                "enum struct Outcome { }[Ok, Failed]\n" +
                "pub func main(): bool {\n" +
                "    const r: Outcome = .Failed\n" +
                "    return (r is .Failed)\n" +
                "}\n");
            CheckOk("type.is.case", isCase);
            CheckBool("is .Failed", isCase, true);
        }

        private static void TestTypeWithAndGetId()
        {
            var typeOf = Run(
                "pub class Box { pub var n: i32 = 0 }\n" +
                "pub func main(): bool {\n" +
                "    var b = new Box()\n" +
                "    var t = typeOf(b)\n" +
                "    return (b is t)\n" +
                "}\n");
            CheckOk("getid.var + type.is.indirect", typeOf);
            CheckBool("b is typeOf(b)", typeOf, true);
            var typeId = Run(
                "pub class Box { pub var n: i32 = 0 }\n" +
                "pub func main(): bool {\n" +
                "    var b = new Box()\n" +
                "    var t = typeOf(Box)\n" +
                "    return (b is t)\n" +
                "}\n");
            CheckOk("getid.type + type.is.indirect", typeId);
            CheckBool("b is typeOf(Box)", typeId, true);
        }

    }
}
