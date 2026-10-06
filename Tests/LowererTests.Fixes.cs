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
    // - variadic 参数索引访问的装箱/拆箱桥接（BIL §7.1 ABI ↔ P3 体内
    //   视角——索引节点 Type 改容器 ABI 元素类型，读外包拆箱 cast /
    //   写按 ABI 类型装箱 cast）
    // - bug O5：`?.` 调 void 方法（§3.4 + BIL §15.1）——then 块发
    //   LoweredCallStatement 走 invoke.noret 路径，s_result 保持 null
    //   不做赋值（修复前恒走赋值管线，发射层 invoke 被 §21.3 拒）
    public static partial class LowererTests
    {
        // ===== bug O5：`?.` 调 void 方法 → then 块 LoweredCallStatement =====
        private static void TestSafeAccessVoidCallStatement()
        {
            // 语句位 u?.M()（M 无返回）：then 块 = 单条 InstCallStmt，
            // 无 s_result 赋值；s_result 初始化为 null 后在表达式位引用
            var (unit, _, lowered) = LowerUnit(
                "class User {\n" +
                "    pub init()\n" +
                "    pub func M() { }\n" +
                "}\n" +
                "func f(u: User?) {\n" +
                "    u?.M()\n" +
                "}\n");
            CheckNoErrors("无诊断（?. 调 void 方法）", unit);
            CaseAssertions.Check("?. void 调用降级形态（then 块 InstCallStmt）",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [.s0: User?, .s1: Any?, .b0: .breakid], [" +
                "Assign(Local(.s0,User?), Param(u,User?)); " +
                "Assign(Local(.s1,Any?), Const(null,Any?)); " +
                "If(Binary(CmpNe, Local(.s0,User?), Const(null,User?), bool), " +
                "[InstCallStmt(M, Cast(Local(.s0,User?), User, User), [])], .b0); " +
                "ExprStmt(Local(.s1,Any?))])");
            // 结构性事实：then 块唯一语句 = LoweredCallStatement（无赋值）
            var body = BodyOf(lowered, "f").Body.Statements;
            var nullCheck = (LoweredIfStatement)body[2];
            CaseAssertions.CheckTrue("then 块 = 单条 LoweredCallStatement（无 s_result 赋值）",
                nullCheck.TrueBlock.Statements.Count == 1
                && nullCheck.TrueBlock.Statements[0] is LoweredCallStatement call
                && call.Method.Name == "M");
        }

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
            CaseAssertions.Check("?. Access 短路前置在 then 块内",
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
            CaseAssertions.CheckTrue("then 块首两条 = receiver 物化 + 短路 if（前置收块结构断言）",
                nullCheck.TrueBlock.Statements.Count == 3
                && nullCheck.TrueBlock.Statements[0] is LoweredAssignmentStatement
                && nullCheck.TrueBlock.Statements[1] is LoweredIfStatement
                && body.Take(2).All(s => s is LoweredAssignmentStatement));
        }

        // ===== 复合赋值单次求值：索引目标恒物化（§13.2）=====
        private static void TestCompoundAssignmentIndexMaterialization()
        {
            // Q6：b[0].c += 1 在 P3 即拒（读侧 T? 非可写 place 链头）——
            // 等价的显式解包形态（const + smart cast）字段复合赋值降级
            // 仍走「读取物化一次」通道，锁定该形态
            var (unit, _, lowered) = LowerUnit(
                "class Counter {\n" +
                "    pub var c: i32\n" +
                "    pub init(x: i32) { c = x }\n" +
                "}\n" +
                "class Bag {\n" +
                "    pub var held: Counter\n" +
                "    pub init(x: Counter) { held = x }\n" +
                "    pub operator getAtIndex(index: i32): Counter? { return held }\n" +
                "}\n" +
                "func f(b: Bag) {\n" +
                "    const held = b[0]\n" +
                "    if (held != null) {\n" +
                "        held.c += 1\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（索引解包后复合赋值）", unit);
            CaseAssertions.Check("解包局部复合赋值（Q6 显式形态）",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [held: Counter?, .s0: Counter, .b0: .breakid], " +
                "[Decl(held, Counter?, = Index(Param(b,Bag), Int(0,i32), Counter?)); " +
                "If(Binary(CmpNe, Local(held,Counter?), Null(Counter?), bool), " +
                "[Assign(Local(.s0,Counter), Cast(Local(held,Counter?), Counter, Counter)); " +
                "Assign(InstField(c, Local(.s0,Counter), i32), " +
                "Binary(Add, InstField(c, Local(.s0,Counter), i32), Int(1,i32), i32)); " +
                "ExprStmt(InstField(c, Local(.s0,Counter), i32))], .b0)])");
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
            CaseAssertions.Check("getter 字段读取物化 .s0（单次求值）",
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
            CaseAssertions.Check("variadic 引用透传 Array<i32>（无 cast）",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [copy: Array<i32>], " +
                "[Decl(copy, Array<i32>, = Param(nums,Array<i32>))])");
        }

        // ===== variadic 参数索引访问装箱/拆箱桥接（BIL §7.1 ABI ↔ P3 视角）=====
        private static void TestVarArgsIndexLowering()
        {
            // vargs 读+写（Q6）：索引节点 Type = .nullable<ABI 元素>，
            // 读位置外包拆箱 cast 回 P3 静态类型 Nullable\<i32\>（下游
            // if?/as 解包消费）；写位置 place 剥壳后命中同一形态，值按
            // ABI 类型装箱（i32 → Any）
            var (unit, _, lowered) = LowerUnitWithStdlib(
                "func sum(nums: i32...): i32 {\n" +
                "    var first = nums[0] if? 0\n" +
                "    nums[1] = first\n" +
                "    return nums[1] if? 0\n" +
                "}\n");
            CheckNoErrors("无诊断（vargs 索引读写降级）", unit);
            CaseAssertions.Check("vargs 索引读外包拆箱 cast（Q6）",
                LoweredDescribe.Body(BodyOf(lowered, "sum")),
                "Body(sum, [first: i32, .s0: i32?, .s1: i32, .b0: .breakid, " +
                ".s2: i32?, .s3: i32, .b1: .breakid], " +
                "[Assign(Local(.s0,i32?), " +
                "Cast(Index(Param(nums,Array<i32>), Int(0,i32), Any?), i32?, i32?)); " +
                "If(Binary(CmpNe, Local(.s0,i32?), Const(null,i32?), bool), " +
                "[Assign(Local(.s1,i32), Cast(Local(.s0,i32?), i32, i32))], " +
                "[Assign(Local(.s1,i32), Int(0,i32))], .b0); " +
                "Decl(first, i32, = Local(.s1,i32)); " +
                "Assign(Index(Param(nums,Array<i32>), Int(1,i32), Any), " +
                "Cast(Local(first,i32), Any, Any)); " +
                "Assign(Local(.s2,i32?), " +
                "Cast(Index(Param(nums,Array<i32>), Int(1,i32), Any?), i32?, i32?)); " +
                "If(Binary(CmpNe, Local(.s2,i32?), Const(null,i32?), bool), " +
                "[Assign(Local(.s3,i32), Cast(Local(.s2,i32?), i32, i32))], " +
                "[Assign(Local(.s3,i32), Int(0,i32))], .b1); " +
                "Return(Local(.s3,i32))])");

            // 结构性断言：写形态值按 ABI 类型装箱（EnsureDeclaredType
            // 目标 Any；if? 脱糖使语句位次不固定，按结构搜索）
            var boxCast = BodyOf(lowered, "sum").Body.Statements
                .OfType<LoweredAssignmentStatement>()
                .Select(a => a.Value)
                .OfType<LoweredCastExpression>()
                .FirstOrDefault(c => ReferenceEquals(c.TargetType, unit.Symbols.Bootstrap.Any));
            CaseAssertions.CheckTrue("vargs 索引写值装箱 cast 目标 Any", boxCast != null);

            // kwargs 读（Q6：读出 Pair?，?. 取 key 后 if? 回退）
            var (unit2, _, lowered2) = LowerUnitWithStdlib(
                "func f(options: named String...): String {\n" +
                "    return options[0]?.key if? \"\"\n" +
                "}\n");
            CheckNoErrors("无诊断（kwargs 索引读降级）", unit2);
            CaseAssertions.Check("kwargs 索引读逐元素转换并重建 Pair",
                LoweredDescribe.Body(BodyOf(lowered2, "f")),
                "Body(f, [.s0: Pair<String, String>?, .s1: String?, .b0: .breakid, " +
                ".s2: String?, .s3: String, .b1: .breakid], " +
                "[Assign(Local(.s0,Pair<String, String>?), " +
                "Call(convertArgument, [Index(Param(options,Array<Pair<String, String>>), Int(0,i32), " +
                "Pair<String, Any>?)], Pair<String, String>?)); " +
                "Assign(Local(.s1,String?), Const(null,String?)); " +
                "If(Binary(CmpNe, Local(.s0,Pair<String, String>?), " +
                "Const(null,Pair<String, String>?), bool), " +
                "[Assign(Local(.s1,String?), " +
                "Cast(InstField(key, " +
                "Cast(Local(.s0,Pair<String, String>?), Pair<String, String>, " +
                "Pair<String, String>), String), String?, String?))], .b0); " +
                "Assign(Local(.s2,String?), Local(.s1,String?)); " +
                "If(Binary(CmpNe, Local(.s2,String?), Const(null,String?), bool), " +
                "[Assign(Local(.s3,String), Cast(Local(.s2,String?), String, String))], " +
                "[Assign(Local(.s3,String), Str(\"\",String))], .b1); " +
                "Return(Local(.s3,String))])");

            // vargs 显式读改写回（Q6 后复合赋值索引形态由显式形态替代）：
            // 读侧拆箱 cast 参与运算、写回值装箱 cast 到 Any
            var (unit3, _, lowered3) = LowerUnitWithStdlib(
                "func bump(nums: i32...) {\n" +
                "    nums[0] = ((nums[0] if? 0) + 1)\n" +
                "}\n");
            CheckNoErrors("无诊断（vargs 索引显式读改写回降级）", unit3);
            CaseAssertions.Check("vargs 索引显式读改写回（读拆箱/写装箱）",
                LoweredDescribe.Body(BodyOf(lowered3, "bump")),
                "Body(bump, [.s0: i32?, .s1: i32, .b0: .breakid], " +
                "[Assign(Local(.s0,i32?), " +
                "Cast(Index(Param(nums,Array<i32>), Int(0,i32), Any?), i32?, i32?)); " +
                "If(Binary(CmpNe, Local(.s0,i32?), Const(null,i32?), bool), " +
                "[Assign(Local(.s1,i32), Cast(Local(.s0,i32?), i32, i32))], " +
                "[Assign(Local(.s1,i32), Int(0,i32))], .b0); " +
                "Assign(Index(Param(nums,Array<i32>), Int(0,i32), Any), " +
                "Cast(Binary(Add, Local(.s1,i32), Int(1,i32), i32), Any, Any))])");
        }

        // ===== bug S1/g9：普通值类型中间链写穿（§13.2/§10）=====
        // VM get.field 对值类型 .Copy()——叶写打在拷贝上丢失；修复 =
        // 「正向 get 物化中间值 + 叶写 + 值类型中间反向 set 写回」
        // （复用 wrapper place 深写设施，根 = 局部/参数/this/静态·全局字段）
        private const string Vec2RectSources =
            "struct Vec2 {\n" +
            "    pub var x: i32\n" +
            "    pub var y: i32\n" +
            "    pub init(_ -> x, _ -> y)\n" +
            "    pub func bumpX() { x = (x + 1) }\n" +
            "    pub func getX(): i32 { return x }\n" +
            "}\n" +
            "struct Rect {\n" +
            "    pub var origin: Vec2\n" +
            "    pub var size: Vec2\n" +
            "    pub init(_ -> origin, _ -> size)\n" +
            "}\n";

        // 字段链写 r.origin.x = 7：正向 get 物化 → 叶写 → 写回根 place
        private static void TestValueChainDeepWrite()
        {
            var (unit, _, lowered) = LowerUnit(Vec2RectSources +
                "func f(r: Rect) {\n" +
                "    r.origin.x = 7\n" +
                "}\n");
            CheckNoErrors("无诊断（值类型链深写）", unit);
            CaseAssertions.Check("r.origin.x = 7 降级形态（正向 get + 叶写 + 反向 set）",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [.s0: Vec2], [" +
                "Assign(Local(.s0,Vec2), InstField(origin, Param(r,Rect), Vec2)); " +
                "Assign(InstField(x, Local(.s0,Vec2), i32), Int(7,i32)); " +
                "Assign(InstField(origin, Param(r,Rect), Vec2), Local(.s0,Vec2))])");
        }

        // this 根字段链写（struct 方法内 origin.x = ...）：写回落在 this 上
        private static void TestValueChainDeepWriteThisRoot()
        {
            var (unit, _, lowered) = LowerUnit(Vec2RectSources +
                "struct Rect2 {\n" +
                "    pub var origin: Vec2\n" +
                "    pub init(_ -> origin)\n" +
                "    pub func shift() { origin.x = (origin.x + 1) }\n" +
                "}\n");
            CheckNoErrors("无诊断（this 根值类型链深写）", unit);
            CaseAssertions.Check("this.origin.x 深写降级形态（写回 this.origin）",
                LoweredDescribe.Body(BodyOf(lowered, "shift")),
                "Body(shift, [.s0: Vec2], [" +
                "Assign(Local(.s0,Vec2), InstField(origin, This(Rect2), Vec2)); " +
                "Assign(InstField(x, Local(.s0,Vec2), i32), " +
                "Binary(Add, InstField(x, InstField(origin, This(Rect2), Vec2), i32), " +
                "Int(1,i32), i32)); " +
                "Assign(InstField(origin, This(Rect2), Vec2), Local(.s0,Vec2))])");
        }

        // 复合赋值 r.origin.x += 1：读叶 → 运算 → 叶写 → 同反向写回
        private static void TestValueChainCompoundWrite()
        {
            var (unit, _, lowered) = LowerUnit(Vec2RectSources +
                "func f(r: Rect) {\n" +
                "    r.origin.x += 1\n" +
                "}\n");
            CheckNoErrors("无诊断（值类型链复合赋值）", unit);
            CaseAssertions.Check("r.origin.x += 1 降级形态（含写回）",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [.s0: Vec2, .s1: i32], [" +
                "Assign(Local(.s0,Vec2), InstField(origin, Param(r,Rect), Vec2)); " +
                "Assign(Local(.s1,i32), " +
                "Binary(Add, InstField(x, Local(.s0,Vec2), i32), Int(1,i32), i32)); " +
                "Assign(InstField(x, Local(.s0,Vec2), i32), Local(.s1,i32)); " +
                "Assign(InstField(origin, Param(r,Rect), Vec2), Local(.s0,Vec2)); " +
                "ExprStmt(Local(.s1,i32))])");
        }

        // 静态字段根：Holder.current.origin.x = 7——拷贝静态值 → 叶写 →
        // 写回 origin → set.field.static 写回槽位
        private static void TestValueChainStaticRootWrite()
        {
            var (unit, _, lowered) = LowerUnit(Vec2RectSources +
                "class Holder {\n" +
                "    pub static var current: Rect\n" +
                "}\n" +
                "func f() {\n" +
                "    Holder.current.origin.x = 7\n" +
                "}\n");
            CheckNoErrors("无诊断（静态根值类型链深写）", unit);
            CaseAssertions.Check("Holder.current.origin.x = 7 降级形态（含静态槽写回）",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [.s0: Rect, .s1: Vec2], [" +
                "Assign(Local(.s0,Rect), Field(current,Rect)); " +
                "Assign(Local(.s1,Vec2), InstField(origin, Local(.s0,Rect), Vec2)); " +
                "Assign(InstField(x, Local(.s1,Vec2), i32), Int(7,i32)); " +
                "Assign(InstField(origin, Local(.s0,Rect), Vec2), Local(.s1,Vec2)); " +
                "Assign(Field(current,Rect), Local(.s0,Rect))])");
        }

        // 静态根复合赋值 Holder.current.origin.x += 1
        private static void TestValueChainStaticRootCompound()
        {
            var (unit, _, lowered) = LowerUnit(Vec2RectSources +
                "class Holder {\n" +
                "    pub static var current: Rect\n" +
                "}\n" +
                "func f() {\n" +
                "    Holder.current.origin.x += 1\n" +
                "}\n");
            CheckNoErrors("无诊断（静态根值类型链复合赋值）", unit);
            CaseAssertions.Check("Holder.current.origin.x += 1 降级形态（含静态槽写回）",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [.s0: Rect, .s1: Vec2, .s2: i32], [" +
                "Assign(Local(.s0,Rect), Field(current,Rect)); " +
                "Assign(Local(.s1,Vec2), InstField(origin, Local(.s0,Rect), Vec2)); " +
                "Assign(Local(.s2,i32), " +
                "Binary(Add, InstField(x, Local(.s1,Vec2), i32), Int(1,i32), i32)); " +
                "Assign(InstField(x, Local(.s1,Vec2), i32), Local(.s2,i32)); " +
                "Assign(InstField(origin, Local(.s0,Rect), Vec2), Local(.s1,Vec2)); " +
                "Assign(Field(current,Rect), Local(.s0,Rect)); " +
                "ExprStmt(Local(.s2,i32))])");
        }

        // 静态根 receiver 方法调用 Holder.current.origin.bumpX()
        private static void TestValueChainStaticRootReceiverCall()
        {
            var (unit, _, lowered) = LowerUnit(Vec2RectSources +
                "class Holder {\n" +
                "    pub static var current: Rect\n" +
                "}\n" +
                "func f() {\n" +
                "    Holder.current.origin.bumpX()\n" +
                "}\n");
            CheckNoErrors("无诊断（静态根值类型 receiver 调用写回）", unit);
            CaseAssertions.Check("Holder.current.origin.bumpX() 降级形态（含静态槽写回）",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [.s0: Rect, .s1: Vec2], [" +
                "Assign(Local(.s0,Rect), Field(current,Rect)); " +
                "Assign(Local(.s1,Vec2), InstField(origin, Local(.s0,Rect), Vec2)); " +
                "[InstCallStmt(bumpX, Local(.s1,Vec2), []); " +
                "Assign(InstField(origin, Local(.s0,Rect), Vec2), Local(.s1,Vec2)); " +
                "Assign(Field(current,Rect), Local(.s0,Rect))]])");
        }

        // 静态值类型根单层字段写 Holder.current.origin = ...
        private static void TestValueChainStaticRootSingleFieldWrite()
        {
            var (unit, _, lowered) = LowerUnit(Vec2RectSources +
                "class Holder {\n" +
                "    pub static var current: Rect\n" +
                "}\n" +
                "func f() {\n" +
                "    Holder.current.origin = new Vec2(8, 2)\n" +
                "}\n");
            CheckNoErrors("无诊断（静态根单层字段写）", unit);
            CaseAssertions.Check("Holder.current.origin = new Vec2 降级形态（含静态槽写回）",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [.s0: Rect], [" +
                "Assign(Local(.s0,Rect), Field(current,Rect)); " +
                "Assign(InstField(origin, Local(.s0,Rect), Vec2), " +
                "New(Vec2, init, [Int(8,i32), Int(2,i32)])); " +
                "Assign(Field(current,Rect), Local(.s0,Rect))])");
        }

        // wrapped 静态字段根（cell）：getValue 拷贝 → 链写 → setValue 写回
        private static void TestValueChainWrappedStaticRootWrite()
        {
            var (unit, _, lowered) = LowerUnitWithStdlib(Vec2RectSources +
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper IdW {\n" +
                "    pub init()\n" +
                "}\n" +
                "class Holder {\n" +
                "    @IdW\n" +
                "    pub static var current: Rect = new Rect(new Vec2(1, 2), new Vec2(3, 4))\n" +
                "}\n" +
                "func f() {\n" +
                "    Holder.current.origin.x = 7\n" +
                "}\n");
            CheckNoErrors("无诊断（wrapped 静态根值类型链深写）", unit);
            var desc = BilTestHarness.NormalizeLambdaUuids(
                LoweredDescribe.Body(BodyOf(lowered, "f")));
            CaseAssertions.CheckTrue("wrapped 静态根含 getValue 拷贝 + 叶写 + setValue 写回",
                desc.Contains("InstCall(getValue, InstField(current, New(..companion, []), ..cell..UUID)")
                && desc.Contains("InstField(x,")
                && desc.Contains("InstCallStmt(setValue, InstField(current, New(..companion, []), ..cell..UUID)"));
        }

        // 值类型 receiver 方法调用写回（§10）：void 语句位 = 块
        // [调用, 写回]；有返回值的表达式位 = 结果物化 + 写回前置
        private static void TestValueReceiverCallWriteback()
        {
            var (unit, _, lowered) = LowerUnit(Vec2RectSources +
                "func f(r: Rect) {\n" +
                "    r.origin.bumpX()\n" +
                "}\n" +
                "func g(r: Rect): i32 {\n" +
                "    return r.origin.getX()\n" +
                "}\n");
            CheckNoErrors("无诊断（值类型 receiver 调用写回）", unit);
            CaseAssertions.Check("r.origin.bumpX() 降级形态（调用后写回 origin）",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [.s0: Vec2], [" +
                "Assign(Local(.s0,Vec2), InstField(origin, Param(r,Rect), Vec2)); " +
                "[InstCallStmt(bumpX, Local(.s0,Vec2), []); " +
                "Assign(InstField(origin, Param(r,Rect), Vec2), Local(.s0,Vec2))]])");
            CaseAssertions.Check("r.origin.getX() 降级形态（结果物化 + 写回前置）",
                LoweredDescribe.Body(BodyOf(lowered, "g")),
                "Body(g, [.s0: Vec2, .s1: i32], [" +
                "Assign(Local(.s0,Vec2), InstField(origin, Param(r,Rect), Vec2)); " +
                "Assign(Local(.s1,i32), InstCall(getX, Local(.s0,Vec2), [], i32)); " +
                "Assign(InstField(origin, Param(r,Rect), Vec2), Local(.s0,Vec2)); " +
                "Return(Local(.s1,i32))])");
        }

        // 只读 place 反例 1：getter-only 的 struct 中间环节——写回报
        // 「no setter」编译错误（§10：只读 place 上的写入不生效，显式拒绝）
        private static void TestValueChainGetterOnlyIntermediateError()
        {
            var (unit, _, _) = LowerUnit(Vec2RectSources +
                "class Box {\n" +
                "    priv var backing: Vec2\n" +
                "    pub init(v: Vec2) { backing = v }\n" +
                "    pub var Item: Vec2 {\n" +
                "        pub get(_: _) { return backing }\n" +
                "    }\n" +
                "}\n" +
                "func f(b: Box) {\n" +
                "    b.Item.x = 7\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("getter-only struct 中间环节写穿报错",
                unit.Diagnostics, "'Item' has no setter");
        }

        // 只读 place 反例 2：const struct 字段中间环节——写回报
        // 「Cannot assign to const field」编译错误
        private static void TestValueChainConstIntermediateError()
        {
            var (unit, _, _) = LowerUnit(Vec2RectSources +
                "struct Holder {\n" +
                "    pub const origin: Vec2\n" +
                "    pub init(_ -> origin)\n" +
                "}\n" +
                "func f(h: Holder) {\n" +
                "    h.origin.x = 7\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("const struct 字段中间环节写穿报错",
                unit.Diagnostics, "Cannot assign to const field 'origin'");
        }

        // P14 后语义负例：arr[0].x = 9 非可写 place（getAtIndex 恒返回
        // T?，可空类型上不得直接访问成员）——锁定 P3 拒绝口径
        private static void TestIndexResultFieldWriteRejected()
        {
            var (unit, _, _) = LowerUnit(Vec2RectSources +
                "class Bag {\n" +
                "    pub var held: Vec2\n" +
                "    pub init(v: Vec2) { held = v }\n" +
                "    pub operator getAtIndex(index: i32): Vec2? { return held }\n" +
                "}\n" +
                "func f(b: Bag) {\n" +
                "    b[0].x = 9\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("索引结果字段写 P3 拒绝（非可写 place）",
                unit.Diagnostics, "cannot be accessed on nullable type");
        }
    }
}
