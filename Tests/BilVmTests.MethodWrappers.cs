using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // MethodWrappers 职责；与主文件共享同一类型、字段及生命周期。

        // .proxy.call a)：实例方法 specific 环绕 + 改返回值（形状镜像被修饰
        // 方法参数）。
        private static void TestMethodWrapperCallSpecificSurrounds()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"before\")\n" +
                "        var r = inner(x)\n" +
                "        core.io.Console.println(\"after\")\n" +
                "        return (((r as i32) + 1) as TReturn)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub func fetch(x: i32): i32 {\n" +
                "        core.io.Console.println(\"body\")\n" +
                "        return (x * 2)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(21)\n" +
                "}\n");
            CheckOk("Method wrapper specific 环绕", result);
            CaseAssertions.Check("Method wrapper stdout 顺序", result.Stdout,
                "before\n" +
                "body\n" +
                "after\n");
            CheckI32("Method wrapper 改返回值 43", result, 43);
        }

        // .proxy.call b)：静态方法经 companion（shared Method wrapper）。
        private static void TestMethodWrapperStaticViaCompanion()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub shared wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn { return inner(x) }\n" +
                "}\n" +
                "pub class Calc {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub static func total(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return Calc.total(41)\n" +
                "}\n");
            CheckOk("静态 Method wrapper 经 companion", result);
            CheckI32("Calc.total(41) 返回 42", result, 42);
        }

        // .proxy.call c)：同方法双 Method wrapper 的 outer→inner 顺序。
        private static void TestMethodWrapperDoubleLayerOrder()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper A {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"A\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper B {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"B\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @A\n" +
                "    @B\n" +
                "    pub func fetch(x: i32): i32 {\n" +
                "        core.io.Console.println(\"body\")\n" +
                "        return x\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(42)\n" +
                "}\n");
            CheckOk("双 Method wrapper 顺序", result);
            CaseAssertions.Check("双 Method wrapper stdout", result.Stdout,
                "A\n" +
                "B\n" +
                "body\n");
            CheckI32("双 Method wrapper 透传 42", result, 42);
        }

        // .proxy.call d)：wrapper 状态跨调用持久（计数器累加）。
        private static void TestMethodWrapperStatePersists()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Counted {\n" +
                "    pub var calls: i32\n" +
                "    pub init() { calls = 0 }\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        calls = (calls + 1)\n" +
                "        var r = inner(x)\n" +
                "        return (((r as i32) + calls) as TReturn)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Counted\n" +
                "    pub func fetch(x: i32): i32 { return (x * 10) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.fetch(1)\n" +
                "    var b = s.fetch(1)\n" +
                "    return ((a * 100) + b)\n" +
                "}\n");
            CheckOk("Method wrapper 状态持久", result);
            CheckI32("第 1 次 11、第 2 次 12 → 1112", result, 1112);
        }

        // .proxy.call e)：参数透传正确性（三参数，proxy 内插值打印实参）。
        private static void TestMethodWrapperArgPassThrough()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Echo {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(a: i32, b: i32, c: i32): TReturn {\n" +
                "        core.io.Console.println(\"a=${a} b=${b} c=${c}\")\n" +
                "        return inner(a, b, c)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Echo\n" +
                "    pub func sum(a: i32, b: i32, c: i32): i32 {\n" +
                "        return (((a * 100) + (b * 10)) + c)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.sum(1, 2, 3)\n" +
                "}\n");
            CheckOk("Method wrapper 参数透传", result);
            CaseAssertions.Check("实参插值 stdout", result.Stdout, "a=1 b=2 c=3\n");
            CheckI32("sum(1,2,3) 返回 123", result, 123);
        }

        // ===== @EntryPoint（SYNTAX §17.1）=====

        // 命名空间内的 main 经 @EntryPoint 成为入口（裸 main 命名约定
        // 只认全局命名空间的历史问题修复）
        private static void TestEntryPointNamespaceMain()
        {
            var result = Run(
                "namespace app\n" +
                "@EntryPoint\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(\"ns main\")\n" +
                "    return 42\n" +
                "}\n");
            CheckOk("命名空间 @EntryPoint main", result);
            CheckI32("命名空间 main 返回 42", result, 42);
            CaseAssertions.Check("命名空间 main stdout", result.Stdout, "ns main\n");
        }

        // 静态成员方法经 @EntryPoint 成为入口
        private static void TestEntryPointStaticMember()
        {
            var result = Run(
                "pub class App {\n" +
                "    pub init()\n" +
                "    @EntryPoint\n" +
                "    pub static func run(): i32 { return 7 }\n" +
                "}\n");
            CheckOk("静态成员 @EntryPoint", result);
            CheckI32("App.run() 返回 7", result, 7);
        }

        // 多入口：缺省运行报错（报文含 --entry-point 提示）；显式符号选中；
        // 指定符号非 entrypoint 报错。同一模块多次运行（VM 无残留状态）
        private static void TestEntryPointMultipleAndExplicitSelection()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 { return 1 }\n" +
                "@EntryPoint\n" +
                "pub func other(): i32 { return 2 }\n");
            CaseAssertions.CheckTrue("双入口编译无诊断", !unit.Diagnostics.HasErrors);

            var autoRunFailed = false;
            try
            {
                BilVm.Run(module);
            }
            catch (VmException ex)
            {
                autoRunFailed = true;
                CaseAssertions.CheckTrue("多入口报文提示 --entry-point",
                    ex.Message.Contains("--entry-point"), ex.Message);
                CaseAssertions.CheckTrue("多入口报文列出候选符号",
                    ex.Message.Contains("$main()@.i32") && ex.Message.Contains("$other()@.i32"),
                    ex.Message);
            }
            CaseAssertions.CheckTrue("多入口缺省运行抛 VmException", autoRunFailed);

            var other = BilVm.Run(module, 0, "$other()@.i32");
            CheckOk("显式选择 other 无异常", other);
            CheckI32("other() 返回 2", other, 2);
            var main = BilVm.Run(module, 0, "$main()@.i32");
            CheckOk("显式选择 main 无异常", main);
            CheckI32("main() 返回 1", main, 1);

            var badSymbolFailed = false;
            try
            {
                BilVm.Run(module, 0, "$nope()@.i32");
            }
            catch (VmException ex)
            {
                badSymbolFailed = true;
                CaseAssertions.CheckTrue("非 entrypoint 符号报文",
                    ex.Message.Contains("不是 entrypoint"), ex.Message);
            }
            CaseAssertions.CheckTrue("--entry-point 指定非入口符号抛 VmException", badSymbolFailed);
        }

        // ===== 全局函数 Method wrapper（§14.4 修复——此前被静默忽略）=====

        // 全局函数经每命名空间 singleton 宿主（..globals.host）获得与静态
        // 方法 companion 相同的 wrapper 链；发射形状断言宿主声明与安装指令
        private static void TestGlobalMethodWrapperEndToEnd()
        {
            var source =
                "@WrapperTarget(.Method)\n" +
                "pub shared wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(): TReturn {\n" +
                "        core.io.Console.println(\"trace\")\n" +
                "        return inner()\n" +
                "    }\n" +
                "}\n" +
                "@Trace()\n" +
                "pub func heavy(): i32 { return 21 }\n" +
                "pub func main(): i32 {\n" +
                "    var r = heavy()\n" +
                "    return (r * 2)\n" +
                "}\n";
            var (unit, module, text) = BilTestHarness.EmitBilUnit(source);
            CaseAssertions.CheckTrue("全局函数 wrapper 编译无诊断", !unit.Diagnostics.HasErrors);
            CaseAssertions.CheckTrue("合成宿主 singleton 声明",
                text.Contains(".type ..globals.host = class pub singleton shared compiler-generated"),
                text);
            CaseAssertions.CheckTrue("宿主 ..init.wrapper 安装 new.wrapper.method",
                text.Contains("new.wrapper.method fn(..globals.host$heavy()@.i32) type(Trace)"),
                text);
            var result = BilVm.Run(module);
            CheckOk("全局函数 Method wrapper 执行", result);
            CaseAssertions.Check("proxy 已执行（trace 打印）", result.Stdout, "trace\n");
            CheckI32("heavy()*2 返回 42", result, 42);
        }

        // 双层 wrapper 的 outer→inner 顺序与参数透传（与实例/静态方法同口径）
        private static void TestGlobalMethodWrapperDoubleLayerOrder()
        {
            var result = Run(
                "@WrapperTarget(.Method)\n" +
                "pub shared wrapper A {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"A\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Method)\n" +
                "pub shared wrapper B {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"B\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "@A\n" +
                "@B\n" +
                "pub func work(x: i32): i32 {\n" +
                "    core.io.Console.println(\"body\")\n" +
                "    return x\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return work(42)\n" +
                "}\n");
            CheckOk("全局函数双 wrapper 顺序", result);
            CaseAssertions.Check("全局函数双 wrapper stdout", result.Stdout,
                "A\n" +
                "B\n" +
                "body\n");
            CheckI32("全局函数双 wrapper 透传 42", result, 42);
        }

        // 命名空间内的全局函数 wrapper：宿主 singleton 落在该命名空间
        //（与 @EntryPoint 命名空间 main 同场景——切片/宿主归属同覆盖）
        private static void TestGlobalMethodWrapperInNamespace()
        {
            var result = Run(
                "namespace app\n" +
                "@WrapperTarget(.Method)\n" +
                "pub shared wrapper T {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"proxied\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "@T\n" +
                "pub func helper(x: i32): i32 { return (x * 2) }\n" +
                "@EntryPoint\n" +
                "pub func main(): i32 {\n" +
                "    return helper(21)\n" +
                "}\n");
            CheckOk("命名空间全局函数 wrapper", result);
            CaseAssertions.Check("proxy 已执行", result.Stdout, "proxied\n");
            CheckI32("helper(21) 返回 42", result, 42);
        }

        // .proxy.call f)：get.self 直构模块——Method wrapper 的 .proxy.call
        // 模板 fn 内 get.self 产出宿主（.this 为 wrapper 实例）。
        private static void TestMethodWrapperGetSelfDirectModule()
        {
            var module = MethodWrapperGetSelfModule();
            var host = new VmObject("Host", valueType: false);
            host.WriteField("Host#n@.i32", new VmI32(42));
            var wrapper = new VmWrapperReceiver(new VmObject("Timed", valueType: true), host);
            var result = RunPrepared(module, "Timed$$.proxy.call(x:.i32)@.i32",
                new VmValue[] { wrapper, new VmI32(0) });
            CheckOk("Method wrapper get.self 直构", result);
            CheckI32("self.n == 42", result, 42);
        }

        // .proxy.call g)：!= 经 .proxy.opr.equals 链取反——直构模块
        // WrappedVecNeModule：proxy 恒 true → != 得 false。
        private static void TestMethodWrapperNotEqualsViaOprEqualsDirectModule()
        {
            var result = BilVm.Run(WrappedVecNeModule());
            CheckOk("WrappedVecNeModule 直构", result);
            CheckBool("proxy equals 恒 true → != 取反 false", result, false);
        }

    }
}
