using System.Linq;

namespace RigiCompiler.Tests
{
    // Lowerer（P4a）修复批次回归测试：
    // - ?. 的 Access 降级前置语句收进 thenBlock（§3.4「receiver 为空则
    //   整体不求值」——修复前 Access 子树内脱糖表达式的前置语句泄漏到
    //   null 检查之前，a 为 null 时仍被求值）
    // - 复合赋值单次求值（§13.2）：索引读取（getAtIndex 调用）与 getter
    //   字段读取恒物化，不作纯读取直通
    // - 可变参数引用 Type 透传 P3 定型的 Array\<元素\>（声明点初始化
    //   不再物化运行期必炸的多余 cast）
    // - variadic 参数索引访问的装箱/拆箱桥接（BIL §7.1 ABI ↔ P3 体内
    //   视角——索引节点 Type 改容器 ABI 元素类型，读外包拆箱 cast /
    //   写按 ABI 类型装箱 cast）
    public static partial class LowererTests
    {
        // ===== ?.：Access 内短路表达式的前置语句收进 then 块 =====
        private static void TestSafeAccessPrefixInThenBlock()
        {
            // Access = u占位.M((a and b))：短路 and 的展开 if 必须落在
            // thenBlock（u 为 null 时 a/b 整体不求值）
            var (unit, _, lowered) = LowerUnit(
                "class User {\n" +
                "    pub var ok: bool\n" +
                "    pub init(_ -> ok) { }\n" +
                "    pub func M(x: bool): bool { return x }\n" +
                "}\n" +
                "func f(u: User?, a: bool, b: bool): bool? {\n" +
                "    return u?.M((a and b))\n" +
                "}\n");
            CheckNoErrors("无诊断（?. Access 前置收块）", unit);
            // 求值序保护（EvalOrderGuard）：实参 (a and b) 产短路前置，
            // receiver 的 cast 物化为 .s3（先于短路 if 执行）
            TestHarness.Check("?. Access 短路前置在 then 块内",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [.s0: User?, .s1: bool?, .s2: bool, .b0: .breakid, .s3: User, .b1: .breakid], [" +
                "Assign(Local(.s0,User?), Param(u,User?)); " +
                "Assign(Local(.s1,bool?), Const(null,bool?)); " +
                "If(Binary(CmpNe, Local(.s0,User?), Const(null,User?), bool), " +
                "[Assign(Local(.s3,User), Cast(Local(.s0,User?), User, User)); " +
                "If(Param(a,bool), [Assign(Local(.s2,bool), Param(b,bool))], " +
                "[Assign(Local(.s2,bool), Const(False,bool))], .b0); " +
                "Assign(Local(.s1,bool?), " +
                "Cast(InstCall(M, Local(.s3,User), " +
                "[Local(.s2,bool)], bool), bool?, bool?))], .b1); " +
                "Return(Local(.s1,bool?))])");

            // 结构性事实：null 检查的 then 块 = receiver 物化 + 短路展开的
            // if + 结果写回（前置语句未泄漏到 If 之前——属主块在 If 前只有
            // receiver/result 两条）
            var body = BodyOf(lowered, "f").Body.Statements;
            var nullCheck = (LoweredIfStatement)body[2];
            TestHarness.CheckTrue("then 块首两条 = receiver 物化 + 短路 if（前置收块结构断言）",
                nullCheck.TrueBlock.Statements.Count == 3
                && nullCheck.TrueBlock.Statements[0] is LoweredAssignmentStatement
                && nullCheck.TrueBlock.Statements[1] is LoweredIfStatement
                && body.Take(2).All(s => s is LoweredAssignmentStatement));
        }

        // ===== 复合赋值单次求值：索引目标恒物化（§13.2）=====
        private static void TestCompoundAssignmentIndexMaterialization()
        {
            // b[0].c += 1：内层索引读取是 getAtIndex 调用——修复前被判
            // 纯读取直通，脱糖后 getAtIndex 求值三次；修复后物化 .sN 一次
            var (unit, _, lowered) = LowerUnit(
                "class Counter {\n" +
                "    pub var c: i32\n" +
                "    pub init(x: i32) { c = x }\n" +
                "}\n" +
                "class Bag {\n" +
                "    pub var held: Counter\n" +
                "    pub init(x: Counter) { held = x }\n" +
                "    pub operator getAtIndex(index: i32): Counter { return held }\n" +
                "}\n" +
                "func f(b: Bag) {\n" +
                "    b[0].c += 1\n" +
                "}\n");
            CheckNoErrors("无诊断（索引复合赋值物化）", unit);
            TestHarness.Check("内层索引物化 .s0（单次求值）",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [.s0: Counter], [" +
                "Assign(Local(.s0,Counter), Index(Param(b,Bag), Int(0,i32), Counter)); " +
                "Assign(InstField(c, Local(.s0,Counter), i32), " +
                "Binary(Add, InstField(c, Local(.s0,Counter), i32), Int(1,i32), i32)); " +
                "ExprStmt(InstField(c, Local(.s0,Counter), i32))])");
        }

        // ===== 复合赋值单次求值：getter 字段目标恒物化（§13.2/§9.4.1）=====
        private static void TestCompoundAssignmentGetterMaterialization()
        {
            // h.Prop.c += 1：Prop 是 computed getter 字段——读取即 getter
            // 调用，修复前不查 Field.Getter 被判纯读取，getter 重复调用；
            // 修复后物化 .sN 一次
            var (unit, _, lowered) = LowerUnit(
                "class Counter {\n" +
                "    pub var c: i32\n" +
                "    pub init(x: i32) { c = x }\n" +
                "}\n" +
                "class Holder {\n" +
                "    priv var backing: Counter\n" +
                "    pub init(x: Counter) { backing = x }\n" +
                "    pub var Prop: Counter {\n" +
                "        pub get(_: _) { return backing }\n" +
                "    }\n" +
                "}\n" +
                "func f(h: Holder) {\n" +
                "    h.Prop.c += 1\n" +
                "}\n");
            CheckNoErrors("无诊断（getter 复合赋值物化）", unit);
            TestHarness.Check("getter 字段读取物化 .s0（单次求值）",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [.s0: Counter], [" +
                "Assign(Local(.s0,Counter), InstField(Prop, Param(h,Holder), Counter)); " +
                "Assign(InstField(c, Local(.s0,Counter), i32), " +
                "Binary(Add, InstField(c, Local(.s0,Counter), i32), Int(1,i32), i32)); " +
                "ExprStmt(InstField(c, Local(.s0,Counter), i32))])");
        }

        // ===== 可变参数引用 Type 透传 P3 定型（Array\<元素\>，不多余 cast）=====
        private static void TestVarArgsParameterType()
        {
            // nums（i32...）体内引用 P3 定型为 Array\<i32\>——声明点
            // 初始化类型相同应直通；修复前 Lowered 取声明元素类型 i32，
            // EnsureDeclaredType 物化 i32 → Array\<i32\> 的多余 cast
            var (unit, _, lowered) = LowerUnit(
                "func f(nums: i32...) {\n" +
                "    var copy: core.Array\\<i32> = nums\n" +
                "}\n");
            CheckNoErrors("无诊断（可变参数引用定型）", unit);
            TestHarness.Check("variadic 引用透传 Array<i32>（无 cast）",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [copy: Array<i32>], " +
                "[Decl(copy, Array<i32>, = Param(nums,Array<i32>))])");
        }

        // ===== variadic 参数索引访问装箱/拆箱桥接（BIL §7.1 ABI ↔ P3 视角）=====
        private static void TestVarArgsIndexLowering()
        {
            // vargs 读+写：索引节点 Type 改容器 ABI 元素类型（Any），
            // 读位置外包拆箱 cast 回 P3 静态元素类型（i32）；写位置
            // place 剥壳后命中同一形态，值按 ABI 类型装箱（i32 → Any）
            var (unit, _, lowered) = LowerUnitWithStdlib(
                "func sum(nums: i32...): i32 {\n" +
                "    var first = nums[0]\n" +
                "    nums[1] = first\n" +
                "    return nums[1]\n" +
                "}\n");
            CheckNoErrors("无诊断（vargs 索引读写降级）", unit);
            TestHarness.Check("vargs 索引读外包拆箱 cast（Type=Any）",
                LoweredDescribe.Body(BodyOf(lowered, "sum")),
                "Body(sum, [first: i32], [" +
                "Decl(first, i32, = " +
                "Cast(Index(Param(nums,Array<i32>), Int(0,i32), Any), i32, i32)); " +
                "Assign(" +
                "Cast(Index(Param(nums,Array<i32>), Int(1,i32), Any), i32, i32), " +
                "Cast(Local(first,i32), Any, Any)); " +
                "Return(" +
                "Cast(Index(Param(nums,Array<i32>), Int(1,i32), Any), i32, i32))])");

            // 结构性断言：索引节点 Type = ABI 元素类型（Any 引用相等）、
            // 壳目标 = P3 元素类型（Int32 引用相等）
            var initializer = ((LoweredLocalDeclarationStatement)
                BodyOf(lowered, "sum").Body.Statements[0]).Initializer!;
            var unboxShell = (LoweredCastExpression)initializer;
            TestHarness.CheckTrue("vargs 索引节点 Type = Any（ABI），壳 = i32（P3）",
                ReferenceEquals(((LoweredIndexExpression)unboxShell.Source).Type,
                    unit.Symbols.Bootstrap.Any)
                && ReferenceEquals(unboxShell.TargetType, unit.Symbols.Bootstrap.Int32)
                && ReferenceEquals(unboxShell.Type, unit.Symbols.Bootstrap.Int32));
            // 写形态：值按 ABI 类型装箱（EnsureDeclaredType 目标 Any）
            var writeAssignment = (LoweredAssignmentStatement)
                BodyOf(lowered, "sum").Body.Statements[1];
            var boxCast = (LoweredCastExpression)writeAssignment.Value;
            TestHarness.CheckTrue("vargs 索引写值装箱 cast 目标 Any",
                ReferenceEquals(boxCast.TargetType, unit.Symbols.Bootstrap.Any));

            // kwargs 读：ABI 元素 = Pair<String, Any> 构造，壳目标 = P3
            // 元素 Pair<String, String>——再经字段访问取 key
            var (unit2, _, lowered2) = LowerUnitWithStdlib(
                "func f(options: named String...): String {\n" +
                "    return options[0].key\n" +
                "}\n");
            CheckNoErrors("无诊断（kwargs 索引读降级）", unit2);
            TestHarness.Check("kwargs 索引读外包 Pair 拆箱 cast",
                LoweredDescribe.Body(BodyOf(lowered2, "f")),
                "Body(f, [], [Return(InstField(key, " +
                "Cast(Index(Param(options,Array<Pair<String, String>>), Int(0,i32), " +
                "Pair<String, Any>), Pair<String, String>, Pair<String, String>), " +
                "String))])");

            // vargs 复合赋值：读侧拆箱 cast 参与运算、写回值装箱 cast
            // 到 Any（脱糖后 place 剥壳物化贯通——包回壳形态不变）
            var (unit3, _, lowered3) = LowerUnitWithStdlib(
                "func bump(nums: i32...) {\n" +
                "    nums[0] += 1\n" +
                "}\n");
            CheckNoErrors("无诊断（vargs 索引复合赋值降级）", unit3);
            TestHarness.Check("vargs 索引复合赋值（读拆箱/写装箱）",
                LoweredDescribe.Body(BodyOf(lowered3, "bump")),
                "Body(bump, [], [" +
                "Assign(" +
                "Cast(Index(Param(nums,Array<i32>), Int(0,i32), Any), i32, i32), " +
                "Cast(Binary(Add, " +
                "Cast(Index(Param(nums,Array<i32>), Int(0,i32), Any), i32, i32), " +
                "Int(1,i32), i32), Any, Any)); " +
                "ExprStmt(" +
                "Cast(Index(Param(nums,Array<i32>), Int(0,i32), Any), i32, i32))])");
        }
    }
}
