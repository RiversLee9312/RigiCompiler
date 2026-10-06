using System.Linq;
using RigiCompiler.Bil;

namespace RigiCompiler.Tests
{
    // BilEmitterTests enum case 组（S11）：§8.5 .case 声明发射（挂类型
    // Members + 洞签名形态 + discriminant auto/res(R)）与 §14.3 new.case /
    // §12.3 type.is.case 值发射端到端（含 switch pattern 降级路径、
    // 显式判别值资源登记；enum 无体 init 经 §9.3 映射体合成照常发射
    // 声明 + fn 定义）
    public static partial class BilEmitterTests
    {

        // ===== 固定 case 端到端：构造/赋值/is .Case =====
        private static void TestEnumCaseFixedEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "enum struct Outcome { }[Ok, Failed]\n" +
                "pub func main(): i32 {\n" +
                "    const r: Outcome = .Ok\n" +
                "    if (r is .Failed) { return 1 }\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（固定 enum case）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（固定 enum case）", module);
            // 声明段断言：.case 声明挂类型 Members（对照 S11Module 形态——
            // 固定 case 空洞签名、auto 判别 DiscriminantResource null）
            var outcome = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Outcome");
            var cases = outcome.Members.OfType<BilCaseDeclaration>().ToArray();
            CaseAssertions.CheckTrue(".case 声明挂类型 Members（数量/名称/空洞/auto）",
                cases.Length == 2
                && cases[0].QualifiedName == "Outcome.Ok"
                && cases[0].Parameters.Count == 0 && cases[0].DiscriminantResource == null
                && cases[1].QualifiedName == "Outcome.Failed"
                && cases[1].Parameters.Count == 0 && cases[1].DiscriminantResource == null);
            // fn 形状黄金：new.case（§14.3 操作数序 type/case/TARGET/[ARGS]）
            // 与 type.is.case（§12.3 VALUE/case/RESULT，结果 .bool）
            BilTestHarness.CheckFnShape("固定 case main 形状（new.case/type.is.case）",
                module, "$main()@.i32",
                ".vars { Outcome r, .breakid .b0, Outcome .t0, .bool .t1, .i32 .t2, .i32 .t3 }\n" +
                ".block entry entrypoint {\n" +
                "new.case type(Outcome) case(Outcome.Ok) $.t0 []\n" +
                "set.var $.t0 $r\n" +
                "type.is.case $r case(Outcome.Failed) $.t1\n" +
                "if $.t1 blk(if0-then) none $.b0\n" +
                "load res(#0) $.t3\n" +
                "ret $.t3\n" +
                "}\n" +
                ".block if0-then {\n" +
                "load res(#1) $.t2\n" +
                "ret $.t2\n" +
                "}\n");
        }

        // ===== 固定 case 组合实参（§12.1/§14.3 回归：声明点固定实参
        // 按 init 参数序写入 new.case——历史 bug 丢弃固定实参，运行时
        // 字段读出零值）=====
        private static void TestEnumCaseFixedPayloadEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub enum struct E {\n" +
                "    pub const v: i32\n" +
                "    pub init(_ -> v)\n" +
                "}[\n" +
                "    Fixed(42),\n" +
                "    Param(v = _)\n" +
                "]\n" +
                "pub func main(): i32 {\n" +
                "    const f: E = .Fixed\n" +
                "    return f.v\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（固定 case 组合实参）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（固定 case 组合实参）", module);
            // fn 形状黄金：.Fixed 构造发声明点固定实参 42（init 参数序组合）
            BilTestHarness.CheckFnShape("固定 case 组合实参 main 形状",
                module, "$main()@.i32",
                ".vars { E f, .i32 .t0, E .t1, .i32 .t2 }\n" +
                "load res(#0) $.t0\n" +
                "new.case type(E) case(E.Fixed) $.t1 [$.t0]\n" +
                "set.var $.t1 $f\n" +
                "get.field $f $.t2 field(E#v@.i32)\n" +
                "ret $.t2\n");
        }

        // ===== 参数化 case 端到端：位置/具名实参 + switch pattern 降级 =====
        private static void TestEnumCaseParameterizedEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub enum struct RequestResult {\n" +
                "    pub const errorCode: i32\n" +
                "\n" +
                "    pub init(_ -> errorCode)\n" +
                "}[\n" +
                "    Success(-1),\n" +
                "    Failed(errorCode = _)\n" +
                "]\n" +
                "pub func main(): i32 {\n" +
                "    const failed: RequestResult = .Failed(404)\n" +
                "    const named: RequestResult = .Failed(errorCode = 404)\n" +
                "    switch (failed) {\n" +
                "        (_ is .Success) -> { return 1 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（参数化 enum case）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（参数化 enum case）", module);
            // 声明段断言：洞签名形态（名 + 类型投影，对照 S11Module 的
            // BilCaseParameter("errorCode", ".i32")）；auto 判别；
            // enum 无体 init 照常声明——映射赋值体合成（§9.3）为其产出
            // fn 定义（§21.2 门槛满足），`_ -> field` 映射保留在 BIL
            // 供 VM case 入口消费（S14）
            var requestResult = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "RequestResult");
            var cases = requestResult.Members.OfType<BilCaseDeclaration>().ToArray();
            CaseAssertions.CheckTrue(".case 声明洞签名形态（名/类型/auto）",
                cases.Length == 2
                && cases[0].QualifiedName == "RequestResult.Success"
                && cases[0].Parameters.Count == 0
                && cases[1].QualifiedName == "RequestResult.Failed"
                && cases[1].Parameters.Count == 1
                && cases[1].Parameters[0].Name == "errorCode"
                && cases[1].Parameters[0].TypeRef == ".i32"
                && cases.All(c => c.DiscriminantResource == null));
            CaseAssertions.CheckTrue("enum 无体 init 照常声明（pub init 修饰符）",
                requestResult.Members.OfType<BilSimpleMemberDeclaration>().Any(m =>
                    m.Symbol == "RequestResult$init(errorCode:.i32)@.void"
                    && m.Modifiers.Any(mod => mod is BilKeywordModifier
                        { Keyword: BilKeyword.Init })));
            // fn 形状黄金：case 模板 init 的合成映射赋值体（§9.3——const
            // 字段写入走 §21.8 init 豁免）
            BilTestHarness.CheckFnShape("enum 模板 init 合成体（set.field + ret）",
                module, "RequestResult$init(errorCode:.i32)@.void",
                ".vars {  }\n" +
                "set.field $errorCode $.this field(RequestResult#errorCode@.i32)\n" +
                "ret\n");
            // fn 形状黄金：位置/具名实参同形态（洞实参先物化 .tN）；switch
            // pattern 降级 selector 物化 .s0 + if 链 + type.is.case 条件
            BilTestHarness.CheckFnShape("参数化 case main 形状（含 pattern 降级）",
                module, "$main()@.i32",
                ".vars { RequestResult failed, RequestResult named, RequestResult .s0, " +
                ".breakid .b0, .i32 .t0, RequestResult .t1, .i32 .t2, RequestResult .t3, " +
                ".bool .t4, .i32 .t5, .i32 .t6 }\n" +
                ".block entry entrypoint {\n" +
                "load res(#0) $.t0\n" +
                "new.case type(RequestResult) case(RequestResult.Failed) $.t1 [$.t0]\n" +
                "set.var $.t1 $failed\n" +
                "load res(#0) $.t2\n" +
                "new.case type(RequestResult) case(RequestResult.Failed) $.t3 [$.t2]\n" +
                "set.var $.t3 $named\n" +
                "set.var $failed $.s0\n" +
                "type.is.case $.s0 case(RequestResult.Success) $.t4\n" +
                "if $.t4 blk(if0-then) blk(if0-else) $.b0\n" +
                "}\n" +
                ".block if0-then {\n" +
                "load res(#1) $.t5\n" +
                "ret $.t5\n" +
                "}\n" +
                ".block if0-else {\n" +
                "load res(#2) $.t6\n" +
                "ret $.t6\n" +
                "}\n");
        }

        // ===== 显式判别值（-> N）：判别值资源登记 + auto 对照 =====
        private static void TestEnumCaseExplicitDiscriminant()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "enum struct SteadyABIEnum {}[\n" +
                "    First -> 0,\n" +
                "    Second -> 2,\n" +
                "    Third -> 1\n" +
                "]\n" +
                "pub func main(): i32 {\n" +
                "    const s: SteadyABIEnum = .Second\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（显式判别值）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（显式判别值）", module);
            var steady = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "SteadyABIEnum");
            var cases = steady.Members.OfType<BilCaseDeclaration>().ToArray();
            CaseAssertions.CheckTrue("显式判别值发 discriminant res(R)（非 auto）",
                cases.Length == 3
                && cases[0].QualifiedName == "SteadyABIEnum.First"
                && cases[1].QualifiedName == "SteadyABIEnum.Second"
                && cases[2].QualifiedName == "SteadyABIEnum.Third"
                && cases.All(c => c.DiscriminantResource != null));
            // 判别值资源登记断言：res 名指向的 i32 标量资源值 = -> N 原文值
            var firstRes = module.Resources.OfType<BilScalarResource>()
                .Single(r => r.Name == cases[0].DiscriminantResource);
            var secondRes = module.Resources.OfType<BilScalarResource>()
                .Single(r => r.Name == cases[1].DiscriminantResource);
            var thirdRes = module.Resources.OfType<BilScalarResource>()
                .Single(r => r.Name == cases[2].DiscriminantResource);
            CaseAssertions.CheckTrue("判别值资源 i32 值（-> 0/-> 2/-> 1）",
                firstRes.Type == BilScalarType.I32 && firstRes.LiteralText == "0"
                && secondRes.Type == BilScalarType.I32 && secondRes.LiteralText == "2"
                && thirdRes.Type == BilScalarType.I32 && thirdRes.LiteralText == "1");
            // 资源段黄金：判别值资源随声明段先行登记（先于 fn 体资源）；
            // 判别值 1 与 stdlib 字面量 1、判别值 0 与 MW11c 基线 i32 0、
            // main 的 0 同键去重；尾部 null 是 Task.executor 初值
            BilTestHarness.CheckScalarResourceOnce("资源（判别值 0 去重）", module,
                BilScalarType.I32, "0");
            BilTestHarness.CheckScalarResourceOnce("资源（判别值 1 去重）", module,
                BilScalarType.I32, "1");
            BilTestHarness.CheckScalarResourceOnce("资源（判别值 2 去重）", module,
                BilScalarType.I32, "2");
        }

        // ===== 丢弃式全形 case 构造：语句语境 E.A(1) 发 new.case 后丢弃 =====
        private static void TestEnumCaseDiscardedStatementEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub enum struct E {\n" +
                "    pub const x: i32\n" +
                "    pub init(_ -> x)\n" +
                "}[\n" +
                "    A(x = _),\n" +
                "    B(0)\n" +
                "]\n" +
                "pub func main(): i32 {\n" +
                "    E.A(1)\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（丢弃式 enum case 语句）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（丢弃式 enum case 语句）", module);
            BilTestHarness.CheckFnShape("丢弃式 enum case 语句 main 形状（new.case 后丢弃）",
                module, "$main()@.i32",
                ".vars { .i32 .t0, E .t1, .i32 .t2 }\n" +
                "load res(#0) $.t0\n" +
                "new.case type(E) case(E.A) $.t1 [$.t0]\n" +
                "load res(#1) $.t2\n" +
                "ret $.t2\n");
        }
    }
}
