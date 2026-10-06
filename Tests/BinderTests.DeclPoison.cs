using System.Linq;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// 局部声明初值绑定失败的毒化静默补齐（constfix，b4-1 字节扫描踩中
    /// 的「循环体常量解析缺陷」根因修复配套回归）：初值表达式绑定失败时
    /// 声明仍登记毒化局部（有标注用标注类型，无标注用 ErrorType 单例），
    /// 后续使用点按毒化静默收口——不再级联「Undefined name」（位置随
    /// 使用点漂移、淹没真正根因），也不再误报「requires a type annotation
    /// or an initializer」/「Const must have an initializer」（该变量写了
    /// 初值，只是绑定失败）。未写初值的原有路径不受影响（Basics 既有
    /// 用例锁定）。同形态路径一并覆盖：解构声明分量、catch 变量、
    /// seq using 资源变量。
    /// </summary>
    public static partial class BinderTests
    {
        private static void TestDeclInitFailurePoisonSilence()
        {
            CompilerTestTools.Section("P3 Decl Init Failure Poison Silence");

            // ---- var 无标注：初值失败的唯一诊断是初值自己的未定义名 ----
            var (unit, _) = BindUnit("func f() { var x = nosuch }\n");
            CaseAssertions.CheckTrue("var 初值失败：恰一条诊断（初值未定义名）",
                unit.Diagnostics.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error) == 1
                    && unit.Diagnostics.Diagnostics.Any(d => d.Message.Contains("Undefined name: 'nosuch'")),
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => d.Message)));
            CaseAssertions.CheckTrue("var 初值失败：无级联 Undefined name",
                !unit.Diagnostics.Diagnostics.Any(d => d.Message.Contains("Undefined name: 'x'")),
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => d.Message)));
            CaseAssertions.CheckTrue("var 初值失败：无 requires annotation 误诊",
                !unit.Diagnostics.Diagnostics.Any(d =>
                    d.Message.Contains("requires a type annotation or an initializer")),
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => d.Message)));

            // ---- const 无标注 + 使用点：毒化局部静默吸收使用 ----
            var (unit2, _) = BindUnit("func f() { const c = nosuch\nvar y = c }\n");
            CaseAssertions.CheckTrue("const 初值失败：恰一条诊断",
                unit2.Diagnostics.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error) == 1
                    && unit2.Diagnostics.Diagnostics.Any(d => d.Message.Contains("Undefined name: 'nosuch'")),
                string.Join("; ", unit2.Diagnostics.Diagnostics.Select(d => d.Message)));
            CaseAssertions.CheckTrue("const 初值失败：无 must have an initializer 误诊",
                !unit2.Diagnostics.Diagnostics.Any(d => d.Message.Contains("must have an initializer")),
                string.Join("; ", unit2.Diagnostics.Diagnostics.Select(d => d.Message)));
            CaseAssertions.CheckTrue("const 初值失败：使用点无级联 Undefined name",
                !unit2.Diagnostics.Diagnostics.Any(d => d.Message.Contains("Undefined name: 'c'")),
                string.Join("; ", unit2.Diagnostics.Diagnostics.Select(d => d.Message)));

            // ---- const 显式标注：毒化局部取标注类型，使用点正常解析 ----
            var (unit3, bodies3) = BindUnit("func f() { const c: i32 = nosuch\nvar y = c }\n");
            CaseAssertions.CheckTrue("const 标注初值失败：恰一条诊断",
                unit3.Diagnostics.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error) == 1,
                string.Join("; ", unit3.Diagnostics.Diagnostics.Select(d => d.Message)));
            CaseAssertions.Check("const 标注初值失败：毒化局部按标注类型绑定",
                BoundDescribe.Body(BodyOf(bodies3, "f")),
                "Body(f, [c: i32, y: i32], [Decl(c, i32); Decl(y, i32, = Local(c,i32))])");

            // ---- 解构声明：初值失败分量仍登记毒化局部 ----
            var (unit4, _) = BindUnit("func f() { var (a, b) = nosuch\nvar y = a }\n");
            CaseAssertions.CheckTrue("解构初值失败：恰一条诊断",
                unit4.Diagnostics.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error) == 1
                    && unit4.Diagnostics.Diagnostics.Any(d => d.Message.Contains("Undefined name: 'nosuch'")),
                string.Join("; ", unit4.Diagnostics.Diagnostics.Select(d => d.Message)));
            CaseAssertions.CheckTrue("解构初值失败：分量使用无级联 Undefined name",
                !unit4.Diagnostics.Diagnostics.Any(d =>
                    d.Message.Contains("Undefined name: 'a'") || d.Message.Contains("Undefined name: 'b'")),
                string.Join("; ", unit4.Diagnostics.Diagnostics.Select(d => d.Message)));

            // ---- catch 变量：异常类型解析失败仍登记毒化变量（TryVisitor
            // 依赖 core.Exception 内建根，带 stdlib 驱动）----
            var (unit5, _) = BindUnitWithStdlib(
                "func f() { try { } catch (e: Nope) { var z = e } }\n");
            CaseAssertions.CheckTrue("catch 类型解析失败：恰一条诊断",
                unit5.Diagnostics.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error) == 1
                    && unit5.Diagnostics.Diagnostics.Any(d => d.Message.Contains("Nope")),
                string.Join("; ", unit5.Diagnostics.Diagnostics.Select(d => d.Message)));
            CaseAssertions.CheckTrue("catch 类型解析失败：体内使用无级联 Undefined name",
                !unit5.Diagnostics.Diagnostics.Any(d => d.Message.Contains("Undefined name: 'e'")),
                string.Join("; ", unit5.Diagnostics.Diagnostics.Select(d => d.Message)));

            // ---- seq using 资源变量：初值失败仍登记毒化资源局部 ----
            var (unit6, _) = BindUnitWithStdlib(
                "func f() { seq using(const r = nosuch) { var z = r } }\n");
            CaseAssertions.CheckTrue("using 初值失败：恰一条诊断",
                unit6.Diagnostics.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error) == 1
                    && unit6.Diagnostics.Diagnostics.Any(d => d.Message.Contains("Undefined name: 'nosuch'")),
                string.Join("; ", unit6.Diagnostics.Diagnostics.Select(d => d.Message)));
            CaseAssertions.CheckTrue("using 初值失败：块内使用无级联 Undefined name",
                !unit6.Diagnostics.Diagnostics.Any(d => d.Message.Contains("Undefined name: 'r'")),
                string.Join("; ", unit6.Diagnostics.Diagnostics.Select(d => d.Message)));

            // ---- 正例锁死：while + const + and 条件（报告人原文形态，b4-1
            // 误判「循环体常量解析缺陷」的形状）绑定零诊断 ----
            var (unit7, _) = BindUnit(
                "func f(): i32 {\n" +
                "    var i: i32 = 0\n" +
                "    while (i < 10) {\n" +
                "        const b = (i + 1)\n" +
                "        if ((b >= 3) and (b <= 8)) { i = b }\n" +
                "        i = (i + 1)\n" +
                "    }\n" +
                "    return i\n" +
                "}\n");
            CheckNoErrors("while+const+and 正例零诊断", unit7);
        }
    }
}
