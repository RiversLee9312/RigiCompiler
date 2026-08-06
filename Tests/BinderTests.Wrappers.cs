using System.Linq;

namespace LatteCompiler.Tests
{
    // BinderTests 的 S11 wrapper place 部分（SYNTAX §14.1/§14.5）：
    // `obj:W` 只读 place 绑定（Entity：宿主类型 AppliedWrappers；Value：
    // 字段/局部符号 AppliedWrappers——局部应用由 P3 登记，矩阵 C 恒合法）
    // 与只读禁令全拦截面（链末 Colon 段按赋值/取值诊断——变量初始化/
    // 实参/返回值/推断源/运算与类型检查操作数全经路径绑定结果一处收口）。
    // P4a 降级（S11c）：Entity = get.wrapper 值拷贝 + get.field /
    // set.field.embedded；字段-Value = get/set.field.embedded 链；
    // 局部/静态存储归口（栈帧/静态存储合成归后续里程碑）。

    public static partial class BinderTests
    {
        // ===== wrapper place 绑定正例（§14.5：成员访问接收者位置）=====
        private static void TestWrapperPlaceBinding()
        {
            TestHarness.Section("P3 Wrapper Place Binding");

            // Entity wrapper：字段读（place 作 InstField receiver）
            var (unit, bodies) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "    pub func dump(): String { return level }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "}\n" +
                "pub func f(s: Service): String {\n" +
                "    return s:Logged.level\n" +
                "}\n");
            CheckNoErrors("Entity wrapper 字段读无诊断", unit);
            TestHarness.Check("Entity wrapper 字段读形态",
                "InstField(level, WrapperPlace(Param(s,Service), Logged), String)",
                BoundDescribe.Expr(
                    ((BoundReturnStatement)((BoundBlock)BodyOf(bodies, "f").Body).Statements[0])
                    .Value));

            // Entity wrapper：方法调用（place 作 InstCall receiver）
            var (unit2, bodies2) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "    pub func dump(): String { return level }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service): String {\n" +
                "    return s:Logged.dump()\n" +
                "}\n");
            CheckNoErrors("Entity wrapper 方法调用无诊断", unit2);
            TestHarness.Check("Entity wrapper 方法调用形态",
                "InstCall(dump, WrapperPlace(Param(s,Service), Logged), [], String)",
                BoundDescribe.Expr(
                    ((BoundReturnStatement)((BoundBlock)BodyOf(bodies2, "f").Body).Statements[0])
                    .Value));

            // Entity wrapper：字段写（place 作赋值目标 InstField 的 receiver）
            var (unit3, bodies3) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service) {\n" +
                "    s:Logged.level = \"TRACE\"\n" +
                "}\n");
            CheckNoErrors("Entity wrapper 字段写无诊断", unit3);
            var assignment3 = (BoundAssignmentStatement)((BoundBlock)BodyOf(bodies3, "f").Body)
                .Statements[0];
            TestHarness.Check("Entity wrapper 字段写形态",
                "InstField(level, WrapperPlace(Param(s,Service), Logged), String)",
                BoundDescribe.Expr(assignment3.Target));

            // 链式 `obj:A:B`：wrapper 的 wrapper（Entity wrapper 修饰 wrapper）
            var (unit4, bodies4) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Inner {\n" +
                "    pub var tag: String\n" +
                "    pub init(_ -> tag)\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "@Inner\n" +
                "pub wrapper Outer {\n" +
                "    pub init()\n" +
                "}\n" +
                "@Outer\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service): String {\n" +
                "    return s:Outer:Inner.tag\n" +
                "}\n");
            CheckNoErrors("链式 wrapper 无诊断", unit4);
            TestHarness.Check("链式 wrapper 形态",
                "InstField(tag, WrapperPlace(WrapperPlace(Param(s,Service), Outer), Inner), String)",
                BoundDescribe.Expr(
                    ((BoundReturnStatement)((BoundBlock)BodyOf(bodies4, "f").Body).Statements[0])
                    .Value));

            // this 宿主：宿主类成员方法体内 `this:W`
            var (unit5, bodies5) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func check(): String { return this:Logged.level }\n" +
                "}\n");
            CheckNoErrors("this 宿主 wrapper 无诊断", unit5);
            TestHarness.Check("this 宿主 wrapper 形态",
                "InstField(level, WrapperPlace(This(Service), Logged), String)",
                BoundDescribe.Expr(
                    ((BoundReturnStatement)((BoundBlock)BodyOf(bodies5, "check").Body).Statements[0])
                    .Value));

            // 局部变量 Value wrapper（P3 登记，§14.9 矩阵 C 恒合法）
            var (unit6, bodies6) = BindUnit(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init(_ -> min)\n" +
                "}\n" +
                "pub func f(): i32 {\n" +
                "    @Clamped\n" +
                "    var health: i32 = 50\n" +
                "    return health:Clamped.min\n" +
                "}\n");
            CheckNoErrors("局部变量 Value wrapper 无诊断", unit6);
            TestHarness.Check("局部变量 Value wrapper 形态",
                "InstField(min, WrapperPlace(Local(health,i32), Clamped), i32)",
                BoundDescribe.Expr(
                    ((BoundReturnStatement)((BoundBlock)BodyOf(bodies6, "f").Body).Statements[1])
                    .Value));
            // 符号断言：局部符号的 AppliedWrappers 即 Clamped 定义（引用相等）
            var health6 = BodyOf(bodies6, "f").Locals.First(l => l.Name == "health");
            var clamped6 = unit6.Symbols.GlobalNamespace.Types.First(t => t.Name == "Clamped");
            TestHarness.CheckTrue("局部 wrapper 应用登记（引用相等）",
                health6.AppliedWrappers.Count == 1
                && ReferenceEquals(health6.AppliedWrappers[0].Wrapper, clamped6));

            // 静态字段 Value wrapper（shared wrapper × 静态目标，§14.9 矩阵）
            var (unit7, bodies7) = BindUnit(
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper SClamp {\n" +
                "    pub var min: i32\n" +
                "    pub init(_ -> min)\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    @SClamp\n" +
                "    pub static var counter: i32 = 0\n" +
                "}\n" +
                "pub func f(): i32 {\n" +
                "    return Holder.counter:SClamp.min\n" +
                "}\n");
            CheckNoErrors("静态字段 Value wrapper 无诊断", unit7);
            TestHarness.Check("静态字段 Value wrapper 形态",
                "InstField(min, WrapperPlace(Field(counter,i32), SClamp), i32)",
                BoundDescribe.Expr(
                    ((BoundReturnStatement)((BoundBlock)BodyOf(bodies7, "f").Body).Statements[0])
                    .Value));

            // wrapper place 上的索引后缀（成员访问扩展：getAtIndex operator）
            var (unit8, bodies8) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Indexed {\n" +
                "    pub var store: i32\n" +
                "    pub init(_ -> store)\n" +
                "    pub operator getAtIndex(index: i32): i32 { return store }\n" +
                "}\n" +
                "@Indexed\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service): i32 {\n" +
                "    return s:Indexed[0]\n" +
                "}\n");
            CheckNoErrors("wrapper place 索引后缀无诊断", unit8);
            TestHarness.Check("wrapper place 索引后缀形态",
                "Index(WrapperPlace(Param(s,Service), Indexed), Int(0,i32), i32)",
                BoundDescribe.Expr(
                    ((BoundReturnStatement)((BoundBlock)BodyOf(bodies8, "f").Body).Statements[0])
                    .Value));
        }

        // ===== 只读禁令（§14.5 全拦截面：链末 Colon 段一处收口）=====
        private static void TestWrapperPlaceReadOnly()
        {
            TestHarness.Section("P3 Wrapper Place Read-Only");

            const string fixture =
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n";

            // 赋值目标：`obj:W = ...` 非法
            var (unit, _) = BindUnit(fixture +
                "pub func f(s: Service) {\n" +
                "    s:Logged = \"x\"\n" +
                "}\n");
            TestHarness.CheckSemanticError("wrapper place 不可赋值", unit.Diagnostics,
                "Cannot assign to wrapper place ':Logged'");

            // 变量初始化（推断源）：`var snap = obj:W` 非法
            var (unit2, _) = BindUnit(fixture +
                "pub func f(s: Service) {\n" +
                "    var snap = s:Logged\n" +
                "}\n");
            TestHarness.CheckSemanticError("wrapper place 不可作推断源", unit2.Diagnostics,
                "Wrapper place ':Logged' cannot be used as a value");

            // 实参：`take(obj:W)` 非法
            var (unit3, _) = BindUnit(fixture +
                "pub func take(l: Logged) { }\n" +
                "pub func f(s: Service) {\n" +
                "    take(s:Logged)\n" +
                "}\n");
            TestHarness.CheckSemanticError("wrapper place 不可作实参", unit3.Diagnostics,
                "Wrapper place ':Logged' cannot be used as a value");

            // 返回值：`return obj:W` 非法
            var (unit4, _) = BindUnit(fixture +
                "pub func f(s: Service): Logged {\n" +
                "    return s:Logged\n" +
                "}\n");
            TestHarness.CheckSemanticError("wrapper place 不可作返回值", unit4.Diagnostics,
                "Wrapper place ':Logged' cannot be used as a value");

            // 复合赋值（读路径取值）：`obj:W += x` 非法
            var (unit5, _) = BindUnit(fixture +
                "pub func f(s: Service) {\n" +
                "    s:Logged += \"x\"\n" +
                "}\n");
            TestHarness.CheckSemanticError("wrapper place 不可复合赋值", unit5.Diagnostics,
                "Wrapper place ':Logged' cannot be used as a value");

            // 类型检查操作数：`obj:W is W` 非法
            var (unit6, _) = BindUnit(fixture +
                "pub func f(s: Service): bool {\n" +
                "    return s:Logged is Logged\n" +
                "}\n");
            TestHarness.CheckSemanticError("wrapper place 不可作 is 操作数", unit6.Diagnostics,
                "Wrapper place ':Logged' cannot be used as a value");

            // 插值段：`"${obj:W}"` 非法
            var (unit7, _) = BindUnit(fixture +
                "pub func f(s: Service): String {\n" +
                "    return \"${s:Logged}\"\n" +
                "}\n");
            TestHarness.CheckSemanticError("wrapper place 不可作插值段", unit7.Diagnostics,
                "Wrapper place ':Logged' cannot be used as a value");
        }

        // ===== wrapper place 绑定负例（查找/宿主/登记检查）=====
        private static void TestWrapperPlaceErrors()
        {
            TestHarness.Section("P3 Wrapper Place Errors");

            const string fixture =
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n";

            // 未应用的 wrapper 名
            var (unit, _) = BindUnit(fixture +
                "pub func f(s: Service): String {\n" +
                "    return s:Unknown.level\n" +
                "}\n");
            TestHarness.CheckSemanticError("未应用的 wrapper 名", unit.Diagnostics,
                "'Service' has no wrapper 'Unknown' applied");

            // nullable 宿主拒绝（Colon 无安全访问形态）
            var (unit2, _) = BindUnit(fixture +
                "pub func f(s: Service?): String {\n" +
                "    return s:Logged.level\n" +
                "}\n");
            TestHarness.CheckSemanticError("nullable 宿主拒绝", unit2.Diagnostics,
                "Wrapper place ':Logged' cannot be accessed on nullable type");

            // `Type:W`：wrapper place 需要值宿主
            var (unit3, _) = BindUnit(fixture +
                "pub func f(): String {\n" +
                "    return Service:Logged.level\n" +
                "}\n");
            TestHarness.CheckSemanticError("类型宿主拒绝", unit3.Diagnostics,
                "Wrapper place ':Logged' requires a value host");

            // 局部挂 Entity wrapper（类别不符）
            var (unit4, _) = BindUnit(fixture +
                "pub func f(): i32 {\n" +
                "    @Logged\n" +
                "    var x: i32 = 1\n" +
                "    return x\n" +
                "}\n");
            TestHarness.CheckSemanticError("局部挂 Entity wrapper", unit4.Diagnostics,
                "Entity wrapper 'Logged' can only be applied to type declarations");

            // 局部挂非 wrapper 类型
            var (unit5, _) = BindUnit(fixture +
                "pub func f(): i32 {\n" +
                "    @Service\n" +
                "    var x: i32 = 1\n" +
                "    return x\n" +
                "}\n");
            TestHarness.CheckSemanticError("局部挂非 wrapper 类型", unit5.Diagnostics,
                "'Service' is not a wrapper type");

            // 局部挂 @WrapperTarget（只能挂 wrapper 声明）
            var (unit6, _) = BindUnit(
                "pub func f(): i32 {\n" +
                "    @WrapperTarget(.Entity)\n" +
                "    var x: i32 = 1\n" +
                "    return x\n" +
                "}\n");
            TestHarness.CheckSemanticError("局部挂 @WrapperTarget", unit6.Diagnostics,
                "@WrapperTarget can only be applied to wrapper declarations");

            // 解构声明上的 wrapper 应用归口
            var (unit7, _) = BindUnit(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init(_ -> min)\n" +
                "}\n" +
                "pub func f(): i32 {\n" +
                "    @Clamped\n" +
                "    var (a, b) = 1\n" +
                "    return a\n" +
                "}\n");
            TestHarness.CheckSemanticError("解构声明 wrapper 应用归口", unit7.Diagnostics,
                "wrapper applications on destructuring declarations are not supported yet (S11)");

            // 局部无应用：`x:W`（x 未挂任何 wrapper，类型 i32 不可修饰）
            var (unit8, _) = BindUnit(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init(_ -> min)\n" +
                "}\n" +
                "pub func f(): i32 {\n" +
                "    var x: i32 = 1\n" +
                "    return x:Clamped.min\n" +
                "}\n");
            TestHarness.CheckSemanticError("局部无 wrapper 应用", unit8.Diagnostics,
                "'i32' has no wrapper 'Clamped' applied");
        }

        // ===== P4a 降级（S11c 转正）：wrapper place 成员访问的 lowering =====
        private static void TestWrapperPlaceLowering()
        {
            TestHarness.Section("P4a Wrapper Place Lowering (S11c)");

            // Entity 字段读：get.wrapper 值拷贝物化 + get.field（§12.4 注记）
            var (unit, bodies) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service): String {\n" +
                "    return s:Logged.level\n" +
                "}\n");
            CheckNoErrors("P3 绑定无诊断", unit);
            var lowered = Lowerer.Lower(unit, bodies);
            CheckNoErrors("P4 降级无诊断", unit);
            TestHarness.Check("Entity 字段读降级形态（值拷贝 + get.field）",
                "Body(f, [.s0: Logged], " +
                "[Assign(Local(.s0,Logged), GetWrapper(Param(s,Service), Logged)); " +
                "Return(InstField(level, Local(.s0,Logged), String))])",
                LoweredDescribe.Body(lowered.Single(b => b.Method.Name == "f")));

            // Entity 字段写：set.field.embedded 链（§13.3）
            var (unit2, bodies2) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service) {\n" +
                "    s:Logged.level = \"TRACE\"\n" +
                "}\n");
            CheckNoErrors("P3 写字段绑定无诊断", unit2);
            var lowered2 = Lowerer.Lower(unit2, bodies2);
            CheckNoErrors("P4 写字段降级无诊断", unit2);
            TestHarness.Check("Entity 字段写降级形态（embedded 链）",
                "Body(f, [], " +
                "[Assign(Embedded(Param(s,Service), [.wrapper.Logged], level, String), " +
                "Str(\"TRACE\",String))])",
                LoweredDescribe.Body(lowered2.Single(b => b.Method.Name == "f")));

            // 字段-Value 应用字段读：embedded 链（宿主值 = 字段访问的 receiver）
            var (unit3, bodies3) = BindUnit(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init(_ -> min)\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Clamped\n" +
                "    pub var hp: i32\n" +
                "    pub init(h: i32) { hp = h }\n" +
                "}\n" +
                "pub func f(hero: Hero): i32 {\n" +
                "    return hero.hp:Clamped.min\n" +
                "}\n");
            CheckNoErrors("P3 字段-Value 绑定无诊断", unit3);
            var lowered3 = Lowerer.Lower(unit3, bodies3);
            CheckNoErrors("P4 字段-Value 降级无诊断", unit3);
            TestHarness.Check("字段-Value 字段读降级形态（embedded 读）",
                "Body(f, [], " +
                "[Return(Embedded(Param(hero,Hero), [.wrapper.Clamped], min, i32))])",
                LoweredDescribe.Body(lowered3.Single(b => b.Method.Name == "f")));

            // 归口：局部 wrapper place（栈帧存储合成归后续里程碑）
            var (unit4, bodies4) = BindUnit(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init(_ -> min)\n" +
                "}\n" +
                "pub func f(): i32 {\n" +
                "    @Clamped\n" +
                "    var health: i32 = 50\n" +
                "    return health:Clamped.min\n" +
                "}\n");
            CheckNoErrors("P3 局部 place 绑定无诊断", unit4);
            Lowerer.Lower(unit4, bodies4);
            TestHarness.CheckSemanticError("P4 局部 wrapper place 归口", unit4.Diagnostics,
                "wrapper place storage for local variables");
        }

        // ===== S11b proxy 体逐组合绑定：转发壳/特化体/解包 shim 三件套 +
        // self/inner/this 上色 + 负例与诊断去重 =====
        private static void TestProxyBodyBinding()
        {
            TestHarness.Section("P3 Proxy Body Binding (S11b)");

            // A. specific 单环三件套：转发壳（invoke 链首）/ 原始体（用户体
            // 改挂 .wrapped.）/ 特化体（inner = 链末原始体，实参直通特化形参）
            var (unit1, bodies1) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "    operator .proxy.doSomething(arg: i32): String { return inner(arg) }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func doSomething(arg: i32): String { return \"x\" }\n" +
                "}\n");
            CheckNoErrors("specific 单环无诊断", unit1);
            TestHarness.Check("转发壳 body（invoke 链首）",
                BoundDescribe.Body(BodyOf(bodies1, "doSomething")),
                "Body(doSomething, [], [Return(InstCall(.proxy.0.doSomething, This(Service), [Param(arg,i32)], String))])");
            TestHarness.Check("原始体 body（用户体改挂 .wrapped. 符号）",
                BoundDescribe.Body(BodyOf(bodies1, ".wrapped.doSomething")),
                "Body(.wrapped.doSomething, [], [Return(Str(\"x\",String))])");
            TestHarness.Check("特化体 body（inner 绑定 = 链末原始体调用）",
                BoundDescribe.Body(BodyOf(bodies1, ".proxy.0.doSomething")),
                "Body(.proxy.0.doSomething, [], [Return(InstCall(.wrapped.doSomething, This(Service), [Param(arg,i32)], String))])");
            TestHarness.CheckTrue("特化体实参引用特化符号形参（BIL .args 一致）",
                ((BoundInstanceCallExpression)((BoundReturnStatement)((BoundBlock)
                    BodyOf(bodies1, ".proxy.0.doSomething").Body).Statements[0]).Value!)
                .Arguments[0] is BoundValueReferenceExpression argRef
                && ReferenceEquals(argRef.Symbol,
                    BodyOf(bodies1, ".proxy.0.doSomething").Method.Parameters[0]));

            // B. self/this 上色：self = 宿主角色 this（TTarget 代入结果）；
            // this = wrapper 只读 place（成员访问接收者）
            var (unit2, bodies2) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "    pub func tag(): String { return level }\n" +
                "    operator .proxy.doSomething(arg: i32): String {\n" +
                "        var s = self\n" +
                "        return this.level\n" +
                "    }\n" +
                "    operator .proxy.ping(): String { return this.tag() }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func doSomething(arg: i32): String { return \"x\" }\n" +
                "    pub func ping(): String { return \"p\" }\n" +
                "}\n");
            CheckNoErrors("self/this 形态无诊断", unit2);
            TestHarness.Check("self 上色（= 宿主角色的 this，TTarget 代入结果）",
                BoundDescribe.Body(BodyOf(bodies2, ".proxy.0.doSomething")),
                "Body(.proxy.0.doSomething, [s: Service], " +
                "[Decl(s, Service, = This(Service)); " +
                "Return(InstField(level, WrapperPlace(This(Service), Logged), String))])");
            TestHarness.Check("this 作方法调用接收者（wrapper place）",
                BoundDescribe.Body(BodyOf(bodies2, ".proxy.0.ping")),
                "Body(.proxy.0.ping, [], " +
                "[Return(InstCall(tag, WrapperPlace(This(Service), Logged), [], String))])");

            // C. wildcard 单环：前奏三形参物化（symbol 常量 + 双包）+
            // inner → 解包 shim；shim body = 逐元素 cast 解包 + invoke 原始体
            var (unit3, bodies3) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audited {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@Audited\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func fetch(id: i32): String { return \"r\" }\n" +
                "}\n");
            CheckNoErrors("wildcard 单环无诊断", unit3);
            TestHarness.Check("wildcard 特化体（前奏物化 + inner 调 shim）",
                BoundDescribe.Body(BodyOf(bodies3, ".proxy.0.fetch")),
                "Body(.proxy.0.fetch, [symbol: String, namedArgs: Array<Any>, unnamedArgs: Array<Any>], " +
                "[Decl(symbol, String, = Str(\"Service$fetch(id:.i32)@.string\",String)); " +
                "Decl(namedArgs, Array<Any>, = KwArgs([])); " +
                "Decl(unnamedArgs, Array<Any>, = VarArgs([Param(id,i32)])); " +
                "Return(InstCall(.proxy.unwrap.0.fetch, This(Service), " +
                "[Local(namedArgs,Array<Any>), Local(unnamedArgs,Array<Any>)], String))])");
            TestHarness.Check("解包 shim body（逐元素 cast + invoke 原始体）",
                BoundDescribe.Body(BodyOf(bodies3, ".proxy.unwrap.0.fetch")),
                "Body(.proxy.unwrap.0.fetch, [], " +
                "[Return(InstCall(.wrapped.fetch, This(Service), " +
                "[Cast(Index(Param(unnamedArgs,Array<Any>), Int(0,i32), Any), i32)], String))])");

            // D. 双环链 outer→inner：L0 specific 的 inner → L1（内层特化）；
            // L1 wildcard 的 inner → shim → 原始体
            var (unit4, bodies4) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    pub init()\n" +
                "    operator .proxy.doSomething(arg: i32): String { return inner(arg) }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audited {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@Logged\n" +
                "@Audited\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func doSomething(arg: i32): String { return \"x\" }\n" +
                "}\n");
            CheckNoErrors("双环链无诊断", unit4);
            TestHarness.Check("L0 specific 的 inner → L1 特化（outer→inner 序）",
                BoundDescribe.Body(BodyOf(bodies4, ".proxy.0.doSomething")),
                "Body(.proxy.0.doSomething, [], [Return(InstCall(.proxy.1.doSomething, " +
                "This(Service), [Param(arg,i32)], String))])");
            TestHarness.Check("L1 wildcard 的 inner → 本环 shim",
                BoundDescribe.Body(BodyOf(bodies4, ".proxy.1.doSomething")),
                "Body(.proxy.1.doSomething, [symbol: String, namedArgs: Array<Any>, unnamedArgs: Array<Any>], " +
                "[Decl(symbol, String, = Str(\"Service$doSomething(arg:.i32)@.string\",String)); " +
                "Decl(namedArgs, Array<Any>, = KwArgs([])); " +
                "Decl(unnamedArgs, Array<Any>, = VarArgs([Param(arg,i32)])); " +
                "Return(InstCall(.proxy.unwrap.1.doSomething, This(Service), " +
                "[Local(namedArgs,Array<Any>), Local(unnamedArgs,Array<Any>)], String))])");
            TestHarness.Check("链末 shim → 原始体",
                BoundDescribe.Body(BodyOf(bodies4, ".proxy.unwrap.1.doSomething")),
                "Body(.proxy.unwrap.1.doSomething, [], " +
                "[Return(InstCall(.wrapped.doSomething, This(Service), " +
                "[Cast(Index(Param(unnamedArgs,Array<Any>), Int(0,i32), Any), i32)], String))])");

            // E. get 访问器链：转发壳（getter 符号）/ 原始体（自动访问器
            // 合成体改挂 .wrapped.get.）/ 特化体（value 前奏物化 = invoke
            // 下一环，proxy 体 return value 直通物化局部）
            var (unit5, bodies5) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.name\\<TField>(value: TField): TField { return value }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub var name: String { get }\n" +
                "    pub init(_ -> name)\n" +
                "}\n");
            CheckNoErrors("get 访问器链无诊断", unit5);
            TestHarness.Check("getter 转发壳 body（invoke get 链首）",
                BoundDescribe.Body(BodyOf(bodies5, "name")),
                "Body(name, [], [Return(InstCall(.proxy.0.get.name, This(Service), [], String))])");
            TestHarness.Check("getter 原始体 body（自动访问器合成体改挂）",
                BoundDescribe.Body(BodyOf(bodies5, ".wrapped.get.name")),
                "Body(.wrapped.get.name, [], [Return(InstField(name, This(Service), String))])");
            TestHarness.Check("getter 特化体（value = invoke 下一环物化）",
                BoundDescribe.Body(BodyOf(bodies5, ".proxy.0.get.name")),
                "Body(.proxy.0.get.name, [value: String], " +
                "[Decl(value, String, = InstCall(.wrapped.get.name, This(Service), [], String)); " +
                "Return(Local(value,String))])");

            // F. 负例
            // 非 proxy 语境的 self/inner（ARCH §5.2：编译错误）
            var (unit6, _) = BindUnit(
                "pub func f(s: Service): i32 {\n" +
                "    var a = self\n" +
                "    return inner(1)\n" +
                "}\n" +
                "pub class Service { pub init() }\n");
            TestHarness.CheckSemanticError("非 proxy 语境 self", unit6.Diagnostics,
                "'self' is only available in a wrapper proxy body");
            TestHarness.CheckSemanticError("非 proxy 语境 inner", unit6.Diagnostics,
                "'inner' is only available in a wrapper proxy body");
            // 零泛型 wrapper 的 proxy 体内 self（§14.2 末条）
            var (unit7, _) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Plain {\n" +
                "    operator .proxy.ping(): i32 {\n" +
                "        var s = self\n" +
                "        return 0\n" +
                "    }\n" +
                "}\n" +
                "@Plain\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(): i32 { return 1 }\n" +
                "}\n");
            TestHarness.CheckSemanticError("零泛型 wrapper 的 self", unit7.Diagnostics,
                "'self' is not available here");
            // proxy 体内裸 this 取值（只读禁令，§14.5）
            var (unit8, _) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(): String { return this }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(): String { return \"p\" }\n" +
                "}\n");
            TestHarness.CheckSemanticError("proxy 体内裸 this 取值", unit8.Diagnostics,
                "Wrapper place 'this' cannot be used as a value");
            // inner 实参与下一环签名不符（specific 全等形状）
            var (unit9, _) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    pub init()\n" +
                "    operator .proxy.doSomething(arg: i32): String { return inner(\"s\") }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func doSomething(arg: i32): String { return \"x\" }\n" +
                "}\n");
            TestHarness.CheckSemanticError("inner 实参类型不符", unit9.Diagnostics,
                "Cannot pass 'String' as 'i32'");
            // 诊断去重：同一 proxy 声明命中两成员（两组合绑定同一声明体），
            // 体内同一错误按 (proxy, span, message) 只报一次
            var (unit10, _) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audited {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return missingFn() }\n" +
                "}\n" +
                "@Audited\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func a(): i32 { return 1 }\n" +
                "    pub func b(): i32 { return 2 }\n" +
                "}\n");
            TestHarness.CheckTrue("proxy 体诊断跨组合去重（同声明同位置同消息一次）",
                unit10.Diagnostics.Diagnostics.Count(d =>
                    d.Message.Contains("Undefined function: 'missingFn'")) == 1,
                string.Join("; ", unit10.Diagnostics.Diagnostics.Select(d => d.Message)));

            // G. P4 开闸（S11d）：合成 fn（特化/原始体/shim）与转发壳
            // 全量 lowering 产物、零诊断（发射端到端见 BilEmitterTests）
            var (unit11, bodies11) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    pub init()\n" +
                "    operator .proxy.doSomething(arg: i32): String { return inner(arg) }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func doSomething(arg: i32): String { return \"x\" }\n" +
                "}\n" +
                "pub func caller(s: Service): String { return s.doSomething(1) }\n");
            CheckNoErrors("P3 全链无诊断", unit11);
            var lowered11 = Lowerer.Lower(unit11, bodies11);
            CheckNoErrors("P4 全链降级无诊断", unit11);
            TestHarness.CheckTrue("P4 开闸：转发壳/原始体/特化体三件套产物齐备",
                lowered11.Any(b => b.Method.Name == "doSomething")
                && lowered11.Any(b => b.Method.Name == ".wrapped.doSomething")
                && lowered11.Any(b => b.Method.Name == ".proxy.0.doSomething")
                && lowered11.Any(b => b.Method.Name == "caller"),
                string.Join("; ", lowered11.Select(b => b.Method.Name)));
            TestHarness.Check("转发壳降级形态（invoke 链首）",
                "Body(doSomething, [], [Return(InstCall(.proxy.0.doSomething, " +
                "This(Service), [Param(arg,i32)], String))])",
                LoweredDescribe.Body(lowered11.Single(b => b.Method.Name == "doSomething")));
            TestHarness.Check("特化体降级形态（inner = 链末原始体调用）",
                "Body(.proxy.0.doSomething, [], [Return(InstCall(.wrapped.doSomething, " +
                "This(Service), [Param(arg,i32)], String))])",
                LoweredDescribe.Body(lowered11.Single(b => b.Method.Name == ".proxy.0.doSomething")));
        }
    }
}
