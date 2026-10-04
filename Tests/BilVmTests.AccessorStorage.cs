using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // AccessorStorage 职责；与主文件共享同一类型、字段及生命周期。

        // bug17：String.length 内建 const i64（.bootstrap.rg ext 声明，
        // VM get.field 直读，同 Array.length 通道）——字面量/多行/插值串
        private static void TestStringLength()
        {
            var result = Run(
                "pub func main(): i32 {\n" +
                "    var s = \"hello\"\n" +
                "    core.io.Console.println(\"${s.length}\")\n" +
                "    var multi = \"\"\"\n" +
                "line1\n" +
                "line2\n" +
                "\"\"\"\n" +
                "    core.io.Console.println(\"${multi.length}\")\n" +
                "    var name = \"world\"\n" +
                "    core.io.Console.println(\"${\"hi ${name}\".length}\")\n" +
                "    core.io.Console.println(\"${\"序列化测试\".length}\")\n" +
                "    core.io.Console.println(\"${\"序列化测试\".characterCount}\")\n" +
                "    core.io.Console.println(\"${\"👨‍👩‍👧\".length}\")\n" +
                "    core.io.Console.println(\"${\"👨‍👩‍👧\".characterCount}\")\n" +
                "    core.io.Console.println(\"${\"🇨🇳\".length}\")\n" +
                "    core.io.Console.println(\"${\"🇨🇳\".characterCount}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("String.length", result);
            TestHarness.Check("length/characterCount stdout", result.Stdout,
                "5\n11\n8\n15\n5\n18\n5\n8\n2\n");
            CheckI32("main 返回 0", result, 0);
        }

        // §13.3 写序：wrapper 链 → setter → 直写 backing
        // wrapper(5+10)=15 → setter(15*2)=30
        private static void TestWrapperOutsideSetterWriteOrder()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Shift {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        var v = (value as i32)\n" +
                "        inner(((v + 10) as TValue))\n" +
                "    }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Shift()\n" +
                "    pub var hp: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { value = value * 2 }\n" +
                "    } = 0\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h = new Hero()\n" +
                "    h.hp = 5\n" +
                "    return h.hp\n" +
                "}\n");
            CheckOk("写序 wrapper→setter", result);
            CheckI32("wrapper(5+10)=15 → setter*2=30", result, 30);
        }

        // §13.3 读序：backing → getter → wrapper 链
        // backing 10 → getter*2=20 → wrapper+1=21
        private static void TestWrapperOutsideGetterReadOrder()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Shift {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        var v = (value as i32)\n" +
                "        return ((v + 1) as TValue)\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Shift()\n" +
                "    pub var hp: i32 {\n" +
                "        pub get(value: _) { return value * 2 }\n" +
                "        pub set(value: _) { }\n" +
                "    } = 10\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Hero().hp\n" +
                "}\n");
            CheckOk("读序 getter→wrapper", result);
            CheckI32("backing 10 → getter*2=20 → wrapper+1=21", result, 21);
        }

        // 初始化器经 setter、不绕尚未安装的 wrapper
        private static void TestWrapperAccessorInitializerNoCrash()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper W {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @W()\n" +
                "    pub var hp: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { if (value > 100) { value = 100 } }\n" +
                "    } = 150\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Hero().hp\n" +
                "}\n");
            CheckOk("初始化器经 setter 不崩溃", result);
            CheckI32("init 豁免 wrapper、setter 钳制 150→100", result, 100);
        }

        // setter 体内多次读写 value（..value 直达 backing）
        private static void TestSetterBodyMultipleValueAccess()
        {
            var result = Run(
                "pub class Box {\n" +
                "    pub var n: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) {\n" +
                "            var t = value\n" +
                "            value = t + 1\n" +
                "        }\n" +
                "    } = 0\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box()\n" +
                "    b.n = 5\n" +
                "    return b.n\n" +
                "}\n");
            CheckOk("setter 体内多次读写 value", result);
            CheckI32("value 读 5 再写 t+1 → 6", result, 6);
        }

        // 局部变量（cell）同等外置顺序
        private static void TestWrapperAccessorLocalOrder()
        {
            var result = Run(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Shift {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        core.io.Console.println(\"wrapper.get\")\n" +
                "        var v = (value as i32)\n" +
                "        return ((v + 1) as TValue)\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        core.io.Console.println(\"wrapper.set\")\n" +
                "        var v = (value as i32)\n" +
                "        inner(((v + 10) as TValue))\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @Shift()\n" +
                "    var x: i32 {\n" +
                "        get(value: _) {\n" +
                "            core.io.Console.println(\"user.get\")\n" +
                "            return value * 2\n" +
                "        }\n" +
                "        set(value: _) {\n" +
                "            core.io.Console.println(\"user.set\")\n" +
                "            value = value * 2\n" +
                "        }\n" +
                "    } = 1\n" +
                "    x = 5\n" +
                "    return x\n" +
                "}\n");
            CheckOk("局部 wrapper+访问器外置序", result);
            TestHarness.Check("局部写读打印序", result.Stdout,
                "wrapper.set\nuser.set\nwrapper.set\nuser.set\nuser.get\nwrapper.get\n");
            CheckI32("局部 5→15→30 读 60→61", result, 61);
        }

    }
}
