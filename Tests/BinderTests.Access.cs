using System.Linq;

namespace RigiCompiler.Tests
{
    // BinderTests 的 S8e 部分：使用点访问控制（SYNTAX §16.1）、访问器绑定
    // （§9.4/§9.4.1）与 override 配套（§9.2.1）的 P3 测试。
    // 多文件用例经 BindUnit(params string[])——每个源串是一个独立文件
    // （文件身份按 RootASTNode 引用判定）。

    public static partial class BinderTests
    {
        // ===== 使用点访问控制（S8e，SYNTAX §16.1：pub/protected/internal/priv）=====
        private static void TestAccessControl()
        {
            TestHarness.Section("P3 Access Control");

            // pub 跨文件可见（类型引用 + 构造 + 成员调用全链路）
            var (unit, _) = BindUnit(
                "pub class Pub {\n" +
                "    pub func m(): i32 { return 1 }\n" +
                "}\n",
                "pub func use(): i32 {\n" +
                "    var p = new Pub()\n" +
                "    return p.m()\n" +
                "}\n");
            CheckNoErrors("pub 跨文件可见", unit);

            // priv（默认）顶层类同文件可见
            var (unit2, _) = BindUnit(
                "class Hidden { }\n" +
                "pub func mk(): Hidden { return new Hidden() }\n");
            CheckNoErrors("priv 顶层类同文件可见", unit2);

            // priv 顶层类跨文件拒绝——函数体内类型引用（局部变量类型标注）
            var (unit3, _) = BindUnit(
                "class Hidden { }\n",
                "pub func mk(): i32 {\n" +
                "    var h: Hidden\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckSemanticError("priv 顶层类跨文件（函数体内类型标注）",
                unit3.Diagnostics, "'Hidden' is inaccessible due to its accessibility level");

            // protected 成员：子类（跨文件）体内可见（裸名经基类链解析）
            var (unit4, _) = BindUnit(
                "pub open class Base {\n" +
                "    protected func secret(): i32 { return 1 }\n" +
                "}\n",
                "pub class Sub : Base {\n" +
                "    pub func use(): i32 { return secret() }\n" +
                "}\n");
            CheckNoErrors("protected 子类跨文件体内可见", unit4);

            // protected 成员：同包（同命名空间不同文件）可见
            var (unit5, _) = BindUnit(
                "namespace pkg\n" +
                "pub class A {\n" +
                "    protected func s(): i32 { return 1 }\n" +
                "}\n",
                "namespace pkg\n" +
                "pub func use(a: A): i32 { return a.s() }\n");
            CheckNoErrors("protected 同包跨文件可见", unit5);

            // protected 成员：异包非子类拒绝
            var (unit6, _) = BindUnit(
                "namespace pkg1\n" +
                "pub class A {\n" +
                "    protected func s(): i32 { return 1 }\n" +
                "}\n",
                "namespace pkg2\n" +
                "pub func use(a: pkg1.A): i32 { return a.s() }\n");
            TestHarness.CheckSemanticError("protected 异包非子类拒绝", unit6.Diagnostics,
                "'s' is inaccessible due to its accessibility level");

            // internal 跨文件放行（当前单编译单元即模块，§16.1）
            var (unit7, _) = BindUnit(
                "internal func helper(): i32 { return 1 }\n",
                "pub func use(): i32 { return helper() }\n");
            CheckNoErrors("internal 跨文件放行", unit7);

            // priv init 跨类 new 拦截（§12.2 构造调用是使用点）；类内自建为对照
            var (unit8, _) = BindUnit(
                "pub class Locked {\n" +
                "    priv init() { }\n" +
                "    pub static func make(): Locked { return new Locked() }\n" +
                "}\n" +
                "pub func f(): Locked { return new Locked() }\n");
            TestHarness.CheckSemanticError("priv init 跨类 new 拦截", unit8.Diagnostics,
                "'init' is inaccessible due to its accessibility level");

            // priv 字段跨类读拒绝（无访问器字段读写位均查字段可见性）
            var (unit9, _) = BindUnit(
                "pub class Data {\n" +
                "    var secret: i32\n" +
                "}\n" +
                "pub class Spy {\n" +
                "    pub func read(d: Data): i32 { return d.secret }\n" +
                "}\n");
            TestHarness.CheckSemanticError("priv 字段跨类读拒绝", unit9.Diagnostics,
                "'secret' is inaccessible due to its accessibility level");

            // priv 字段跨类写拒绝
            var (unit10, _) = BindUnit(
                "pub class Data {\n" +
                "    var secret: i32\n" +
                "}\n" +
                "pub class Spy {\n" +
                "    pub func write(d: Data) { d.secret = 1 }\n" +
                "}\n");
            TestHarness.CheckSemanticError("priv 字段跨类写拒绝", unit10.Diagnostics,
                "'secret' is inaccessible due to its accessibility level");

            // ===== M81 裁决：ext 可见性按声明位置（§4.4/§16.1）=====
            // priv ext 同文件可见（默认 private = 仅声明文件）
            var (unit11, _) = BindUnit(
                "pub class Host { }\n" +
                "ext func Host.localHelp(): i32 { return 1 }\n" +
                "pub func use(h: Host): i32 { return h.localHelp() }\n");
            CheckNoErrors("priv ext 同文件可见", unit11);

            // priv ext 跨文件拒绝（按声明文件，非目标类型容器）
            var (unit12, _) = BindUnit(
                "pub class Host { }\n" +
                "ext func Host.localHelp(): i32 { return 1 }\n",
                "pub func use(h: Host): i32 { return h.localHelp() }\n");
            TestHarness.CheckSemanticError("priv ext 跨文件拒绝", unit12.Diagnostics,
                "'localHelp' is inaccessible due to its accessibility level");

            // pub ext 跨文件可见
            var (unit13, _) = BindUnit(
                "pub class Host { }\n" +
                "pub ext func Host.pubHelp(): i32 { return 1 }\n",
                "pub func use(h: Host): i32 { return h.pubHelp() }\n");
            CheckNoErrors("pub ext 跨文件可见", unit13);

            // internal ext 跨文件放行（单编译单元即模块）
            var (unit14, _) = BindUnit(
                "pub class Host { }\n" +
                "internal ext func Host.modHelp(): i32 { return 1 }\n",
                "pub func use(h: Host): i32 { return h.modHelp() }\n");
            CheckNoErrors("internal ext 跨文件放行", unit14);

            // ext 方法体不获得目标类型私有成员特权（DeclaringType = 语法宿主 null）
            var (unit15, _) = BindUnit(
                "pub class Host {\n" +
                "    var secret: i32\n" +
                "}\n" +
                "pub ext func Host.leak(): i32 { return this.secret }\n");
            TestHarness.CheckSemanticError("ext 体不获目标 priv 成员特权", unit15.Diagnostics,
                "'secret' is inaccessible due to its accessibility level");
        }

        // ===== 访问器绑定（S8e，SYNTAX §9.4/§9.4.1）=====
        private static void TestAccessors()
        {
            TestHarness.Section("P3 Accessors");

            // 类字段 backing 访问器：外部读写绑定——节点形态不变是本设计要点
            // （读 = InstField 字段访问节点、写 = Assign 赋值语句节点，不转调用）
            var (unit, bodies) = BindUnit(
                "pub class Counter {\n" +
                "    pub var value: i32 {\n" +
                "        pub get(value: _) {\n" +
                "            return value\n" +
                "        }\n" +
                "        pub set(value: _) {\n" +
                "        }\n" +
                "    }\n" +
                "    pub init() { value = 0 }\n" +
                "}\n" +
                "func read(c: Counter): i32 { return c.value }\n" +
                "func write(c: Counter, x: i32) { c.value = x }\n");
            CheckNoErrors("无诊断（backing 访问器读写）", unit);
            TestHarness.Check("读带访问器字段 → InstField（形态不变）",
                BoundDescribe.Body(BodyOf(bodies, "read")),
                "Body(read, [], [Return(InstField(value, Param(c,Counter), i32))])");
            TestHarness.Check("写带访问器字段 → Assign（形态不变）",
                BoundDescribe.Body(BodyOf(bodies, "write")),
                "Body(write, [], [Assign(InstField(value, Param(c,Counter), i32), " +
                "Param(x,i32))])");

            // 访问器体绑定（符号名即字段名，getter/setter 经 Method.Kind 区分）：
            // getter 体内裸名 value 解析为 backing 字段读（实例补 this）；
            // backing setter 体首合成隐含赋值 backing = value
            var counterType = unit.Symbols.GlobalNamespace.Types
                .Single(t => t.Name == "Counter");
            var getterBody = bodies.Single(b => b.Method.Kind == MethodKind.Getter
                && b.Method.Name == "value");
            TestHarness.Check("backing getter 体（value → backing 读）",
                BoundDescribe.Body(getterBody),
                "Body(value, [], [Return(InstField(value, This(Counter), i32))])");
            var setterBody = bodies.Single(b => b.Method.Kind == MethodKind.Setter
                && b.Method.Name == "value");
            TestHarness.Check("backing setter 体首隐含赋值合成",
                BoundDescribe.Body(setterBody),
                "Body(value, [], [Assign(InstField(value, This(Counter), i32), " +
                "Param(value,i32))])");
            // 结构性事实：访问器符号挂字段三槽，读写绑定与声明同一字段符号
            var valueField = counterType.Fields.Single(f => f.Name == "value");
            TestHarness.CheckTrue("访问器符号挂字段 Getter/Setter 槽",
                ReferenceEquals(valueField.Getter, getterBody.Method)
                && ReferenceEquals(valueField.Setter, setterBody.Method));
            var readReturn = (BoundReturnStatement)BodyOf(bodies, "read").Body.Statements[0];
            TestHarness.CheckTrue("读侧字段符号引用相等",
                ReferenceEquals(((BoundFieldAccessExpression)readReturn.Value!).Field,
                    valueField));

            // 全局变量访问器（命名空间内）：读写绑定 + 自动访问器合成体
            // （无体 getter 合成 return value、无体 setter 仅隐含赋值）
            var (unit2, bodies2) = BindUnit(
                "namespace app\n" +
                "pub var height: i32 {\n" +
                "    pub get\n" +
                "    pub set\n" +
                "} = 200\n" +
                "pub var depth: i32 {\n" +
                "    pub get(value: _) { return value }\n" +
                "    pub set(value: _) { }\n" +
                "} = 0\n" +
                "pub func readH(): i32 { return height }\n" +
                "pub func writeD(x: i32) { depth = x }\n");
            CheckNoErrors("无诊断（全局访问器）", unit2);
            TestHarness.Check("全局访问器字段读 → Field（形态不变）",
                BoundDescribe.Body(BodyOf(bodies2, "readH")),
                "Body(readH, [], [Return(Field(height,i32))])");
            TestHarness.Check("全局访问器字段写 → Assign（形态不变）",
                BoundDescribe.Body(BodyOf(bodies2, "writeD")),
                "Body(writeD, [], [Assign(Field(depth,i32), Param(x,i32))])");
            var autoGetter = bodies2.Single(b => b.Method.Kind == MethodKind.Getter
                && b.Method.Name == "height");
            TestHarness.Check("自动 getter 合成体（return value）",
                BoundDescribe.Body(autoGetter),
                "Body(height, [], [Return(Field(height,i32))])");
            var autoSetter = bodies2.Single(b => b.Method.Kind == MethodKind.Setter
                && b.Method.Name == "height");
            TestHarness.Check("自动 setter 合成体（仅隐含赋值）",
                BoundDescribe.Body(autoSetter),
                "Body(height, [], [Assign(Field(height,i32), Param(value,i32))])");

            // 仅 get 字段写 → has no setter
            var (unit3, _) = BindUnit(
                "pub class Box {\n" +
                "    pub var ro: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "    }\n" +
                "    pub init() { }\n" +
                "}\n" +
                "func f(b: Box) { b.ro = 1 }\n");
            TestHarness.CheckSemanticError("仅 get 字段写", unit3.Diagnostics,
                "'ro' has no setter");

            // 仅 set 字段读 → has no getter
            var (unit4, _) = BindUnit(
                "pub class Box {\n" +
                "    pub var wo: i32 {\n" +
                "        pub set(value: _) { }\n" +
                "    }\n" +
                "    pub init() { }\n" +
                "}\n" +
                "func g(b: Box): i32 { return b.wo }\n");
            TestHarness.CheckSemanticError("仅 set 字段读", unit4.Diagnostics,
                "'wo' has no getter");

            // priv set 跨类写 → setter is inaccessible（访问器自身可见性在
            // 使用点分别检查，§9.4.1）；同类 get 可读故无次生读诊断
            var (unit5, _) = BindUnit(
                "pub class Box {\n" +
                "    pub var v: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        priv set(value: _) { }\n" +
                "    }\n" +
                "    pub init() { }\n" +
                "}\n" +
                "func h(b: Box): i32 {\n" +
                "    b.v = 3\n" +
                "    return b.v\n" +
                "}\n");
            TestHarness.CheckSemanticError("priv set 跨类写", unit5.Diagnostics,
                "'v' setter is inaccessible due to its accessibility level");
            TestHarness.CheckTrue("pub get 同读无次生诊断（错误恰一条）",
                unit5.Diagnostics.Diagnostics.Count(
                    d => d.Severity == DiagnosticSeverity.Error) == 1,
                string.Join("; ", unit5.Diagnostics.Diagnostics.Select(d => d.Message)));

            // getter 体内 value = 1 → Cannot assign to 'value' in a getter
            var (unit6, _) = BindUnit(
                "pub class Box {\n" +
                "    pub var v: i32 {\n" +
                "        pub get(value: _) {\n" +
                "            value = 1\n" +
                "            return value\n" +
                "        }\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("getter 体内写 value", unit6.Diagnostics,
                "Cannot assign to 'value' in a getter");

            // smart cast：带访问器的 const 字段 is/null 判等后不收窄（§9.4.1：
            // 外部读写一律经访问器，读取结果不承诺稳定）；无访问器 const 字段
            // 收窄为对照
            var (unit7, bodies7) = BindUnit(
                "pub class Box {\n" +
                "    pub const wrapped: String? {\n" +
                "        pub get(value: _) { return value }\n" +
                "    }\n" +
                "    pub const plain: String?\n" +
                "    pub init(p: String?) { plain = p }\n" +
                "    pub func use(): String? {\n" +
                "        if (wrapped != null) { return wrapped }\n" +
                "        return null\n" +
                "    }\n" +
                "    pub func use2(): String? {\n" +
                "        if (plain != null) { return plain }\n" +
                "        return null\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（访问器收窄规则）", unit7);
            TestHarness.CheckTrue("带访问器 const 字段不收窄",
                !BoundDescribe.Body(BodyOf(bodies7, "use")).Contains("SmartCast"));
            TestHarness.CheckTrue("无访问器 const 字段收窄（对照）",
                BoundDescribe.Body(BodyOf(bodies7, "use2")).Contains(
                    "SmartCast(InstField(plain, This(Box), String?), String)"));

            // 局部访问器（M107 路线 C）：正例见 TestLocalAccessors
        }

        // ===== 局部变量访问器（M107 路线 C，SYNTAX §9.4 栈上形态）=====
        private static void TestLocalAccessors()
        {
            TestHarness.Section("P3 Local Accessors");

            // backing 读写：cell 化 + Getter/Setter 槽
            var (unit, bodies) = BindUnitWithStdlib(
                "pub func f(): i32 {\n" +
                "    var local: i32 {\n" +
                "        get(value: _) { return value }\n" +
                "        set(value: _) { }\n" +
                "    } = 0\n" +
                "    local = 1\n" +
                "    return local\n" +
                "}\n");
            CheckNoErrors("backing 局部访问器无诊断", unit);
            var local = BodyOf(bodies, "f").Locals.First(l => l.Name == "local");
            TestHarness.CheckTrue("backing 局部 cell 化", local.CellStorage != null);
            TestHarness.CheckTrue("backing 局部 Getter/Setter 槽",
                local.Getter != null && local.Setter != null && local.HasBackingStorage);

            // 计算形态
            var (unit2, bodies2) = BindUnitWithStdlib(
                "pub func f(base: i32): i32 {\n" +
                "    var doubled: i32 {\n" +
                "        get(_: _) { return base + base }\n" +
                "        set(_: _) { }\n" +
                "    } = 0\n" +
                "    return doubled\n" +
                "}\n");
            CheckNoErrors("计算形态局部访问器无诊断", unit2);
            var doubled = BodyOf(bodies2, "f").Locals.First(l => l.Name == "doubled");
            TestHarness.CheckTrue("计算形态无 backing",
                doubled.Getter != null && !doubled.HasBackingStorage);

            // 自动访问器
            var (unit3, _) = BindUnitWithStdlib(
                "pub func f(): i32 {\n" +
                "    var h: i32 {\n" +
                "        get\n" +
                "        set\n" +
                "    } = 7\n" +
                "    h = 8\n" +
                "    return h\n" +
                "}\n");
            CheckNoErrors("自动访问器无诊断", unit3);

            // const + setter
            var (unit4, _) = BindUnitWithStdlib(
                "pub func f(): i32 {\n" +
                "    const c: i32 {\n" +
                "        get(value: _) { return value }\n" +
                "        set(value: _) { }\n" +
                "    } = 1\n" +
                "    return c\n" +
                "}\n");
            TestHarness.CheckSemanticError("const 局部不得有 setter", unit4.Diagnostics,
                "Const local 'c' cannot declare a setter");

            // 无体计算形态（parser 要求 (_: _) 必带体，源码不可达——自动
            // 访问器后手工翻 HasBackingField=false 直达 P3 防御检查，同 P2 字段先例）
            {
                var roots = new List<RootASTNode>();
                roots.AddRange(StdlibSources.ParseAll());
                var user = TestHarness.ParseRoot(
                    "pub func f(): i32 {\n" +
                    "    var x: i32 {\n" +
                    "        get\n" +
                    "    } = 0\n" +
                    "    return x\n" +
                    "}\n");
                var fn = user.Declarations.OfType<CallableDeclarationASTNode>().First();
                var localDecl = fn.Body!.Statements.OfType<VariableDeclarationASTNode>().First();
                localDecl.Getter!.HasBackingField = false;
                roots.Add(user);
                var unit5b = new CompilationUnit(roots.ToArray());
                var decls5b = DeclarationCollector.Collect(unit5b);
                DeclarationResolver.Resolve(unit5b, decls5b);
                Binder.Bind(unit5b, decls5b);
                TestHarness.CheckSemanticError("无体计算 getter", unit5b.Diagnostics,
                    "Computed getter of 'x' must have a body");
            }

            // 仅 get 写
            var (unit6, _) = BindUnitWithStdlib(
                "pub func f(): i32 {\n" +
                "    var x: i32 {\n" +
                "        get(value: _) { return value }\n" +
                "    } = 0\n" +
                "    x = 1\n" +
                "    return x\n" +
                "}\n");
            TestHarness.CheckSemanticError("仅 get 不可写", unit6.Diagnostics,
                "'x' has no setter");

            // 仅 set 读
            var (unit7, _) = BindUnitWithStdlib(
                "pub func f(): i32 {\n" +
                "    var x: i32 {\n" +
                "        set(value: _) { }\n" +
                "    } = 0\n" +
                "    return x\n" +
                "}\n");
            TestHarness.CheckSemanticError("仅 set 不可读", unit7.Diagnostics,
                "'x' has no getter");

            // pub/priv 修饰符
            var (unit8, _) = BindUnitWithStdlib(
                "pub func f(): i32 {\n" +
                "    var x: i32 {\n" +
                "        pub get(value: _) { return value }\n" +
                "        priv set(value: _) { }\n" +
                "    } = 0\n" +
                "    return x\n" +
                "}\n");
            TestHarness.CheckSemanticError("局部访问器禁 pub", unit8.Diagnostics,
                "Local variable accessor cannot have modifier 'pub'");
            TestHarness.CheckSemanticError("局部访问器禁 priv", unit8.Diagnostics,
                "Local variable accessor cannot have modifier 'priv'");

            // 访问器体内引用外层局部
            var (unit9, bodies9) = BindUnitWithStdlib(
                "pub func f(): i32 {\n" +
                "    var outer = 10\n" +
                "    var x: i32 {\n" +
                "        get(_: _) { return outer }\n" +
                "        set(_: _) { outer = value }\n" +
                "    } = 0\n" +
                "    x = 1\n" +
                "    return outer\n" +
                "}\n");
            CheckNoErrors("访问器体内引用外层局部", unit9);
            var xLocal = BodyOf(bodies9, "f").Locals.First(l => l.Name == "x");
            TestHarness.CheckTrue("外层引用触发捕获条目",
                xLocal.CellStorage is { AccessorCaptures.Count: > 0 });

            // getter 副作用：写外层局部（可观测 cell 捕获）
            var (unit10, bodies10) = BindUnitWithStdlib(
                "pub func f(): i32 {\n" +
                "    var log = 0\n" +
                "    var x: i32 {\n" +
                "        get(value: _) { log = log + 1\n" +
                "            return value }\n" +
                "        set(value: _) { }\n" +
                "    } = 5\n" +
                "    var a = x\n" +
                "    var b = x\n" +
                "    return log\n" +
                "}\n");
            CheckNoErrors("getter 副作用写外层", unit10);
            TestHarness.CheckTrue("副作用访问器 cell + 捕获",
                BodyOf(bodies10, "f").Locals.First(l => l.Name == "x").CellStorage
                    is { AccessorCaptures.Count: > 0 });

            // M112：外层方法泛型 T + 局部访问器 cell 化
            var (unit11, bodies11) = BindUnitWithStdlib(
                "pub func wrap\\<T>(x: T): T {\n" +
                "    var y: T {\n" +
                "        get(value: _) { return value }\n" +
                "        set(value: _) { }\n" +
                "    } = x\n" +
                "    y = x\n" +
                "    return y\n" +
                "}\n");
            CheckNoErrors("方法泛型 T 局部访问器 cell 化无诊断", unit11);
            var yAcc = BodyOf(bodies11, "wrap").Locals.First(l => l.Name == "y");
            TestHarness.CheckTrue("访问器 cell 子类共享 generic(T)",
                yAcc.CellStorage != null
                && yAcc.CellStorage.CellClass.GenericParameters.Count == 1
                && yAcc.CellStorage.CellClass.GenericParameters[0].Name == "T");

            // M112：方法泛型 T 访问器 + lambda 捕获
            var (unit12, bodies12) = BindUnitWithStdlib(
                "pub func wrap\\<T>(x: T): T {\n" +
                "    var y: T {\n" +
                "        get(value: _) { return value }\n" +
                "        set(value: _) { }\n" +
                "    } = x\n" +
                "    var f = func{(): T -> y}\n" +
                "    return f()\n" +
                "}\n");
            CheckNoErrors("方法泛型 T 访问器被 lambda 捕获无诊断", unit12);
            var yCap = BodyOf(bodies12, "wrap").Locals.First(l => l.Name == "y");
            TestHarness.CheckTrue("访问器与捕获共享同一 cell",
                yCap.CellStorage != null && yCap.Getter != null);
        }

        // ===== override 配套（S8e，SYNTAX §9.2.1；P3 侧正例 + new abstract）=====
        private static void TestOverride()
        {
            TestHarness.Section("P3 Override");

            // 正例：open 基类 + override
            var (unit, _) = BindUnit(
                "pub open class Base {\n" +
                "    pub open func m(): i32 { return 1 }\n" +
                "}\n" +
                "pub class Sub : Base {\n" +
                "    pub override func m(): i32 { return 2 }\n" +
                "}\n");
            CheckNoErrors("open 基类 + override", unit);

            // 正例：接口实现 override
            var (unit2, _) = BindUnit(
                "pub interface Sized {\n" +
                "    func size(): i32\n" +
                "}\n" +
                "pub class Box implements Sized {\n" +
                "    pub override func size(): i32 { return 42 }\n" +
                "}\n");
            CheckNoErrors("接口实现 override", unit2);

            // 正例：stdlib 风格构造接口代入（IEnumerator\<i32\>——override
            // 签名按定义 → 构造代入实参后比较，current(): T → i32）
            var (unit3, _) = BindUnitWithStdlib(
                "pub class CounterEnum implements core.collections.IEnumerator\\<i32> {\n" +
                "    var n: i32\n" +
                "    pub init() { n = 0 }\n" +
                "    pub override func moveNext(): bool {\n" +
                "        n = n + 1\n" +
                "        return n < 3\n" +
                "    }\n" +
                "    pub override func current(): i32 { return n }\n" +
                "}\n");
            CheckNoErrors("构造接口 override（stdlib 风格代入）", unit3);

            // 正例：有默认实现的接口成员隐式继承（不待实现，§9.2.1）
            var (unit4, _) = BindUnit(
                "pub interface Greet {\n" +
                "    func hello(): i32 { return 1 }\n" +
                "}\n" +
                "pub class C implements Greet { }\n");
            CheckNoErrors("接口默认实现隐式继承", unit4);

            // 正例：经具体类调用 override 方法——候选池去重被覆写的基类
            // 成员（override 遮蔽不进重载候选池，否则同签名候选歧义）
            var (unit4b, bodies4b) = BindUnit(
                "pub open class Base {\n" +
                "    pub open func area(): i32 { return 0 }\n" +
                "}\n" +
                "pub class Square : Base {\n" +
                "    pub override func area(): i32 { return 1 }\n" +
                "}\n" +
                "func f(s: Square): i32 { return s.area() }\n");
            CheckNoErrors("经具体类调用 override 方法", unit4b);
            TestHarness.Check("override 调用绑定形态",
                BoundDescribe.Body(BodyOf(bodies4b, "f")),
                "Body(f, [], [Return(InstCall(area, Param(s,Square), [], i32))])");
            var squareType = unit4b.Symbols.GlobalNamespace.Types
                .Single(t => t.Name == "Square");
            var areaReturn = (BoundReturnStatement)BodyOf(bodies4b, "f").Body.Statements[0];
            TestHarness.CheckTrue("调用符号即 Square 的 override（非基类成员）",
                ReferenceEquals(((BoundInstanceCallExpression)areaReturn.Value!).Method,
                    squareType.Methods.Single(m => m.Name == "area")));

            // 负例：无目标 override（基类链与接口表均无签名匹配）
            var (unit5, _) = BindUnit(
                "pub open class Base {\n" +
                "    pub open func m(): i32 { return 1 }\n" +
                "}\n" +
                "pub class Sub : Base {\n" +
                "    pub override func nope(): i32 { return 3 }\n" +
                "}\n");
            TestHarness.CheckSemanticError("无目标 override", unit5.Diagnostics,
                "'nope': no inherited member to override");

            // 负例：目标非 open/abstract（静默继承成员不可覆写）
            var (unit6, _) = BindUnit(
                "pub open class Base {\n" +
                "    pub func m(): i32 { return 1 }\n" +
                "}\n" +
                "pub class Sub : Base {\n" +
                "    pub override func m(): i32 { return 2 }\n" +
                "}\n");
            TestHarness.CheckSemanticError("目标非 open", unit6.Diagnostics,
                "'m': inherited member is not 'open' or 'abstract'");

            // 负例：同名同签名隐藏继承成员未显式 override
            var (unit7, _) = BindUnit(
                "pub open class Base {\n" +
                "    pub open func m(): i32 { return 1 }\n" +
                "}\n" +
                "pub class Sub : Base {\n" +
                "    pub func m(): i32 { return 2 }\n" +
                "}\n");
            TestHarness.CheckSemanticError("隐藏无 override", unit7.Diagnostics,
                "'m' hides an inherited member; declare it 'override'");

            // 负例：abstract 方法必须在 abstract 类内
            var (unit8, _) = BindUnit(
                "pub class C {\n" +
                "    pub abstract func m(): i32\n" +
                "}\n");
            TestHarness.CheckSemanticError("abstract 在非 abstract 类", unit8.Diagnostics,
                "'m': abstract method requires an abstract class");

            // 负例：接口外的无体方法必须 abstract 或 native
            var (unit9, _) = BindUnit(
                "pub class C {\n" +
                "    pub func m(): i32\n" +
                "}\n");
            TestHarness.CheckSemanticError("无体非 abstract/native", unit9.Diagnostics,
                "'m' must have a body or be marked 'abstract'");

            // 负例：具体类未实现基类链上的 abstract 成员（两级链）
            var (unit10, _) = BindUnit(
                "pub abstract class A {\n" +
                "    pub abstract func m(): i32\n" +
                "}\n" +
                "pub abstract class B : A { }\n" +
                "pub class C : B { }\n");
            TestHarness.CheckSemanticError("未实现基类链 abstract 成员", unit10.Diagnostics,
                "'C' does not implement abstract member 'm'");

            // 负例：具体类未实现无体接口成员
            var (unit11, _) = BindUnit(
                "pub interface I {\n" +
                "    func m(): i32\n" +
                "}\n" +
                "pub class C implements I { }\n");
            TestHarness.CheckSemanticError("未实现无体接口成员", unit11.Diagnostics,
                "'C' does not implement abstract member 'm'");

            // new abstract 类是编译错误（§9.2.1，P3 构造点）
            var (unit12, _) = BindUnit(
                "pub abstract class A {\n" +
                "    pub func m(): i32 { return 1 }\n" +
                "}\n" +
                "pub func f(): A { return new A() }\n");
            TestHarness.CheckSemanticError("new abstract 类", unit12.Diagnostics,
                "Cannot construct an instance of abstract type 'A'");
        }
    }
}
