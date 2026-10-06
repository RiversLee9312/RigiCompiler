using System.Linq;

namespace RigiCompiler.Tests
{
    public static partial class BinderTests
    {
        // ===== 实例成员（S7c-2：this/实例调用/实例字段/裸名补 this/接口 receiver）=====
        private static void TestInstanceMembers()
        {
            CompilerTestTools.Section("P3 Instance Members");

            var (unit, bodies) = BindUnit(
                "class Counter {\n" +
                "    pub var value: i32\n" +
                "    pub func add(n: i32): i32 {\n" +
                "        return value + n\n" +
                "    }\n" +
                "    pub func bump(): i32 {\n" +
                "        return this.add(1)\n" +
                "    }\n" +
                "}\n" +
                "func read(c: Counter): i32 { return c.value }\n" +
                "func call(c: Counter): i32 { return c.add(2) }\n" +
                "func write(c: Counter) { c.value = 5 }\n");
            CheckNoErrors("无诊断（实例成员）", unit);
            CaseAssertions.Check("裸名实例字段 → this.value",
                BoundDescribe.Body(BodyOf(bodies, "add")),
                "Body(add, [], [Return(Binary(Add, " +
                "InstField(value, This(Counter), i32), Param(n,i32), i32))])");
            CaseAssertions.Check("this 链实例调用",
                BoundDescribe.Body(BodyOf(bodies, "bump")),
                "Body(bump, [], [Return(InstCall(add, This(Counter), [Int(1,i32)], i32))])");
            CaseAssertions.Check("实例字段访问（参数 receiver）",
                BoundDescribe.Body(BodyOf(bodies, "read")),
                "Body(read, [], [Return(InstField(value, Param(c,Counter), i32))])");
            CaseAssertions.Check("实例方法调用（参数 receiver）",
                BoundDescribe.Body(BodyOf(bodies, "call")),
                "Body(call, [], [Return(InstCall(add, Param(c,Counter), [Int(2,i32)], i32))])");
            CaseAssertions.Check("实例字段写入",
                BoundDescribe.Body(BodyOf(bodies, "write")),
                "Body(write, [], [Assign(InstField(value, Param(c,Counter), i32), Int(5,i32))])");
            // 结构性事实：字段/方法符号引用相等（符号图唯一实例）
            var counterType = unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Counter");
            var addReturn = (BoundReturnStatement)BodyOf(bodies, "add").Body.Statements[0];
            var valueAccess = (BoundFieldAccessExpression)
                ((BoundBinaryExpression)addReturn.Value!).Left;
            CaseAssertions.CheckTrue("实例字段符号引用相等",
                ReferenceEquals(valueAccess.Field,
                    counterType.Fields.Single(f => f.Name == "value")));
            var callReturn = (BoundReturnStatement)BodyOf(bodies, "call").Body.Statements[0];
            CaseAssertions.CheckTrue("实例方法符号引用相等",
                ReferenceEquals(((BoundInstanceCallExpression)callReturn.Value!).Method,
                    counterType.Methods.Single(m => m.Name == "add")));

            // 裸名实例方法调用（this 隐式 receiver）
            var (unit2, bodies2) = BindUnit(
                "class C {\n" +
                "    pub func a(): i32 { return b() }\n" +
                "    pub func b(): i32 { return 1 }\n" +
                "}\n");
            CheckNoErrors("无诊断（裸名实例方法）", unit2);
            CaseAssertions.Check("裸名实例方法 → InstCall(this)",
                BoundDescribe.Body(BodyOf(bodies2, "a")),
                "Body(a, [], [Return(InstCall(b, This(C), [], i32))])");

            // 实例链两段（字段的字段）
            var (unit3, bodies3) = BindUnit(
                "class Inner { pub var n: i32 }\n" +
                "class Outer { pub var child: Inner }\n" +
                "func f(o: Outer): i32 { return o.child.n }\n");
            CheckNoErrors("无诊断（两段实例链）", unit3);
            CaseAssertions.Check("o.child.n 链上色",
                BoundDescribe.Body(BodyOf(bodies3, "f")),
                "Body(f, [], [Return(InstField(n, " +
                "InstField(child, Param(o,Outer), Inner), i32))])");

            // 静态上下文诊断
            var (unit4, _) = BindUnit("func f(): i32 { return this }\n");
            CaseAssertions.CheckSemanticError("全局函数 this", unit4.Diagnostics,
                "'this' is not available in a static context");

            var (unit5, _) = BindUnit(
                "class C { pub var x: i32\npub static func f(): i32 { return x } }\n");
            CaseAssertions.CheckSemanticError("static 方法裸名实例字段", unit5.Diagnostics,
                "instance field 'x' requires a receiver");

            var (unit6, _) = BindUnit(
                "class C { pub func m(): i32 { return 1 }\n" +
                "pub static func f(): i32 { return m() } }\n");
            CaseAssertions.CheckSemanticError("static 方法裸名实例方法", unit6.Diagnostics,
                "instance method 'm' requires a receiver");

            // 接口 receiver：接口方法符号引用（分派归 Middleware）
            var (unit7, bodies7) = BindUnit(
                "interface Sized { func size(): i32 }\n" +
                "class Box implements Sized {\n" +
                "    pub override func size(): i32 { return 42 }\n" +
                "}\n" +
                "func f(s: Sized): i32 { return s.size() }\n");
            CheckNoErrors("无诊断（接口 receiver）", unit7);
            CaseAssertions.Check("接口方法调用",
                BoundDescribe.Body(BodyOf(bodies7, "f")),
                "Body(f, [], [Return(InstCall(size, Param(s,Sized), [], i32))])");
            var sizedType = unit7.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Sized");
            var fReturn = (BoundReturnStatement)BodyOf(bodies7, "f").Body.Statements[0];
            CaseAssertions.CheckTrue("接口方法符号引用（接口自身成员）",
                ReferenceEquals(((BoundInstanceCallExpression)fReturn.Value!).Method,
                    sizedType.Methods.Single(m => m.Name == "size")));
        }

        private static void TestSuperCalls()
        {
            CompilerTestTools.Section("P3 Super Calls");
            var (unit, bodies) = BindUnit(
                "open class A {\n" +
                "    pub init(n: i32) {}\n" +
                "    pub open func f(x: i32): i32 { return x }\n" +
                "    pub open func f(x: String): i32 { return 2 }\n" +
                "    pub open func ping() {}\n" +
                "}\n" +
                "open class B: A {\n" +
                "    pub init(n: i32) { super(n) }\n" +
                "    pub override func f(x: i32): i32 { return super(x) }\n" +
                "    pub override func ping() { super() }\n" +
                "}\n" +
                "class C: B {\n" +
                "    pub init(n: i32) {}\n" +
                "    pub override func f(x: i32): i32 { return super(x) }\n" +
                "}\n");
            CheckNoErrors("super 正例", unit);
            var bF = bodies.Where(body => body.Method.Owner?.Name == "B" && body.Method.Name == "f")
                .Single().Body.Statements[0] as BoundReturnStatement;
            CaseAssertions.CheckTrue("super 返回值有独立 Bound 节点",
                bF?.Value is BoundSuperCallExpression { Method.Owner.Name: "A" });
            var bPing = bodies.Where(body => body.Method.Owner?.Name == "B" && body.Method.Name == "ping")
                .Single().Body.Statements[0] as BoundExpressionStatement;
            CaseAssertions.CheckTrue("void super 有独立 Bound 节点",
                bPing?.Expression is BoundSuperCallExpression { IsVoid: true });
            var cF = bodies.Where(body => body.Method.Owner?.Name == "C" && body.Method.Name == "f")
                .Single().Body.Statements[0] as BoundReturnStatement;
            CaseAssertions.CheckTrue("super 仅命中直接基类", cF?.Value is BoundSuperCallExpression
                { Method.Owner.Name: "B" });

            var (invalid, _) = BindUnit(
                "open class A { pub open func f(): i32 { return 1 } }\n" +
                "class B: A {\n" +
                "    pub func plain(): i32 { return super() }\n" +
                "    pub static func stat(): i32 { return super() }\n" +
                "}\n" +
                "func global(): i32 { return super() }\n");
            CaseAssertions.CheckSemanticError("non-override super", invalid.Diagnostics,
                "super(...) is only available in an override method or init body");
            CaseAssertions.CheckSemanticError("static/global super", invalid.Diagnostics,
                "super(...) is only available in an instance member body");
        }

        // ===== 默认构造合成（BindingDriver 阶段 1.8，SYNTAX §9.3）=====
        private static void TestDefaultConstructorSynthesis()
        {
            CompilerTestTools.Section("P3 Default Constructor Synthesis");

            // 链式：无本类初始化器的派生类，基类需要初始化链 → 同样合成
            //（super-only 体）；有本类初始化器的派生合成体 super 先行
            var (unit, bodies) = BindUnit(
                "open class A { pub var x: i32 = 41 }\n" +
                "open class B : A { }\n" +
                "class C : B { pub var z: i32 = 9 }\n");
            CheckNoErrors("链式合成无诊断", unit);
            var bInit = bodies.Single(b => b.Method.Kind == MethodKind.Init
                && b.Method.Owner?.Name == "B");
            CaseAssertions.Check("无本类初始化器的派生合成 super-only 体",
                BoundDescribe.Body(bInit),
                "Body(init, [], [ExprStmt(SuperCall(init, [], void))])");
            var cInit = bodies.Single(b => b.Method.Kind == MethodKind.Init
                && b.Method.Owner?.Name == "C");
            CaseAssertions.Check("派生合成体 super-only（初始值在 ..init.field.z）",
                BoundDescribe.Body(cInit),
                "Body(init, [], [ExprStmt(SuperCall(init, [], void))])");
            // 字段初始值合成方法（新 init 原则）：C 自带 ..init.field.z
            CaseAssertions.CheckTrue("C 合成 ..init.field.z",
                bodies.Any(b => b.Method.Name == "..init.field.z"
                    && b.Method.Owner?.Name == "C"));
            // super 命中直接基类的合成 init（符号引用相等）
            var bType = unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == "B");
            var cSuper = (BoundExpressionStatement)cInit.Body.Statements[0];
            CaseAssertions.CheckTrue("super 命中直接基类合成 init（引用相等）",
                cSuper.Expression is BoundSuperCallExpression super
                && ReferenceEquals(super.Method,
                    bType.Methods.Single(m => m.Kind == MethodKind.Init)));

            // 无可链时不合成：无初始化器且基类无零参 init
            var (unit2, bodies2) = BindUnit(
                "open class P { pub var n: i32 }\n" +
                "class Q : P { }\n");
            CheckNoErrors("无可链无诊断", unit2);
            CaseAssertions.CheckTrue("无可链时不合成 init",
                !unit2.Symbols.GlobalNamespace.Types
                    .Where(t => t.Name == "P" || t.Name == "Q")
                    .SelectMany(t => t.Methods).Any(m => m.Kind == MethodKind.Init)
                && !bodies2.Any(b => b.Method.Kind == MethodKind.Init));

            // 泛型基类：基类定义查零参 init（构造壳不挂方法表），合成体
            // super 先行——隐藏实参与显式 super(...) 同口径（合成 init 无
            // 自身泛型参数，恒空）
            var (unit3, bodies3) = BindUnit(
                "open class B\\<T> { pub var v: i32 = 41 }\n" +
                "class C\\<T> : B\\<T> { pub var w: i32 = 7 }\n");
            CheckNoErrors("泛型基类链式无诊断", unit3);
            var cInit3 = bodies3.Single(b => b.Method.Kind == MethodKind.Init
                && b.Method.Owner?.Name == "C");
            CaseAssertions.CheckTrue("泛型基类合成体 super 先行",
                cInit3.Body.Statements[0] is BoundExpressionStatement
                { Expression: BoundSuperCallExpression { Method.Owner.Name: "B" } });
        }

        // ===== 索引访问（S8c，SYNTAX §13.2：getAtIndex/setAtIndex 绑定、
        // 表达式底座路径、赋值 place 扩展）=====
        private static void TestIndexAccess()
        {
            CompilerTestTools.Section("P3 Index Access");

            // 测试类型与正例用例（自声明运算符，仿 TestInstanceMembers 模式）
            var (unit, bodies) = BindUnit(
                "class Counter {\n" +
                "    pub var value: i32\n" +
                "}\n" +
                "class Bag {\n" +
                "    pub var item: i32\n" +
                "    pub init(_ -> item)\n" +
                "    pub operator getAtIndex(index: i32): i32? { return item }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { item = element }\n" +
                "}\n" +
                "class CounterBag {\n" +
                "    pub var first: Counter\n" +
                "    pub operator getAtIndex(index: i32): Counter? { return first }\n" +
                "}\n" +
                "class Matrix {\n" +
                "    pub var row: Bag\n" +
                "    pub operator getAtIndex(index: i32): Bag? { return row }\n" +
                "}\n" +
                "class Holder {\n" +
                "    pub var bag: Bag\n" +
                "}\n" +
                "func make(): Bag { return new Bag(0) }\n" +
                "func read(b: Bag, i: i32): i32? { return b[i] }\n" +
                "func write(b: Bag, i: i32, x: i32) { b[i] = x }\n" +
                "func segment(h: Holder, i: i32): i32? { return h.bag[i] }\n" +
                "func fieldAfter(cb: CounterBag): i32? { return cb[0]?.value }\n" +
                "func callIndex(i: i32): i32? { return make()[i] }\n");
            CheckNoErrors("无诊断（索引访问正例）", unit);
            CaseAssertions.Check("a[i] 读形态（Q6：Type = i32?）", BoundDescribe.Body(BodyOf(bodies, "read")),
                "Body(read, [], [Return(Index(Param(b,Bag), Param(i,i32), i32?))])");
            CaseAssertions.Check("a[i] = x 写形态", BoundDescribe.Body(BodyOf(bodies, "write")),
                "Body(write, [], [Assign(Index(Param(b,Bag), Param(i,i32), i32), " +
                "Param(x,i32))])");
            CaseAssertions.Check("a.b[i] 段索引", BoundDescribe.Body(BodyOf(bodies, "segment")),
                "Body(segment, [], [Return(Index(InstField(bag, Param(h,Holder), Bag), " +
                "Param(i,i32), i32?))])");
            CaseAssertions.Check("a[i]?.b 索引后安全访问", BoundDescribe.Body(BodyOf(bodies, "fieldAfter")),
                "Body(fieldAfter, [], [Return(SafeAccess(" +
                "Index(Param(cb,CounterBag), Int(0,i32), Counter?), " +
                "InstField(value, SafeReceiver(Counter), i32), i32?))])");
            CaseAssertions.Check("foo()[i] 调用后索引", BoundDescribe.Body(BodyOf(bodies, "callIndex")),
                "Body(callIndex, [], [Return(Index(Call(make, [], Bag), Param(i,i32), i32?))])");
            // 结构性事实：读/写索引的 Operator 符号引用相等（符号图唯一实例）
            var bagType = unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Bag");
            var readIndex = (BoundIndexExpression)((BoundReturnStatement)
                BodyOf(bodies, "read").Body.Statements[0]).Value!;
            CaseAssertions.CheckTrue("读索引 Operator = getAtIndex 符号",
                ReferenceEquals(readIndex.Operator,
                    bagType.Methods.Single(m => m.Name == "getAtIndex")));
            var writeIndex = (BoundIndexExpression)((BoundAssignmentStatement)
                BodyOf(bodies, "write").Body.Statements[0]).Target;
            CaseAssertions.CheckTrue("写索引 Operator = setAtIndex 符号",
                ReferenceEquals(writeIndex.Operator,
                    bagType.Methods.Single(m => m.Name == "setAtIndex")));

            // Q6 负例（读侧 T? 的空安全后果）：
            // a[i] += x ——读侧 i32? 无二元运算，复合赋值索引形态编译错误
            var (unit1b, _) = BindUnit(
                "class Bag {\n" +
                "    pub var item: i32\n" +
                "    pub operator getAtIndex(index: i32): i32? { return item }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { item = element }\n" +
                "}\n" +
                "func bump(b: Bag, i: i32, x: i32) { b[i] += x }\n");
            CaseAssertions.CheckSemanticError("Q6：a[i] += x 读侧可空拒绝", unit1b.Diagnostics,
                "Cannot assign 'Nullable<i32>' to 'i32'");
            // a[i].f ——读出的 T? 上直接成员访问编译错误（须 ?.）
            var (unit1c, _) = BindUnit(
                "class Counter { pub var value: i32 }\n" +
                "class CounterBag {\n" +
                "    pub var first: Counter\n" +
                "    pub operator getAtIndex(index: i32): Counter? { return first }\n" +
                "}\n" +
                "func fieldAfter(cb: CounterBag): i32 { return cb[0].value }\n");
            CaseAssertions.CheckSemanticError("Q6：a[i].f 读侧可空拒绝", unit1c.Diagnostics,
                "cannot be accessed on nullable type");
            // a[i].f = x ——T? 不是可写 place（同一 nullable 成员拒绝通道）
            var (unit1d, _) = BindUnit(
                "class Counter { pub var value: i32 }\n" +
                "class CounterBag {\n" +
                "    pub var first: Counter\n" +
                "    pub operator getAtIndex(index: i32): Counter? { return first }\n" +
                "}\n" +
                "func writeField(cb: CounterBag) { cb[0].value = 1 }\n");
            CaseAssertions.CheckSemanticError("Q6：a[i].f = x 非可写 place", unit1d.Diagnostics,
                "cannot be accessed on nullable type");
            // a[i][j] ——第一重读出 Bag?，第二重索引 nullable 拒绝
            var (unit1e, _) = BindUnit(
                "class Bag {\n" +
                "    pub var item: i32\n" +
                "    pub operator getAtIndex(index: i32): i32? { return item }\n" +
                "}\n" +
                "class Matrix {\n" +
                "    pub var row: Bag\n" +
                "    pub operator getAtIndex(index: i32): Bag? { return row }\n" +
                "}\n" +
                "func twice(m: Matrix, i: i32, j: i32): i32 { return m[i][j] }\n");
            CaseAssertions.CheckSemanticError("Q6：a[i][j] 二重索引 nullable 拒绝", unit1e.Diagnostics,
                "Cannot index nullable type");

            // 表达式底座与调用结果底座
            var (unit2, bodies2) = BindUnit(
                "class Bag {\n" +
                "    pub var item: i32\n" +
                "    pub init(_ -> item)\n" +
                "    pub operator getAtIndex(index: i32): i32? { return item }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { item = element }\n" +
                "}\n" +
                "class Wrap {\n" +
                "    pub var bag: Bag = new Bag(0)\n" +
                "}\n" +
                "func makeWrap(): Wrap { return new Wrap() }\n" +
                "func grouped(a: i32, b: i32): String { return (a + b).toString() }\n" +
                "func callBase(): Bag { return makeWrap().bag }\n" +
                "func newBase(): i32 { return new Wrap().bag.item }\n" +
                "func maybeBag(): Bag? { return null }\n" +
                "func safeBase(): i32? { return maybeBag()?.item }\n");
            CheckNoErrors("无诊断（底座路径）", unit2);
            CaseAssertions.Check("(a+b).c 分组底座", BoundDescribe.Body(BodyOf(bodies2, "grouped")),
                "Body(grouped, [], [Return(InstCall(toString, " +
                "Binary(Add, Param(a,i32), Param(b,i32), i32), [], String))])");
            CaseAssertions.Check("foo().c 调用底座", BoundDescribe.Body(BodyOf(bodies2, "callBase")),
                "Body(callBase, [], [Return(InstField(bag, Call(makeWrap, [], Wrap), Bag))])");
            CaseAssertions.Check("new X().c 构造底座", BoundDescribe.Body(BodyOf(bodies2, "newBase")),
                "Body(newBase, [], [Return(InstField(item, " +
                "InstField(bag, New(Wrap, init, []), Bag), i32))])");
            CaseAssertions.Check("foo()?.bar 调用后 SafeDot",
                BoundDescribe.Body(BodyOf(bodies2, "safeBase")),
                "Body(safeBase, [], [Return(SafeAccess(Call(maybeBag, [], Bag?), " +
                "InstField(item, SafeReceiver(Bag), i32), i32?))])");

            // this[i] 读写（声明了运算符的类的方法体内）
            var (unit3, bodies3) = BindUnit(
                "class SelfIdx {\n" +
                "    pub var v: i32\n" +
                "    pub operator getAtIndex(index: i32): i32? { return v }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { v = element }\n" +
                "    pub func readThis(i: i32): i32? { return this[i] }\n" +
                "    pub func writeThis(i: i32, x: i32) { this[i] = x }\n" +
                "}\n");
            CheckNoErrors("无诊断（this 索引）", unit3);
            CaseAssertions.Check("this[i] 读", BoundDescribe.Body(BodyOf(bodies3, "readThis")),
                "Body(readThis, [], [Return(Index(This(SelfIdx), Param(i,i32), i32?))])");
            CaseAssertions.Check("this[i] = x 写", BoundDescribe.Body(BodyOf(bodies3, "writeThis")),
                "Body(writeThis, [], [Assign(Index(This(SelfIdx), Param(i,i32), i32), " +
                "Param(x,i32))])");

            // ===== 负例 =====
            // 无 getAtIndex 的类型读索引
            var (unit4, _) = BindUnit(
                "class Plain { pub var x: i32 }\n" +
                "func f(p: Plain): i32 { return p[0] }\n");
            CaseAssertions.CheckSemanticError("无 getAtIndex 读索引", unit4.Diagnostics,
                "Type 'Plain' does not define an index operator ('getAtIndex')");

            // 无 setAtIndex 写索引
            var (unit5, _) = BindUnit(
                "class ReadOnly {\n" +
                "    pub operator getAtIndex(index: i32): i32? { return 0 }\n" +
                "}\n" +
                "func g(r: ReadOnly) { r[0] = 1 }\n");
            CaseAssertions.CheckSemanticError("无 setAtIndex 写索引", unit5.Diagnostics,
                "Type 'ReadOnly' does not define an index operator ('setAtIndex')");

            // 多参数索引（§13.2 签名固定单 TIndex）：读形态经实参个数检查
            var (unit6, _) = BindUnit(
                "class Bag {\n" +
                "    pub operator getAtIndex(index: i32): i32? { return 0 }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { }\n" +
                "}\n" +
                "func h(b: Bag, i: i32, j: i32): i32 { return b[i, j] }\n" +
                "func h2(b: Bag, i: i32, j: i32) { b[i, j] = 0 }\n");
            CaseAssertions.CheckSemanticError("多参数索引读形态", unit6.Diagnostics,
                "Too many arguments for 'getAtIndex'");
            CaseAssertions.CheckSemanticError("多参数索引写形态", unit6.Diagnostics,
                "Index access on 'Bag' expects exactly one index argument, got 2");

            // 具名索引实参名字不匹配
            var (unit7, _) = BindUnit(
                "class Bag {\n" +
                "    pub operator getAtIndex(index: i32): i32? { return 0 }\n" +
                "}\n" +
                "func k(b: Bag): i32 { return b[wrong = 1] }\n");
            CaseAssertions.CheckSemanticError("具名索引实参不匹配", unit7.Diagnostics,
                "'getAtIndex' has no parameter named 'wrong'");

            // nullable receiver 索引
            var (unit8, _) = BindUnit(
                "class Bag {\n" +
                "    pub operator getAtIndex(index: i32): i32? { return 0 }\n" +
                "}\n" +
                "func n(b: Bag?): i32 { return b[0] }\n");
            CaseAssertions.CheckSemanticError("nullable receiver 索引", unit8.Diagnostics,
                "Cannot index nullable type");

            // 索引实参类型不匹配
            var (unit9, _) = BindUnit(
                "class Bag {\n" +
                "    pub operator getAtIndex(index: i32): i32? { return 0 }\n" +
                "}\n" +
                "func t(b: Bag, s: String): i32 { return b[s] }\n");
            CaseAssertions.CheckSemanticError("索引实参类型不匹配", unit9.Diagnostics,
                "Cannot pass 'String' as 'i32'");

            // void getAtIndex（Q6：P2 声明形状校验拦截——返回必须 T?）
            var (unit10, _) = BindUnit(
                "class VoidIdx {\n" +
                "    pub operator getAtIndex(index: i32) { }\n" +
                "}\n" +
                "func v(vd: VoidIdx): i32 { return vd[0] }\n");
            CaseAssertions.CheckSemanticError("void getAtIndex", unit10.Diagnostics,
                "Operator 'getAtIndex' must return a nullable type");

            // 同参数个数 getAtIndex 重载（S8d）：按索引实参类型 ranking 命中——
            // 两版本返回类型不同，定型结果即命中版本的见证
            var (unit11, bodies11) = BindUnit(
                "class Multi {\n" +
                "    pub operator getAtIndex(index: i32): i32? { return 0 }\n" +
                "    pub operator getAtIndex(index: String): String? { return \"s\" }\n" +
                "}\n" +
                "func o(m: Multi): i32? { return m[0] }\n" +
                "func p(m: Multi): String? { return m[\"x\"] }\n");
            CheckNoErrors("getAtIndex 重载 ranking（S8d）", unit11);
            CaseAssertions.Check("i32 索引命中 i32 版",
                BoundDescribe.Body(BodyOf(bodies11, "o")),
                "Body(o, [], [Return(Index(Param(m,Multi), Int(0,i32), i32?))])");
            CaseAssertions.Check("String 索引命中 String 版",
                BoundDescribe.Body(BodyOf(bodies11, "p")),
                "Body(p, [], [Return(Index(Param(m,Multi), Str(\"x\",String), String?))])");

            // 索引非值（命名空间）
            var (unit12, _) = BindUnit(
                "namespace stuff\n" +
                "\n" +
                "pub func q(): i32 { return stuff[0] }\n");
            CaseAssertions.CheckSemanticError("ns[0] 索引非值", unit12.Diagnostics,
                "Undefined name: 'stuff'");
        }

        // ===== 容器中间段 Call 后缀后实例链（#20②：ns.make().field /
        // Type.make()[0].value；复用 CallFacility + BindInstanceChain）=====
        private static void TestContainerCallSuffixChain()
        {
            CompilerTestTools.Section("P3 Container Call Suffix Chain");

            var (unit, bodies) = BindUnit(
                "namespace ns\n" +
                "pub class Box {\n" +
                "    pub var field: i32\n" +
                "    pub init() { field = 0 }\n" +
                "}\n" +
                "pub class Nested {\n" +
                "    pub var value: i32\n" +
                "    pub init(v: i32) { value = v }\n" +
                "}\n" +
                "pub class Bag {\n" +
                "    pub var first: Nested\n" +
                "    pub operator getAtIndex(index: i32): Nested? { return first }\n" +
                "    pub init(n: Nested) { first = n }\n" +
                "}\n" +
                "pub func make(): Box { return new Box() }\n" +
                "pub func makeBag(): Bag { return new Bag(new Nested(1)) }\n" +
                "pub func voidMake() { }\n" +
                "class Factory {\n" +
                "    pub static func make(): Box { return new Box() }\n" +
                "}\n",
                "func fieldAfter(): i32 { return ns.make().field }\n" +
                "func indexAfter(): i32? { return ns.makeBag()[0]?.value }\n" +
                "func typeStatic(): i32 { return ns.Factory.make().field }\n" +
                "func assignEnd() { ns.make().field = 9 }\n");
            CheckNoErrors("无诊断（容器 Call 后缀链）", unit);
            CaseAssertions.Check("ns.make().field 调用后字段",
                BoundDescribe.Body(BodyOf(bodies, "fieldAfter")),
                "Body(fieldAfter, [], [Return(InstField(field, Call(make, [], Box), i32))])");
            CaseAssertions.Check("ns.makeBag()[0]?.value 调用后索引/安全访问",
                BoundDescribe.Body(BodyOf(bodies, "indexAfter")),
                "Body(indexAfter, [], [Return(SafeAccess(" +
                "Index(Call(makeBag, [], Bag), Int(0,i32), Nested?), " +
                "InstField(value, SafeReceiver(Nested), i32), i32?))])");
            CaseAssertions.Check("ns.Factory.make().field 类型容器静态调用后字段",
                BoundDescribe.Body(BodyOf(bodies, "typeStatic")),
                "Body(typeStatic, [], [Return(InstField(field, Call(make, [], Box), i32))])");
            CaseAssertions.Check("ns.make().field = 9 全路径末端赋值",
                BoundDescribe.Body(BodyOf(bodies, "assignEnd")),
                "Body(assignEnd, [], [Assign(InstField(field, Call(make, [], Box), i32), " +
                "Int(9,i32))])");

            // 底座行为保留：裸名 foo().field / makeBag()[i]
            var (unitBase, bodiesBase) = BindUnit(
                "class Box {\n" +
                "    pub var field: i32\n" +
                "    pub init() { field = 0 }\n" +
                "}\n" +
                "class Nested {\n" +
                "    pub var value: i32\n" +
                "    pub init(v: i32) { value = v }\n" +
                "}\n" +
                "class Bag {\n" +
                "    pub var first: Nested\n" +
                "    pub operator getAtIndex(index: i32): Nested? { return first }\n" +
                "    pub init(n: Nested) { first = n }\n" +
                "}\n" +
                "func make(): Box { return new Box() }\n" +
                "func makeBag(): Bag { return new Bag(new Nested(1)) }\n" +
                "func bareField(): i32 { return make().field }\n" +
                "func bareIndex(): i32? { return makeBag()[0]?.value }\n");
            CheckNoErrors("无诊断（裸名调用底座保留）", unitBase);
            CaseAssertions.Check("make().field 底座保留",
                BoundDescribe.Body(BodyOf(bodiesBase, "bareField")),
                "Body(bareField, [], [Return(InstField(field, Call(make, [], Box), i32))])");
            CaseAssertions.Check("makeBag()[0]?.value 底座保留",
                BoundDescribe.Body(BodyOf(bodiesBase, "bareIndex")),
                "Body(bareIndex, [], [Return(SafeAccess(" +
                "Index(Call(makeBag, [], Bag), Int(0,i32), Nested?), " +
                "InstField(value, SafeReceiver(Nested), i32), i32?))])");

            // ===== 负例 =====
            var (unitNeg1, _) = BindUnit(
                "namespace ns\n" +
                "pub func make(): i32 { return 1 }\n",
                "func f(): i32 { return ns.make }\n");
            CaseAssertions.CheckSemanticError("无后缀方法不能作为值", unitNeg1.Diagnostics,
                "Method 'ns.make' cannot be used as a value");

            var (unitNeg2, _) = BindUnit(
                "namespace ns\n" +
                "class Box {\n" +
                "    pub var field: i32\n" +
                "    pub init() { field = 0 }\n" +
                "}\n" +
                "pub func make(): Box { return new Box() }\n",
                "func f(): i32 { return ns.make().nope }\n");
            CaseAssertions.CheckSemanticError("调用后未知成员", unitNeg2.Diagnostics,
                "Undefined member 'nope'");

            var (unitNeg3, _) = BindUnit(
                "namespace ns\n" +
                "pub func voidMake() { }\n",
                "func f() { var x = ns.voidMake().field }\n");
            CaseAssertions.CheckSemanticError("void 调用不能作 receiver", unitNeg3.Diagnostics,
                "has no result (void) and cannot be used as a value");
        }

        // ===== 泛型基类成员查找（构造类型 BaseType 回填的 P3 端到端验证：
        // Sub\<i32\> 在参数类型解析时驻留（定义基类尚未解析），修复前基类链
        // 断在默认 Object 快照/停在未代入快照 → 误报未定义成员/类型不匹配）=====
        private static void TestGenericBaseClassMemberLookup()
        {
            CompilerTestTools.Section("P3 Generic Base Class Member Lookup");

            // 泛型基类方法两跳：s.foo() 经 Sub\<i32\> → Base\<i32\> 命中
            var (unit, bodies) = BindUnit(
                "pub open class Base\\<T> {\n" +
                "    pub func foo(): i32 { return 1 }\n" +
                "}\n" +
                "pub class Sub\\<T> : Base\\<T> { }\n" +
                "func bar(s: Sub\\<i32>): i32 { return s.foo() }\n");
            CheckNoErrors("无诊断（泛型基类方法查找）", unit);
            CaseAssertions.Check("s.foo() 实例调用上色",
                BoundDescribe.Body(BodyOf(bodies, "bar")),
                "Body(bar, [], [Return(InstCall(foo, Param(s,Sub<i32>), [], i32))])");

            // 泛型字段两跳代入：s.x 类型为 i32（而非定义级 T）
            var (unit2, bodies2) = BindUnit(
                "pub open class Base\\<T> {\n" +
                "    pub var x: T\n" +
                "    pub init(_ -> x) { }\n" +
                "}\n" +
                "pub class Sub\\<T> : Base\\<T> { }\n" +
                "func bar(s: Sub\\<i32>) { var y: i32 = s.x }\n");
            CheckNoErrors("无诊断（泛型字段两跳代入）", unit2);
            CaseAssertions.Check("s.x 定型 i32",
                BoundDescribe.Body(BodyOf(bodies2, "bar")),
                "Body(bar, [y: i32], [Decl(y, i32, = InstField(x, Param(s,Sub<i32>), i32))])");
        }
    }
}
