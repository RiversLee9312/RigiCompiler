using System.Linq;

namespace RigiCompiler.Tests
{
    // P3 闸门/驱动/重载修复批次（review 确认问题）的回归测试：
    // 1. async 闸门 2 对可变参数包按 SYNTAX §4.5 判定「展开后的每一个
    //    实参类型」（包自身 Array\<Any\> 打包形态不参与）+ 闸门 5 覆盖
    //    泛型可变包推导类型（GenericPack 槽）；
    // 2. 泛型 backing 字段的自动访问器体合成（getter return value /
    //    setter 隐含赋值）与 getter 全路径 return 检查（口径 != null）；
    // 3. 显式泛型实参逐候选约束检查（§3.6——首拒次收调用成功、全部
    //    拒绝才诊断且不逐候选重复）；
    // 4. 歧义诊断只列真正平局的 winners 子集 + 空泛型包 Syntax 非空契约；
    // 5. bug A2：shared interface + 接口传染矩阵（§3.1.1/§4.5——接口可标
    //    shared、含 async 成员的接口必须 shared、shared 接口沿 implements
    //    与接口继承单向传染）。
    public static partial class BinderTests
    {

        // ===== bug g4：泛型填入点隐式限制检查（P3 侧挂点）=====
        //
        // P3 两处填入点与 P2 同通道（GenericConstraints.CheckConstructedType）：
        // 函数体内类型引用（TypeReferences.Resolve——new/标注/cast）与泛型
        // 调用显式实参（CallFacility.ResolveGenericArguments）。隐式限制
        // 违规只诊断不拒绝（可恢复模型，避免级联误诊）。
        private static void TestInstantiationFillInP3()
        {
            CompilerTestTools.Section("P3 Instantiation Fill-In Limits (§3.1.1/§3.6, bug g4)");

            const string prelude =
                "class LocalUser {\n" +
                "    pub const name: String\n" +
                "    pub init(_ -> name)\n" +
                "}\n" +
                "struct Wrap\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "}\n";

            // new 表达式填入点（g4 原始形态的函数体内等价）
            var (u1, _) = BindUnit(prelude +
                "func main() { const w = new Wrap\\<LocalUser>(new LocalUser(\"x\")) }\n");
            CaseAssertions.CheckSemanticError("new 填入点非 rich struct 持 Object", u1.Diagnostics,
                "Non-rich struct 'Wrap' cannot hold object field 'v' " +
                "(via type argument of 'Wrap<LocalUser>')");

            // 隐式限制违规不拒绝（可恢复）：同一函数体内后续语句正常绑定，
            // 不产生「缺初始化器」级联误诊
            CaseAssertions.CheckTrue("隐式限制违规无级联误诊",
                !u1.Diagnostics.Diagnostics.Any(d =>
                    d.Message.Contains("requires a type annotation or an initializer")));

            // 反例：值类型实参与 rich 持有者经 new 填入正常
            var (ok1, _) = BindUnit(prelude +
                "rich struct RichWrap\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "}\n" +
                "func main() {\n" +
                "    const a = new Wrap\\<i32>(8)\n" +
                "    const b = new RichWrap\\<LocalUser>(new LocalUser(\"x\"))\n" +
                "}\n");
            CheckNoErrors("值类型实参 / rich 持有者 new 填入合法", ok1);

            // 局部标注填入点（TypeReferences.Resolve 同通道）
            var (u2, _) = BindUnit(prelude +
                "func main() { var w: Wrap\\<LocalUser> }\n");
            CaseAssertions.CheckSemanticError("局部标注填入点", u2.Diagnostics,
                "Non-rich struct 'Wrap' cannot hold object field 'v'");

            // 泛型调用显式实参填入点（CallFacility.ResolveGenericArguments）
            var (u3, _) = BindUnit(prelude +
                "func id\\<T>(x: T): T { return x }\n" +
                "func main() {\n" +
                "    const w = id\\<Wrap\\<LocalUser>>(new Wrap\\<LocalUser>(new LocalUser(\"x\")))\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("泛型调用显式实参填入点", u3.Diagnostics,
                "Non-rich struct 'Wrap' cannot hold object field 'v'");

            // 内建构造透明：Box 实参递归到内层用户构造
            var (ok2, _) = BindUnit(prelude +
                "func main() { var b: Box\\<Wrap\\<i32>> }\n");
            CheckNoErrors("Box\\<Wrap\\<i32>> 嵌套合法", ok2);
            var (u4, _) = BindUnit(prelude +
                "func main() { var b: Box\\<Wrap\\<LocalUser>> }\n");
            CaseAssertions.CheckSemanticError("Box\\<Wrap\\<LocalUser>> 嵌套报错", u4.Diagnostics,
                "Non-rich struct 'Wrap' cannot hold object field 'v'");

            // async 闸门 2/3 经实参收口（P3 标注填入点，stdlib Task 在场）
            var (u5, _) = BindUnitWithStdlib(
                "class LocalUser { }\n" +
                "class AC\\<T> { async func f(x: T) { } }\n" +
                "func main() { var c: AC\\<LocalUser> }\n");
            CaseAssertions.CheckSemanticError("async 闸门 2 经实参收口（P3）", u5.Diagnostics,
                "Parameter 'x' of async function 'f' must be a shared-safe type: " +
                "'LocalUser' (via instantiation 'AC<LocalUser>')");
        }


        // ===== bug A2：shared interface 与接口传染矩阵（§3.1.1/§4.5）=====
        private static void TestSharedInterfaceContagion()
        {
            CompilerTestTools.Section("P2/P3 Shared Interface Contagion (§3.1.1/§4.5, bug A2)");

            // 正例：shared interface 声明 async 成员 + 经接口类型 await 调用
            // （修复前闸门 1 误拒：'Worker' 不在共享安全白名单）
            var (unit, bodies) = BindUnitWithStdlib(
                "pub shared interface Worker {\n" +
                "    async func run(x: i32): i32\n" +
                "}\n" +
                "pub shared class W implements Worker {\n" +
                "    pub init()\n" +
                "    pub async override func run(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "func main() {\n" +
                "    const w: Worker = new W()\n" +
                "    const r = await w.run(10)\n" +
                "}\n");
            CheckNoErrors("shared interface 经接口 await 调用无诊断", unit);
            var awaitDecl = (BoundLocalDeclarationStatement)BodyOf(bodies, "main").Body.Statements[1];
            CaseAssertions.CheckTrue("await 结果类型 = i32（Task\\<i32> 解包）",
                ReferenceEquals(awaitDecl.Local.Type, unit.Symbols.Bootstrap.Int32));
            var ifaceCall = (BoundInstanceCallExpression)((BoundAwaitExpression)awaitDecl.Initializer!).Operand;
            CaseAssertions.CheckTrue("经接口绑定 async 接口方法（Owner = Worker）",
                ifaceCall.Method.IsAsync
                && ifaceCall.Method.Owner is { Kind: TypeKind.Interface, Name: "Worker", IsShared: true });

            // 负例：非 shared 接口声明 async 成员（声明点 fail-fast）
            var (unit2, _) = BindUnit(
                "pub interface Worker {\n" +
                "    async func run(x: i32): i32\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("非 shared 接口含 async 成员", unit2.Diagnostics,
                "'Worker': an interface declaring 'async' members must be 'shared'");

            // 负例：非 shared class 实现 shared 接口
            var (unit3, _) = BindUnit(
                "pub shared interface Worker { func run(x: i32): i32\n }\n" +
                "pub class W implements Worker {\n" +
                "    pub override func run(x: i32): i32 { return (x + 1) }\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("非 shared class 实现 shared 接口", unit3.Diagnostics,
                "'W': interface 'Worker' is 'shared', so the implementing type must also be 'shared'");

            // 正例：shared class 实现非 shared 接口（反向收紧不管）
            var (unit4, _) = BindUnit(
                "pub interface Worker { func run(x: i32): i32\n }\n" +
                "pub shared class W implements Worker {\n" +
                "    pub override func run(x: i32): i32 { return (x + 1) }\n" +
                "}\n");
            CheckNoErrors("shared class 实现非 shared 接口无诊断", unit4);

            // 负例：接口继承 shared 接口未标 shared
            var (unit5, _) = BindUnit(
                "pub shared interface IBase { }\n" +
                "pub interface IChild : IBase { }\n");
            CaseAssertions.CheckSemanticError("派生接口未标 shared", unit5.Diagnostics,
                "'IChild': base interface 'IBase' is 'shared', so the derived interface must also be 'shared'");

            // 链式传染：shared 沿接口继承链逐段传染 + 实现端收口
            var (unit6, _) = BindUnit(
                "pub shared interface IA { }\n" +
                "pub shared interface IB : IA { }\n" +
                "pub interface IC : IB { }\n");
            CaseAssertions.CheckSemanticError("链式传染（IC 未标 shared）", unit6.Diagnostics,
                "'IC': base interface 'IB' is 'shared', so the derived interface must also be 'shared'");
            var (unit7, _) = BindUnit(
                "pub shared interface IA { }\n" +
                "pub shared interface IB : IA { }\n" +
                "pub shared class Impl implements IB { }\n");
            CheckNoErrors("链式全标 shared 无诊断", unit7);
        }

        // ===== async 闸门 2/5 可变参数包（§4.5）=====
        private static void TestAsyncGateVariadicPacks()
        {
            CompilerTestTools.Section("P3 Async Gates Variadic Pack Fixes (§4.5)");

            // 正例：位置值包元素共享安全（i32）→ 无诊断
            // （修复前包整体按 Array\<Any\> 判定，任何带包 async 调用误报）
            var (unit, _) = BindUnit(
                "async func log(nums: i32...) { }\n" +
                "func f() { log(1, 2) }\n");
            CheckNoErrors("位置值包元素共享安全无诊断", unit);

            // 正例：空值包（log()）同不误报
            var (unit2, _) = BindUnit(
                "async func log(nums: i32...) { }\n" +
                "func f() { log() }\n");
            CheckNoErrors("空值包无诊断", unit2);

            // 正例：具名值包元素共享安全
            var (unit3, _) = BindUnit(
                "async func log(options: named i32...) { }\n" +
                "func f() { log(a = 1, b = 2) }\n");
            CheckNoErrors("具名值包元素共享安全无诊断", unit3);

            // 反例：泛型可变包实参/推导类型非共享安全——闸门 2（展开
            // 实参值）与闸门 5（推导泛型实参，GenericPack 槽）双双命中
            // （修复前闸门 5 对包推导类型漏查、闸门 2 对包整体误判）
            var (unit4, _) = BindUnit(
                "class LocalUser { }\n" +
                "async func collect\\<TArgs...>(xs: TArgs...) { }\n" +
                "func f() { collect(new LocalUser()) }\n");
            CaseAssertions.CheckSemanticError("闸门 2 包展开实参非共享安全", unit4.Diagnostics,
                "argument of async function 'collect' must be a shared-safe type: 'LocalUser'");
            CaseAssertions.CheckSemanticError("闸门 5 包推导类型非共享安全", unit4.Diagnostics,
                "type argument of async function 'collect' must be a shared-safe type: 'LocalUser'");

            // 正例：泛型可变包推导类型共享安全（i32/String）
            var (unit5, _) = BindUnit(
                "async func collect\\<TArgs...>(xs: TArgs...) { }\n" +
                "func f() { collect(1, \"s\") }\n");
            CheckNoErrors("泛型包共享安全推导类型无诊断", unit5);
        }

        // ===== 泛型 backing 字段访问器（§9.4.1 + S9a 值层契约）=====
        private static void TestGenericBackingAccessors()
        {
            CompilerTestTools.Section("P3 Generic Backing Accessors");

            // 正例：泛型 backing 自动访问器端到端（P1–P3）——自动 getter
            // 合成 return value（backing 读，类型 T）、自动 setter 体首
            // 合成隐含赋值 backing = value（修复前两处均按 is TypeSymbol
            // 跳过，泛型 backing 合成空体/丢赋值）
            var (unit, bodies) = BindUnit(
                "pub class Box\\<T> {\n" +
                "    pub var item: T {\n" +
                "        pub get\n" +
                "        pub set\n" +
                "    }\n" +
                "    pub init(_ -> item) { }\n" +
                "}\n");
            CheckNoErrors("无诊断（泛型 backing 自动访问器）", unit);
            var getterBody = bodies.Single(b => b.Method.Kind == MethodKind.Getter
                && b.Method.Name == "item");
            CaseAssertions.Check("自动 getter 合成体（return value，T）",
                BoundDescribe.Body(getterBody),
                "Body(item, [], [Return(InstField(item, This(Box<T>), T))])");
            var setterBody = bodies.Single(b => b.Method.Kind == MethodKind.Setter
                && b.Method.Name == "item");
            CaseAssertions.Check("自动 setter 合成体（隐含赋值，T）",
                BoundDescribe.Body(setterBody),
                "Body(item, [], [Assign(InstField(..value, This(Box<T>), T), Param(value,T))])");

            // 反例：返回类型为泛型参数 T 的空体 computed getter（(_: _)
            // 形态）→ 全路径 return 检查（修复前 ReturnType is TypeSymbol
            // 判定跳过检查，空体漏诊断；口径与 BindBody 的 != null 统一）
            var (unit2, _) = BindUnit(
                "pub class Box\\<T> {\n" +
                "    pub var item: T {\n" +
                "        pub get(_: _) { }\n" +
                "    }\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("返回 T 的空体 getter 报缺 return", unit2.Diagnostics,
                "Function 'item' must return a value on all code paths");
        }

        // ===== 显式泛型实参逐候选约束检查（§3.6）=====
        private static void TestPerCandidateConstraints()
        {
            CompilerTestTools.Section("P3 Per-candidate Constraint Checks (§3.6)");

            // 首拒次收：首候选（声明序在前）约束拒绝 i32，次候选无约束
            // 接受——调用成功并命中次候选（修复前只对 matching[0] 检查，
            // 整调用误拒）
            var (unit, bodies) = BindUnit(
                "open class Animal { }\n" +
                "func pick\\<T extends Animal>(x: T): i32 { return 1 }\n" +
                "func pick\\<T>(x: T, y: i32 = 0): i32 { return 2 }\n" +
                "func main() { var v = pick\\<i32>(5) }\n");
            CheckNoErrors("无诊断（首拒次收调用成功）", unit);
            var call = (BoundCallExpression)((BoundLocalDeclarationStatement)
                BodyOf(bodies, "main").Body.Statements[0]).Initializer!;
            CaseAssertions.CheckTrue("命中无约束的次候选（2 形参 + 显式泛型实参）",
                call.Method.Parameters.Count == 2 && call.TypeArguments.Count == 1);

            // 全部候选约束拒绝：对首候选回放一次诊断（不逐候选重复）
            var (unit2, _) = BindUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func pick\\<T extends Animal>(x: T): i32 { return 1 }\n" +
                "func pick\\<T extends Dog>(x: T, y: i32 = 0): i32 { return 2 }\n" +
                "func main() { var v = pick\\<i32>(5) }\n");
            CaseAssertions.CheckSemanticError("全部候选约束拒绝即诊断", unit2.Diagnostics,
                "Type argument 'i32' does not satisfy the 'Extends Animal' constraint of 'T'");
            CaseAssertions.CheckTrue("约束诊断不逐候选重复",
                unit2.Diagnostics.Diagnostics.Count(d => d.Message.Contains("constraint")) == 1);

            // 约束满足的多候选正常 ranking（平局打破：填充默认值更少者胜）
            var (unit3, bodies3) = BindUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func pick\\<T extends Animal>(x: T): i32 { return 1 }\n" +
                "func pick\\<T>(x: T, y: i32 = 0): i32 { return 2 }\n" +
                "func main() { var v = pick\\<Dog>(new Dog()) }\n");
            CheckNoErrors("无诊断（约束满足多候选）", unit3);
            var call3 = (BoundCallExpression)((BoundLocalDeclarationStatement)
                BodyOf(bodies3, "main").Body.Statements[0]).Initializer!;
            CaseAssertions.CheckTrue("填充默认值更少的候选胜出（1 形参）",
                call3.Method.Parameters.Count == 1);
        }

        // ===== 歧义诊断 winners 子集 + 空泛型包 Syntax 契约 =====
        private static void TestAmbiguityWinnersAndPackSyntax()
        {
            CompilerTestTools.Section("P3 Ambiguity Winners & Generic Pack Syntax");

            // 歧义消息只列真正平局的 winners（修复前列全部 applicable，
            // 被占优淘汰的 m(Base, Base) 误导定位）
            var (unit, _) = BindUnit(
                "open class Base { }\n" +
                "open class Mid : Base { }\n" +
                "class Leaf : Mid { }\n" +
                "func m(a: Mid, b: Base) { }\n" +
                "func m(a: Base, b: Mid) { }\n" +
                "func m(a: Base, b: Base) { }\n" +
                "func f(l: Leaf) { m(l, l) }\n");
            var ambiguous = unit.Diagnostics.Diagnostics
                .Single(d => d.Message.Contains("ambiguous"));
            CaseAssertions.CheckTrue("歧义消息列出平局 winners",
                ambiguous.Message.Contains("m(Mid, Base)")
                && ambiguous.Message.Contains("m(Base, Mid)"));
            CaseAssertions.CheckTrue("歧义消息不含被占优候选",
                !ambiguous.Message.Contains("m(Base, Base)"));

            // 空泛型包推导产物的 Syntax 非空契约（修复前空包以 null! 占位；
            // 以调用节点承载）
            var (unit2, bodies2) = BindUnit(
                "func collect\\<TArgs...>(xs: TArgs...): i32 { return 0 }\n" +
                "func main() { var v = collect() }\n");
            CheckNoErrors("无诊断（空泛型包）", unit2);
            var call2 = (BoundCallExpression)((BoundLocalDeclarationStatement)
                BodyOf(bodies2, "main").Body.Statements[0]).Initializer!;
            CaseAssertions.CheckTrue("空包 Syntax 以调用节点承载",
                call2.GenericPack != null
                && ReferenceEquals(call2.GenericPack.Syntax, call2.Syntax)
                && call2.GenericPack.TypeArguments.Count == 0);
        }

        // ===== W8：IsSharedSafe 沿 GP extends 界链递归推导 =====
        private static void TestGenericBoundSharedSafeDerivation()
        {
            CompilerTestTools.Section("P3 GateFixes: GP 界链 shared-safe 推导（W8）");

            // 正例：T extends U、U extends shared class → async 闸门通过
            // （参数 T 走闸门 5 界链；参数 T? 走 Nullable\<GP\> 按界推导）
            var (ok, _) = BindUnit(
                "shared class Base { pub init() }\n" +
                "class Host\\<U extends Base> {\n" +
                "    pub async func send\\<T extends U>(x: T) { }\n" +
                "    pub async func sendNull\\<T extends U>(x: T?) { }\n" +
                "}\n");
            CheckNoErrors("W8 多层 GP 界链 async 闸门通过", ok);

            // 三层：T extends U、U extends V、V extends shared interface
            var (ok3, _) = BindUnit(
                "shared interface IShared { }\n" +
                "class Outer\\<V extends IShared> {\n" +
                "    class Inner\\<U extends V> {\n" +
                "        pub async func send\\<T extends U>(x: T?) { }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("W8 三层 GP 界链 Nullable\\<T\\> async 闸门通过", ok3);

            // 反例：非 shared 界链仍不过闸门
            var (bad, _) = BindUnit(
                "class Local { pub init() }\n" +
                "class Host\\<U extends Local> {\n" +
                "    pub async func send\\<T extends U>(x: T) { }\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("W8 反例：非 shared 界链闸门 5",
                bad.Diagnostics,
                "Generic parameter 'T' of async function 'send' must have a shared-safe " +
                "constraint bound: 'Local'");

            var (badNull, _) = BindUnit(
                "class Local { pub init() }\n" +
                "class Host\\<U extends Local> {\n" +
                "    pub async func sendNull\\<T extends U>(x: T?) { }\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("W8 反例：Nullable\\<非 shared 界链\\> 闸门 2",
                badNull.Diagnostics,
                "Parameter 'x' of async function 'sendNull' must be a shared-safe type");

            // 环界：直接构造符号，不经语法（同列表 T extends T 声明侧已拒）
            var self = new GenericParameterSymbol("T");
            self.Constraints.Add(new GenericConstraintInfo(
                GenericConstraintKind.Extends, self));
            CaseAssertions.CheckTrue("W8 环界 T extends T 不发散且非 shared-safe",
                !self.IsSharedSafe());

            var a = new GenericParameterSymbol("T");
            var b = new GenericParameterSymbol("U");
            a.Constraints.Add(new GenericConstraintInfo(GenericConstraintKind.Extends, b));
            b.Constraints.Add(new GenericConstraintInfo(GenericConstraintKind.Extends, a));
            CaseAssertions.CheckTrue("W8 环界 T extends U extends T 不发散",
                !a.IsSharedSafe() && !b.IsSharedSafe());

            // 语法侧 T extends T 报同列表引用，不挂起
            var (cycleSyntax, _) = BindUnit(
                "func loop\\<T extends T>(x: T): T { return x }\n");
            CaseAssertions.CheckSemanticError("W8 语法环界 T extends T 报同列表引用",
                cycleSyntax.Diagnostics,
                "Constraint bound of 'T' cannot reference generic parameter 'T'");
        }
    }
}
