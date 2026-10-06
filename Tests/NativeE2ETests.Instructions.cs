using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Middleware.Cli;
using RigiCompiler.Middleware.Emit;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests
{
    public static partial class NativeE2ETests
    {
        // Instructions 职责；与主文件共享同一类型、字段及生命周期。

        // getid.field + get/set.field.indirect + get/set.field.static.indirect
        //（§12.6/§13.5，前端尚不发射——BilVmTests.IndirectBoxModule 同构
        // 手工模块，返回实例字段与静态字段之和验证双向读写）
        private static void RunFieldIndirectCase()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_7", BilScalarType.I32, "7"));
            var box = new BilTypeDeclaration("Box", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            box.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field, "Box#n@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            box.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.StaticField,
                "Box#.static.tag@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            box.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Box$init()@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            module.LocalSymbols.Add(box);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));
            var init = new BilFunction("Box$init()@.void");
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", "Box"));
            var initEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            initEntry.Instructions.Add(new RetInstruction());
            init.Blocks.Add(initEntry);
            module.Functions.Add(init);

            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "v"));
            main.Vars.Add(new BilVarDeclaration("Box", "obj"));
            main.Vars.Add(new BilVarDeclaration(".typeid<Box>", "tid"));
            main.Vars.Add(new BilVarDeclaration(".fieldid<Box, .i32, instance>", "fid"));
            main.Vars.Add(new BilVarDeclaration(".fieldid<Box, .i32, static>", "sfid"));
            main.Vars.Add(new BilVarDeclaration(".i32", "r"));
            main.Vars.Add(new BilVarDeclaration(".i32", "r2"));
            main.Vars.Add(new BilVarDeclaration(".i32", "sum"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("v")));
            entry.Instructions.Add(new GetIdTypeInstruction(BilOp.Type("Box"), BilOp.Var("tid")));
            entry.Instructions.Add(new GetIdFieldInstruction(BilOp.Field("Box#n@.i32"),
                BilOp.Var("fid")));
            entry.Instructions.Add(new GetIdFieldInstruction(
                BilOp.Field("Box#.static.tag@.i32"), BilOp.Var("sfid")));
            entry.Instructions.Add(new NewIndirectInstruction(BilOp.Var("tid"),
                BilOp.Var("obj"), Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new SetFieldIndirectInstruction(BilOp.Var("v"),
                BilOp.Var("obj"), BilOp.Var("fid")));
            entry.Instructions.Add(new SetFieldStaticIndirectInstruction(BilOp.Var("v"),
                BilOp.Var("tid"), BilOp.Var("sfid")));
            entry.Instructions.Add(new GetFieldIndirectInstruction(BilOp.Var("obj"),
                BilOp.Var("r"), BilOp.Var("fid")));
            entry.Instructions.Add(new GetFieldStaticIndirectInstruction(BilOp.Var("r2"),
                BilOp.Var("tid"), BilOp.Var("sfid")));
            entry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("r"), BilOp.Var("r2"), BilOp.Var("sum")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("sum")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            RunBilCase("getid.field + field.indirect 族（BIL 级）", BilWriter.Write(module));
        }

        // new.wrapped.case（§14.4.2，前端尚不发射）：带参
        // ..init.wrapper 的 enum case 构造——wrapper 实参先行（体内写
        // 静态字段作可观测副作用），case 实参随后进 init。返回
        // v + 静态标记验证双序执行（7 + 3 = 10）
        private static void RunNewWrappedCaseCase()
        {
            RunBilCase("new.wrapped.case（BIL 级）",
                BilWriter.Write(NewWrappedCaseModule(mismatch: false)));
        }

        // new.wrapped.case 的 case 实参不匹配任何 init：VM 运行期抛
        // 「new.case 实参不匹配任何 init」（VM 不过门禁）；native 由
        // BilGate（BilVerifier §14.3/§14.4.2）编译期拒绝——同一非法
        // 模块双侧同拒（消息关键字对齐）
        private static void RunNewWrappedCaseRejectCase()
        {
            var text = BilWriter.Write(NewWrappedCaseModule(mismatch: true));
            var vm = BilVm.Run(BilReader.Read(text));
            CaseAssertions.CheckTrue("new.wrapped.case init 失配：VM 有异常",
                vm.Exception != null);
            CaseAssertions.CheckTrue("new.wrapped.case init 失配：VM 消息含关键字",
                vm.Exception != null && vm.Exception.Message.Contains("不匹配任何 init"),
                vm.Exception?.Message ?? "");

            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var bilPath = Path.Combine(dir, "case.bil");
                File.WriteAllText(bilPath, text, new UTF8Encoding(false));
                var compiled = RunNative("native", "--file", bilPath,
                    "--out", Path.Combine(dir, "case.exe"));
                CaseAssertions.CheckTrue("new.wrapped.case init 失配：native 门禁拒绝（退出 1）",
                    compiled.Code == 1, $"code={compiled.Code} err={compiled.Err}");
                CaseAssertions.CheckTrue("new.wrapped.case init 失配：native 消息含关键字",
                    compiled.Err.Contains("不匹配"), compiled.Err);
            }
            finally
            {
                DeleteNativeTestDirectory(dir);
            }
        }

        // raw.hex/raw.bin → core::Span<u8>/core::SharedSpan<u8>（§19.3 字节
        // 序列；L5 资源面）：VM 对 raw load 整体无物化语义（VmContext
        // LoadResource 拒绝）——先钉住该取证，native 侧按字节缓冲区语义
        // 物化（span_alloc + 静态字节常量 memcpy）。exit = 4 + 2 = 6
        //（两缓冲区 length 字段之和），全程 native-only 验证
        private static void RunRawBufferSpanCase()
        {
            const string bil =
                "BIL \"1.1\"\n" +
                "\n" +
                "Metadata {\n" +
                "    module = string \"rawspan\"\n" +
                "}\n" +
                "\n" +
                "Resources {\n" +
                "    R_Data = raw.hex x2FF2331C,\n" +
                "    R_Bits = raw.bin b0101010101010101\n" +
                "}\n" +
                "\n" +
                "LocalSymbols {\n" +
                "    .method $main()@.i32 pub entrypoint\n" +
                "}\n" +
                "\n" +
                "ExternalSymbols {\n" +
                "}\n" +
                "\n" +
                "fn($main()@.i32) {\n" +
                "    .args {\n" +
                "        .return = .i32\n" +
                "    }\n" +
                "\n" +
                "    .vars {\n" +
                "        core::Span<.u8> d,\n" +
                "        core::SharedSpan<.u8> b,\n" +
                "        .i32 n,\n" +
                "        .i32 m,\n" +
                "        .i32 r\n" +
                "    }\n" +
                "\n" +
                "    .block entry entrypoint {\n" +
                "        load res(R_Data) $d\n" +
                "        load res(R_Bits) $b\n" +
                "        get.field $d $n field(core::Span#length@.i32)\n" +
                "        get.field $b $m field(core::SharedSpan#length@.i32)\n" +
                "        add $n $m $r\n" +
                "        ret $r\n" +
                "    }\n" +
                "}\n";
            // 取证钉住：VM 对 raw 资源 load 拒绝（无物化语义可对照，
            // 故本面只能 native-only 验证，不走 RunBilCase 对拍）
            var vm = BilVm.Run(BilReader.Read(bil));
            CaseAssertions.CheckTrue("raw → Span：VM 拒绝 raw load（取证）",
                vm.Exception != null
                && vm.Exception.Message.Contains("不支持的标量资源类型"),
                vm.Exception?.Message ?? "");

            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var bilPath = Path.Combine(dir, "case.bil");
                File.WriteAllText(bilPath, bil, new UTF8Encoding(false));
                var exePath = Path.Combine(dir,
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "case.exe" : "case");
                var compiled = RunNative("native", "--file", bilPath, "--out", exePath);
                CaseAssertions.CheckTrue("raw → Span：native 编译链接成功", compiled.Code == 0,
                    compiled.Err);
                if (compiled.Code != 0)
                {
                    return;
                }
                var runExit = ExternalProcess.Run(exePath, Array.Empty<string>(),
                    out var nativeOut, out var nativeErr, environment: MemtrackEnv);
                CaseAssertions.CheckTrue("raw → Span：退出码 6（两 length 之和）",
                    runExit == 6, $"exit={runExit} stderr={nativeErr}");
                CaseAssertions.CheckTrue("raw → Span：stdout 为空", nativeOut.Length == 0,
                    nativeOut);
            }
            finally
            {
                DeleteNativeTestDirectory(dir);
            }
        }

        // enum E（字段 v + 静态 tag + 有参 ..init.wrapper）：case E.Param
        // 洞实参 x 进 init；wrapper 实参 t 进 ..init.wrapper（写静态
        // tag）。mismatch = case 实参多给一个（不匹配任何 init）
        private static BilModule NewWrappedCaseModule(bool mismatch)
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_7", BilScalarType.I32, "7"));
            module.Resources.Add(new BilScalarResource("R_3", BilScalarType.I32, "3"));
            var e = new BilTypeDeclaration("E", BilTypeKind.EnumStruct,
                new BilAccessibilityModifier(BilAccessibility.Public));
            e.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field, "E#v@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            e.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.StaticField,
                "E#.static.tag@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            e.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "E$init(x:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            e.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "E$..init.wrapper(t:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilKeywordModifier(BilKeyword.CompilerGenerated),
                }));
            e.Members.Add(new BilCaseDeclaration("E.Fixed"));
            e.Members.Add(new BilCaseDeclaration("E.Param",
                new[] { new BilCaseParameter("v", ".i32") }));
            module.LocalSymbols.Add(e);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));

            var init = new BilFunction("E$init(x:.i32)@.void");
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", "E"));
            init.Args.Add(new BilArgDeclaration("x", ".i32"));
            var initEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            initEntry.Instructions.Add(new SetFieldInstruction(BilOp.Var("x"),
                BilOp.Var(".this"), BilOp.Field("E#v@.i32")));
            initEntry.Instructions.Add(new RetInstruction());
            init.Blocks.Add(initEntry);
            module.Functions.Add(init);

            var wrapper = new BilFunction("E$..init.wrapper(t:.i32)@.void");
            wrapper.Args.Add(new BilArgDeclaration(".return", ".void"));
            wrapper.Args.Add(new BilArgDeclaration(".this", "E"));
            wrapper.Args.Add(new BilArgDeclaration("t", ".i32"));
            var wrapperEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            wrapperEntry.Instructions.Add(new SetFieldStaticInstruction(BilOp.Var("t"),
                BilOp.Type("E"), BilOp.Field("E#.static.tag@.i32")));
            wrapperEntry.Instructions.Add(new RetInstruction());
            wrapper.Blocks.Add(wrapperEntry);
            module.Functions.Add(wrapper);

            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
            main.Vars.Add(new BilVarDeclaration(".i32", "t"));
            main.Vars.Add(new BilVarDeclaration("E", "e"));
            main.Vars.Add(new BilVarDeclaration(".i32", "r"));
            main.Vars.Add(new BilVarDeclaration(".i32", "s"));
            main.Vars.Add(new BilVarDeclaration(".i32", "sum"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("x")));
            entry.Instructions.Add(new LoadInstruction(module.Resources[1], BilOp.Var("t")));
            entry.Instructions.Add(new NewWrappedCaseInstruction(BilOp.Type("E"),
                BilOp.Case("E.Param"), BilOp.Var("e"),
                new[] { BilOp.Var("t") },
                mismatch
                    ? new[] { BilOp.Var("x"), BilOp.Var("t") }
                    : (IReadOnlyList<BilVariableOperand>)new[] { BilOp.Var("x") }));
            entry.Instructions.Add(new GetFieldInstruction(BilOp.Var("e"), BilOp.Var("r"),
                BilOp.Field("E#v@.i32")));
            entry.Instructions.Add(new GetFieldStaticInstruction(BilOp.Var("s"),
                BilOp.Type("E"), BilOp.Field("E#.static.tag@.i32")));
            entry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("r"), BilOp.Var("s"), BilOp.Var("sum")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("sum")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

    }
}
