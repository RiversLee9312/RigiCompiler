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
                "[Assign(Embedded(Param(s,Service), [Logged], level, String), " +
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
                "[Return(Embedded(Param(hero,Hero), [Clamped], min, i32))])",
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
                "local/static wrapper place storage is not supported yet");
        }

        // ===== S11b proxy 体逐组合绑定：转发壳/特化体/解包 shim 三件套 +
        // self/inner/this 上色 + 负例与诊断去重 =====
        // M88：proxy 模板态绑定冒烟（烘焙体合成已删，完整用例归 M88b-3）
        private static void TestProxyBodyBinding()
        {
            TestHarness.Section("P3 Proxy Template Binding (M88)");
            var (unit, bodies) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    operator .proxy.doSomething(arg: i32): String {\n" +
                "        return inner(arg)\n" +
                "    }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub func doSomething(arg: i32): String { return \"x\" }\n" +
                "}\n");
            CheckNoErrors("proxy 模板绑定无诊断", unit);
            var proxyBody = bodies.FirstOrDefault(b => b.Method.Name == ".proxy.doSomething");
            TestHarness.CheckTrue("proxy 声明体已绑定", proxyBody != null);
            if (proxyBody != null)
            {
                TestHarness.CheckTrue("含 BoundInnerCall",
                    BoundDescribe.Body(proxyBody).Contains("InnerCall"));
            }
            // 非 proxy 语境 self 诊断
            var (unit2, _) = BindUnit(
                "pub func g(): i32 { return self }\n");
            TestHarness.CheckSemanticError("非 proxy 语境 self 诊断",
                unit2.Diagnostics, "'self' is only available");
        }

        // ===== #27⑧ proxy 声明泛型参数体内类型引用（模板态天然可解析）=====
        private static void TestProxyGenericParamTypeRefs()
        {
            TestHarness.Section("P3 Proxy Generic Param Type Refs (#27⑧)");

            // get proxy：TField 局部声明 / as / is / return
            var (unit, bodies) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    operator .proxy.get.name\\<TField>(value: TField): TField {\n" +
                "        var x: TField = value\n" +
                "        var casted = x as TField\n" +
                "        var ok = x is TField\n" +
                "        return casted\n" +
                "    }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub var name: String { get }\n" +
                "    pub init(_ -> name)\n" +
                "}\n");
            CheckNoErrors("proxy TField 体内类型引用无诊断", unit);
            var getBody = bodies.First(b => b.Method.Name == ".proxy.get.name");
            var getDesc = BoundDescribe.Body(getBody);
            TestHarness.CheckTrue("TField 局部声明",
                getDesc.Contains("Local(x,TField)") || getDesc.Contains("Decl(x, TField"));
            TestHarness.CheckTrue("as TField", getDesc.Contains("Cast(") && getDesc.Contains("TField"));
            TestHarness.CheckTrue("is TField", getDesc.Contains("Is(") && getDesc.Contains("TField"));
            TestHarness.CheckTrue("return TField", getDesc.Contains("Return("));

            // wildcard：TReturn 局部 + return
            var (unit2, bodies2) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audited {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...): TReturn {\n" +
                "        var r: TReturn = inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs)\n" +
                "        return r\n" +
                "    }\n" +
                "}\n" +
                "@Audited\n" +
                "pub class Service { pub func known(): i32 { return 1 } }\n");
            CheckNoErrors("proxy TReturn 体内类型引用无诊断", unit2);
            var wildBody = bodies2.First(b => b.Method.Name == ".proxy.*");
            TestHarness.CheckTrue("TReturn 局部声明",
                BoundDescribe.Body(wildBody).Contains("Local(r,")
                && BoundDescribe.Body(wildBody).Contains("TReturn"));
        }

        // ===== M79 遗留：param:W（泛型参数 receiver + with 约束）=====
        private static void TestGenericParamWithWrapperPlace()
        {
            TestHarness.Section("P3 Generic Param with-Constraint Wrapper Place (M79)");

            const string fixture =
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "    pub func dump(): String { return level }\n" +
                "}\n";

            // 成员读
            var (unit, bodies) = BindUnit(fixture +
                "pub func f\\<T with Logged>(param: T): String {\n" +
                "    return param:Logged.level\n" +
                "}\n");
            CheckNoErrors("param:W 字段读无诊断", unit);
            TestHarness.Check("param:W 字段读形态",
                "InstField(level, WrapperPlace(Param(param,T), Logged), String)",
                BoundDescribe.Expr(
                    ((BoundReturnStatement)((BoundBlock)BodyOf(bodies, "f").Body).Statements[0])
                    .Value));
            var place = ((BoundFieldAccessExpression)((BoundReturnStatement)
                ((BoundBlock)BodyOf(bodies, "f").Body).Statements[0]).Value!).Receiver
                as BoundWrapperAccessExpression;
            TestHarness.CheckTrue("合成 Application（Syntax 空）",
                place != null && place.Application.Syntax == null
                && place.Application.Wrapper.Name == "Logged");

            // 成员写
            var (unit2, bodies2) = BindUnit(fixture +
                "pub func g\\<T with Logged>(param: T) {\n" +
                "    param:Logged.level = \"TRACE\"\n" +
                "}\n");
            CheckNoErrors("param:W 字段写无诊断", unit2);
            var assign2 = (BoundAssignmentStatement)((BoundBlock)BodyOf(bodies2, "g").Body)
                .Statements[0];
            TestHarness.Check("param:W 字段写形态",
                "InstField(level, WrapperPlace(Param(param,T), Logged), String)",
                BoundDescribe.Expr(assign2.Target));

            // 方法调用
            var (unit3, bodies3) = BindUnit(fixture +
                "pub func h\\<T with Logged>(param: T): String {\n" +
                "    return param:Logged.dump()\n" +
                "}\n");
            CheckNoErrors("param:W 方法调用无诊断", unit3);
            TestHarness.Check("param:W 方法调用形态",
                "InstCall(dump, WrapperPlace(Param(param,T), Logged), [], String)",
                BoundDescribe.Expr(
                    ((BoundReturnStatement)((BoundBlock)BodyOf(bodies3, "h").Body).Statements[0])
                    .Value));

            // 只读禁令：链末取值
            var (unit4, _) = BindUnit(fixture +
                "pub func r\\<T with Logged>(param: T): Logged {\n" +
                "    return param:Logged\n" +
                "}\n");
            TestHarness.CheckSemanticError("param:W 只读禁令（取值）", unit4.Diagnostics,
                "Wrapper place ':Logged' cannot be used as a value");

            // 只读禁令：链末赋值
            var (unit5, _) = BindUnit(fixture +
                "pub func a\\<T with Logged>(param: T) {\n" +
                "    param:Logged = \"x\"\n" +
                "}\n");
            TestHarness.CheckSemanticError("param:W 只读禁令（赋值）", unit5.Diagnostics,
                "Cannot assign to wrapper place ':Logged'");

            // 无 with 约束负例
            var (unit6, _) = BindUnit(fixture +
                "pub func n\\<T>(param: T): String {\n" +
                "    return param:Logged.level\n" +
                "}\n");
            TestHarness.CheckSemanticError("无 with 约束负例", unit6.Diagnostics,
                "has no wrapper 'Logged' applied");

            // 嵌套链：T with Outer，Outer 挂 Inner
            var (unit7, bodies7) = BindUnit(
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
                "pub func nest\\<T with Outer>(param: T): String {\n" +
                "    return param:Outer:Inner.tag\n" +
                "}\n");
            CheckNoErrors("param:W 嵌套链无诊断", unit7);
            TestHarness.Check("param:W 嵌套链形态",
                "InstField(tag, WrapperPlace(WrapperPlace(Param(param,T), Outer), Inner), String)",
                BoundDescribe.Expr(
                    ((BoundReturnStatement)((BoundBlock)BodyOf(bodies7, "nest").Body).Statements[0])
                    .Value));

            // P4a 降级：get.wrapper 值拷贝
            var (unit8, bodies8) = BindUnit(fixture +
                "pub func low\\<T with Logged>(param: T): String {\n" +
                "    return param:Logged.level\n" +
                "}\n");
            CheckNoErrors("param:W P3 无诊断", unit8);
            var lowered8 = Lowerer.Lower(unit8, bodies8);
            CheckNoErrors("param:W P4 降级无诊断", unit8);
            TestHarness.Check("param:W 字段读降级（get.wrapper）",
                "Body(low, [.s0: Logged], " +
                "[Assign(Local(.s0,Logged), GetWrapper(Param(param,T), Logged)); " +
                "Return(InstField(level, Local(.s0,Logged), String))])",
                LoweredDescribe.Body(lowered8.Single(b => b.Method.Name == "low")));

            // P4a 写：embedded
            var (unit9, bodies9) = BindUnit(fixture +
                "pub func wlow\\<T with Logged>(param: T) {\n" +
                "    param:Logged.level = \"X\"\n" +
                "}\n");
            CheckNoErrors("param:W 写 P3 无诊断", unit9);
            var lowered9 = Lowerer.Lower(unit9, bodies9);
            CheckNoErrors("param:W 写 P4 无诊断", unit9);
            TestHarness.Check("param:W 字段写降级（embedded）",
                "Body(wlow, [], " +
                "[Assign(Embedded(Param(param,T), [Logged], level, String), " +
                "Str(\"X\",String))])",
                LoweredDescribe.Body(lowered9.Single(b => b.Method.Name == "wlow")));
        }

        // M88：降级调用点 → Any.call???（完整用例归 M88b-3）
        private static void TestDowngradeBinding()
        {
            TestHarness.Section("P3 Downgrade Call (M88)");
            const string fixture =
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audited {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...): TReturn {\n" +
                "        return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Audited\n" +
                "pub class Service { pub func known(): i32 { return 1 } }\n";
            var (unit, bodies) = BindUnit(fixture +
                "pub func f(service: Service) {\n" +
                "    service.fetch(42)\n" +
                "}\n");
            CheckNoErrors("降级调用无诊断", unit);
            var f = bodies.Single(b => b.Method.Name == "f");
            TestHarness.CheckTrue("调用 Any.call???",
                BoundDescribe.Body(f).Contains("call???"));
            // 无链时保持 Undefined member
            var (unit2, _) = BindUnit(
                "pub class Plain { }\n" +
                "pub func g(p: Plain) { p.missing() }\n");
            TestHarness.CheckSemanticError("无 wrapper 不降级",
                unit2.Diagnostics, "Undefined member");
        }
    }
}
