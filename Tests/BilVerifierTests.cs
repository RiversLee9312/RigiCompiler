using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// BIL 验证器（BilVerifier，M58）测试：
    /// 正例——中端全管线产出（覆盖 M44–M54 各发射特性）必须验证器零错误；
    /// 负例——手工构造/改造非法模块，按 §21 规则逐类断言命中。
    /// S8c 增补：§13.6 索引严格三元组查询（get.array/set.array 手工模块
    /// 基线正例 + 索引/元素/结果类型不符与无索引运算符负例）。
    /// S8e 增补：访问器与 override 全管线正例 + §21.8 访问器声明负例
    /// （getter(FIELD) 引用未声明字段 / getter 修饰配非 $.get. 形态方法
    /// 符号 / backing 与 computed 共存，手工模块基例）。
    /// M64 增补：§18 hint 指令（string 资源正例 + 非 string 资源 /
    /// 模块外资源负例）。
    /// M75 增补（S11 规范定稿落地）：§12.3 type.is.case（case 符号/
    /// 操作数类型/结果 bool 负例 + case 声明宿主与判别值资源负例）与
    /// §13.3 嵌套字段访问（宿主字段不可解析/非隐藏字段名/宿主类型非
    /// wrapper/内层字段不可解析/静态内层/get·set 类型/宿主对象/const
    /// 写入负例，手工模块基线正例）。
    /// 验证器修复批次增补：namespaced 全局函数 invoke（owner 以 "::"
    /// 结尾无 receiver）、普通参数+值包混合调用（§7.2 值包在普通参数
    /// 之后）、loop.rev DA（§16.4 body 至少一次）、try-finally 无 catch
    /// 判终止、同名不同元数类型共存反查（符号+元数键，手工模块）、
    /// 跨 fn 越权块类型检查不级联、保留名家族空余部/非法字符负例。
    /// const 发射开闸批次增补：§21.8 init 豁免（init 体内写实例 const
    /// 字段正例——set.field 与 set.wrapper.field 双形态；普通方法写入
    /// 与 init 内静态写入反例，手工模块）。
    /// TypesCompatible canonical 全等收紧批次增补：内建标量别名 ↔
    /// canonical 与标准构造头 ↔ canonical 泛型宿主（含无边界 .typeid ≡
    /// .typeid<.any>、.pair ↔ core::Pair 嵌套）正例；构造类型实参不同/
    /// 同名不同命名空间/嵌套构造实参不同/同名不同元数负例（手工模块，
    /// set.var 两端比对）。
    /// §21.8 enum struct 实例字段 init 全路径 set.field（§14.3 无零值）：
    /// 正例 5（单路径 / if 双分支 / loop.rev / try-finally / 派生写基类字段）
    /// + 负例 5（未写 / if 单分支 / 正向 loop / set 前 get / 另一 init 重载未写）
    /// + 回归 2（非 enum 字段不受约束、enum 局部仍走 DA）。
    /// </summary>
    public static class BilVerifierTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestHarness.Section("BilVerifier");

            // ===== 正例：全管线产出验证器零错误 =====
            Positive("hello world",
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(\"Hello, world!\")\n" +
                "    return 0\n" +
                "}\n");
            Positive("局部声明/赋值/运算",
                "pub func main(): i32 {\n" +
                "    var x: i32 = 1 + 2\n" +
                "    x = x * 3\n" +
                "    return x\n" +
                "}\n");
            Positive("带返回值 invoke 与 new",
                "pub func double(a: i32): i32 { return a * 2 }\n" +
                "pub func main(): i32 {\n" +
                "    var d = double(21)\n" +
                "    return d\n" +
                "}\n");
            TestAwaitInstructions();
            TestYieldInstructions();
            TestIndirectInvokeShapes();
            Positive("实例成员（this/get.field/set.field/实例 invoke）",
                "pub class Counter {\n" +
                "    pub var value: i32\n" +
                "    pub init(v: i32) { value = v }\n" +
                "    pub func add(n: i32): i32 { return value + n }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Counter(1)\n" +
                "    return c.add(2)\n" +
                "}\n");
            Positive("if/值块/短路",
                "pub func main(): i32 {\n" +
                "    var x: i32 = 1\n" +
                "    if ((x > 0) and (x < 10)) { x = x + 1 } else { x = x - 1 }\n" +
                "    return x\n" +
                "}\n");
            Positive("while/break/continue",
                "pub func main(): i32 {\n" +
                "    var i: i32 = 0\n" +
                "    while (i < 10) {\n" +
                "        i = i + 1\n" +
                "        if (i == 5) { continue }\n" +
                "        if (i == 9) { break }\n" +
                "    }\n" +
                "    return i\n" +
                "}\n");
            Positive("do-while",
                "pub func main(): i32 {\n" +
                "    var i: i32 = 0\n" +
                "    do { i = i + 1 } while (i < 3)\n" +
                "    return i\n" +
                "}\n");
            Positive("for（迭代协议）",
                "pub func main(): i32 {\n" +
                "    var sum: i32 = 0\n" +
                "    for (i in 0 to 3) { sum = sum + i }\n" +
                "    return sum\n" +
                "}\n");
            Positive("常量 switch",
                "pub func main(): i32 {\n" +
                "    var x: i32 = 2\n" +
                "    switch (x) {\n" +
                "        (1) -> { return 10 }\n" +
                "        (2) -> { return 20 }\n" +
                "        default -> { return 30 }\n" +
                "    }\n" +
                "}\n");
            Positive("throw 与 try/catch/finally",
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new core.RuntimeException(\"boom\")\n" +
                "    } catch (e: core.Exception) {\n" +
                "        return 1\n" +
                "    } finally (f) {\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            Positive("enum 局部变量 DA（读前已写）",
                "pub enum struct Color {}[Red, Blue]\n" +
                "pub func main(): i32 {\n" +
                "    var c: Color = .Red\n" +
                "    return 0\n" +
                "}\n");
            Positive("enum struct 实例字段 init 单路径 set.field（全管线）",
                "pub enum struct Color {}[Red, Blue]\n" +
                "pub class Flag {\n" +
                "    pub var kind: Color\n" +
                "    pub init(k: Color) { kind = k }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = new Flag(.Red)\n" +
                "    return 0\n" +
                "}\n");
            Positive("enum struct 实例字段 init if 双分支 set.field（全管线）",
                "pub enum struct Color {}[Red, Blue]\n" +
                "pub class Flag {\n" +
                "    pub var kind: Color\n" +
                "    pub init(k: Color, alt: Color, which: bool) {\n" +
                "        if (which) { kind = k } else { kind = alt }\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = new Flag(.Red, .Blue, true)\n" +
                "    return 0\n" +
                "}\n");
            Positive("enum struct 实例字段 init loop.rev set.field（全管线）",
                "pub enum struct Color {}[Red, Blue]\n" +
                "pub class Flag {\n" +
                "    pub var kind: Color\n" +
                "    pub init(k: Color) {\n" +
                "        do { kind = k } while (false)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = new Flag(.Red)\n" +
                "    return 0\n" +
                "}\n");
            Positive("enum struct 实例字段 init loop.rev 赋值后 break（全管线）",
                "pub enum struct Color {}[Red, Blue]\n" +
                "pub class Flag {\n" +
                "    pub var kind: Color\n" +
                "    pub init(k: Color, skip: bool) {\n" +
                "        do { kind = k\n            if (skip) { break } } while (false)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = new Flag(.Red, false)\n" +
                "    return 0\n" +
                "}\n");
            Positive("enum struct 实例字段 init try-finally set.field（全管线）",
                "pub enum struct Color {}[Red, Blue]\n" +
                "pub class Flag {\n" +
                "    pub var kind: Color\n" +
                "    pub init(k: Color) {\n" +
                "        try { var n: i32 = 0 } finally (e) { kind = k }\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = new Flag(.Red)\n" +
                "    return 0\n" +
                "}\n");
            Positive("派生 init 设置基类 enum struct 字段（全管线）",
                "pub enum struct Color {}[Red, Blue]\n" +
                "pub open class Base {\n" +
                "    pub var kind: Color\n" +
                "    pub init(k: Color) { kind = k }\n" +
                "}\n" +
                "pub class Derived : Base {\n" +
                "    pub init(k: Color) {\n" +
                "        super(k)\n" +
                "        kind = k\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var d = new Derived(.Red)\n" +
                "    return 0\n" +
                "}\n");
            Positive("非 enum 实例字段 init 不强制 set.field（回归）",
                "pub class Box {\n" +
                "    pub var n: i32 = 0\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box()\n" +
                "    return 0\n" +
                "}\n");
            Positive("cast/is/typeOf",
                "pub func main(): i32 {\n" +
                "    var a: Any = 1\n" +
                "    var b: i32 = 0\n" +
                "    if (a is i32) { b = a as i32 }\n" +
                "    var t = typeOf(a)\n" +
                "    return b\n" +
                "}\n");
            Positive("字符串插值",
                "pub func main(): i32 {\n" +
                "    var x: i32 = 42\n" +
                "    core.io.Console.println(\"x = ${x}\")\n" +
                "    return 0\n" +
                "}\n");
            Positive("?. 安全调用",
                "pub class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "pub func f(u: User?): String? { return u?.name }\n" +
                "pub func main(): i32 { return 0 }\n");
            Positive("if? 空值回退",
                "pub class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "pub func g(u: User?): User { return u if? new User(\"anon\") }\n" +
                "pub func main(): i32 { return 0 }\n");
            Positive("解构声明",
                "class Entry : core.Pair\\<String, i32> {\n" +
                "    pub init(k: String, v: i32) {\n        key = k\n        value = v\n    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var (k, v) = new Entry(\"a\", 1)\n" +
                "    return v\n" +
                "}\n");
            Positive("索引访问（get.array/set.array，S8c）",
                "pub class Bag {\n" +
                "    pub var item: i32\n" +
                "    pub init() { item = 0 }\n" +
                "    pub operator getAtIndex(index: i32): i32? { return item }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { item = element }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Bag()\n" +
                "    b[0] = 7\n" +
                "    b[1] = ((b[1] if? 0) + 2)\n" +
                "    return b[2] if? 0\n" +
                "}\n");
            Positive("访问器（backing/computed/全局自动，S8e）",
                "namespace app\n" +
                "pub var height: i32 {\n" +
                "    pub get\n" +
                "    pub set\n" +
                "} = 200\n" +
                "pub class Counter {\n" +
                "    pub var value: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { }\n" +
                "    }\n" +
                "    pub var doubled: i32 {\n" +
                "        pub get(_: _) { return value + value }\n" +
                "    }\n" +
                "    pub init() { value = 0 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Counter()\n" +
                "    c.value = 1\n" +
                "    return (c.value + height) + c.doubled\n" +
                "}\n");
            Positive("override/abstract 投影（S8e）",
                "pub open class Base {\n" +
                "    pub open func area(): i32 { return 0 }\n" +
                "}\n" +
                "pub class Square : Base {\n" +
                "    pub override func area(): i32 { return 1 }\n" +
                "}\n" +
                "pub abstract class Concept {\n" +
                "    pub abstract func id(): i32\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Square()\n" +
                "    return s.area()\n" +
                "}\n");
            // 验证器修复批次：invoke 的 owner 段以 "::" 结尾是命名空间前缀
            // （全局函数），无 receiver——首实参不得被当 .this 吞掉
            Positive("namespaced 全局函数调用（invoke owner 命名空间前缀）",
                "import core.coroutine.*\n" +
                "pub func main(): i32 {\n" +
                "    var alarm = sleep(1000)\n" +
                "    return 0\n" +
                "}\n");
            // 验证器修复批次：§7.2 调用序——值包（.vargs/.kwargs）在普通
            // 参数之后逐条比对，只有 .generic.* 前导跳过
            Positive("普通参数 + 值包混合调用（§7.2 调用序）",
                "func sum(first: i32, rest: i32...): i32 { return first }\n" +
                "func config(name: String, options: named i32...): i32 { return 0 }\n" +
                "pub func main(): i32 {\n" +
                "    return sum(1, 2, 3) + config(\"a\", x = 1, y = 2)\n" +
                "}\n");
            // 验证器修复批次：§16.4 loop.rev 执行序 body → judge → condition，
            // body 至少执行一次——judge 与循环出口以 body 出口态分析
            Positive("do-while 体内赋值循环后可见（loop.rev DA）",
                "pub func main(): i32 {\n" +
                "    var x: i32\n" +
                "    do { x = 1 } while (x < 5)\n" +
                "    return x\n" +
                "}\n");
            // 验证器修复批次：try-finally 无 catch 时 catch-table 为空，
            // body 终止即判终止
            Positive("try-finally 无 catch 判终止（entrypoint 结构化）",
                "func f(): i32 {\n" +
                "    try { return 1 } finally (e) { core.io.Console.println(\"f\") }\n" +
                "}\n" +
                "pub func main(): i32 { return f() }\n");

            // ===== S11：§12.3 type.is.case / §13.3 嵌套字段访问（手工模块）=====
            // P3 尚未发射（is .Case 与 wrapper place 归 S11），故不走全管线
            // 正例；以手工模块断言指令形态与验证器规则。宿主 Service 带
            // Logged wrapper 隐藏字段（§5.3 命名）、RequestResult enum + 两
            // case（Failed 判别值资源 R_FC）；main(svc, e) 参数入口已赋值
            // 基线：is.case + set.wrapper.field；lv 经 S11Module 内 R_LV load 已赋值
            BilTestHarness.CheckBilValid("S11 手工模块（基线，is.case + set.wrapper.field 正例）",
                S11Module(
                    new IsCaseInstruction(BilOp.Var("e"),
                        BilOp.Case("com.example::RequestResult.Failed"), BilOp.Var("b")),
                    new SetWrapperFieldInstruction(BilOp.Var("lv"), BilOp.Var("svc"),
                        BilOp.Wrapper("core.logging::Logged"),
                        BilOp.Field("core.logging::Logged#level@.string"))));
            BilTestHarness.CheckBilValid("S11 get.wrapper.field 正例",
                FieldValueModule(
                    new GetWrapperFieldInstruction(BilOp.Var("hero"),
                        BilOp.Field("com.example::Hero#hp@.i32"),
                        BilOp.Type("core.clamp::Clamped"), BilOp.Var("w"))));
            BilTestHarness.CheckBilValid("..super 手工模块（override 正例）",
                SuperInvokeModule(validReceiver: true));
            BilTestHarness.CheckBilInvalid("..super 缺 $.this 首参",
                SuperInvokeModule(validReceiver: false), "首实参必须精确为 $.this");

            // ===== V3：indirect 全家 + getid.field 手工模块 =====
            TestV3IndirectForms();

            // ===== M109a：..init.wrapper / new.wrapped / new.wrapper.* / companion =====
            TestInitWrapperAndNewWrapped();

            // ===== 负例：非法模块按规则命中 =====
            NegativeCases();
            TestBitwiseTypeRestriction();
            TestRegionBreakIdBinding();
            TestEnumStructInstanceFieldInit();

            return TestHarness.Summary("BilVerifier");
        }

        private static void TestInitWrapperAndNewWrapped()
        {
            TestHarness.Section("BilVerifier M109a wrapper init");
            BilTestHarness.CheckBilValid("..init.wrapper + new.wrapper.entity 正例",
                InitWrapperModule(includeEntity: true));
            BilTestHarness.CheckBilValid("new.wrapped 正例（有参 ..init.wrapper）",
                NewWrappedHostModule());
            BilTestHarness.CheckBilValid("..companion 结构正例",
                CompanionModule());

            BilTestHarness.CheckBilInvalid("new.wrapper.entity 出现在非 ..init.wrapper",
                OrdinaryFnWithNewWrapperEntityModule(), "仅允许在 ..init.wrapper");
            BilTestHarness.CheckBilInvalid("new.wrapper.field 出现在非 ..init.wrapper",
                OrdinaryFnWithNewWrapperFieldModule(), "仅允许在 ..init.wrapper");
            BilTestHarness.CheckBilInvalid("new.wrapper.method 出现在非 ..init.wrapper",
                OrdinaryFnWithNewWrapperMethodModule(), "仅允许在 ..init.wrapper");

            BilTestHarness.CheckBilInvalid("..init.wrapper 非 void",
                InitWrapperBadReturnModule(), "返回 .void");
            BilTestHarness.CheckBilInvalid("..init.wrapper 缺 priv/compiler-generated",
                InitWrapperMissingModifiersModule(), "priv 与 compiler-generated");
            BilTestHarness.CheckBilInvalid("同一实体两个 ..init.wrapper",
                DuplicateInitWrapperModule(), "至多一个");

            BilTestHarness.CheckBilInvalid("有参 ..init.wrapper 上普通 new",
                NewOnParameterizedInitWrapperModule(), "必须使用 new.wrapped");
            BilTestHarness.CheckBilInvalid("无参 ..init.wrapper 上 new.wrapped",
                NewWrappedOnParameterlessModule(), "禁止 new.wrapped");
            BilTestHarness.CheckBilInvalid("new.wrapped wrapper 前缀类型不符",
                NewWrappedBadWrapperArgsModule(), "wrapper 前缀实参不匹配");

            BilTestHarness.CheckBilInvalid("..companion 非 singleton",
                CompanionNotSingletonModule(), "singleton 与 shared");
            BilTestHarness.CheckBilInvalid("..companion 无实例方法",
                CompanionNoMethodModule(), "至少有一个实例方法");
        }

        private static void TestV3IndirectForms()
        {
            TestHarness.Section("BilVerifier V3 indirect");
            BilTestHarness.CheckBilValid("cast.indirect 正例",
                V3IndirectModule(
                    new CastIndirectInstruction(BilOp.Var("obj"), BilOp.Var("casted"),
                        BilOp.Var("tid"), isSafe: false)));
            BilTestHarness.CheckBilValid("cast.safe.indirect 正例",
                V3IndirectModule(
                    new CastIndirectInstruction(BilOp.Var("obj"), BilOp.Var("safe"),
                        BilOp.Var("tid"), isSafe: true)));
            BilTestHarness.CheckBilValid("get.wrapper.indirect 正例",
                V3IndirectModule(
                    new GetWrapperIndirectInstruction(BilOp.Var("obj"), BilOp.Var("wid"),
                        BilOp.Var("w"))));
            BilTestHarness.CheckBilValid("getid.field 正例",
                V3IndirectModule(
                    new GetIdFieldInstruction(BilOp.Field("Box#n@.i32"), BilOp.Var("fid"))));
            BilTestHarness.CheckBilValid("get/set.field.indirect 正例",
                V3IndirectModule(
                    new SetFieldIndirectInstruction(BilOp.Var("x"), BilOp.Var("obj"),
                        BilOp.Var("fid")),
                    new GetFieldIndirectInstruction(BilOp.Var("obj"), BilOp.Var("x"),
                        BilOp.Var("fid"))));
            BilTestHarness.CheckBilValid("get/set.field.static.indirect 正例",
                V3IndirectModule(
                    new SetFieldStaticIndirectInstruction(BilOp.Var("x"), BilOp.Var("tid"),
                        BilOp.Var("sfid")),
                    new GetFieldStaticIndirectInstruction(BilOp.Var("x"), BilOp.Var("tid"),
                        BilOp.Var("sfid"))));
            BilTestHarness.CheckBilValid("new.indirect 正例",
                V3IndirectModule(
                    new NewIndirectInstruction(BilOp.Var("tid"), BilOp.Var("obj"),
                        Array.Empty<BilVariableOperand>())));

            BilTestHarness.CheckBilInvalid("getid.field 字段不可解析",
                V3IndirectModule(
                    new GetIdFieldInstruction(BilOp.Field("Box#ghost@.i32"), BilOp.Var("fid"))),
                "getid.field 的字段符号不可解析");
            BilTestHarness.CheckBilInvalid("cast.indirect typeid 类型不符",
                V3IndirectModule(
                    new CastIndirectInstruction(BilOp.Var("obj"), BilOp.Var("casted"),
                        BilOp.Var("x"), isSafe: false)),
                "cast.indirect typeid");
        }

        // 带 Logged wrapper 的 Service + 有参 ..init.wrapper（level:.string）
        private static BilModule InitWrapperModule(bool includeEntity)
        {
            var module = new BilModule();
            var logged = new BilTypeDeclaration("core.logging::Logged", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            logged.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "core.logging::Logged$init(level:.string)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            module.LocalSymbols.Add(logged);

            var service = new BilTypeDeclaration("com.example::Service", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilWrappedModifier("core.logging::Logged"));
            service.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "com.example::Service$init()@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            var initWrapperSym =
                "com.example::Service$..init.wrapper(level:.string)@.void";
            service.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                initWrapperSym,
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilKeywordModifier(BilKeyword.CompilerGenerated),
                }));
            module.LocalSymbols.Add(service);

            // init 声明在 LocalSymbols → 必须有对应 fn 体（§21.2）
            AddVoidInstanceFn(module, "core.logging::Logged$init(level:.string)@.void",
                "core.logging::Logged", "level", ".string");
            AddVoidInstanceFn(module, "com.example::Service$init()@.void",
                "com.example::Service", null, null);

            var iw = new BilFunction(initWrapperSym);
            iw.Args.Add(new BilArgDeclaration(".return", ".void"));
            iw.Args.Add(new BilArgDeclaration(".this", "com.example::Service"));
            iw.Args.Add(new BilArgDeclaration("level", ".string"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            if (includeEntity)
            {
                entry.Instructions.Add(new NewWrapperEntityInstruction(
                    BilOp.Type("core.logging::Logged"), new[] { BilOp.Var("level") }));
            }
            entry.Instructions.Add(new RetInstruction());
            iw.Blocks.Add(entry);
            module.Functions.Add(iw);
            return module;
        }

        private static void AddVoidInstanceFn(BilModule module, string symbol, string thisType,
            string? paramName, string? paramType)
        {
            var fn = new BilFunction(symbol);
            fn.Args.Add(new BilArgDeclaration(".return", ".void"));
            fn.Args.Add(new BilArgDeclaration(".this", thisType));
            if (paramName != null && paramType != null)
            {
                fn.Args.Add(new BilArgDeclaration(paramName, paramType));
            }
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new RetInstruction());
            fn.Blocks.Add(entry);
            module.Functions.Add(fn);
        }

        private static BilModule NewWrappedHostModule()
        {
            var module = InitWrapperModule(includeEntity: true);
            // 调用方 main：new.wrapped Service [$level] []
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main(level:.string)@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var main = new BilFunction("$main(level:.string)@.void");
            main.Args.Add(new BilArgDeclaration(".return", ".void"));
            main.Args.Add(new BilArgDeclaration("level", ".string"));
            main.Vars.Add(new BilVarDeclaration("com.example::Service", "svc"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new NewWrappedInstruction(
                BilOp.Type("com.example::Service"), BilOp.Var("svc"),
                new[] { BilOp.Var("level") }, new BilVariableOperand[0]));
            entry.Instructions.Add(new RetInstruction());
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        private static BilModule CompanionModule()
        {
            var module = new BilModule();
            var companion = new BilTypeDeclaration("Math...companion", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Singleton),
                new BilKeywordModifier(BilKeyword.Shared),
                new BilKeywordModifier(BilKeyword.CompilerGenerated));
            companion.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Math...companion$heavy()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.CompilerGenerated),
                }));
            module.LocalSymbols.Add(companion);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Math...companion$heavy()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.CompilerGenerated),
                }));
            // companion 方法体（空 ret 需值——给参数路径简化：void 壳体）
            // 改用 void 方法避免返回值 DA
            // 上面已声明 @.i32——补 fn 返回常量
            module.Resources.Add(new BilScalarResource("R_Zero", BilScalarType.I32, "0"));
            var fn = new BilFunction("Math...companion$heavy()@.i32");
            fn.Args.Add(new BilArgDeclaration(".return", ".i32"));
            fn.Args.Add(new BilArgDeclaration(".this", "Math...companion"));
            fn.Vars.Add(new BilVarDeclaration(".i32", "r"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(
                (BilScalarResource)module.Resources[0], BilOp.Var("r")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            fn.Blocks.Add(entry);
            module.Functions.Add(fn);
            return module;
        }

        private static BilModule OrdinaryFnWithNewWrapperEntityModule()
        {
            var module = InitWrapperModule(includeEntity: false);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main(level:.string)@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var main = new BilFunction("$main(level:.string)@.void");
            main.Args.Add(new BilArgDeclaration(".return", ".void"));
            main.Args.Add(new BilArgDeclaration("level", ".string"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new NewWrapperEntityInstruction(
                BilOp.Type("core.logging::Logged"), new[] { BilOp.Var("level") }));
            entry.Instructions.Add(new RetInstruction());
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        private static BilModule OrdinaryFnWithNewWrapperFieldModule()
        {
            var module = OrdinaryFnWithNewWrapperEntityModule();
            var main = module.Functions.Single(f => f.Symbol.StartsWith("$main"));
            main.Blocks[0].Instructions.Clear();
            main.Blocks[0].Instructions.Add(new NewWrapperFieldInstruction(
                BilOp.Field("com.example::Service#x@.i32"),
                BilOp.Type("core.logging::Logged"), new[] { BilOp.Var("level") }));
            main.Blocks[0].Instructions.Add(new RetInstruction());
            return module;
        }

        private static BilModule OrdinaryFnWithNewWrapperMethodModule()
        {
            var module = OrdinaryFnWithNewWrapperEntityModule();
            var main = module.Functions.Single(f => f.Symbol.StartsWith("$main"));
            main.Blocks[0].Instructions.Clear();
            main.Blocks[0].Instructions.Add(new NewWrapperMethodInstruction(
                BilOp.Fn("com.example::Service$init()@.void"),
                BilOp.Type("core.logging::Logged"), new[] { BilOp.Var("level") }));
            main.Blocks[0].Instructions.Add(new RetInstruction());
            return module;
        }

        private static BilModule InitWrapperBadReturnModule()
        {
            var module = InitWrapperModule(includeEntity: false);
            // 替换声明与 fn 为非 void
            var service = (BilTypeDeclaration)module.LocalSymbols
                .Single(s => s is BilTypeDeclaration t && t.Symbol == "com.example::Service");
            service.Members.RemoveAll(m => m is BilSimpleMemberDeclaration sm
                && MethodNameOf(sm.Symbol) == BilSpellings.InitWrapperMethodName);
            var badSym = "com.example::Service$..init.wrapper(level:.string)@.i32";
            service.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method, badSym,
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilKeywordModifier(BilKeyword.CompilerGenerated),
                }));
            module.Functions.Clear();
            var iw = new BilFunction(badSym);
            iw.Args.Add(new BilArgDeclaration(".return", ".i32"));
            iw.Args.Add(new BilArgDeclaration(".this", "com.example::Service"));
            iw.Args.Add(new BilArgDeclaration("level", ".string"));
            iw.Vars.Add(new BilVarDeclaration(".i32", "r"));
            module.Resources.Add(new BilScalarResource("R_Z", BilScalarType.I32, "0"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(
                (BilScalarResource)module.Resources[0], BilOp.Var("r")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            iw.Blocks.Add(entry);
            module.Functions.Add(iw);
            return module;
        }

        private static BilModule InitWrapperMissingModifiersModule()
        {
            var module = InitWrapperModule(includeEntity: false);
            var service = (BilTypeDeclaration)module.LocalSymbols
                .Single(s => s is BilTypeDeclaration t && t.Symbol == "com.example::Service");
            var iwDecl = (BilSimpleMemberDeclaration)service.Members
                .Single(m => m is BilSimpleMemberDeclaration sm
                    && MethodNameOf(sm.Symbol) == BilSpellings.InitWrapperMethodName);
            // 换成 pub 且无 compiler-generated
            service.Members.Remove(iwDecl);
            service.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method, iwDecl.Symbol,
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            return module;
        }

        private static BilModule DuplicateInitWrapperModule()
        {
            var module = InitWrapperModule(includeEntity: false);
            var service = (BilTypeDeclaration)module.LocalSymbols
                .Single(s => s is BilTypeDeclaration t && t.Symbol == "com.example::Service");
            service.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "com.example::Service$..init.wrapper(other:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilKeywordModifier(BilKeyword.CompilerGenerated),
                }));
            return module;
        }

        private static BilModule NewOnParameterizedInitWrapperModule()
        {
            var module = InitWrapperModule(includeEntity: true);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var main = new BilFunction("$main()@.void");
            main.Args.Add(new BilArgDeclaration(".return", ".void"));
            main.Vars.Add(new BilVarDeclaration("com.example::Service", "svc"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new NewInstruction(BilOp.Type("com.example::Service"),
                BilOp.Var("svc"), new BilVariableOperand[0]));
            entry.Instructions.Add(new RetInstruction());
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        private static BilModule NewWrappedOnParameterlessModule()
        {
            var module = new BilModule();
            var logged = new BilTypeDeclaration("core.logging::Logged", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            logged.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "core.logging::Logged$init()@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            module.LocalSymbols.Add(logged);
            var service = new BilTypeDeclaration("com.example::Service", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilWrappedModifier("core.logging::Logged"));
            service.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "com.example::Service$init()@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            var iwSym = "com.example::Service$..init.wrapper()@.void";
            service.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method, iwSym,
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilKeywordModifier(BilKeyword.CompilerGenerated),
                }));
            module.LocalSymbols.Add(service);
            AddVoidInstanceFn(module, "core.logging::Logged$init()@.void",
                "core.logging::Logged", null, null);
            AddVoidInstanceFn(module, "com.example::Service$init()@.void",
                "com.example::Service", null, null);
            var iw = new BilFunction(iwSym);
            iw.Args.Add(new BilArgDeclaration(".return", ".void"));
            iw.Args.Add(new BilArgDeclaration(".this", "com.example::Service"));
            var iwEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            iwEntry.Instructions.Add(new NewWrapperEntityInstruction(
                BilOp.Type("core.logging::Logged"), new BilVariableOperand[0]));
            iwEntry.Instructions.Add(new RetInstruction());
            iw.Blocks.Add(iwEntry);
            module.Functions.Add(iw);

            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var main = new BilFunction("$main()@.void");
            main.Args.Add(new BilArgDeclaration(".return", ".void"));
            main.Vars.Add(new BilVarDeclaration("com.example::Service", "svc"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new NewWrappedInstruction(
                BilOp.Type("com.example::Service"), BilOp.Var("svc"),
                new BilVariableOperand[0], new BilVariableOperand[0]));
            entry.Instructions.Add(new RetInstruction());
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        private static BilModule NewWrappedBadWrapperArgsModule()
        {
            var module = InitWrapperModule(includeEntity: true);
            module.Resources.Add(new BilScalarResource("R_One", BilScalarType.I32, "1"));
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var main = new BilFunction("$main()@.void");
            main.Args.Add(new BilArgDeclaration(".return", ".void"));
            main.Vars.Add(new BilVarDeclaration(".i32", "n"));
            main.Vars.Add(new BilVarDeclaration("com.example::Service", "svc"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(
                (BilScalarResource)module.Resources[0], BilOp.Var("n")));
            entry.Instructions.Add(new NewWrappedInstruction(
                BilOp.Type("com.example::Service"), BilOp.Var("svc"),
                new[] { BilOp.Var("n") }, new BilVariableOperand[0]));
            entry.Instructions.Add(new RetInstruction());
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        private static BilModule CompanionNotSingletonModule()
        {
            var module = new BilModule();
            var companion = new BilTypeDeclaration("Math...companion", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            companion.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Math...companion$m()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            module.LocalSymbols.Add(companion);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Math...companion$m()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var fn = new BilFunction("Math...companion$m()@.void");
            fn.Args.Add(new BilArgDeclaration(".return", ".void"));
            fn.Args.Add(new BilArgDeclaration(".this", "Math...companion"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new RetInstruction());
            fn.Blocks.Add(entry);
            module.Functions.Add(fn);
            return module;
        }

        private static BilModule CompanionNoMethodModule()
        {
            var module = new BilModule();
            var companion = new BilTypeDeclaration("Math...companion", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Singleton),
                new BilKeywordModifier(BilKeyword.Shared));
            module.LocalSymbols.Add(companion);
            return module;
        }

        private static string? MethodNameOf(string symbol)
        {
            var dollar = symbol.IndexOf('$');
            if (dollar < 0) return null;
            var rest = symbol.Substring(dollar + 1);
            if (rest.StartsWith(".static.")) rest = rest.Substring(".static.".Length);
            var paren = rest.IndexOf('(');
            return paren >= 0 ? rest.Substring(0, paren) : rest;
        }

        private static void TestIndirectInvokeShapes()
        {
            // §15.3 callable 协议：invoke.indirect 的对象必须（沿 extends 链）
            // 声明与实参严格匹配的 $$call。Handler 置 ExternalSymbols（本地
            // 非 native 方法必须带 fn 定义，§21.2；手工模块以外部声明形态
            // 聚焦指令侧检查——同 IndexModule 先例）
            var module = MinimalModule(out _, out var entry);
            var voidHandler = new BilTypeDeclaration("Handler", BilTypeKind.Class);
            voidHandler.Modifiers.Add(new BilAccessibilityModifier(BilAccessibility.Public));
            voidHandler.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Handler$$call()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilOperatorModifier("call") }));
            module.ExternalSymbols.Add(voidHandler);
            module.Functions[0].Vars.Add(new BilVarDeclaration("Handler", "h"));
            entry.Instructions.Insert(1, new NewInstruction(BilOp.Type("Handler"),
                BilOp.Var("h"), new List<BilVariableOperand>()));
            entry.Instructions.Insert(2, new InvokeIndirectNoResultInstruction(
                BilOp.Var("h"), new List<BilVariableOperand>()));
            BilTestHarness.CheckBilValid("operator call 对象 + invoke.indirect.noret 正例", module);

            module = MinimalModule(out _, out entry);
            var intHandler = new BilTypeDeclaration("Handler", BilTypeKind.Class);
            intHandler.Modifiers.Add(new BilAccessibilityModifier(BilAccessibility.Public));
            intHandler.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Handler$$call(value:.i32)@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilOperatorModifier("call") }));
            module.ExternalSymbols.Add(intHandler);
            module.Functions[0].Vars.Add(new BilVarDeclaration("Handler", "h"));
            module.Functions[0].Vars.Add(new BilVarDeclaration(".i32", "arg"));
            module.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "wrong"));
            entry.Instructions.Insert(1, new NewInstruction(BilOp.Type("Handler"),
                BilOp.Var("h"), new List<BilVariableOperand>()));
            entry.Instructions.Insert(2, new LoadInstruction(module.Resources[0],
                BilOp.Var("arg")));
            entry.Instructions.Insert(3, new InvokeIndirectInstruction(BilOp.Var("h"),
                BilOp.Var("wrong"), new[] { BilOp.Var("arg") }));
            BilTestHarness.CheckBilInvalid("invoke.indirect 结果类型不匹配",
                module, "invoke.indirect 结果");

            module = MinimalModule(out _, out entry);
            module.Functions[0].Vars.Add(new BilVarDeclaration(".i32", "plain"));
            entry.Instructions.Insert(1, new LoadInstruction(module.Resources[0],
                BilOp.Var("plain")));
            entry.Instructions.Insert(2, new InvokeIndirectNoResultInstruction(
                BilOp.Var("plain"), new List<BilVariableOperand>()));
            BilTestHarness.CheckBilInvalid("无 operator call 的类型不得 invoke.indirect",
                module, "operator call");

            // M108 §15.3 泛型负例：缺 typeid 前缀 / 错误前缀类型
            // 带 fn 定义（.generic.T = .typeid）的泛型 $$call——实参仅 value
            // 无 typeid 前缀应拒；前缀为 .i32 非 .typeid 应拒
            module = MinimalModule(out _, out entry);
            var genericHandler = new BilTypeDeclaration("GHandler", BilTypeKind.Class);
            genericHandler.Modifiers.Add(new BilAccessibilityModifier(BilAccessibility.Public));
            genericHandler.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "GHandler$$call(value:.i32)@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilOperatorModifier("call") }));
            module.LocalSymbols.Add(genericHandler);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "GHandler$$call(value:.i32)@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilOperatorModifier("call") }));
            var callFn = new BilFunction("GHandler$$call(value:.i32)@.i32");
            callFn.Args.Add(new BilArgDeclaration(".return", ".i32"));
            callFn.Args.Add(new BilArgDeclaration(".this", "GHandler"));
            callFn.Args.Add(new BilArgDeclaration(".generic.T", ".typeid"));
            callFn.Args.Add(new BilArgDeclaration("value", ".i32"));
            var callEntry = new BilBlock("entry");
            callEntry.Instructions.Add(new RetInstruction(BilOp.Var("value")));
            callFn.Blocks.Add(callEntry);
            module.Functions.Add(callFn);
            module.Functions[0].Vars.Add(new BilVarDeclaration("GHandler", "h"));
            module.Functions[0].Vars.Add(new BilVarDeclaration(".i32", "arg"));
            module.Functions[0].Vars.Add(new BilVarDeclaration(".i32", "r"));
            entry.Instructions.Insert(1, new NewInstruction(BilOp.Type("GHandler"),
                BilOp.Var("h"), new List<BilVariableOperand>()));
            entry.Instructions.Insert(2, new LoadInstruction(module.Resources[0],
                BilOp.Var("arg")));
            entry.Instructions.Insert(3, new InvokeIndirectInstruction(BilOp.Var("h"),
                BilOp.Var("r"), new[] { BilOp.Var("arg") }));
            BilTestHarness.CheckBilInvalid("invoke.indirect 缺 typeid 前缀",
                module, "operator call");

            module = MinimalModule(out _, out entry);
            var genericHandler2 = new BilTypeDeclaration("GHandler", BilTypeKind.Class);
            genericHandler2.Modifiers.Add(new BilAccessibilityModifier(BilAccessibility.Public));
            genericHandler2.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "GHandler$$call(value:.i32)@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilOperatorModifier("call") }));
            module.LocalSymbols.Add(genericHandler2);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "GHandler$$call(value:.i32)@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilOperatorModifier("call") }));
            callFn = new BilFunction("GHandler$$call(value:.i32)@.i32");
            callFn.Args.Add(new BilArgDeclaration(".return", ".i32"));
            callFn.Args.Add(new BilArgDeclaration(".this", "GHandler"));
            callFn.Args.Add(new BilArgDeclaration(".generic.T", ".typeid"));
            callFn.Args.Add(new BilArgDeclaration("value", ".i32"));
            callEntry = new BilBlock("entry");
            callEntry.Instructions.Add(new RetInstruction(BilOp.Var("value")));
            callFn.Blocks.Add(callEntry);
            module.Functions.Add(callFn);
            module.Functions[0].Vars.Add(new BilVarDeclaration("GHandler", "h"));
            module.Functions[0].Vars.Add(new BilVarDeclaration(".i32", "arg"));
            module.Functions[0].Vars.Add(new BilVarDeclaration(".i32", "r"));
            entry.Instructions.Insert(1, new NewInstruction(BilOp.Type("GHandler"),
                BilOp.Var("h"), new List<BilVariableOperand>()));
            entry.Instructions.Insert(2, new LoadInstruction(module.Resources[0],
                BilOp.Var("arg")));
            // 前缀误用 .i32 而非 .typeid
            entry.Instructions.Insert(3, new InvokeIndirectInstruction(BilOp.Var("h"),
                BilOp.Var("r"), new[] { BilOp.Var("arg"), BilOp.Var("arg") }));
            BilTestHarness.CheckBilInvalid("invoke.indirect 错误 typeid 前缀类型",
                module, "泛型隐藏实参");
        }

        private static void Positive(string label, string userSource)
        {
            CompilationUnit unit;
            BilModule module;
            try
            {
                (unit, module, _) = BilTestHarness.EmitBilUnit(userSource);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue(label + "：全管线未抛异常", false, ex.ToString());
                return;
            }
            TestHarness.CheckTrue(label + "：全管线无诊断", !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
            try
            {
                BilTestHarness.CheckBilValid(label + "：验证器零错误", module);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue(label + "：验证器未抛异常", false, ex.ToString());
            }
        }

        // 最小合法模块：fn($main()@.i32) + 声明 + entry block（load R_0 → ret）
        private static BilModule MinimalModule(out BilScalarResource zero,
            out BilBlock entry)
        {
            var module = new BilModule();
            zero = new BilScalarResource("R_0", BilScalarType.I32, "0");
            module.Resources.Add(zero);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
            entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(zero, BilOp.Var("x")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        // 索引验证手工模块（S8c §13.6）：Vec 声明指定 operator 成员 + main
        // （v/i/s/flag 赋值后执行给定的索引指令，ret $x 收尾）——Vec 置于
        // ExternalSymbols（本地非 native 方法必须带 fn 定义，§21.2；手工
        // 模块不构造 operator 函数体，以外部声明形态聚焦指令侧检查）。
        // 负例均在其上改造（替换 operator 声明或索引指令）
        private static BilModule IndexModule(BilSimpleMemberDeclaration[] vecOperators,
            params BilInstruction[] indexInstructions)
        {
            var module = new BilModule();
            var i32Resource = new BilScalarResource("R_0", BilScalarType.I32, "0");
            var stringResource = new BilScalarResource("R_1", BilScalarType.String, "\"a\"");
            var boolResource = new BilScalarResource("R_2", BilScalarType.Bool, "true");
            module.Resources.Add(i32Resource);
            module.Resources.Add(stringResource);
            module.Resources.Add(boolResource);
            var vecDeclaration = new BilTypeDeclaration("Vec", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            foreach (var op in vecOperators)
            {
                vecDeclaration.Members.Add(op);
            }
            module.ExternalSymbols.Add(vecDeclaration);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
            main.Vars.Add(new BilVarDeclaration("Vec", "v"));
            main.Vars.Add(new BilVarDeclaration(".i32", "i"));
            main.Vars.Add(new BilVarDeclaration(".string", "s"));
            main.Vars.Add(new BilVarDeclaration(".bool", "flag"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(i32Resource, BilOp.Var("x")));
            entry.Instructions.Add(new NewInstruction(BilOp.Type("Vec"), BilOp.Var("v"),
                new List<BilVariableOperand>()));
            entry.Instructions.Add(new LoadInstruction(i32Resource, BilOp.Var("i")));
            entry.Instructions.Add(new LoadInstruction(stringResource, BilOp.Var("s")));
            entry.Instructions.Add(new LoadInstruction(boolResource, BilOp.Var("flag")));
            entry.Instructions.AddRange(indexInstructions);
            entry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        // Vec 的索引运算符声明（$$名 canonical + pub + operator(名)，§8.4）
        private static BilSimpleMemberDeclaration IndexOperator(string symbol, string name)
        {
            return new BilSimpleMemberDeclaration(BilMemberKind.Method, symbol,
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilOperatorModifier(name),
                });
        }

        // ===== §11.4：位运算的内建标量操作数仅整数族（§21.3 门禁）=====
        private static void TestBitwiseTypeRestriction()
        {
            // 正例：整数族（.i8 代表）bin.and/shift.left/bin.not 放行（防误伤）
            var m = MinimalModule(out _, out var entryBlock);
            var i8Resource = new BilScalarResource("R_I8", BilScalarType.I8, "1");
            m.Resources.Add(i8Resource);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".i8", "i8a"));
            m.Functions[0].Vars.Add(new BilVarDeclaration(".i8", "i8b"));
            m.Functions[0].Vars.Add(new BilVarDeclaration(".i8", "i8r"));
            var at = entryBlock.Instructions.Count - 1;
            entryBlock.Instructions.Insert(at, new LoadInstruction(i8Resource, BilOp.Var("i8a")));
            entryBlock.Instructions.Insert(at + 1, new LoadInstruction(i8Resource, BilOp.Var("i8b")));
            entryBlock.Instructions.Insert(at + 2, new BinaryIntrinsicInstruction(
                BilBinaryOp.BinAnd, BilOp.Var("i8a"), BilOp.Var("i8b"), BilOp.Var("i8r")));
            entryBlock.Instructions.Insert(at + 3, new BinaryIntrinsicInstruction(
                BilBinaryOp.ShiftLeft, BilOp.Var("i8a"), BilOp.Var("i8b"), BilOp.Var("i8r")));
            entryBlock.Instructions.Insert(at + 4, new UnaryIntrinsicInstruction(
                BilUnaryOp.BinNot, BilOp.Var("i8a"), BilOp.Var("i8r")));
            BilTestHarness.CheckBilValid("整数族位运算放行（防误伤）", m);

            // 负例：.bool/.char/.f64 的 bin.and 拒绝（消息含 opcode 与类型）
            foreach (var (typeRef, scalarType, literal) in new[]
            {
                (".bool", BilScalarType.Bool, "true"),
                (".char", BilScalarType.Char, "'a'"),
                (".f64", BilScalarType.F64, "1.5"),
            })
            {
                m = MinimalModule(out _, out entryBlock);
                var resource = new BilScalarResource("R_N", scalarType, literal);
                m.Resources.Add(resource);
                m.Functions[0].Vars.Add(new BilVarDeclaration(typeRef, "na"));
                m.Functions[0].Vars.Add(new BilVarDeclaration(typeRef, "nb"));
                m.Functions[0].Vars.Add(new BilVarDeclaration(typeRef, "nr"));
                at = entryBlock.Instructions.Count - 1;
                entryBlock.Instructions.Insert(at, new LoadInstruction(resource, BilOp.Var("na")));
                entryBlock.Instructions.Insert(at + 1, new LoadInstruction(resource, BilOp.Var("nb")));
                entryBlock.Instructions.Insert(at + 2, new BinaryIntrinsicInstruction(
                    BilBinaryOp.BinAnd, BilOp.Var("na"), BilOp.Var("nb"), BilOp.Var("nr")));
                BilTestHarness.CheckBilInvalid($"{typeRef} bin.and 拒绝", m,
                    $"bin.and 的操作数类型非法：\"{typeRef}\"");
            }

            // 负例代表：.bool shift.left、.char bin.not
            m = MinimalModule(out _, out entryBlock);
            var trueResource = new BilScalarResource("R_T", BilScalarType.Bool, "true");
            m.Resources.Add(trueResource);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "sa"));
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "sb"));
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "sr"));
            at = entryBlock.Instructions.Count - 1;
            entryBlock.Instructions.Insert(at, new LoadInstruction(trueResource, BilOp.Var("sa")));
            entryBlock.Instructions.Insert(at + 1, new LoadInstruction(trueResource, BilOp.Var("sb")));
            entryBlock.Instructions.Insert(at + 2, new BinaryIntrinsicInstruction(
                BilBinaryOp.ShiftLeft, BilOp.Var("sa"), BilOp.Var("sb"), BilOp.Var("sr")));
            BilTestHarness.CheckBilInvalid(".bool shift.left 拒绝", m,
                "shift.left 的操作数类型非法：\".bool\"");

            m = MinimalModule(out _, out entryBlock);
            var charResource = new BilScalarResource("R_C", BilScalarType.Char, "'a'");
            m.Resources.Add(charResource);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".char", "ca"));
            m.Functions[0].Vars.Add(new BilVarDeclaration(".char", "cr"));
            at = entryBlock.Instructions.Count - 1;
            entryBlock.Instructions.Insert(at, new LoadInstruction(charResource, BilOp.Var("ca")));
            entryBlock.Instructions.Insert(at + 1, new UnaryIntrinsicInstruction(
                BilUnaryOp.BinNot, BilOp.Var("ca"), BilOp.Var("cr")));
            BilTestHarness.CheckBilInvalid(".char bin.not 拒绝", m,
                "bin.not 的操作数类型非法：\".char\"");
        }

        private static void NegativeCases()
        {
            // 基线：最小手工模块本身必须合法（负例均在其上改造）
            BilTestHarness.CheckBilValid("最小手工模块（基线）", MinimalModule(out _, out _));

            // §21.1：版本号
            var m = MinimalModule(out _, out _);
            m.BilVersion = "2.0";
            BilTestHarness.CheckBilInvalid("版本号不受支持", m, "不支持的 BIL 版本");

            // §21.1：保留名声明为局部变量
            m = MinimalModule(out _, out _);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".i32", ".this"));
            BilTestHarness.CheckBilInvalid("保留名作局部变量", m, "保留名");

            // §21.1：.void 局部变量
            m = MinimalModule(out _, out _);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".void", "v"));
            BilTestHarness.CheckBilInvalid(".void 局部变量", m, ".void");

            // §21.5：无 entrypoint
            m = MinimalModule(out _, out var entryBlock);
            entryBlock.Instructions.Clear();
            m.Functions[0].Blocks[0] = new BilBlock("entry");
            m.Functions[0].Blocks[0].Instructions.Add(new RetInstruction());
            BilTestHarness.CheckBilInvalid("无 entrypoint block", m, "恰有一个 entrypoint");

            // §21.5：双 entrypoint
            m = MinimalModule(out _, out _);
            var second = new BilBlock("second", BilBlockModifier.Entrypoint);
            second.Instructions.Add(new RetInstruction());
            m.Functions[0].Blocks.Add(second);
            BilTestHarness.CheckBilInvalid("双 entrypoint block", m, "恰有一个 entrypoint");

            // §21.5：entry 落尾
            m = MinimalModule(out _, out entryBlock);
            entryBlock.Instructions.RemoveAt(entryBlock.Instructions.Count - 1);
            BilTestHarness.CheckBilInvalid("entry 落尾", m, "不得以落尾结束");

            // §21.5：跨函数 block 引用（if 引用了别的函数的块）
            m = MinimalModule(out var zero2, out _);
            var otherFn = new BilFunction("$other()@.void");
            otherFn.Args.Add(new BilArgDeclaration(".return", ".void"));
            var otherBlock = new BilBlock("other", BilBlockModifier.Entrypoint);
            otherBlock.Instructions.Add(new RetInstruction());
            otherFn.Blocks.Add(otherBlock);
            m.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$other()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            m.Functions.Add(otherFn);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "c"));
            m.Functions[0].Vars.Add(new BilVarDeclaration(".breakid", "bk0"));
            m.Functions[0].Blocks[0].Instructions.Insert(1, new IfInstruction(
                BilOp.Var("c"), otherBlock, null, BilOp.Var("bk0")));
            BilTestHarness.CheckBilInvalid("跨函数 block 引用", m, "不属于当前函数");

            // §21.2：未声明变量
            m = MinimalModule(out _, out entryBlock);
            entryBlock.Instructions.Insert(1, new SetVarInstruction(BilOp.Var("x"), BilOp.Var("y")));
            BilTestHarness.CheckBilInvalid("未声明变量", m, "未声明的变量");

            // §21.4：读前未赋值
            m = MinimalModule(out _, out entryBlock);
            entryBlock.Instructions.RemoveAt(0);   // 去掉 load，直接 ret $x
            BilTestHarness.CheckBilInvalid("读前未赋值", m, "在赋值前被读取");

            // §21.4 loop.rev：body 内 break 跳过赋值，出口与出环点取交
            m = MinimalModule(out _, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "condRev"));
            m.Functions[0].Vars.Add(new BilVarDeclaration(".i32", "yRev"));
            m.Functions[0].Vars.Add(new BilVarDeclaration(".breakid", "lpRev"));
            m.Functions[0].Vars.Add(new BilVarDeclaration(".breakid", "ifRev"));
            var trueRev = new BilScalarResource("R_TRev", BilScalarType.Bool, "true");
            m.Resources.Add(trueRev);
            var thenRev = new BilBlock("then-rev");
            thenRev.Instructions.Add(new BreakInstruction(BilOp.Var("lpRev")));
            var bodyRev = new BilBlock("body-rev");
            bodyRev.Instructions.Add(new IfInstruction(
                BilOp.Var("condRev"), thenRev, null, BilOp.Var("ifRev")));
            bodyRev.Instructions.Add(new SetVarInstruction(BilOp.Var("yRev"), BilOp.Var("x")));
            var judgeRev = new BilBlock("judge-rev");
            judgeRev.Instructions.Add(new LoadInstruction(trueRev, BilOp.Var("condRev")));
            m.Functions[0].Blocks.Add(thenRev);
            m.Functions[0].Blocks.Add(bodyRev);
            m.Functions[0].Blocks.Add(judgeRev);
            entryBlock.Instructions.Insert(1, new LoadInstruction(trueRev, BilOp.Var("condRev")));
            entryBlock.Instructions.Insert(2, new LoopInstruction(
                BilOp.Var("condRev"), bodyRev, null, judgeRev, BilOp.Var("lpRev"), isRev: true));
            entryBlock.Instructions.Insert(3, new SetVarInstruction(BilOp.Var("x"), BilOp.Var("yRev")));
            BilTestHarness.CheckBilInvalid("loop.rev body 内 break 跳过局部赋值", m,
                "在赋值前被读取");

            // §21.3：set.var 两端类型不等
            m = MinimalModule(out _, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "b"));
            entryBlock.Instructions.Insert(1, new SetVarInstruction(BilOp.Var("x"), BilOp.Var("b")));
            BilTestHarness.CheckBilInvalid("set.var 类型不等", m, "set.var 两端");

            // §21.3：cmp 结果非 .bool
            m = MinimalModule(out _, out entryBlock);
            entryBlock.Instructions.Insert(1, new BinaryIntrinsicInstruction(
                BilBinaryOp.CmpEq, BilOp.Var("x"), BilOp.Var("x"), BilOp.Var("x")));
            BilTestHarness.CheckBilInvalid("cmp 结果非 bool", m, "cmp 结果");

            // §21.6：breakid 被普通读写
            m = MinimalModule(out _, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".breakid", "bk"));
            entryBlock.Instructions.Insert(1, new SetVarInstruction(BilOp.Var("x"), BilOp.Var("bk")));
            BilTestHarness.CheckBilInvalid("breakid 普通读写", m, "不得被普通读写");

            // §21.5：非 void 裸 ret
            m = MinimalModule(out _, out entryBlock);
            entryBlock.Instructions[entryBlock.Instructions.Count - 1] = new RetInstruction();
            BilTestHarness.CheckBilInvalid("非 void 裸 ret", m, "不得裸 ret");

            // §21.3：invoke 实参类型不符
            m = MinimalModule(out _, out entryBlock);
            m.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$callee(a:.i32)@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var callee = new BilFunction("$callee(a:.i32)@.void");
            callee.Args.Add(new BilArgDeclaration(".return", ".void"));
            callee.Args.Add(new BilArgDeclaration("a", ".i32"));
            var calleeEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            calleeEntry.Instructions.Add(new RetInstruction());
            callee.Blocks.Add(calleeEntry);
            m.Functions.Add(callee);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "flag"));
            entryBlock.Instructions.Insert(1, new InvokeNoResultInstruction(
                BilOp.Fn("$callee(a:.i32)@.void"), new[] { BilOp.Var("flag") }));
            BilTestHarness.CheckBilInvalid("invoke 实参类型不符", m, "invoke 实参 0");

            // §21.2：native 方法不得有 fn 定义
            m = MinimalModule(out _, out _);
            const string nativeSymbol = "core.io::Console$.static.print(text:.string)@.void";
            m.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.StaticMethod,
                nativeSymbol,
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilKeywordModifier(BilKeyword.Native),
                    new BilNativeSymbolModifier("print"),
                    new BilNativeLibraryModifier("rigi_rt"),
                }));
            var nativeBody = new BilFunction(nativeSymbol);
            nativeBody.Args.Add(new BilArgDeclaration(".return", ".void"));
            nativeBody.Args.Add(new BilArgDeclaration("text", ".string"));
            var nativeEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            nativeEntry.Instructions.Add(new RetInstruction());
            nativeBody.Blocks.Add(nativeEntry);
            m.Functions.Add(nativeBody);
            BilTestHarness.CheckBilInvalid("native 带 fn 定义", m, "native 方法不得存在 fn 定义");

            // §21.2：声明关键字与符号 .static. 标记不一致
            m = MinimalModule(out _, out _);
            m.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "com.example::App$.static.run()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            BilTestHarness.CheckBilInvalid("static 标记不一致", m, "不一致");

            // §21.8：class 带 rich
            m = MinimalModule(out _, out _);
            m.LocalSymbols.Add(new BilTypeDeclaration("com.example::Rich", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich)));
            BilTestHarness.CheckBilInvalid("class 带 rich", m, "rich 仅适用");

            // §21.8：wrapper 未带 rich
            m = MinimalModule(out _, out _);
            m.LocalSymbols.Add(new BilTypeDeclaration("com.example::Wrap", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public)));
            BilTestHarness.CheckBilInvalid("wrapper 未带 rich", m, "必须显式带 rich");

            // §21.2：资源不属于本模块
            m = MinimalModule(out _, out entryBlock);
            var orphan = new BilScalarResource("R_Orphan", BilScalarType.I32, "1");
            entryBlock.Instructions.Insert(1, new LoadInstruction(orphan, BilOp.Var("x")));
            BilTestHarness.CheckBilInvalid("资源不属于本模块", m, "不属于本模块");

            // §21.2：类型符号不可解析
            m = MinimalModule(out _, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration("com.example::Missing", "ghost"));
            entryBlock.Instructions.Insert(1, new NewInstruction(
                BilOp.Type("com.example::Missing"), BilOp.Var("ghost"),
                new List<BilVariableOperand>()));
            BilTestHarness.CheckBilInvalid("类型符号不可解析", m, "不可解析");

            // §21.3：if 条件非 .bool
            m = MinimalModule(out _, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".breakid", "bk1"));
            var thenBlock = new BilBlock("then");
            m.Functions[0].Blocks.Add(thenBlock);
            entryBlock.Instructions.Insert(1, new IfInstruction(BilOp.Var("x"), thenBlock, null,
                BilOp.Var("bk1")));
            BilTestHarness.CheckBilInvalid("if 条件非 bool", m, "if 条件");

            // §21.3：ret 返回值类型不符
            m = MinimalModule(out _, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "bb"));
            entryBlock.Instructions[entryBlock.Instructions.Count - 1] =
                new RetInstruction(BilOp.Var("bb"));
            BilTestHarness.CheckBilInvalid("ret 类型不符", m, "ret 返回值");

            // §21.5：continue 引用 switch token
            m = MinimalModule(out _, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".breakid", "sw"));
            var caseBlock = new BilBlock("case0");
            caseBlock.Instructions.Add(new ContinueInstruction(BilOp.Var("sw")));
            var defaultBlock = new BilBlock("default");
            m.Functions[0].Blocks.Add(caseBlock);
            m.Functions[0].Blocks.Add(defaultBlock);
            var table = new BilSwitchTableResource("R_Sw", ".i32", new List<string> { "1" });
            m.Resources.Add(table);
            entryBlock.Instructions.Insert(1, new SwitchInstruction(
                BilOp.Var("x"), table, new List<BilBlock> { caseBlock }, defaultBlock,
                BilOp.Var("sw")));
            BilTestHarness.CheckBilInvalid("continue 引用 switch token", m,
                "continue 不得引用非 loop");

            // §21.4：if 分支合并后读取（then 内赋值的变量，合并后视为未赋值）
            m = MinimalModule(out _, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "cond3"));
            m.Functions[0].Vars.Add(new BilVarDeclaration(".i32", "y"));
            m.Functions[0].Vars.Add(new BilVarDeclaration(".breakid", "bk3"));
            var trueRes = new BilScalarResource("R_T3", BilScalarType.Bool, "true");
            m.Resources.Add(trueRes);
            var thenAssign = new BilBlock("then-assign");
            thenAssign.Instructions.Add(new SetVarInstruction(BilOp.Var("x"), BilOp.Var("y")));
            m.Functions[0].Blocks.Add(thenAssign);
            entryBlock.Instructions.Insert(1, new LoadInstruction(trueRes, BilOp.Var("cond3")));
            entryBlock.Instructions.Insert(2, new IfInstruction(
                BilOp.Var("cond3"), thenAssign, null, BilOp.Var("bk3")));
            entryBlock.Instructions.Insert(3, new SetVarInstruction(BilOp.Var("y"), BilOp.Var("x")));
            // y 只在 then 分支赋值，if 落尾后读取 y —— 合并取交集，报未赋值；
            // 同时 then 内 set.var $x $y 读取未赋值的 y 也报（同变量去重后各一条）
            BilTestHarness.CheckBilInvalid("if 分支合并后读取", m, "在赋值前被读取");

            // §21.4：judge 未写 condition
            m = MinimalModule(out zero2, out entryBlock);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "cond2"));
            m.Functions[0].Vars.Add(new BilVarDeclaration(".breakid", "lp2"));
            var body2 = new BilBlock("loop-body");
            var judge2 = new BilBlock("loop-judge");
            m.Functions[0].Blocks.Add(body2);
            m.Functions[0].Blocks.Add(judge2);
            var trueResource = new BilScalarResource("R_T", BilScalarType.Bool, "true");
            m.Resources.Add(trueResource);
            entryBlock.Instructions.Insert(1, new LoadInstruction(trueResource, BilOp.Var("cond2")));
            entryBlock.Instructions.Insert(2, new LoopInstruction(
                BilOp.Var("cond2"), body2, null, judge2, BilOp.Var("lp2"), false));
            BilTestHarness.CheckBilInvalid("judge 未写 condition", m, "未对条件变量");

            // ===== S8c：§13.6 索引严格三元组查询 =====
            // 基线：手工索引模块本身必须合法（get.array/set.array 正例）
            BilTestHarness.CheckBilValid("索引手工模块（基线，get/set 正例）",
                IndexModule(BothIndexOperators(),
                    new GetArrayInstruction(BilOp.Var("v"), BilOp.Var("i"), BilOp.Var("s")),
                    new SetArrayInstruction(BilOp.Var("v"), BilOp.Var("i"), BilOp.Var("s"))));

            // §13.6：set.array 索引类型与 setAtIndex param[0] 不符
            BilTestHarness.CheckBilInvalid("set.array 索引类型不符（无精确匹配）",
                IndexModule(BothIndexOperators(),
                    new SetArrayInstruction(BilOp.Var("v"), BilOp.Var("flag"), BilOp.Var("s"))),
                "无精确匹配");

            // §13.6：set.array 元素类型与 setAtIndex param[1] 不符
            BilTestHarness.CheckBilInvalid("set.array 元素类型不符（无精确匹配）",
                IndexModule(BothIndexOperators(),
                    new SetArrayInstruction(BilOp.Var("v"), BilOp.Var("i"), BilOp.Var("i"))),
                "无精确匹配");

            // §13.6：get.array 结果类型与 getAtIndex 返回类型不符
            BilTestHarness.CheckBilInvalid("get.array 结果类型不符",
                IndexModule(BothIndexOperators(),
                    new GetArrayInstruction(BilOp.Var("v"), BilOp.Var("i"), BilOp.Var("x"))),
                "get.array 目标变量");

            // §13.6：可解析类型无索引运算符实现
            BilTestHarness.CheckBilInvalid("可解析类型无索引运算符",
                IndexModule(new BilSimpleMemberDeclaration[0],
                    new GetArrayInstruction(BilOp.Var("v"), BilOp.Var("i"), BilOp.Var("s"))),
                "没有 getAtIndex 索引运算符实现");

            // ===== S8e：§21.8 访问器声明 =====
            // 基线：手工访问器模块本身必须合法（字段 local 裸条目 + 访问器
            // 方法声明 external——外部声明无需 fn 定义，聚焦声明侧修饰规则）
            m = MinimalModule(out _, out _);
            m.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "#v@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Backing),
                    new BilKeywordModifier(BilKeyword.Readable),
                }));
            m.ExternalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$.get.v@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilAccessorModifier(BilAccessorKind.Getter, "#v@.i32"),
                }));
            BilTestHarness.CheckBilValid("访问器手工模块（基线，getter 正例）", m);

            // §21.8：getter(FIELD) 的 FIELD 必须可解析为已声明字段符号
            m.ExternalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$.get.w@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilAccessorModifier(BilAccessorKind.Getter, "#w@.i32"),
                }));
            BilTestHarness.CheckBilInvalid("getter(FIELD) 引用未声明字段", m,
                "访问器修饰引用的字段符号不可解析");

            // §21.8：getter 修饰与方法符号形态必须一致（非 $.get. 形态即拒）
            m = MinimalModule(out _, out _);
            m.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "#v@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Backing),
                    new BilKeywordModifier(BilKeyword.Readable),
                }));
            m.ExternalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$v()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilAccessorModifier(BilAccessorKind.Getter, "#v@.i32"),
                }));
            BilTestHarness.CheckBilInvalid("getter 修饰配非 $.get. 形态方法符号", m,
                "修饰与方法符号形态不符");

            // §21.8：backing 与 computed 是互斥的存储形态标记
            m = MinimalModule(out _, out _);
            m.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "#v@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Backing),
                    new BilKeywordModifier(BilKeyword.Computed),
                }));
            BilTestHarness.CheckBilInvalid("backing 与 computed 共存", m,
                "backing 与 computed 不得共存");

            // ===== M64：§18 hint 指令 =====
            // 基线：string 资源 + entry block 内 hint——合法（纯位置标记，
            // 不读写变量、不参与 DA、不是终结指令）
            m = MinimalModule(out _, out entryBlock);
            var hintResource = new BilScalarResource("R_Hint", BilScalarType.String, "\"{}\"");
            m.Resources.Add(hintResource);
            entryBlock.Instructions.Insert(0, new HintInstruction(hintResource));
            BilTestHarness.CheckBilValid("hint 指令（string 资源，正例）", m);

            // §21.3：hint 引用非 string 资源即非法
            m = MinimalModule(out var hintI64, out entryBlock);
            entryBlock.Instructions.Insert(0, new HintInstruction(hintI64));
            BilTestHarness.CheckBilInvalid("hint 引用非 string 资源", m,
                "必须是 string 资源");

            // §21.2：hint 引用的资源必须属于本模块
            m = MinimalModule(out _, out entryBlock);
            entryBlock.Instructions.Insert(0, new HintInstruction(
                new BilScalarResource("R_Foreign", BilScalarType.String, "\"{}\"")));
            BilTestHarness.CheckBilInvalid("hint 引用模块外资源", m,
                "不属于本模块");

            // ===== S11：§12.3 type.is.case 与 §13.3 嵌套字段访问负例 =====
            // 基线见 RunAll 的 S11Module 正例；负例均在其上改造

            // §21.2：is.case 的 case 符号未登记
            m = S11Module(new IsCaseInstruction(BilOp.Var("e"),
                BilOp.Case("com.example::RequestResult.Missing"), BilOp.Var("b")));
            BilTestHarness.CheckBilInvalid("is.case case 符号不可解析", m,
                "case 符号不可解析");

            // §21.3：is.case 操作数类型必须严格等于 case 的 enum 类型
            m = S11Module(new IsCaseInstruction(BilOp.Var("svc"),
                BilOp.Case("com.example::RequestResult.Failed"), BilOp.Var("b")));
            BilTestHarness.CheckBilInvalid("is.case 操作数类型不符", m,
                "type.is.case 的操作数");

            // §21.3：is.case 结果必须为 .bool
            m = S11Module(new IsCaseInstruction(BilOp.Var("e"),
                BilOp.Case("com.example::RequestResult.Failed"), BilOp.Var("lv")));
            BilTestHarness.CheckBilInvalid("is.case 结果非 bool", m,
                "type.is.case 结果");

            // ===== §14.3 new.case 组合实参校验（固定实参 + 洞实参按 init
            // 参数序组合，匹配宿主 enum 的 init 签名；历史 bug 前按 case
            // 洞签名校验，固定实参无通道进 BIL）=====
            m = EnumCaseInitModule(new NewCaseInstruction(BilOp.Type("E"),
                BilOp.Case("E.Param"), BilOp.Var(".t0"), new[] { BilOp.Var("x") }));
            BilTestHarness.CheckBilValid("new.case 洞实参匹配 init（正例）", m);
            // 固定 case 的组合实参（声明点固定实参）同样匹配 init
            m = EnumCaseInitModule(new NewCaseInstruction(BilOp.Type("E"),
                BilOp.Case("E.Fixed"), BilOp.Var(".t0"), new[] { BilOp.Var("x") }));
            BilTestHarness.CheckBilValid("new.case 固定 case 组合实参（正例）", m);
            // 组合实参类型不匹配任何 init
            m = EnumCaseInitModule(new NewCaseInstruction(BilOp.Type("E"),
                BilOp.Case("E.Param"), BilOp.Var(".t0"), new[] { BilOp.Var("s") }));
            BilTestHarness.CheckBilInvalid("new.case 组合实参不匹配 init", m,
                "不匹配 enum-struct \"E\" 的任何 init 签名");
            // 无 init 声明的 enum 带参数构造
            m = EnumCaseInitModule(new NewCaseInstruction(BilOp.Type("E2"),
                BilOp.Case("E2.Lone"), BilOp.Var(".t1"), new[] { BilOp.Var("x") }));
            BilTestHarness.CheckBilInvalid("new.case 无 init 带参数", m,
                "没有 init 声明，new.case 不得带参数");
            m = EnumCaseInitModule(new NewCaseInstruction(BilOp.Type("E2"),
                BilOp.Case("E2.Lone"), BilOp.Var(".t1"),
                new List<BilVariableOperand>()));
            BilTestHarness.CheckBilValid("new.case 无 init 零参数（正例）", m);

            // §21.2：case 声明宿主不是 enum-struct（声明侧）
            m = MinimalModule(out _, out _);
            var classWithCase = new BilTypeDeclaration("com.example::Service",
                BilTypeKind.Class, new BilAccessibilityModifier(BilAccessibility.Public));
            classWithCase.Members.Add(new BilCaseDeclaration("com.example::Service.Foo"));
            m.LocalSymbols.Add(classWithCase);
            BilTestHarness.CheckBilInvalid("case 声明宿主非 enum-struct", m,
                "宿主类型不是 enum-struct");

            // §21.2：case 判别值资源未登记
            m = MinimalModule(out _, out _);
            var orphanCase = new BilTypeDeclaration("com.example::RequestResult",
                BilTypeKind.EnumStruct, new BilAccessibilityModifier(BilAccessibility.Public));
            orphanCase.Members.Add(new BilCaseDeclaration("com.example::RequestResult.Failed",
                new[] { new BilCaseParameter("errorCode", ".i32") },
                discriminantResource: "R_OrphanCase"));
            m.LocalSymbols.Add(orphanCase);
            BilTestHarness.CheckBilInvalid("case 判别值资源未登记", m,
                "未登记");

            // §21.2：set.wrapper.field 链字段符号不可解析
            m = S11Module(new SetWrapperFieldInstruction(BilOp.Var("lv"), BilOp.Var("svc"),
                BilOp.Field("com.example::Service#name@.string"),
                BilOp.Field("core.logging::Logged#level@.string")));
            // 纯 field 链先被「必须含 wrapper」拒绝；带 wrapper 的不可解析字段：
            m = S11Module(new SetWrapperFieldInstruction(BilOp.Var("lv"), BilOp.Var("svc"),
                new BilOperand[]
                {
                    BilOp.Field("com.example::Service#ghost@.string"),
                    BilOp.Wrapper("core.logging::Logged"),
                },
                BilOp.Field("core.logging::Logged#level@.string")));
            BilTestHarness.CheckBilInvalid("set.wrapper.field 链字段不可解析", m,
                "链字段符号不可解析");

            // §21.3：set.wrapper.field wrapper 元素必须是 wrapper 类型
            m = S11Module(
                new SetWrapperFieldInstruction(BilOp.Var("lv"), BilOp.Var("svc"),
                    BilOp.Wrapper("com.example::Plain"),
                    BilOp.Field("core.logging::Logged#level@.string")));
            m.LocalSymbols.Add(new BilTypeDeclaration("com.example::Plain",
                BilTypeKind.Class, new BilAccessibilityModifier(BilAccessibility.Public)));
            BilTestHarness.CheckBilInvalid("set.wrapper.field wrapper 元素非 wrapper 类型", m,
                "不是 wrapper 类型");

            // §21.2：set.wrapper.field 内层字段符号不可解析
            m = S11Module(new SetWrapperFieldInstruction(BilOp.Var("lv"), BilOp.Var("svc"),
                BilOp.Wrapper("core.logging::Logged"),
                BilOp.Field("core.logging::Logged#ghost@.string")));
            BilTestHarness.CheckBilInvalid("set.wrapper.field 内层字段不可解析", m,
                "内层字段符号不可解析");

            // §21.3：set.wrapper.field 内层字段必须是实例字段（静态字段拒绝）
            m = S11Module(
                new SetWrapperFieldInstruction(BilOp.Var("flag"), BilOp.Var("svc"),
                    BilOp.Wrapper("core.logging::Logged"),
                    BilOp.Field("core.logging::Logged#.static.flag@.bool")));
            var loggedWithStatic = (BilTypeDeclaration)m.LocalSymbols
                .Single(e => e is BilTypeDeclaration { Symbol: "core.logging::Logged" });
            loggedWithStatic.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "core.logging::Logged#.static.flag@.bool",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "flag"));
            BilTestHarness.CheckBilInvalid("set.wrapper.field 内层字段为静态字段", m,
                "必须是实例字段");

            // §21.3：set.wrapper.field 源类型必须严格等于内层字段类型
            m = S11Module(new SetWrapperFieldInstruction(BilOp.Var("b"), BilOp.Var("svc"),
                BilOp.Wrapper("core.logging::Logged"),
                BilOp.Field("core.logging::Logged#level@.string")));
            BilTestHarness.CheckBilInvalid("set.wrapper.field 源类型不符", m,
                "set.wrapper.field 源变量");

            // §21.3：set.wrapper.field 禁止纯 field 链（非 wrapper 后门）
            m = S11Module(new SetWrapperFieldInstruction(BilOp.Var("lv"), BilOp.Var("svc"),
                BilOp.Field("com.example::Service#name@.string"),
                BilOp.Field("com.example::Service#name@.string")));
            BilTestHarness.CheckBilInvalid("set.wrapper.field 纯 field 链拒绝", m,
                "set.wrapper.field 链必须含至少一个 wrapper");

            // §21.8：set.wrapper.field 不得写入 const 内层字段
            m = S11Module(
                new SetWrapperFieldInstruction(BilOp.Var("lv"), BilOp.Var("svc"),
                    BilOp.Wrapper("core.logging::Logged"),
                    BilOp.Field("core.logging::Logged#tag@.string")));
            var loggedWithConst = (BilTypeDeclaration)m.LocalSymbols
                .Single(e => e is BilTypeDeclaration { Symbol: "core.logging::Logged" });
            loggedWithConst.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "core.logging::Logged#tag@.string",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Const),
                }));
            BilTestHarness.CheckBilInvalid("set.wrapper.field 写 const 内层字段", m,
                "不得被写入");

            // ===== M84：§12.4 get.wrapper.field 负例 =====
            m = FieldValueModule(new GetWrapperFieldInstruction(BilOp.Var("hero"),
                BilOp.Field("com.example::Hero#ghost@.i32"),
                BilOp.Type("core.clamp::Clamped"), BilOp.Var("w")));
            BilTestHarness.CheckBilInvalid("get.wrapper.field 宿主字段不可解析", m,
                "宿主字段符号不可解析");

            m = FieldValueModule(new GetWrapperFieldInstruction(BilOp.Var("hero"),
                BilOp.Field("com.example::Hero#hp@.i32"),
                BilOp.Type("com.example::Plain"), BilOp.Var("w")));
            m.LocalSymbols.Add(new BilTypeDeclaration("com.example::Plain",
                BilTypeKind.Class, new BilAccessibilityModifier(BilAccessibility.Public)));
            BilTestHarness.CheckBilInvalid("get.wrapper.field 非 wrapper 类型", m,
                "不是 wrapper 类型");

            m = FieldValueModule(new GetWrapperFieldInstruction(BilOp.Var("hero"),
                BilOp.Field("com.example::Hero#raw@.i32"),
                BilOp.Type("core.clamp::Clamped"), BilOp.Var("w")));
            var heroDecl = (BilTypeDeclaration)m.LocalSymbols
                .Single(e => e is BilTypeDeclaration { Symbol: "com.example::Hero" });
            heroDecl.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "com.example::Hero#raw@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            BilTestHarness.CheckBilInvalid("get.wrapper.field 字段缺 wrapped 标记", m,
                "必须带 wrapped");

            m = FieldValueModule(new GetWrapperFieldInstruction(BilOp.Var("hero"),
                BilOp.Field("com.example::Hero#hp@.i32"),
                BilOp.Type("core.clamp::Clamped"), BilOp.Var("x")));
            BilTestHarness.CheckBilInvalid("get.wrapper.field 结果类型不符", m,
                "get.wrapper.field 结果");

            // 静态 HOST_FIELD
            m = FieldValueModule(new GetWrapperFieldInstruction(BilOp.Var("hero"),
                BilOp.Field("com.example::Hero#.static.counter@.i32"),
                BilOp.Type("core.clamp::Clamped"), BilOp.Var("w")));
            heroDecl = (BilTypeDeclaration)m.LocalSymbols
                .Single(e => e is BilTypeDeclaration { Symbol: "com.example::Hero" });
            heroDecl.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "com.example::Hero#.static.counter@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilWrappedModifier("core.clamp::Clamped"),
                }));
            BilTestHarness.CheckBilInvalid("get.wrapper.field 静态 HOST_FIELD", m,
                "必须是实例字段");

            // OBJECT owner 不匹配（可解析的非 owner 类型；内建 .i32 查不到会降级）
            m = FieldValueModule(new GetWrapperFieldInstruction(BilOp.Var("plain"),
                BilOp.Field("com.example::Hero#hp@.i32"),
                BilOp.Type("core.clamp::Clamped"), BilOp.Var("w")));
            m.LocalSymbols.Add(new BilTypeDeclaration("com.example::Plain",
                BilTypeKind.Class, new BilAccessibilityModifier(BilAccessibility.Public)));
            m.Functions[0].Vars.Add(new BilVarDeclaration("com.example::Plain", "plain"));
            BilTestHarness.CheckBilInvalid("get.wrapper.field OBJECT owner 不匹配", m,
                "宿主对象");

            // 字段应用对：field(F)+wrapper(W) 但 F 缺匹配 wrapped
            m = FieldValueModule(new SetWrapperFieldInstruction(BilOp.Var("x"),
                BilOp.Var("hero"),
                new BilOperand[]
                {
                    BilOp.Field("com.example::Hero#raw@.i32"),
                    BilOp.Wrapper("core.clamp::Clamped"),
                },
                BilOp.Field("core.clamp::Clamped#min@.i32")));
            heroDecl = (BilTypeDeclaration)m.LocalSymbols
                .Single(e => e is BilTypeDeclaration { Symbol: "com.example::Hero" });
            heroDecl.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "com.example::Hero#raw@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilWrappedModifier("core.clamp::Other"),
                }));
            m.LocalSymbols.Add(new BilTypeDeclaration("core.clamp::Other", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich)));
            BilTestHarness.CheckBilInvalid("set.wrapper.field 字段应用对错 W", m,
                "要求当前位置类型");

            // 字段应用对正例：field(HOST)+wrapper(W)
            BilTestHarness.CheckBilValid("set.wrapper.field 字段应用对 field+wrapper 正例",
                FieldValueModule(new SetWrapperFieldInstruction(BilOp.Var("x"),
                    BilOp.Var("hero"),
                    new BilOperand[]
                    {
                        BilOp.Field("com.example::Hero#hp@.i32"),
                        BilOp.Wrapper("core.clamp::Clamped"),
                    },
                    BilOp.Field("core.clamp::Clamped#min@.i32"))));

            // ===== §8.4/§21.8 wrapper-proxy（M88：specific|wildcard 两态）=====
            // proxy 模板必须在 wrapper 类型内；ExternalSymbols 免 fn 定义
            m = MinimalModule(out _, out _);
            var proxyWrapper = new BilTypeDeclaration("core.logging::Logged", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            proxyWrapper.GenericParameters.Add("TTarget");
            proxyWrapper.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "core.logging::Logged$.proxy.doSomething(arg:.i32)@.string",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilWrapperProxyModifier(BilProxyKind.Specific) }));
            proxyWrapper.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "core.logging::Logged$.proxy.*(symbol:.string)@.any",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilWrapperProxyModifier(BilProxyKind.Wildcard) }));
            m.ExternalSymbols.Add(proxyWrapper);
            BilTestHarness.CheckBilValid("wrapper-proxy 模板声明（specific/wildcard 正例）", m);

            // §21.8：非 .proxy. 名带 wrapper-proxy
            m = MinimalModule(out _, out _);
            var nonProxyHost = new BilTypeDeclaration("core.logging::Logged", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            nonProxyHost.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "core.logging::Logged$f()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilWrapperProxyModifier(BilProxyKind.Specific) }));
            m.ExternalSymbols.Add(nonProxyHost);
            BilTestHarness.CheckBilInvalid("非 .proxy. 名带 wrapper-proxy", m,
                "只允许在名以 .proxy. 开头");

            // §21.8：.proxy. 名缺 wrapper-proxy
            m = MinimalModule(out _, out _);
            var unmarkedProxy = new BilTypeDeclaration("core.logging::Logged", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            unmarkedProxy.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "core.logging::Logged$.proxy.x()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            m.ExternalSymbols.Add(unmarkedProxy);
            BilTestHarness.CheckBilInvalid(".proxy. 名缺 wrapper-proxy", m,
                "缺少 wrapper-proxy(...)");

            // §21.2：.proxy. 方法不在 wrapper 类型内
            m = MinimalModule(out _, out _);
            var classHost = new BilTypeDeclaration("Svc", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            classHost.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Svc$.proxy.x()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilWrapperProxyModifier(BilProxyKind.Specific) }));
            m.ExternalSymbols.Add(classHost);
            BilTestHarness.CheckBilInvalid(".proxy. 不在 wrapper 类型内", m,
                "必须声明在 wrapper 类型内");

            // §21.8：通配 proxy 名必须 wildcard kind
            m = MinimalModule(out _, out _);
            var wrongWildKind = new BilTypeDeclaration("core.logging::Logged", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            wrongWildKind.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "core.logging::Logged$.proxy.*(symbol:.string)@.any",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilWrapperProxyModifier(BilProxyKind.Specific) }));
            m.ExternalSymbols.Add(wrongWildKind);
            BilTestHarness.CheckBilInvalid("通配 proxy 带 specific", m,
                "必须带 wrapper-proxy(wildcard)");

            // §21.8：具名 proxy 必须 specific kind
            m = MinimalModule(out _, out _);
            var wrongSpecKind = new BilTypeDeclaration("core.logging::Logged", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            wrongSpecKind.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "core.logging::Logged$.proxy.x()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilWrapperProxyModifier(BilProxyKind.Wildcard) }));
            m.ExternalSymbols.Add(wrongSpecKind);
            BilTestHarness.CheckBilInvalid("具名 proxy 带 wildcard", m,
                "必须带 wrapper-proxy(specific)");

            // §21.8：wrapper-proxy 修饰符重复
            m = MinimalModule(out _, out _);
            var dupProxyHost = new BilTypeDeclaration("core.logging::Logged", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            dupProxyHost.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "core.logging::Logged$.proxy.x()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilWrapperProxyModifier(BilProxyKind.Specific),
                    new BilWrapperProxyModifier(BilProxyKind.Wildcard) }));
            m.ExternalSymbols.Add(dupProxyHost);
            BilTestHarness.CheckBilInvalid("wrapper-proxy 修饰符重复", m,
                "wrapper-proxy 修饰符重复");

            // §21.3：get.self / invoke fn(..inner) 仅 proxy 模板 fn 内合法
            BilTestHarness.CheckBilValid("get.self/invoke fn(..inner) 在 proxy 模板内（正例）",
                ProxyTemplateModule(includeSelfInner: true));
            BilTestHarness.CheckBilInvalid("get.self 在普通 fn 内",
                OrdinaryFnWithGetSelfModule(), "仅允许在带 wrapper-proxy");
            // #27⑦ / §15.4：invoke fn(..inner) 前置 .generic 操作数须已声明
            BilTestHarness.CheckBilInvalid("invoke fn(..inner) 未声明 .generic 操作数",
                InnerUndeclaredGenericModule(), "未声明的变量");
            BilTestHarness.CheckBilInvalid("invoke fn(..inner) 泛型包顺序错误",
                InnerWrongGenericOrderModule(), "按 .args 声明序前置");
            BilTestHarness.CheckBilInvalid("invoke fn(..inner) 不接受 receiver",
                InnerWithReceiverModule(), "不接受 receiver");
            BilTestHarness.CheckBilInvalid("invoke fn(..inner) 缺 wildcard 保留首参操作数",
                WildcardInnerMissingReservedModule(), "保留首参操作数");

            // §8.3.1 wrapped(W) 正例 / 非 wrapper 类型拒
            m = MinimalModule(out _, out _);
            m.LocalSymbols.Add(new BilTypeDeclaration("core.logging::Logged", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich)));
            var wrappedService = new BilTypeDeclaration("com.example::Service", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilWrappedModifier("core.logging::Logged"));
            m.LocalSymbols.Add(wrappedService);
            BilTestHarness.CheckBilValid("wrapped(W) 类型声明正例", m);
            m = MinimalModule(out _, out _);
            m.LocalSymbols.Add(new BilTypeDeclaration("com.example::Plain", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public)));
            var badWrapped = new BilTypeDeclaration("com.example::Service", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilWrappedModifier("com.example::Plain"));
            m.LocalSymbols.Add(badWrapped);
            BilTestHarness.CheckBilInvalid("wrapped 非 wrapper 类型", m,
                "不是 wrapper 类型");


            // ===== §21.8 init 豁免（对齐 P3 ConstFieldRules，SYNTAX §9.3）=====
            // init 方法（§8.4 init 修饰符标识）体内写实例 const 字段放行
            // （构造期一次性赋值；豁免不限字段宿主==函数宿主，继承的基类
            // 字段同放行——全管线继承形态见正例「解构声明」）；普通方法
            // 写入与 init 内静态写入仍拒
            BilTestHarness.CheckBilValid("init 内写实例 const 字段（init 豁免，正例）",
                ConstWriteModule(ConstWriteTarget.InstanceInInit));
            BilTestHarness.CheckBilInvalid("普通方法写实例 const 字段（不豁免）",
                ConstWriteModule(ConstWriteTarget.InstanceInMethod), "不得被写入");
            BilTestHarness.CheckBilInvalid("init 内写静态 const 字段（静态不豁免）",
                ConstWriteModule(ConstWriteTarget.StaticInInit), "不得被写入");
            BilTestHarness.CheckBilValid("init 内 set.wrapper.field 写 const 内层字段（init 豁免，正例）",
                S11InitWrapperFieldModule());

            // ===== 验证器修复批次 =====
            // S10：同名不同元数类型共存——声明反查键 = 符号 + 泛型元数
            // （Wrap init() 与 Wrap\<T\> init(x: .i32)，两种声明顺序各验
            // 一次：new 两形态按元数各自命中 init，互不遮蔽）
            BilTestHarness.CheckBilValid("同名不同元数共存（plain 先声明，正例）",
                ArityCoexistModule(genericFirst: false));
            BilTestHarness.CheckBilValid("同名不同元数共存（generic 先声明，正例）",
                ArityCoexistModule(genericFirst: true));

            // §21.5/§21.3：跨函数 block 引用的越权块不展开——块内类型错误
            // 只在所属 fn 视角报一次，不经引用点级联（§21.3 类型检查与
            // §21.5 结构检查同走块成员资格过滤）
            m = MinimalModule(out var i32Res, out _);
            var foreignFn = new BilFunction("$foreign()@.void");
            foreignFn.Args.Add(new BilArgDeclaration(".return", ".void"));
            foreignFn.Vars.Add(new BilVarDeclaration(".bool", "fb"));
            foreignFn.Vars.Add(new BilVarDeclaration(".i32", "fi"));
            var foreignBlock = new BilBlock("foreign", BilBlockModifier.Entrypoint);
            var boolResCf = new BilScalarResource("R_CF", BilScalarType.Bool, "true");
            m.Resources.Add(boolResCf);
            foreignBlock.Instructions.Add(new LoadInstruction(i32Res, BilOp.Var("fi")));
            foreignBlock.Instructions.Add(new LoadInstruction(boolResCf, BilOp.Var("fb")));
            foreignBlock.Instructions.Add(new SetVarInstruction(BilOp.Var("fi"), BilOp.Var("fb")));
            foreignBlock.Instructions.Add(new RetInstruction());
            foreignFn.Blocks.Add(foreignBlock);
            m.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$foreign()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            m.Functions.Add(foreignFn);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "cf"));
            m.Functions[0].Vars.Add(new BilVarDeclaration(".breakid", "bkf"));
            m.Functions[0].Blocks[0].Instructions.Insert(1,
                new LoadInstruction(boolResCf, BilOp.Var("cf")));
            m.Functions[0].Blocks[0].Instructions.Insert(2, new IfInstruction(
                BilOp.Var("cf"), foreignBlock, null, BilOp.Var("bkf")));
            var cascadeErrors = BilVerifier.Verify(m);
            TestHarness.CheckTrue("越权块类型错误不级联（越权一条 + 所属 fn 一条）",
                cascadeErrors.Count == 2
                && cascadeErrors.Count(e => e.Message.Contains("不属于当前函数")) == 1
                && cascadeErrors.Count(e => e.Message.Contains("set.var 两端")) == 1,
                "实际: " + string.Join("; ", cascadeErrors.Select(e => e.ToString())));

            // §21.1：保留名家族（.generic./.vargs./.kwargs.）放行前缀后余部
            // 仍须合法——空余部与含非法字符均拒绝
            m = MinimalModule(out _, out _);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".typeid", ".generic."));
            BilTestHarness.CheckBilInvalid("保留名家族空余部（.generic.）", m,
                "非法局部变量名");
            m = MinimalModule(out _, out _);
            m.Functions[0].Vars.Add(new BilVarDeclaration(".array<.any>", ".vargs.bad name"));
            BilTestHarness.CheckBilInvalid("保留名家族余部非法字符", m,
                "非法局部变量名");

            // ===== §6.4 类型严格相等（TypesCompatible 收紧为 canonical 全等）=====
            // 手工模块：main(a: sourceType, b: targetType) 参数入口已赋值
            // （DA 免扰），entry 内 set.var b = a 触发两端类型比对

            // 内建标量别名 ↔ canonical 是同一类型的两种拼写（§6.2）——仍兼容
            BilTestHarness.CheckBilValid("内建别名兼容（.i32 ↔ core::i32，正例）",
                TypeCompatModule(".i32", "core::i32"));
            BilTestHarness.CheckBilValid("内建别名兼容（core::String ↔ .string，正例）",
                TypeCompatModule("core::String", ".string"));
            // §6.3：无边界 .typeid ≡ .typeid<.any>
            BilTestHarness.CheckBilValid("无边界 .typeid ≡ .typeid<.any>（正例）",
                TypeCompatModule(".typeid", ".typeid<.any>"));
            // 标准构造头 ↔ canonical 泛型宿主（§6.3）——发射器 .kwargs 契约
            // 形态（.array<.pair<.string, .any>>）与调用点打包形态
            // （.array<core::Pair<.string, .any>>）的混用即此等价
            var corePair = new BilTypeDeclaration("core::Pair", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            corePair.GenericParameters.Add("TKey");
            corePair.GenericParameters.Add("TValue");
            BilTestHarness.CheckBilValid("构造头别名兼容（.pair ↔ core::Pair 嵌套，正例）",
                TypeCompatModule(".array<.pair<.string, .any>>",
                    ".array<core::Pair<.string, .any>>", corePair));

            // 构造类型实参不同不兼容（§6.4：Array\<Dog> ≢ Array\<Animal>）
            BilTestHarness.CheckBilInvalid("构造类型实参不同不兼容",
                TypeCompatModule(".array<.string>", ".array<.i32>"), "set.var 两端");
            // 不同命名空间的同名类型不兼容
            BilTestHarness.CheckBilInvalid("同名不同命名空间不兼容",
                TypeCompatModule("one::Box", "two::Box",
                    new BilTypeDeclaration("one::Box", BilTypeKind.Class,
                        new BilAccessibilityModifier(BilAccessibility.Public)),
                    new BilTypeDeclaration("two::Box", BilTypeKind.Class,
                        new BilAccessibilityModifier(BilAccessibility.Public))),
                "set.var 两端");
            // 嵌套构造逐实参递归全等
            BilTestHarness.CheckBilInvalid("嵌套构造实参不同不兼容",
                TypeCompatModule(".array<.array<.string>>", ".array<.array<.i32>>"),
                "set.var 两端");
            // 同名不同元数不兼容（Wrap ≢ Wrap\<T\>）
            var wrapPlainCompat = new BilTypeDeclaration("com.example::Wrap", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            var wrapGenericCompat = new BilTypeDeclaration("com.example::Wrap", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            wrapGenericCompat.GenericParameters.Add("T");
            BilTestHarness.CheckBilInvalid("同名不同元数不兼容（Wrap ≢ Wrap<.i32>）",
                TypeCompatModule("com.example::Wrap", "com.example::Wrap<.i32>",
                    wrapPlainCompat, wrapGenericCompat),
                "set.var 两端");
        }

        // Vec 的索引运算符对（get 返回 .string / set 元素 .string，索引皆 .i32）
        private static BilSimpleMemberDeclaration[] BothIndexOperators()
        {
            return new[]
            {
                IndexOperator("Vec$$getAtIndex(index:.i32)@.string", "getAtIndex"),
                IndexOperator("Vec$$setAtIndex(index:.i32,element:.string)@.void", "setAtIndex"),
            };
        }

        // M84：字段-Value get.wrapper.field 手工模块——Hero#hp 带 wrapped(Clamped)
        private static BilModule FieldValueModule(params BilInstruction[] body)
        {
            var module = new BilModule();
            var clamped = new BilTypeDeclaration("core.clamp::Clamped", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            clamped.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "core.clamp::Clamped#min@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            module.LocalSymbols.Add(clamped);

            var hero = new BilTypeDeclaration("com.example::Hero", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            hero.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "com.example::Hero#hp@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilWrappedModifier("core.clamp::Clamped"),
                }));
            module.LocalSymbols.Add(hero);

            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main(hero:com.example::Hero)@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var main = new BilFunction("$main(hero:com.example::Hero)@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Args.Add(new BilArgDeclaration("hero", "com.example::Hero"));
            main.Vars.Add(new BilVarDeclaration("core.clamp::Clamped", "w"));
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.AddRange(body);
            entry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            // x 未赋值——加 load 免 DA（若 body 未写 x）
            module.Resources.Add(new BilScalarResource("R_X", BilScalarType.I32, "0"));
            entry.Instructions.Insert(0, new LoadInstruction(
                (BilScalarResource)module.Resources[0], BilOp.Var("x")));
            return module;
        }

        // S11 手工模块（§12.3/§13.3，M88）：core.logging::Logged wrapper（rich，
        // 实例字段 level）+ com.example::Service class（wrapped 应用标记）+
        // RequestResult enum-struct + main(svc, e)——正例与负例共用
        private static BilModule S11Module(params BilInstruction[] body)
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_FC", BilScalarType.I32, "0"));
            module.Resources.Add(new BilScalarResource("R_X", BilScalarType.I32, "0"));
            module.Resources.Add(new BilScalarResource("R_LV", BilScalarType.String, "TRACE"));

            var logged = new BilTypeDeclaration("core.logging::Logged", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            logged.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "core.logging::Logged#level@.string",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            module.LocalSymbols.Add(logged);

            var service = new BilTypeDeclaration("com.example::Service", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilWrappedModifier("core.logging::Logged"));
            module.LocalSymbols.Add(service);

            var result = new BilTypeDeclaration("com.example::RequestResult",
                BilTypeKind.EnumStruct, new BilAccessibilityModifier(BilAccessibility.Public));
            result.Members.Add(new BilCaseDeclaration("com.example::RequestResult.Success"));
            result.Members.Add(new BilCaseDeclaration("com.example::RequestResult.Failed",
                new[] { new BilCaseParameter("errorCode", ".i32") },
                discriminantResource: "R_FC"));
            module.LocalSymbols.Add(result);

            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main(svc:com.example::Service,e:com.example::RequestResult)@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var main = new BilFunction(
                "$main(svc:com.example::Service,e:com.example::RequestResult)@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Args.Add(new BilArgDeclaration("svc", "com.example::Service"));
            main.Args.Add(new BilArgDeclaration("e", "com.example::RequestResult"));
            main.Vars.Add(new BilVarDeclaration(".string", "lv"));
            main.Vars.Add(new BilVarDeclaration(".bool", "b"));
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(
                (BilScalarResource)module.Resources[1], BilOp.Var("x")));
            entry.Instructions.Add(new LoadInstruction(
                (BilScalarResource)module.Resources[2], BilOp.Var("lv")));
            entry.Instructions.AddRange(body);
            entry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        // §14.3 new.case 校验模块：enum-struct E 含 init(v:.i32)（字段 v +
        // 映射赋值 fn 定义）与 Fixed（空洞）/Param（单洞）两 case；E2 无
        // init 声明。main(x i32=42, s string) + .t0/.t1 enum 临时
        private static BilModule EnumCaseInitModule(params BilInstruction[] body)
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_42", BilScalarType.I32, "42"));
            module.Resources.Add(new BilScalarResource("R_S", BilScalarType.String, "x"));

            var e = new BilTypeDeclaration("E", BilTypeKind.EnumStruct,
                new BilAccessibilityModifier(BilAccessibility.Public));
            e.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field, "E#v@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Const),
                }));
            e.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "E$init(v:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            e.Members.Add(new BilCaseDeclaration("E.Fixed"));
            e.Members.Add(new BilCaseDeclaration("E.Param",
                new[] { new BilCaseParameter("v", ".i32") }));
            module.LocalSymbols.Add(e);

            var e2 = new BilTypeDeclaration("E2", BilTypeKind.EnumStruct,
                new BilAccessibilityModifier(BilAccessibility.Public));
            e2.Members.Add(new BilCaseDeclaration("E2.Lone"));
            module.LocalSymbols.Add(e2);

            // init fn 定义（映射赋值体；§21.8 const 实例字段 init 豁免）
            var init = new BilFunction("E$init(v:.i32)@.void");
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", "E"));
            init.Args.Add(new BilArgDeclaration("v", ".i32"));
            var initEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            initEntry.Instructions.Add(new SetFieldInstruction(BilOp.Var("v"),
                BilOp.Var(".this"), BilOp.Field("E#v@.i32")));
            initEntry.Instructions.Add(new RetInstruction());
            init.Blocks.Add(initEntry);
            module.Functions.Add(init);

            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32", new BilModifier[]
                { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
            main.Vars.Add(new BilVarDeclaration(".string", "s"));
            main.Vars.Add(new BilVarDeclaration("E", ".t0"));
            main.Vars.Add(new BilVarDeclaration("E2", ".t1"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(
                (BilScalarResource)module.Resources[0], BilOp.Var("x")));
            entry.Instructions.Add(new LoadInstruction(
                (BilScalarResource)module.Resources[1], BilOp.Var("s")));
            entry.Instructions.AddRange(body);
            entry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        // V3：Box 类型 + typeid/fieldid 槽，供 indirect 全家校验
        private static BilModule V3IndirectModule(params BilInstruction[] body)
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_0", BilScalarType.I32, "0"));
            var box = new BilTypeDeclaration("Box", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilWrappedModifier("Wrap"));
            box.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field, "Box#n@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            box.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.StaticField,
                "Box#.static.count@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            box.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method, "Box$init()@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            module.LocalSymbols.Add(box);
            var wrap = new BilTypeDeclaration("Wrap", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            module.LocalSymbols.Add(wrap);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
            main.Vars.Add(new BilVarDeclaration("Box", "obj"));
            main.Vars.Add(new BilVarDeclaration("Box", "casted"));
            main.Vars.Add(new BilVarDeclaration(".nullable<Box>", "safe"));
            main.Vars.Add(new BilVarDeclaration(".typeid<Box>", "tid"));
            main.Vars.Add(new BilVarDeclaration(".typeid<Wrap>", "wid"));
            main.Vars.Add(new BilVarDeclaration("Wrap", "w"));
            main.Vars.Add(new BilVarDeclaration(".fieldid<Box, .i32, instance>", "fid"));
            main.Vars.Add(new BilVarDeclaration(".fieldid<Box, .i32, static>", "sfid"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("x")));
            entry.Instructions.Add(new GetIdTypeInstruction(BilOp.Type("Box"), BilOp.Var("tid")));
            entry.Instructions.Add(new GetIdTypeInstruction(BilOp.Type("Wrap"), BilOp.Var("wid")));
            entry.Instructions.Add(new GetIdFieldInstruction(BilOp.Field("Box#n@.i32"),
                BilOp.Var("fid")));
            entry.Instructions.Add(new GetIdFieldInstruction(BilOp.Field("Box#.static.count@.i32"),
                BilOp.Var("sfid")));
            entry.Instructions.Add(new NewInstruction(BilOp.Type("Box"), BilOp.Var("obj"),
                Array.Empty<BilVariableOperand>()));
            entry.Instructions.AddRange(body);
            entry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            var init = new BilFunction("Box$init()@.void");
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", "Box"));
            var initEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            initEntry.Instructions.Add(new RetInstruction());
            init.Blocks.Add(initEntry);
            module.Functions.Add(init);
            return module;
        }

        // M88：proxy 模板 fn 含 get.self + invoke fn(..inner) 正例
        private static BilModule ProxyTemplateModule(bool includeSelfInner)
        {
            var module = MinimalModule(out _, out _);
            var logged = new BilTypeDeclaration("core.logging::Logged", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            logged.GenericParameters.Add("TTarget");
            var proxySym = "core.logging::Logged$.proxy.doSomething(arg:.i32)@.string";
            logged.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method, proxySym,
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilWrapperProxyModifier(BilProxyKind.Specific),
                }));
            module.LocalSymbols.Add(logged);
            var fn = new BilFunction(proxySym);
            fn.Args.Add(new BilArgDeclaration(".return", ".string"));
            fn.Args.Add(new BilArgDeclaration(".this", "core.logging::Logged"));
            fn.Args.Add(new BilArgDeclaration("arg", ".i32"));
            fn.Vars.Add(new BilVarDeclaration(".generic<$.generic.TTarget>", "self"));
            fn.Vars.Add(new BilVarDeclaration(".string", "r"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            if (includeSelfInner)
            {
                entry.Instructions.Add(new GetSelfInstruction(BilOp.Var("self")));
                entry.Instructions.Add(new InvokeInstruction(
                    BilOp.Fn(BilSpellings.InnerReservedFunction), BilOp.Var("r"),
                    new[] { BilOp.Var("arg") }));
                entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            }
            else
            {
                entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            }
            fn.Blocks.Add(entry);
            module.Functions.Add(fn);
            // MinimalModule 已有 $main——保留
            return module;
        }

        private static BilModule OrdinaryFnWithGetSelfModule()
        {
            var module = MinimalModule(out _, out var entry);
            module.Functions[0].Vars.Add(new BilVarDeclaration(".i32", "self"));
            entry.Instructions.Insert(0, new GetSelfInstruction(BilOp.Var("self")));
            return module;
        }

        // wildcard 全形状负例：fn 声明了 symbol 保留首参，但 invoke fn(..inner)
        // 操作数为空（未显式携带 symbol）→ §15.4 验证错误。
        private static BilModule WildcardInnerMissingReservedModule()
        {
            var module = MinimalModule(out _, out _);
            var logged = new BilTypeDeclaration("core.logging::Logged", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            logged.GenericParameters.Add("TTarget");
            var proxySym = "core.logging::Logged$.proxy.*(symbol:.string)@.any";
            logged.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method, proxySym,
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilWrapperProxyModifier(BilProxyKind.Wildcard),
                }));
            module.LocalSymbols.Add(logged);
            var fn = new BilFunction(proxySym);
            fn.Args.Add(new BilArgDeclaration(".return", ".any"));
            fn.Args.Add(new BilArgDeclaration(".this", "core.logging::Logged"));
            fn.Args.Add(new BilArgDeclaration("symbol", ".string"));
            fn.Vars.Add(new BilVarDeclaration(".any", "r"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new InvokeInstruction(
                BilOp.Fn(BilSpellings.InnerReservedFunction), BilOp.Var("r"),
                Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            fn.Blocks.Add(entry);
            module.Functions.Add(fn);
            return module;
        }

        // #27⑦：proxy 模板 invoke fn(..inner) 引用未在 .args 声明的 .generic.TNamedArgs
        private static BilModule InnerUndeclaredGenericModule()
        {
            var module = ProxyTemplateModule(includeSelfInner: false);
            var fn = module.Functions.Single(f => f.Symbol.Contains(".proxy."));
            var entry = fn.Blocks[0];
            entry.Instructions.Clear();
            entry.Instructions.Add(new InvokeInstruction(
                BilOp.Fn(BilSpellings.InnerReservedFunction), BilOp.Var("r"),
                new[] { BilOp.Var(".generic.TNamedArgs"), BilOp.Var("arg") }));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            return module;
        }

        // ===== §16.5 推广：if/call/try 的 breakid 绑定与 token 作用域 =====
        private static void TestRegionBreakIdBinding()
        {
            TestHarness.Section("BilVerifier §16.5 region breakid（if/call/try）");

            foreach (var kind in new[] { "if", "call", "try" })
            {
                // 正例：合法绑定 + child 内 break 命中自身 token
                BilTestHarness.CheckBilValid(kind + " breakid 绑定与区域内 break（正例）",
                    RegionModule(kind, ".breakid",
                        child => child.Instructions.Add(new BreakInstruction(BilOp.Var("bk")))));

                // §21.2：breakid 未声明
                BilTestHarness.CheckBilInvalid(kind + " 的 breakid 未声明",
                    RegionModule(kind, null, _ => { }), "未声明");

                // §21.6：绑定非 .breakid 类型变量
                BilTestHarness.CheckBilInvalid(kind + " 绑定非 .breakid 变量",
                    RegionModule(kind, ".i32", _ => { }), "只能绑定 .breakid 类型变量");

                // §21.5：token 作用域外 break
                BilTestHarness.CheckBilInvalid(kind + " token 作用域外 break",
                    RegionModule(kind, ".breakid", _ => { },
                        entry => entry.Instructions.Insert(2,
                            new BreakInstruction(BilOp.Var("bk")))),
                    "不在当前活跃结构作用域内");

                // §16.5：continue 不得引用非 loop token
                BilTestHarness.CheckBilInvalid("continue 引用 " + kind + " token",
                    RegionModule(kind, ".breakid",
                        child => child.Instructions.Add(new ContinueInstruction(BilOp.Var("bk")))),
                    "continue 不得引用非 loop");
            }

            // §21.6：同一 .breakid 被两条 region 指令二次绑定
            var dup = RegionModule("if", ".breakid", _ => { });
            var secondChild = new BilBlock("if-child2");
            dup.Functions[0].Blocks.Add(secondChild);
            dup.Functions[0].Blocks[0].Instructions.Insert(3, new IfInstruction(
                BilOp.Var("c"), secondChild, null, BilOp.Var("bk")));
            BilTestHarness.CheckBilInvalid("region breakid 二次绑定", dup, "二次绑定");
        }

        // if/call/try region 模块骨架（MinimalModule 改造）：声明 breakid
        // 变量 bk（breakIdType 为 null = 不声明，模拟未声明负例）、bool
        // 变量 c（if 条件）与 try 异常槽 ex；entry 依次 load c → region
        // 指令（child 为唯一子块，breakid = $bk）；fillEntry 可向 entry
        // 追加指令
        private static BilModule RegionModule(string kind, string? breakIdType,
            Action<BilBlock> fillChild, Action<BilBlock>? fillEntry = null)
        {
            var m = MinimalModule(out _, out var entry);
            m.Resources.Add(new BilScalarResource("R_BT", BilScalarType.Bool, "true"));
            if (breakIdType != null)
            {
                m.Functions[0].Vars.Add(new BilVarDeclaration(breakIdType, "bk"));
            }
            m.Functions[0].Vars.Add(new BilVarDeclaration(".bool", "c"));
            m.Functions[0].Vars.Add(new BilVarDeclaration(".nullable<core::Exception>", "ex"));
            var child = new BilBlock(kind + "-child");
            fillChild(child);
            m.Functions[0].Blocks.Add(child);
            BilInstruction instruction = kind switch
            {
                "if" => new IfInstruction(BilOp.Var("c"), child, null, BilOp.Var("bk")),
                "call" => new CallBlockInstruction(child, BilOp.Var("bk")),
                _ => new TryInstruction(child, BilOp.Var("ex"), EmptyRegionCatchTable(m), null,
                    BilOp.Var("bk")),
            };
            entry.Instructions.Insert(1,
                new LoadInstruction(m.Resources[m.Resources.Count - 1], BilOp.Var("c")));
            entry.Instructions.Insert(2, instruction);
            fillEntry?.Invoke(entry);
            return m;
        }

        private static BilCatchTableResource EmptyRegionCatchTable(BilModule module)
        {
            var table = new BilCatchTableResource("R_CT", Array.Empty<BilCatchEntry>());
            module.Resources.Add(table);
            return table;
        }

        private static void TestAwaitInstructions()
        {
            TestHarness.Section("BilVerifier await");
            BilTestHarness.CheckBilValid("await Task 正例", AwaitModule(
                "core.coroutine::Task", null, null));
            BilTestHarness.CheckBilValid("await Task<T> 匹配正例", AwaitModule(
                "core.coroutine::Task<.i32>", ".i32", "result"));
            BilTestHarness.CheckBilInvalid("await Task<T> 缺 RESULT", AwaitModule(
                "core.coroutine::Task<.i32>", null, null), "必须带 RESULT");
            BilTestHarness.CheckBilInvalid("await Task<T> 错误 RESULT", AwaitModule(
                "core.coroutine::Task<.i32>", ".string", "result"), "严格等于");
            BilTestHarness.CheckBilInvalid("await 非 Task", AwaitModule(
                ".i32", null, null), "必须是精确");
        }

        private static void TestYieldInstructions()
        {
            TestHarness.Section("BilVerifier yield");
            BilTestHarness.CheckBilValid("裸 yield 正例", YieldModule(null, false));
            BilTestHarness.CheckBilValid("PollingAlarm 正例", YieldModule(
                "core.coroutine::PollingAlarm", false));
            BilTestHarness.CheckBilValid("EventAlarm 正例", YieldModule(
                "core.coroutine::EventAlarm", false));
            BilTestHarness.CheckBilValid("Alarm 子类正例", YieldModule("UserPolling", true));
            BilTestHarness.CheckBilInvalid("yield 非 Alarm", YieldModule(".i32", false),
                "必须可赋值");
            BilTestHarness.CheckBilInvalid("yield 未赋值 Alarm", YieldModule(
                "core.coroutine::EventAlarm", false, unassigned: true), "赋值前被读取");
        }

        private static BilModule YieldModule(string? alarmType, bool derived,
            bool unassigned = false)
        {
            var module = new BilModule();
            var polling = new BilTypeDeclaration("core.coroutine::PollingAlarm", BilTypeKind.Class);
            var eventAlarm = new BilTypeDeclaration("core.coroutine::EventAlarm", BilTypeKind.Class);
            module.ExternalSymbols.Add(polling);
            module.ExternalSymbols.Add(eventAlarm);
            if (derived)
            {
                var user = new BilTypeDeclaration("UserPolling", BilTypeKind.Class)
                {
                    ExtendsType = "core.coroutine::PollingAlarm"
                };
                module.ExternalSymbols.Add(user);
            }
            var symbol = alarmType == null
                ? "$f()@.void"
                : "$f(alarm:" + alarmType + ")@.void";
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method, symbol,
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var function = new BilFunction(symbol);
            function.Args.Add(new BilArgDeclaration(".return", ".void"));
            if (alarmType != null)
            {
                if (unassigned) function.Vars.Add(new BilVarDeclaration(alarmType, "alarm"));
                else function.Args.Add(new BilArgDeclaration("alarm", alarmType));
            }
            var block = new BilBlock("entry", BilBlockModifier.Entrypoint);
            block.Instructions.Add(new YieldInstruction(
                alarmType == null ? null : BilOp.Var("alarm")));
            block.Instructions.Add(new RetInstruction());
            function.Blocks.Add(block);
            module.Functions.Add(function);
            return module;
        }

        private static BilModule AwaitModule(string taskType, string? resultType,
            string? resultName)
        {
            var module = new BilModule();
            var taskDeclaration = new BilTypeDeclaration("core.coroutine::Task", BilTypeKind.Class);
            if (taskType.Contains("<", StringComparison.Ordinal))
            {
                taskDeclaration.GenericParameters.Add("TResult");
            }
            module.ExternalSymbols.Add(taskDeclaration);
            var symbol = "$f(task:" + taskType + ")@.void";
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method, symbol,
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var function = new BilFunction(symbol);
            function.Args.Add(new BilArgDeclaration(".return", ".void"));
            function.Args.Add(new BilArgDeclaration("task", taskType));
            if (resultName != null)
            {
                function.Vars.Add(new BilVarDeclaration(resultType!, resultName));
            }
            var block = new BilBlock("entry", BilBlockModifier.Entrypoint);
            block.Instructions.Add(new AwaitInstruction(BilOp.Var("task"),
                resultName == null ? null : BilOp.Var(resultName)));
            block.Instructions.Add(new RetInstruction());
            function.Blocks.Add(block);
            module.Functions.Add(function);
            return module;
        }

        private static BilModule SuperInvokeModule(bool validReceiver)
        {
            var module = new BilModule();
            var baseType = new BilTypeDeclaration("Base", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Open));
            var derived = new BilTypeDeclaration("Derived", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            derived.ExtendsType = "Base";
            var symbol = "Derived$f()@.i32";
            derived.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method, symbol,
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Override),
                }));
            module.LocalSymbols.Add(baseType);
            module.LocalSymbols.Add(derived);
            var fn = new BilFunction(symbol);
            fn.Args.Add(new BilArgDeclaration(".return", ".i32"));
            fn.Args.Add(new BilArgDeclaration(".this", "Derived"));
            fn.Vars.Add(new BilVarDeclaration(".i32", "r"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new InvokeInstruction(BilOp.Fn(BilSpellings.SuperReservedFunction),
                BilOp.Var("r"), new[] { BilOp.Var(validReceiver ? ".this" : "r") }));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            fn.Blocks.Add(entry);
            module.Functions.Add(fn);
            return module;
        }

        private static BilModule InnerWrongGenericOrderModule()
        {
            var module = ProxyTemplateModule(includeSelfInner: false);
            var fn = module.Functions.Single(f => f.Symbol.Contains(".proxy."));
            fn.Args.Insert(2, new BilArgDeclaration(".generic.TNamedArgs",
                ".map<.string,.typeid<.any>>"));
            fn.Args.Insert(3, new BilArgDeclaration(".generic.TUnnamedArgs",
                ".array<.typeid<.any>>"));
            var entry = fn.Blocks[0];
            entry.Instructions.Clear();
            entry.Instructions.Add(new InvokeInstruction(
                BilOp.Fn(BilSpellings.InnerReservedFunction), BilOp.Var("r"), new[]
            {
                BilOp.Var(".generic.TUnnamedArgs"),
                BilOp.Var(".generic.TNamedArgs"),
                BilOp.Var("arg"),
            }));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            return module;
        }

        private static BilModule InnerWithReceiverModule()
        {
            var module = ProxyTemplateModule(includeSelfInner: false);
            var fn = module.Functions.Single(f => f.Symbol.Contains(".proxy."));
            var entry = fn.Blocks[0];
            entry.Instructions.Clear();
            entry.Instructions.Add(new InvokeInstruction(
                BilOp.Fn(BilSpellings.InnerReservedFunction), BilOp.Var("r"),
                new[] { BilOp.Var(".this"), BilOp.Var("arg") }));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            return module;
        }

        // S10 同名不同元数共存手工模块（验证器修复批次）：com.example::Wrap
        // （init()）与 com.example::Wrap\<T\>（init(x: .i32)）——external
        // 声明形态聚焦指令侧（免 fn 定义）；genericFirst 控制两声明的登记
        // 顺序；main 内 new 两形态各一次（x 已由 R_0 赋值，实参类型 .i32）
        private static BilModule ArityCoexistModule(bool genericFirst)
        {
            var module = MinimalModule(out _, out var entry);
            var wrapPlain = new BilTypeDeclaration("com.example::Wrap", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            wrapPlain.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "com.example::Wrap$init()@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            var wrapGeneric = new BilTypeDeclaration("com.example::Wrap", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            wrapGeneric.GenericParameters.Add("T");
            wrapGeneric.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "com.example::Wrap$init(x:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            if (genericFirst)
            {
                module.ExternalSymbols.Add(wrapGeneric);
                module.ExternalSymbols.Add(wrapPlain);
            }
            else
            {
                module.ExternalSymbols.Add(wrapPlain);
                module.ExternalSymbols.Add(wrapGeneric);
            }
            module.Functions[0].Vars.Add(new BilVarDeclaration("com.example::Wrap", "w0"));
            module.Functions[0].Vars.Add(new BilVarDeclaration("com.example::Wrap<.i32>", "w1"));
            entry.Instructions.Insert(1, new NewInstruction(
                BilOp.Type("com.example::Wrap"), BilOp.Var("w0"), new List<BilVariableOperand>()));
            entry.Instructions.Insert(2, new NewInstruction(
                BilOp.Type("com.example::Wrap<.i32>"), BilOp.Var("w1"),
                new List<BilVariableOperand> { BilOp.Var("x") }));
            return module;
        }

        // §6.4 类型严格相等手工模块（TypesCompatible canonical 全等收紧批次）：
        // main(a: sourceType, b: targetType) 两参数入口已赋值（DA 免构造），
        // entry 内 load x 后 set.var b = a 触发两端类型比对，ret x 收尾；
        // extraTypes 登记额外类型声明（core::Pair/one::Box 等使类型引用
        // 可解析，§21.2——构造头免检声明，用户 canonical 类型须登记）
        private static BilModule TypeCompatModule(string sourceType, string targetType,
            params BilTypeDeclaration[] extraTypes)
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_0", BilScalarType.I32, "0"));
            foreach (var type in extraTypes)
            {
                module.ExternalSymbols.Add(type);
            }
            var symbol = $"$main(a:{sourceType},b:{targetType})@.i32";
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                symbol, new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var main = new BilFunction(symbol);
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Args.Add(new BilArgDeclaration("a", sourceType));
            main.Args.Add(new BilArgDeclaration("b", targetType));
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(
                (BilScalarResource)module.Resources[0], BilOp.Var("x")));
            entry.Instructions.Add(new SetVarInstruction(BilOp.Var("b"), BilOp.Var("a")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        // §21.8 enum struct 实例字段 init 全路径 set.field（§14.3 无零值）
        private const string EnumColorType = "com.example::Color";
        private const string EnumHostType = "com.example::Host";
        private const string EnumKindField = "com.example::Host#kind@com.example::Color";
        private const string EnumHostInit = "com.example::Host$init(c:com.example::Color)@.void";
        private const string EnumBaseType = "com.example::Base";
        private const string EnumBaseKindField = "com.example::Base#kind@com.example::Color";
        private const string EnumDerivedType = "com.example::Derived";
        private const string EnumDerivedInit = "com.example::Derived$init(c:com.example::Color)@.void";

        private static void TestEnumStructInstanceFieldInit()
        {
            TestHarness.Section("BilVerifier §21.8 enum struct 实例字段");

            BilTestHarness.CheckBilValid("init 单路径 set.field",
                EnumFieldInitModule((module, init, entry) =>
                {
                    entry.Instructions.Add(new SetFieldInstruction(BilOp.Var("c"),
                        BilOp.Var(".this"), BilOp.Field(EnumKindField)));
                    entry.Instructions.Add(new RetInstruction());
                }));

            BilTestHarness.CheckBilValid("if 双分支都 set.field",
                EnumFieldInitModule((module, init, entry) =>
                {
                    var thenBlock = new BilBlock("then");
                    thenBlock.Instructions.Add(new SetFieldInstruction(BilOp.Var("c"),
                        BilOp.Var(".this"), BilOp.Field(EnumKindField)));
                    var elseBlock = new BilBlock("else");
                    elseBlock.Instructions.Add(new SetFieldInstruction(BilOp.Var("c"),
                        BilOp.Var(".this"), BilOp.Field(EnumKindField)));
                    init.Blocks.Add(thenBlock);
                    init.Blocks.Add(elseBlock);
                    entry.Instructions.Add(new LoadInstruction(
                        (BilScalarResource)module.Resources[1], BilOp.Var("cond")));
                    entry.Instructions.Add(new IfInstruction(
                        BilOp.Var("cond"), thenBlock, elseBlock, BilOp.Var("brk")));
                    entry.Instructions.Add(new RetInstruction());
                }));

            BilTestHarness.CheckBilValid("loop.rev body 内 set.field（至少一次）",
                EnumFieldInitModule((module, init, entry) =>
                {
                    var body = new BilBlock("body");
                    body.Instructions.Add(new SetFieldInstruction(BilOp.Var("c"),
                        BilOp.Var(".this"), BilOp.Field(EnumKindField)));
                    var judge = new BilBlock("judge");
                    judge.Instructions.Add(new LoadInstruction(
                        (BilScalarResource)module.Resources[1], BilOp.Var("cond")));
                    init.Blocks.Add(body);
                    init.Blocks.Add(judge);
                    entry.Instructions.Add(new LoopInstruction(
                        BilOp.Var("cond"), body, null, judge, BilOp.Var("brk"), isRev: true));
                    entry.Instructions.Add(new RetInstruction());
                }));

            BilTestHarness.CheckBilValid("loop.rev set.field 后再 break 本 region",
                EnumFieldInitModule((module, init, entry) =>
                {
                    init.Vars.Add(new BilVarDeclaration(".breakid", "brkif"));
                    var thenBlock = new BilBlock("then");
                    thenBlock.Instructions.Add(new BreakInstruction(BilOp.Var("brk")));
                    var body = new BilBlock("body");
                    body.Instructions.Add(new SetFieldInstruction(BilOp.Var("c"),
                        BilOp.Var(".this"), BilOp.Field(EnumKindField)));
                    body.Instructions.Add(new LoadInstruction(
                        (BilScalarResource)module.Resources[1], BilOp.Var("cond")));
                    body.Instructions.Add(new IfInstruction(
                        BilOp.Var("cond"), thenBlock, null, BilOp.Var("brkif")));
                    var judge = new BilBlock("judge");
                    judge.Instructions.Add(new LoadInstruction(
                        (BilScalarResource)module.Resources[1], BilOp.Var("cond")));
                    init.Blocks.Add(thenBlock);
                    init.Blocks.Add(body);
                    init.Blocks.Add(judge);
                    entry.Instructions.Add(new LoopInstruction(
                        BilOp.Var("cond"), body, null, judge, BilOp.Var("brk"), isRev: true));
                    entry.Instructions.Add(new RetInstruction());
                }));

            BilTestHarness.CheckBilValid("try-finally 在 finally set.field",
                EnumFieldInitModule((module, init, entry) =>
                {
                    var catchTable = new BilCatchTableResource("R_EF",
                        Array.Empty<BilCatchEntry>());
                    module.Resources.Add(catchTable);
                    var tryBody = new BilBlock("tryBody");
                    var finallyBlock = new BilBlock("finally");
                    finallyBlock.Instructions.Add(new SetFieldInstruction(BilOp.Var("c"),
                        BilOp.Var(".this"), BilOp.Field(EnumKindField)));
                    init.Blocks.Add(tryBody);
                    init.Blocks.Add(finallyBlock);
                    entry.Instructions.Add(new TryInstruction(tryBody, BilOp.Var("ex"),
                        catchTable, finallyBlock, BilOp.Var("brk")));
                    entry.Instructions.Add(new RetInstruction());
                }));

            BilTestHarness.CheckBilValid("派生 init 设置基类 enum 字段",
                EnumFieldDerivedInitModule(setBaseField: true));

            BilTestHarness.CheckBilInvalid("init 完全没 set.field",
                EnumFieldInitModule((module, init, entry) =>
                {
                    entry.Instructions.Add(new RetInstruction());
                }), "全部执行路径");

            BilTestHarness.CheckBilInvalid("if 只在一分支 set.field",
                EnumFieldInitModule((module, init, entry) =>
                {
                    var thenBlock = new BilBlock("then");
                    thenBlock.Instructions.Add(new SetFieldInstruction(BilOp.Var("c"),
                        BilOp.Var(".this"), BilOp.Field(EnumKindField)));
                    var elseBlock = new BilBlock("else");
                    init.Blocks.Add(thenBlock);
                    init.Blocks.Add(elseBlock);
                    entry.Instructions.Add(new LoadInstruction(
                        (BilScalarResource)module.Resources[1], BilOp.Var("cond")));
                    entry.Instructions.Add(new IfInstruction(
                        BilOp.Var("cond"), thenBlock, elseBlock, BilOp.Var("brk")));
                    entry.Instructions.Add(new RetInstruction());
                }), "全部执行路径");

            BilTestHarness.CheckBilInvalid("loop.rev body 内 break 跳过 set.field",
                EnumFieldInitModule((module, init, entry) =>
                {
                    init.Vars.Add(new BilVarDeclaration(".breakid", "brkif"));
                    var thenBlock = new BilBlock("then");
                    thenBlock.Instructions.Add(new BreakInstruction(BilOp.Var("brk")));
                    var body = new BilBlock("body");
                    body.Instructions.Add(new LoadInstruction(
                        (BilScalarResource)module.Resources[1], BilOp.Var("cond")));
                    body.Instructions.Add(new IfInstruction(
                        BilOp.Var("cond"), thenBlock, null, BilOp.Var("brkif")));
                    body.Instructions.Add(new SetFieldInstruction(BilOp.Var("c"),
                        BilOp.Var(".this"), BilOp.Field(EnumKindField)));
                    var judge = new BilBlock("judge");
                    judge.Instructions.Add(new LoadInstruction(
                        (BilScalarResource)module.Resources[1], BilOp.Var("cond")));
                    init.Blocks.Add(thenBlock);
                    init.Blocks.Add(body);
                    init.Blocks.Add(judge);
                    entry.Instructions.Add(new LoopInstruction(
                        BilOp.Var("cond"), body, null, judge, BilOp.Var("brk"), isRev: true));
                    entry.Instructions.Add(new RetInstruction());
                }), "全部执行路径");

            BilTestHarness.CheckBilInvalid("正向 loop body 内 set.field（可能零次）",
                EnumFieldInitModule((module, init, entry) =>
                {
                    var body = new BilBlock("body");
                    body.Instructions.Add(new SetFieldInstruction(BilOp.Var("c"),
                        BilOp.Var(".this"), BilOp.Field(EnumKindField)));
                    var judge = new BilBlock("judge");
                    judge.Instructions.Add(new LoadInstruction(
                        (BilScalarResource)module.Resources[1], BilOp.Var("cond")));
                    init.Blocks.Add(body);
                    init.Blocks.Add(judge);
                    entry.Instructions.Add(new LoadInstruction(
                        (BilScalarResource)module.Resources[1], BilOp.Var("cond")));
                    entry.Instructions.Add(new LoopInstruction(
                        BilOp.Var("cond"), body, null, judge, BilOp.Var("brk"), isRev: false));
                    entry.Instructions.Add(new RetInstruction());
                }), "全部执行路径");

            BilTestHarness.CheckBilInvalid("set.field 之前 get.field",
                EnumFieldInitModule((module, init, entry) =>
                {
                    entry.Instructions.Add(new GetFieldInstruction(BilOp.Var(".this"),
                        BilOp.Var("tmp"), BilOp.Field(EnumKindField)));
                    entry.Instructions.Add(new SetFieldInstruction(BilOp.Var("c"),
                        BilOp.Var(".this"), BilOp.Field(EnumKindField)));
                    entry.Instructions.Add(new RetInstruction());
                }), "于 set.field 之前被 get.field 读取");

            BilTestHarness.CheckBilInvalid("另一 init 重载未 set.field",
                EnumFieldTwoInitModule(), "全部执行路径");

            BilTestHarness.CheckBilValid("非 enum 实例字段不受本规则约束",
                NonEnumFieldInitModule());
            BilTestHarness.CheckBilInvalid("enum 局部变量仍走 DA（读前未写）",
                EnumLocalUnreadModule(), "在赋值前被读取");
        }

        private static BilModule EnumFieldInitModule(
            Action<BilModule, BilFunction, BilBlock> fillInit)
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_0", BilScalarType.I32, "0"));
            module.Resources.Add(new BilScalarResource("R_T", BilScalarType.Bool, "true"));
            var color = new BilTypeDeclaration(EnumColorType, BilTypeKind.EnumStruct,
                new BilAccessibilityModifier(BilAccessibility.Public));
            color.Members.Add(new BilCaseDeclaration(EnumColorType + ".Red"));
            module.LocalSymbols.Add(color);
            var host = new BilTypeDeclaration(EnumHostType, BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            host.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field, EnumKindField,
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            host.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method, EnumHostInit,
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            module.LocalSymbols.Add(host);
            var init = new BilFunction(EnumHostInit);
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", EnumHostType));
            init.Args.Add(new BilArgDeclaration("c", EnumColorType));
            init.Vars.Add(new BilVarDeclaration(".bool", "cond"));
            init.Vars.Add(new BilVarDeclaration(".breakid", "brk"));
            init.Vars.Add(new BilVarDeclaration(EnumColorType, "tmp"));
            init.Vars.Add(new BilVarDeclaration(".nullable<core::Exception>", "ex"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            init.Blocks.Add(entry);
            module.Functions.Add(init);
            fillInit(module, init, entry);
            return module;
        }

        private static BilModule EnumFieldDerivedInitModule(bool setBaseField)
        {
            var module = new BilModule();
            var color = new BilTypeDeclaration(EnumColorType, BilTypeKind.EnumStruct,
                new BilAccessibilityModifier(BilAccessibility.Public));
            color.Members.Add(new BilCaseDeclaration(EnumColorType + ".Red"));
            module.LocalSymbols.Add(color);
            var baseType = new BilTypeDeclaration(EnumBaseType, BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Open));
            baseType.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                EnumBaseKindField,
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            module.LocalSymbols.Add(baseType);
            var derived = new BilTypeDeclaration(EnumDerivedType, BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            derived.ExtendsType = EnumBaseType;
            derived.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                EnumDerivedInit,
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            module.LocalSymbols.Add(derived);
            var init = new BilFunction(EnumDerivedInit);
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", EnumDerivedType));
            init.Args.Add(new BilArgDeclaration("c", EnumColorType));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            if (setBaseField)
            {
                entry.Instructions.Add(new SetFieldInstruction(BilOp.Var("c"),
                    BilOp.Var(".this"), BilOp.Field(EnumBaseKindField)));
            }
            entry.Instructions.Add(new RetInstruction());
            init.Blocks.Add(entry);
            module.Functions.Add(init);
            return module;
        }

        private static BilModule EnumFieldTwoInitModule()
        {
            var module = EnumFieldInitModule((m, init, entry) =>
            {
                entry.Instructions.Add(new SetFieldInstruction(BilOp.Var("c"),
                    BilOp.Var(".this"), BilOp.Field(EnumKindField)));
                entry.Instructions.Add(new RetInstruction());
            });
            var host = (BilTypeDeclaration)module.LocalSymbols[1];
            var emptyInitSym = EnumHostType + "$init()@.void";
            host.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method, emptyInitSym,
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            var empty = new BilFunction(emptyInitSym);
            empty.Args.Add(new BilArgDeclaration(".return", ".void"));
            empty.Args.Add(new BilArgDeclaration(".this", EnumHostType));
            var emptyEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            emptyEntry.Instructions.Add(new RetInstruction());
            empty.Blocks.Add(emptyEntry);
            module.Functions.Add(empty);
            return module;
        }

        private static BilModule NonEnumFieldInitModule()
        {
            var module = new BilModule();
            var host = new BilTypeDeclaration(EnumHostType, BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            host.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                EnumHostType + "#n@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var initSym = EnumHostType + "$init()@.void";
            host.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method, initSym,
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            module.LocalSymbols.Add(host);
            var init = new BilFunction(initSym);
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", EnumHostType));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new RetInstruction());
            init.Blocks.Add(entry);
            module.Functions.Add(init);
            return module;
        }

        private static BilModule EnumLocalUnreadModule()
        {
            var module = MinimalModule(out _, out _);
            var color = new BilTypeDeclaration(EnumColorType, BilTypeKind.EnumStruct,
                new BilAccessibilityModifier(BilAccessibility.Public));
            color.Members.Add(new BilCaseDeclaration(EnumColorType + ".Red"));
            module.LocalSymbols.Add(color);
            module.Functions[0].Vars.Add(new BilVarDeclaration(EnumColorType, "c"));
            module.Functions[0].Vars.Add(new BilVarDeclaration(EnumColorType, "d"));
            module.Functions[0].Blocks[0].Instructions.Insert(1,
                new GetVarInstruction(BilOp.Var("c"), BilOp.Var("d")));
            return module;
        }

        // §21.8 init 豁免手工模块：com.example::Entry class（const 实例字段
        // key + const 静态字段 count + init(k)/普通方法 reset(k) 两 fn 定义；
        // 参数入口已赋值、n 先 load 后用，DA 免扰）——writeTarget 选择写入
        // 指令落点：init 体内写实例字段=豁免正例 / 普通方法写实例字段=反例 /
        // init 体内写静态字段=反例（静态不在豁免内，与 P3 对齐）
        private enum ConstWriteTarget { InstanceInInit, InstanceInMethod, StaticInInit }

        private static BilModule ConstWriteModule(ConstWriteTarget writeTarget)
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_CW", BilScalarType.I32, "0"));
            var entry = new BilTypeDeclaration("com.example::Entry", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            entry.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "com.example::Entry#key@.string",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Const),
                }));
            entry.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.StaticField,
                "com.example::Entry#.static.count@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Const),
                }));
            entry.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "com.example::Entry$init(k:.string)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            entry.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "com.example::Entry$reset(k:.string)@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            module.LocalSymbols.Add(entry);

            var init = new BilFunction("com.example::Entry$init(k:.string)@.void");
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", "com.example::Entry"));
            init.Args.Add(new BilArgDeclaration("k", ".string"));
            init.Vars.Add(new BilVarDeclaration(".i32", "n"));
            var initBlock = new BilBlock("entry", BilBlockModifier.Entrypoint);
            initBlock.Instructions.Add(new LoadInstruction(
                (BilScalarResource)module.Resources[0], BilOp.Var("n")));
            if (writeTarget == ConstWriteTarget.InstanceInInit)
            {
                initBlock.Instructions.Add(new SetFieldInstruction(BilOp.Var("k"),
                    BilOp.Var(".this"), BilOp.Field("com.example::Entry#key@.string")));
            }
            if (writeTarget == ConstWriteTarget.StaticInInit)
            {
                initBlock.Instructions.Add(new SetFieldStaticInstruction(BilOp.Var("n"),
                    BilOp.Type("com.example::Entry"),
                    BilOp.Field("com.example::Entry#.static.count@.i32")));
            }
            initBlock.Instructions.Add(new RetInstruction());
            init.Blocks.Add(initBlock);
            module.Functions.Add(init);

            var reset = new BilFunction("com.example::Entry$reset(k:.string)@.void");
            reset.Args.Add(new BilArgDeclaration(".return", ".void"));
            reset.Args.Add(new BilArgDeclaration(".this", "com.example::Entry"));
            reset.Args.Add(new BilArgDeclaration("k", ".string"));
            var resetBlock = new BilBlock("entry", BilBlockModifier.Entrypoint);
            if (writeTarget == ConstWriteTarget.InstanceInMethod)
            {
                resetBlock.Instructions.Add(new SetFieldInstruction(BilOp.Var("k"),
                    BilOp.Var(".this"), BilOp.Field("com.example::Entry#key@.string")));
            }
            resetBlock.Instructions.Add(new RetInstruction());
            reset.Blocks.Add(resetBlock);
            module.Functions.Add(reset);
            return module;
        }

        // §21.8 init 豁免（§13.3 set.wrapper.field，M88 wrapper 链）：Logged
        // wrapper（const 内层字段）+ Service（wrapped 标记 + init）+ init
        // 体内 set.wrapper.field wrapper(W) 写 const 内层字段
        private static BilModule S11InitWrapperFieldModule()
        {
            var module = new BilModule();
            var logged = new BilTypeDeclaration("core.logging::Logged", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            logged.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "core.logging::Logged#level@.string",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Const),
                }));
            module.LocalSymbols.Add(logged);

            var service = new BilTypeDeclaration("com.example::Service", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilWrappedModifier("core.logging::Logged"));
            service.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "com.example::Service$init(lv:.string)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            module.LocalSymbols.Add(service);

            var init = new BilFunction("com.example::Service$init(lv:.string)@.void");
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", "com.example::Service"));
            init.Args.Add(new BilArgDeclaration("lv", ".string"));
            var initBlock = new BilBlock("entry", BilBlockModifier.Entrypoint);
            initBlock.Instructions.Add(new SetWrapperFieldInstruction(BilOp.Var("lv"),
                BilOp.Var(".this"),
                BilOp.Wrapper("core.logging::Logged"),
                BilOp.Field("core.logging::Logged#level@.string")));
            initBlock.Instructions.Add(new RetInstruction());
            init.Blocks.Add(initBlock);
            module.Functions.Add(init);
            return module;
        }
    }
}
