using RigiCompiler.Bil;

namespace RigiCompiler.Tests;

// 并行契约采用完整序列/字节对拍；不归一化隐藏名字，也不排序诊断。
public static class CompilerParallelTests
{


    internal static TestSuiteData Spec { get; } = new("CompilerParallel",
    [
        (nameof(TestFrontend), TestFrontend),
        (nameof(TestGraph), TestGraph),
        (nameof(TestDeclarations), TestDeclarations),
        (nameof(TestBodies), TestBodies),
    ], sectionTitle: "CompilerParallel");

    private static void TestFrontend()
    {
        var sources = Enumerable.Range(0, 8).Select(i => new SourceInput(
            string.Concat(Enumerable.Repeat("// padding........................................\n", 200))
                + $"pub func f{i}(): i32 {{ return {i} }}\n", $"file{i}.rg")).ToArray();
        string[] Render(int jobs)
        {
            using var setting = CompilerJobs.WithJobs(jobs);
            return Frontend.ParseRoots(sources).Select(root =>
            {
                using var output = new StringWriter();
                AstJsonlSerializer.Serialize(root, output);
                return output.ToString();
            }).ToArray();
        }
        var serial = Render(1);
        for (var repeat = 0; repeat < 3; repeat++)
            CaseAssertions.CheckTrue("frontend indexed AST/Span/sourceName 完整字节 " + repeat,
                serial.SequenceEqual(Render(4)));
        sources[1] = sources[1] with { Text = "pub func broken( {" };
        sources[5] = sources[5] with { Text = "pub func broken2( {" };
        using var parallel = CompilerJobs.WithJobs(4);
        var errors = Frontend.ParseMany(sources);
        CaseAssertions.CheckTrue("多文件失败保留全部 indexed 结果",
            errors[1].Error is ParserException && errors[5].Error is ParserException
                && errors.Where((_, i) => i != 1 && i != 5).All(r => r.Root != null));
        string[] ErrorSequence(int jobs)
        {
            using var setting = CompilerJobs.WithJobs(jobs);
            return Frontend.ParseMany(sources).Select((slot, index) =>
                index + "|" + slot.Error?.GetType().FullName + "|" + slot.Error?.Message).ToArray();
        }
        var serialErrors = ErrorSequence(1);
        for (var repeat = 0; repeat < 3; repeat++)
            CaseAssertions.CheckTrue("原始异常类型/消息/源码坐标按文件序 " + repeat,
                serialErrors.SequenceEqual(ErrorSequence(4)));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var canceled = false;
        try { Frontend.ParseMany(sources, cancel.Token); }
        catch (OperationCanceledException) { canceled = true; }
        CaseAssertions.CheckTrue("开始前取消不发布部分 root", canceled);

        // 明确观测真实同时执行，不能把被 lease 限到一槽的运行冒称 jobs=4。
        if (ResourceBudget.Shared.Capacity.CpuSlots >= 2)
        {
            using var together = new CountdownEvent(2);
            var active = 0; var peak = 0;
            CompilerJobs.Map(4, index =>
            {
                var current = Interlocked.Increment(ref active);
                int observed;
                do { observed = Volatile.Read(ref peak); }
                while (observed < current && Interlocked.CompareExchange(ref peak, current, observed) != observed);
                if (index < 2) { together.Signal(); together.Wait(TimeSpan.FromSeconds(5)); }
                Interlocked.Decrement(ref active);
                return index;
            });
            CaseAssertions.CheckTrue("真实 worker active peak 至少2", peak >= 2,
                $"capacity={ResourceBudget.Shared.Capacity.CpuSlots}, peak={peak}");
        }
        else CaseAssertions.RecordSkip("真实并行需要至少2 CPU slots");

        var oldError = Console.Error;
        using var output = new StringWriter();
        try
        {
            WorkerConsole.SetError(output);
            var captures = CompilerJobs.Map(8, index =>
            {
                using var logs = Logger.CaptureJob();
                Logger.Verbose("Filtered", "逐字符日志不能入缓冲");
                Logger.Warning("File", index + ":first");
                Logger.Warning("File", index + ":second");
                return logs;
            });
            foreach (var capture in captures) capture.Replay();
        }
        finally { WorkerConsole.SetError(oldError); }
        var expected = string.Concat(Enumerable.Range(0, 8).Select(index =>
            $"WARNING [File]{index}:first{Environment.NewLine}WARNING [File]{index}:second{Environment.NewLine}"));
        CaseAssertions.Check("文件日志按输入序且过滤verbose", output.ToString(), expected);

        var originalException = false;
        try
        {
            CompilerJobs.Map(4, index => index switch
            {
                0 => throw new IOException("低序号原异常"),
                2 => throw new InvalidOperationException("高序号原异常"),
                _ => index,
            }, phase: "contract.exception.parallel");
        }
        catch (IOException exception) { originalException = exception.Message == "低序号原异常"; }
        CaseAssertions.CheckTrue("多worker异常join后按index重抛且释放lease", originalException
            && ResourceBudget.Shared.Usage == (0, 0, 0));
        var serialException = false;
        using (CompilerJobs.WithJobs(1))
        {
            try { CompilerJobs.Map<int>(2, _ => throw new IOException("串行原异常"), phase: "contract.exception.serial"); }
            catch (IOException exception) { serialException = exception.Message == "串行原异常"; }
        }
        CaseAssertions.CheckTrue("串行异常仍保持原异常与预算", serialException
            && ResourceBudget.Shared.Usage == (0, 0, 0));
        using var runningCancel = new CancellationTokenSource();
        var runningCanceled = false;
        try
        {
            CompilerJobs.Map(8, index =>
            {
                if (index == 0) runningCancel.Cancel();
                return index;
            }, cancellationToken: runningCancel.Token, phase: "contract.cancel.parallel");
        }
        catch (OperationCanceledException) { runningCanceled = true; }
        CaseAssertions.CheckTrue("运行中取消不返回部分集合并释放lease", runningCanceled
            && ResourceBudget.Shared.Usage == (0, 0, 0));
    }

    private static void TestGraph()
    {
        var graph = new SymbolGraph("parallel-test");
        var definition = new TypeSymbol("Recursive", TypeKind.Class);
        var parameter = new GenericParameterSymbol("T") { StableIdentity = "recursive/gp/0" };
        definition.GenericParameters.Add(parameter);
        var wrapper = new TypeSymbol("Wrapper", TypeKind.Class);
        wrapper.GenericParameters.Add(new GenericParameterSymbol("U") { StableIdentity = "wrapper/gp/0" });
        definition.BaseType = graph.GetConstructedType(wrapper, graph.GetConstructedType(definition, parameter));
        var values = new TypeSymbol[64];
        Parallel.For(0, values.Length, i => values[i] = graph.GetConstructedType(definition, graph.Bootstrap.Int32));
        CaseAssertions.CheckTrue("同键并发驻留身份唯一且完整基类可见", values.All(value =>
            ReferenceEquals(value, values[0]) && value.BaseType?.TypeArguments is { Count: 1 } args
                && ReferenceEquals(args[0], value)));
        var a = new TypeSymbol("Fail", TypeKind.Class);
        var t0 = new GenericParameterSymbol("T0") { StableIdentity = "fail/gp/0" };
        var t1 = new GenericParameterSymbol("T1") { StableIdentity = "fail/gp/1" };
        a.GenericParameters.AddRange([t0, t1]);
        var pair = new TypeSymbol("Pair", TypeKind.Class);
        a.BaseType = graph.GetConstructedType(pair,
            graph.GetConstructedType(wrapper, graph.GetConstructedType(a, t0, t1)),
            graph.GetConstructedType(wrapper, t1));
        // 缺第二实参强制 Substitute 失败；同一次施工可能已有递归子壳。
        var before = graph.ConstructedTypeSnapshot().ToArray();
        var failed = false;
        try { graph.GetConstructedType(a, graph.Bootstrap.Int32); }
        catch (ArgumentOutOfRangeException) { failed = true; }
        catch (IndexOutOfRangeException) { failed = true; }
        CaseAssertions.CheckTrue("失败事务移除整次递归新增驻留", failed
            && before.SequenceEqual(graph.ConstructedTypeSnapshot()));
        var p = new GenericParameterSymbol("T") { StableIdentity = "owner1/gp/0" };
        var q = new GenericParameterSymbol("T") { StableIdentity = "owner2/gp/0" };
        CaseAssertions.CheckTrue("同名不同 GP 宿主保留独立稳定键",
            graph.StableTypeIdentity(graph.GetNullable(new TypeSymbol("X", TypeKind.Class))) != ""
                && graph.StableTypeIdentity(p) != graph.StableTypeIdentity(q));
    }

    private static void TestDeclarations()
    {
        var sources = new[]
        {
            new SourceInput("namespace demo\nclass A\\<T> { pub var value: T\n pub init(_ -> value) }\npub func f(x: A\\<i32>): i32 { return 1 }\n", "a.rg"),
            new SourceInput("namespace demo\nclass B\\<T> { pub var value: T\n pub init(_ -> value) }\npub func f(x: B\\<i32>): i32 { return 2 }\n", "b.rg"),
            new SourceInput("namespace demo\nclass A\\<T> { var missing: Unknown }\npub func wrong(x: Unknown): Hidden { return 0 }\n", "c.rg"),
        };
        string Snapshot(int jobs)
        {
            using var setting = CompilerJobs.WithJobs(jobs);
            var unit = new CompilationUnit("declarations", Frontend.ParseRoots(sources));
            var declarations = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, declarations);
            var diagnostics = string.Join("\n", unit.Diagnostics.Diagnostics.Select(d =>
                $"{d.Phase}|{d.Severity}|{d.Span?.sourceName}|"
                    + $"{d.Span?.Start.line},{d.Span?.Start.column},{d.Span?.Start.offset}|"
                    + $"{d.Span?.End.line},{d.Span?.End.column},{d.Span?.End.offset}|{d.Message}"));
            var types = string.Join("\n", unit.Symbols.ConstructedTypeSnapshot().Select(unit.Symbols.StableTypeIdentity));
            return diagnostics + "\n---types---\n" + types;
        }
        var serial = Snapshot(1);
        for (var repeat = 0; repeat < 3; repeat++)
            CaseAssertions.Check("P1/P2 完整诊断序列与GP类型身份 " + repeat, Snapshot(4), serial);
    }

    private static void TestBodies()
    {
        var stdlib = StdlibSources.ParseAll();
        foreach (var name in new[] { "wrap001_review_generic_forward", "place_global_positive",
            "lambda_this_owned", "serialization_enum_snapshot", "serialization_graph_class", "place_nested_callback",
            "lambda_return_throw_finally", "comp_parallel_resources",
            "wrap001_review_forward_da_negative" })
        {
            var path = TestCorpusPaths.Resolve("Tests/e2e/rigi/" + name + ".rg");
            var user = CompilerTestTools.ParseRoot(File.ReadAllText(path), name + ".rg");
            string Snapshot(int jobs)
            {
                using var setting = CompilerJobs.WithJobs(jobs);
                var unit = new CompilationUnit("body-diff", stdlib.Concat(new[] { user }).ToArray());
                var declarations = DeclarationCollector.Collect(unit);
                DeclarationResolver.Resolve(unit, declarations);
                var bodies = Binder.Bind(unit, declarations);
                var diagnostics = string.Join("\n", unit.Diagnostics.Diagnostics.Select(d =>
                    $"{d.Phase}|{d.Severity}|{d.Span?.sourceName}|"
                    + $"{d.Span?.Start.line},{d.Span?.Start.column},{d.Span?.Start.offset}|"
                    + $"{d.Span?.End.line},{d.Span?.End.column},{d.Span?.End.offset}|{d.Message}"));
                var bound = string.Join("\n", bodies.Select(body =>
                    unit.Symbols.StableTypeIdentity(body.Method) + "=" + BoundDescribe.Body(body)));
                var cells = string.Join("\n", declarations.AllSymbols.OfType<FieldSymbol>()
                    .Where(field => field.CellStorage != null)
                    .Select(field => field.StableIdentity + "=" + field.CellStorage!.CellClass.Name));
                if (unit.Diagnostics.HasErrors != name.EndsWith("_negative", StringComparison.Ordinal))
                    throw new InvalidOperationException("fixture 诊断预期不符: " + name + "\n" + diagnostics);
                if (unit.Diagnostics.HasErrors) return diagnostics + "\n" + bound + "\n" + cells;
                var lowered = Lowerer.Lower(unit, bodies);
                var module = BilEmitter.Emit(unit, lowered, "body-diff");
                var verification = BilVerifier.Verify(module);
                if (verification.Count != 0) throw new InvalidOperationException(string.Join(";", verification.Select(e => e.Message)));
                var vm = new BilVm(module).Run(maxSteps: 2_000_000);
                if (vm.Exception != null) throw new InvalidOperationException(vm.Exception.ToString());
                if (vm.ReturnValue is not RigiCompiler.Bil.Vm.VmI32 { Value: 0 })
                    throw new InvalidOperationException("fixture 返回值不是0: " + name);
                if (name == "comp_parallel_resources")
                {
                    if (vm.Stdout.Replace("\r\n", "\n") != "12\n1\n2\n7\n8\n")
                        throw new InvalidOperationException("全局初始化/资源fixture副作用顺序错误: " + vm.Stdout);
                    if (module.Resources.OfType<BilSwitchTableResource>().Count() != 1)
                        throw new InvalidOperationException("跨函数同表未去重");
                    foreach (var function in module.Functions)
                        foreach (var attempt in function.Blocks.SelectMany(block => block.Instructions).OfType<TryInstruction>())
                            if (((BilCatchTableResource)attempt.CatchTable).Entries.Any(entry => !function.Blocks.Contains(entry.Handler)))
                                throw new InvalidOperationException("catch handler跨函数");
                }
                return diagnostics + "\n" + bound + "\n" + cells + "\n"
                    + string.Join("\n", lowered.Select(LoweredDescribe.Body)) + "\n"
                    + BilWriter.Write(module) + "\n" + vm.Stdout + "|" + vm.Stderr + "|"
                    + (vm.ReturnValue is RigiCompiler.Bil.Vm.VmI32 code ? code.Value : -1);
            }
            var serial = Snapshot(1);
            for (var repeat = 0; repeat < 3; repeat++)
                CaseAssertions.Check("P3/BIL/VM 完整序列 " + name + "/" + repeat, Snapshot(4), serial);
        }
    }
}
