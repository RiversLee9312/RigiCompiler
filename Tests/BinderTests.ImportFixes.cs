using System.Linq;

namespace RigiCompiler.Tests
{
    // S4 具名 import 顶层函数/全局字段（P3 消费侧，§15.2）：
    // 具名导入的顶层函数（同名重载全部入池）与全局字段/const 进入值/调用
    // 查找序（宿主成员 → 命名空间链之后、通配 import 之前）；本地同名声明
    // 优先于任何 import；双具名同名有效命中报 Ambiguous import；
    // 与通配 import 同名共存时具名优先
    public static partial class BinderTests
    {
        // 被导入侧库：顶层函数 pickAxis 两份重载 + 全局 const + 类型
        private const string NamedImportLib =
            "namespace scene.geom\n" +
            "pub class Vec2 {\n" +
            "    pub var x: i32\n" +
            "    pub var y: i32\n" +
            "}\n" +
            "pub const axisBoost: i32 = 10\n" +
            "pub func pickAxis(v: Vec2): String { return \"vec\" }\n" +
            "pub func pickAxis(x: i32, y: i32): String { return \"xy\" }\n";

        private static NamespaceSymbol GeomNs(CompilationUnit unit)
        {
            return unit.Symbols.GlobalNamespace.ChildNamespaces.Single(n => n.Name == "scene")
                .ChildNamespaces.Single(n => n.Name == "geom");
        }

        private static void TestNamedImportValueConsumption()
        {
            TestHarness.Section("P3 Named Import Functions/Fields (S4)");

            // ===== 具名导入顶层函数：同名重载全部入池，按签名消歧 =====
            var (unit, bodies) = BindUnit(NamedImportLib,
                "import scene.geom.pickAxis\n" +
                "import scene.geom.Vec2\n" +
                "pub func a(v: Vec2): String { return pickAxis(v) }\n" +
                "pub func b(): String { return pickAxis(1, 2) }\n");
            CheckNoErrors("无诊断（具名导入函数双形态调用）", unit);
            var geom = GeomNs(unit);
            var retA = (BoundReturnStatement)BodyOf(bodies, "a").Body.Statements[0];
            TestHarness.CheckTrue("pickAxis(v) 命中 Vec2 重载",
                ReferenceEquals(((BoundCallExpression)retA.Value!).Method,
                    geom.Methods.Single(m => m.Name == "pickAxis" && m.Parameters.Count == 1)));
            var retB = (BoundReturnStatement)BodyOf(bodies, "b").Body.Statements[0];
            TestHarness.CheckTrue("pickAxis(1, 2) 命中 (i32, i32) 重载（重载全导入）",
                ReferenceEquals(((BoundCallExpression)retB.Value!).Method,
                    geom.Methods.Single(m => m.Name == "pickAxis" && m.Parameters.Count == 2)));

            // ===== 具名导入全局 const：值读取绑定到被导入字段 =====
            var (constUnit, constBodies) = BindUnit(NamedImportLib,
                "import scene.geom.axisBoost\n" +
                "pub func read(): i32 { return axisBoost }\n");
            CheckNoErrors("无诊断（具名导入全局 const）", constUnit);
            var readRet = (BoundReturnStatement)BodyOf(constBodies, "read").Body.Statements[0];
            TestHarness.CheckTrue("axisBoost 绑定 geom 全局字段",
                readRet.Value is BoundFieldReferenceExpression readField
                && ReferenceEquals(readField.Field,
                    GeomNs(constUnit).Fields.Single(f => f.Name == "axisBoost")));

            // ===== 本地同名函数优先于具名 import（命名空间链先于 import）=====
            var (local, localBodies) = BindUnit(NamedImportLib,
                "import scene.geom.pickAxis\n" +
                "pub func pickAxis(): String { return \"local\" }\n" +
                "pub func use(): String { return pickAxis() }\n");
            CheckNoErrors("无诊断（本地同名遮蔽 import）", local);
            var useRet = (BoundReturnStatement)BodyOf(localBodies, "use").Body.Statements[0];
            TestHarness.CheckTrue("本地 pickAxis() 优先",
                ReferenceEquals(((BoundCallExpression)useRet.Value!).Method,
                    local.Symbols.GlobalNamespace.Methods.Single(m => m.Name == "pickAxis")));

            // ===== 与通配 import 同名共存：具名优先（先者胜口径）=====
            var (both, bothBodies) = BindUnit(NamedImportLib,
                "namespace other\n" +
                "pub func pickAxis(x: i32, y: i32): String { return \"other\" }\n",
                "import scene.geom.pickAxis\n" +
                "import other.*\n" +
                "pub func use(): String { return pickAxis(1, 2) }\n");
            CheckNoErrors("无诊断（具名 + 通配同名）", both);
            var bothRet = (BoundReturnStatement)BodyOf(bothBodies, "use").Body.Statements[0];
            TestHarness.CheckTrue("同名时具名 import 优先于通配",
                ReferenceEquals(((BoundCallExpression)bothRet.Value!).Method,
                    GeomNs(both).Methods.Single(m => m.Name == "pickAxis"
                        && m.Parameters.Count == 2)));

            // ===== 双具名同名分属不同容器：Ambiguous import =====
            var (amb, _) = BindUnit(NamedImportLib,
                "namespace other\n" +
                "pub func pickAxis(x: i32, y: i32): String { return \"other\" }\n",
                "import scene.geom.pickAxis\n" +
                "import other.pickAxis\n" +
                "pub func use(): String { return pickAxis(1, 2) }\n");
            TestHarness.CheckSemanticError("双具名同名函数歧义", amb.Diagnostics,
                "Ambiguous import: 'pickAxis'");

            // ===== 导入不存在的函数名：P2 统一报 Unresolved import，
            // 使用点不再叠加次生「Undefined function」噪音 =====
            var (missing, _) = BindUnit(NamedImportLib,
                "import scene.geom.missing\n" +
                "pub func use(): i32 { return 0 }\n");
            TestHarness.CheckSemanticError("导入不存在函数名", missing.Diagnostics,
                "Unresolved import: 'scene.geom.missing'");
        }
    }
}
