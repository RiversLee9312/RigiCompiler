using RigiCompiler.Bil;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// S4 BIL 对象模型 + BilWriter 测试（M38）：手工构造 BIL §20 完整示例的
    /// 内存模型，输出与规范文本逐行一致（黄金文件断言）；另覆盖 §19 资源
    /// 全形态、§8 声明形态与 §10–§16 指令形态抽样。
    /// M57 起构造走强类型模型（指令子类/枚举种类与修饰符/类型化资源），
    /// 黄金文本与迁移前逐字节一致（行为零变化判据）。
    /// 黄金文本经 Lines(...) 显式拼 \n，与源文件换行编码无关。
    /// M58：本套件黄金锁排版；自足合法模块（§20 两个示例 + M64 §18 hint
    /// 模块）同时过 BilVerifier（CheckBilValid），纯排版抽样用例（§19/§8.2+§8.5/
    /// §10–§16/§8.4.1）操作数与类型引用未声明进模块，不过验证器。
    /// </summary>
    public static class BilWriterTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestHarness.Section("BilWriter");

            // ===== §20 完整黄金示例（逐行一致）=====
            var module = new BilModule();
            module.Metadata.Add(new BilMetadataEntry("module", BilScalarType.String, "\"com.example.app\""));
            var helloResource = new BilScalarResource("R_Hello", BilScalarType.String, "\"hello, world\"");
            var zeroResource = new BilScalarResource("R_Zero", BilScalarType.I32, "0");
            module.Resources.Add(helloResource);
            module.Resources.Add(zeroResource);

            var app = new BilTypeDeclaration("com.example::App", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            app.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.StaticMethod,
                "com.example::App$.static.main(args:.array<.string>)@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));
            module.LocalSymbols.Add(app);

            var console = new BilTypeDeclaration("core::Console", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            console.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.StaticMethod,
                "core::Console$.static.println(value:.string)@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            module.ExternalSymbols.Add(console);

            var main = new BilFunction("com.example::App$.static.main(args:.array<.string>)@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Args.Add(new BilArgDeclaration("args", ".array<.string>"));
            main.Vars.Add(new BilVarDeclaration(".string", "message"));
            main.Vars.Add(new BilVarDeclaration(".i32", "result"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(helloResource, BilOp.Var("message")));
            entry.Instructions.Add(new InvokeNoResultInstruction(
                BilOp.Fn("core::Console$.static.println(value:.string)@.void"),
                new[] { BilOp.Var("message") }));
            entry.Instructions.Add(new LoadInstruction(zeroResource, BilOp.Var("result")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("result")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);

            TestHarness.Check("§20 完整黄金示例", BilWriter.Write(module), Lines(
                "BIL \"1.1\"",
                "",
                "Metadata {",
                "    module = string \"com.example.app\"",
                "}",
                "",
                "Resources {",
                "    R_Hello = string \"hello, world\",",
                "    R_Zero = i32 0",
                "}",
                "",
                "LocalSymbols {",
                "    .type com.example::App = class pub {",
                "        .static-method com.example::App$.static.main(args:.array<.string>)@.i32 pub entrypoint",
                "    }",
                "}",
                "",
                "ExternalSymbols {",
                "    .type core::Console = class pub {",
                "        .static-method core::Console$.static.println(value:.string)@.void pub",
                "    }",
                "}",
                "",
                "fn(com.example::App$.static.main(args:.array<.string>)@.i32) {",
                "    .args {",
                "        .return = .i32,",
                "        args = .array<.string>",
                "    }",
                "",
                "    .vars {",
                "        .string message,",
                "        .i32 result",
                "    }",
                "",
                "    .block entry entrypoint {",
                "        load res(R_Hello) $message",
                "        invoke.noret fn(core::Console$.static.println(value:.string)@.void) [$message]",
                "        load res(R_Zero) $result",
                "        ret $result",
                "    }",
                "}"));
            // §20 完整示例是自足合法模块：验证器零错误（M58）
            BilTestHarness.CheckBilValid("§20 完整示例验证器零错误", module);

            // ===== §20 wrapper 应用标记与 proxy 模板示例（M88）=====
            // 黄金按 §20 用 LocalSymbols 排版；验证器侧把 proxy 模板放
            // ExternalSymbols（声明无 body，§9.1 本地方法才强制 fn）
            var wrapperModule = new BilModule();
            var service = new BilTypeDeclaration("com.example::Service", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilWrappedModifier("core.logging::Logged"));
            service.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "com.example::Service#name@.string",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            wrapperModule.LocalSymbols.Add(service);
            var logged = new BilTypeDeclaration("core.logging::Logged", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            logged.GenericParameters.Add("TTarget");
            logged.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "core.logging::Logged$.proxy.get.name(value:.string)@.string",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilWrapperProxyModifier(BilProxyKind.Specific),
                },
                modifiersOnNextLine: true));
            wrapperModule.LocalSymbols.Add(logged);

            TestHarness.Check("§20 wrapper 应用标记与 proxy 模板示例", BilWriter.Write(wrapperModule), Lines(
                "BIL \"1.1\"",
                "",
                "Metadata {",
                "}",
                "",
                "Resources {",
                "}",
                "",
                "LocalSymbols {",
                "    .type com.example::Service = class pub wrapped(core.logging::Logged) {",
                "        .field com.example::Service#name@.string pub",
                "    }",
                "    .type core.logging::Logged = wrapper generic(TTarget) pub rich {",
                "        .method core.logging::Logged$.proxy.get.name(value:.string)@.string",
                "            pub wrapper-proxy(specific)",
                "    }",
                "}",
                "",
                "ExternalSymbols {",
                "}"));
            var wrapperValid = new BilModule();
            wrapperValid.LocalSymbols.Add(service);
            wrapperValid.ExternalSymbols.Add(logged);
            BilTestHarness.CheckBilValid("§20 wrapper 示例验证器零错误", wrapperValid);

            // ===== §19 资源全形态 =====
            // （排版抽样：资源引用的类型（com.example::User/core::IO*Exception）
            // 未声明进模块，不过验证器——合法性归 BilEmitter/BilVerifier 套件）
            var resModule = new BilModule();
            resModule.Resources.Add(new BilScalarResource("R_Message", BilScalarType.String, "\"hello, world\""));
            resModule.Resources.Add(new BilScalarResource("R_Enabled", BilScalarType.Bool, "true"));
            resModule.Resources.Add(new BilScalarResource("R_Count", BilScalarType.I64, "123"));
            resModule.Resources.Add(new BilScalarResource("R_Ratio", BilScalarType.F64, "0.5"));
            resModule.Resources.Add(new BilNullResource("R_NullUser", "com.example::User"));
            resModule.Resources.Add(new BilCollectionResource("R_Names", "array<string>",
                new[] { "\"a\"", "\"b\"" }));
            resModule.Resources.Add(new BilCollectionResource("R_Entry", "pair<string, i64>",
                new[] { "\"count\"", "2" }));
            resModule.Resources.Add(new BilCollectionResource("R_Map", "map<string, i64>",
                new[] { "\"a\" = 1", "\"b\" = 2" }, multiline: true));
            resModule.Resources.Add(new BilScalarResource("R_DataHex", BilScalarType.RawHex, "x2FF2331C"));
            resModule.Resources.Add(new BilScalarResource("R_DataBin", BilScalarType.RawBin, "b01010101"));
            resModule.Resources.Add(new BilSwitchTableResource("R_Switch", ".i32",
                new[] { "1", "2", "3" }));
            resModule.Resources.Add(new BilCatchTableResource("R_Catches",
                new[]
                {
                    new BilCatchEntry(BilOp.Type("core::IOException"), new BilBlock("catchIo")),
                    new BilCatchEntry(BilOp.Type("core::RuntimeException"), new BilBlock("catchRuntime")),
                }));

            TestHarness.Check("§19 资源全形态", BilWriter.Write(resModule), Lines(
                "BIL \"1.1\"",
                "",
                "Metadata {",
                "}",
                "",
                "Resources {",
                "    R_Message = string \"hello, world\",",
                "    R_Enabled = bool true,",
                "    R_Count = i64 123,",
                "    R_Ratio = f64 0.5,",
                "    R_NullUser = null type(com.example::User),",
                "    R_Names = array<string> { \"a\", \"b\" },",
                "    R_Entry = pair<string, i64> { \"count\", 2 },",
                "    R_Map = map<string, i64> {",
                "        \"a\" = 1,",
                "        \"b\" = 2",
                "    },",
                "    R_DataHex = raw.hex x2FF2331C,",
                "    R_DataBin = raw.bin b01010101,",
                "    R_Switch = switch-table<.i32> { 1, 2, 3 },",
                "    R_Catches = catch-table {",
                "        type(core::IOException) -> blk(catchIo),",
                "        type(core::RuntimeException) -> blk(catchRuntime)",
                "    }",
                "}",
                "",
                "LocalSymbols {",
                "}",
                "",
                "ExternalSymbols {",
                "}"));

            // ===== §18 hint 指令 =====
            // 自足合法模块（string 资源 + entry block 内 hint），过验证器
            var hintModule = new BilModule();
            var hintResource = new BilScalarResource("R_Hint", BilScalarType.String, "\"{}\"");
            var hintZero = new BilScalarResource("R_Zero", BilScalarType.I32, "0");
            hintModule.Resources.Add(hintResource);
            hintModule.Resources.Add(hintZero);
            hintModule.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var hintMain = new BilFunction("$main()@.i32");
            hintMain.Args.Add(new BilArgDeclaration(".return", ".i32"));
            hintMain.Vars.Add(new BilVarDeclaration(".i32", "x"));
            var hintEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            hintEntry.Instructions.Add(new HintInstruction(hintResource));
            hintEntry.Instructions.Add(new LoadInstruction(hintZero, BilOp.Var("x")));
            hintEntry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            hintMain.Blocks.Add(hintEntry);
            hintModule.Functions.Add(hintMain);

            TestHarness.Check("§18 hint 指令", BilWriter.Write(hintModule), Lines(
                "BIL \"1.1\"",
                "",
                "Metadata {",
                "}",
                "",
                "Resources {",
                "    R_Hint = string \"{}\",",
                "    R_Zero = i32 0",
                "}",
                "",
                "LocalSymbols {",
                "    .method $main()@.i32 pub",
                "}",
                "",
                "ExternalSymbols {",
                "}",
                "",
                "fn($main()@.i32) {",
                "    .args {",
                "        .return = .i32",
                "    }",
                "",
                "    .vars {",
                "        .i32 x",
                "    }",
                "",
                "    .block entry entrypoint {",
                "        hint res(R_Hint)",
                "        load res(R_Zero) $x",
                "        ret $x",
                "    }",
                "}"));
            BilTestHarness.CheckBilValid("§18 hint 模块验证器零错误", hintModule);

            // ===== §8.2 extends/implements 多行形态 + §8.5 enum case =====
            // （排版抽样：extends/implements 类型与 discriminant 资源未登记进
            // 模块，不过验证器）
            var declModule = new BilModule();
            var dog = new BilTypeDeclaration("com.example::Dog", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            dog.ExtendsType = "com.example::Animal";
            dog.ImplementsTypes.Add("com.example::IPet");
            dog.ImplementsTypes.Add("com.example::INamed");
            declModule.LocalSymbols.Add(dog);
            var result = new BilTypeDeclaration("com.example::RequestResult", BilTypeKind.EnumStruct,
                new BilAccessibilityModifier(BilAccessibility.Public));
            result.Members.Add(new BilCaseDeclaration("com.example::RequestResult.Success"));
            result.Members.Add(new BilCaseDeclaration("com.example::RequestResult.Failed",
                new[] { new BilCaseParameter("errorCode", ".i32") }, discriminantResource: "R_FailedCase"));
            declModule.LocalSymbols.Add(result);

            TestHarness.Check("§8.2/§8.5 声明形态", BilWriter.Write(declModule), Lines(
                "BIL \"1.1\"",
                "",
                "Metadata {",
                "}",
                "",
                "Resources {",
                "}",
                "",
                "LocalSymbols {",
                "    .type com.example::Dog = class",
                "        extends com.example::Animal",
                "        implements com.example::IPet, com.example::INamed",
                "        pub {",
                "    }",
                "    .type com.example::RequestResult = enum-struct pub {",
                "        .case com.example::RequestResult.Success() discriminant auto",
                "        .case com.example::RequestResult.Failed(errorCode:.i32) discriminant res(R_FailedCase)",
                "    }",
                "}",
                "",
                "ExternalSymbols {",
                "}"));

            // ===== §10–§16 指令形态抽样 =====
            // （抽样指令引用的 block/资源仅作操作数占位，不进模块——
            // 强类型模型下悬空引用不可构造，须先建对象；变量/符号均未声明，
            // 不过验证器）
            var instModule = new BilModule();
            var fn = new BilFunction("com.example::App$.static.test()@.void");
            fn.Args.Add(new BilArgDeclaration(".return", ".void"));
            fn.Vars.Add(new BilVarDeclaration(".i32", "x"));
            var body = new BilBlock("entry", BilBlockModifier.Entrypoint);
            var switchTable = new BilSwitchTableResource("R_T", ".i32", new[] { "1", "2" });
            var tryCatchTable = new BilCatchTableResource("R_Catches", new BilCatchEntry[0]);
            body.Instructions.Add(new CastInstruction(BilOp.Var("a"), BilOp.Var("b"),
                BilOp.Type("com.example::User"), isSafe: false));
            body.Instructions.Add(new CastInstruction(BilOp.Var("a"), BilOp.Var("b"),
                BilOp.Type("com.example::User"), isSafe: true));
            body.Instructions.Add(new CastIndirectInstruction(BilOp.Var("a"), BilOp.Var("b"),
                BilOp.Var("tid"), isSafe: false));
            body.Instructions.Add(new CastIndirectInstruction(BilOp.Var("a"), BilOp.Var("b"),
                BilOp.Var("tid"), isSafe: true));
            body.Instructions.Add(new DirectTypeCheckInstruction(BilTypeCheckKind.Is,
                BilOp.Var("a"), BilOp.Type("com.example::User"), BilOp.Var("b")));
            body.Instructions.Add(new DirectTypeCheckInstruction(BilTypeCheckKind.Supers,
                BilOp.Var("a"), BilOp.Type("com.example::User"), BilOp.Var("b")));
            body.Instructions.Add(new DirectTypeCheckInstruction(BilTypeCheckKind.With,
                BilOp.Var("a"), BilOp.Type("core.logging::Logged"), BilOp.Var("b")));
            body.Instructions.Add(new IndirectTypeCheckInstruction(BilTypeCheckKind.Is,
                BilOp.Var("a"), BilOp.Var("tid"), BilOp.Var("b")));
            body.Instructions.Add(new IsCaseInstruction(BilOp.Var("a"),
                BilOp.Case("com.example::RequestResult.Failed"), BilOp.Var("b")));
            body.Instructions.Add(new GetWrapperInstruction(BilOp.Var("a"),
                BilOp.Type("core.logging::Logged"), BilOp.Var("b")));
            body.Instructions.Add(new GetWrapperIndirectInstruction(BilOp.Var("a"),
                BilOp.Var("tid"), BilOp.Var("b")));
            body.Instructions.Add(new GetWrapperFieldInstruction(BilOp.Var("obj"),
                BilOp.Field("com.example::Hero#hp@.i32"),
                BilOp.Type("core.clamp::Clamped"), BilOp.Var("b")));
            body.Instructions.Add(new GetIdVarInstruction(BilOp.Var("a"), BilOp.Var("t")));
            body.Instructions.Add(new GetIdTypeInstruction(BilOp.Type("com.example::User"),
                BilOp.Var("t")));
            body.Instructions.Add(new GetIdFieldInstruction(
                BilOp.Field("com.example::Service#name@.string"), BilOp.Var("fid")));
            body.Instructions.Add(new GetVarInstruction(BilOp.Var("a"), BilOp.Var("b")));
            body.Instructions.Add(new GetFieldInstruction(BilOp.Var("obj"), BilOp.Var("t"),
                BilOp.Field("com.example::Service#name@.string")));
            body.Instructions.Add(new GetFieldIndirectInstruction(BilOp.Var("obj"),
                BilOp.Var("t"), BilOp.Var("fid")));
            body.Instructions.Add(new SetFieldIndirectInstruction(BilOp.Var("v"),
                BilOp.Var("obj"), BilOp.Var("fid")));
            body.Instructions.Add(new SetWrapperFieldInstruction(BilOp.Var("v"), BilOp.Var("obj"),
                BilOp.Wrapper("core.logging::Logged"),
                BilOp.Field("core.logging::Logged#level@.string")));
            body.Instructions.Add(new GetSelfInstruction(BilOp.Var("self")));
            body.Instructions.Add(new InvokeInstruction(BilOp.Fn(BilSpellings.InnerReservedFunction),
                BilOp.Var("r"), new[] { BilOp.Var("a") }));
            body.Instructions.Add(new InvokeNoResultInstruction(
                BilOp.Fn(BilSpellings.InnerReservedFunction), new[] { BilOp.Var("a") }));
            body.Instructions.Add(new SetFieldStaticInstruction(BilOp.Var("v"),
                BilOp.Type("com.example::Service"),
                BilOp.Field("com.example::Service#.static.instanceCount@.i64")));
            body.Instructions.Add(new GetFieldStaticIndirectInstruction(BilOp.Var("t"),
                BilOp.Var("tid"), BilOp.Var("fid")));
            body.Instructions.Add(new SetFieldStaticIndirectInstruction(BilOp.Var("v"),
                BilOp.Var("tid"), BilOp.Var("fid")));
            body.Instructions.Add(new GetArrayInstruction(BilOp.Var("arr"), BilOp.Var("i"),
                BilOp.Var("e")));
            body.Instructions.Add(new NewInstruction(BilOp.Type("com.example::User"),
                BilOp.Var("u"), new[] { BilOp.Var("a") }));
            body.Instructions.Add(new NewIndirectInstruction(BilOp.Var("tid"),
                BilOp.Var("u"), new[] { BilOp.Var("a") }));
            body.Instructions.Add(new NewCaseInstruction(BilOp.Type("com.example::RequestResult"),
                BilOp.Case("com.example::RequestResult.Failed"), BilOp.Var("r"),
                new[] { BilOp.Var("e") }));
            body.Instructions.Add(new NewWrappedInstruction(BilOp.Type("com.example::Service"),
                BilOp.Var("svc"), new[] { BilOp.Var("level") }, new[] { BilOp.Var("a") }));
            body.Instructions.Add(new NewWrappedCaseInstruction(
                BilOp.Type("com.example::RequestResult"),
                BilOp.Case("com.example::RequestResult.Failed"), BilOp.Var("r"),
                new[] { BilOp.Var("level") }, new[] { BilOp.Var("e") }));
            body.Instructions.Add(new NewWrapperFieldInstruction(
                BilOp.Field("com.example::Hero#hp@.i32"),
                BilOp.Type("core.clamp::Clamped"), new[] { BilOp.Var("a"), BilOp.Var("b") }));
            body.Instructions.Add(new NewWrapperMethodInstruction(
                BilOp.Fn("com.example::Service$load(id:.i64)@com.example::User"),
                BilOp.Type("core.logging::Timed"), new BilVariableOperand[0]));
            body.Instructions.Add(new NewWrapperEntityInstruction(
                BilOp.Type("core.logging::Logged"), new[] { BilOp.Var("level") }));
            body.Instructions.Add(new InvokeInstruction(
                BilOp.Fn("com.example::Service$load(id:.i64)@com.example::User"), BilOp.Var("r"),
                new[] { BilOp.Var("a"), BilOp.Var("b") }));
            body.Instructions.Add(new InvokeIndirectInstruction(BilOp.Var("fn"),
                BilOp.Var("r"), new[] { BilOp.Var("a") }));
            body.Instructions.Add(new InvokeIndirectNoResultInstruction(BilOp.Var("fn"),
                new[] { BilOp.Var("a") }));
            body.Instructions.Add(new IfInstruction(BilOp.Var("cond"), new BilBlock("then"), null,
                BilOp.Var("brk")));
            body.Instructions.Add(new LoopInstruction(BilOp.Var("cond"), new BilBlock("body"),
                null, new BilBlock("judge"), BilOp.Var("brk"), isRev: false));
            body.Instructions.Add(new LoopInstruction(BilOp.Var("cond"), new BilBlock("body"),
                new BilBlock("enum"), new BilBlock("judge"), BilOp.Var("brk"), isRev: true));
            body.Instructions.Add(new BreakInstruction(BilOp.Var("brk")));
            body.Instructions.Add(new ContinueInstruction(BilOp.Var("brk")));
            body.Instructions.Add(new SwitchInstruction(BilOp.Var("sel"), switchTable,
                new[] { new BilBlock("case0"), new BilBlock("case1") },
                new BilBlock("default"), BilOp.Var("brk")));
            body.Instructions.Add(new TryInstruction(new BilBlock("body"), BilOp.Var("ex"),
                tryCatchTable, null, BilOp.Var("brk")));
            body.Instructions.Add(new ThrowInstruction(BilOp.Var("ex")));
            body.Instructions.Add(new CallBlockInstruction(new BilBlock("helper"),
                BilOp.Var("brk")));
            body.Instructions.Add(new RetInstruction());
            fn.Blocks.Add(body);
            instModule.Functions.Add(fn);

            TestHarness.Check("§10–§16 指令形态抽样", BilWriter.Write(instModule), Lines(
                "BIL \"1.1\"",
                "",
                "Metadata {",
                "}",
                "",
                "Resources {",
                "}",
                "",
                "LocalSymbols {",
                "}",
                "",
                "ExternalSymbols {",
                "}",
                "",
                "fn(com.example::App$.static.test()@.void) {",
                "    .args {",
                "        .return = .void",
                "    }",
                "",
                "    .vars {",
                "        .i32 x",
                "    }",
                "",
                "    .block entry entrypoint {",
                "        cast $a $b type(com.example::User)",
                "        cast.safe $a $b type(com.example::User)",
                "        cast.indirect $a $b $tid",
                "        cast.safe.indirect $a $b $tid",
                "        type.is $a type(com.example::User) $b",
                "        type.supers $a type(com.example::User) $b",
                "        type.with $a type(core.logging::Logged) $b",
                "        type.is.indirect $a $tid $b",
                "        type.is.case $a case(com.example::RequestResult.Failed) $b",
                "        get.wrapper $a type(core.logging::Logged) $b",
                "        get.wrapper.indirect $a $tid $b",
                "        get.wrapper.field $obj field(com.example::Hero#hp@.i32) type(core.clamp::Clamped) $b",
                "        getid.var $a $t",
                "        getid.type type(com.example::User) $t",
                "        getid.field field(com.example::Service#name@.string) $fid",
                "        get.var $a $b",
                "        get.field $obj $t field(com.example::Service#name@.string)",
                "        get.field.indirect $obj $t $fid",
                "        set.field.indirect $v $obj $fid",
                "        set.wrapper.field $v $obj wrapper(core.logging::Logged) field(core.logging::Logged#level@.string)",
                "        get.self $self",
                "        invoke fn(..inner) $r [$a]",
                "        invoke.noret fn(..inner) [$a]",
                "        set.field.static $v type(com.example::Service) field(com.example::Service#.static.instanceCount@.i64)",
                "        get.field.static.indirect $t $tid $fid",
                "        set.field.static.indirect $v $tid $fid",
                "        get.array $arr $i $e",
                "        new type(com.example::User) $u [$a]",
                "        new.indirect $tid $u [$a]",
                "        new.case type(com.example::RequestResult) case(com.example::RequestResult.Failed) $r [$e]",
                "        new.wrapped type(com.example::Service) $svc [$level] [$a]",
                "        new.wrapped.case type(com.example::RequestResult) case(com.example::RequestResult.Failed) $r [$level] [$e]",
                "        new.wrapper.field field(com.example::Hero#hp@.i32) type(core.clamp::Clamped) [$a, $b]",
                "        new.wrapper.method fn(com.example::Service$load(id:.i64)@com.example::User) type(core.logging::Timed) []",
                "        new.wrapper.entity type(core.logging::Logged) [$level]",
                "        invoke fn(com.example::Service$load(id:.i64)@com.example::User) $r [$a, $b]",
                "        invoke.indirect $fn $r [$a]",
                "        invoke.indirect.noret $fn [$a]",
                "        if $cond blk(then) none $brk",
                "        loop $cond blk(body) none blk(judge) $brk",
                "        loop.rev $cond blk(body) blk(enum) blk(judge) $brk",
                "        break $brk",
                "        continue $brk",
                "        switch $sel res(R_T)",
                "            [blk(case0), blk(case1)]",
                "            blk(default)",
                "            $brk",
                "        try blk(body)",
                "            $ex",
                "            res(R_Catches)",
                "            none",
                "            $brk",
                "        throw $ex",
                "        call blk(helper) $brk",
                "        ret",
                "    }",
                "}"));

            // ===== §8.4.1 段内裸成员声明（全局函数，含 native 修饰符串）=====
            // （排版抽样：println 声明无 fn 体、无 fn 段产出，不过验证器）
            var globalModule = new BilModule();
            var consoleType = new BilTypeDeclaration("core.io::Console", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            consoleType.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.StaticMethod,
                "core.io::Console$.static.print(value:.string)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilKeywordModifier(BilKeyword.Native),
                    new BilNativeSymbolModifier("print"),
                    new BilNativeLibraryModifier("rigi_rt"),
                }));
            consoleType.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.StaticMethod,
                "core.io::Console$.static.printErr(value:.string)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilKeywordModifier(BilKeyword.Native),
                    new BilNativeSymbolModifier("printErr"),
                    new BilNativeLibraryModifier("rigi_rt"),
                }));
            consoleType.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.StaticMethod,
                "core.io::Console$.static.println(value:.string)@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            globalModule.LocalSymbols.Add(consoleType);
            // 不属于任何类型的全局函数：裸 .method 直接出现在段内（一级缩进），
            // 顺序在类型声明之后（§8.4.1：条目不强制先后）
            globalModule.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));

            TestHarness.Check("§8.4.1 段内裸成员声明", BilWriter.Write(globalModule), Lines(
                "BIL \"1.1\"",
                "",
                "Metadata {",
                "}",
                "",
                "Resources {",
                "}",
                "",
                "LocalSymbols {",
                "    .type core.io::Console = class pub {",
                "        .static-method core.io::Console$.static.print(value:.string)@.void priv native symbol(\"print\") lib(\"rigi_rt\")",
                "        .static-method core.io::Console$.static.printErr(value:.string)@.void priv native symbol(\"printErr\") lib(\"rigi_rt\")",
                "        .static-method core.io::Console$.static.println(value:.string)@.void pub",
                "    }",
                "    .method $main()@.i32 pub entrypoint",
                "}",
                "",
                "ExternalSymbols {",
                "}"));

            // ===== Origin 调试链占位（ARCHITECTURE §6.3：反序列化/手工构造恒为 null）=====
            TestHarness.CheckTrue("Origin 默认 null", entry.Instructions[0].Origin == null);

            return TestHarness.Summary("BilWriter");
        }

        // 黄金文本拼装：显式 \n，与源文件换行编码无关（autocrlf 免疫）
        private static string Lines(params string[] lines)
        {
            return string.Join("\n", lines) + "\n";
        }
    }
}
