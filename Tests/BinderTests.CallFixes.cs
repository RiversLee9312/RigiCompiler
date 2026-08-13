using System.Linq;

namespace RigiCompiler.Tests
{
    // BinderTests 的调用/路径/二元修复部分（review 批次）：
    // 收窄区域内复合赋值剥壳（S8b）、写模式索引宿主代入（S8c）、
    // Type.instanceMethod 补 this 的链检查（S7c-2）、泛型参数 null 判等（S9a）、
    // 裸名调用宿主代入（S9b）、可变参数链头 Array\<T> 包装（S9d）、
    // 索引复合赋值写回校验（S8c）、显式泛型实参访问控制（S8e）、
    // 泛型 backing 的 value 别名（S9a）、const 字段收窄区域内赋值诊断
    // 与可变参数空包 Syntax 契约。
    public static partial class BinderTests
    {
        private static void TestCallFixes()
        {
            TestHarness.Section("P3 Call/Path/Binary Fixes");

            // ===== 收窄区域内复合赋值（S8b 剥壳修复）：读路径的 SmartCast
            // 包装不再落 place switch 的 default =====
            var (unit1, bodies1) = BindUnit(
                "func f(): String? {\n" +
                "    var s: String? = \"a\"\n" +
                "    if (s != null) {\n" +
                "        s += \"b\"\n" +
                "    }\n" +
                "    return s\n" +
                "}\n" +
                "func g(p: String?): String? {\n" +
                "    if (p != null) {\n" +
                "        p += \"x\"\n" +
                "    }\n" +
                "    return p\n" +
                "}\n");
            CheckNoErrors("无诊断（收窄区域内复合赋值：局部与参数）", unit1);
            // Target 保留收窄包装（读值按收窄类型参与运算定型）
            TestHarness.CheckTrue("局部版 Target 保留 SmartCast 包装",
                BoundDescribe.Body(BodyOf(bodies1, "f")).Contains(
                    "CompoundAssign(Add, SmartCast(Local(s,String?), String)"));
            TestHarness.CheckTrue("参数版 Target 保留 SmartCast 包装",
                BoundDescribe.Body(BodyOf(bodies1, "g")).Contains(
                    "CompoundAssign(Add, SmartCast(Param(p,String?), String)"));

            // ===== 写模式索引宿主代入（S8c）：setAtIndex 形参的宿主泛型
            // 参数按 receiver 构造链代入 =====
            var (unit2, _) = BindUnit(
                "class Box\\<T> {\n" +
                "    pub var storage: T\n" +
                "    pub init(_ -> storage) { }\n" +
                "    pub operator getAtIndex(index: i32): T { return storage }\n" +
                "    pub operator setAtIndex(index: i32, element: T) { storage = element }\n" +
                "}\n" +
                "func f() {\n" +
                "    var box = new Box\\<i32>(0)\n" +
                "    box[0] = 5\n" +
                "}\n");
            CheckNoErrors("无诊断（写模式 element: T 代入 i32）", unit2);
            // index 形参同为宿主泛型参数的同病场景
            var (unit3, _) = BindUnit(
                "class Pair2\\<T> {\n" +
                "    pub var storage: T\n" +
                "    pub init(_ -> storage) { }\n" +
                "    pub operator getAtIndex(index: T): T { return storage }\n" +
                "    pub operator setAtIndex(index: T, element: T) { storage = element }\n" +
                "}\n" +
                "func g() {\n" +
                "    var p = new Pair2\\<i32>(0)\n" +
                "    p[1] = 5\n" +
                "}\n");
            CheckNoErrors("无诊断（写模式 index: T 代入 i32）", unit3);

            // ===== Type.instanceMethod() 补 this 的链检查（S7c-2）=====
            // 反例：他类实例方法——this 类型不符，不得盲补
            var (unit4, _) = BindUnit(
                "class A { pub func m(): i32 { return 1 } }\n" +
                "class B { pub func f(): i32 { return A.m() } }\n");
            TestHarness.CheckSemanticError("他类实例方法盲补 this 拦截", unit4.Diagnostics,
                "instance method 'm' requires a receiver ('this' is not an instance of 'A')");
            // 正例：基类在 this 链上（A.m() 等价 this.m()）
            var (unit5, _) = BindUnit(
                "pub open class A { pub func m(): i32 { return 1 } }\n" +
                "class B : A { pub func f(): i32 { return A.m() } }\n");
            CheckNoErrors("无诊断（基类实例方法多段路径补 this）", unit5);
            // 正例：自身类型的多段路径
            var (unit6, _) = BindUnit(
                "class A {\n" +
                "    pub func m(): i32 { return 1 }\n" +
                "    pub func f(): i32 { return A.m() }\n" +
                "}\n");
            CheckNoErrors("无诊断（自身类型实例方法多段路径）", unit6);
            // 正例：接口默认实现在接口闭包上
            var (unit7, _) = BindUnit(
                "pub interface I { func m(): i32 { return 1 } }\n" +
                "class C implements I { pub func f(): i32 { return I.m() } }\n");
            CheckNoErrors("无诊断（接口默认实现多段路径补 this）", unit7);
            // 正例：静态方法不受影响（无 receiver）
            var (unit8, _) = BindUnit(
                "class A { pub static func m(): i32 { return 1 } }\n" +
                "class B { pub func f(): i32 { return A.m() } }\n");
            CheckNoErrors("无诊断（静态方法多段路径）", unit8);

            // ===== 泛型参数的 null 判等（S9a）：null 定型为 T =====
            var (unit9, bodies9) = BindUnit(
                "func eq\\<T>(t: T, u: T): bool { return t == null }\n" +
                "func ne\\<T>(t: T): bool { return t != null }\n");
            CheckNoErrors("无诊断（泛型参数 null 判等）", unit9);
            var eqReturn = (BoundBinaryExpression)((BoundReturnStatement)
                BodyOf(bodies9, "eq").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("null 侧定型为泛型参数 T",
                eqReturn.Right is BoundLiteralExpression { Type: GenericParameterSymbol });

            // ===== 裸名调用宿主代入（S9b）：receiverType = 当前宿主 =====
            // 多候选（显式泛型实参路径）：宿主代入前宿主泛型形参不可判——
            // IsApplicable 全剔误报 No applicable overload（非显式路径的
            // 视图代入需 OverloadResolution 配套，不在本批次文件范围）
            var (unit10, _) = BindUnit(
                "pub open class Base\\<T> {\n" +
                "    pub func foo\\<U>(x: T): T { return x }\n" +
                "    pub func foo\\<U>(x: i64): i64 { return x }\n" +
                "}\n" +
                "class Derived : Base\\<i32> {\n" +
                "    pub func f(): i32 { return foo\\<String>(1) }\n" +
                "}\n");
            CheckNoErrors("无诊断（多候选裸名调用宿主代入）", unit10);
            // 单候选：实参类型检查不再被静默跳过（行为修正——此前误放行）
            var (unit11, _) = BindUnit(
                "pub open class Base\\<T> {\n" +
                "    pub func foo\\<U>(x: T): T { return x }\n" +
                "}\n" +
                "class Derived : Base\\<i32> {\n" +
                "    pub func f(): i32 { return foo\\<String>(\"s\") }\n" +
                "}\n");
            TestHarness.CheckSemanticError("单候选实参类型检查落实", unit11.Diagnostics,
                "Cannot pass 'String' as 'i32'");
            // 宿主泛型形参与返回类型同时代入（任务书触发形态）
            var (unit12, _) = BindUnit(
                "pub open class Base\\<T> {\n" +
                "    pub func foo\\<U>(x: T): T { return x }\n" +
                "}\n" +
                "class Derived : Base\\<i32> {\n" +
                "    pub func f(): i32 { return foo\\<String>(1) }\n" +
                "}\n");
            CheckNoErrors("无诊断（显式泛型实参 + 宿主代入返回类型）", unit12);

            // ===== 可变参数调用链头包装（S9d）：numbers.m() 的 numbers
            // 定型为 Array\<元素> =====
            var (unit13, bodies13) = BindUnit(
                "pub ext func Any.describe(): String { return \"x\" }\n" +
                "func f(numbers: i32...): String { return numbers.describe() }\n");
            CheckNoErrors("无诊断（可变参数链头调用）", unit13);
            var chainCall = (BoundInstanceCallExpression)((BoundReturnStatement)
                BodyOf(bodies13, "f").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("链头 receiver 定型 Array<i32>",
                chainCall.Receiver.Type is TypeSymbol { ConstructedFrom: not null } rc
                && ReferenceEquals(rc.ConstructedFrom, unit13.Symbols.Bootstrap.ArrayDefinition));

            // ===== 索引复合赋值写回校验（S8c）：setAtIndex 元素形参 =====
            // 反例：写回类型与元素形参不一致——此前只查存在性放行
            var (unit14, _) = BindUnit(
                "class Bag {\n" +
                "    pub var storage: i32\n" +
                "    pub init(_ -> storage) { }\n" +
                "    pub operator getAtIndex(index: i32): i32 { return storage }\n" +
                "    pub operator setAtIndex(index: i32, element: String) { }\n" +
                "}\n" +
                "func f() {\n" +
                "    var bag = new Bag(0)\n" +
                "    bag[0] += 5\n" +
                "}\n");
            TestHarness.CheckSemanticError("索引复合赋值写回类型校验", unit14.Diagnostics,
                "Cannot assign 'i32' to 'String'");
            // 正例：读写元素类型一致
            var (unit15, _) = BindUnit(
                "class Bag {\n" +
                "    pub var storage: i32\n" +
                "    pub init(_ -> storage) { }\n" +
                "    pub operator getAtIndex(index: i32): i32 { return storage }\n" +
                "    pub operator setAtIndex(index: i32, element: i32) { storage = element }\n" +
                "}\n" +
                "func f() {\n" +
                "    var bag = new Bag(0)\n" +
                "    bag[0] += 5\n" +
                "}\n");
            CheckNoErrors("无诊断（索引复合赋值读写一致）", unit15);
            // 正例：元素形参宿主代入（校验与代入设施协同——只校验不代入
            // 则对 T 误报；读侧返回类型此处用具体类型，绕开
            // OverloadResolution 非显式路径的读侧代入缺口）
            var (unit16, _) = BindUnit(
                "class Box\\<T> {\n" +
                "    pub var storage: i32\n" +
                "    pub init(_ -> storage) { }\n" +
                "    pub operator getAtIndex(index: i32): i32 { return storage }\n" +
                "    pub operator setAtIndex(index: i32, element: T) { }\n" +
                "}\n" +
                "func f() {\n" +
                "    var box = new Box\\<i32>(0)\n" +
                "    box[0] += 5\n" +
                "}\n");
            CheckNoErrors("无诊断（索引复合赋值元素形参宿主代入）", unit16);

            // ===== 显式泛型实参访问控制（S8e，SYNTAX §16.1）=====
            var (unit17, _) = BindUnit(
                "class Secret { }\n",
                "pub func f\\<T>(): i32 { return 0 }\n" +
                "pub func g(): i32 { return f\\<Secret>() }\n");
            TestHarness.CheckSemanticError("显式泛型实参跨文件不可见拦截", unit17.Diagnostics,
                "'Secret' is inaccessible due to its accessibility level");
            // 可见实参对照（独立编译单元——不可见拦截的 must-return 级联
            // 不混入）
            var (unit17b, _) = BindUnit(
                "pub func f\\<T>(): i32 { return 0 }\n" +
                "pub func h(): i32 { return f\\<i32>() }\n");
            CheckNoErrors("无诊断（可见实参 i32 对照）", unit17b);

            // ===== 泛型 backing 的 value 别名（S9a）：FieldType 为泛型
            // 参数同样直达 backing 字段 =====
            var (unit18, bodies18) = BindUnit(
                "class Box\\<T> {\n" +
                "    pub var item: T {\n" +
                "        pub get(value: _) { return value }\n" +
                "        pub set(value: _) { value = value }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（泛型 backing 的 value 别名）", unit18);
            var itemGetter = bodies18.Single(b => b.Method.Kind == MethodKind.Getter
                && b.Method.Name == "item");
            var getterReturn = (BoundReturnStatement)itemGetter.Body.Statements[0];
            TestHarness.CheckTrue("getter 体 value → this.item（backing 直达）",
                getterReturn.Value is BoundFieldAccessExpression { Field.Name: "item" } gacc
                && gacc.Type is GenericParameterSymbol);
            var itemSetter = bodies18.Single(b => b.Method.Kind == MethodKind.Setter
                && b.Method.Name == "item");
            TestHarness.CheckTrue("setter 体 value 赋值到 backing",
                BoundDescribe.Body(itemSetter).Contains("InstField(item, This(Box), T)"));

            // ===== const 字段收窄区域内赋值（诊断归位：const 检查优先于
            // place 形态）=====
            var (unit19, _) = BindUnit(
                "pub class Holder {\n" +
                "    pub const field: String?\n" +
                "    pub init(f: String?) { field = f }\n" +
                "    pub func touch() {\n" +
                "        if (field != null) {\n" +
                "            field = \"x\"\n" +
                "        }\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("const 实例字段收窄区域内赋值", unit19.Diagnostics,
                "Cannot assign to const field 'field'");
            var (unit20, _) = BindUnit(
                "const g: String? = \"a\"\n" +
                "func f() {\n" +
                "    if (g != null) {\n" +
                "        g = \"x\"\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("const 全局字段收窄区域内赋值", unit20.Diagnostics,
                "Cannot assign to const field 'g'");

            // ===== 可变参数空包 Syntax 契约（BoundNode.Syntax 非空）=====
            var (unit21, bodies21) = BindUnit(
                "func g(nums: i32...): i32 { return 0 }\n" +
                "func f(): i32 { return g() }\n");
            CheckNoErrors("无诊断（空包调用）", unit21);
            var emptyPackCall = (BoundCallExpression)((BoundReturnStatement)
                BodyOf(bodies21, "f").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("空包 BoundVarArgsArgument.Syntax 非空",
                emptyPackCall.Arguments[^1] is BoundVarArgsArgument pack
                && pack.Syntax != null);

            // ===== 非显式路径宿主代入（S9b 收口）：非泛型方法的签名引用
            // 宿主泛型参数时，实例调用沿 receiver 构造链代入（修复前
            // 非显式路径直构 CandidateView，T 不代入误报赋值不兼容）=====
            var (unit22, _) = BindUnit(
                "class Box\\<T> {\n" +
                "    pub var storage: T\n" +
                "    pub init(_ -> storage) { }\n" +
                "    pub func get(): T { return storage }\n" +
                "}\n" +
                "func f(): i32 {\n" +
                "    var box = new Box\\<i32>(0)\n" +
                "    var x: i32 = box.get()\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("无诊断（非显式路径宿主代入：返回 T → i32）", unit22);
            // 读模式 getAtIndex 返回类型代入（同根因，OverloadResolution
            // 非显式路径视图）
            var (unit23, _) = BindUnit(
                "class Box\\<T> {\n" +
                "    pub var storage: T\n" +
                "    pub init(_ -> storage) { }\n" +
                "    pub operator getAtIndex(index: i32): T { return storage }\n" +
                "}\n" +
                "func f(): i32 {\n" +
                "    var box = new Box\\<i32>(0)\n" +
                "    var x: i32 = box[0]\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("无诊断（getAtIndex 返回 T → i32 代入）", unit23);
        }
    }
}
