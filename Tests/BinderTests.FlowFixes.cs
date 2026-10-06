namespace RigiCompiler.Tests
{
    /// <summary>
    /// P3 流分析/smart cast 修复组测试（review 发现的六处确认 bug）：
    /// 1. if 无 else 非 guard 合并——before 中被 then 体内赋值失效的收窄键
    ///    不得复活（纯交集合并，同双分支规则）；
    /// 2. while/do-while/for 循环出口——before 快照中体赋值根的收窄不得复活
    ///    （出口对恢复表同样 ClearRoot）；
    /// 3. try/catch/finally 收窄维度——try 体内收窄（guard）不泄入 catch/
    ///    finally/try 之后；无 catch 时 try 尾直通；finally 恒执行其体内
    ///    赋值根出口失效；
    /// 4. null 字面量定型——上下文必须可空（Nullable\&lt;T&gt; 构造放行，
    ///    非可空上下文落诊断）；
    /// 5. 值块全路径裸 return——穿透值块结束函数的路径不算落到块尾；
    /// 6. return@ 收集/终止判定下钻表达式子树——藏在 if/switch/seq 表达式
    ///    分支体里的 return@ 参与外层值块类型统一。
    /// 断言：BoundDescribe 描述串（Contains 为主）+ CheckSemanticError。
    /// </summary>
    public static partial class BinderTests
    {

        // ===== 1. if 无 else 非 guard：then 体内失效的收窄不复活 =====
        private static void TestIfMergeNoRevive()
        {
            CompilerTestTools.Section("P3 FlowFixes: if 无 else 收窄合并");

            // 内层 if 体内 x = null（ClearRoot 已剔除）——合并不得复活 x→String
            var (unit, bodies) = BindUnit(
                "func f(x: String?, c: bool): String? {\n" +
                "    if (x != null) {\n" +
                "        if (c) { x = null }\n" +
                "        var s = x\n" +
                "    }\n" +
                "    return null\n" +
                "}\n");
            CheckNoErrors("无诊断（内层失效不复活）", unit);
            CaseAssertions.CheckTrue("内层 if 后 x 不收窄（s: String?）",
                BoundDescribe.Body(BodyOf(bodies, "f")).Contains(
                    "Decl(s, String?, = Param(x,String?))"));

            // 对照：内层 if 体无赋值——before 的合法收窄经纯交集保留（不过度保守）
            var (unit2, bodies2) = BindUnit(
                "func g(x: String?, c: bool): String {\n" +
                "    if (x != null) {\n" +
                "        if (c) { }\n" +
                "        return x\n" +
                "    }\n" +
                "    return \"\"\n" +
                "}\n");
            CheckNoErrors("无诊断（交集保留对照）", unit2);
            CaseAssertions.CheckTrue("内层无赋值时收窄保留",
                BoundDescribe.Body(BodyOf(bodies2, "g")).Contains(
                    "Return(SmartCast(Param(x,String?), String))"));
        }

        // ===== 2. 循环出口：体赋值根的收窄不复活 =====
        private static void TestLoopExitNoRevive()
        {
            CompilerTestTools.Section("P3 FlowFixes: 循环出口收窄");

            var (unit, bodies) = BindUnit(
                "func f(x: String?, c: bool): String? {\n" +
                "    if (x != null) {\n" +
                "        while (c) { x = null }\n" +
                "        var s = x\n" +
                "    }\n" +
                "    return null\n" +
                "}\n");
            CheckNoErrors("无诊断（while 出口）", unit);
            CaseAssertions.CheckTrue("while 出口 x 收窄失效",
                BoundDescribe.Body(BodyOf(bodies, "f")).Contains(
                    "Decl(s, String?, = Param(x,String?))"));

            // do-while 体至少执行一次——体赋值根的收窄在出口必失效
            var (unit2, bodies2) = BindUnit(
                "func g(x: String?, c: bool): String? {\n" +
                "    if (x != null) {\n" +
                "        do { x = null } while (c)\n" +
                "        var s = x\n" +
                "    }\n" +
                "    return null\n" +
                "}\n");
            CheckNoErrors("无诊断（do-while 出口）", unit2);
            CaseAssertions.CheckTrue("do-while 出口 x 收窄失效",
                BoundDescribe.Body(BodyOf(bodies2, "g")).Contains(
                    "Decl(s, String?, = Param(x,String?))"));

            // for 同规则（需 stdlib 迭代协议）
            var (unit3, bodies3) = BindUnitWithStdlib(
                "func h(x: String?, c: bool): String? {\n" +
                "    if (x != null) {\n" +
                "        for (i in 1 to 3) { x = null }\n" +
                "        var s = x\n" +
                "    }\n" +
                "    return null\n" +
                "}\n");
            CheckNoErrors("无诊断（for 出口）", unit3);
            CaseAssertions.CheckTrue("for 出口 x 收窄失效",
                BoundDescribe.Body(BodyOf(bodies3, "h")).Contains(
                    "Decl(s, String?, = Param(x,String?))"));
        }

        // ===== 3. try/catch/finally 收窄维度 =====
        private static void TestTryNarrowing()
        {
            CompilerTestTools.Section("P3 FlowFixes: try 收窄");

            // try 体内 guard 收窄不泄入 catch 体（该路径上 x 恰恰可能是 null）
            var (unit, bodies) = BindUnitWithStdlib(
                "open class E : core.Exception {\n" +
                "    pub override func getMessage(): String { return message }\n" +
                "}\n" +
                "func f(x: String?, b: bool): String? {\n" +
                "    try {\n" +
                "        if (x == null) { throw new E() }\n" +
                "        var s = x\n" +
                "        if (b) { throw new E() }\n" +
                "    } catch (_: core.Exception) {\n" +
                "        var t = x\n" +
                "    }\n" +
                "    return null\n" +
                "}\n");
            CheckNoErrors("无诊断（try 收窄不泄入 catch）", unit);
            var body = BoundDescribe.Body(BodyOf(bodies, "f"));
            CaseAssertions.CheckTrue("try 体内 guard 收窄生效（s: String）",
                body.Contains("Decl(s, String, = SmartCast(Param(x,String?), String))"));
            CaseAssertions.CheckTrue("catch 体内不收窄（t: String?）",
                body.Contains("Decl(t, String?, = Param(x,String?))"));

            // try 体内 guard 收窄不活到 try 之后（catch 路径上不成立 → 交集剔除）
            var (unit2, bodies2) = BindUnitWithStdlib(
                "open class E : core.Exception {\n" +
                "    pub override func getMessage(): String { return message }\n" +
                "}\n" +
                "func g(x: String?): String? {\n" +
                "    try {\n" +
                "        if (x == null) { throw new E() }\n" +
                "        var s = x\n" +
                "    } catch (_: core.Exception) {\n" +
                "    }\n" +
                "    var t = x\n" +
                "    return null\n" +
                "}\n");
            CheckNoErrors("无诊断（try 后不继承收窄）", unit2);
            var body2 = BoundDescribe.Body(BodyOf(bodies2, "g"));
            CaseAssertions.CheckTrue("try 体内收窄生效（s: String）",
                body2.Contains("Decl(s, String, = SmartCast(Param(x,String?), String))"));
            CaseAssertions.CheckTrue("try 之后不收窄（t: String?）",
                body2.Contains("Decl(t, String?, = Param(x,String?))"));

            // 正例：无 catch 时出口路径唯一（try 正常完成）——guard 收窄成立
            var (unit3, bodies3) = BindUnitWithStdlib(
                "open class E : core.Exception {\n" +
                "    pub override func getMessage(): String { return message }\n" +
                "}\n" +
                "func h(x: String?): String {\n" +
                "    try {\n" +
                "        if (x == null) { throw new E() }\n" +
                "    } finally(_) {\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("无诊断（无 catch try 尾直通）", unit3);
            CaseAssertions.CheckTrue("无 catch 时 guard 收窄活到 try 之后",
                BoundDescribe.Body(BodyOf(bodies3, "h")).Contains(
                    "Return(SmartCast(Param(x,String?), String))"));

            // finally 恒执行：其体内赋值根的收窄在出口失效
            var (unit4, bodies4) = BindUnitWithStdlib(
                "func k(x: String?): String? {\n" +
                "    if (x != null) {\n" +
                "        try {\n" +
                "            var s = x\n" +
                "        } finally(_) {\n" +
                "            x = null\n" +
                "        }\n" +
                "        var t = x\n" +
                "    }\n" +
                "    return null\n" +
                "}\n");
            CheckNoErrors("无诊断（finally 赋值失效）", unit4);
            var body4 = BoundDescribe.Body(BodyOf(bodies4, "k"));
            CaseAssertions.CheckTrue("try 体内收窄生效（s: String）",
                body4.Contains("Decl(s, String, = SmartCast(Param(x,String?), String))"));
            CaseAssertions.CheckTrue("finally 赋值后不收窄（t: String?）",
                body4.Contains("Decl(t, String?, = Param(x,String?))"));
        }

        // ===== 4. null 字面量定型：上下文必须可空 =====
        private static void TestNullLiteralContext()
        {
            CompilerTestTools.Section("P3 FlowFixes: null 定型上下文");

            var (unit, _) = BindUnit(
                "func f(): i32 {\n" +
                "    var s: String = null\n" +
                "    return 1\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("var s: String = null 拒绝", unit.Diagnostics,
                "null requires a nullable type context");

            var (unit2, _) = BindUnit(
                "func f(): i32 {\n" +
                "    var s = \"a\"\n" +
                "    s = null\n" +
                "    return 1\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("非空局部赋值 null 拒绝", unit2.Diagnostics,
                "null requires a nullable type context");

            var (unit3, _) = BindUnit("func f(): String { return null }\n");
            CaseAssertions.CheckSemanticError("非空返回 return null 拒绝", unit3.Diagnostics,
                "null requires a nullable type context");

            var (unit4, _) = BindUnit(
                "func g(s: String): i32 { return 1 }\n" +
                "func f(): i32 { return g(null) }\n");
            CaseAssertions.CheckSemanticError("非空形参 null 实参拒绝", unit4.Diagnostics,
                "null requires a nullable type context");

            // 正例：Nullable 上下文照常放行（单候选快路径同规则）
            var (unit5, bodies5) = BindUnit(
                "func g(s: String?): i32 { return 1 }\n" +
                "func f(): i32 { return g(null) }\n");
            CheckNoErrors("无诊断（Nullable 形参放行）", unit5);
            CaseAssertions.CheckTrue("null 定型 Nullable<String>",
                BoundDescribe.Body(BodyOf(bodies5, "f")).Contains("Null(String?)"));
        }

        // ===== 5. 值块全路径裸 return 穿透（SYNTAX §6.1）=====
        private static void TestValueBlockBareReturn()
        {
            CompilerTestTools.Section("P3 FlowFixes: 值块裸 return");

            // §6.1 裁决：裸 return 不得穿透值块边界——值块（含 if/switch
            // 表达式分支体）内一切裸 return 均为编译错误（原「以 return
            // 终止的分支路径不落到块尾」形态自此非法）
            var (unit, _) = BindUnit(
                "func f(c: bool): i32 {\n" +
                "    var r = if (c) { return 5 } else { 1 }\n" +
                "    return r\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("裸 return 穿透值块拒绝", unit.Diagnostics,
                "Bare 'return' cannot cross a value block boundary");
        }

        // ===== 6. 嵌套值块 return@ 参与外层类型统一 =====
        private static void TestNestedValueBlockReturnValue()
        {
            CompilerTestTools.Section("P3 FlowFixes: 嵌套值块 return@");

            // return@ 的值位置：内层 if 表达式分支里 return@outer 产 String，
            // 与外层语句值类型 i32 不一致——修复前漏诊断
            var (unit, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = if (x > 0) named outer {\n" +
                "        return@outer if (x > 1) { return@outer \"s\" } else { return@_ 1 }\n" +
                "    } else {\n" +
                "        0\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("return@ 值位置嵌套不一致拒绝", unit.Diagnostics,
                "if expression branch produces different types ('i32' and 'String')");

            // 声明初始值位置：seq 值块内 var y = if 表达式，内层分支
            // return@outer 产 String，与块尾 return@outer y（i32）不一致
            var (unit2, _) = BindUnit(
                "func h(x: i32): i32 {\n" +
                "    var r = seq named outer {\n" +
                "        var y = if (x > 1) { return@outer \"s\" } else { 1 }\n" +
                "        return@outer y\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("声明初始值位置嵌套不一致拒绝", unit2.Diagnostics,
                "seq expression branch produces different types ('String' and 'i32')");

            // 正例：嵌套 return@ 类型一致——无误报
            var (unit3, _) = BindUnit(
                "func g(x: i32): i32 {\n" +
                "    var r = if (x > 0) named outer {\n" +
                "        return@outer if (x > 1) { return@outer 2 } else { return@_ 1 }\n" +
                "    } else {\n" +
                "        0\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckNoErrors("无诊断（嵌套 return@ 类型一致）", unit3);
        }
    }
}
