namespace RigiCompiler.Tests
{
    // bug A1 修复回归（§9.2.1）：override 签名匹配纳入 async 一致性——
    // 基类/接口成员与 override 成员的 IsAsync 必须相同，不一致即编译
    // 错误（专项诊断，不复用「无匹配成员」泛化消息）。修复前接口/基类
    // 的 sync 签名可被 async 实现「满足」：经接口调用时静态类型 T 而
    // 运行期实得 Task\<T\>（ReturnType 存 T，调用点按被绑定方法的
    // IsAsync 改写为 Task\<T\>）的类型洞。hiding 场景（无 override 的
    // async 方法与继承 sync 成员同名同签名）同口径拦截；双 async /
    // 双 sync 的合法 override 不受影响。
    public static partial class BinderTests
    {
        private static void TestOverrideFixes()
        {
            TestHarness.Section("P3 Override Async Consistency Fixes (§9.2.1, bug A1)");

            var (closed, _) = BindUnit("""
                pub interface Parent\<T> { }
                pub interface Child\<T> implements Parent\<T> { }
                pub open class Base\<T> implements Child\<T> {
                    pub open func pick(value: T): T { return value }
                    pub open func get\<U>(): i32 { return 1 }
                }
                pub class Derived: Base\<i32> {
                    pub override func pick(value: i32): i32 { return value }
                    pub func pick(value: String): String { return value }
                    pub override func get\<V>(): i32 { return 2 }
                    pub func get\<V, W>(): i32 { return 3 }
                }
                func check(d: Derived) {
                    const p: Parent\<i32> = d
                    const n: i32 = d.pick(1)
                    const s: String = d.pick("text")
                    const a: i32 = d.get\<String>()
                    const b: i32 = d.get\<String, i32>()
                }
                """);
            CheckNoErrors("闭合基类覆写仅去同槽并保留参数与泛型元数重载", closed);
            var (incompatible, _) = BindUnit("""
                pub interface Parent\<T> { }
                pub interface Child\<T> implements Parent\<T> { }
                pub open class Base\<T> implements Child\<T> { }
                pub class Derived: Base\<i32> { }
                func reject(d: Derived) { const p: Parent\<String> = d }
                """);
            TestHarness.CheckSemanticError("传递泛型接口不同实参仍拒绝", incompatible.Diagnostics,
                "Cannot assign");
            // 负例：接口 sync 成员 + async override 实现（bug A1 本体）
            var (unit, _) = BindUnit(
                "pub interface Worker {\n" +
                "    func run(x: i32): i32\n" +
                "}\n" +
                "pub shared class W implements Worker {\n" +
                "    pub init()\n" +
                "    pub async override func run(x: i32): i32 { return (x + 1) }\n" +
                "}\n");
            TestHarness.CheckSemanticError("接口 sync + async override 实现", unit.Diagnostics,
                "'run': 'async' modifier does not match the inherited member");

            // 负例：反向——接口 async 成员 + sync override 实现
            // （A2 起含 async 成员的接口必须 shared，故接口标 shared）
            var (unit2, _) = BindUnit(
                "pub shared interface Worker {\n" +
                "    async func run(x: i32): i32\n" +
                "}\n" +
                "pub shared class W implements Worker {\n" +
                "    pub init()\n" +
                "    pub override func run(x: i32): i32 { return (x + 1) }\n" +
                "}\n");
            TestHarness.CheckSemanticError("接口 async + sync override 实现", unit2.Diagnostics,
                "'run': 'async' modifier does not match the inherited member");

            // 负例：基类 sync open + 子类 async override（类继承版本）
            var (unit3, _) = BindUnit(
                "pub open shared class Base {\n" +
                "    pub open func run(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub shared class Sub : Base {\n" +
                "    pub async override func run(x: i32): i32 { return (x + 1) }\n" +
                "}\n");
            TestHarness.CheckSemanticError("基类 sync open + 子类 async override", unit3.Diagnostics,
                "'run': 'async' modifier does not match the inherited member");

            // 正例：双 async——shared 接口 async 成员 + async override 实现
            var (unit4, _) = BindUnit(
                "pub shared interface Worker {\n" +
                "    async func run(x: i32): i32\n" +
                "}\n" +
                "pub shared class W implements Worker {\n" +
                "    pub init()\n" +
                "    pub async override func run(x: i32): i32 { return (x + 1) }\n" +
                "}\n");
            CheckNoErrors("双 async 接口实现 override", unit4);

            // 正例：双 async——open 基类 async + 子类 async override
            var (unit5, _) = BindUnit(
                "pub open shared class Base {\n" +
                "    pub open async func run(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub shared class Sub : Base {\n" +
                "    pub async override func run(x: i32): i32 { return (x + 1) }\n" +
                "}\n");
            CheckNoErrors("双 async 类继承 override", unit5);

            // 正例：双 sync 既有口径不受影响（对照 TestOverride 正例，
            // 此处显式回归 shared 场景）
            var (unit6, _) = BindUnit(
                "pub open shared class Base {\n" +
                "    pub open func run(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub shared class Sub : Base {\n" +
                "    pub override func run(x: i32): i32 { return (x + 1) }\n" +
                "}\n");
            CheckNoErrors("双 sync 类继承 override", unit6);

            // 负例：hiding——无 override 关键字的 async 方法与继承 sync
            // 成员同名同签名（同走成员级命中集，按 async 不一致专项拦截）
            var (unit7, _) = BindUnit(
                "pub open shared class Base {\n" +
                "    pub open func run(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub shared class Sub : Base {\n" +
                "    pub async func run(x: i32): i32 { return (x + 1) }\n" +
                "}\n");
            TestHarness.CheckSemanticError("无 override 的 async 隐藏 sync 继承成员", unit7.Diagnostics,
                "'run': 'async' modifier does not match the inherited member");

            // 负例：hiding 反向——无 override 的 sync 方法隐藏继承 async 成员
            var (unit8, _) = BindUnit(
                "pub open shared class Base {\n" +
                "    pub open async func run(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub shared class Sub : Base {\n" +
                "    pub func run(x: i32): i32 { return (x + 1) }\n" +
                "}\n");
            TestHarness.CheckSemanticError("无 override 的 sync 隐藏 async 继承成员", unit8.Diagnostics,
                "'run': 'async' modifier does not match the inherited member");
        }

        // ===== 字段 open/override（§9.2.1 字段覆写，新 init 原则）=====
        // override 字段 = 同名继承 open 字段的初始值替换：类型一致 + 新
        // 初始值 + 无 wrapper/访问器；通过后符号挂 OverriddenField 并移出
        // 宿主 Fields 表（存储归基类槽）。双侧带初始值的 hiding 拒绝
        //（..init.field.<名> 按名成族，静默隐藏会吞掉基类槽初值）
        private static void TestFieldOverrideRules()
        {
            TestHarness.Section("P2/P3 Field Override Rules (§9.2.1)");

            // 正例：open + override——OverriddenField 落定、宿主表移除、
            // 名称解析落基类槽
            var (ok, _) = BindUnit(
                "pub open class Base {\n" +
                "    pub open var hp: i32 = 10\n" +
                "    pub init()\n" +
                "}\n" +
                "pub class Hero : Base {\n" +
                "    pub override var hp: i32 = 99\n" +
                "    pub init()\n" +
                "}\n");
            CheckNoErrors("open + override 正例", ok);
            var hero = ok.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Hero");
            var baseType = ok.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Base");
            TestHarness.CheckTrue("override 字段移出宿主 Fields 表（存储归基类槽）",
                !hero.Fields.Any(f => f.Name == "hp")
                && baseType.Fields.Single(f => f.Name == "hp").IsOpen);

            // 负例：基类字段非 open
            var (notOpen, _) = BindUnit(
                "pub open class Base { pub var hp: i32 = 10 }\n" +
                "pub class Hero : Base { pub override var hp: i32 = 99 }\n");
            TestHarness.CheckSemanticError("非 open 字段被 override", notOpen.Diagnostics,
                "'hp': inherited field is not 'open'");

            // 负例：override 无新初始值
            var (noInit, _) = BindUnit(
                "pub open class Base { pub open var hp: i32 = 10 }\n" +
                "pub class Hero : Base { pub override var hp: i32 }\n");
            TestHarness.CheckSemanticError("override 无新初始值", noInit.Diagnostics,
                "'hp': field override requires a new initial value");

            // 负例：类型不一致
            var (mismatch, _) = BindUnit(
                "pub open class Base { pub open var hp: i32 = 10 }\n" +
                "pub class Hero : Base { pub override var hp: String = \"x\" }\n");
            TestHarness.CheckSemanticError("override 类型不一致", mismatch.Diagnostics,
                "'hp': field type must match the overridden field");

            // 负例：双侧带初始值的 hiding（无 override）
            var (hiding, _) = BindUnit(
                "pub open class Base { pub var hp: i32 = 10 }\n" +
                "pub class Hero : Base { pub var hp: i32 = 99 }\n");
            TestHarness.CheckSemanticError("双侧初始值 hiding 拒绝", hiding.Diagnostics,
                "'hp' hides an inherited field with an initial value; declare it 'override'");

            // 放行：单侧初始值的 hiding 沿用既有行为（无 ..init.field 碰撞）
            var (okHiding, _) = BindUnit(
                "pub open class Base { pub var hp: i32 }\n" +
                "pub class Hero : Base { pub var hp: i32 = 99 }\n");
            CheckNoErrors("基类无初始值的 hiding 放行", okHiding);

            // 负例：无继承同名字段
            var (noBase, _) = BindUnit(
                "pub class C { pub override var v: i32 = 1 }\n");
            TestHarness.CheckSemanticError("无继承字段", noBase.Diagnostics,
                "'v': no inherited field to override");

            // 负例：static 字段写 override（静态无多态）
            var (staticOverride, _) = BindUnit(
                "pub class C { pub static override var v: i32 = 1 }\n");
            TestHarness.CheckSemanticError("static 字段 override", staticOverride.Diagnostics,
                "'open'/'override' cannot be applied to static or global fields");
        }
    }
}
