using System.Linq;

namespace RigiCompiler.Tests
{
    // BinderTests 的 S11 wrapper place 部分（SYNTAX §14.1/§14.5）：
    // `obj:W` 只读 place 绑定（Entity：宿主类型 AppliedWrappers；Value：
    // 字段/局部符号 AppliedWrappers——局部应用由 P3 登记，矩阵 C 恒合法）
    // 与只读禁令全拦截面（链末 Colon 段按赋值/取值诊断——变量初始化/
    // 实参/返回值/推断源/运算与类型检查操作数全经路径绑定结果一处收口）。
    // P4a 降级（S11c）：Entity = get.wrapper 值拷贝 + get.field /
    // set.wrapper.field；字段-Value 读 = get.wrapper.field + get.field、
    // 写 = set.wrapper.field 链；局部/静态存储归口（栈帧/静态存储合成归后续里程碑）。

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
                "    pub init() { level = \"INFO\" }\n" +
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
                "    pub init() { level = \"INFO\" }\n" +
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
                "    pub init() { level = \"INFO\" }\n" +
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
                "    pub init() { tag = \"x\" }\n" +
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
                "    pub init() { level = \"INFO\" }\n" +
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

            // 局部变量 Value wrapper（P3 登记 + cell 存储合成，需 stdlib Cell 族）
            var (unit6, bodies6) = BindUnitWithStdlib(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init() { min = 0 }\n" +
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
            TestHarness.CheckTrue("局部 Value wrapper 合成 Cell 存储",
                health6.CellStorage is { IsReadOnly: false, CellClass.Name: var cn }
                && cn.StartsWith("..cell..", System.StringComparison.Ordinal)
                && ReferenceEquals(health6.CellStorage.ValueField.AppliedWrappers[0],
                    health6.AppliedWrappers[0]));

            // 静态字段 Value wrapper（shared wrapper × 静态目标，§14.9 矩阵）
            var (unit7, bodies7) = BindUnitWithStdlib(
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper SClamp {\n" +
                "    pub var min: i32\n" +
                "    pub init() { min = 0 }\n" +
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
            var counter7 = unit7.Symbols.GlobalNamespace.Types
                .First(t => t.Name == "Holder").Fields.First(f => f.Name == "counter");
            TestHarness.CheckTrue("静态字段 Value wrapper 合成 Cell 存储",
                counter7.CellStorage is { IsReadOnly: false, CellClass.Name: var scn }
                && scn.StartsWith("..cell..", System.StringComparison.Ordinal));

            // wrapper place 上的索引后缀（成员访问扩展：getAtIndex operator）
            var (unit8, bodies8) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Indexed {\n" +
                "    pub var store: i32\n" +
                "    pub init() { store = 0 }\n" +
                "    pub operator getAtIndex(index: i32): i32? { return store }\n" +
                "}\n" +
                "@Indexed\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service): i32? {\n" +
                "    return s:Indexed[0]\n" +
                "}\n");
            CheckNoErrors("wrapper place 索引后缀无诊断", unit8);
            TestHarness.Check("wrapper place 索引后缀形态（Q6：Type = i32?）",
                "Index(WrapperPlace(Param(s,Service), Indexed), Int(0,i32), i32?)",
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
                "    pub init() { level = \"INFO\" }\n" +
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

        // ===== M109b-2：静态 Method wrapper companion 语义 =====
        private static void TestStaticMethodCompanionBinding()
        {
            TestHarness.Section("P3 Static Method Companion (M109b-2)");

            var (unit, bodies) = BindUnitWithStdlib(
                "@WrapperTarget(.Method)\n" +
                "pub shared wrapper Timed { pub init() }\n" +
                "pub class Math {\n" +
                "    @Timed\n" +
                "    pub static func square(x: i32): i32 { return (x * x) }\n" +
                "}\n" +
                "pub func f(n: i32): i32 { return Math.square(n) }\n");
            CheckNoErrors("静态 Method companion 绑定无诊断", unit);

            var shell = bodies.Select(b => b.Method)
                .First(m => m.Name == "square" && m.IsStatic);
            TestHarness.CheckTrue("原静态方法有 Companion 槽", shell.Companion != null);
            var info = shell.Companion!;
            TestHarness.CheckTrue("companion 类型名 ..companion（无 UUID）",
                info.CompanionType.Name == "..companion"
                && info.CompanionType.IsSingleton
                && info.CompanionType.IsShared);
            TestHarness.CheckTrue("实例方法承接 wrapper 应用",
                info.InstanceMethod.AppliedWrappers.Count == 1
                && !info.InstanceMethod.IsStatic
                && info.InstanceMethod.IsCompanionInstance);
            TestHarness.CheckTrue("壳体不再挂 wrapper", shell.AppliedWrappers.Count == 0);

            var shellBody = bodies.First(b => ReferenceEquals(b.Method, shell));
            TestHarness.CheckTrue("壳体体为 return(invoke companion)",
                shellBody.Body.Statements.Count == 1
                && shellBody.Body.Statements[0] is BoundReturnStatement
                {
                    Value: BoundInstanceCallExpression
                    {
                        Method: { IsCompanionInstance: true },
                        Receiver: BoundNewExpression
                    }
                });

            var instanceBody = bodies.First(b =>
                ReferenceEquals(b.Method, info.InstanceMethod));
            TestHarness.CheckTrue("companion 实例方法体含用户 return",
                instanceBody.Body.Statements.Any(s => s is BoundReturnStatement));

            // 调用点无感：f 体仍是对 Math.square 的静态调用
            var fBody = bodies.First(b => b.Method.Name == "f");
            TestHarness.CheckTrue("调用点仍绑壳体静态方法",
                fBody.Body.Statements.OfType<BoundReturnStatement>()
                    .Any(r => r.Value is BoundCallExpression c
                        && c.Method.IsStatic && c.Method.Name == "square"));
        }

        // ===== M109b-1：wrapper init 实参绑定诊断 =====
        private static void TestWrapperInitArgBinding()
        {
            TestHarness.Section("P3 Wrapper Init Arg Binding (M109b-1)");

            // 类型级：实参类型不匹配
            var (unit, _) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "}\n" +
                "@Logged(42)\n" +
                "pub class Service { pub init() }\n");
            TestHarness.CheckSemanticError("类型级 wrapper init 实参类型不匹配", unit.Diagnostics,
                "level");

            // 类型级：缺实参
            var (unit2, _) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n");
            TestHarness.CheckSemanticError("类型级 wrapper init 缺实参", unit2.Diagnostics,
                "Missing argument");

            // 局部 cell：实参类型不匹配
            var (unit3, _) = BindUnit(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init(_ -> min)\n" +
                "}\n" +
                "pub func f() {\n" +
                "    @Clamped(\"nope\")\n" +
                "    var x: i32 = 1\n" +
                "}\n");
            TestHarness.CheckSemanticError("局部 wrapper init 实参类型不匹配", unit3.Diagnostics,
                "min");

            // 正例：局部 args 引用外层参数（cell 需 stdlib）
            var (unit4, bodies) = BindUnitWithStdlib(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init(_ -> min)\n" +
                "}\n" +
                "pub func f(lo: i32): i32 {\n" +
                "    @Clamped(lo)\n" +
                "    const health: i32 = 50\n" +
                "    return health\n" +
                "}\n");
            CheckNoErrors("局部 wrapper args 引用外层参数无诊断", unit4);
            var fBody = bodies.First(b => b.Method.Name == "f");
            var health = fBody.Locals.First(l => l.Name == "health");
            TestHarness.CheckTrue("局部 AppliedWrappers 已绑定 init 实参",
                health.AppliedWrappers.Count == 1
                && health.AppliedWrappers[0].BoundInitArguments is { Count: 1 });
            TestHarness.CheckTrue("cell ..init.wrapper 有参",
                health.CellStorage?.InitWrapper is { Parameters.Count: 1 });
        }

        // ===== §14.3 只读适用性（P3 栈上变量应用点）：var + 只实现 get 的
        // Value wrapper 即编译错误（检查前移，不再推迟到 VM 且诊断不带合成
        // 符号）；const + get-only 合法 =====
        private static void TestValueWrapperGetOnlyLocal()
        {
            TestHarness.Section("P3 Value Wrapper Get-Only (§14.3)");

            const string getOnly =
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Doubled {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        return (((value as i32) * 2) as TValue)\n" +
                "    }\n" +
                "}\n";

            // var + get-only wrapper：编译错误（规范字面「只适用于只读变量」——
            // 无需等到写入发生）
            var (unit, _) = BindUnitWithStdlib(getOnly +
                "pub func main(): i32 {\n" +
                "    @Doubled()\n" +
                "    var x: i32 = 10\n" +
                "    x = 20\n" +
                "    return x\n" +
                "}\n");
            TestHarness.CheckSemanticError("var + get-only wrapper 编译错误", unit.Diagnostics,
                "Value wrapper 'Doubled' does not implement .proxy.set");
            // 诊断不得泄漏合成符号（..cell..UUID 归编译器内部）
            TestHarness.CheckTrue("诊断不含 cell 合成符号",
                unit.Diagnostics.Diagnostics.All(d => !d.Message.Contains("..cell..")),
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => d.Message)));

            // const + get-only wrapper：合法（只读变量）
            var (unit2, _) = BindUnitWithStdlib(getOnly +
                "pub func main(): i32 {\n" +
                "    @Doubled()\n" +
                "    const x: i32 = 10\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("const + get-only wrapper 合法", unit2);

            // 不带任何 proxy 的纯状态修饰器：var 合法（无拦截链，不受
            // 只读适用性约束）
            var (unit3, _) = BindUnitWithStdlib(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Tag {\n" +
                "    pub var label: i32\n" +
                "    pub init() { label = 0 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @Tag()\n" +
                "    var x: i32 = 10\n" +
                "    x = 20\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("纯状态修饰器 var 合法", unit3);
        }

        // ===== wrapper place 绑定负例（查找/宿主/登记检查）=====
        private static void TestWrapperPlaceErrors()
        {
            TestHarness.Section("P3 Wrapper Place Errors");

            const string fixture =
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init() { level = \"INFO\" }\n" +
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
                "    pub init() { min = 0 }\n" +
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
                "    pub init() { min = 0 }\n" +
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
                "    pub init() { level = \"INFO\" }\n" +
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

            // Entity 字段写：set.wrapper.field 链（§13.3）
            var (unit2, bodies2) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init() { level = \"INFO\" }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service) {\n" +
                "    s:Logged.level = \"TRACE\"\n" +
                "}\n");
            CheckNoErrors("P3 写字段绑定无诊断", unit2);
            var lowered2 = Lowerer.Lower(unit2, bodies2);
            CheckNoErrors("P4 写字段降级无诊断", unit2);
            TestHarness.Check("Entity 字段写降级形态（set.wrapper.field place）",
                "Body(f, [], " +
                "[Assign(WrapperField(Param(s,Service), [Logged], level, String), " +
                "Str(\"TRACE\",String))])",
                LoweredDescribe.Body(lowered2.Single(b => b.Method.Name == "f")));

            // 字段-Value 应用字段读：get.wrapper.field 值拷贝 + 普通 get.field
            var (unit3, bodies3) = BindUnit(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init() { min = 0 }\n" +
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
            TestHarness.Check("字段-Value 字段读降级形态（GetFieldWrapper + InstField）",
                "Body(f, [.s0: Clamped], " +
                "[Assign(Local(.s0,Clamped), GetFieldWrapper(Param(hero,Hero), hp, Clamped)); " +
                "Return(InstField(min, Local(.s0,Clamped), i32))])",
                LoweredDescribe.Body(lowered3.Single(b => b.Method.Name == "f")));

            // 局部 wrapper place 正例：cell 宿主 + GetFieldWrapper(value)/WrapperField 链
            var (unit4, bodies4) = BindUnitWithStdlib(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init() { min = 0 }\n" +
                "}\n" +
                "pub func f(): i32 {\n" +
                "    @Clamped\n" +
                "    var health: i32 = 50\n" +
                "    health:Clamped.min = 10\n" +
                "    return health:Clamped.min\n" +
                "}\n");
            CheckNoErrors("P3 局部 place 绑定无诊断", unit4);
            var lowered4 = Lowerer.Lower(unit4, bodies4);
            CheckNoErrors("P4 局部 wrapper place 降级无诊断", unit4);
            var localPlaceDesc = BilTestHarness.NormalizeLambdaUuids(
                LoweredDescribe.Body(lowered4.Single(b => b.Method.Name == "f")));
            TestHarness.Check("局部 wrapper place 降级形态（cell 构造 + 读写）",
                "Body(f, [health: i32, .s0: Clamped], [" +
                "Decl(health, i32, = New(..cell..UUID, init, [Int(50,i32)])); " +
                "Assign(WrapperField(CellRef(health,..cell..UUID), [value > Clamped], min, i32), " +
                "Int(10,i32)); " +
                "Assign(Local(.s0,Clamped), GetFieldWrapper(CellRef(health,..cell..UUID), value, Clamped)); " +
                "Return(InstField(min, Local(.s0,Clamped), i32))])",
                localPlaceDesc);

            // wrapped const 局部：ReadonlyCell 子类（无 setValue、value 为 const）
            var (unitConst, bodiesConst) = BindUnitWithStdlib(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init() { min = 0 }\n" +
                "}\n" +
                "pub func f(): i32 {\n" +
                "    @Clamped\n" +
                "    const health: i32 = 50\n" +
                "    return health:Clamped.min\n" +
                "}\n");
            CheckNoErrors("P3 wrapped const 局部无诊断", unitConst);
            var healthConst = BodyOf(bodiesConst, "f").Locals.First(l => l.Name == "health");
            TestHarness.CheckTrue("const 局部 CellStorage 只读",
                healthConst.CellStorage is { IsReadOnly: true }
                && healthConst.CellStorage.CellClass.BaseType is
                    { ConstructedFrom: { Name: "ReadonlyCell" } });
            var loweredConst = Lowerer.Lower(unitConst, bodiesConst);
            CheckNoErrors("P4 wrapped const 局部无诊断", unitConst);
            TestHarness.CheckTrue("const 局部 place 读走 GetFieldWrapper(CellRef)",
                BilTestHarness.NormalizeLambdaUuids(LoweredDescribe.Body(
                    loweredConst.Single(b => b.Method.Name == "f")))
                    .Contains("GetFieldWrapper(CellRef(health,..cell..UUID), value, Clamped)"));

            // wrapped 局部被 lambda 捕获：不套第二层 cell，.capture 字段类型 = 已有 cell 子类
            var (unitCap, bodiesCap) = BindUnitWithStdlib(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init() { min = 0 }\n" +
                "}\n" +
                "pub func f(): i32 {\n" +
                "    @Clamped\n" +
                "    var health: i32 = 50\n" +
                "    var g = func{(): i32 -> health}\n" +
                "    health = 60\n" +
                "    return g()\n" +
                "}\n");
            CheckNoErrors("P3 wrapped 局部 lambda 捕获无诊断", unitCap);
            var healthCap = BodyOf(bodiesCap, "f").Locals.First(l => l.Name == "health");
            TestHarness.CheckTrue("捕获前已有单一 CellStorage",
                healthCap.CellStorage != null
                && healthCap.CellStorage.CellClass.Name.StartsWith("..cell..",
                    System.StringComparison.Ordinal));
            var loweredCap = Lowerer.Lower(unitCap, bodiesCap);
            CheckNoErrors("P4 wrapped 局部 lambda 捕获无诊断", unitCap);
            var capDesc = BilTestHarness.NormalizeLambdaUuids(
                LoweredDescribe.Body(loweredCap.Single(b => b.Method.Name == "f")));
            TestHarness.CheckTrue("捕获构造传 CellRef（不套第二层 cell）",
                capDesc.Contains("CellRef(health,..cell..UUID)")
                && capDesc.Contains("InstCallStmt(setValue, CellRef(health,..cell..UUID)"));

            // 局部 wrapper 复合赋值 + 深写
            var (unitComp, bodiesComp) = BindUnitWithStdlib(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init() { min = 0 }\n" +
                "}\n" +
                "pub struct Inner { pub var x: i32\n pub init(v: i32) { x = v } }\n" +
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Boxed {\n" +
                "    pub var sub: Inner\n" +
                "    pub init() { sub = new Inner(0) }\n" +
                "}\n" +
                "pub func f(): i32 {\n" +
                "    @Clamped\n" +
                "    var health: i32 = 50\n" +
                "    health:Clamped.min += 1\n" +
                "    return health:Clamped.min\n" +
                "}\n" +
                "pub func deep(): i32 {\n" +
                "    @Boxed\n" +
                "    var slot: i32 = 0\n" +
                "    slot:Boxed.sub.x = 3\n" +
                "    return slot:Boxed.sub.x\n" +
                "}\n");
            CheckNoErrors("P3 局部 wrapper 复合/深写无诊断", unitComp);
            var loweredComp = Lowerer.Lower(unitComp, bodiesComp);
            CheckNoErrors("P4 局部 wrapper 复合/深写无诊断", unitComp);
            var compDesc = BilTestHarness.NormalizeLambdaUuids(
                LoweredDescribe.Body(loweredComp.Single(b => b.Method.Name == "f")));
            TestHarness.CheckTrue("复合赋值读 GetFieldWrapper + 写 WrapperField",
                compDesc.Contains("GetFieldWrapper(CellRef(health,..cell..UUID), value, Clamped)")
                && compDesc.Contains(
                    "WrapperField(CellRef(health,..cell..UUID), [value > Clamped], min, i32)"));
            var deepDescLocal = BilTestHarness.NormalizeLambdaUuids(
                LoweredDescribe.Body(loweredComp.Single(b => b.Method.Name == "deep")));
            TestHarness.CheckTrue("局部 wrapper 深写含叶 set + WrapperField 写回",
                deepDescLocal.Contains("InstField(x,")
                && deepDescLocal.Contains("WrapperField(CellRef(slot,..cell..UUID)")
                && deepDescLocal.Contains("sub"));

            // 静态字段普通读写（非 place）：getValue/setValue
            var (unitStaticRw, bodiesStaticRw) = BindUnitWithStdlib(
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper SClamp {\n" +
                "    pub var min: i32\n" +
                "    pub init() { min = 0 }\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    @SClamp\n" +
                "    pub static var counter: i32 = 0\n" +
                "}\n" +
                "pub func g(): i32 {\n" +
                "    Holder.counter = 7\n" +
                "    return Holder.counter\n" +
                "}\n");
            CheckNoErrors("P3 静态字段普通读写无诊断", unitStaticRw);
            var loweredStaticRw = Lowerer.Lower(unitStaticRw, bodiesStaticRw);
            CheckNoErrors("P4 静态字段普通读写无诊断", unitStaticRw);
            TestHarness.Check("静态字段普通读写降级（setValue/getValue）",
                BilTestHarness.NormalizeLambdaUuids(
                    LoweredDescribe.Body(loweredStaticRw.Single(b => b.Method.Name == "g"))),
                "Body(g, [], [" +
                "InstCallStmt(setValue, InstField(counter, New(..companion, []), ..cell..UUID), " +
                "[Int(7,i32)]); " +
                "Return(InstCall(getValue, InstField(counter, New(..companion, []), ..cell..UUID), " +
                "[], i32))])");

            // 多 Value wrapper 分别 place（x:A / x:B；嵌套 x:A:B 需 Entity 应用）
            var (unitMulti, bodiesMulti) = BindUnitWithStdlib(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper A {\n" +
                "    pub var tag: String\n" +
                "    pub init() { tag = \"x\" }\n" +
                "}\n" +
                "@WrapperTarget(.Value)\n" +
                "pub wrapper B {\n" +
                "    pub var n: i32\n" +
                "    pub init() { n = 0 }\n" +
                "}\n" +
                "pub func f(): i32 {\n" +
                "    @A\n" +
                "    @B\n" +
                "    var x: i32 = 1\n" +
                "    return x:B.n\n" +
                "}\n" +
                "pub func g(): String {\n" +
                "    @A\n" +
                "    @B\n" +
                "    var x: i32 = 1\n" +
                "    return x:A.tag\n" +
                "}\n");
            CheckNoErrors("P3 多 Value wrapper 分别 place 无诊断", unitMulti);
            TestHarness.Check("x:B 形态",
                "InstField(n, WrapperPlace(Local(x,i32), B), i32)",
                BoundDescribe.Expr(
                    ((BoundReturnStatement)((BoundBlock)BodyOf(bodiesMulti, "f").Body)
                        .Statements[1]).Value));
            TestHarness.Check("x:A 形态",
                "InstField(tag, WrapperPlace(Local(x,i32), A), String)",
                BoundDescribe.Expr(
                    ((BoundReturnStatement)((BoundBlock)BodyOf(bodiesMulti, "g").Body)
                        .Statements[1]).Value));
            var xMulti = BodyOf(bodiesMulti, "f").Locals.First(l => l.Name == "x");
            TestHarness.CheckTrue("多 wrapper 共用单一 cell 存储",
                xMulti.AppliedWrappers.Count == 2 && xMulti.CellStorage != null);

            // M84：字段-Value 方法调用 → GetFieldWrapper 物化
            var (unit5, bodies5) = BindUnit(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub init() { min = 0 }\n" +
                "    pub func clamp(v: i32): i32 { return v }\n" +
                "}\n" +
                "pub class Hero {\n" +
                "    @Clamped\n" +
                "    pub var hp: i32\n" +
                "    pub init(h: i32) { hp = h }\n" +
                "}\n" +
                "pub func f(hero: Hero): i32 {\n" +
                "    return hero.hp:Clamped.clamp(1)\n" +
                "}\n");
            CheckNoErrors("P3 字段-Value 调用绑定无诊断", unit5);
            var lowered5 = Lowerer.Lower(unit5, bodies5);
            CheckNoErrors("P4 字段-Value 调用降级无诊断", unit5);
            TestHarness.Check("字段-Value 调用降级形态（GetFieldWrapper + InstCall）",
                "Body(f, [.s0: Clamped], " +
                "[Assign(Local(.s0,Clamped), GetFieldWrapper(Param(hero,Hero), hp, Clamped)); " +
                "Return(InstCall(clamp, Local(.s0,Clamped), [Int(1,i32)], i32))])",
                LoweredDescribe.Body(lowered5.Single(b => b.Method.Name == "f")));

            // M84：深层写穿一层值中间
            var (unit6, bodies6) = BindUnit(
                "pub struct Inner {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var sub: Inner\n" +
                "    pub init() { sub = new Inner(0) }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "pub func f(s: Service) {\n" +
                "    s:Logged.sub.x = 1\n" +
                "}\n");
            CheckNoErrors("P3 深写绑定无诊断", unit6);
            var lowered6 = Lowerer.Lower(unit6, bodies6);
            CheckNoErrors("P4 深写降级无诊断", unit6);
            var deepDesc = LoweredDescribe.Body(lowered6.Single(b => b.Method.Name == "f"));
            TestHarness.CheckTrue("深写含 GetWrapper", deepDesc.Contains("GetWrapper"));
            TestHarness.CheckTrue("深写含叶 set InstField x", deepDesc.Contains("InstField(x,"));
            TestHarness.CheckTrue("深写含 WrapperField 写回 sub",
                deepDesc.Contains("WrapperField(") && deepDesc.Contains("sub"));
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

            // 负例：wildcard 体内 inner 漏传保留首参 → P3 形状不匹配（全形状新规则）
            var (unit3, _) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@W\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "}\n");
            TestHarness.CheckSemanticError("wildcard inner 漏传 symbol 报形状不匹配",
                unit3.Diagnostics, "inner(...) arguments do not match proxy '.proxy.*' shape");
        }

        // ===== #27⑦ inner 泛型包透传：声明序锁定 + Bound/Lowered 描述 =====
        private static void TestInnerCallGenericPackForwarding()
        {
            TestHarness.Section("P3 Inner Call Generic Pack Forwarding (#27⑦)");
            var (unit, bodies) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audited {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@Audited\n" +
                "pub class Service {\n" +
                "    pub func fetch(id: i32): String { return \"r\" }\n" +
                "}\n");
            CheckNoErrors("wildcard 模板包透传无诊断", unit);
            var proxyBody = bodies.First(b => b.Method.Name == ".proxy.*");
            var boundDesc = BoundDescribe.Body(proxyBody);
            TestHarness.CheckTrue("Bound 含 packs 声明序",
                boundDesc.Contains("packs=[TNamedArgs, TUnnamedArgs]"));
            // 固定泛型 TReturn 不入 ForwardedGenericPacks
            TestHarness.CheckTrue("固定泛型 TReturn 不入 packs",
                !boundDesc.Contains("TReturn]") && !boundDesc.Contains("TReturn,"));
            var inner = FindFirstInnerCall(proxyBody.Body);
            TestHarness.CheckTrue("找到 BoundInnerCall", inner != null);
            if (inner != null)
            {
                TestHarness.CheckTrue("恰两包", inner.ForwardedGenericPacks.Count == 2);
                TestHarness.Check("包0=TNamedArgs", "TNamedArgs",
                    inner.ForwardedGenericPacks[0].Name);
                TestHarness.Check("包1=TUnnamedArgs", "TUnnamedArgs",
                    inner.ForwardedGenericPacks[1].Name);
                TestHarness.CheckTrue("具名包标记",
                    inner.ForwardedGenericPacks[0].IsNamedVariadic);
                TestHarness.CheckTrue("位置包标记",
                    inner.ForwardedGenericPacks[1].IsVariadic
                    && !inner.ForwardedGenericPacks[1].IsNamedVariadic);
            }
            var lowered = Lowerer.Lower(unit, bodies);
            CheckNoErrors("P4a 包透传无诊断", unit);
            var loweredProxy = lowered.First(b => b.Method.Name == ".proxy.*");
            TestHarness.CheckTrue("Lowered 含 packs 声明序",
                LoweredDescribe.Body(loweredProxy)
                    .Contains("packs=[TNamedArgs, TUnnamedArgs]"));
        }

        // 仅本套件：wildcard 模板体 return inner(...) 单层形态
        private static BoundInnerCallExpression? FindFirstInnerCall(BoundBlock body)
        {
            foreach (var stmt in body.Statements)
            {
                if (stmt is BoundReturnStatement { Value: BoundInnerCallExpression inner })
                    return inner;
                if (stmt is BoundExpressionStatement { Expression: BoundInnerCallExpression expr })
                    return expr;
            }
            return null;
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
                "        var r: TReturn = inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs)\n" +
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
                "    pub init() { level = \"INFO\" }\n" +
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
                "    pub init() { tag = \"x\" }\n" +
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

            // P4a 写：set.wrapper.field place
            var (unit9, bodies9) = BindUnit(fixture +
                "pub func wlow\\<T with Logged>(param: T) {\n" +
                "    param:Logged.level = \"X\"\n" +
                "}\n");
            CheckNoErrors("param:W 写 P3 无诊断", unit9);
            var lowered9 = Lowerer.Lower(unit9, bodies9);
            CheckNoErrors("param:W 写 P4 无诊断", unit9);
            TestHarness.Check("param:W 字段写降级（WrapperField place）",
                "Body(wlow, [], " +
                "[Assign(WrapperField(Param(param,T), [Logged], level, String), " +
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
                "        return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs)\n" +
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

            var (genericUnit, genericBodies) = BindUnit(fixture +
                "pub func fGeneric(service: Service): Any {\n" +
                "    return service.fetch\\<i32, String>(42)\n" +
                "}\n");
            CheckNoErrors("降级调用解析显式泛型无诊断", genericUnit);
            TestHarness.CheckTrue("降级 symbol 携带显式泛型",
                BoundDescribe.Body(genericBodies.Single(b => b.Method.Name == "fGeneric"))
                    .Contains("Service$fetch<.i32,.string>(.i32)@.any"));

            // 无链时保持 Undefined member
            var (unit2, _) = BindUnit(
                "pub class Plain { }\n" +
                "pub func g(p: Plain) { p.missing() }\n");
            TestHarness.CheckSemanticError("无 wrapper 不降级",
                unit2.Diagnostics, "Undefined member");

            // #28③：interface 应用 .proxy.* → 实现者显式重声明后调用可降级
            // （wrapper 传递闭包要求实现者声明处重复应用；资格查询仍沿闭包）
            const string wildcardW =
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audited {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...): TReturn {\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n";
            var (unitIface, bodiesIface) = BindUnit(wildcardW +
                "@Audited\n" +
                "pub interface IService { }\n" +
                "@Audited\n" +
                "pub class SvcImpl implements IService { pub init() }\n" +
                "pub func fIface(s: SvcImpl) { s.fetch(1) }\n");
            CheckNoErrors("#28③ 直接 implements 接口 wildcard 可降级", unitIface);
            TestHarness.CheckTrue("#28③ 实现者调用 Any.call???",
                BoundDescribe.Body(bodiesIface.Single(b => b.Method.Name == "fIface"))
                    .Contains("call???"));
            TestHarness.CheckTrue("#28③ 实现者显式登记 AppliedWrappers",
                unitIface.Symbols.GlobalNamespace.Types
                    .Single(t => t.Name == "SvcImpl").AppliedWrappers.Count == 1);

            // #28③：接口继承传递闭包（IChild : IBase，wrapper 在 IBase）
            var (unitTrans, bodiesTrans) = BindUnit(wildcardW +
                "@Audited\n" +
                "pub interface IBase { }\n" +
                "@Audited\n" +
                "pub interface IChild : IBase { }\n" +
                "@Audited\n" +
                "pub class ViaChild implements IChild { pub init() }\n" +
                "pub func fTrans(s: ViaChild) { s.remote() }\n");
            CheckNoErrors("#28③ 继承接口传递闭包可降级", unitTrans);
            TestHarness.CheckTrue("#28③ 传递闭包调用 Any.call???",
                BoundDescribe.Body(bodiesTrans.Single(b => b.Method.Name == "fTrans"))
                    .Contains("call???"));

            // #28③：仅 specific proxy（无 .proxy.*）不具降级资格
            var (unitSpec, _) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper SpecificOnly {\n" +
                "    operator .proxy.known(): i32 { return inner() }\n" +
                "}\n" +
                "@SpecificOnly\n" +
                "pub interface IKnown { func known(): i32 }\n" +
                "pub class SpecImpl implements IKnown {\n" +
                "    pub init()\n" +
                "    pub func known(): i32 { return 1 }\n" +
                "}\n" +
                "pub func gSpec(s: SpecImpl) { s.missing() }\n");
            TestHarness.CheckSemanticError("#28③ 仅 specific 不降级",
                unitSpec.Diagnostics, "Undefined member");

            // #28④：if? 右操作数 / throw 操作数 / 复合赋值 RHS / 索引写值
            // ——P3 豁免降级 Any（P4a cast 物化；普通 Any 不误伤）
            const string w =
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...): TReturn {\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@W\n" +
                "pub class Svc { pub init() }\n" +
                "pub class User { pub init() }\n";
            var (unitIf, _) = BindUnit(w +
                "pub func fIf(u: User?, s: Svc): User { return u if? s.fetch() }\n");
            CheckNoErrors("#28④ if? 右操作数降级豁免", unitIf);
            var (unitThrow, _) = BindUnit(w +
                "pub func fThrow(s: Svc) { throw s.err() }\n");
            CheckNoErrors("#28④ throw 操作数降级豁免", unitThrow);
            var (unitComp, _) = BindUnit(w +
                "pub func fComp(s: Svc): String {\n" +
                "    var t = \"a\"\n" +
                "    t += s.suffix()\n" +
                "    return t\n" +
                "}\n");
            CheckNoErrors("#28④ 复合赋值 RHS 降级豁免", unitComp);
            var (unitIdx, _) = BindUnit(w +
                "pub class Bag {\n" +
                "    pub var item: User\n" +
                "    pub init(_ -> item)\n" +
                "    pub operator getAtIndex(index: i32): User? { return item }\n" +
                "    pub operator setAtIndex(index: i32, element: User) { item = element }\n" +
                "}\n" +
                "pub func fIdx(b: Bag, s: Svc) { b[0] = s.fetch() }\n");
            CheckNoErrors("#28④ 索引写值降级豁免", unitIdx);
            // 普通 Any（非降级）不误伤
            var (unitPlainAny, _) = BindUnit(
                "pub class User { pub init() }\n" +
                "pub func gIf(u: User?, a: Any): User { return u if? a }\n");
            TestHarness.CheckSemanticError("普通 Any 不作 if? 回退",
                unitPlainAny.Diagnostics, "Null fallback must be assignable");
            var (unitPlainThrow, _) = BindUnit(
                "pub func gThrow(a: Any) { throw a }\n");
            TestHarness.CheckSemanticError("普通 Any 不可 throw",
                unitPlainThrow.Diagnostics, "Cannot throw");
        }
    }
}
