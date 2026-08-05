using System.Linq;

namespace LatteCompiler.Tests
{
    // LowererTests enum case 组（S11）：enum case 构造恒等降级（固定/
    // 位置/乱序具名归位 + 洞签名 cast 物化 §14.3 严格匹配）与 is .Case
    // 槽透传（含 switch pattern 降级路径）
    public static partial class LowererTests
    {
        // 共享源骨架（同 BinderTests.EnumCases 的 RequestResultSource）：
        // 一参 pub init + 固定 case（Success）与单洞参数化 case（Failed）
        private const string EnumRequestResultSource =
            "pub enum struct RequestResult {\n" +
            "    pub const errorCode: i32\n" +
            "\n" +
            "    pub init(_ -> errorCode)\n" +
            "}[\n" +
            "    Success(-1),\n" +
            "    Failed(errorCode = _)\n" +
            "]\n";

        // 共享源骨架：两参 init + 双洞参数化 case（Error）
        private const string EnumDataResultSource =
            "pub enum struct DataResult {\n" +
            "    pub const code: i32\n" +
            "    pub const text: String\n" +
            "\n" +
            "    pub init(_ -> code, _ -> text)\n" +
            "}[\n" +
            "    Okay(0, \"ok\"),\n" +
            "    Error(code = _, text = _)\n" +
            "]\n";

        private static void TestEnumCaseLowering()
        {
            // ===== 固定 case 恒等降级 =====
            var (unit, bound, lowered) = LowerUnit(EnumRequestResultSource +
                "func f() {\n" +
                "    const result: RequestResult = .Success\n" +
                "}\n");
            CheckNoErrors("无诊断（固定 case 降级）", unit);
            TestHarness.Check("固定 case 降级形态", LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [result: RequestResult], " +
                "[Decl(result, RequestResult, = EnumCase(RequestResult.Success, []))])");
            var boundCase = (BoundEnumCaseExpression)((BoundLocalDeclarationStatement)
                bound.Single(b => b.Method.Name == "f").Body.Statements[0]).Initializer!;
            var loweredCase = (LoweredEnumCaseExpression)((LoweredLocalDeclarationStatement)
                BodyOf(lowered, "f").Body.Statements[0]).Initializer!;
            TestHarness.CheckTrue("enum case Origin 回指 + Case 符号引用相等 + 空实参",
                ReferenceEquals(loweredCase.Origin, boundCase)
                && ReferenceEquals(loweredCase.Case, boundCase.Case)
                && loweredCase.Arguments.Count == 0);

            // ===== 参数化 case 位置实参：洞实参逐条降级 =====
            var (unit2, bound2, lowered2) = LowerUnit(EnumRequestResultSource +
                "func f() {\n" +
                "    const failed: RequestResult = .Failed(404)\n" +
                "}\n");
            CheckNoErrors("无诊断（参数化 case 降级）", unit2);
            TestHarness.Check("参数化 case 位置实参降级形态",
                LoweredDescribe.Body(BodyOf(lowered2, "f")),
                "Body(f, [failed: RequestResult], " +
                "[Decl(failed, RequestResult, = EnumCase(RequestResult.Failed, [Int(404,i32)]))])");
            var boundArg = (BoundEnumCaseExpression)((BoundLocalDeclarationStatement)
                bound2.Single(b => b.Method.Name == "f").Body.Statements[0]).Initializer!;
            var loweredArg = (LoweredEnumCaseExpression)((LoweredLocalDeclarationStatement)
                BodyOf(lowered2, "f").Body.Statements[0]).Initializer!;
            TestHarness.CheckTrue("洞实参逐条降级（Origin 回指 Bound 实参）",
                loweredArg.Arguments.Count == 1
                && ReferenceEquals(loweredArg.Arguments[0].Origin, boundArg.Arguments[0]));

            // ===== 乱序具名：产物规范序 = 洞签名序（= init 参数序）=====
            var (unit3, _, lowered3) = LowerUnit(EnumDataResultSource +
                "func f() {\n" +
                "    const e: DataResult = .Error(text = \"bad\", code = 7)\n" +
                "}\n");
            CheckNoErrors("无诊断（乱序具名降级）", unit3);
            TestHarness.Check("乱序具名归位规范序降级形态",
                LoweredDescribe.Body(BodyOf(lowered3, "f")),
                "Body(f, [e: DataResult], [Decl(e, DataResult, = EnumCase(DataResult.Error, " +
                "[Int(7,i32), Str(\"bad\",String)]))])");

            // ===== 洞实参按洞签名类型物化 cast（§14.3 严格匹配，
            // LowerArguments 同先例：P3 IsAssignable 兼容 → BIL 严格相等；
            // rich enum struct 才能持有 class 字段——§3.1.1 rich 规则）=====
            var (unit4, _, lowered4) = LowerUnit(
                "open class Base { }\n" +
                "class Derived : Base { }\n" +
                "pub rich enum struct Wrapped {\n" +
                "    pub const v: Base\n" +
                "    pub init(_ -> v)\n" +
                "}[\n" +
                "    Custom(v = _)\n" +
                "]\n" +
                "func f() {\n" +
                "    const w: Wrapped = .Custom(new Derived())\n" +
                "}\n");
            CheckNoErrors("无诊断（洞签名 cast 物化）", unit4);
            TestHarness.Check("洞实参按洞签名类型物化 cast",
                LoweredDescribe.Body(BodyOf(lowered4, "f")),
                "Body(f, [w: Wrapped], [Decl(w, Wrapped, = EnumCase(Wrapped.Custom, " +
                "[Cast(New(Derived, []), Base, Base)]))])");

            // ===== is .Case 槽透传 =====
            var (unit5, bound5, lowered5) = LowerUnit(EnumRequestResultSource +
                "func consume(r: RequestResult) { }\n" +
                "func f(result: RequestResult): i32 {\n" +
                "    if (result is .Failed) { consume(result) }\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("无诊断（is .Case 降级）", unit5);
            TestHarness.Check("is .Case 降级形态", LoweredDescribe.Body(BodyOf(lowered5, "f")),
                "Body(f, [], [If(IsCase(Param(result,RequestResult), RequestResult.Failed), " +
                "[CallStmt(consume, [Param(result,RequestResult)])]); Return(Int(0,i32))])");
            var boundIsCase = (BoundTypeCheckExpression)((BoundIfStatement)
                bound5.Single(b => b.Method.Name == "f").Body.Statements[0]).Condition;
            var loweredIsCase = (LoweredTypeCheckExpression)((LoweredIfStatement)
                BodyOf(lowered5, "f").Body.Statements[0]).Condition;
            TestHarness.CheckTrue("IsCase 槽透传（Origin/Kind/Case 引用相等/双槽 null）",
                ReferenceEquals(loweredIsCase.Origin, boundIsCase)
                && loweredIsCase.Kind == BoundTypeCheckKind.IsCase
                && ReferenceEquals(loweredIsCase.Case, boundIsCase.Case)
                && loweredIsCase.TargetType == null && loweredIsCase.TargetValue == null);

            // ===== switch pattern 降级路径（`(_ is .Success)`：selector
            // 物化 .sN + 嵌套 if 链，IsCase 条件走同一 Rewriter 通道）=====
            var (unit6, _, lowered6) = LowerUnit(EnumRequestResultSource +
                "func f(result: RequestResult): i32 {\n" +
                "    switch (result) {\n" +
                "        (_ is .Success) -> { return 1 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（switch pattern is .Case 降级）", unit6);
            TestHarness.Check("switch pattern is .Case 降级形态",
                LoweredDescribe.Body(BodyOf(lowered6, "f")),
                "Body(f, [.s0: RequestResult], " +
                "[Assign(Local(.s0,RequestResult), Param(result,RequestResult)); " +
                "[If(IsCase(Local(.s0,RequestResult), RequestResult.Success), " +
                "[Return(Int(1,i32))], [Return(Int(0,i32))])]])");
        }
    }
}
