using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // ObjectFixtures 职责；与主文件共享同一类型、字段及生命周期。

        // V2.5：拆除「单 i32 = 长度」特权。new .array [2] 是 1 元数组
        //（元素为 2），不再分配长度 2 的零数组。源码 `new Array<T>(n)`
        // 由 P3 拒绝（§9.3 隐式默认构造只接受零参，不接受长度）。
        private static void TestBuiltinArrayDirectModule()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_N", BilScalarType.I32, "2"));
            module.Resources.Add(new BilScalarResource("R_Z", BilScalarType.I32, "0"));
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));
            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "n"));
            main.Vars.Add(new BilVarDeclaration(".i32", "z"));
            main.Vars.Add(new BilVarDeclaration(".array<.i32>", "a"));
            // Q6：get.array 结果 = .nullable<.i32>（索引读取恒可空）
            main.Vars.Add(new BilVarDeclaration(".nullable<.i32>", "x"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("n")));
            entry.Instructions.Add(new LoadInstruction(module.Resources[1], BilOp.Var("z")));
            entry.Instructions.Add(new NewInstruction(BilOp.Type(".array<.i32>"),
                BilOp.Var("a"), new[] { BilOp.Var("n") }));
            entry.Instructions.Add(new GetArrayInstruction(BilOp.Var("a"), BilOp.Var("z"),
                BilOp.Var("x")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            var result = BilVm.Run(module);
            CheckOk("new .array 不再把单 i32 当长度", result);
            TestHarness.CheckTrue("元素是 2 不是零（长度特权已拆除；Q6 包 Nullable）",
                result.ReturnValue is VmNullable { HasValue: true, Value: VmI32 { Value: 2 } });

            var (unit, _, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var a = new Array\\<i32>(3)\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckTrue("源码 new Array<T>(n) 不接受长度参数",
                unit.Diagnostics.HasErrors
                && unit.Diagnostics.Diagnostics.Any(d =>
                    d.Message.Contains("Too many arguments for 'init'", StringComparison.Ordinal)),
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => d.Message)));
        }

        private static void TestArrayOfI32()
        {
            var result = Run(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    a[0] = 10\n" +
                "    a[1] = 20\n" +
                "    a[2] = 12\n" +
                "    return (((a[0] if? 0) + (a[1] if? 0)) + (a[2] if? 0))\n" +
                "}\n");
            CheckOk("arrayOf<i32>", result);
            CheckI32("arrayOf 内容 10+20+12", result, 42);
        }

        private static void TestArrayOfElementsString()
        {
            var result = Run(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOfElements\\<String>(\"Hello, \", \"world!\")\n" +
                "    core.io.Console.println(((a[0] if? \"\") + (a[1] if? \"\")))\n" +
                "    return a.length\n" +
                "}\n");
            CheckOk("arrayOfElements<String>", result);
            TestHarness.Check("arrayOfElements stdout", result.Stdout, "Hello, world!\n");
            CheckI32("arrayOfElements.length", result, 2);
        }

        // new.wrapped / new.wrapper.entity 安装语义：frontend 对无参
        // ..init.wrapper 走普通 new（§14.4 互斥），有参形态此处直接构造。
        private static void TestWrapperInstallDirectModule()
        {
            var module = WrapperHostModule();
            var result = BilVm.Run(module);
            CheckOk("wrapper 安装", result);
            TestHarness.CheckTrue("返回宿主", result.ReturnValue is VmObject host
                && host.TypeRef == "Host"
                && host.TryReadHidden(VmContext.HiddenEntityKey("Wrap"), out var stored)
                && stored is VmObject wrapper
                && wrapper.TryReadField("Wrap#level@.i32", out var level)
                && level is VmI32 n && n.Value == 9,
                result.ReturnValue?.ToStandardText() ?? "<null>");
        }

        // get.self 仅 proxy 模板体内合法；proxy 派发属 V3，此处直接把
        // 已安装 Host 的 wrapper 作为 .this 压入模板 fn。
        private static void TestGetSelfDirectModule()
        {
            var module = GetSelfModule();
            var host = new VmObject("Host", valueType: false);
            host.WriteField("Host#n@.i32", new VmI32(9));
            var wrapper = new VmWrapperReceiver(new VmObject("Wrap", valueType: true), host);
            var result = RunPrepared(module, "Wrap$.proxy.read()@.i32", new VmValue[] { wrapper });
            CheckOk("get.self", result);
            CheckI32("self.n", result, 9);
        }

        private static BilModule WrapperHostModule()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_L", BilScalarType.I32, "5"));
            module.Resources.Add(new BilScalarResource("R_L2", BilScalarType.I32, "9"));
            var wrap = new BilTypeDeclaration("Wrap", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            wrap.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "Wrap#level@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            wrap.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Wrap$init(level:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            module.LocalSymbols.Add(wrap);
            var host = new BilTypeDeclaration("Host", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilWrappedModifier("Wrap"));
            host.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Host$init()@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            host.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Host$..init.wrapper(level:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilKeywordModifier(BilKeyword.CompilerGenerated),
                }));
            module.LocalSymbols.Add(host);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@Host",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));

            var wrapInit = new BilFunction("Wrap$init(level:.i32)@.void");
            wrapInit.Args.Add(new BilArgDeclaration(".return", ".void"));
            wrapInit.Args.Add(new BilArgDeclaration(".this", "Wrap"));
            wrapInit.Args.Add(new BilArgDeclaration("level", ".i32"));
            var wrapEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            wrapEntry.Instructions.Add(new SetFieldInstruction(BilOp.Var("level"),
                BilOp.Var(".this"), BilOp.Field("Wrap#level@.i32")));
            wrapEntry.Instructions.Add(new RetInstruction());
            wrapInit.Blocks.Add(wrapEntry);
            module.Functions.Add(wrapInit);

            var hostInit = new BilFunction("Host$init()@.void");
            hostInit.Args.Add(new BilArgDeclaration(".return", ".void"));
            hostInit.Args.Add(new BilArgDeclaration(".this", "Host"));
            var hostInitEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            hostInitEntry.Instructions.Add(new RetInstruction());
            hostInit.Blocks.Add(hostInitEntry);
            module.Functions.Add(hostInit);

            var initWrapper = new BilFunction("Host$..init.wrapper(level:.i32)@.void");
            initWrapper.Args.Add(new BilArgDeclaration(".return", ".void"));
            initWrapper.Args.Add(new BilArgDeclaration(".this", "Host"));
            initWrapper.Args.Add(new BilArgDeclaration("level", ".i32"));
            var wrapperEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            wrapperEntry.Instructions.Add(new NewWrapperEntityInstruction(BilOp.Type("Wrap"),
                new[] { BilOp.Var("level") }));
            wrapperEntry.Instructions.Add(new RetInstruction());
            initWrapper.Blocks.Add(wrapperEntry);
            module.Functions.Add(initWrapper);

            var main = new BilFunction("$main()@Host");
            main.Args.Add(new BilArgDeclaration(".return", "Host"));
            main.Vars.Add(new BilVarDeclaration(".i32", "lv"));
            main.Vars.Add(new BilVarDeclaration(".i32", "lv2"));
            main.Vars.Add(new BilVarDeclaration("Host", "h"));
            var mainEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            mainEntry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("lv")));
            mainEntry.Instructions.Add(new LoadInstruction(module.Resources[1], BilOp.Var("lv2")));
            mainEntry.Instructions.Add(new NewWrappedInstruction(BilOp.Type("Host"),
                BilOp.Var("h"), new[] { BilOp.Var("lv") },
                Array.Empty<BilVariableOperand>()));
            mainEntry.Instructions.Add(new SetWrapperFieldInstruction(BilOp.Var("lv2"),
                BilOp.Var("h"), BilOp.Wrapper("Wrap"),
                BilOp.Field("Wrap#level@.i32")));
            mainEntry.Instructions.Add(new RetInstruction(BilOp.Var("h")));
            main.Blocks.Add(mainEntry);
            module.Functions.Add(main);
            return module;
        }

        private static BilModule GetSelfModule()
        {
            var module = new BilModule();
            var wrap = new BilTypeDeclaration("Wrap", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            wrap.GenericParameters.Add("TTarget");
            wrap.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Wrap$.proxy.read()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilWrapperProxyModifier(BilProxyKind.Specific),
                }));
            module.LocalSymbols.Add(wrap);
            var host = new BilTypeDeclaration("Host", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            host.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "Host#n@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            module.LocalSymbols.Add(host);
            var proxy = new BilFunction("Wrap$.proxy.read()@.i32");
            proxy.Args.Add(new BilArgDeclaration(".return", ".i32"));
            proxy.Args.Add(new BilArgDeclaration(".this", "Wrap"));
            proxy.Vars.Add(new BilVarDeclaration(".generic<$.generic.TTarget>", "s"));
            proxy.Vars.Add(new BilVarDeclaration(".i32", "n"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new GetSelfInstruction(BilOp.Var("s")));
            entry.Instructions.Add(new GetFieldInstruction(BilOp.Var("s"), BilOp.Var("n"),
                BilOp.Field("Host#n@.i32")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("n")));
            proxy.Blocks.Add(entry);
            module.Functions.Add(proxy);
            return module;
        }

    }
}
