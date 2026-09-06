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
        private static void TestEnumCaseEmission()
        {
            TestEnumCaseFixedEmission();
            TestEnumCaseFixedPayloadEmission();
            TestEnumCaseParameterizedEmission();
            TestEnumCaseExplicitDiscriminant();
            TestEnumCaseDiscardedStatementEmission();
        }

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
            TestHarness.CheckTrue(".case 声明挂类型 Members（数量/名称/空洞/auto）",
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
            TestHarness.CheckTrue(".case 声明洞签名形态（名/类型/auto）",
                cases.Length == 2
                && cases[0].QualifiedName == "RequestResult.Success"
                && cases[0].Parameters.Count == 0
                && cases[1].QualifiedName == "RequestResult.Failed"
                && cases[1].Parameters.Count == 1
                && cases[1].Parameters[0].Name == "errorCode"
                && cases[1].Parameters[0].TypeRef == ".i32"
                && cases.All(c => c.DiscriminantResource == null));
            TestHarness.CheckTrue("enum 无体 init 照常声明（pub init 修饰符）",
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
            TestHarness.CheckTrue("显式判别值发 discriminant res(R)（非 auto）",
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
            TestHarness.CheckTrue("判别值资源 i32 值（-> 0/-> 2/-> 1）",
                firstRes.Type == BilScalarType.I32 && firstRes.LiteralText == "0"
                && secondRes.Type == BilScalarType.I32 && secondRes.LiteralText == "2"
                && thirdRes.Type == BilScalarType.I32 && thirdRes.LiteralText == "1");
            // 资源段黄金：判别值资源随声明段先行登记（先于 fn 体资源）；
            // 判别值 1 与 stdlib 字面量 1、判别值 0 与 MW11c 基线 i32 0、
            // main 的 0 同键去重；尾部 null 是 Task.executor 初值
BilTestHarness.CheckResShape("资源（判别值登记 + 同键去重）", module,
                "#0 = i32 0\n" +
                "#1 = i32 2\n" +
                "#2 = i32 1\n" +
                "#3 = string \"\\n\"\n" +
                "#4 = catch-table {  }\n" +
                "#5 = catch-table {  }\n" +
                "#6 = catch-table {  }\n" +
                "#7 = string \"Map 快照键值长度必须相等\"\n" +
                "#8 = bool false\n" +
                "#9 = null type(.generic<$.generic.V>)\n" +
                "#10 = null type(.generic<$.generic.K>)\n" +
                "#11 = null type(core::AtomicMapSnapshot<.generic<$.generic.K>, .generic<$.generic.V>>)\n" +
                "#12 = null type(.generic<$.generic.T>)\n" +
                "#13 = null type(core::AtomicSnapshot<.generic<$.generic.T>>)\n" +
                "#14 = bool true\n" +
                "#15 = i32 8\n" +
                "#16 = string \"Task 只允许启动一次：对已完成启动的 Task 调用 run\"\n" +
                "#17 = string \"冷 Task body 无法 spawn-into：无匹配闭包\"\n" +
                "#18 = i32 3\n" +
                "#19 = i32 4\n" +
                "#20 = i32 5\n" +
                "#21 = null type(core.coroutine::I64Queue)\n" +
                "#22 = catch-table {  }\n" +
                "#23 = string \"Mutex.release：令牌不属于此 Mutex 或已释放\"\n" +
                "#24 = catch-table {  }\n" +
                "#25 = catch-table {  }\n" +
                "#26 = string \"Timer.RepeatOption.Repeat：repeatCount 必须 > 0\"\n" +
                "#27 = null type(.generic<$.generic.TValue>)\n" +
                "#28 = catch-table {  }\n" +
                "#29 = catch-table {  }\n" +
                "#30 = i32 -1\n" +
                "#31 = null type(core.coroutine::Executor)\n" +
                "#32 = string \"目标 Place 不可写\"\n" +
                "#33 = string \"无法将 \"\n" +
                "#34 = string \" 转换为 \"\n" +
                "#35 = string \"new.indirect 目标不可构造：不匹配任何 init：\"\n" +
                "#36 = string \"整数除以零\"\n" +
                "#37 = string \"数组下标越界：\"\n" +
                "#38 = string \"（长度 \"\n" +
                "#39 = string \"）\"\n" +
                "#40 = string \"对象在销毁前从未调用 dispose()：\"\n" +
                "#41 = string \"core::UndisposedResourceException: \"\n" +
                "#42 = null type(core.messaging::QueueReaderState<.generic<$.generic.TMessage>>)\n" +
                "#43 = catch-table {  }\n" +
                "#44 = null type(core.messaging::QueueState<.generic<$.generic.TMessage>>)\n" +
                "#45 = i32 -3\n" +
                "#46 = i32 -4\n" +
                "#47 = i32 -9\n" +
                "#48 = i32 -5\n" +
                "#49 = catch-table {  }\n" +
                "#50 = i32 -2\n" +
                "#51 = catch-table {  }\n" +
                "#52 = i32 -6\n" +
                "#53 = i32 -8\n" +
                "#54 = catch-table {  }\n" +
                "#55 = i32 -7\n" +
                "#56 = i32 -10\n" +
                "#57 = null type(.generic<$.generic.TMessage>)\n" +
                "#58 = null type(core.messaging::QueueLogSegment<.generic<$.generic.TMessage>>)\n" +
                "#59 = null type(core.coroutine::Mutex.Lock)\n" +
                "#60 = i32 64\n" +
                "#61 = catch-table {  }\n" +
                "#62 = string \"MessageQueue: 句柄已释放或不存在\"\n" +
                "#63 = string \"MessageQueue: 句柄重复释放\"\n" +
                "#64 = string \"MessageQueue: 不能派生 Owner\"\n" +
                "#65 = string \"MessageQueue: 不能从该句柄派生 Sender\"\n" +
                "#66 = string \"MessageQueue: 不能从该句柄派生 Reader\"\n" +
                "#67 = string \"MessageQueue: 该句柄不能 post\"\n" +
                "#68 = string \"MessageQueue: 该句柄不能 next\"\n" +
                "#69 = string \"MessageQueue: 队列已 sealed，不能 post\"\n" +
                "#70 = string \"MessageQueue: 队列已 sealed，不能派生 Sender\"\n" +
                "#71 = string \"MessageQueue: 同一 Reader 同时只能有一个 outstanding next\"\n" +
                "#72 = string \"MessageQueue: 非法操作\"\n" +
                "#73 = catch-table {  }\n" +
                "#74 = catch-table {  }\n" +
                "#75 = string \"Receiver.addListener：Receiver 已 dispose\"\n" +
                "#76 = catch-table {  }\n" +
                "#77 = catch-table {  }\n" +
                "#78 = string \"Receiver.setExecutor：listener 未注册\"\n" +
                "#79 = catch-table {  }\n" +
                "#80 = catch-table {  }\n" +
                "#81 = catch-table { type(core::IllegalStateException) -> blk(try0-catch0) }\n" +
                "#82 = catch-table {  }\n" +
                "#83 = catch-table {  }\n" +
                "#84 = null type(core.messaging::Receiver<.generic<$.generic.TMessage>>)\n" +
                "#85 = catch-table {  }\n" +
                "#86 = catch-table {  }\n" +
                "#87 = null type(.any)\n" +
                "#88 = i64 0\n" +
                "#89 = string \"检测到环引用，需显式启用 loopedRefEnabled\"\n" +
                "#90 = i64 1\n" +
                "#91 = string \"无效或重复的序列化节点编号\"\n" +
                "#92 = string \"序列化引用指向不存在的节点\"\n" +
                "#93 = string \"无效的序列化引用编号\"\n" +
                "#94 = null type(.generic<$.generic.TField>)\n" +
                "#95 = string \"Parcel 中不存在键：\"\n" +
                "#96 = i32 999999\n" +
                "#97 = string \"TimeStamp.nanoseconds 越界：\"\n" +
                "#98 = string \"（范围 0..999999）\"\n" +
                "#99 = null type(.generic<$.generic.TReturn>)\n" +
                "#100 = catch-table {  }\n" +
                "#101 = string \"..ref\"\n" +
                "#102 = string \"..id\"\n" +
                "#103 = string \"..data\"");
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
