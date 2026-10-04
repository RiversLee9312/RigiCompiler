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
        // 原编号 188..210 的有序用例；与主文件共享同一类型、字段及生命周期。
        private static (string Label, Action Run)[] CreateWrapperPlacesCases() => new (string Label, Action Run)[]
        {
            // MW10 刀2：字段-Value wrapper get/set 链（VM↔native 对拍，
            // 用例转录自 BilVmTests wrapper 段）
            Case("wrapper Value 局部双层链序（A.set→B.set→B.get→A.get）",
                "@WrapperTarget(.Value)\n" +
                "pub wrapper A {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        core.io.Console.println(\"A.get\")\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        core.io.Console.println(\"A.set\")\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Value)\n" +
                "pub wrapper B {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        core.io.Console.println(\"B.get\")\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        core.io.Console.println(\"B.set\")\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @A\n" +
                "    @B\n" +
                "    var x: i32 = 0\n" +
                "    x = 1\n" +
                "    var r = x\n" +
                "    return 0\n" +
                "}\n"),
            Case("wrapper Value 局部 Clamped 写夹取",
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub var max: i32\n" +
                "    pub init() {\n" +
                "        min = 0\n" +
                "        max = 100\n" +
                "    }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        var v = (value as i32)\n" +
                "        if ((v > max)) { v = max }\n" +
                "        if ((v < min)) { v = min }\n" +
                "        inner((v as TValue))\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @Clamped\n" +
                "    var health: i32 = 50\n" +
                "    health = 200\n" +
                "    var a = health\n" +
                "    health = -20\n" +
                "    var b = health\n" +
                "    if (((a == 100) and (b == 0))) { return 0 }\n" +
                "    return 1\n" +
                "}\n"),
            Case("wrapper Value get-only 修饰 const 局部可读",
                "@WrapperTarget(.Value)\n" +
                "pub wrapper ReadOnly {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        return (((value as i32) + 1) as TValue)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @ReadOnly\n" +
                "    const y: i32 = 1\n" +
                "    var v = y\n" +
                "    return v\n" +
                "}\n"),
            Case("wrapper Value 实例字段读写",
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var max: i32\n" +
                "    pub init() { max = 100 }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        var v = (value as i32)\n" +
                "        if ((v > max)) { v = max }\n" +
                "        inner((v as TValue))\n" +
                "    }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Clamped\n" +
                "    pub var hp: i32 = 0\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h = new Hero()\n" +
                "    h.hp = 200\n" +
                "    return h.hp\n" +
                "}\n"),
            Case("wrapper Value 写序 wrapper.set→user.set",
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Shift {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        core.io.Console.println(\"wrapper.set\")\n" +
                "        var v = (value as i32)\n" +
                "        inner(((v + 10) as TValue))\n" +
                "    }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Shift()\n" +
                "    pub var hp: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) {\n" +
                "            core.io.Console.println(\"user.set\")\n" +
                "            value = value * 2\n" +
                "        }\n" +
                "    } = 0\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h = new Hero()\n" +
                "    h.hp = 5\n" +
                "    return h.hp\n" +
                "}\n"),
            Case("wrapper Value 读序 user.get→wrapper.get",
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Shift {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        core.io.Console.println(\"wrapper.get\")\n" +
                "        var v = (value as i32)\n" +
                "        return ((v + 1) as TValue)\n" +
                "    }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Shift()\n" +
                "    pub var hp: i32 {\n" +
                "        pub get(value: _) {\n" +
                "            core.io.Console.println(\"user.get\")\n" +
                "            return value * 2\n" +
                "        }\n" +
                "        pub set(value: _) { }\n" +
                "    } = 10\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Hero().hp\n" +
                "}\n"),
            Case("wrapper Value init 写豁免经用户 setter",
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
                "}\n"),
            // MW10 刀3b：Entity 字段 get/set proxy 链（VM↔native 对拍，
            // 用例转录自 BilVmTests/BilVmStressTests wrapper 段与
            // Tests/e2e/rigi/o7_base_init_wrapper_installed.rg）
            Case("wrapper Entity o7 基类 init 读命中 get.*",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audit {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.*\\<TValue>(symbol: String, value: TValue): TValue {\n" +
                "        core.io.Console.println(\"audit\")\n" +
                "        return value\n" +
                "    }\n" +
                "}\n" +
                "@Audit()\n" +
                "pub open class Base {\n" +
                "    pub var hp: i32 = 10\n" +
                "    pub init() { hp = (this.hp + 1) }\n" +
                "}\n" +
                "@Audit()\n" +
                "pub class Hero : Base {\n" +
                "    pub init() { super() }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return new Hero().hp\n" +
                "}\n"),
            // specific get/set 各绕一层（写 s、读 g 打印序 + 字段值生效）。
            // 注：BilVmTests TestEntityGetterSetterProxyCounts 的计数器形态
            // 依赖 wrapper 实例状态持久——native 隐藏槽是内联值拷贝 ABI
            //（get.wrapper 拷贝出槽，环内写 .this 字段不落回），方法面
            // 刀1 起同病，属既有缺口非本刀引入；此处以打印序对拍同等语义
            Case("wrapper Entity specific get/set 各绕一层",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Echo {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.name\\<TField>(value: TField): TField {\n" +
                "        core.io.Console.println(\"g\")\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set.name\\<TField>(value: TField) {\n" +
                "        core.io.Console.println(\"s\")\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "@Echo\n" +
                "pub class Service {\n" +
                "    pub var name: String\n" +
                "    pub init() { name = \"a\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    s.name = \"b\"\n" +
                "    var n = s.name\n" +
                "    if (n == \"b\") { return 1 } else { return 0 }\n" +
                "}\n"),
            Case("wrapper Entity wildcard get.* 收符号变值",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audited {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.*\\<TValue>(symbol: String, value: TValue): TValue {\n" +
                "        core.io.Console.println(\"get:\" + symbol)\n" +
                "        return value\n" +
                "    }\n" +
                "}\n" +
                "@Audited\n" +
                "pub class Service {\n" +
                "    pub var name: String\n" +
                "    pub init() { name = \"a\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var n = s.name\n" +
                "    return 0\n" +
                "}\n"),
            Case("wrapper Entity wildcard set.* 落原始写",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audited {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        core.io.Console.println(\"set:\" + symbol)\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@Audited\n" +
                "pub class Service {\n" +
                "    pub var name: String\n" +
                "    pub init() { name = \"a\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    s.name = \"b\"\n" +
                "    if (s.name == \"b\") { return 1 } else { return 0 }\n" +
                "}\n"),
            // MW10 遗留⑧回归：访问器调用被 .proxy.* 拦截重路由为 Set 链
            //（胖值 ABI），wildcard set 环 inner 改写 symbol 后链末落带用户
            // setter 的字段——修前 VM 未拆 VmAny 装箱，setter 内 == 抛
            //「没有用户 operator equals：.any」；修后双端一致落 setter 写
            // backing（改写只影响后续环查找，链末写目标双端同为原字段 hp）
            Case("wrapper set 环 inner 改写 symbol 重路由落用户 setter",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audit {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        if (symbol == \"Entity#hp@.i32\") {\n" +
                "            symbol = \"Entity#hp2@.i32\"\n" +
                "        }\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@Audit()\n" +
                "pub class Entity {\n" +
                "    pub var hp: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) {\n" +
                "            if (value == 10) { core.io.Console.println(\"hp setter sees 10\") }\n" +
                "        }\n" +
                "    } = 10\n" +
                "    pub var hp2: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) {\n" +
                "            if (value == 10) { core.io.Console.println(\"hp2 setter sees 10\") }\n" +
                "        }\n" +
                "    } = 0\n" +
                "    pub init() {}\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const e = new Entity()\n" +
                "    core.io.Console.println(\"hp=${e.hp} hp2=${e.hp2}\")\n" +
                "    return 0\n" +
                "}\n"),
            // MW10 遗留④：wildcard set 环 inner 改写 symbol 到另一无 proxy
            // 字段——剩余层无环可进，VM RerouteWildcardInner 只改 MemberName、
            // FieldSymbol 恒为原字段，链末落**原字段**的 setter/backing
            Case("wrapper set 环 inner 改写 symbol 到无 proxy 字段落原字段终态",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audit {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        if (symbol == \"Entity#hp@.i32\") {\n" +
                "            symbol = \"Entity#mp@.i32\"\n" +
                "        }\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@Audit()\n" +
                "pub class Entity {\n" +
                "    pub var hp: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) {\n" +
                "            if (value == 7) { core.io.Console.println(\"hp setter 7\") }\n" +
                "        }\n" +
                "    } = 0\n" +
                "    pub var mp: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) {\n" +
                "            if (value == 7) { core.io.Console.println(\"mp setter 7\") }\n" +
                "        }\n" +
                "    } = 0\n" +
                "    pub init() {}\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const e = new Entity()\n" +
                "    e.hp = 7\n" +
                "    core.io.Console.println(\"hp=${e.hp} mp=${e.mp}\")\n" +
                "    return 0\n" +
                "}\n"),
            // MW10 遗留④：改写到有 specific set proxy 的字段——剩余层命中
            // .proxy.set.mp 进新字段环（打印 WB.mp）；环 inner 到底后 VM
            // 链末仍写原字段 hp（FieldSymbol 不随改写变）
            Case("wrapper set 环 inner 改写 symbol 进他字段 specific 环",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WA {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        if (symbol == \"Entity#hp@.i32\") {\n" +
                "            symbol = \"Entity#mp@.i32\"\n" +
                "        }\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WB {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.mp\\<TField>(value: TField) {\n" +
                "        core.io.Console.println(\"WB.mp\")\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "@WA\n" +
                "@WB\n" +
                "pub class Entity {\n" +
                "    pub var hp: i32 = 0\n" +
                "    pub var mp: i32 = 0\n" +
                "    pub init() {}\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const e = new Entity()\n" +
                "    e.hp = 7\n" +
                "    core.io.Console.println(\"hp=${e.hp} mp=${e.mp}\")\n" +
                "    e.mp = 9\n" +
                "    core.io.Console.println(\"hp=${e.hp} mp=${e.mp}\")\n" +
                "    return 0\n" +
                "}\n"),
            // MW10 遗留④：三层 set 环，中间层（WB，layer 1）改写 hp→mp，
            // 重路由自 layer 2 起命中 WC 的 .proxy.set.mp（层序正确：
            // WA identity → WB 改写 → WC.mp 接管），链末落原字段 hp
            Case("wrapper set 环内层改写 symbol 剩余层序",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WA {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        core.io.Console.println(\"WA\")\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WB {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        core.io.Console.println(\"WB\")\n" +
                "        if (symbol == \"Entity#hp@.i32\") {\n" +
                "            symbol = \"Entity#mp@.i32\"\n" +
                "        }\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WC {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.mp\\<TField>(value: TField) {\n" +
                "        core.io.Console.println(\"WC.mp\")\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "@WA\n" +
                "@WB\n" +
                "@WC\n" +
                "pub class Entity {\n" +
                "    pub var hp: i32 = 0\n" +
                "    pub var mp: i32 = 0\n" +
                "    pub init() {}\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const e = new Entity()\n" +
                "    e.hp = 7\n" +
                "    core.io.Console.println(\"hp=${e.hp} mp=${e.mp}\")\n" +
                "    e.mp = 9\n" +
                "    core.io.Console.println(\"hp=${e.hp} mp=${e.mp}\")\n" +
                "    return 0\n" +
                "}\n"),
            // MW10 遗留④：改写到可解析但不存在的字段名——VM 按
            // FieldSimpleName 查环无果（剩余层无 wildcard），链末落
            // 原字段终态（不抛异常；FieldSymbol 恒为原字段）
            Case("wrapper set 环 inner 改写 symbol 未知名落原字段终态",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audit {\n" +
                "    pub init()\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        if (symbol == \"Entity#hp@.i32\") {\n" +
                "            symbol = \"Entity#ghost@.i32\"\n" +
                "        }\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@Audit()\n" +
                "pub class Entity {\n" +
                "    pub var hp: i32 = 0\n" +
                "    pub init() {}\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const e = new Entity()\n" +
                "    e.hp = 7\n" +
                "    core.io.Console.println(\"hp=${e.hp}\")\n" +
                "    return 0\n" +
                "}\n"),
            Case("wrapper Entity 双层字段链写序 outer→inner",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WO {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.*\\<TValue>(symbol: String, value: TValue): TValue {\n" +
                "        core.io.Console.println(\"O.get\")\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        core.io.Console.println(\"O.set\")\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WI {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.*\\<TValue>(symbol: String, value: TValue): TValue {\n" +
                "        core.io.Console.println(\"I.get\")\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) {\n" +
                "        core.io.Console.println(\"I.set\")\n" +
                "        inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@WO\n" +
                "@WI\n" +
                "pub class Service {\n" +
                "    pub var name: String\n" +
                "    pub init() { name = \"a\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    s.name = \"b\"\n" +
                "    var n = s.name\n" +
                "    if (n == \"b\") { return 1 } else { return 0 }\n" +
                "}\n"),
            // ===== 遗1：用户运算符 native 分派（VM 为基准逐类对拍） =====
            Case("用户运算符 算术/比较/一元/复合赋值",
                "class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator plus(another: Vec): Vec { return new Vec((x + another.x)) }\n" +
                "    pub operator minus(another: Vec): Vec { return new Vec((x - another.x)) }\n" +
                "    pub operator equals(another: Vec): bool { return (x == another.x) }\n" +
                "    pub operator compareTo(another: Vec): ComparisonResult {\n" +
                "        if ((x < another.x)) { return .LesserThanAnother }\n" +
                "        if ((x > another.x)) { return .GreaterThanAnother }\n" +
                "        return .Equal\n" +
                "    }\n" +
                "    pub operator opposite(): Vec { return new Vec((0 - x)) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(1)\n" +
                "    var b = new Vec(2)\n" +
                "    var n = 0\n" +
                "    if (((a + b).x == 3)) { n = (n + 1) }\n" +
                "    if (((b - a).x == 1)) { n = (n + 2) }\n" +
                "    if ((a == new Vec(1))) { n = (n + 4) }\n" +
                "    if ((a != b)) { n = (n + 8) }\n" +
                "    if ((a < b)) { n = (n + 16) }\n" +
                "    if ((a <= new Vec(1))) { n = (n + 32) }\n" +
                "    if ((b > a)) { n = (n + 64) }\n" +
                "    if ((b >= b)) { n = (n + 128) }\n" +
                "    if (((-a).x == (0 - 1))) { n = (n + 256) }\n" +
                "    var c = new Vec(10)\n" +
                "    c += b\n" +
                "    if ((c.x == 12)) { n = (n + 512) }\n" +
                "    if ((b < a)) { n = (n + 1024) }\n" +
                "    core.io.Console.println(n.toString())\n" +
                // 语言内校验（linux 退出码 8-bit 截断规避）：位标累计值整体比对
                "    if ((n != 1023)) { return 1 }\n" +
                "    return 42\n" +
                "}\n"),
            // 混合类型操作数（重载按右操作数形参可赋匹配）+ 继承下探
            //（子类未定义运算符时沿 extends 链命中基类实现）
            Case("用户运算符 混合类型操作数与继承下探",
                "class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator plus(another: Vec): Vec { return new Vec((x + another.x)) }\n" +
                "    pub operator plus(n: i32): Vec { return new Vec((x + n)) }\n" +
                "}\n" +
                "open class Animal {\n" +
                "    pub var legs: i32\n" +
                "    pub init(_ -> legs) { }\n" +
                "    pub operator equals(other: Animal): bool { return (legs == other.legs) }\n" +
                "}\n" +
                "class Dog : Animal {\n" +
                "    pub init(n: i32) { super(n) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(1)\n" +
                "    var m = a + 10\n" +
                "    var n = 0\n" +
                "    if ((m.x == 11)) { n = (n + 1) }\n" +
                "    var v = a + new Vec(2)\n" +
                "    if ((v.x == 3)) { n = (n + 2) }\n" +
                "    var d1: Animal = new Dog(4)\n" +
                "    var d2: Animal = new Dog(4)\n" +
                "    var d3: Animal = new Dog(3)\n" +
                "    if ((d1 == d2)) { n = (n + 4) }\n" +
                "    if ((d1 != d3)) { n = (n + 8) }\n" +
                "    var e1 = new Dog(4)\n" +
                "    var e2 = new Dog(4)\n" +
                "    if ((e1 == e2)) { n = (n + 16) }\n" +
                "    core.io.Console.println(n.toString())\n" +
                "    return n\n" +
                "}\n"),
            // shadow 派发：operator 不可 override 但可同名再定义（静默
            // hiding）；VM 按左操作数实际类型沿派生链命中最具体实现，
            // native 经 vtable 槽覆盖对齐（Derived$$equals → false → 0）
            Case("用户运算符 shadow 实际类型派发",
                "open class Base {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator equals(other: Base): bool { return (x == other.x) }\n" +
                "}\n" +
                "class Derived : Base {\n" +
                "    pub init(n: i32) { super(n) }\n" +
                "    pub operator equals(other: Base): bool { return false }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a: Base = new Derived(1)\n" +
                "    var b: Base = new Base(1)\n" +
                "    var r = if ((a == b)) { return@_ 1 } else { return@_ 0 }\n" +
                "    core.io.Console.println(r.toString())\n" +
                "    return r\n" +
                "}\n"),
            // 遗1 刀3a 首次行为对拍：Entity wrapper 的 specific
            // .proxy.opr.plus 拦截 + inner 落原始体 + 状态跨调用持久
            //（转录 BilVmTests.TestEntityProxyStatePersists 形状到运算符）
            Case("wrapper 运算符 specific proxy 链",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Counting {\n" +
                "    pub var hits: i32\n" +
                "    pub init() { hits = 0 }\n" +
                "    operator .proxy.opr.plus(another: Vec): Vec {\n" +
                "        hits = (hits + 1)\n" +
                "        var r = inner(another)\n" +
                "        return new Vec((r.x + 100))\n" +
                "    }\n" +
                "}\n" +
                "@Counting\n" +
                "pub class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator plus(another: Vec): Vec { return new Vec((x + another.x)) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(1)\n" +
                "    var b = new Vec(2)\n" +
                "    var c = a + b\n" +
                "    var d = a + b\n" +
                "    var n = ((c.x + a:Counting.hits))\n" +
                "    core.io.Console.println(n.toString())\n" +
                "    return n\n" +
                "}\n"),
            // wildcard .proxy.opr.* 拦截（转录 BilVmTests:1828
            // WrappedVecOperatorProxyModule 的源码级形态）：proxy 直接
            // 返回 99，原始 plus 不执行
            Case("wrapper 运算符 wildcard proxy 拦截",
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    pub init()\n" +
                "    operator .proxy.opr.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String,\n" +
                "        namedArgs: named TNamedArgs...,\n" +
                "        unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        return (new Vec(99) as TReturn)\n" +
                "    }\n" +
                "}\n" +
                "@W\n" +
                "pub class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator plus(another: Vec): Vec { return new Vec((x + another.x)) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(1)\n" +
                "    var b = new Vec(2)\n" +
                "    var c = a + b\n" +
                "    core.io.Console.println(c.x.toString())\n" +
                "    return c.x\n" +
                "}\n"),
            Case("位置值包 0/1/3 实参",
                "import core.io.Console\n" +
                "func sum(nums: i32...): i32 {\n" +
                "    var total = 0\n" +
                "    var i = 0\n" +
                "    while (i < nums.length) {\n" +
                "        total = total + (nums[i] if? 0)\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    return total\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    if (sum() == 0) { Console.println(\"empty\") }\n" +
                "    if (sum(7) == 7) { Console.println(\"one\") }\n" +
                "    if (sum(1, 2, 3) == 6) { Console.println(\"three\") }\n" +
                "    return 0\n" +
                "}\n"),

        };

    }
}
