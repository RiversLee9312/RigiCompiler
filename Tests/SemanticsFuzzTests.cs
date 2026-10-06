using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using RigiCompiler.Bil;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// 语义 fuzz 测试（S8d：重载解析 + 默认参数 + 具名实参重排）。
    ///
    /// 固定种子随机生成多声明 Rigi 源：函数重载组（名字池自然碰撞）、
    /// 默认参数（字面量/全局函数调用/简单构造/刻意类型不匹配）、具名实参
    /// （正名/未知名/重复填充/跳位）、可空形参与 null 实参、自定义类
    /// （init 重载/实例方法重载/open 继承 ranking 三人组），并混入少量
    /// 「注定归口」形态——泛型方法调用、可变参数、具名可变参数、默认值
    /// 顺序违反、默认值依赖环（含自递归）。每个用例跑中端全管线
    /// （BilTestHarness.EmitBilUnit：Parser → P1 → P2 → P3 → P4 → BIL）。
    ///
    /// 逐用例不变量（任一违反即失败，打印用例序号 + 完整源可复现）：
    /// 1. 编译器永不崩溃——任何异常逃逸都是 bug（用户源码错误必须表现为
    ///    可恢复诊断；Lexer/Parser 异常单列——生成器只应产出合法语法）；
    /// 2. P1–P4 零 Error 时产出 BIL 必须过 BilVerifier（零验证错误）；
    /// 3. 诊断不重复刷屏——同一 (阶段, 级别, 位置, 消息) 不得出现两次；
    /// 4. 诊断确定性——每 60 例抽 1 例编译两次，诊断序列逐字一致。
    /// </summary>
    public static class SemanticsFuzzTests
    {
        private const int Seed = 20260804;
        private const int DefaultCaseCount = 3000;
        private const int DeterminismEvery = 60;   // 3000/60 = 50 例确定性抽查
        private const int ProgressEvery = 25;      // 每 N 个已跑 case 打一行进度并 flush


        // 父进程等待每个并行子进程的超时（毫秒）；<=0 表示不限时（默认）。
        // 由 --suite-args 的 child-timeout-ms=N 设置；NativeAOT 产物无 JIT
        // 运行时优化，fuzz 速度约为 CoreCLR 的 1/3，按 JIT 校准的固定超时会误杀。
        // 异常分类计数（crash = 编译器 bug；parseFailure = 生成器或前端 bug）
        private static int crashes;
        private static int parseFailures;
        private static int verifierFailures;
        private static int nondeterministic;
        private static int duplicateDiagnostics;

        // 覆盖统计（证明 fuzz 确实同时命中错误路径与干净路径）
        private static int cleanCases;                 // 全程零诊断
        private static int errorCases;                 // 含 Error 诊断
        private static readonly Dictionary<string, int> messageFrequency = new();

        private static readonly List<string> failureLog = new();

        // --suite-args <from> <to> [child-timeout-ms=N]：
        // from/to 为含两端的 case 序号区间（种子固定，区间可复现）；
        // child-timeout-ms=N 为可选的父进程等待每个子进程的最大毫秒数（默认不限时）。
        // 注意套件参数不能带 -- 前缀（会被命令行解析器当成 test 子命令），故用 key=value 形态。


        internal static int RunSelected(IReadOnlyList<int> indices)
        {
            int from = indices.Min(), to = indices.Max();
            var selected = indices.ToHashSet();
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  Semantics Fuzz Tests (S8d)        ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");
            Console.Out.Flush();

            crashes = parseFailures = verifierFailures = nondeterministic = duplicateDiagnostics = 0;
            cleanCases = errorCases = 0;
            messageFrequency.Clear();
            failureLog.Clear();

            int caseCount = indices.Count;
            ReportProgress($"SemanticsFuzz 全局索引 {string.Join(',', indices)}（跨度 case#{from}..#{to}）（种子 {Seed}，共 {caseCount} 例）");

            var rng = new Random(Seed);
            var stopwatch = Stopwatch.StartNew();
            int ran = 0;
            for (int i = 0; i <= to; i++)
            {
                string source = new Generator(rng).Generate();
                if (!selected.Contains(i)) continue;
                // 进度打在 RunCase 之前：卡死时能看到正在跑的编号
                if (ran % ProgressEvery == 0 || caseCount <= ProgressEvery)
                    ReportProgress($"SemanticsFuzz 开始 case#{i}（种子 {Seed}，本区间已跑 {ran}/{caseCount}）");
                if (caseCount <= 10)
                    DumpSource(i, source);
                RunCase(i, source);
                ran++;
            }
            stopwatch.Stop();

            Console.WriteLine($"  fuzz 汇总：{caseCount} 用例（种子 {Seed}），" +
                $"{CaseAssertions.Current.PassedCount} passed, {CaseAssertions.Current.FailureCount} failed，耗时 {stopwatch.ElapsedMilliseconds} ms");
            Console.WriteLine($"  分类：编译器崩溃 {crashes} / 前端或生成器异常 {parseFailures} / " +
                $"零诊断但 BIL 验证失败 {verifierFailures} / 诊断不确定 {nondeterministic} / " +
                $"重复诊断 {duplicateDiagnostics}");
            Console.WriteLine($"  覆盖：零诊断用例 {cleanCases} / 含 Error 用例 {errorCases}；" +
                $"诊断消息 Top12：");
            foreach (var pair in messageFrequency.OrderByDescending(p => p.Value).Take(12))
            {
                Console.WriteLine($"      ×{pair.Value}  {pair.Key}");
            }
            foreach (string line in failureLog.Take(10))
            {
                Console.WriteLine(line);
            }
            if (failureLog.Count > 10)
            {
                Console.WriteLine($"  ...（其余 {failureLog.Count - 10} 条省略）");
            }
            Console.WriteLine($"=== Semantics Fuzz Tests Complete: {CaseAssertions.Current.PassedCount} passed, {CaseAssertions.Current.FailureCount} failed ===");
            Console.Out.Flush();
            return CaseAssertions.Current.FailureCount;
        }

        private static void ReportProgress(string message)
        {
            Console.WriteLine("  [progress] " + message);
            Console.Out.Flush();
            Logger.Verbose("SemanticsFuzz", message);
        }

        private static void DumpSource(int index, string source)
        {
            Console.WriteLine($"  [dump] case#{index}（种子 {Seed}）输入源码：");
            foreach (string line in source.Split('\n'))
            {
                Console.WriteLine("      | " + line);
            }
            Console.Out.Flush();
            Logger.Verbose("SemanticsFuzz", $"dump case#{index}:\n{source}");
        }

        // ===== 单用例驱动与不变量断言 =====

        private static void RunCase(int index, string source)
        {
            bool ok = true;   // 任一不变量违反即 false，出口统一计数
            CompilationUnit? unit = null;
            BilModule? module = null;
            try
            {
                (unit, module, _) = BilTestHarness.EmitBilUnit(source);
            }
            catch (Exception ex) when (ex is LexerException || ex is ParserException)
            {
                // 生成器只应产出合法语法——命中即生成器 bug 或前端 bug
                parseFailures++;
                Fail(index, source, $"前端拒绝生成源 {ex.GetType().Name}: {ex.Message}");
                ok = false;
            }
            catch (Exception ex)
            {
                crashes++;
                Fail(index, source, $"编译器崩溃 {ex.GetType().Name}: {ex.Message}\n{FirstFrames(ex)}");
                ok = false;
            }

            // 覆盖统计：零诊断 vs 含 Error；消息频次（Top8 证明错误路径命中面）
            if (ok)
            {
                if (unit!.Diagnostics.HasErrors) errorCases++;
                else if (unit.Diagnostics.Diagnostics.Count == 0) cleanCases++;
                foreach (var d in unit.Diagnostics.Diagnostics)
                {
                    string key = $"{d.Phase}: {d.Message}";
                    messageFrequency[key] = messageFrequency.GetValueOrDefault(key) + 1;
                }
            }

            // 不变量 2：零 Error ⇒ BIL 必须过 BilVerifier
            if (ok && !unit!.Diagnostics.HasErrors)
            {
                IReadOnlyList<BilVerificationError> errors;
                try
                {
                    errors = BilVerifier.Verify(module!);
                }
                catch (Exception ex)
                {
                    crashes++;
                    Fail(index, source, $"BilVerifier 崩溃 {ex.GetType().Name}: {ex.Message}");
                    errors = new List<BilVerificationError>();
                    ok = false;
                }
                if (ok && errors.Count > 0)
                {
                    verifierFailures++;
                    Fail(index, source, "零诊断但 BIL 验证失败: " +
                        string.Join("; ", errors.Select(e => e.ToString())));
                    ok = false;
                }
            }

            // 不变量 3：同 (阶段, 级别, 位置, 消息) 诊断不得重复
            if (ok)
            {
                var seen = new HashSet<string>();
                foreach (var d in unit!.Diagnostics.Diagnostics)
                {
                    if (!seen.Add(KeyOf(d)))
                    {
                        duplicateDiagnostics++;
                        Fail(index, source, "重复诊断: " + KeyOf(d));
                        ok = false;
                        break;
                    }
                }
            }

            // 不变量 4：确定性抽查（同一进程内编译两次，诊断序列逐字一致）
            if (ok && index % DeterminismEvery == 0)
            {
                string fingerprint = Fingerprint(unit!);
                CompilationUnit second;
                try
                {
                    (second, _, _) = BilTestHarness.EmitBilUnit(source);
                }
                catch (Exception ex)
                {
                    crashes++;
                    Fail(index, source, $"确定性复查编译崩溃 {ex.GetType().Name}: {ex.Message}");
                    ok = false;
                    second = unit!;
                }
                if (ok && Fingerprint(second) != fingerprint)
                {
                    nondeterministic++;
                    Fail(index, source, "两次编译诊断序列不一致");
                    ok = false;
                }
            }

            if (ok) CaseAssertions.Record(true); else CaseAssertions.Record(false);
        }

        private static string KeyOf(Diagnostic d)
        {
            return $"{d.Phase}|{d.Severity}|{SpanKey(d.Span)}|{d.Message}";
        }

        private static string SpanKey(CharRange? span)
        {
            return span == null
                ? "-"
                : $"{span.Value.Start.offset}-{span.Value.End.offset}";
        }

        private static string Fingerprint(CompilationUnit unit)
        {
            return string.Join("\n", unit.Diagnostics.Diagnostics.Select(KeyOf));
        }

        private static string FirstFrames(Exception ex)
        {
            var lines = (ex.StackTrace ?? "").Split('\n');
            return string.Join("\n", lines.Take(4));
        }

        private static void Fail(int index, string source, string problem)
        {
            var sb = new StringBuilder();
            sb.Append($"  [FAIL] case#{index}（种子 {Seed}）：{problem}\n");
            foreach (string line in source.Split('\n'))
            {
                sb.Append("      | ").Append(line).Append('\n');
            }
            failureLog.Add(sb.ToString());
        }

        // ===== 随机源生成器 =====
        // 同一个 Random 顺序消费——用例 i 的源由 (Seed, i) 唯一确定，可复现。

        private sealed class Generator
        {
            private static readonly string[] PrimitiveTypes =
                { "i32", "i64", "String", "bool", "double", "char", "Any" };

            private readonly Random rng;
            private readonly List<string> decls = new();          // 类型与函数声明
            private readonly List<FuncSpec> funcs = new();        // 全局函数规格
            private readonly List<FuncSpec> methods = new();      // Alpha.m 重载组
            private readonly List<FuncSpec> inits = new();        // Alpha init 重载组
            private readonly HashSet<string> providers = new();   // 被引用的默认值提供器
            private bool classActive;
            private bool genericActive;
            private bool variadicActive;
            private bool namedVariadicActive;
            private bool cyclicActive;
            private bool selfCycleActive;
            private bool ambiguityActive;        // am(String, Any)/am(Any, String) 二义对
            private bool nullAmbiguityActive;    // nm(String?)/nm(Alpha?) null 二义对
            // 保守模式（约 1/3 用例）：关闭一切错误注入——实参精确定型、结构
            // 合法、无归口声明——保证零诊断干净用例占比，让「零 Error ⇒ BIL
            // 必须过验证」不变量获得充分覆盖（重载/默认值填充的 P4 发射面）
            private bool conservative;

            private sealed class FuncSpec
            {
                public string Name = "";
                public string? ReturnType;   // null = void
                public readonly List<(string Name, string Type, bool HasDefault)> Params = new();
                public int RequiredCount => Params.Count(p => !p.HasDefault);
            }

            public Generator(Random rng)
            {
                this.rng = rng;
            }

            public string Generate()
            {
                conservative = rng.Next(3) == 0;
                // 类场景先行（类型池随后包含 Alpha）
                if (rng.Next(5) < 3) GenClassScenario();
                // 全局函数 2-5 个，名字池碰撞自然形成 1-3 个重载组
                int funcCount = 2 + rng.Next(4);
                for (int i = 0; i < funcCount; i++)
                {
                    GenFunc(Pick("f", "g", "h"));
                }
                MaybeSpecialDecls();
                string main = GenMain();

                var sb = new StringBuilder();
                foreach (string p in providers) sb.Append(ProviderDecl(p));
                foreach (string d in decls) sb.Append(d);
                sb.Append(main);
                return sb.ToString();
            }

            // ----- 类型与值 -----

            private string RandomType()
            {
                string t = Pick(PrimitiveTypes);
                if (classActive && rng.Next(4) == 0) t = "Alpha";
                // 偶发可空形式（String?/Alpha?/i32?——null 实参与装箱视图维度）
                if (t is "String" or "Alpha" or "i32" && rng.Next(8) == 0) t += "?";
                return t;
            }

            private string ExactValue(string type)
            {
                return type switch
                {
                    "i32" => Pick("0", "1", "2", "42"),
                    "i64" => Pick("7L", "100L"),
                    "String" => Pick("\"s\"", "\"abc\"", "\"\""),
                    "bool" => Pick("true", "false"),
                    "double" => Pick("1.5", "0.25"),
                    "char" => Pick("'a'", "'Z'"),
                    "Any" => ExactValue(Pick("i32", "String", "bool", "double")),
                    "Alpha" => "new Alpha()",
                    _ => "0",
                };
            }

            // 刻意不匹配的实参（i64 给 i32 字面量——无隐式数值转换维度）
            private string MismatchedValue(string type)
            {
                var options = new List<string> { "i32", "String", "bool" };
                options.Remove(type.TrimEnd('?'));
                if (type == "i64") options = new List<string> { "i32", "String" };
                if (options.Count == 0) options.Add("char");
                return ExactValue(options[rng.Next(options.Count)]);
            }

            // 调用点实参值：60% 精确类型 / 20% 刻意不匹配 / 10% null / 10% 精确
            // （保守模式：恒精确——null 仅对可空形参合法地使用）
            private string ValueFor(string type)
            {
                if (type.EndsWith("?"))
                {
                    return !conservative && rng.Next(5) < 2 ? "null" : ExactValue(type.TrimEnd('?'));
                }
                if (conservative) return ExactValue(type);
                int roll = rng.Next(10);
                if (roll < 7) return ExactValue(type);
                if (roll < 9) return MismatchedValue(type);
                return "null";
            }

            // 默认值表达式：字面量 / 全局函数调用 / 简单构造 / 刻意不匹配
            // （保守模式关闭不匹配注入）
            private string DefaultFor(string type)
            {
                int roll = rng.Next(10);
                if (type.EndsWith("?"))
                {
                    return roll < 6 ? "null" : ExactValue(type.TrimEnd('?'));
                }
                if (roll < 5) return ExactValue(type);
                if (roll < 7) return ProviderCall(type);
                if (roll < 9 || conservative) return ExactValue(type);
                return MismatchedValue(type);
            }

            private string ProviderCall(string type)
            {
                string name = type switch
                {
                    "i64" => "dl",
                    "String" => "ds",
                    "bool" => "db",
                    "double" => "dd",
                    "char" => "dc",
                    "Alpha" => "dA",
                    _ => "di",   // i32 / Any（i32 可赋给 Any）
                };
                providers.Add(name);
                return name + "()";
            }

            private static string ProviderDecl(string name)
            {
                return name switch
                {
                    "dl" => "func dl(): i64 { return 9L }\n",
                    "ds" => "func ds(): String { return \"d\" }\n",
                    "db" => "func db(): bool { return true }\n",
                    "dd" => "func dd(): double { return 2.5 }\n",
                    "dc" => "func dc(): char { return 'z' }\n",
                    "dA" => "func dA(): Alpha { return new Alpha() }\n",
                    _ => "func di(): i32 { return 7 }\n",
                };
            }

            // ----- 声明生成 -----

            private void GenClassScenario()
            {
                classActive = true;
                bool open = rng.Next(3) == 0;
                var cd = new StringBuilder();
                cd.Append(open ? "open class Alpha {\n" : "class Alpha {\n");
                cd.Append("    pub var f0: i32\n");
                // init 带默认值——new Alpha() 与 new Alpha(3) 均可用
                cd.Append("    pub init(_ -> f0 = 9) { }\n");
                inits.Add(new FuncSpec { Name = "init",
                    Params = { ("f0", "i32", true) } });
                if (rng.Next(3) == 0)   // init 重载
                {
                    cd.Append("    pub init(_ -> f0, s: String) { }\n");
                    inits.Add(new FuncSpec { Name = "init",
                        Params = { ("f0", "i32", false), ("s", "String", false) } });
                }
                // 实例方法重载：两个不同形参类型（可能碰撞成重复声明——归诊断路径）
                string t1 = RandomType();
                cd.Append($"    pub func m(p: {t1}): i32 {{ return 1 }}\n");
                methods.Add(new FuncSpec { Name = "m", ReturnType = "i32",
                    Params = { ("p", t1, false) } });
                if (rng.Next(2) == 0)
                {
                    string t2 = RandomType();
                    // 25% 同首参 + 默认值第二参——直击「填充默认值更少者优先」平局打破
                    if (rng.Next(4) == 0)
                    {
                        cd.Append($"    pub func m(p: {t1}, q: {t2} = {DefaultFor(t2)}): i32 " +
                            "{ return 2 }\n");
                        methods.Add(new FuncSpec { Name = "m", ReturnType = "i32",
                            Params = { ("p", t1, false), ("q", t2, true) } });
                    }
                    else
                    {
                        cd.Append($"    pub func m(p: {t2}): i32 {{ return 2 }}\n");
                        methods.Add(new FuncSpec { Name = "m", ReturnType = "i32",
                            Params = { ("p", t2, false) } });
                    }
                }
                // 带默认参数的实例方法
                if (rng.Next(3) == 0)
                {
                    string t3 = RandomType();
                    cd.Append($"    pub func md(p: {t3} = {DefaultFor(t3)}): {t3} {{ return p }}\n");
                    methods.Add(new FuncSpec { Name = "md", ReturnType = t3,
                        Params = { ("p", t3, true) } });
                }
                // 默认值引用实例成员——声明点无 this，归口诊断
                if (!conservative && rng.Next(30) == 0)
                {
                    cd.Append("    pub func mb(x: i32 = f0): i32 { return x }\n");
                }
                cd.Append("}\n");
                decls.Add(cd.ToString());

                // open 继承 ranking 三人组（Beta 不构造，经带参 helper 传值绑定）
                if (open)
                {
                    decls.Add("class Beta : Alpha { }\n");
                    decls.Add("func pk(a: Alpha): i32 { return 1 }\n");
                    decls.Add("func pk(b: Beta): i32 { return 2 }\n");
                    decls.Add("func usepk(b: Beta): i32 { return pk(b) }\n");
                    decls.Add("func usepka(a: Alpha): i32 { return pk(a) }\n");
                }
            }

            private void GenFunc(string name)
            {
                var spec = new FuncSpec { Name = name };
                var usedNames = new List<string>();
                int paramCount = rng.Next(4);   // 0..3
                bool defaultStarted = false;
                bool violateOrder = !conservative && rng.Next(20) == 0;   // 5% 刻意顺序违反（P2 诊断）
                var text = new StringBuilder($"func {name}(");
                for (int i = 0; i < paramCount; i++)
                {
                    string pname = PickUnused(usedNames, "a", "b", "c", "x", "y");
                    string ptype = RandomType();
                    string? def = null;
                    if (defaultStarted) def = DefaultFor(ptype);
                    else if (rng.Next(3) == 0) { defaultStarted = true; def = DefaultFor(ptype); }
                    // 顺序违反：首个默认值之后末位形参不给默认值
                    if (violateOrder && defaultStarted && i == paramCount - 1 && paramCount > 1)
                    {
                        def = null;
                    }
                    spec.Params.Add((pname, ptype, def != null));
                    if (i > 0) text.Append(", ");
                    text.Append($"{pname}: {ptype}");
                    if (def != null) text.Append($" = {def}");
                }
                text.Append(')');
                // 2/3 带返回类型（可空返回 return null；Alpha 返回 new Alpha()）
                if (rng.Next(3) != 0)
                {
                    string rt = RandomType();
                    spec.ReturnType = rt;
                    string value = rt.EndsWith("?") ? "null" : ExactValue(rt);
                    text.Append($": {rt} {{ return {value} }}\n");
                }
                else
                {
                    text.Append(" { }\n");
                }
                decls.Add(text.ToString());
                funcs.Add(spec);
            }

            private void MaybeSpecialDecls()
            {
                if (conservative) return;   // 保守模式不混入归口形态
                // 泛型方法调用归口（S9）
                if (rng.Next(16) == 0)
                {
                    decls.Add("func gf\\<T>(x: T): T { return x }\n");
                    genericActive = true;
                }
                // 可变参数归口
                if (rng.Next(20) == 0)
                {
                    decls.Add("func vf(numbers: i32...): i32 { return 0 }\n");
                    variadicActive = true;
                }
                // 具名可变参数归口
                if (rng.Next(33) == 0)
                {
                    decls.Add("func nv(options: named String...) { }\n");
                    namedVariadicActive = true;
                }
                // 默认值依赖环（in-flight 集合拦截，保守 null——不得崩溃）
                if (rng.Next(40) == 0)
                {
                    decls.Add("func cy1(x: i32 = cy2()): i32 { return x }\n");
                    decls.Add("func cy2(y: i32 = cy1()): i32 { return y }\n");
                    cyclicActive = true;
                }
                // 自递归默认值
                if (rng.Next(50) == 0)
                {
                    decls.Add("func sr(x: i32 = sr()): i32 { return x }\n");
                    selfCycleActive = true;
                }
                // 交叉二义对（SYNTAX §4.2 combine 形态）：两候选互不占优
                if (rng.Next(7) == 0)
                {
                    decls.Add("func am(a: String, b: Any) { }\n");
                    decls.Add("func am(a: Any, b: String) { }\n");
                    ambiguityActive = true;
                }
                // null 二义对：两个 Nullable 候选对 null 实参互不占优
                if (classActive && rng.Next(7) == 0)
                {
                    decls.Add("func nm(x: String?) { }\n");
                    decls.Add("func nm(x: Alpha?) { }\n");
                    nullAmbiguityActive = true;
                }
            }

            // ----- main 与调用点 -----

            private string GenMain()
            {
                var sb = new StringBuilder("func main() {\n");
                if (classActive) sb.Append("    var a0 = new Alpha()\n");
                int callCount = 1 + rng.Next(4);   // 1..4
                for (int k = 0; k < callCount; k++)
                {
                    sb.Append("    ").Append(GenCallStatement(k)).Append('\n');
                }
                // 二义对声明后必配一次命中调用（两对均为 void，裸语句即可）
                if (ambiguityActive) sb.Append("    am(\"x\", \"y\")\n");
                if (nullAmbiguityActive) sb.Append("    nm(null)\n");
                sb.Append("}\n");
                return sb.ToString();
            }

            private string GenCallStatement(int k)
            {
                int roll = rng.Next(100);
                // 全局函数调用（55%）
                if (roll < 55) return GlobalCall(k);
                // 实例方法调用（15%，无类场景回落全局）
                if (roll < 70)
                {
                    if (classActive && methods.Count > 0)
                    {
                        var intent = methods[rng.Next(methods.Count)];
                        string call = $"a0.{intent.Name}(" + GenArgs(intent) + ")";
                        return $"var r{k} = {call}";
                    }
                    return GlobalCall(k);
                }
                // init 构造调用（10%，无类场景回落全局）
                if (roll < 80)
                {
                    if (classActive && inits.Count > 0)
                    {
                        var intent = inits[rng.Next(inits.Count)];
                        return $"var r{k} = new Alpha(" + GenArgs(intent) + ")";
                    }
                    return GlobalCall(k);
                }
                // 归口形态调用（各 2-4%，未激活回落全局）
                if (roll < 84)
                    return genericActive ? $"var r{k} = " + (rng.Next(2) == 0 ? "gf(1)" : "gf\\<i32>(\"s\")")
                        : GlobalCall(k);
                if (roll < 87) return variadicActive ? $"var r{k} = vf(1, 2)" : GlobalCall(k);
                if (roll < 89) return namedVariadicActive ? $"nv(q = \"s\")" : GlobalCall(k);
                if (roll < 91) return cyclicActive ? $"var r{k} = cy1()" : GlobalCall(k);
                if (roll < 93) return selfCycleActive ? $"var r{k} = sr()" : GlobalCall(k);
                // 未知名调用（4%，保守模式回落全局）
                if (roll < 97 && !conservative) return "nosuch(1)";
                // 兜底：随机函数混沌实参（保守模式收敛为结构适配）
                var any = funcs[rng.Next(funcs.Count)];
                if (any.ReturnType == null) return any.Name + "(" + GenArgs(any) + ")";
                return conservative
                    ? $"var r{k} = {any.Name}({GenArgs(any)})"
                    : $"var r{k} = {any.Name}({ChaosArgs()})";
            }

            // 全局函数调用：随机选重载组、随机选一候选作 intent
            private string GlobalCall(int k)
            {
                var groups = funcs.GroupBy(f => f.Name).Select(g => g.ToList()).ToList();
                var group = groups[rng.Next(groups.Count)];
                var intent = group[rng.Next(group.Count)];
                string call = intent.Name + "(" + GenArgs(intent) + ")";
                return group.All(f => f.ReturnType == null) ? call : $"var r{k} = {call}";
            }

            // 结构适配 intent 的实参列表（混入具名/跳位/重复/越界形态；
            // 保守模式只出结构合法实参——具名不跳位、不重复、不越界）
            private string GenArgs(FuncSpec spec)
            {
                int total = spec.Params.Count;
                int required = spec.RequiredCount;
                if (!conservative)
                {
                    // 25% 完全混沌
                    if (rng.Next(4) == 0) return ChaosArgs();
                    // 10% 单具名跳位（greet(punct = "?") 形态）
                    if (total > 0 && rng.Next(10) == 0)
                    {
                        int j = rng.Next(total);
                        return spec.Params[j].Name + " = " + ValueFor(spec.Params[j].Type);
                    }
                }
                int count = required + rng.Next(total - required + 1);
                if (!conservative)
                {
                    int roll = rng.Next(20);
                    if (roll == 0) count = total + 1;                       // 偶发超个数
                    else if (roll == 1 && required > 0) count = required - 1;   // 偶发缺必填
                }
                var parts = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    string value = i < total ? ValueFor(spec.Params[i].Type) : ValueFor("i32");
                    int form = rng.Next(20);
                    if (i < total && form >= 13 && form < 18)
                    {
                        parts.Add(spec.Params[i].Name + " = " + value);   // 具名 25%
                    }
                    else if (!conservative && i < total && form == 18)
                    {
                        parts.Add("zzz = " + value);                      // 未知名 5%
                    }
                    else
                    {
                        parts.Add(value);                                 // 位置
                    }
                }
                // 偶发重复填充（位置已占 param0，再具名 param0）
                if (!conservative && total > 0 && count > 0 && rng.Next(20) == 0)
                {
                    parts.Add(spec.Params[0].Name + " = " + ValueFor(spec.Params[0].Type));
                }
                return string.Join(", ", parts);
            }

            private string ChaosArgs()
            {
                int n = rng.Next(5);   // 0..4
                var parts = new List<string>();
                for (int i = 0; i < n; i++)
                {
                    string value = rng.Next(10) == 0
                        ? "null"
                        : ExactValue(Pick(PrimitiveTypes));
                    parts.Add(rng.Next(4) == 0
                        ? Pick("a", "b", "x", "zzz") + " = " + value
                        : value);
                }
                return string.Join(", ", parts);
            }

            // ----- 小工具 -----

            private T Pick<T>(params T[] options) => options[rng.Next(options.Length)];

            private string PickUnused(List<string> used, params string[] pool)
            {
                var available = pool.Where(p => !used.Contains(p)).ToList();
                string name = available.Count > 0
                    ? available[rng.Next(available.Count)]
                    : "p" + used.Count;
                used.Add(name);
                return name;
            }
        }
    }
}
