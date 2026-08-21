using System.Linq;

namespace RigiCompiler.Tests
{
    // P18/S2 init 字段定值赋值分析（DA，SYNTAX §9.3）测试：非 Nullable
    // 实例字段三选一（声明初始值 / init 内显式赋值 / Nullable）——逐 init
    // 重载每条路径出口检查（块尾与中途 return）；super(...) 收窄义务到
    // 本类声明字段；不调 super 时基类无初始值非空字段计入本 init 义务；
    // 无 init 类型在零参 new 使用点拒绝；合成默认构造与无 init enum 固定
    // case 同样受检。g6（§3.7）零参 T() 界检查用例在 Types 套件
    // TestDynamicNew 内。
    public static partial class BinderTests
    {
        private static void TestInitFieldDa()
        {
            TestHarness.Section("P3 Init Field Definite Assignment (§9.3, P18/S2)");

            // ===== 正例 =====
            // 声明初始值（..init.field.* 视为进入时已赋值）
            var (ok1, _) = BindUnit(
                "class A { pub var x: i32 = 1 }\n" +
                "func m(): i32 { return new A().x }\n");
            CheckNoErrors("声明初始值 + 默认构造", ok1);

            // init(_ -> x) 映射算赋值（无体与有体两形态）
            var (ok2, _) = BindUnit(
                "class B { pub var x: i32\n    pub init(_ -> x) }\n" +
                "class C { pub var y: i32\n    pub init(_ -> y) { } }\n" +
                "func m(): i32 { return (new B(1).x + new C(2).y) }\n");
            CheckNoErrors("映射 init 两形态", ok2);

            // init 体显式赋值
            var (ok3, _) = BindUnit(
                "class D { pub var x: i32\n    pub init(v: i32) { x = v } }\n" +
                "func m(): i32 { return new D(3).x }\n");
            CheckNoErrors("init 体赋值", ok3);

            // Nullable 字段豁免
            var (ok4, _) = BindUnit(
                "class E { pub var x: i32?\n    pub init() { } }\n" +
                "func m(): i32 { return 0 }\n");
            CheckNoErrors("Nullable 字段豁免", ok4);

            // 双分支都赋值（if/else 交集成立）
            var (ok5, _) = BindUnit(
                "class F { pub var x: i32\n" +
                "    pub init(c: bool) { if (c) { x = 1 } else { x = 2 } } }\n");
            CheckNoErrors("双分支都赋值", ok5);

            // 调 super 的子类：基类字段由基类 init 担保
            var (ok6, _) = BindUnit(
                "pub open class G { pub var g: i32\n    pub init(_ -> g) }\n" +
                "pub class H : G { pub var h: i32\n" +
                "    pub init(_ -> h, gv: i32) { super(gv) } }\n");
            CheckNoErrors("调 super 的子类", ok6);

            // 不调 super：子类直接给可见基类字段赋值（RangeEnumerator 先例）
            var (ok7, _) = BindUnit(
                "pub open class I { protected var i: i32 }\n" +
                "pub class J : I {\n    pub init(v: i32) { i = v } }\n");
            CheckNoErrors("不调 super 子类自赋基类字段", ok7);

            // 抽象基类义务转移：自身不可构造不报错，具体子类 init 兜底
            var (ok8, _) = BindUnit(
                "pub abstract class K { protected var k: i32 }\n" +
                "pub class L : K {\n    pub init(v: i32) { k = v } }\n");
            CheckNoErrors("抽象基类义务转移", ok8);

            // do-while 体至少一次：体尾赋值计入出口
            var (ok9, _) = BindUnit(
                "class M { pub var x: i32\n" +
                "    pub init() { do { x = 1 } while (false) } }\n");
            CheckNoErrors("do-while 体尾计入", ok9);

            // try/catch 双路都赋值（catch 类型用 bootstrap 根 core.Exception）
            var (ok10, _) = BindUnit(
                "class N { pub var x: i32\n" +
                "    pub init() {\n" +
                "        try { x = 1 } catch (e: Exception) { x = 2 }\n" +
                "    } }\n");
            CheckNoErrors("try/catch 交集赋值", ok10);

            // 仅 get 无 set 访问器字段豁免（无写入通道，既定零值语义）
            var (ok11, _) = BindUnit(
                "class O {\n" +
                "    pub var v: i32 { pub get(value: _) { return value } }\n" +
                "    pub init() { }\n" +
                "}\n");
            CheckNoErrors("仅 get 访问器字段豁免", ok11);

            // 无 init 类型但从不构造：声明点合法（义务在使用点判定）
            var (ok12, _) = BindUnit(
                "struct P { pub var x: i32 }\n" +
                "func m(): i32 { return 0 }\n");
            CheckNoErrors("无 init 未构造合法", ok12);

            // ===== 反例 =====
            // 分支漏赋值（仅 then）
            var (bad1, _) = BindUnit(
                "class Q { pub var x: i32\n" +
                "    pub init(c: bool) { if (c) { x = 1 } } }\n");
            TestHarness.CheckSemanticError("分支漏赋值", bad1.Diagnostics,
                "Field 'x' of 'Q' is not definitely assigned on all paths");

            // 中途 return 路径漏赋值
            var (bad2, _) = BindUnit(
                "class R { pub var x: i32\n" +
                "    pub init(c: bool) {\n        if (c) { return }\n        x = 1\n    } }\n");
            TestHarness.CheckSemanticError("中途 return 路径漏赋值", bad2.Diagnostics,
                "Field 'x' of 'R' is not definitely assigned on all paths");

            // while 体可能零次：体内赋值不计入出口
            var (bad3, _) = BindUnit(
                "class S { pub var x: i32\n" +
                "    pub init() { while (false) { x = 1 } } }\n");
            TestHarness.CheckSemanticError("while 体赋值不计入", bad3.Diagnostics,
                "Field 'x' of 'S' is not definitely assigned on all paths");

            // 不调 super 且基类有无初始值非空字段：诊断引导 super
            var (bad4, _) = BindUnit(
                "pub open class T { pub var t: i32\n    pub init(_ -> t) }\n" +
                "pub class U : T { pub init() { } }\n");
            TestHarness.CheckSemanticError("不调 super 基类义务", bad4.Diagnostics,
                "Field 't' of 'T' is not definitely assigned on all paths of this init " +
                "of 'U'");
            TestHarness.CheckSemanticError("诊断引导调 super", bad4.Diagnostics,
                "or call super(...)");

            // 无 init 类型零参 new 使用点拒绝
            var (bad5, _) = BindUnit(
                "struct V { pub var x: i32 }\n" +
                "func m(): i32 { return new V().x }\n");
            TestHarness.CheckSemanticError("无 init 直接 new", bad5.Diagnostics,
                "Type 'V' has no constructor that assigns non-nullable field 'x'");

            // 合成默认构造覆盖不了无初始值字段（有初始值触发合成，
            // 同类另一个无初始值非空字段漏出）
            var (bad6, _) = BindUnit(
                "class W { pub var a: i32 = 1\n    pub var b: i32 }\n");
            TestHarness.CheckSemanticError("合成默认构造漏字段", bad6.Diagnostics,
                "Field 'b' of 'W' is not definitely assigned on all paths");

            // 无 init enum struct 的固定 case：默认构造无法担保字段
            var (bad7, _) = BindUnit(
                "pub enum struct X { pub const c: i32 }[A]\n");
            TestHarness.CheckSemanticError("无 init enum 固定 case", bad7.Diagnostics,
                "cannot assign non-nullable field 'c'");

            // 泛型参数类型字段按非空悲观（要豁免须写 T?）
            var (bad8, _) = BindUnit(
                "class Y\\<T> { pub var v: T\n    pub init() { } }\n");
            TestHarness.CheckSemanticError("泛型参数字段悲观非空", bad8.Diagnostics,
                "Field 'v' of 'Y' is not definitely assigned on all paths");
        }
    }
}
