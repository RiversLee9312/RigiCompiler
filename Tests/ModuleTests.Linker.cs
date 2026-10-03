using RigiCompiler.Bil;

namespace RigiCompiler.Tests;

public static partial class ModuleTests
{
    private static bool LinkReject(Action action)
    {
        try { action(); return false; }
        catch (BilLinkException) { return true; }
    }
    private static BilSimpleMemberDeclaration Member(string symbol, BilKeyword? keyword = null) => new(
        BilMemberKind.Method, symbol, keyword == null ? [new BilAccessibilityModifier(BilAccessibility.Public)]
            : [new BilAccessibilityModifier(BilAccessibility.Public), new BilKeywordModifier(keyword.Value)]);
    private static BilModule ScalarModule(string name, int value)
    {
        var module = new BilModule();
        var scalar = new BilScalarResource("R_0", BilScalarType.I32, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        module.Resources.Add(scalar);
        var symbol = "$" + name + "()@.i32";
        module.LocalSymbols.Add(Member(symbol));
        var fn = new BilFunction(symbol);
        fn.Args.Add(new(".return", ".i32")); fn.Vars.Add(new(".i32", "value"));
        var block = new BilBlock("entry", BilBlockModifier.Entrypoint);
        block.Instructions.Add(new LoadInstruction(scalar, new("value")));
        block.Instructions.Add(new RetInstruction(new("value")));
        fn.Blocks.Add(block); module.Functions.Add(fn);
        return module;
    }
    private static void TestTransactionalLink()
    {
        var provider = ScalarModule("provided", 7);
        var consumer = new BilModule(); consumer.ExternalSymbols.Add(Member("$provided()@.i32"));
        var before = BilWriter.Write(provider);
        var linked = BilModuleLinker.Link([consumer, provider]);
        TestHarness.CheckTrue("external 契约由兼容 local 定义满足", linked.ExternalSymbols.Count == 0 && linked.LocalSymbols.Count == 1);
        BilTestHarness.CheckBilValid("独立链接普通函数通过 verifier", linked);
        TestHarness.CheckTrue("链接不修改 provider cachedmodel", BilWriter.Write(provider) == before);
        TestHarness.CheckTrue("重复 local 事务拒绝且不修改", LinkReject(() => BilModuleLinker.Link([provider, provider])) && BilWriter.Write(provider) == before);
        consumer.ExternalSymbols.Clear(); consumer.ExternalSymbols.Add(Member("$provided()@.i32", BilKeyword.Async));
        TestHarness.CheckTrue("async ABI 不相容拒绝", LinkReject(() => BilModuleLinker.Link([consumer, provider])));
        var local = new BilTypeDeclaration("sample::Box", BilTypeKind.Class, new BilAccessibilityModifier(BilAccessibility.Public));
        local.GenericParameters.Add("T"); local.GenericVariances.Add(BilGenericVariance.Out);
        local.Members.Add(Member("sample::Box$read()@.i32"));
        local.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field, "sample::Box#hidden@.i32",
            [new BilAccessibilityModifier(BilAccessibility.Private)]));
        var required = new BilTypeDeclaration("sample::Box", BilTypeKind.Class, new BilAccessibilityModifier(BilAccessibility.Public));
        required.GenericParameters.Add("T"); required.GenericVariances.Add(BilGenericVariance.Out);
        required.Members.Add(Member("sample::Box$read()@.i32"));
        var declaration = new BilModule(); declaration.LocalSymbols.Add(local);
        var import = new BilModule(); import.ExternalSymbols.Add(required);
        TestHarness.CheckTrue("local 允许额外 private 实现", BilModuleLinker.Link([import, declaration]).ExternalSymbols.Count == 0);
        required.GenericVariances[0] = BilGenericVariance.In;
        TestHarness.CheckTrue("GP variance ABI 不相容拒绝", LinkReject(() => BilModuleLinker.Link([import, declaration])));
        required.GenericVariances[0] = BilGenericVariance.Out;
        required.ExtendsType = "sample::Other";
        TestHarness.CheckTrue("base ABI 不相容拒绝", LinkReject(() => BilModuleLinker.Link([declaration, import])));
    }
    private static void TestTypedResources()
    {
        TestReaderCatchBinding();
        var first = ScalarModule("first", 11); var second = ScalarModule("second", 22);
        var marker = new object(); first.Functions[0].Blocks[0].Instructions[0].Origin = marker;
        var hint = new BilScalarResource("R_1", BilScalarType.String, "\"{}\"");
        first.Resources.Add(hint); first.Functions[0].Blocks[0].Instructions.Insert(0, new HintInstruction(hint));
        // 字面量中刻意出现局部资源名；它不是 typed ref，不能文本替换。
        first.Resources.Add(new BilCollectionResource("R_2", "array<string>", ["\"R_0\""]));
        var linked = BilModuleLinker.Link([first, second]);
        BilTestHarness.CheckBilValid("碰撞 R_0 load/hint 链接后 verifier", linked);
        var one = (LoadInstruction)linked.Functions[0].Blocks[0].Instructions[1];
        var two = (LoadInstruction)linked.Functions[1].Blocks[0].Instructions[0];
        TestHarness.CheckTrue("相同 R0 分别指向自己的 typed resource", one.Resource != two.Resource
            && ((BilScalarResource)one.Resource).LiteralText == "11" && ((BilScalarResource)two.Resource).LiteralText == "22");
        TestHarness.CheckTrue("保留 instruction Origin", ReferenceEquals(one.Origin, marker));
        TestHarness.CheckTrue("collection 字面 R_0 不替换", ((BilCollectionResource)linked.Resources[2]).Elements[0] == "\"R_0\"");
        var catchModule = new BilModule();
        for (var i = 0; i < 2; i++)
        {
            var fn = new BilFunction("$catch" + i + "()@.void");
            fn.Args.Add(new(".return", ".void")); fn.Vars.Add(new(".any", "exception")); fn.Vars.Add(new(".breakid", "breakid"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            var body = new BilBlock("body"); var handler = new BilBlock("handler");
            body.Instructions.Add(new RetInstruction()); handler.Instructions.Add(new RetInstruction());
            var table = new BilCatchTableResource("C_" + i, [new(new(".any"), handler)]);
            catchModule.Resources.Add(table);
            entry.Instructions.Add(new TryInstruction(body, new("exception"), table, null, new("breakid")));
            fn.Blocks.AddRange([entry, body, handler]); catchModule.Functions.Add(fn);
            catchModule.LocalSymbols.Add(Member(fn.Symbol));
        }
        var cloned = BilModuleLinker.Link([catchModule]);
        for (var i = 0; i < 2; i++)
        {
            var attempt = (TryInstruction)cloned.Functions[i].Blocks[0].Instructions[0];
            TestHarness.CheckTrue("两函数重复 blockId catch handler 身份 " + i,
                ReferenceEquals(((BilCatchTableResource)attempt.CatchTable).Entries[0].Handler, cloned.Functions[i].Blocks[2])
                && !ReferenceEquals(attempt.Body, catchModule.Functions[i].Blocks[1]));
        }
        var switchSource = catchModule.Functions[0];
        var switchTable = new BilSwitchTableResource("Switch", ".string", ["\"R_0\""]);
        catchModule.Resources.Add(switchTable);
        switchSource.Blocks[0].Instructions.Add(new SwitchInstruction(new("selector"), switchTable,
            [switchSource.Blocks[1]], switchSource.Blocks[2], new("breakid")) { Origin = marker });
        var switched = BilModuleLinker.Link([catchModule]);
        var select = (SwitchInstruction)switched.Functions[0].Blocks[0].Instructions[1];
        TestHarness.CheckTrue("Switch typed Table/ItemBlocks/default/Origin完整克隆",
            ((BilSwitchTableResource)select.Table).Elements[0] == "\"R_0\"" && select.Table.Name != "Switch"
            && ReferenceEquals(select.ItemBlocks[0], switched.Functions[0].Blocks[1])
            && ReferenceEquals(select.DefaultBlock, switched.Functions[0].Blocks[2]) && ReferenceEquals(select.Origin, marker));
        var enumLocal = new BilTypeDeclaration("E", BilTypeKind.EnumStruct);
        enumLocal.Members.Add(new BilCaseDeclaration("E.C", discriminantResource: "R_0"));
        var enumExt = new BilTypeDeclaration("E", BilTypeKind.EnumStruct);
        enumExt.Members.Add(new BilCaseDeclaration("E.C", discriminantResource: "Other"));
        var a = new BilModule(); a.Resources.Add(new BilScalarResource("R_0", BilScalarType.I32, "3")); a.LocalSymbols.Add(enumLocal);
        var b = new BilModule(); b.Resources.Add(new BilScalarResource("Other", BilScalarType.I32, "03")); b.ExternalSymbols.Add(enumExt);
        TestHarness.CheckTrue("enum discriminant 比较实际整数", BilModuleLinker.Link([b, a]).ExternalSymbols.Count == 0);
        b.Resources[0] = new BilScalarResource("Other", BilScalarType.U64, "3");
        TestHarness.CheckTrue("enum unsigned 实际整数相同", BilModuleLinker.Link([b, a]).ExternalSymbols.Count == 0);
        enumLocal.Members.Clear(); enumLocal.Members.Add(new BilCaseDeclaration("E.C")); enumLocal.Members.Add(new BilCaseDeclaration("E.D"));
        enumExt.Members.Clear(); enumExt.Members.Add(new BilCaseDeclaration("E.D")); enumExt.Members.Add(new BilCaseDeclaration("E.C"));
        TestHarness.CheckTrue("auto case 重排实际ABI不同拒绝", LinkReject(() => BilModuleLinker.Link([b, a])));
        enumExt.Members.Clear(); enumExt.Members.Add(new BilCaseDeclaration("E.C", discriminantResource: "Other"));
        b.Resources[0] = new BilScalarResource("Other", BilScalarType.U8, "0");
        TestHarness.CheckTrue("auto 与 explicit 同实际值等价", BilModuleLinker.Link([b, a]).ExternalSymbols.Count == 0);
    }
    private static void TestReaderCatchBinding()
    {
        var module = new BilModule();
        var left = new BilScalarResource("R_left", BilScalarType.I32, "10");
        var zero = new BilScalarResource("R_zero", BilScalarType.I32, "0");
        module.Resources.AddRange([left, zero]);
        for (int i = 0; i < 2; i++)
        {
            var fn = new BilFunction("$readCatch" + i + "()@.i32");
            fn.Args.Add(new(".return", ".i32"));
            fn.Vars.AddRange([new(".i32", "left"), new(".i32", "zero"), new(".i32", "result"), new(".any", "exception"), new(".breakid", "exit")]);
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            var body = new BilBlock("body"); var handler = new BilBlock("handler");
            var table = new BilCatchTableResource("C_shared", [new(new(".any"), handler)]);
            if (i == 0) module.Resources.Add(table);
            var result = new BilScalarResource("R_result" + i, BilScalarType.I32, (11 + i * 11).ToString()); module.Resources.Add(result);
            entry.Instructions.Add(new LoadInstruction(left, new("left"))); entry.Instructions.Add(new LoadInstruction(zero, new("zero")));
            entry.Instructions.Add(new TryInstruction(body, new("exception"), table, null, new("exit")));
            entry.Instructions.Add(new RetInstruction(new("result")));
            body.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Mod, new("left"), new("zero"), new("result")));
            handler.Instructions.Add(new LoadInstruction(result, new("result"))); handler.Instructions.Add(new RetInstruction(new("result")));
            fn.Blocks.AddRange([entry, body, handler]); module.Functions.Add(fn); module.LocalSymbols.Add(Member(fn.Symbol, BilKeyword.Entrypoint));
        }
        var text = BilWriter.Write(module); var parsed = BilReader.Read(text); var linked = BilModuleLinker.Link([parsed]);
        for (int i = 0; i < 2; i++)
        {
            var attempt = (TryInstruction)linked.Functions[i].Blocks[0].Instructions[2];
            TestHarness.CheckTrue("Writer/Reader共享catch文本表按所属函数绑定 " + i,
                ReferenceEquals(((BilCatchTableResource)attempt.CatchTable).Entries[0].Handler, linked.Functions[i].Blocks[2]));
            var run = RigiCompiler.Bil.BilVm.Run(linked, entryPoint: linked.Functions[i].Symbol);
            TestHarness.CheckTrue("Writer→Reader→Link→VM各catch执行真实不同body " + i,
                run.Exception == null && run.ReturnValue is RigiCompiler.Bil.Vm.VmI32 n && n.Value == 11 + i * 11, run.Exception?.ToString() ?? "");
        }
        TestHarness.CheckTrue("Reader/link不修改输入字节", BilWriter.Write(module) == text && BilWriter.Write(parsed) == text);
    }
}
