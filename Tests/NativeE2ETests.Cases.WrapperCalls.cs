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
        // 原编号 136..163 的有序用例；与主文件共享同一类型、字段及生命周期。
        private static (string Label, Action Run)[] CreateWrapperCallsCases() => new (string Label, Action Run)[]
        {
            Case("with wrapper 修饰",
                "import core.io.Console\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Mark {\n" +
                "    pub init()\n" +
                "}\n" +
                "@Mark\n" +
                "pub class Tagged { pub init() }\n" +
                "pub class Plain { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    var t = new Tagged()\n" +
                "    var p = new Plain()\n" +
                "    if (t with Mark) { Console.println(\"with hit\") }\n" +
                "    if (p with Mark) { Console.println(\"BAD with\") }\n" +
                "    return 0\n" +
                "}\n"),
            Case("wrapper Entity 字段读写",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: i32\n" +
                "    pub init() { level = 7 }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    if (s:Logged.level != 7) { return 1 }\n" +
                "    s:Logged.level = 42\n" +
                "    return s:Logged.level\n" +
                "}\n"),
            Case("wrapper Entity 方法调用",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: i32\n" +
                "    pub init() { level = 11 }\n" +
                "    pub func dump(): i32 { return level }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s:Logged.dump()\n" +
                "}\n"),
            Case("wrapper Entity specific proxy（self + inner）",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    pub init()\n" +
                "    operator .proxy.doSomething(arg: i32): i32 {\n" +
                "        var host = self\n" +
                "        return (inner(arg) + 1)\n" +
                "    }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func doSomething(arg: i32): i32 { return arg }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.doSomething(5)\n" +
                "}\n"),
            // MW10 刀3c：proxy 环 receiver 原地访问宿主隐藏槽（§14.5）——
            // wrapper 可变状态跨调用持久（VM 为基准；用例①转录自
            // BilVmTests.TestEntityProxyStatePersists）
            Case("wrapper Entity specific proxy 状态跨调用持久",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Counting {\n" +
                "    pub var calls: i32\n" +
                "    pub init() { calls = 0 }\n" +
                "    operator .proxy.fetch(x: i32): i32 {\n" +
                "        calls = (calls + 1)\n" +
                "        return (inner(x) + calls)\n" +
                "    }\n" +
                "}\n" +
                "@Counting\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func fetch(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.fetch(10)\n" +
                "    var b = s.fetch(10)\n" +
                "    if (((a == 11) and (b == 12))) { return 1 } else { return 0 }\n" +
                "}\n"),
            // Entity specific .proxy.get.<名>：TField 占位源赋给 Any，烘焙
            // extraSubst 后静态 .i32 → .any 须装箱（与 Method inner 经 Any
            // 同根因）
            Case("wrapper Entity specific get 经 Any 往返",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.hp\\<TField>(value: TField): TField {\n" +
                "        var a: Any = value\n" +
                "        return (a as TField)\n" +
                "    }\n" +
                "}\n" +
                "@W\n" +
                "pub class Hero {\n" +
                "    pub var hp: i32\n" +
                "    pub init() { hp = 41 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h = new Hero()\n" +
                "    var a = h.hp\n" +
                "    if ((a != 41)) { return 1 }\n" +
                "    return 42\n" +
                "}\n"),
            // 遗3 用例①：基类 @W 有状态 wrapper，子类按 §14.9 重申覆盖，
            // 子类实例调基类未 override 方法——VM HiddenEntityKey 仅含
            // wrapper TypeRef（重申覆盖同一键），native 物理槽同归首次
            // 声明（最基类）偏移，计数跨调用递增。init 置 100 起计以区分
            // 「读到未安装的零槽」（零槽原地写回会伪造递增假象）：分歧前
            // 环读基类零槽得 11/12，对拍必败
            Case("wrapper Entity 重申覆盖状态跨调用持久（子类走基类链）",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Counting {\n" +
                "    pub var calls: i32\n" +
                "    pub init() { calls = 100 }\n" +
                "    operator .proxy.fetch(x: i32): i32 {\n" +
                "        calls = (calls + 1)\n" +
                "        return (inner(x) + calls)\n" +
                "    }\n" +
                "}\n" +
                "@Counting\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub func fetch(x: i32): i32 { return x }\n" +
                "}\n" +
                "@Counting\n" +
                "pub class Child : Base {\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Child()\n" +
                "    var a = c.fetch(10)\n" +
                "    var b = c.fetch(10)\n" +
                "    if (((a == 111) and (b == 112))) { return 42 } else { return 0 }\n" +
                "}\n"),
            // 遗3 用例②（回归）：三级重申链——Leaf 实例经 Mid 静态类型
            // 调 Base 未 override 方法，环读/安装同归 Base 槽（下探越过
            // 无自有槽的 Mid）；Base 实例直调同槽不回归
            Case("wrapper Entity 三级重申链经中间基类读槽",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Counting {\n" +
                "    pub var calls: i32\n" +
                "    pub init() { calls = 100 }\n" +
                "    operator .proxy.fetch(x: i32): i32 {\n" +
                "        calls = (calls + 1)\n" +
                "        return (inner(x) + calls)\n" +
                "    }\n" +
                "}\n" +
                "@Counting\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub func fetch(x: i32): i32 { return x }\n" +
                "}\n" +
                "@Counting\n" +
                "pub open class Mid : Base {\n" +
                "    pub init()\n" +
                "}\n" +
                "@Counting\n" +
                "pub class Leaf : Mid {\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const m: Mid = new Leaf()\n" +
                "    var a = m.fetch(10)\n" +
                "    var b = m.fetch(10)\n" +
                "    var s = new Base()\n" +
                "    var c = s.fetch(10)\n" +
                "    var d = s.fetch(10)\n" +
                "    if ((((a == 111) and (b == 112)) and ((c == 111) and (d == 112)))) {\n" +
                "        return 42\n" +
                "    } else { return 0 }\n" +
                "}\n"),
            // 遗3 用例③（加压 o2）：Method wrapper 槽钥匙归方法声明类
            // （VM HiddenMethodKey 含方法符号天然唯一）——子类实例经基类
            // 静态类型调继承方法两次，wrapper 状态跨调用持久（刀6 既有
            // 归一口径的加压锁死）
            Case("Method wrapper 继承方法槽归一状态持久（基类静态类型）",
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
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    @Counted\n" +
                "    pub open func work(x: i32): i32 { return (x * 10) }\n" +
                "}\n" +
                "pub class Child : Base {\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b: Base = new Child()\n" +
                "    var a = b.work(1)\n" +
                "    var c = b.work(1)\n" +
                "    if (((a == 11) and (c == 12))) { return 42 } else { return 0 }\n" +
                "}\n"),
            // 刀3c 用例②：字段-Value wrapper 链环内状态持久（.proxy.set
            // 计数，二次写读到 1，三次写读到 2）
            Case("wrapper Value 链环内状态跨调用持久",
                "@WrapperTarget(.Value)\n" +
                "pub wrapper SetCount {\n" +
                "    pub var sets: i32\n" +
                "    pub init() { sets = 0 }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        sets = (sets + 1)\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @SetCount\n" +
                "    pub var hp: i32\n" +
                "    pub init() { hp = 0 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h = new Hero()\n" +
                "    h.hp = 10\n" +
                "    var one = h.hp:SetCount.sets\n" +
                "    h.hp = 20\n" +
                "    var two = h.hp:SetCount.sets\n" +
                "    if ((((one == 1) and (two == 2)) and (h.hp == 20))) {\n" +
                "        return 1\n" +
                "    } else { return 0 }\n" +
                "}\n"),
            // 刀3c 用例③：wildcard 环状态持久（每次拦截累加，读回 2）
            Case("wrapper Entity wildcard 环状态跨调用持久",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WCount {\n" +
                "    pub var calls: i32\n" +
                "    pub init() { calls = 0 }\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        calls = (calls + 1)\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@WCount\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.ping(1)\n" +
                "    var b = s.ping(2)\n" +
                "    if ((((a == 1) and (b == 2)) and (s:WCount.calls == 2))) {\n" +
                "        return 1\n" +
                "    } else { return 0 }\n" +
                "}\n"),
            // MW10 刀3a：wildcard Entity 方法 proxy 链（VM↔native 对拍，
            // 用例转录/改写自 BilVmTests wrapper 段）
            // ===== MW10 刀6：Method wrapper（.proxy.call）链 =====
            // ① 虚/接口/中间层静态类型调用全命中（转录
            // Tests/e2e/rigi/o1_method_wrapper_virtual_dispatch.rg——
            // trampoline 在实现槽 fn，vtable/iMap 派发自然命中）
            Case("Method wrapper 虚/接口派发命中实现槽",
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(): TReturn {\n" +
                "        core.io.Console.println(\"trace\")\n" +
                "        return inner()\n" +
                "    }\n" +
                "}\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    @Trace()\n" +
                "    pub open func work(): i32 { return 1 }\n" +
                "}\n" +
                "pub class Child : Base {\n" +
                "    pub init()\n" +
                "    @Trace()\n" +
                "    pub override func work(): i32 { return 2 }\n" +
                "}\n" +
                "pub interface Work {\n" +
                "    func work(): i32\n" +
                "}\n" +
                "pub class Job implements Work {\n" +
                "    pub init()\n" +
                "    @Trace()\n" +
                "    pub override func work(): i32 { return 3 }\n" +
                "}\n" +
                "pub open class Mid : Base {\n" +
                "    pub init()\n" +
                "    @Trace()\n" +
                "    pub override func work(): i32 { return 2 }\n" +
                "}\n" +
                "pub class Leaf : Mid {\n" +
                "    pub init()\n" +
                "    @Trace()\n" +
                "    pub override func work(): i32 { return 3 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b: Base = new Child()\n" +
                "    core.io.Console.println(\"${b.work()}\")\n" +
                "    const w: Work = new Job()\n" +
                "    core.io.Console.println(\"${w.work()}\")\n" +
                "    const m: Mid = new Leaf()\n" +
                "    core.io.Console.println(\"${m.work()}\")\n" +
                "    return 0\n" +
                "}\n"),
            // ② 继承方法 wrapper（转录 o2_inherited_method_wrapper.rg——
            // 子类 ..init.wrapper 闭包缝合安装，槽钥匙归声明类）
            Case("Method wrapper 继承方法命中（子类不 override）",
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(): TReturn {\n" +
                "        core.io.Console.println(\"trace\")\n" +
                "        return inner()\n" +
                "    }\n" +
                "}\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    @Trace()\n" +
                "    pub open func work(): i32 { return 1 }\n" +
                "}\n" +
                "pub class Child : Base {\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(\"${new Child().work()}\")\n" +
                "    return 0\n" +
                "}\n"),
            // ③a specific 双层 outer→inner 序（转录 BilVmTests
            // TestMethodWrapperDoubleLayerOrder）
            Case("Method wrapper specific 双层 outer→inner 序",
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
                "}\n"),
            // ③b specific 环绕 + inner 改参/改返回值（转录
            // TestMethodWrapperCallSpecificSurrounds）
            Case("Method wrapper specific 环绕改参",
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
                "}\n"),
            // 占位源 inner 结果经 extraSubst 落 i32 后静态 cast → .any：
            // CastLowering 留下 MirCast（非 BoxAny），native 须装箱而非
            // EmitFail（CastException: .i32 → .any）
            Case("Method wrapper specific inner 经 Any 往返",
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        var a: Any = inner(x)\n" +
                "        return (a as TReturn)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Timed()\n" +
                "    pub func fetch(x: i32): i32 { return (x * 2) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.fetch(21)\n" +
                "    if ((a != 42)) { return 1 }\n" +
                "    return 42\n" +
                "}\n"),
            // ④ wildcard .proxy.call：.name = 实现槽 canonical（转录
            // TestMethodWrapperWildcardInnerFullShape）
            Case("Method wrapper wildcard .name = 实现槽 canonical",
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        core.io.Console.println(\"name=\" + .name)\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub func fetch(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(41)\n" +
                "}\n"),
            // ④b wildcard 在双层链中间（specific 外环打包进 wildcard
            // 内环；转录改写自 TestWildcardInnerMiddleOfWrapperChain 的
            // Method 面）
            Case("Method wrapper specific→wildcard 混合链",
                "@WrapperTarget(.Method)\n" +
                "pub wrapper WOut {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        core.io.Console.println(\"out\")\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper WIn {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        core.io.Console.println(\"in:\" + .name)\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @WOut\n" +
                "    @WIn\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.ping(41)\n" +
                "}\n"),
            // ⑤ 静态方法经 companion（转录
            // TestMethodWrapperStaticViaCompanion；companion 实例 fn 被
            // trampoline，静态壳调它自然命中）
            Case("Method wrapper 静态方法经 companion",
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
                "}\n"),
            // ⑥ 全局函数经 ..globals.host（转录
            // TestGlobalMethodWrapperEndToEnd）
            Case("Method wrapper 全局函数经 globals.host",
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
                "}\n"),
            // ⑦ lambda 头 Method wrapper 经 invoke.indirect（转录
            // TestLambdaMethodWrapperEndToEnd 的环绕例 + wildcard 例的
            // .name 合成符号）
            Case("Method wrapper lambda 经 invoke.indirect",
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
                "pub func main(): i32 {\n" +
                "    var f = func{ @Timed (x: i32): i32 -> (x + 1) }\n" +
                "    return f(41)\n" +
                "}\n"),
            Case("Method wrapper lambda wildcard .name 合成符号",
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        core.io.Console.println(\"name=\" + .name)\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var fn = func{ @Timed (x: i32): i32 -> (x + 1) }\n" +
                "    return fn(41)\n" +
                "}\n"),
            // ⑧ 环内 wrapper 状态跨调用持久（转录
            // TestMethodWrapperStatePersists——刀3c 槽地址原地访问语义）
            Case("Method wrapper 环内状态跨调用持久",
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
                // 语言内校验（linux 退出码 8-bit 截断规避）：分步小码保诊断
                "    if ((a != 11)) { return 1 }\n" +
                "    if ((b != 12)) { return 2 }\n" +
                "    return 42\n" +
                "}\n"),
            // ⑧b 具名包乱序还原（遗留12 任务①，对齐 VM
            // UnboxNamedArgs）：wildcard 环模板调换包内 Pair 次序后经
            // inner 转发，链末按名还原——a/b 不得错位（VM 按名读取同
            // 口径；按位还原会得到 (b - a) = -7）
            Case("Method wrapper wildcard 具名包乱序按名还原",
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Swap {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        var p0 = (args[0] if? new Pair\\<String, Any>(\"\", (0 as Any)))\n" +
                "        var p1 = (args[1] if? new Pair\\<String, Any>(\"\", (0 as Any)))\n" +
                "        args[0] = p1\n" +
                "        args[1] = p0\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Swap\n" +
                "    pub func sub(a: i32, b: i32): i32 { return (a - b) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.sub(10, 3)\n" +
                "}\n"),
            // ⑧c super 绕过 Method wrapper 链（遗留12 任务②，转录
            // TestSuperBypassesMethodWrapper）：override 体内 super()
            // 直落基类原始实现，不触发基类方法键上的 wrapper——仅外层
            // 一次 trace（VM ResolveSuper 直接压帧同口径）
            Case("super 绕过 Method wrapper 链",
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(): TReturn {\n" +
                "        core.io.Console.println(\"trace\")\n" +
                "        return inner()\n" +
                "    }\n" +
                "}\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    @Trace()\n" +
                "    pub open func work(): i32 { return 1 }\n" +
                "}\n" +
                "pub class Child : Base {\n" +
                "    pub init()\n" +
                "    @Trace()\n" +
                "    pub override func work(): i32 { return (super() + 10) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Child().work()\n" +
                "}\n"),
            // ⑧d super 绕过 Entity wrapper 链（同口径）：基类 Entity
            // wrapper 的 .proxy.work 不得经 super 触发
            Case("super 绕过 Entity wrapper 链",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.work(): i32 {\n" +
                "        core.io.Console.println(\"trace\")\n" +
                "        return inner()\n" +
                "    }\n" +
                "}\n" +
                "@Trace()\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub open func work(): i32 { return 1 }\n" +
                "}\n" +
                "@Trace()\n" +
                "pub class Child : Base {\n" +
                "    pub init()\n" +
                "    pub override func work(): i32 { return (super() + 10) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Child().work()\n" +
                "}\n"),
            // ===== MW10 刀6b：Method wrapper wildcard 改写 .name
            // 重路由（VM ResolveInner/RerouteWildcardInner 同口径）=====
            // ⑨e 基本重路由命中：proxy 体覆写 .name 形参（前端/验证器
            // 放行——保留首参操作数名恒等、值可改写），hit/miss 分派
            // miss 进 $.mw.mwr router → other 原名 fn（未被烘焙）；VM
            // 侧帧符号改写后链末 InvokeResolved 同落点
            Case("Method wrapper wildcard 改写 .name 重路由命中",
                "import core.io.Console\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        .name = \"Service$other(x:.i32)@.i32\"\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub func fetch(x: i32): i32 { return (x + 1) }\n" +
                "    pub func other(x: i32): i32 { return (x + 100) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var r = s.fetch(1)\n" +
                "    Console.println(\"r=\" + r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // ⑨f miss 双侧口径：改写目标不存在——VM 链末
            // InvokeResolved 抛基础设施级 VmException「找不到 fn 定义」
            // （用户不可捕获）；native router 全不中抛
            // core.NoSuchMethodException「未路由的降级请求：」（Entity
            // router 同文案）——双侧 exit 1、stdout 一致，各自关键字
            FailCase("Method wrapper wildcard 改写 .name 未路由",
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        .name = \"Service$ghost(x:.i32)@.i32\"\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    @Timed\n" +
                "    pub func fetch(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(1)\n" +
                "}\n", "找不到 fn 定义", "未路由的降级请求"),

        };

    }
}
