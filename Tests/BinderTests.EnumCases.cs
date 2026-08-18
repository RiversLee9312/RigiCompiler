using System.Linq;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// S11 enum case（SYNTAX §12）P3 测试：声明点 init 模板绑定（结构过滤/
    /// 固定实参类型适用性决胜/洞 pub 规则/符号两槽落定）+ 使用侧三形态
    /// （裸 `.Success` 变量初始化·实参·返回值三上下文 / `.Failed(404)`
    /// 位置·具名·乱序具名 / `is .Case` 判别匹配——if 条件/switch pattern/
    /// Bool 定型/不触发 smart cast）与负例矩阵（上下文缺失/未知 case/参数化
    /// 裸引用/洞实参个数·类型·具名错误/声明点模板失败与使用侧静默/泛型
    /// enum 归口/new 永久规则/值匹配非常量锁定/is .Case 操作数非 enum）。
    /// </summary>
    public static partial class BinderTests
    {
        // 共享源骨架：一参 pub init + 固定 case（Success）与单洞参数化
        // case（Failed）
        private const string RequestResultSource =
            "pub enum struct RequestResult {\n" +
            "    pub const errorCode: i32\n" +
            "\n" +
            "    pub init(_ -> errorCode)\n" +
            "}[\n" +
            "    Success(-1),\n" +
            "    Failed(errorCode = _)\n" +
            "]\n";

        // 共享源骨架：两参 init + 双固定 case（Okay）与双洞参数化 case（Error）
        private const string DataResultSource =
            "pub enum struct DataResult {\n" +
            "    pub const code: i32\n" +
            "    pub const text: String\n" +
            "\n" +
            "    pub init(_ -> code, _ -> text)\n" +
            "}[\n" +
            "    Okay(0, \"ok\"),\n" +
            "    Error(code = _, text = _)\n" +
            "]\n";

        private static void TestEnumCases()
        {
            TestHarness.Section("P3 enum case（S11）");
            TestEnumCaseFixedForms();
            TestEnumCaseParameterizedForms();
            TestEnumCaseIsCase();
            TestEnumCaseTemplateBinding();
            TestEnumCaseSwitchBranchContext();
            TestEnumCaseFullNameForms();
            TestEnumCaseErrors();
        }

        // ===== 固定 case 使用侧（§12.1：裸 `.Success` 三上下文）=====
        private static void TestEnumCaseFixedForms()
        {
            // 变量初始化（声明类型提供期望类型）
            var (unit, bodies) = BindUnit(RequestResultSource +
                "func f() {\n" +
                "    const result: RequestResult = .Success\n" +
                "}\n");
            CheckNoErrors("固定 case 变量初始化无诊断", unit);
            TestHarness.Check("固定 case 变量初始化形态",
                BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [result: RequestResult], " +
                "[Decl(result, RequestResult, = EnumCase(RequestResult.Success, []))])");

            // 调用实参（形参类型提供期望类型）
            var (unit2, bodies2) = BindUnit(RequestResultSource +
                "func consume(r: RequestResult) { }\n" +
                "func f() {\n" +
                "    consume(.Success)\n" +
                "}\n");
            CheckNoErrors("固定 case 实参无诊断", unit2);
            TestHarness.Check("固定 case 实参形态",
                BoundDescribe.Body(BodyOf(bodies2, "f")),
                "Body(f, [], [CallStmt(consume, [EnumCase(RequestResult.Success, [])])])");

            // 返回值（返回类型提供期望类型）
            var (unit3, bodies3) = BindUnit(RequestResultSource +
                "func f(): RequestResult {\n" +
                "    return .Success\n" +
                "}\n");
            CheckNoErrors("固定 case 返回值无诊断", unit3);
            TestHarness.Check("固定 case 返回值形态",
                BoundDescribe.Body(BodyOf(bodies3, "f")),
                "Body(f, [], [Return(EnumCase(RequestResult.Success, []))])");

            // 嵌套固定实参：case 模板的固定实参本身是 enum case 引用
            // （单候选带目标类型绑定提供期望类型）；顺带覆盖无显式 init
            // 的零实参 case（默认零参构造成功路径）
            var (unit4, _) = BindUnit(
                "enum struct Level { }[Low, High]\n" +
                "pub enum struct Wrapped {\n" +
                "    pub const level: Level\n" +
                "    pub init(_ -> level)\n" +
                "}[\n" +
                "    Default(.Low)\n" +
                "]\n" +
                "func f() {\n" +
                "    const w: Wrapped = .Default\n" +
                "}\n");
            CheckNoErrors("嵌套 .Case 固定实参无诊断", unit4);
            var wrappedType = unit4.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Wrapped");
            var defaultCase = wrappedType.Cases.Single(c => c.Name == "Default");
            TestHarness.CheckTrue("嵌套固定实参模板落定",
                defaultCase.ResolvedInit != null && defaultCase.HoleParameters is { Count: 0 });
            var levelType = unit4.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Level");
            TestHarness.CheckTrue("零实参 case 默认零参构造落定（ResolvedInit 保持 null）",
                levelType.Cases.All(c => c.ResolvedInit == null
                    && c.HoleParameters is { Count: 0 }));
        }

        // ===== 参数化 case 使用侧（§12.1：`.Failed(404)` 位置/具名/乱序具名）=====
        private static void TestEnumCaseParameterizedForms()
        {
            // 位置实参
            var (unit, bodies) = BindUnit(RequestResultSource +
                "func f() {\n" +
                "    const failed: RequestResult = .Failed(404)\n" +
                "}\n");
            CheckNoErrors("参数化 case 位置实参无诊断", unit);
            TestHarness.Check("参数化 case 位置实参形态",
                BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [failed: RequestResult], " +
                "[Decl(failed, RequestResult, = EnumCase(RequestResult.Failed, [Int(404,i32)]))])");

            // 具名实参（按洞名归位）
            var (unit2, bodies2) = BindUnit(RequestResultSource +
                "func f() {\n" +
                "    const failed: RequestResult = .Failed(errorCode = 404)\n" +
                "}\n");
            CheckNoErrors("参数化 case 具名实参无诊断", unit2);
            TestHarness.Check("参数化 case 具名实参形态",
                BoundDescribe.Body(BodyOf(bodies2, "f")),
                "Body(f, [failed: RequestResult], " +
                "[Decl(failed, RequestResult, = EnumCase(RequestResult.Failed, [Int(404,i32)]))])");

            // 乱序具名（双洞）：产物规范序 = 洞签名序（= init 参数序）
            var (unit3, bodies3) = BindUnit(DataResultSource +
                "func f() {\n" +
                "    const e: DataResult = .Error(text = \"bad\", code = 7)\n" +
                "}\n");
            CheckNoErrors("乱序具名实参无诊断", unit3);
            TestHarness.Check("乱序具名归位规范序（init 参数序）",
                BoundDescribe.Body(BodyOf(bodies3, "f")),
                "Body(f, [e: DataResult], [Decl(e, DataResult, = EnumCase(DataResult.Error, " +
                "[Int(7,i32), Str(\"bad\",String)]))])");
            var errorCall = (BoundEnumCaseExpression)((BoundLocalDeclarationStatement)
                BodyOf(bodies3, "f").Body.Statements[0]).Initializer!;
            TestHarness.CheckTrue("洞签名结构事实（名/类型/索引取自 init 参数）",
                errorCall.Case.HoleParameters is { Count: 2 }
                && errorCall.Case.HoleParameters[0].Name == "code"
                && ReferenceEquals(errorCall.Case.HoleParameters[0].Type,
                    unit3.Symbols.Bootstrap.Int32)
                && errorCall.Case.HoleParameters[0].InitParameterIndex == 0
                && errorCall.Case.HoleParameters[1].Name == "text"
                && ReferenceEquals(errorCall.Case.HoleParameters[1].Type,
                    unit3.Symbols.Bootstrap.String)
                && errorCall.Case.HoleParameters[1].InitParameterIndex == 1);
        }

        // ===== is .Case 判别匹配（§12.3）=====
        private static void TestEnumCaseIsCase()
        {
            // if 条件：IsCase 形态 + Bool 定型；§12.3 不触发 smart cast
            // （体内 result 引用无 SmartCast 包装，描述串锁定）
            var (unit, bodies) = BindUnit(RequestResultSource +
                "func consume(r: RequestResult) { }\n" +
                "func f(result: RequestResult): i32 {\n" +
                "    if (result is .Failed) { consume(result) }\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("is .Case if 条件无诊断", unit);
            TestHarness.Check("is .Case 判别形态（不收窄——体内引用无 SmartCast）",
                BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [If(IsCase(Param(result,RequestResult), RequestResult.Failed), " +
                "[CallStmt(consume, [Param(result,RequestResult)])]); Return(Int(0,i32))])");
            var isCase = (BoundTypeCheckExpression)((BoundIfStatement)
                ((BoundBlock)BodyOf(bodies, "f").Body).Statements[0]).Condition;
            var requestResult = unit.Symbols.GlobalNamespace.Types
                .Single(t => t.Name == "RequestResult");
            TestHarness.CheckTrue("is .Case 结构事实（Bool 定型 + 三态互斥 + 符号引用相等）",
                isCase.Kind == BoundTypeCheckKind.IsCase
                && ReferenceEquals(isCase.Type, unit.Symbols.Bootstrap.Bool)
                && isCase.TargetType == null && isCase.TargetValue == null
                && ReferenceEquals(isCase.Case, requestResult.Cases.Single(c => c.Name == "Failed")));

            // switch pattern（`(_ is .Success)`：占位 selector 类型即操作数
            // 类型，TypeCheck 同一通道自动可用）
            var (unit2, bodies2) = BindUnit(RequestResultSource +
                "func f(result: RequestResult): i32 {\n" +
                "    switch (result) {\n" +
                "        (_ is .Success) -> { return 1 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("switch pattern is .Case 无诊断", unit2);
            TestHarness.Check("switch pattern is .Case 形态",
                BoundDescribe.Body(BodyOf(bodies2, "f")),
                "Body(f, [], [Switch(Param(result,RequestResult), " +
                "[CaseP(IsCase(Placeholder(RequestResult), RequestResult.Success), " +
                "[Return(Int(1,i32))])], [Return(Int(0,i32))])])");
        }

        // ===== 声明点模板绑定（§12.1/§12.2：符号两槽落定）=====
        private static void TestEnumCaseTemplateBinding()
        {
            var (unit, _) = BindUnit(RequestResultSource + "func f() { }\n");
            CheckNoErrors("模板绑定正例无诊断", unit);
            var enumType = unit.Symbols.GlobalNamespace.Types
                .Single(t => t.Name == "RequestResult");
            var init = enumType.Methods.Single(m => m.Kind == MethodKind.Init);
            var successCase = enumType.Cases.Single(c => c.Name == "Success");
            var failedCase = enumType.Cases.Single(c => c.Name == "Failed");
            TestHarness.CheckTrue("固定 case 模板落定（ResolvedInit + 空洞表）",
                ReferenceEquals(successCase.ResolvedInit, init)
                && successCase.HoleParameters is { Count: 0 });
            TestHarness.CheckTrue("参数化 case 模板落定（ResolvedInit + 洞签名）",
                ReferenceEquals(failedCase.ResolvedInit, init)
                && failedCase.HoleParameters is { Count: 1 }
                && failedCase.HoleParameters[0].Name == "errorCode"
                && ReferenceEquals(failedCase.HoleParameters[0].Type,
                    unit.Symbols.Bootstrap.Int32)
                && failedCase.HoleParameters[0].InitParameterIndex == 0);

            // priv init + 固定 case（§12.2：非 pub init 只允许固定模板——
            // 固定 case 合法）
            var (unit2, _) = BindUnit(
                "pub enum struct TokenKind {\n" +
                "    pub const code: i32\n" +
                "\n" +
                "    priv init(_ -> code)\n" +
                "}[\n" +
                "    Identifier(1),\n" +
                "    Number(2)\n" +
                "]\n" +
                "func f() {\n" +
                "    const k: TokenKind = .Identifier\n" +
                "}\n");
            CheckNoErrors("priv init 固定 case 合法", unit2);
            var tokenKind = unit2.Symbols.GlobalNamespace.Types
                .Single(t => t.Name == "TokenKind");
            TestHarness.CheckTrue("priv init 固定 case 模板落定",
                tokenKind.Cases.All(c => c.ResolvedInit != null
                    && c.HoleParameters is { Count: 0 }));
        }

        // ===== switch 分支体 `.Case` 解析上下文（§7.2/§12：selector
        // 静态类型为 enum struct 时，分支体内省略形式以 selector 类型
        // 为解析上下文；仅上下文贡献，不钉死分支期望类型）=====
        private static void TestEnumCaseSwitchBranchContext()
        {
            // 表达式形态：单表达式分支的 `.Failed(1)` 无期望类型可依赖
            // （隐式取值不经期望类型定型），纯靠 selector 上下文解析
            var (unit, bodies) = BindUnit(RequestResultSource +
                "func f(result: RequestResult): RequestResult {\n" +
                "    return switch(result) {\n" +
                "        (_ is .Success) -> { .Failed(1) }\n" +
                "        default -> { result }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("switch 表达式分支体 .Case 无诊断", unit);
            TestHarness.Check("switch 表达式分支体 .Case 形态",
                BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Return(SwitchExpr(Param(result,RequestResult), " +
                "[CaseP(IsCase(Placeholder(RequestResult), RequestResult.Success), " +
                "ValueBlock(_, RequestResult, implicit, " +
                "[ExprStmt(EnumCase(RequestResult.Failed, [Int(1,i32)]))]))], " +
                "ValueBlock(_, RequestResult, implicit, [ExprStmt(Param(result,RequestResult))]), " +
                "RequestResult))])");

            // 语句形态：分支体内无标注 const 的 `.Case`（含嵌套 if 块）
            // 同样以 selector 类型解析
            var (unit2, bodies2) = BindUnit(RequestResultSource +
                "func f(result: RequestResult): i32 {\n" +
                "    switch (result) {\n" +
                "        (_ is .Success) -> {\n" +
                "            if ((result.errorCode > 0)) {\n" +
                "                const x = .Failed(2)\n" +
                "                return x.errorCode\n" +
                "            }\n" +
                "            return 1\n" +
                "        }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("switch 语句分支体（嵌套）.Case 无诊断", unit2);
            TestHarness.CheckTrue("switch 语句分支体（嵌套）.Case 解析为 selector 类型",
                BoundDescribe.Body(BodyOf(bodies2, "f"))
                    .Contains("EnumCase(RequestResult.Failed, [Int(2,i32)])"));

            // 异质分支不受影响（负对照）：i32 selector 的 String 分支
            // 照既有统一规则推导，不引入任何 enum 上下文
            var (unit3, bodies3) = BindUnit(
                "func f(x: i32): String {\n" +
                "    return switch(x) {\n" +
                "        (1) -> { \"one\" }\n" +
                "        default -> { \"?\" }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("异质分支不受影响（i32 selector / String 分支）", unit3);
            TestHarness.CheckTrue("异质分支产值类型仍为 String",
                BoundDescribe.Body(BodyOf(bodies3, "f")).Contains(", String))])"));

            // 负对照：非 enum selector 的分支体内 `.Case` 维持既有拒绝
            var (e1, _) = BindUnit(RequestResultSource +
                "func f(x: i32): i32 {\n" +
                "    switch (x) {\n" +
                "        (1) -> { const y = .Success\nreturn 1 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("非 enum selector 分支体 .Case 仍拒绝",
                e1.Diagnostics, "Cannot infer the enum type of '.Success' from context");
        }

        // ===== `EnumType.Case` 全形（§12：case 亦可以全形引用；与
        // `.Case` 省略形式同一 case 构造通道）=====
        private static void TestEnumCaseFullNameForms()
        {
            // 固定 case 全形（无类型标注——全形自带类型上下文）
            var (unit, bodies) = BindUnit(RequestResultSource +
                "func f() {\n" +
                "    const result = RequestResult.Success\n" +
                "}\n");
            CheckNoErrors("全形固定 case 无诊断", unit);
            TestHarness.Check("全形固定 case 形态",
                BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [result: RequestResult], " +
                "[Decl(result, RequestResult, = EnumCase(RequestResult.Success, []))])");

            // 参数化全形：位置实参
            var (unit2, bodies2) = BindUnit(RequestResultSource +
                "func f() {\n" +
                "    const failed = RequestResult.Failed(404)\n" +
                "}\n");
            CheckNoErrors("全形参数化 case 位置实参无诊断", unit2);
            TestHarness.Check("全形参数化 case 位置实参形态",
                BoundDescribe.Body(BodyOf(bodies2, "f")),
                "Body(f, [failed: RequestResult], " +
                "[Decl(failed, RequestResult, = EnumCase(RequestResult.Failed, [Int(404,i32)]))])");

            // 参数化全形：具名实参（与省略形式同一归位通道）
            var (unit3, bodies3) = BindUnit(RequestResultSource +
                "func f() {\n" +
                "    const failed = RequestResult.Failed(errorCode = 404)\n" +
                "}\n");
            CheckNoErrors("全形参数化 case 具名实参无诊断", unit3);
            TestHarness.Check("全形参数化 case 具名实参形态",
                BoundDescribe.Body(BodyOf(bodies3, "f")),
                "Body(f, [failed: RequestResult], " +
                "[Decl(failed, RequestResult, = EnumCase(RequestResult.Failed, [Int(404,i32)]))])");

            // 全形 case 值作 receiver 续链（`EnumType.Case.member`）
            var (unit4, bodies4) = BindUnit(RequestResultSource +
                "func f(): i32 {\n" +
                "    return RequestResult.Failed(7).errorCode\n" +
                "}\n");
            CheckNoErrors("全形 case 续链无诊断", unit4);
            var chainAccess = (BoundFieldAccessExpression)((BoundReturnStatement)
                ((BoundBlock)BodyOf(bodies4, "f").Body).Statements[0]).Value!;
            TestHarness.CheckTrue("全形 case 续链结构事实（receiver 是 case 构造）",
                chainAccess.Field.Name == "errorCode"
                && chainAccess.Receiver is BoundEnumCaseExpression { Case.Name: "Failed" });
        }

        // ===== 负例矩阵 =====
        private static void TestEnumCaseErrors()
        {
            // 上下文缺失（var 无类型标注——§12 编译器不反向猜测 enum 类型）
            var (e1, _) = BindUnit(RequestResultSource +
                "func f() {\n" +
                "    var x = .Success\n" +
                "}\n");
            TestHarness.CheckSemanticError("裸 case 上下文缺失拒绝", e1.Diagnostics,
                "Cannot infer the enum type of '.Success' from context");

            // 期望类型非 enum struct
            var (e2, _) = BindUnit(RequestResultSource +
                "func f() {\n" +
                "    const x: i32 = .Success\n" +
                "}\n");
            TestHarness.CheckSemanticError("期望类型非 enum 拒绝", e2.Diagnostics,
                "Cannot infer the enum type of '.Success' from context");

            // 未知 case 名
            var (e3, _) = BindUnit(RequestResultSource +
                "func f() {\n" +
                "    const x: RequestResult = .NoSuch\n" +
                "}\n");
            TestHarness.CheckSemanticError("未知 case 名拒绝", e3.Diagnostics,
                "Undefined case 'NoSuch' on 'RequestResult'");

            // 参数化 case 裸引用
            var (e4, _) = BindUnit(RequestResultSource +
                "func f() {\n" +
                "    const x: RequestResult = .Failed\n" +
                "}\n");
            TestHarness.CheckSemanticError("参数化裸引用拒绝", e4.Diagnostics,
                "Case 'Failed' requires 1 argument(s)");

            // 洞实参缺失（洞无默认值）
            var (e5, _) = BindUnit(RequestResultSource +
                "func f() {\n" +
                "    const x: RequestResult = .Failed()\n" +
                "}\n");
            TestHarness.CheckSemanticError("洞实参缺失拒绝", e5.Diagnostics,
                "Missing argument for hole 'errorCode'");

            // 洞实参过多
            var (e6, _) = BindUnit(RequestResultSource +
                "func f() {\n" +
                "    const x: RequestResult = .Failed(1, 2)\n" +
                "}\n");
            TestHarness.CheckSemanticError("洞实参过多拒绝", e6.Diagnostics,
                "Too many arguments for case 'Failed'");

            // 洞实参类型不符
            var (e7, _) = BindUnit(RequestResultSource +
                "func f() {\n" +
                "    const x: RequestResult = .Failed(\"oops\")\n" +
                "}\n");
            TestHarness.CheckSemanticError("洞实参类型不符拒绝", e7.Diagnostics,
                "Cannot pass 'String' as 'i32'");

            // 洞具名不匹配
            var (e8, _) = BindUnit(RequestResultSource +
                "func f() {\n" +
                "    const x: RequestResult = .Failed(code = 1)\n" +
                "}\n");
            TestHarness.CheckSemanticError("洞具名不匹配拒绝", e8.Diagnostics,
                "Case 'Failed' has no hole named 'code'");

            // 固定 case 带实参
            var (e9, _) = BindUnit(RequestResultSource +
                "func f() {\n" +
                "    const x: RequestResult = .Success(1)\n" +
                "}\n");
            TestHarness.CheckSemanticError("固定 case 带实参拒绝", e9.Diagnostics,
                "Case 'Success' takes no arguments");

            // 声明点：非 pub init 有洞（§12.2）
            var (e10, _) = BindUnit(
                "pub enum struct TokenKind {\n" +
                "    pub const code: i32\n" +
                "\n" +
                "    priv init(_ -> code)\n" +
                "}[\n" +
                "    Identifier(1),\n" +
                "    Custom(code = _)\n" +
                "]\n");
            TestHarness.CheckSemanticError("非 pub init 有洞拒绝（声明点）", e10.Diagnostics,
                "Parameterized enum case 'Custom' requires a pub init");

            // 声明点：无匹配 init（实参个数不符）
            var (e11, _) = BindUnit(
                "pub enum struct Weird {\n" +
                "    pub const code: i32\n" +
                "    pub init(_ -> code)\n" +
                "}[\n" +
                "    Odd(1, 2)\n" +
                "]\n");
            TestHarness.CheckSemanticError("无匹配 init（个数不符）拒绝", e11.Diagnostics,
                "Enum case 'Odd' has no matching init template");

            // 声明点：无匹配 init（具名不匹配——对应位置参数名不符）
            var (e12, _) = BindUnit(
                "pub enum struct Weird2 {\n" +
                "    pub const code: i32\n" +
                "    pub init(_ -> code)\n" +
                "}[\n" +
                "    Stray(other = 1)\n" +
                "]\n");
            TestHarness.CheckSemanticError("无匹配 init（具名不符）拒绝", e12.Diagnostics,
                "Enum case 'Stray' has no matching init template");

            // 声明点：固定实参类型不符
            var (e13, _) = BindUnit(
                "pub enum struct Weird3 {\n" +
                "    pub const code: i32\n" +
                "    pub init(_ -> code)\n" +
                "}[\n" +
                "    Bad(\"x\")\n" +
                "]\n");
            TestHarness.CheckSemanticError("固定实参类型不符拒绝（声明点）", e13.Diagnostics,
                "Enum case 'Bad' has no matching init template");

            // 声明点：多候选歧义（固定实参类型两可）
            var (e14, _) = BindUnit(
                "open class Base { }\n" +
                "class Derived : Base { }\n" +
                "pub enum struct Amb {\n" +
                "    pub const v: Base\n" +
                "    pub init(_ -> v)\n" +
                "    pub init(x: Derived) { }\n" +
                "}[\n" +
                "    Which(new Derived())\n" +
                "]\n");
            TestHarness.CheckSemanticError("模板绑定歧义拒绝（声明点）", e14.Diagnostics,
                "Enum case 'Which' matches multiple init templates");

            // 模板绑定失败的使用侧静默（声明点诊断已报，不二次报）
            var (e15, _) = BindUnit(
                "pub enum struct Broken {\n" +
                "    pub const code: i32\n" +
                "    pub init(_ -> code)\n" +
                "}[\n" +
                "    Bad(\"x\")\n" +
                "]\n" +
                "func f() {\n" +
                "    var x: Broken = .Bad\n" +
                "}\n");
            TestHarness.CheckSemanticError("模板失败声明点诊断", e15.Diagnostics,
                "Enum case 'Bad' has no matching init template");
            var errorCount = e15.Diagnostics.Diagnostics
                .Count(d => d.Severity == DiagnosticSeverity.Error);
            TestHarness.CheckTrue("模板失败使用侧静默（不二次报）", errorCount == 1,
                string.Join("; ", e15.Diagnostics.Diagnostics.Select(d => d.Message)));

            // 泛型 enum 归口（声明侧：模板绑定跳过）
            var (e16, _) = BindUnit(
                "enum struct Box\\<T> {\n" +
                "    pub const value: i32\n" +
                "    pub init(_ -> value)\n" +
                "}[\n" +
                "    A(1)\n" +
                "]\n");
            TestHarness.CheckSemanticError("泛型 enum 归口（声明侧）", e16.Diagnostics,
                "generic enum cases are not supported yet (S11)");

            // 泛型 enum 归口（使用侧：expectedType 为构造 enum 类型）
            var (e17, _) = BindUnit(
                "enum struct Box\\<T> {\n" +
                "    pub const value: i32\n" +
                "    pub init(_ -> value)\n" +
                "}[\n" +
                "    A(1)\n" +
                "]\n" +
                "func f() {\n" +
                "    const x: Box\\<i32> = .A\n" +
                "}\n");
            TestHarness.CheckSemanticError("泛型 enum 归口（使用侧）", e17.Diagnostics,
                "generic enum cases are not supported yet (S11)");

            // new 永久规则（§12.2：enum 值只能经具名 case 入口产生）
            var (e18, _) = BindUnit(RequestResultSource +
                "func f() {\n" +
                "    var x = new RequestResult(-1)\n" +
                "}\n");
            TestHarness.CheckSemanticError("new enum 永久规则", e18.Diagnostics,
                "Cannot construct enum struct 'RequestResult' directly; use its named cases");

            // switch 值匹配位置 enum case 非常量锁定（M49 规则不扩展）
            var (e19, _) = BindUnit(RequestResultSource +
                "func f(result: RequestResult): i32 {\n" +
                "    switch (result) {\n" +
                "        (.Success) -> { return 1 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("值匹配位置非常量锁定", e19.Diagnostics,
                "switch value-match case requires a compile-time constant");

            // is .Case 操作数非 enum struct
            var (e20, _) = BindUnit(RequestResultSource +
                "func f(): bool {\n" +
                "    return (1 is .Failed)\n" +
                "}\n");
            TestHarness.CheckSemanticError("is .Case 操作数非 enum 拒绝", e20.Diagnostics,
                "Left operand of 'is .Case' must be an enum struct type (got 'i32')");

            // is .Case 未知 case 名
            var (e21, _) = BindUnit(RequestResultSource +
                "func f(result: RequestResult): bool {\n" +
                "    return (result is .NoSuch)\n" +
                "}\n");
            TestHarness.CheckSemanticError("is .Case 未知 case 拒绝", e21.Diagnostics,
                "Undefined case 'NoSuch' on 'RequestResult'");

            // 全形未知 case 名
            var (e22, _) = BindUnit(RequestResultSource +
                "func f() {\n" +
                "    const x = RequestResult.NoSuch\n" +
                "}\n");
            TestHarness.CheckSemanticError("全形未知 case 名拒绝", e22.Diagnostics,
                "Undefined case 'NoSuch' on 'RequestResult'");

            // 全形参数化 case 裸引用（缺洞实参）
            var (e23, _) = BindUnit(RequestResultSource +
                "func f() {\n" +
                "    const x = RequestResult.Failed\n" +
                "}\n");
            TestHarness.CheckSemanticError("全形参数化裸引用拒绝", e23.Diagnostics,
                "Case 'Failed' requires 1 argument(s)");

            // 全形固定 case 带实参
            var (e24, _) = BindUnit(RequestResultSource +
                "func f(): i32 {\n" +
                "    return RequestResult.Success(1)\n" +
                "}\n");
            TestHarness.CheckSemanticError("全形固定 case 带实参拒绝", e24.Diagnostics,
                "Case 'Success' takes no arguments");
        }
    }
}
