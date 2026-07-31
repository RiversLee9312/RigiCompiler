using LatteCompiler.Bil;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// S4 BIL 对象模型 + BilWriter 测试（M38）：手工构造 BIL §19 完整示例的
    /// 内存模型，输出与规范文本逐行一致（黄金文件断言）；另覆盖 §18 资源
    /// 全形态、§8 声明形态与 §10–§16 指令形态抽样。
    /// 黄金文本经 Lines(...) 显式拼 \n，与源文件换行编码无关。
    /// </summary>
    public static class BilWriterTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestHarness.Section("BilWriter");

            // ===== §19 完整黄金示例（逐行一致）=====
            var module = new BilModule();
            module.Metadata.Add(new BilMetadataEntry("module", "string", "\"com.example.app\""));
            module.Resources.Add(new BilScalarResource("R_Hello", "string", "\"hello, world\""));
            module.Resources.Add(new BilScalarResource("R_Zero", "i32", "0"));

            var app = new BilTypeDeclaration("com.example::App", "class", "pub");
            app.Members.Add(new BilSimpleMemberDeclaration(".static-method",
                "com.example::App$.static.main(args:.array<.string>)@.i32",
                new[] { "pub", "entrypoint" }));
            module.LocalSymbols.Add(app);

            var console = new BilTypeDeclaration("core::Console", "class", "pub");
            console.Members.Add(new BilSimpleMemberDeclaration(".static-method",
                "core::Console$.static.println(value:.string)@.void",
                new[] { "pub" }));
            module.ExternalSymbols.Add(console);

            var main = new BilFunction("com.example::App$.static.main(args:.array<.string>)@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Args.Add(new BilArgDeclaration("args", ".array<.string>"));
            main.Vars.Add(new BilVarDeclaration(".string", "message"));
            main.Vars.Add(new BilVarDeclaration(".i32", "result"));
            var entry = new BilBlock("entry", "entrypoint");
            entry.Instructions.Add(new BilInstruction("load", BilOp.Res("R_Hello"), BilOp.Var("message")));
            entry.Instructions.Add(new BilInstruction("invoke.noret",
                BilOp.Fn("core::Console$.static.println(value:.string)@.void"),
                BilOp.List(BilOp.Var("message"))));
            entry.Instructions.Add(new BilInstruction("load", BilOp.Res("R_Zero"), BilOp.Var("result")));
            entry.Instructions.Add(new BilInstruction("ret", BilOp.Var("result")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);

            TestHarness.Check("§19 完整黄金示例", BilWriter.Write(module), Lines(
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

            // ===== §19 wrapper 隐藏字段示例（修饰符续行形态）=====
            var wrapperModule = new BilModule();
            var service = new BilTypeDeclaration("com.example::Service", "class", "pub");
            service.Members.Add(new BilSimpleMemberDeclaration(".field",
                "com.example::Service#.wrapper.core.logging::Logged@core.logging::Logged",
                new[] { "priv", "backing", "compiler-generated" },
                modifiersOnNextLine: true));
            wrapperModule.LocalSymbols.Add(service);

            TestHarness.Check("§19 wrapper 隐藏字段示例", BilWriter.Write(wrapperModule), Lines(
                "BIL \"1.1\"",
                "",
                "Metadata {",
                "}",
                "",
                "Resources {",
                "}",
                "",
                "LocalSymbols {",
                "    .type com.example::Service = class pub {",
                "        .field com.example::Service#.wrapper.core.logging::Logged@core.logging::Logged",
                "            priv backing compiler-generated",
                "    }",
                "}",
                "",
                "ExternalSymbols {",
                "}"));

            // ===== §18 资源全形态 =====
            var resModule = new BilModule();
            resModule.Resources.Add(new BilScalarResource("R_Message", "string", "\"hello, world\""));
            resModule.Resources.Add(new BilScalarResource("R_Enabled", "bool", "true"));
            resModule.Resources.Add(new BilScalarResource("R_Count", "i64", "123"));
            resModule.Resources.Add(new BilScalarResource("R_Ratio", "f64", "0.5"));
            resModule.Resources.Add(new BilNullResource("R_NullUser", "com.example::User"));
            resModule.Resources.Add(new BilCollectionResource("R_Names", "array<string>",
                new[] { "\"a\"", "\"b\"" }));
            resModule.Resources.Add(new BilCollectionResource("R_Entry", "pair<string, i64>",
                new[] { "\"count\"", "2" }));
            resModule.Resources.Add(new BilCollectionResource("R_Map", "map<string, i64>",
                new[] { "\"a\" = 1", "\"b\" = 2" }, multiline: true));
            resModule.Resources.Add(new BilScalarResource("R_DataHex", "raw.hex", "x2FF2331C"));
            resModule.Resources.Add(new BilScalarResource("R_DataBin", "raw.bin", "b01010101"));
            resModule.Resources.Add(new BilCollectionResource("R_Switch", "switch-table<.i32>",
                new[] { "1", "2", "3" }));
            resModule.Resources.Add(new BilCollectionResource("R_Catches", "catch-table",
                new[] { "type(core::IOException) -> blk(catchIo)",
                        "type(core::RuntimeException) -> blk(catchRuntime)" }, multiline: true));

            TestHarness.Check("§18 资源全形态", BilWriter.Write(resModule), Lines(
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

            // ===== §8.2 extends/implements 多行形态 + §8.5 enum case =====
            var declModule = new BilModule();
            var dog = new BilTypeDeclaration("com.example::Dog", "class", "pub");
            dog.ExtendsType = "com.example::Animal";
            dog.ImplementsTypes.Add("com.example::IPet");
            dog.ImplementsTypes.Add("com.example::INamed");
            declModule.LocalSymbols.Add(dog);
            var result = new BilTypeDeclaration("com.example::RequestResult", "enum-struct", "pub");
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
            var instModule = new BilModule();
            var fn = new BilFunction("com.example::App$.static.test()@.void");
            fn.Args.Add(new BilArgDeclaration(".return", ".void"));
            fn.Vars.Add(new BilVarDeclaration(".i32", "x"));
            var body = new BilBlock("entry", "entrypoint");
            body.Instructions.Add(new BilInstruction("cast", BilOp.Var("a"), BilOp.Var("b"), BilOp.Type("com.example::User")));
            body.Instructions.Add(new BilInstruction("type.is", BilOp.Var("a"), BilOp.Type("com.example::User"), BilOp.Var("b")));
            body.Instructions.Add(new BilInstruction("get.wrapper", BilOp.Var("a"), BilOp.Type("core.logging::Logged"), BilOp.Var("b")));
            body.Instructions.Add(new BilInstruction("getid.var", BilOp.Var("a"), BilOp.Var("t")));
            body.Instructions.Add(new BilInstruction("get.var", BilOp.Var("a"), BilOp.Var("b")));
            body.Instructions.Add(new BilInstruction("get.field", BilOp.Var("obj"), BilOp.Var("t"), BilOp.Field("com.example::Service#name@.string")));
            body.Instructions.Add(new BilInstruction("set.field.static", BilOp.Var("v"), BilOp.Type("com.example::Service"), BilOp.Field("com.example::Service#.static.instanceCount@.i64")));
            body.Instructions.Add(new BilInstruction("get.array", BilOp.Var("arr"), BilOp.Var("i"), BilOp.Var("e")));
            body.Instructions.Add(new BilInstruction("new", BilOp.Type("com.example::User"), BilOp.Var("u"), BilOp.List(BilOp.Var("a"))));
            body.Instructions.Add(new BilInstruction("new.case", BilOp.Type("com.example::RequestResult"), BilOp.Case("com.example::RequestResult.Failed"), BilOp.Var("r"), BilOp.List(BilOp.Var("e"))));
            body.Instructions.Add(new BilInstruction("invoke", BilOp.Fn("com.example::Service$load(id:.i64)@com.example::User"), BilOp.Var("r"), BilOp.List(BilOp.Var("a"), BilOp.Var("b"))));
            body.Instructions.Add(new BilInstruction("if", BilOp.Var("cond"), BilOp.Blk("then"), BilOp.None));
            body.Instructions.Add(new BilInstruction("loop", BilOp.Var("cond"), BilOp.Blk("body"), BilOp.None, BilOp.Blk("judge"), BilOp.Var("brk")));
            body.Instructions.Add(new BilInstruction("loop.rev", BilOp.Var("cond"), BilOp.Blk("body"), BilOp.Blk("enum"), BilOp.Blk("judge"), BilOp.Var("brk")));
            body.Instructions.Add(new BilInstruction("break", BilOp.Var("brk")));
            body.Instructions.Add(new BilInstruction("continue", BilOp.Var("brk")));
            body.Instructions.Add(new BilInstruction("switch", BilOp.Var("sel"), BilOp.Res("R_T"),
                BilOp.List(BilOp.Blk("case0"), BilOp.Blk("case1")), BilOp.Blk("default"), BilOp.Var("brk")));
            body.Instructions.Add(new BilInstruction("try", BilOp.Blk("body"), BilOp.Var("ex"), BilOp.Res("R_Catches"), BilOp.None));
            body.Instructions.Add(new BilInstruction("throw", BilOp.Var("ex")));
            body.Instructions.Add(new BilInstruction("call", BilOp.Blk("helper")));
            body.Instructions.Add(new BilInstruction("ret"));
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
                "        type.is $a type(com.example::User) $b",
                "        get.wrapper $a type(core.logging::Logged) $b",
                "        getid.var $a $t",
                "        get.var $a $b",
                "        get.field $obj $t field(com.example::Service#name@.string)",
                "        set.field.static $v type(com.example::Service) field(com.example::Service#.static.instanceCount@.i64)",
                "        get.array $arr $i $e",
                "        new type(com.example::User) $u [$a]",
                "        new.case type(com.example::RequestResult) case(com.example::RequestResult.Failed) $r [$e]",
                "        invoke fn(com.example::Service$load(id:.i64)@com.example::User) $r [$a, $b]",
                "        if $cond blk(then) none",
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
                "        throw $ex",
                "        call blk(helper)",
                "        ret",
                "    }",
                "}"));

            // ===== §8.4.1 段内裸成员声明（全局函数，含 native 修饰符串）=====
            var globalModule = new BilModule();
            var consoleType = new BilTypeDeclaration("core.io::Console", "class", "pub");
            consoleType.Members.Add(new BilSimpleMemberDeclaration(".static-method",
                "core.io::Console$.static.print(value:.string)@.void",
                new[] { "priv", "native", "symbol(\"print\")", "lib(\"latte_rt\")" }));
            consoleType.Members.Add(new BilSimpleMemberDeclaration(".static-method",
                "core.io::Console$.static.printErr(value:.string)@.void",
                new[] { "priv", "native", "symbol(\"printErr\")", "lib(\"latte_rt\")" }));
            consoleType.Members.Add(new BilSimpleMemberDeclaration(".static-method",
                "core.io::Console$.static.println(value:.string)@.void",
                new[] { "pub" }));
            globalModule.LocalSymbols.Add(consoleType);
            // 不属于任何类型的全局函数：裸 .method 直接出现在段内（一级缩进），
            // 顺序在类型声明之后（§8.4.1：条目不强制先后）
            globalModule.LocalSymbols.Add(new BilSimpleMemberDeclaration(".method",
                "$main()@.i32",
                new[] { "pub", "entrypoint" }));

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
                "        .static-method core.io::Console$.static.print(value:.string)@.void priv native symbol(\"print\") lib(\"latte_rt\")",
                "        .static-method core.io::Console$.static.printErr(value:.string)@.void priv native symbol(\"printErr\") lib(\"latte_rt\")",
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
