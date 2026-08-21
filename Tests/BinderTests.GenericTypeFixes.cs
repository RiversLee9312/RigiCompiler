using System.Linq;

namespace RigiCompiler.Tests
{
    // P3 泛型类型系统修复组：this 自身具化、嵌套构造字段代换、
    // 字段访问路径型参代入、for-in 协议按具化接口判定、协变 init 豁免。
    public static partial class BinderTests
    {
        private static void TestGenericTypeFixes()
        {
            TestThisSelfConstructed();
            TestNestedGenericFieldIdentity();
            TestGenericFieldSubstitution();
            TestForEachConstructedInterface();
            TestCovariantInitUsage();
            TestConstructedTypeStaticMembers();
            TestGenericNullableFixes();
        }

        // ===== g8/g10：泛型参数可空（Nullable<T>，T 为型参）——T → T? 装箱
        // 视图可赋值、if?/?. 认 Nullable<GP>、反例不误放 =====
        private static void TestGenericNullableFixes()
        {
            TestHarness.Section("P3 GenericTypeFixes: 泛型参数可空（g8/g10）");

            // 1. g8：T → T? return（无约束泛型参数的 Nullable 装箱视图）
            var (wrap, wrapBodies) = BindUnit(
                "pub func wrapNull\\<T>(x: T): T? { return x }\n");
            CheckNoErrors("g8 无诊断（T → T? return）", wrap);
            var wrapMethod = BodyOf(wrapBodies, "wrapNull").Method;
            var wrapT = wrapMethod.GenericParameters.Single(p => p.Name == "T");
            var wrapReturn = (BoundReturnStatement)
                BodyOf(wrapBodies, "wrapNull").Body.Statements[0];
            TestHarness.CheckTrue("g8 return 值类型即 T（引用相等）",
                ReferenceEquals(wrapReturn.Value!.Type, wrapT));
            TestHarness.CheckTrue("g8 返回类型 Nullable<T> 内层即 T（引用相等）",
                wrapMethod.ReturnType is TypeSymbol { ConstructedFrom: not null,
                    TypeArguments: { } wrapArgs }
                && ReferenceEquals(wrapArgs[0], wrapT));

            // 2. g10：if? 解包 GP 可空——左操作数 Nullable<T>（T 为型参），
            //    结果类型 T
            var (unwrap, unwrapBodies) = BindUnit(
                "pub func unwrap\\<T>(x: T?, fallback: T): T { return (x if? fallback) }\n");
            CheckNoErrors("g10 无诊断（if? 解包 GP 可空）", unwrap);
            var unwrapT = BodyOf(unwrapBodies, "unwrap").Method
                .GenericParameters.Single(p => p.Name == "T");
            var fallbackReturn = (BoundReturnStatement)
                BodyOf(unwrapBodies, "unwrap").Body.Statements[0];
            TestHarness.CheckTrue("g10 if? 绑定形态与定型（Type = T）",
                fallbackReturn.Value is BoundNullFallbackExpression nullFallback
                && ReferenceEquals(nullFallback.Type, unwrapT)
                && nullFallback.Left.Type is TypeSymbol { ConstructedFrom: not null,
                    TypeArguments: { } fallbackArgs }
                && ReferenceEquals(fallbackArgs[0], unwrapT));

            // 3. g10：`?.` 于 GP 可空——段在非空 T 上绑定（有效成员类型
            //    Any.toString），结果包 Nullable<String>
            var (safe, safeBodies) = BindUnit(
                "pub func nameOf\\<T>(x: T?): String? { return x?.toString() }\n");
            CheckNoErrors("g10 无诊断（?. 于 GP 可空）", safe);
            var safeT = BodyOf(safeBodies, "nameOf").Method
                .GenericParameters.Single(p => p.Name == "T");
            var safeReturn = (BoundReturnStatement)
                BodyOf(safeBodies, "nameOf").Body.Statements[0];
            TestHarness.CheckTrue("g10 ?. 绑定形态与定型（结果 String?）",
                safeReturn.Value is BoundSafeAccessExpression safeAccess
                && ReferenceEquals(safeAccess.Placeholder.Type, safeT)
                && safeAccess.Type is TypeSymbol { ConstructedFrom: not null,
                    TypeArguments: { } safeArgs }
                && ReferenceEquals(safeArgs[0], safe.Symbols.Bootstrap.String));

            // 4. 端到端 Binder 形态：unwrap\<i32>(null, -1)——null 实参定型
            //    Nullable<i32>，返回 i32
            var (e2e, e2eBodies) = BindUnit(
                "pub func unwrap\\<T>(x: T?, fallback: T): T { return (x if? fallback) }\n" +
                "pub func main(): i32 { return unwrap\\<i32>(null, -1) }\n");
            CheckNoErrors("g8/g10 无诊断（unwrap\\<i32>(null, -1) 端到端）", e2e);
            var mainReturn = (BoundReturnStatement)
                BodyOf(e2eBodies, "main").Body.Statements[0];
            TestHarness.CheckTrue("g8/g10 端到端调用定型 i32",
                mainReturn.Value is BoundCallExpression call
                && call.Method.Name == "unwrap"
                && call.TypeArguments.Count == 1
                && ReferenceEquals(call.TypeArguments[0], e2e.Symbols.Bootstrap.Int32)
                && ReferenceEquals(call.Type, e2e.Symbols.Bootstrap.Int32));

            // 5. 反例：无约束 T → Nullable<U>（U 是另一不同型参）不误放
            var (bad, _) = BindUnit(
                "pub func bad\\<T, U>(x: T): U? { return x }\n");
            TestHarness.CheckSemanticError("g8 反例：无约束 T → Nullable\\<U\\> 仍报错",
                bad.Diagnostics, "Cannot return 'T' from function returning 'Nullable<U>'");

            // 6. 具体类型可空回归：i32 → i32? 装箱视图与 if? 依旧
            var (concrete, concreteBodies) = BindUnit(
                "pub func f(): i32 {\n" +
                "    var x: i32? = 3\n" +
                "    return (x if? -1)\n" +
                "}\n");
            CheckNoErrors("g8/g10 回归：具体类型可空行为不变", concrete);
            TestHarness.CheckTrue("g8/g10 回归：if? 定型 i32",
                ((BoundReturnStatement)BodyOf(concreteBodies, "f").Body.Statements[1])
                .Value is BoundNullFallbackExpression concreteFallback
                && ReferenceEquals(concreteFallback.Type, concrete.Symbols.Bootstrap.Int32));
        }

        // ===== g7：构造类型上的静态成员绑定（Box\<i32>.wrap(8) / 静态字段
        // 读写 / 方法自有泛型实参并存 / 宿主 T 代入成员签名）=====
        private static void TestConstructedTypeStaticMembers()
        {
            TestHarness.Section("P3 GenericTypeFixes: 构造类型静态成员（g7）");

            // 1. 构造类型静态方法调用：头段实参是类型构造实参；宿主 T 代入
            // 成员签名——wrap(x: T): Box\<T> 在 Box\<i32> 上 → x: i32、
            // 返回 Box\<i32>（可续访 .v 得 i32）
            var (unit, bodies) = BindUnit(
                "pub class Box\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "    pub static func wrap(x: T): Box\\<T> { return new Box\\<T>(x) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b = Box\\<i32>.wrap(8)\n" +
                "    return b.v\n" +
                "}\n");
            CheckNoErrors("g7 无诊断（Box\\<i32>.wrap(8)）", unit);
            var wrapCall = (BoundCallExpression)
                ((BoundLocalDeclarationStatement)BodyOf(bodies, "main").Body.Statements[0])
                .Initializer!;
            TestHarness.CheckTrue("g7 wrap 静态调用 + 宿主泛型实参 i32 + 返回 Box<i32>",
                wrapCall.Method.Name == "wrap" && wrapCall.Method.IsStatic
                && wrapCall.TypeArguments.Count == 0
                && wrapCall.HostTypeArguments.Count == 1
                && ReferenceEquals(wrapCall.HostTypeArguments[0], unit.Symbols.Bootstrap.Int32)
                && wrapCall.Type is TypeSymbol { ConstructedFrom: not null } wrapType
                && wrapType.ConstructedFrom.Name == "Box"
                && ReferenceEquals(wrapType.TypeArguments![0], unit.Symbols.Bootstrap.Int32));
            var returnV = (BoundReturnStatement)BodyOf(bodies, "main").Body.Statements[1];
            TestHarness.CheckTrue("g7 wrap 返回值续访 .v 代入 i32",
                returnV.Value is BoundFieldAccessExpression { Field.Name: "v" } vAccess
                && ReferenceEquals(vAccess.Type, unit.Symbols.Bootstrap.Int32));

            // 2. 类型构造实参与方法自有泛型实参并存：
            // Box\<i32>.pick\<String>(5, "hi")——头段 i32 归容器、末段
            // String 归方法，签名双层代入（x: T→i32，返回 U→String）
            var (combo, comboBodies) = BindUnit(
                "pub class Box\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "    pub static func pick\\<U>(x: T, u: U): U { return u }\n" +
                "}\n" +
                "pub func main(): String {\n" +
                "    return Box\\<i32>.pick\\<String>(5, \"hi\")\n" +
                "}\n");
            CheckNoErrors("g7 无诊断（Box\\<i32>.pick\\<String> 两处实参并存）", combo);
            var pickCall = (BoundCallExpression)((BoundReturnStatement)
                BodyOf(comboBodies, "main").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("g7 pick 宿主实参与方法实参分流",
                pickCall.Method.Name == "pick"
                && pickCall.HostTypeArguments.Count == 1
                && ReferenceEquals(pickCall.HostTypeArguments[0],
                    combo.Symbols.Bootstrap.Int32)
                && pickCall.TypeArguments.Count == 1
                && ReferenceEquals(pickCall.TypeArguments[0],
                    combo.Symbols.Bootstrap.String)
                && ReferenceEquals(pickCall.Type, combo.Symbols.Bootstrap.String));

            // 3. 静态字段读写：Box\<i32>.zero 的声明类型 T 按容器构造实参
            // 代入为 i32（写 place 与读路径同型）
            var (fields, fieldBodies) = BindUnit(
                "pub class Box\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "    pub static var zero: T\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Box\\<i32>.zero = 41\n" +
                "    return Box\\<i32>.zero\n" +
                "}\n");
            CheckNoErrors("g7 无诊断（Box\\<i32>.zero 读写）", fields);
            var zeroAssign = (BoundAssignmentStatement)
                BodyOf(fieldBodies, "main").Body.Statements[0];
            var zeroReturn = (BoundReturnStatement)
                BodyOf(fieldBodies, "main").Body.Statements[1];
            TestHarness.CheckTrue("g7 静态字段写 place 类型代入 i32",
                zeroAssign.Target is BoundFieldReferenceExpression { Field.Name: "zero" } zeroWrite
                && zeroWrite.Field.IsStatic
                && ReferenceEquals(zeroWrite.Type, fields.Symbols.Bootstrap.Int32));
            TestHarness.CheckTrue("g7 静态字段读类型代入 i32",
                zeroReturn.Value is BoundFieldReferenceExpression { Field.Name: "zero" } zeroRead
                && ReferenceEquals(zeroRead.Type, fields.Symbols.Bootstrap.Int32));

            // 4. 语句位置：void 静态调用落 BoundCallStatement 且携带宿主
            // 泛型实参（P4 发射 .generic.T 依据）
            var (stmt, stmtBodies) = BindUnit(
                "pub class Box\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "    pub static func touch(x: T) { }\n" +
                "}\n" +
                "pub func main() {\n" +
                "    Box\\<i32>.touch(1)\n" +
                "}\n");
            CheckNoErrors("g7 无诊断（void 静态调用语句）", stmt);
            TestHarness.CheckTrue("g7 语句位置 BoundCallStatement 带宿主实参",
                BodyOf(stmtBodies, "main").Body.Statements[0] is BoundCallStatement touchStmt
                && touchStmt.Method.Name == "touch"
                && touchStmt.HostTypeArguments.Count == 1
                && ReferenceEquals(touchStmt.HostTypeArguments[0],
                    stmt.Symbols.Bootstrap.Int32));

            // 5. 反例：末段实参仍按方法泛型实参口径——wrap 非泛型方法，
            // Box.wrap\<i32>(8) 保持容器元数诊断（不落进方法实参曲解）
            var (bad, _) = BindUnit(
                "pub class Box\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "    pub static func wrap(x: T): Box\\<T> { return new Box\\<T>(x) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b = Box.wrap\\<i32>(8)\n" +
                "    return b.v\n" +
                "}\n");
            TestHarness.CheckSemanticError("g7 Box.wrap\\<i32>(8) 保持容器元数诊断",
                bad.Diagnostics, "'Box' expects 1 type argument(s), got 0");
        }

        // ===== A7：泛型类体内 this 定型为自身具化 Box\<T\> =====
        private static void TestThisSelfConstructed()
        {
            TestHarness.Section("P3 GenericTypeFixes: this 自身具化");

            var (unit, bodies) = BindUnit(
                "pub class Box\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "    pub func me(): Box\\<T> { return this }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box\\<i32>(7)\n" +
                "    return b.me().item\n" +
                "}\n");
            CheckNoErrors("无诊断（this 返回 Box<T>）", unit);
            TestHarness.CheckTrue("this 定型为 Box<T>",
                BoundDescribe.Body(BodyOf(bodies, "me")).Contains("This(Box<T>)"));

            // 嵌套泛型 Box<Box<i32>> 上调 me
            var (nested, _) = BindUnit(
                "pub class Box\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "    pub func me(): Box\\<T> { return this }\n" +
                "}\n" +
                "pub func wrap(b: Box\\<Box\\<i32>>): Box\\<Box\\<i32>> { return b.me() }\n");
            CheckNoErrors("无诊断（嵌套 Box<Box<i32>>.me）", nested);

            // 多层继承链上 this 返回派生/基类具化
            var (inherit, inheritBodies) = BindUnit(
                "pub open class Box\\<T> {\n" +
                "    pub func me(): Box\\<T> { return this }\n" +
                "}\n" +
                "pub class Child\\<T> : Box\\<T> {\n" +
                "    pub func myself(): Child\\<T> { return this }\n" +
                "    pub func asBox(): Box\\<T> { return this }\n" +
                "}\n");
            CheckNoErrors("无诊断（继承链 this 返回）", inherit);
            TestHarness.CheckTrue("派生 this 定型为 Child<T>",
                BoundDescribe.Body(BodyOf(inheritBodies, "myself")).Contains("This(Child<T>)"));

            // 反例：返回类型与 this 具化不一致
            var (bad, _) = BindUnit(
                "pub class Box\\<T> {\n" +
                "    pub func asOther(): Box\\<i32> { return this }\n" +
                "}\n");
            TestHarness.CheckSemanticError("this 不能当成无关具化返回", bad.Diagnostics,
                "Cannot return 'Box<T>' from function returning 'Box<i32>'");
        }

        // ===== A8：同形嵌套构造类型互赋（型参身份统一）=====
        private static void TestNestedGenericFieldIdentity()
        {
            TestHarness.Section("P3 GenericTypeFixes: 嵌套构造字段身份");

            var (unit, _) = BindUnit(
                "pub class Node\\<T> {\n" +
                "    pub var next: Node\\<T>?\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v) { next = null }\n" +
                "}\n" +
                "pub class Holder\\<T> {\n" +
                "    pub var head: Node\\<T>?\n" +
                "    pub init() { head = null }\n" +
                "    pub func walk() {\n" +
                "        var n = head\n" +
                "        if (n != null) { head = n.next }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（Node<T>? 同形互赋）", unit);

            // 嵌套更深：Node<Node<T>>?
            var (deep, _) = BindUnit(
                "pub class Node\\<T> {\n" +
                "    pub var nest: Node\\<Node\\<T>>?\n" +
                "    pub init() { nest = null }\n" +
                "}\n" +
                "pub class Holder\\<T> {\n" +
                "    pub var cur: Node\\<Node\\<T>>?\n" +
                "    pub init() { cur = null }\n" +
                "    pub func step(n: Node\\<T>) {\n" +
                "        cur = n.nest\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（Node<Node<T>>? 跨类代换）", deep);

            // 反例：不同实参的同形构造不可赋
            var (bad, _) = BindUnit(
                "pub class Node\\<T> {\n" +
                "    pub var next: Node\\<T>?\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v) { next = null }\n" +
                "}\n" +
                "pub class Holder\\<T> {\n" +
                "    pub var head: Node\\<T>?\n" +
                "    pub init() { head = null }\n" +
                "}\n" +
                "pub func bad(h: Holder\\<i32>, n: Node\\<String>?) { h.head = n }\n");
            TestHarness.CheckSemanticError("Node<String>? 不可赋给 Node<i32>?", bad.Diagnostics,
                "Cannot assign 'Nullable<Node<String>>' to 'Nullable<Node<i32>>'");
        }

        // ===== C3：实例字段访问路径代入宿主实参 =====
        private static void TestGenericFieldSubstitution()
        {
            TestHarness.Section("P3 GenericTypeFixes: 字段访问路径代入");

            var (unit, bodies) = BindUnit(
                "pub class Repo\\<TItem> {\n" +
                "    pub var data: Array\\<TItem>\n" +
                "}\n" +
                "pub func write(r: Repo\\<i32>, a: Array\\<i32>) {\n" +
                "    r.data = a\n" +
                "    r.data[0] = 7\n" +
                "}\n");
            CheckNoErrors("无诊断（Array<TItem> 字段代入 i32）", unit);
            TestHarness.CheckTrue("字段访问定型为 Array<i32>",
                BoundDescribe.Body(BodyOf(bodies, "write")).Contains(
                    "InstField(data, Param(r,Repo<i32>), Array<i32>)"));

            // 嵌套 Array<Array<TItem>>
            var (nested, _) = BindUnit(
                "pub class Repo\\<TItem> {\n" +
                "    pub var grid: Array\\<Array\\<TItem>>\n" +
                "}\n" +
                "pub func write(r: Repo\\<i32>, g: Array\\<Array\\<i32>>) { r.grid = g }\n");
            CheckNoErrors("无诊断（嵌套 Array<Array<TItem>> 代入）", nested);

            // 方法形参路径对照（本就代入）+ 字段对照
            var (paramOk, _) = BindUnit(
                "pub class Repo\\<TItem> {\n" +
                "    pub func take(items: Array\\<TItem>) { }\n" +
                "}\n" +
                "pub func f(r: Repo\\<i32>, a: Array\\<i32>) { r.take(a) }\n");
            CheckNoErrors("无诊断（方法形参 Array<TItem> 对照）", paramOk);

            // 反例：实参类型对不上代入后的字段
            var (bad, _) = BindUnit(
                "pub class Repo\\<TItem> {\n" +
                "    pub var data: Array\\<TItem>\n" +
                "}\n" +
                "pub func bad(r: Repo\\<i32>, a: Array\\<String>) { r.data = a }\n");
            TestHarness.CheckSemanticError("Array<String> 不可赋给 Array<i32> 字段",
                bad.Diagnostics, "Cannot assign 'Array<String>' to 'Array<i32>'");
        }

        // ===== A3：for-in 按具化接口判定 =====
        private static void TestForEachConstructedInterface()
        {
            TestHarness.Section("P3 GenericTypeFixes: for-in 具化协议");

            var bagSrc =
                "import core.collections.*\n" +
                "pub class Bag\\<T> implements IEnumerable\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "    pub class It implements IEnumerator\\<T> {\n" +
                "        priv var given: bool\n" +
                "        priv var val: T\n" +
                "        pub init(v: T) { val = v\n            given = false }\n" +
                "        pub override func moveNext(): bool {\n" +
                "            if (given) { return false }\n" +
                "            given = true\n" +
                "            return true\n" +
                "        }\n" +
                "        pub override func current(): T { return val }\n" +
                "    }\n" +
                "    pub override func iterate(): IEnumerator\\<T> { return new It(item) }\n" +
                "}\n";

            var (unit, bodies) = BindUnitWithStdlib(bagSrc +
                "pub func main(): i32 {\n" +
                "    var b = new Bag\\<i32>(7)\n" +
                "    var n = 0\n" +
                "    for (x in b) { n = x }\n" +
                "    return n\n" +
                "}\n");
            CheckNoErrors("无诊断（Bag<i32> 直接 for-in）", unit);
            TestHarness.CheckTrue("循环变量定型为 i32",
                BoundDescribe.Body(BodyOf(bodies, "main")).Contains("For(x, Local(b,Bag<i32>)"));
            var loop = BodyOf(bodies, "main").Body.Statements
                .OfType<BoundLoop>().Single();
            TestHarness.CheckTrue("元素类型为 i32",
                loop.LoopVariable != null
                && ReferenceEquals(loop.LoopVariable.Type, unit.Symbols.Bootstrap.Int32));

            // 先赋给 IEnumerable<i32> 再遍历（对照：修复前仅此路径成功）
            var (viaIface, _) = BindUnitWithStdlib(bagSrc +
                "pub func main(): i32 {\n" +
                "    var e: IEnumerable\\<i32> = new Bag\\<i32>(7)\n" +
                "    var n = 0\n" +
                "    for (x in e) { n = x }\n" +
                "    return n\n" +
                "}\n");
            CheckNoErrors("无诊断（经 IEnumerable<i32> 变量 for-in）", viaIface);

            // 嵌套 Bag<Bag<i32>>
            var (nested, nestedBodies) = BindUnitWithStdlib(bagSrc +
                "pub func main(): i32 {\n" +
                "    var leaf = new Bag\\<i32>(7)\n" +
                "    var outer = new Bag\\<Bag\\<i32>>(leaf)\n" +
                "    var n = 0\n" +
                "    for (b in outer) {\n" +
                "        for (x in b) { n = x }\n" +
                "    }\n" +
                "    return n\n" +
                "}\n");
            CheckNoErrors("无诊断（Bag<Bag<i32>> 嵌套 for-in）", nested);
            TestHarness.CheckTrue("外层元素为 Bag<i32>",
                BoundDescribe.Body(BodyOf(nestedBodies, "main")).Contains("For(b, Local(outer,Bag<Bag<i32>>)"));

            // 接口继承链：IBag<T> implements IEnumerable<T>
            var (chain, _) = BindUnitWithStdlib(
                "import core.collections.*\n" +
                "pub interface IBag\\<T> implements IEnumerable\\<T> { }\n" +
                "pub class Bag\\<T> implements IBag\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "    pub class It implements IEnumerator\\<T> {\n" +
                "        priv var given: bool\n" +
                "        priv var val: T\n" +
                "        pub init(v: T) { val = v\n            given = false }\n" +
                "        pub override func moveNext(): bool {\n" +
                "            if (given) { return false }\n" +
                "            given = true\n" +
                "            return true\n" +
                "        }\n" +
                "        pub override func current(): T { return val }\n" +
                "    }\n" +
                "    pub override func iterate(): IEnumerator\\<T> { return new It(item) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Bag\\<i32>(3)\n" +
                "    var n = 0\n" +
                "    for (x in b) { n = x }\n" +
                "    return n\n" +
                "}\n");
            CheckNoErrors("无诊断（经 IBag<T> 接口链 for-in）", chain);

            // 反例：未实现 IEnumerable
            var (bad, _) = BindUnitWithStdlib(
                "pub class NotBag { }\n" +
                "pub func main(): i32 {\n" +
                "    for (x in new NotBag()) { }\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckSemanticError("未实现 IEnumerable 仍拒绝", bad.Diagnostics,
                "does not implement core.collections.IEnumerable<T>");
        }

        // ===== A1：协变类型经 init 构造后再经协变引用读取 =====
        private static void TestCovariantInitUsage()
        {
            TestHarness.Section("P3 GenericTypeFixes: 协变 init 使用");

            var (unit, _) = BindUnit(
                "pub open class Animal { }\n" +
                "pub class Dog : Animal { }\n" +
                "pub class Box\\<out T> {\n" +
                "    pub const item: T\n" +
                "    pub init(_ -> item)\n" +
                "}\n" +
                "pub func take(b: Box\\<Animal>): Animal { return b.item }\n" +
                "pub func main(): Animal {\n" +
                "    var b = new Box\\<Dog>(new Dog())\n" +
                "    return take(b)\n" +
                "}\n");
            CheckNoErrors("无诊断（Box<out T> init 后协变传递）", unit);

            // 反例：普通方法参数仍禁 out T
            var (badMethod, _) = BindUnit(
                "pub class Box\\<out T> {\n" +
                "    pub func put(item: T) { }\n" +
                "}\n");
            TestHarness.CheckSemanticError("out T 仍不可用于普通方法参数",
                badMethod.Diagnostics,
                "covariant parameter 'T' cannot be used in parameter 'item' of method 'put'");
        }
    }
}
