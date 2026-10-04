using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Middleware.Cli;
using RigiCompiler.Middleware.Emit;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests
{
    public static partial class NativeE2ETests
    {
        // 原编号 164..187 的有序用例；与主文件共享同一类型、字段及生命周期。
        private static (string Label, Action Run)[] CreateWrapperDispatchCases() => new (string Label, Action Run)[]
        {
            // ⑨g 多层环组合：外层 wildcard 改写 .name、内层 wildcard
            // 原样透传（fetch 与 other 同装 WIn——VM 内层环按改写后
            // 符号取槽，无状态 wrapper 双端可观察一致）；改写经透传
            // 路径进内层环，环序 out→in、内层看到改写后符号、落点
            // other 原始体
            Case("Method wrapper 双层 wildcard 改写 .name 剩余环序",
                "import core.io.Console\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper WOut {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        Console.println(\"out:\" + .name)\n" +
                "        .name = \"Service$other(x:.i32)@.i32\"\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper WIn {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        Console.println(\"in:\" + .name)\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @WOut\n" +
                "    @WIn\n" +
                "    pub func fetch(x: i32): i32 { return (x + 1) }\n" +
                "    @WIn\n" +
                "    pub func other(x: i32): i32 { return (x + 100) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var r = s.fetch(1)\n" +
                "    Console.println(\"r=\" + r.toString())\n" +
                "    var r2 = s.other(2)\n" +
                "    Console.println(\"r2=\" + r2.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // ⑨h 落点绕过目标自身 wrapper 链（VM FinishChain 链末
            // InvokeResolved 同口径）：other 自挂 Trace（specific），
            // 重路由落点 = other 的 $.mwrapped. 最深层原始体——fetch(1)
            // 经 Rer 改写后只有 "rer" 一次打印（无 "trace"）；直调
            // other(2) 正常触发 Trace
            Case("Method wrapper 改写 .name 落点绕过目标自身链",
                "import core.io.Console\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Rer {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        Console.println(\"rer\")\n" +
                "        .name = \"Service$other(x:.i32)@.i32\"\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        Console.println(\"trace\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Rer\n" +
                "    pub func fetch(x: i32): i32 { return (x + 1) }\n" +
                "    @Trace\n" +
                "    pub func other(x: i32): i32 { return (x + 100) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var r = s.fetch(1)\n" +
                "    Console.println(\"r=\" + r.toString())\n" +
                "    var r2 = s.other(2)\n" +
                "    Console.println(\"r2=\" + r2.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // ===== MW10 刀5：singleton 运行时（VM 为基准）=====
            // ① 基本语义：两次 new 同一实例、状态共享（VM §8.7）
            Case("singleton 基本语义（两次 new 同一实例）",
                "pub shared singleton class S {\n" +
                "    pub var v: i32\n" +
                "    pub init() { v = 7 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new S()\n" +
                "    var b = new S()\n" +
                "    b.v = (a.v + 1)\n" +
                "    return new S().v\n" +
                "}\n"),
            // ② 急切初始化：用户 singleton init 副作用先于 main 首句；
            // 从不触达的 singleton 同样急切初始化
            Case("singleton 急切初始化先于 main",
                "import core.io.Console\n" +
                "pub shared singleton class Boot {\n" +
                "    pub var answer: i32\n" +
                "    pub init() {\n" +
                "        answer = 42\n" +
                "        Console.println(\"boot init\")\n" +
                "    }\n" +
                "}\n" +
                "pub shared singleton class Idle {\n" +
                "    pub init() { Console.println(\"idle init\") }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(\"main first\")\n" +
                "    return new Boot().answer\n" +
                "}\n"),
            // ②b companion 急切初始化：静态 wrapped 字段的 cell/wrapper
            // 构造副作用先于 main（companion 不被显式 new）
            Case("companion 急切初始化（cell wrapper 构造先于 main）",
                "import core.io.Console\n" +
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper Trace {\n" +
                "    pub init() { Console.println(\"wrapper init\") }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    @Trace\n" +
                "    pub static var level: i32 = 5\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(\"main first\")\n" +
                "    return Holder.level\n" +
                "}\n"),
            // ③ init 互访递归触发：A 的 init 用 B → B 恰好构造一次
            Case("singleton init 互访递归触发",
                "import core.io.Console\n" +
                "pub shared singleton class A {\n" +
                "    pub var b: i32\n" +
                "    pub init() {\n" +
                "        Console.println(\"A init\")\n" +
                "        b = (new B().value + 1)\n" +
                "    }\n" +
                "}\n" +
                "pub shared singleton class B {\n" +
                "    pub var value: i32\n" +
                "    pub init() {\n" +
                "        Console.println(\"B init\")\n" +
                "        value = 41\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(\"main first\")\n" +
                "    return new A().b\n" +
                "}\n"),
            // ④ 构造环：急切初始化期互引 → 异常（自定义 runner：VM 侧
            // 该异常是基础设施级 VmException，BilVm.Run 直接抛出，不能走
            // RunFailCase 的 BilVmResult.Exception 通道）
            ("singleton 构造环抛异常（急切初始化期）", () => RunSingletonCycleCase()),
            // ⑤ 静态字段 Value wrapper：companion cell 读写经 proxy 链
            //（playground/mw10_probe.rg 的 Holder.level 形态）
            Case("静态字段 Value wrapper（companion cell 链）",
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper SClamp {\n" +
                "    pub var floor: i32\n" +
                "    pub init(_ -> floor)\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    @SClamp(0)\n" +
                "    pub static var level: i32 = 5\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Holder.level = (Holder.level + 1)\n" +
                "    return Holder.level\n" +
                "}\n"),
            // ⑥ 全局字段 Value wrapper：cell 即 singleton（带 wrapper 实参，
            // 实参在 cell ..init.wrapper 体内求值）
            Case("全局字段 Value wrapper（cell 单例）",
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper SClamp {\n" +
                "    pub var min: i32\n" +
                "    pub var max: i32\n" +
                "    pub init(_ -> min, _ -> max)\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "@SClamp(40, 2)\n" +
                "var h: i32 = 40\n" +
                "pub func main(): i32 {\n" +
                "    h = (h + 1)\n" +
                "    return h\n" +
                "}\n"),
            Case("wrapper Entity wildcard 拦截改返与原样转发",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Router {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(symbol)\n" +
                "        if (symbol == \"Service$zap(x:.i32)@.i32\") {\n" +
                "            return (99 as TReturn)\n" +
                "        }\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Router\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "    pub func zap(x: i32): i32 { return x }\n" +
                "    pub func poke() { core.io.Console.println(\"poke\") }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    s.poke()\n" +
                "    var a = s.ping(41)\n" +
                "    var b = s.zap(1)\n" +
                // 语言内校验（linux 退出码 8-bit 截断规避）：分步小码保诊断
                "    if ((a != 42)) { return 1 }\n" +
                "    if ((b != 99)) { return 2 }\n" +
                "    return 42\n" +
                "}\n"),
            Case("wrapper Entity 同层 specific 压 wildcard",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Mix {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(x: i32): i32 {\n" +
                "        core.io.Console.println(\"specific\")\n" +
                "        return (inner(x) + 1)\n" +
                "    }\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(\"wild\")\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Mix\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return x }\n" +
                "    pub func pong(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.ping(1)\n" +
                "    var b = s.pong(1)\n" +
                "    return ((a * 10) + b)\n" +
                "}\n"),
            Case("wrapper Entity 双层 specific→wildcard 顺序",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WOuter {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(x: i32): i32 {\n" +
                "        core.io.Console.println(\"outer\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WInner {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(\"inner\")\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@WOuter\n" +
                "@WInner\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.ping(41)\n" +
                "}\n"),
            Case("wrapper Entity 双层 wildcard→wildcard 顺序",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WA {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(\"A\")\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WB {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(\"B\")\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@WA\n" +
                "@WB\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.ping(41)\n" +
                "}\n"),
            Case("wrapper Entity wildcard 改写 symbol 重路由",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Router {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        if (symbol == \"Service$ping(x:.i32)@.i32\") {\n" +
                "            symbol = \"Service$pong(x:.i32)@.i32\"\n" +
                "            return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "        }\n" +
                "        if (symbol == \"Service$zap(x:.i32)@.i32\") {\n" +
                "            return (99 as TReturn)\n" +
                "        }\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Router\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "    pub func pong(x: i32): i32 { return (x * 10) }\n" +
                "    pub func zap(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.ping(5)\n" +
                "    var b = s.zap(1)\n" +
                "    var c = s.pong(2)\n" +
                // 语言内校验（linux 退出码 8-bit 截断规避）：分步小码保诊断
                "    if ((a != 50)) { return 1 }\n" +
                "    if ((b != 99)) { return 2 }\n" +
                "    if ((c != 20)) { return 3 }\n" +
                "    return 42\n" +
                "}\n"),
            // 遗6：泛型宿主成员经 wildcard 的完整烘焙（VM↔native 对拍。
            // VM 语义：方法级 typeid 隐藏实参随值实参同装箱进 unnamed
            // 位置包（声明序居值参前），环末解包恢复作隐藏形参调原始
            // 泛型体；.generic.TUnnamedArgs 类型包恒空不承载 typeid）
            // ①+② 泛型方法经 wildcard 被拦截（打印 symbol + inner 原样
            // 转发、T 推断正确）+ 返回 T 的装箱往返值正确
            Case("wrapper Entity wildcard 泛型方法拦截原样转发",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Tracer {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(symbol)\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Tracer\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func pick\\<T>(x: T): T { return x }\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.pick\\<i32>(41)\n" +
                "    var b = s.ping(1)\n" +
                // 语言内校验（linux 退出码 8-bit 截断规避）：分步小码保诊断
                "    if ((a != 41)) { return 1 }\n" +
                "    if ((b != 2)) { return 2 }\n" +
                "    return 42\n" +
                "}\n"),
            // ②b 泛型调用的 unnamed 包 = [typeid, 值]（proxy 可观察包长
            // 并拦截改返；非泛型 ping 包长 1 原样转发）
            Case("wrapper Entity wildcard 泛型方法包首 typeid 可观察",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Tracer3 {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        if (unnamedArgs.length == 2) {\n" +
                "            return (7 as TReturn)\n" +
                "        }\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Tracer3\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func pick\\<T>(x: T): T { return x }\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.pick\\<i32>(41)\n" +
                "    var b = s.ping(1)\n" +
                "    if (a == 7) {\n" +
                "        core.io.Console.println(\"pack2-intercepted\")\n" +
                "    } else {\n" +
                "        core.io.Console.println(\"pack-other\")\n" +
                "    }\n" +
                "    if (b == 2) {\n" +
                "        core.io.Console.println(\"ping-ok\")\n" +
                "    }\n" +
                // 语言内校验（linux 退出码 8-bit 截断规避）：分步小码保诊断
                "    if ((a != 7)) { return 1 }\n" +
                "    if ((b != 2)) { return 2 }\n" +
                "    return 42\n" +
                "}\n"),
            // ③+⑤ 多泛型参数 + 混合值参 + 显式泛型实参形态
            //（s.mix\<i32, String\>(...)）：unnamed 包 = [T typeid,
            // U typeid, 值...]，终态逐槽恢复
            Case("wrapper Entity wildcard 多泛型参数混合值参",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Tracer4 {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(symbol)\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Tracer4\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func mix\\<T, U>(x: T, y: U, n: i32): T { return x }\n" +
                "    pub func size\\<T, U>(x: T, y: U): U { return y }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.mix\\<i32, String>(41, \"hej\", 1)\n" +
                "    var n = s.size\\<i32, i32>(1, 5)\n" +
                // 语言内校验（linux 退出码 8-bit 截断规避）：分步小码保诊断
                "    if ((a != 41)) { return 1 }\n" +
                "    if ((n != 5)) { return 2 }\n" +
                "    return 42\n" +
                "}\n"),
            // ④ 泛型方法经 inner 改写 symbol 重路由到另一泛型方法
            //（包形状一致 [typeid, 值]；router 分支解包恢复 typeid 调
            // gank 的 $.wrapped. 体——gank 把 x 当 i32 加 100 再 as T，
            // 41 → 141；重路由到非泛型方法 VM 侧实参个数不匹配抛错，
            // 不进对拍）
            Case("wrapper Entity wildcard 泛型方法改写 symbol 重路由泛型",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Rerouter {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        if (symbol == \"Service$pick(x:.generic<$.generic.T>)" +
                "@.generic<$.generic.T>\") {\n" +
                "            symbol = \"Service$gank(x:.generic<$.generic.T>)" +
                "@.generic<$.generic.T>\"\n" +
                "            return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "        }\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Rerouter\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func pick\\<T>(x: T): T { return x }\n" +
                "    pub func gank\\<T>(x: T): T { return ((((x as i32) + 100)) as T) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.pick\\<i32>(41)\n" +
                "    return a\n" +
                "}\n"),
            // 类级泛型宿主边界：类级 typeid 不进包（调用约定剔除，
            // 被调方从 .this 隐藏字段自取）；trampoline 打包与终态
            // 实参拼装均跳过类级槽（VM PushFrame 重注入同口径）
            Case("wrapper Entity wildcard 类级泛型宿主成员",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Tracer2 {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(symbol)\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Tracer2\n" +
                "pub class Box\\<T> {\n" +
                "    pub var value: T\n" +
                "    pub init(v: T) { value = v }\n" +
                "    pub func get(): T { return value }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box\\<i32>(7)\n" +
                "    var a = b.get()\n" +
                "    return a\n" +
                "}\n"),
            // MW10 刀4：call??? 降级全链（VM↔native 对拍，用例转录/参照
            // BilVmTests TestDowngradeCallWildcard /
            // TestEntityWildcardMethodProxyBothDirections）
            // ① 未声明方法命中 .proxy.*（proxy 改返 99）+ 已声明 ping
            // 原样转发经环到原始体；返回 .any 的 cast 拆箱由前端既有
            // cast 承担（(s.fetchUserById(42) as i32) 顺带覆盖）
            Case("wrapper call??? 未声明命中 proxy 改返与原样转发",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Router {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(symbol)\n" +
                "        if (symbol == \"Service$fetchUserById(.i32)@.any\") {\n" +
                "            return (99 as TReturn)\n" +
                "        } else {\n" +
                "            return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "        }\n" +
                "    }\n" +
                "}\n" +
                "@Router\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.ping(41)\n" +
                "    var b = (s.fetchUserById(42) as i32)\n" +
                // 语言内校验（linux 退出码 8-bit 截断规避）：分步小码保诊断
                "    if ((a != 42)) { return 1 }\n" +
                "    if ((b != 99)) { return 2 }\n" +
                "    return 42\n" +
                "}\n"),
            // ② call??? 进环后经 router 重路由命中另一已声明成员的剩余
            // 环（外层 wildcard 改写 symbol 原样 inner，内层 specific
            // .proxy.ping 环接管返回；VM RerouteWildcardInner 同口径）
            Case("wrapper call??? 重路由命中已声明成员剩余环",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WA {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        if (symbol == \"Service$fetchUserById(.i32)@.any\") {\n" +
                "            symbol = \"Service$ping(x:.i32)@.i32\"\n" +
                "            return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "        }\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WB {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(x: i32): i32 { return (x + 100) }\n" +
                "}\n" +
                "@WA\n" +
                "@WB\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return (s.fetchUserById(42) as i32)\n" +
                "}\n"),
            // ③ 未路由抛 NoSuchMethodException 且可被 try/catch 捕获
            //（消息口径对齐 VM「未路由的降级请求：」+ symbol）
            Case("wrapper call??? 未路由抛 NoSuchMethod 可 catch",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@W\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var service = new Service()\n" +
                "    try {\n" +
                "        service.fetchUserById(42)\n" +
                "    } catch (e: core.NoSuchMethodException) {\n" +
                "        core.io.Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n"),
            // ④ 子类实例的降级调用命中基类 wrapper（继承闭包：Child
            // 重申 @W，dispatch 深度序先命中 Child entry 环；继承方法
            // ping 经基类烘焙链同被拦截——转录 BilVmDispatchTests
            // WrapperFixes 子类拦截形态）
            Case("wrapper call??? 子类实例命中基类 wrapper",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(\"hit:\" + symbol)\n" +
                "        return (99 as TReturn)\n" +
                "    }\n" +
                "}\n" +
                "@W\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "@W\n" +
                "pub class Child : Base { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Child()\n" +
                "    var a = c.ping(1)\n" +
                "    var b = (c.fetchUserById(42) as i32)\n" +
                // 语言内校验（linux 退出码 8-bit 截断规避）：分步小码保诊断
                "    if ((a != 99)) { return 1 }\n" +
                "    if ((b != 99)) { return 2 }\n" +
                "    return 42\n" +
                "}\n"),
            Case("wrapper 字段-Value 读写",
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init() { min = 3 }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Clamped\n" +
                "    pub var hp: i32 = 0\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h = new Hero()\n" +
                "    if (h.hp:Clamped.min != 3) { return 1 }\n" +
                "    h.hp:Clamped.min = 9\n" +
                "    return h.hp:Clamped.min\n" +
                "}\n"),

        };

    }
}
