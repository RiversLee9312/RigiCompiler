using System.Linq;

namespace LatteCompiler.Tests
{
    public static partial class BinderTests
    {
        // ===== 实例成员（S7c-2：this/实例调用/实例字段/裸名补 this/接口 receiver）=====
        private static void TestInstanceMembers()
        {
            TestHarness.Section("P3 Instance Members");

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
            TestHarness.Check("裸名实例字段 → this.value",
                BoundDescribe.Body(BodyOf(bodies, "add")),
                "Body(add, [], [Return(Binary(Add, " +
                "InstField(value, This(Counter), i32), Param(n,i32), i32))])");
            TestHarness.Check("this 链实例调用",
                BoundDescribe.Body(BodyOf(bodies, "bump")),
                "Body(bump, [], [Return(InstCall(add, This(Counter), [Int(1,i32)], i32))])");
            TestHarness.Check("实例字段访问（参数 receiver）",
                BoundDescribe.Body(BodyOf(bodies, "read")),
                "Body(read, [], [Return(InstField(value, Param(c,Counter), i32))])");
            TestHarness.Check("实例方法调用（参数 receiver）",
                BoundDescribe.Body(BodyOf(bodies, "call")),
                "Body(call, [], [Return(InstCall(add, Param(c,Counter), [Int(2,i32)], i32))])");
            TestHarness.Check("实例字段写入",
                BoundDescribe.Body(BodyOf(bodies, "write")),
                "Body(write, [], [Assign(InstField(value, Param(c,Counter), i32), Int(5,i32))])");
            // 结构性事实：字段/方法符号引用相等（符号图唯一实例）
            var counterType = unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Counter");
            var addReturn = (BoundReturnStatement)BodyOf(bodies, "add").Body.Statements[0];
            var valueAccess = (BoundFieldAccessExpression)
                ((BoundBinaryExpression)addReturn.Value!).Left;
            TestHarness.CheckTrue("实例字段符号引用相等",
                ReferenceEquals(valueAccess.Field,
                    counterType.Fields.Single(f => f.Name == "value")));
            var callReturn = (BoundReturnStatement)BodyOf(bodies, "call").Body.Statements[0];
            TestHarness.CheckTrue("实例方法符号引用相等",
                ReferenceEquals(((BoundInstanceCallExpression)callReturn.Value!).Method,
                    counterType.Methods.Single(m => m.Name == "add")));

            // 裸名实例方法调用（this 隐式 receiver）
            var (unit2, bodies2) = BindUnit(
                "class C {\n" +
                "    pub func a(): i32 { return b() }\n" +
                "    pub func b(): i32 { return 1 }\n" +
                "}\n");
            CheckNoErrors("无诊断（裸名实例方法）", unit2);
            TestHarness.Check("裸名实例方法 → InstCall(this)",
                BoundDescribe.Body(BodyOf(bodies2, "a")),
                "Body(a, [], [Return(InstCall(b, This(C), [], i32))])");

            // 实例链两段（字段的字段）
            var (unit3, bodies3) = BindUnit(
                "class Inner { pub var n: i32 }\n" +
                "class Outer { pub var child: Inner }\n" +
                "func f(o: Outer): i32 { return o.child.n }\n");
            CheckNoErrors("无诊断（两段实例链）", unit3);
            TestHarness.Check("o.child.n 链上色",
                BoundDescribe.Body(BodyOf(bodies3, "f")),
                "Body(f, [], [Return(InstField(n, " +
                "InstField(child, Param(o,Outer), Inner), i32))])");

            // 静态上下文诊断
            var (unit4, _) = BindUnit("func f(): i32 { return this }\n");
            TestHarness.CheckSemanticError("全局函数 this", unit4.Diagnostics,
                "'this' is not available in a static context");

            var (unit5, _) = BindUnit(
                "class C { pub var x: i32\npub static func f(): i32 { return x } }\n");
            TestHarness.CheckSemanticError("static 方法裸名实例字段", unit5.Diagnostics,
                "instance field 'x' requires a receiver");

            var (unit6, _) = BindUnit(
                "class C { pub func m(): i32 { return 1 }\n" +
                "pub static func f(): i32 { return m() } }\n");
            TestHarness.CheckSemanticError("static 方法裸名实例方法", unit6.Diagnostics,
                "instance method 'm' requires a receiver");

            // 接口 receiver：接口方法符号引用（分派归 Middleware）
            var (unit7, bodies7) = BindUnit(
                "interface Sized { func size(): i32 }\n" +
                "class Box implements Sized {\n" +
                "    pub override func size(): i32 { return 42 }\n" +
                "}\n" +
                "func f(s: Sized): i32 { return s.size() }\n");
            CheckNoErrors("无诊断（接口 receiver）", unit7);
            TestHarness.Check("接口方法调用",
                BoundDescribe.Body(BodyOf(bodies7, "f")),
                "Body(f, [], [Return(InstCall(size, Param(s,Sized), [], i32))])");
            var sizedType = unit7.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Sized");
            var fReturn = (BoundReturnStatement)BodyOf(bodies7, "f").Body.Statements[0];
            TestHarness.CheckTrue("接口方法符号引用（接口自身成员）",
                ReferenceEquals(((BoundInstanceCallExpression)fReturn.Value!).Method,
                    sizedType.Methods.Single(m => m.Name == "size")));
        }

        // ===== 索引访问（S8c，SYNTAX §13.2：getAtIndex/setAtIndex 绑定、
        // 表达式底座路径、赋值 place 扩展）=====
        private static void TestIndexAccess()
        {
            TestHarness.Section("P3 Index Access");

            // 测试类型与正例用例（自声明运算符，仿 TestInstanceMembers 模式）
            var (unit, bodies) = BindUnit(
                "class Counter {\n" +
                "    pub var value: i32\n" +
                "}\n" +
                "class Bag {\n" +
                "    pub var item: i32\n" +
                "    pub operator getAtIndex(index: i32): i32 { return item }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { item = element }\n" +
                "}\n" +
                "class CounterBag {\n" +
                "    pub var first: Counter\n" +
                "    pub operator getAtIndex(index: i32): Counter { return first }\n" +
                "}\n" +
                "class Matrix {\n" +
                "    pub var row: Bag\n" +
                "    pub operator getAtIndex(index: i32): Bag { return row }\n" +
                "}\n" +
                "class Holder {\n" +
                "    pub var bag: Bag\n" +
                "}\n" +
                "func make(): Bag { return new Bag() }\n" +
                "func read(b: Bag, i: i32): i32 { return b[i] }\n" +
                "func write(b: Bag, i: i32, x: i32) { b[i] = x }\n" +
                "func bump(b: Bag, i: i32, x: i32) { b[i] += x }\n" +
                "func segment(h: Holder, i: i32): i32 { return h.bag[i] }\n" +
                "func fieldAfter(cb: CounterBag): i32 { return cb[0].value }\n" +
                "func callIndex(i: i32): i32 { return make()[i] }\n" +
                "func twice(m: Matrix, i: i32, j: i32): i32 { return m[i][j] }\n");
            CheckNoErrors("无诊断（索引访问正例）", unit);
            TestHarness.Check("a[i] 读形态", BoundDescribe.Body(BodyOf(bodies, "read")),
                "Body(read, [], [Return(Index(Param(b,Bag), Param(i,i32), i32))])");
            TestHarness.Check("a[i] = x 写形态", BoundDescribe.Body(BodyOf(bodies, "write")),
                "Body(write, [], [Assign(Index(Param(b,Bag), Param(i,i32), i32), " +
                "Param(x,i32))])");
            TestHarness.Check("a[i] += x 复合形态", BoundDescribe.Body(BodyOf(bodies, "bump")),
                "Body(bump, [], [ExprStmt(CompoundAssign(Add, " +
                "Index(Param(b,Bag), Param(i,i32), i32), Param(x,i32), i32))])");
            TestHarness.Check("a.b[i] 段索引", BoundDescribe.Body(BodyOf(bodies, "segment")),
                "Body(segment, [], [Return(Index(InstField(bag, Param(h,Holder), Bag), " +
                "Param(i,i32), i32))])");
            TestHarness.Check("a[i].b 索引后字段", BoundDescribe.Body(BodyOf(bodies, "fieldAfter")),
                "Body(fieldAfter, [], [Return(InstField(value, " +
                "Index(Param(cb,CounterBag), Int(0,i32), Counter), i32))])");
            TestHarness.Check("foo()[i] 调用后索引", BoundDescribe.Body(BodyOf(bodies, "callIndex")),
                "Body(callIndex, [], [Return(Index(Call(make, [], Bag), Param(i,i32), i32))])");
            TestHarness.Check("a[i][j] 双重索引", BoundDescribe.Body(BodyOf(bodies, "twice")),
                "Body(twice, [], [Return(Index(Index(Param(m,Matrix), Param(i,i32), Bag), " +
                "Param(j,i32), i32))])");
            // 结构性事实：读/写索引的 Operator 符号引用相等（符号图唯一实例）
            var bagType = unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Bag");
            var readIndex = (BoundIndexExpression)((BoundReturnStatement)
                BodyOf(bodies, "read").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("读索引 Operator = getAtIndex 符号",
                ReferenceEquals(readIndex.Operator,
                    bagType.Methods.Single(m => m.Name == "getAtIndex")));
            var writeIndex = (BoundIndexExpression)((BoundAssignmentStatement)
                BodyOf(bodies, "write").Body.Statements[0]).Target;
            TestHarness.CheckTrue("写索引 Operator = setAtIndex 符号",
                ReferenceEquals(writeIndex.Operator,
                    bagType.Methods.Single(m => m.Name == "setAtIndex")));

            // 表达式底座与调用结果底座
            var (unit2, bodies2) = BindUnit(
                "class Bag {\n" +
                "    pub var item: i32\n" +
                "    pub operator getAtIndex(index: i32): i32 { return item }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { item = element }\n" +
                "}\n" +
                "class Wrap {\n" +
                "    pub var bag: Bag\n" +
                "}\n" +
                "func makeWrap(): Wrap { return new Wrap() }\n" +
                "func grouped(a: i32, b: i32): String { return (a + b).toString() }\n" +
                "func callBase(): Bag { return makeWrap().bag }\n" +
                "func newBase(): i32 { return new Wrap().bag.item }\n" +
                "func maybeBag(): Bag? { return null }\n" +
                "func safeBase(): i32? { return maybeBag()?.item }\n");
            CheckNoErrors("无诊断（底座路径）", unit2);
            TestHarness.Check("(a+b).c 分组底座", BoundDescribe.Body(BodyOf(bodies2, "grouped")),
                "Body(grouped, [], [Return(InstCall(toString, " +
                "Binary(Add, Param(a,i32), Param(b,i32), i32), [], String))])");
            TestHarness.Check("foo().c 调用底座", BoundDescribe.Body(BodyOf(bodies2, "callBase")),
                "Body(callBase, [], [Return(InstField(bag, Call(makeWrap, [], Wrap), Bag))])");
            TestHarness.Check("new X().c 构造底座", BoundDescribe.Body(BodyOf(bodies2, "newBase")),
                "Body(newBase, [], [Return(InstField(item, " +
                "InstField(bag, New(Wrap, []), Bag), i32))])");
            TestHarness.Check("foo()?.bar 调用后 SafeDot",
                BoundDescribe.Body(BodyOf(bodies2, "safeBase")),
                "Body(safeBase, [], [Return(SafeAccess(Call(maybeBag, [], Bag?), " +
                "InstField(item, SafeReceiver(Bag), i32), i32?))])");

            // this[i] 读写（声明了运算符的类的方法体内）
            var (unit3, bodies3) = BindUnit(
                "class SelfIdx {\n" +
                "    pub var v: i32\n" +
                "    pub operator getAtIndex(index: i32): i32 { return v }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { v = element }\n" +
                "    pub func readThis(i: i32): i32 { return this[i] }\n" +
                "    pub func writeThis(i: i32, x: i32) { this[i] = x }\n" +
                "}\n");
            CheckNoErrors("无诊断（this 索引）", unit3);
            TestHarness.Check("this[i] 读", BoundDescribe.Body(BodyOf(bodies3, "readThis")),
                "Body(readThis, [], [Return(Index(This(SelfIdx), Param(i,i32), i32))])");
            TestHarness.Check("this[i] = x 写", BoundDescribe.Body(BodyOf(bodies3, "writeThis")),
                "Body(writeThis, [], [Assign(Index(This(SelfIdx), Param(i,i32), i32), " +
                "Param(x,i32))])");

            // ===== 负例 =====
            // 无 getAtIndex 的类型读索引
            var (unit4, _) = BindUnit(
                "class Plain { pub var x: i32 }\n" +
                "func f(p: Plain): i32 { return p[0] }\n");
            TestHarness.CheckSemanticError("无 getAtIndex 读索引", unit4.Diagnostics,
                "Type 'Plain' does not define an index operator ('getAtIndex')");

            // 无 setAtIndex 写索引
            var (unit5, _) = BindUnit(
                "class ReadOnly {\n" +
                "    pub operator getAtIndex(index: i32): i32 { return 0 }\n" +
                "}\n" +
                "func g(r: ReadOnly) { r[0] = 1 }\n");
            TestHarness.CheckSemanticError("无 setAtIndex 写索引", unit5.Diagnostics,
                "Type 'ReadOnly' does not define an index operator ('setAtIndex')");

            // 多参数索引（§13.2 签名固定单 TIndex）：读形态经实参个数检查
            var (unit6, _) = BindUnit(
                "class Bag {\n" +
                "    pub operator getAtIndex(index: i32): i32 { return 0 }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { }\n" +
                "}\n" +
                "func h(b: Bag, i: i32, j: i32): i32 { return b[i, j] }\n" +
                "func h2(b: Bag, i: i32, j: i32) { b[i, j] = 0 }\n");
            TestHarness.CheckSemanticError("多参数索引读形态", unit6.Diagnostics,
                "Too many arguments for 'getAtIndex'");
            TestHarness.CheckSemanticError("多参数索引写形态", unit6.Diagnostics,
                "Index access on 'Bag' expects exactly one index argument, got 2");

            // 具名索引实参名字不匹配
            var (unit7, _) = BindUnit(
                "class Bag {\n" +
                "    pub operator getAtIndex(index: i32): i32 { return 0 }\n" +
                "}\n" +
                "func k(b: Bag): i32 { return b[wrong = 1] }\n");
            TestHarness.CheckSemanticError("具名索引实参不匹配", unit7.Diagnostics,
                "'getAtIndex' has no parameter named 'wrong'");

            // nullable receiver 索引
            var (unit8, _) = BindUnit(
                "class Bag {\n" +
                "    pub operator getAtIndex(index: i32): i32 { return 0 }\n" +
                "}\n" +
                "func n(b: Bag?): i32 { return b[0] }\n");
            TestHarness.CheckSemanticError("nullable receiver 索引", unit8.Diagnostics,
                "Cannot index nullable type");

            // 索引实参类型不匹配
            var (unit9, _) = BindUnit(
                "class Bag {\n" +
                "    pub operator getAtIndex(index: i32): i32 { return 0 }\n" +
                "}\n" +
                "func t(b: Bag, s: String): i32 { return b[s] }\n");
            TestHarness.CheckSemanticError("索引实参类型不匹配", unit9.Diagnostics,
                "Cannot pass 'String' as 'i32'");

            // void getAtIndex 读索引
            var (unit10, _) = BindUnit(
                "class VoidIdx {\n" +
                "    pub operator getAtIndex(index: i32) { }\n" +
                "}\n" +
                "func v(vd: VoidIdx): i32 { return vd[0] }\n");
            TestHarness.CheckSemanticError("void getAtIndex", unit10.Diagnostics,
                "Method 'getAtIndex' has no result (void) and cannot be used as a value");

            // 同参数个数 getAtIndex 重载（S8d）：按索引实参类型 ranking 命中——
            // 两版本返回类型不同，定型结果即命中版本的见证
            var (unit11, bodies11) = BindUnit(
                "class Multi {\n" +
                "    pub operator getAtIndex(index: i32): i32 { return 0 }\n" +
                "    pub operator getAtIndex(index: String): String { return \"s\" }\n" +
                "}\n" +
                "func o(m: Multi): i32 { return m[0] }\n" +
                "func p(m: Multi): String { return m[\"x\"] }\n");
            CheckNoErrors("getAtIndex 重载 ranking（S8d）", unit11);
            TestHarness.Check("i32 索引命中 i32 版",
                BoundDescribe.Body(BodyOf(bodies11, "o")),
                "Body(o, [], [Return(Index(Param(m,Multi), Int(0,i32), i32))])");
            TestHarness.Check("String 索引命中 String 版",
                BoundDescribe.Body(BodyOf(bodies11, "p")),
                "Body(p, [], [Return(Index(Param(m,Multi), Str(\"x\",String), String))])");

            // 索引非值（命名空间）
            var (unit12, _) = BindUnit(
                "namespace stuff\n" +
                "\n" +
                "pub func q(): i32 { return stuff[0] }\n");
            TestHarness.CheckSemanticError("ns[0] 索引非值", unit12.Diagnostics,
                "Undefined name: 'stuff'");
        }
    }
}
