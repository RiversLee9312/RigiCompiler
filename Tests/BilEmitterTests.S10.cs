using System.Linq;
using RigiCompiler.Bil;

namespace RigiCompiler.Tests
{
    // BilEmitter S10 stdlib 端到端发射测试：异常具体子类（throw/catch/
    // getMessage）、core.IDisposable 实现、async 调用 Task 形态（§15.2）、
    // Task 同名不同元数声明共存（§8.2 generic 子句）。全部用例经 stdlib
    // 全管线（EmitBilUnit）出合法 BIL（BilVerifier 零错误）。

    public static partial class BilEmitterTests
    {
        // ===== S10：用户异常端到端（自定义子类 + stdlib 子类 catch +
        // 抽象 getMessage 多态派发）=====
        private static void TestExceptionEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub open class ValidationError : core.Exception {\n" +
                "    pub init(text: String) { message = text }\n" +
                "    pub override func getMessage(): String { return message }\n" +
                "}\n" +
                "func fail(): i32 {\n" +
                "    throw new ValidationError(\"invalid\")\n" +
                "    return 0\n" +
                "}\n" +
                "func handle(): String {\n" +
                "    try {\n" +
                "        fail()\n" +
                "        return \"ok\"\n" +
                "    } catch (e: core.IOException) {\n" +
                "        return e.getMessage()\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        return \"runtime error\"\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（异常端到端）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（异常端到端）", module);

            // catch-table 引用 stdlib 异常子类（§19.5 保序）
            var catchTable = module.Resources.OfType<BilCatchTableResource>().Single();
            TestHarness.Check("catch-table 元素（stdlib 异常子类保序）",
                string.Join("\n", catchTable.Entries.Select(e => e.Render())),
                "type(core::IOException) -> blk(try0-catch0)\n" +
                "type(core::RuntimeException) -> blk(try0-catch1)");

            // 自定义异常声明：extends core::Exception、init 自持（§8.1）；
            // message 是 bootstrap 根字段（不重复声明）
            var validationError = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "ValidationError");
            TestHarness.Check("ValidationError extends 与成员声明",
                validationError.ExtendsType + " / " +
                string.Join("; ", validationError.Members
                    .OfType<BilSimpleMemberDeclaration>().Select(m => m.Symbol)),
                "core::Exception / ValidationError$init(text:.string)@.void; ValidationError$getMessage()@.string");

            // init 体 set.field 引用 bootstrap 根字段（预定义符号表闭合）
            var validationInit = module.Functions.Single(
                fn => fn.Symbol == "ValidationError$init(text:.string)@.void");
            TestHarness.CheckTrue("init 体 set.field message（bootstrap 字段）",
                validationInit.Blocks.SelectMany(b => b.Instructions)
                    .Any(i => i is SetFieldInstruction setField
                        && setField.Field.Symbol == "core::Exception#message@.string"));

            // catch 块调用 getMessage：接收者静态类型为 core::IOException，
            // override 遮蔽使 invoke 指向 IOException 的 override（§9.2.1）
            var handle = module.Functions.Single(fn => fn.Symbol == "$handle()@.string");
            TestHarness.CheckTrue("catch 块调用 getMessage（指向 IOException override）",
                handle.Blocks.SelectMany(b => b.Instructions)
                    .Any(i => i is InvokeInstruction invoke
                        && invoke.Method.Symbol == "core::IOException$getMessage()@.string"));

            // stdlib 具体子类各自发射 getMessage override 声明（抽象化落点：
            // 具体实现进符号段，抽象根仅作可解析预定义符号）
            var ioException = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "core::IOException");
            TestHarness.CheckTrue("IOException 声明 getMessage override",
                ioException.Members.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Symbol == "core::IOException$getMessage()@.string"
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Override })));
        }

        // ===== S10：core.IDisposable 实现判定（§6.2）=====
        private static void TestDisposableEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "class Resource implements core.IDisposable {\n" +
                "    pub override func dispose() { }\n" +
                "}\n" +
                "func main(): i32 {\n" +
                "    var r = new Resource()\n" +
                "    r.dispose()\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（IDisposable）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（IDisposable）", module);

            var resource = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == "Resource");
            TestHarness.Check("Resource implements core::IDisposable",
                string.Join(",", resource.ImplementsTypes), "core::IDisposable");
        }

        // ===== S10：using 语句端到端（nested try/finally + dispose 逆序）=====
        private static void TestUsingEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "class Resource implements core.IDisposable {\n" +
                "    pub override func dispose() { }\n" +
                "}\n" +
                "func acquire(): Resource { return new Resource() }\n" +
                "func use(r: Resource) { }\n" +
                "func main() {\n" +
                "    seq using(const a = acquire()) using(var b: Resource = acquire()) { use(a) }\n" +
                "}\n");
            CheckNoErrors("using BIL 全管线无诊断", unit);
            BilTestHarness.CheckBilValid("using BIL 验证器零错误", module);

            var main = module.Functions.Single(f => f.Symbol == "$main()@.void");
            var disposeInvokes = main.Blocks.SelectMany(b => b.Instructions)
                .OfType<InvokeNoResultInstruction>()
                .Where(i => i.Method.Symbol == "Resource$dispose()@.void")
                .ToList();
            TestHarness.CheckTrue("using dispose invoke.noret 数量", disposeInvokes.Count == 2);
            TestHarness.CheckTrue("using dispose invoke.noret 逆序",
                disposeInvokes[0].Arguments.Count == 1 &&
                disposeInvokes[0].Arguments[0] is BilVariableOperand firstReceiver &&
                firstReceiver.Name == "b" &&
                disposeInvokes[1].Arguments.Count == 1 &&
                disposeInvokes[1].Arguments[0] is BilVariableOperand secondReceiver &&
                secondReceiver.Name == "a");

            var (awaitUnit, awaitModule, _) = BilTestHarness.EmitBilUnit(
                "class AwaitResource implements core.IDisposable {\n" +
                "    pub override func dispose() { }\n" +
                "}\n" +
                "async func flush() { }\n" +
                "func acquireAwait(): AwaitResource { return new AwaitResource() }\n" +
                "func awaitBody() {\n" +
                "    seq using(var r = acquireAwait()) { await flush() }\n" +
                "}\n");
            CheckNoErrors("using body await 无诊断", awaitUnit);
            BilTestHarness.CheckBilValid("using body await verifier 零错误", awaitModule);
            TestHarness.CheckTrue("using body await 保留 await",
                awaitModule.Functions.Single(f => f.Symbol == "$awaitBody()@.void")
                    .Blocks.SelectMany(b => b.Instructions).OfType<AwaitInstruction>().Count() == 1);

            var (yieldUnit, yieldModule, _) = BilTestHarness.EmitBilUnit(
                "class YieldResource implements core.IDisposable {\n" +
                "    pub override func dispose() { }\n" +
                "}\n" +
                "func acquireYield(): YieldResource { return new YieldResource() }\n" +
                "func yieldBody() {\n" +
                "    seq using(var r = acquireYield()) { yield }\n" +
                "}\n");
            CheckNoErrors("using body yield 无诊断", yieldUnit);
            BilTestHarness.CheckBilValid("using body yield verifier 零错误", yieldModule);
            TestHarness.CheckTrue("using body yield 保留 yield",
                yieldModule.Functions.Single(f => f.Symbol == "$yieldBody()@.void")
                    .Blocks.SelectMany(b => b.Instructions).OfType<YieldInstruction>().Count() == 1);
        }

        // ===== S10：async 调用 Task 形态 + Task 同名共存声明（§15.2/§8.2）=====
        private static void TestAsyncTaskEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "async func loadUser(id: i32): String { return \"u\" }\n" +
                "async func flushLogs() { }\n" +
                "func main(): i32 {\n" +
                "    const task: core.coroutine.Task\\<String> = loadUser(42)\n" +
                "    flushLogs()\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（async Task）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（async Task）", module);

            // Task 同名不同元数声明共存（§8.2 generic(...) 子句区分）
            var taskDecls = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Where(t => t.Symbol == "core.coroutine::Task").ToList();
            TestHarness.CheckTrue("Task 两声明（泛型 generic 子句 + 非泛型）",
                taskDecls.Count == 2
                && taskDecls.Any(t => t.GenericParameters.Count == 1)
                && taskDecls.Any(t => t.GenericParameters.Count == 0));

            // async 方法声明带 async 修饰符（§8.4）
            var loadUser = module.LocalSymbols.OfType<BilSimpleMemberDeclaration>()
                .Single(m => m.Symbol == "$loadUser(id:.i32)@.string");
            TestHarness.CheckTrue("async 方法声明带 async 修饰符",
                loadUser.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Async }));

            // main：值位置 invoke 产 Task\<.string\>；async 无结果语句位置发
            // invoke（fire-and-forget，非 invoke.noret）
            var main = module.Functions.Single(fn => fn.Symbol == "$main()@.i32");
            var invokeSymbols = main.Blocks.SelectMany(b => b.Instructions)
                .OfType<InvokeInstruction>().Select(i => i.Method.Symbol).ToList();
            TestHarness.Check("main invoke 序列（loadUser + flushLogs）",
                string.Join(",", invokeSymbols),
                "$loadUser(id:.i32)@.string,$flushLogs()@.void");
            TestHarness.CheckTrue("main 无 invoke.noret（async 均有 Task 结果）",
                !main.Blocks.SelectMany(b => b.Instructions).Any(i => i is InvokeNoResultInstruction));
        }

        private static void TestAwaitEmission()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "async func load(): String { return \"ok\" }\n" +
                "async func flush() { }\n" +
                "func main() {\n" +
                "    var value = await load()\n" +
                "    await flush()\n" +
                "}\n");
            CheckNoErrors("await BIL 全管线无诊断", unit);
            BilTestHarness.CheckBilValid("await BIL 验证器零错误", module);
            TestHarness.CheckTrue("await 发射严格 TASK [RESULT]",
                module.Functions.Single(f => f.Symbol == "$main()@.void").Blocks
                    .SelectMany(b => b.Instructions).OfType<AwaitInstruction>().Count() == 2);
            TestHarness.CheckTrue("await 文本含值与无值两形态",
                text.Contains("await $") && text.Split('\n').Count(line => line.StartsWith("        await ")) == 2);
        }

        private static void TestYieldEmission()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "import core.coroutine.*\n" +
                "func main() {\n" +
                "    yield\n" +
                "    yield sleep(1)\n" +
                "}");
            CheckNoErrors("yield BIL 全管线无诊断", unit);
            BilTestHarness.CheckBilValid("yield BIL 验证器零错误", module);
            var instructions = module.Functions.Single(f => f.Symbol == "$main()@.void")
                .Blocks.SelectMany(b => b.Instructions).OfType<YieldInstruction>().ToList();
            TestHarness.CheckTrue("yield 裸/Alarm 两形态", instructions.Count == 2
                && instructions[0].Alarm == null && instructions[1].Alarm != null);
            TestHarness.CheckTrue("yield 文本严格形态", text.Contains("yield\n")
                && text.Contains("yield $") && !text.Contains("yield RESULT"));

            // 用户定义 Alarm 子类必须贯通 P3 类型判定、P4 发射与 verifier。
            var (derivedUnit, derivedModule, _) = BilTestHarness.EmitBilUnit(
                "import core.coroutine.*\n" +
                "shared abstract class UserPolling : PollingAlarm { }\n" +
                "func main(alarm: UserPolling) { yield alarm }\n");
            CheckNoErrors("用户 Alarm 子类 yield 全管线无诊断", derivedUnit);
            BilTestHarness.CheckBilValid("用户 Alarm 子类 yield verifier 零错误", derivedModule);
        }
    }
}
