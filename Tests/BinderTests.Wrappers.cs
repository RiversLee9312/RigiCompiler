using System.Linq;

namespace LatteCompiler.Tests
{
    // BinderTests 的 S11 wrapper place 部分（SYNTAX §14.1/§14.5）：
    // `obj:W` 只读 place 绑定（Entity：宿主类型 AppliedWrappers；Value：
    // 字段/局部符号 AppliedWrappers——局部应用由 P3 登记，矩阵 C 恒合法）
    // 与只读禁令全拦截面（链末 Colon 段按赋值/取值诊断——变量初始化/
    // 实参/返回值/推断源/运算与类型检查操作数全经路径绑定结果一处收口）。
    // P4 发射归 proxy 烘焙（S11 后续），本组断言到 P3 为止。

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

        // ===== P4 归口：wrapper place 的 lowering 归 proxy 烘焙（S11 后续）=====
        private static void TestWrapperPlaceLoweringGate()
        {
            TestHarness.Section("P4 Wrapper Place Gate");

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
            TestHarness.CheckSemanticError("P4 wrapper place 归口", unit.Diagnostics,
                "P4: wrapper place lowering is not supported yet (S11)");
            TestHarness.CheckTrue("归口函数体跳过（f 无 LoweredFunctionBody 产出）",
                lowered.All(b => b.Method.Name != "f"));
        }
    }
}
