using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    // Wrapper 簇发射：§14.5 语句位 void 经 wrapper place 发 invoke.noret。
    public static partial class BilEmitterTests
    {
        internal static IReadOnlyList<string> GroupLabels(string group)
            => group.Equals("WRAP-001-review", System.StringComparison.OrdinalIgnoreCase)
                ? [nameof(TestGenericParameterFieldInitializers), nameof(TestForwardDefaultConstructionEmission),
                    nameof(TestSerializableImplicitDefaultConstruction)]
                : [nameof(TestGenericParameterFieldInitializers), nameof(TestForwardDefaultConstructionEmission),
                    nameof(TestSerializableImplicitDefaultConstruction), nameof(TestWrapperFieldInitializerEmission),
                    nameof(TestGenericWrapperFieldInitializerEmission)];


        private static void TestSerializableImplicitDefaultConstruction()
        {
            foreach (var fields in new[] { "", "pub var n: i64?" })
            {
                var (unit, module, _) = BilTestHarness.EmitBilUnit(
                    "import core.serialization.*\n@Serializable\nopen class Payload { " + fields + " }\n" +
                    "func make\\<T extends Payload>(): Payload { return T() }\n" +
                    "pub func main(): i32 { var p = make\\<Payload>()\n var q = new Payload()\n return 0 }\n");
                CheckNoErrors("序列化隐含零参构造保持合法：" + fields, unit);
                BilTestHarness.CheckBilValid("普通 new 与 T() 的真实入口签名合法", module);
                var result = BilVm.Run(module);
                CaseAssertions.CheckTrue("普通 new 与 T() 运行期入口可用",
                    result.ReturnValue is VmI32 { Value: 0 } && result.Exception == null);
            }
        }

        private static void TestForwardDefaultConstructionEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\nrich wrapper W { pub var payload: Payload = new Payload() }\n" +
                "@W\nclass Host { }\n" +
                "class Early { pub var payload: Payload = new Payload() }\n" +
                "class Payload { pub var n: i64 = 7L }\n" +
                "pub func main(): i32 { var h = new Host()\n var e = new Early()\n return e.payload.n as i32 }\n");
            CheckNoErrors("前向普通 new 的早期 Init=null 绑定合法", unit);
            BilTestHarness.CheckBilValid("前向默认构造在最终 BIL 类型上解析合法", module);
        }

        private static void TestGenericParameterFieldInitializers()
        {
            foreach (var init in new[] { "", "pub init(value: TTarget) { reads = value }" })
            {
                var (unit, module, _) = BilTestHarness.EmitBilUnit(
                    "func seed(): i64 { return 7L }\n" +
                    "@WrapperTarget(.Entity)\nrich wrapper W\\<TTarget> {\n" +
                    "pub var reads: TTarget = seed() as TTarget\n" + init + "\n}\n" +
                    "@W\\<i64>" + (init.Length == 0 ? "" : "(9L)") + "\nclass Host { }\n" +
                    "pub func main(): i32 { var h = new Host()\n return 0 }\n");
                CheckNoErrors("WRAP-001 泛型参数字段初值发射：" + init, unit);
                BilTestHarness.CheckBilValid("泛型参数字段赋值及安装接收者合法", module);
            }
        }

        private static void TestWrapperFieldInitializerEmission()
        {
            foreach (var init in new[] { "", "pub init()", "pub init() { }" })
            {
                var (unit, module, _) = BilTestHarness.EmitBilUnit(
                    "@WrapperTarget(.Entity)\n" +
                    "wrapper W { pub var reads: i64 = 7L\n" + init + "\n}\n" +
                    "@W\nclass Host { pub init() }\n" +
                    "pub func main(): i32 { return 0 }\n");
                CheckNoErrors("WRAP-001 wrapper 初值发射：" + init, unit);
                BilTestHarness.CheckBilValid("WRAP-001 wrapper 初值 BIL 合法", module);
                BilTestHarness.CheckFnShape("wrapper 真实字段初始化器", module,
                    "W$..init.field.reads()@.void",
                    ".vars { .i64 .t0 }\n" +
                    "load res(#0) $.t0\n" +
                    "set.field $.t0 $.this field(W#reads@.i64)\nret\n");
                BilTestHarness.CheckFnShape("wrapper 安装阶段缝合字段初值", module,
                    "W$..init.wrapper()@.void",
                    ".vars {  }\n" +
                    "invoke.noret fn(W$..init.field.reads()@.void) [$.this]\nret\n");
                BilTestHarness.CheckFnShape("默认/无体/空体 init 不重复初始化", module,
                    "W$init()@.void", ".vars {  }\nret\n");
            }
        }

        private static void TestGenericWrapperFieldInitializerEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "wrapper W\\<TTarget> { pub var reads: i64 = 7L\n pub init() }\n" +
                "@W\nclass Host { pub init() }\n" +
                "pub func main(): i32 { return 0 }\n");
            CheckNoErrors("WRAP-001 泛型 wrapper 初值无诊断", unit);
            BilTestHarness.CheckBilValid("WRAP-001 泛型 wrapper 初始化接收者合法", module);
        }

        private static void TestWrapperPlaceVoidCallEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "    pub func bump() { }\n" +
                "}\n" +
                "@Logged(\"INFO\")\n" +
                "pub class Svc { pub init() }\n" +
                "pub func f(s: Svc) {\n" +
                "    s:Logged.bump()\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（wrapper place void）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（wrapper place void）", module);
            BilTestHarness.CheckFnShape("语句位 void：get.wrapper + invoke.noret",
                module, "$f(s:Svc)@.void",
                ".vars { Logged .s0, Logged .t0 }\n" +
                "get.wrapper $s type(Logged) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "invoke.noret fn(Logged$bump()@.void) [$.s0]\n" +
                "ret\n");
        }
    }
}
