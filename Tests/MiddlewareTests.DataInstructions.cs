using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RigiCompiler.Bil;
using RigiCompiler.Middleware;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Cli;
using RigiCompiler.Middleware.Emit;
using RigiCompiler.Middleware.Gate;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Passes;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Toolchain;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Tests
{
    public static partial class MiddlewareTests
    {
        // DataInstructions 职责；与主文件共享同一类型、字段及生命周期。

        // ===== raw.hex/raw.bin 字节缓冲区物化（§19.3；L5 资源面）=====
        // 合法 load 目标 = 字节缓冲区三族：.array<u8>（TestArrayPathEmission
        // 已覆盖）/ core::Span<u8> / core::SharedSpan<u8>。VM 对 raw load
        // 无物化语义（LoadResource 拒绝），本面 LL 级断言 + NativeE2E
        // native-only 端到端（RunRawBufferSpanCase）

        private static void TestRawBufferEmission()
        {
            // raw.hex → core::Span<u8>：span_alloc + 静态字节常量 + memcpy
            const string spanBil =
                "BIL \"1.1\"\n" +
                "\n" +
                "Metadata {\n" +
                "    module = string \"rawspan\"\n" +
                "}\n" +
                "\n" +
                "Resources {\n" +
                "    R_Data = raw.hex x2FF2331C,\n" +
                "    R_Zero = i32 0\n" +
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
                "    .vars {\n" +
                "        core::Span<.u8> d,\n" +
                "        .i32 r\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        load res(R_Data) $d\n" +
                "        load res(R_Zero) $r\n" +
                "        ret $r\n" +
                "    }\n" +
                "}\n";
            var spanGate = BilGate.Accept(spanBil, "rawspan.bil");
            CaseAssertions.CheckTrue("raw→Span 门禁放行", spanGate.IsAccepted,
                string.Join("; ", spanGate.Errors));
            var spanContext = new MwContext(spanGate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(spanContext);
            using var llvmLease5540 = LlvmHost.Enter();
            using var spanModule = ModuleBuilder.Build(spanContext, spanContext.Mir!);
            var spanLl = spanModule.PrintToString();
            CaseAssertions.CheckTrue("raw→Span 字节常量",
                spanLl.Contains("@raw.R_Data") && spanLl.Contains("c\"/\\F23\\1C\""), spanLl);
            CaseAssertions.CheckTrue("raw→Span 走 span_alloc",
                spanLl.Contains("call ptr @rigi_span_alloc(ptr"), spanLl);
            CaseAssertions.CheckTrue("raw→Span 具化 sheet",
                spanLl.Contains("typesheet.core::Span<core::u8>"), spanLl);

            // raw.bin → core::SharedSpan<u8>（b0101010101010101 = 0x55 0x55）
            const string sharedBil =
                "BIL \"1.1\"\n" +
                "\n" +
                "Metadata {\n" +
                "    module = string \"rawshared\"\n" +
                "}\n" +
                "\n" +
                "Resources {\n" +
                "    R_Bits = raw.bin b0101010101010101,\n" +
                "    R_Zero = i32 0\n" +
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
                "    .vars {\n" +
                "        core::SharedSpan<.u8> b,\n" +
                "        .i32 r\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        load res(R_Bits) $b\n" +
                "        load res(R_Zero) $r\n" +
                "        ret $r\n" +
                "    }\n" +
                "}\n";
            var sharedGate = BilGate.Accept(sharedBil, "rawshared.bil");
            CaseAssertions.CheckTrue("raw→SharedSpan 门禁放行", sharedGate.IsAccepted,
                string.Join("; ", sharedGate.Errors));
            var sharedContext = new MwContext(sharedGate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(sharedContext);
            using var llvmLease5588 = LlvmHost.Enter();
            using var sharedModule = ModuleBuilder.Build(sharedContext, sharedContext.Mir!);
            var sharedLl = sharedModule.PrintToString();
            CaseAssertions.CheckTrue("raw→SharedSpan 字节常量",
                sharedLl.Contains("@raw.R_Bits") && sharedLl.Contains("c\"UU\""), sharedLl);
            CaseAssertions.CheckTrue("raw→SharedSpan 走 span_alloc",
                sharedLl.Contains("call ptr @rigi_span_alloc(ptr"), sharedLl);
            CaseAssertions.CheckTrue("raw→SharedSpan 具化 sheet FlagShared",
                SheetHasFlags(sharedLl, "typesheet.core::SharedSpan<core::u8>", 32, 18),
                sharedLl);

            // 负例：raw → .i32（字节缓冲区外目标无 §19.3 语义；VM 同拒）
            var scalarBil = spanBil.Replace("core::Span<.u8> d", ".i32 d")
                .Replace("rawspan", "rawscalar");
            var scalarGate = BilGate.Accept(scalarBil, "rawscalar.bil");
            CaseAssertions.CheckTrue("raw→i32 门禁放行（verifier 跳过严格匹配）",
                scalarGate.IsAccepted, string.Join("; ", scalarGate.Errors));
            var scalarCaught = false;
            try
            {
                var scalarContext = new MwContext(scalarGate.Module!);
                var scalarMir = MirBuilder.Build(scalarContext);
                using var llvmLease5609 = LlvmHost.Enter();
                using var scalarModule = ModuleBuilder.Build(scalarContext, scalarMir);
            }
            catch (MwNotSupportedException ex)
            {
                scalarCaught = ex.Message.Contains("字节缓冲区");
            }
            CaseAssertions.CheckTrue("raw→i32 受控拒绝（非字节缓冲区目标）", scalarCaught);

            // 负例：§19.2 集合资源（array<string>）load 受控拒绝——VM
            // LoadResource 同拒（"不支持的资源形态"），物化语义规范留白
            const string collectionBil =
                "BIL \"1.1\"\n" +
                "\n" +
                "Metadata {\n" +
                "    module = string \"rawcoll\"\n" +
                "}\n" +
                "\n" +
                "Resources {\n" +
                "    R_Names = array<string> { \"a\", \"b\" },\n" +
                "    R_Zero = i32 0\n" +
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
                "    .vars {\n" +
                "        .string d,\n" +
                "        .i32 r\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        load res(R_Names) $d\n" +
                "        load res(R_Zero) $r\n" +
                "        ret $r\n" +
                "    }\n" +
                "}\n";
            var collGate = BilGate.Accept(collectionBil, "rawcoll.bil");
            CaseAssertions.CheckTrue("集合资源 load 门禁放行", collGate.IsAccepted,
                string.Join("; ", collGate.Errors));
            var collCaught = false;
            try
            {
                var collContext = new MwContext(collGate.Module!);
                var collMir = MirBuilder.Build(collContext);
                using var llvmLease5660 = LlvmHost.Enter();
                using var collModule = ModuleBuilder.Build(collContext, collMir);
            }
            catch (MwNotSupportedException ex)
            {
                collCaught = ex.Message.Contains("无 load 物化语义");
            }
            CaseAssertions.CheckTrue("集合资源 load 受控拒绝（VM 同拒）", collCaught);

            // 负例：switch-table 资源不是可装载的值（§19.4 走 switch 指令
            // 专用通道），load 引用受控拒绝
            var tableBil = collectionBil
                .Replace("R_Names = array<string> { \"a\", \"b\" }",
                    "R_Switch = switch-table<.i32> { 1, 2, 3 }")
                .Replace("R_Names", "R_Switch")
                .Replace("rawcoll", "rawtable");
            var tableGate = BilGate.Accept(tableBil, "rawtable.bil");
            CaseAssertions.CheckTrue("switch-table load 门禁放行", tableGate.IsAccepted,
                string.Join("; ", tableGate.Errors));
            var tableCaught = false;
            try
            {
                var tableContext = new MwContext(tableGate.Module!);
                var tableMir = MirBuilder.Build(tableContext);
                using var llvmLease5683 = LlvmHost.Enter();
                using var tableModule = ModuleBuilder.Build(tableContext, tableMir);
            }
            catch (MwNotSupportedException ex)
            {
                tableCaught = ex.Message.Contains("无 load 物化语义");
            }
            CaseAssertions.CheckTrue("switch-table load 受控拒绝（专用通道）", tableCaught);
        }

        // ===== invoke.indirect（§15.3 callable 协议）=====

        private static void TestInvokeIndirect()
        {
            // MIR 直译：lambda 经变量调用 → MirInvokeIndirect 字段齐全
            var (_, lambdaTextModule, lambdaText) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var fn = func{(x: i32): i32 -> (x + 1)}\n" +
                "    return fn(41)\n" +
                "}\n");
            lambdaText = BilWriter.Write(lambdaTextModule);
            var lambdaGate = BilGate.Accept(lambdaText, "ind.lambda.bil");
            CaseAssertions.CheckTrue("lambda 间接调用门禁放行", lambdaGate.IsAccepted,
                string.Join("; ", lambdaGate.Errors));
            var lambdaContext = new MwContext(lambdaGate.Module!);
            var lambdaMir = MirBuilder.Build(lambdaContext);
            var lambdaInst = lambdaMir.Functions
                .SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
                .OfType<MirInvokeIndirect>().FirstOrDefault();
            CaseAssertions.CheckTrue("MIR 含 MirInvokeIndirect", lambdaInst != null);
            CaseAssertions.CheckTrue("有结果槽", lambdaInst is { Result: not null });
            CaseAssertions.CheckTrue("实参不含 receiver", lambdaInst is { Args.Count: 1 });
            CaseAssertions.CheckTrue("静态类型已附带",
                lambdaInst != null && lambdaInst.CallTargetType.Canonical.Length > 0);

            // noret：Action 语句调用 Result=null
            var (_, actionTextModule, actionText) = BilTestHarness.EmitBilUnit(
                "pub func sink(v: i32) { }\n" +
                "pub func main(): i32 {\n" +
                "    var act = func{() -> { sink(1) }}\n" +
                "    act()\n" +
                "    return 0\n" +
                "}\n");
            actionText = BilWriter.Write(actionTextModule);
            var actionGate = BilGate.Accept(actionText, "ind.action.bil");
            CaseAssertions.CheckTrue("Action noret 门禁放行", actionGate.IsAccepted,
                string.Join("; ", actionGate.Errors));
            var actionMir = MirBuilder.Build(new MwContext(actionGate.Module!));
            var noret = actionMir.Functions
                .SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
                .OfType<MirInvokeIndirect>().FirstOrDefault();
            CaseAssertions.CheckTrue("noret Result=null", noret is { Result: null });

            // 绑定分流：用户类 operator call → IndirectCallBinding.CallOperator
            var (_, userTextModule, userText) = BilTestHarness.EmitBilUnit(
                "pub class Doubler {\n" +
                "    pub init() { }\n" +
                "    pub operator call(x: i32): i32 { return (x * 2) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var d = new Doubler()\n" +
                "    return d(21)\n" +
                "}\n");
            userText = BilWriter.Write(userTextModule);
            var userGate = BilGate.Accept(userText, "ind.user.bil");
            CaseAssertions.CheckTrue("用户 operator call 门禁放行", userGate.IsAccepted,
                string.Join("; ", userGate.Errors));
            var userContext = new MwContext(userGate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(userContext);
            var userMir = userContext.Mir!;
            var userInst = userMir.Functions
                .SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
                .OfType<MirInvokeIndirect>().First();
            var binding = ImplBinder.BindIndirectCall(userContext.Symbols,
                userInst.CallTargetType.Canonical, new[] { "core::i32" }, "core::i32",
                userContext.Module.Functions);
            CaseAssertions.CheckTrue("绑定为 IndirectCallBinding", binding is IndirectCallBinding);
            CaseAssertions.CheckTrue("CallOperator 是 Doubler$$call",
                binding.CallOperator.Canonical.Contains("Doubler$$call")
                && binding.CallOperator.Canonical.Contains(".i32"));

            using var llvmLease5763 = LlvmHost.Enter();
            using var userModule = ModuleBuilder.Build(userContext, userMir);
            var ll = userModule.PrintToString();
            CaseAssertions.CheckTrue("间接调用经 rigi_vtable_entry",
                ll.Contains("call ptr @rigi_vtable_entry(ptr"), ll);
            CaseAssertions.CheckTrue("虚槽常量为 i32 1（槽 0 分发器）",
                ll.Contains("i32 1") && ll.Contains("rigi_vtable_entry"), ll);

            // 泛型 $$call：typeid 前缀平铺在实参前部（VM FindCallTarget 尚未
            // 吃此前缀，E2E 对拍降级；Middleware 绑定/发射覆盖）
            var (_, genTextModule, genText) = BilTestHarness.EmitBilUnit(
                "pub class Mapper {\n" +
                "    pub init() { }\n" +
                "    pub operator call\\<T>(x: T): T { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = new Mapper()\n" +
                "    return f\\<i32>(42)\n" +
                "}\n");
            genText = BilWriter.Write(genTextModule);
            var genGate = BilGate.Accept(genText, "ind.generic.bil");
            CaseAssertions.CheckTrue("泛型 $$call 门禁放行", genGate.IsAccepted,
                string.Join("; ", genGate.Errors));
            var genContext = new MwContext(genGate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(genContext);
            var genInst = genContext.Mir!.Functions
                .SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
                .OfType<MirInvokeIndirect>().FirstOrDefault();
            CaseAssertions.CheckTrue("泛型 $$call MIR 含间接调用", genInst != null);
            CaseAssertions.CheckTrue("泛型 $$call 实参含 typeid 前缀",
                genInst is { Args.Count: 2 });
            var mainFn = genContext.Mir!.Functions.First(f => f.IsEntrypoint);
            var genArgTypes = new List<string>();
            foreach (var arg in genInst!.Args)
            {
                genArgTypes.Add(mainFn.FindLocal(((MirLocalOperand)arg).Name).Type.Canonical);
            }
            var genBind = ImplBinder.BindIndirectCall(genContext.Symbols,
                genInst.CallTargetType.Canonical, genArgTypes,
                mainFn.FindLocal(genInst.Result!).Type.Canonical,
                genContext.Module.Functions);
            CaseAssertions.CheckTrue("泛型 $$call 绑定命中 Mapper$$call",
                genBind.CallOperator.Canonical.Contains("Mapper$$call"));
            using var llvmLease5805 = LlvmHost.Enter();
            using var genModule = ModuleBuilder.Build(genContext, genContext.Mir!);
            CaseAssertions.CheckTrue("泛型 $$call 发射 vtable 入口",
                genModule.PrintToString().Contains("rigi_vtable_entry"));
        }

        // ===== native FFI ABI（MW5 切片 b：String/胖引用 out 首参、Any 拒绝、typeid）=====

        private static void TestNativeFfiAbi()
        {
            var stringLl = EmitLlFromSource(
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"i64_to_string\")\n" +
                "native func i64_to_string(value: i64): String\n" +
                "pub func main(): i32 {\n" +
                "    var s = i64_to_string(42L)\n" +
                "    return 0\n" +
                "}\n", "ffi.string.bil");
            CaseAssertions.CheckTrue("String 返回 fn 类型：void + rigi_string* 首参",
                stringLl.Contains("declare void @rigi_i64_to_string(ptr, i64)"), stringLl);
            CaseAssertions.CheckTrue("String 返回调用点 alloca rigi_string",
                stringLl.Contains("alloca { ptr, i64 }"), stringLl);

            var u64Ll = EmitLlFromSource(
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"u64_to_string\")\n" +
                "native func u64_to_string(value: u64): String\n" +
                "pub func main(): i32 {\n" +
                "    var s = u64_to_string(1UL)\n" +
                "    return 0\n" +
                "}\n", "ffi.u64.bil");
            CaseAssertions.CheckTrue("u64 面 fn 类型：void + rigi_string* 首参 + i64",
                u64Ll.Contains("declare void @rigi_u64_to_string(ptr, i64)"), u64Ll);

            var anyToStrLl = EmitLlFromSource(
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"any_to_string\")\n" +
                "native func any_to_string(value: Any): String\n" +
                "pub func main(): i32 {\n" +
                "    var s = any_to_string((42 as Any))\n" +
                "    return 0\n" +
                "}\n", "ffi.any_to_string.bil");
            CaseAssertions.CheckTrue("any_to_string 面：String out 首参 + Any 槽指针",
                anyToStrLl.Contains("declare void @rigi_any_to_string(ptr, ptr)"), anyToStrLl);
            CaseAssertions.CheckTrue("any_to_string 调用形状",
                anyToStrLl.Contains("call void @rigi_any_to_string(ptr"), anyToStrLl);

            var f32Ll = EmitLlFromSource(
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"f32_to_string\")\n" +
                "native func f32_to_string(value: float): String\n" +
                "pub func main(): i32 {\n" +
                "    var s = f32_to_string(1.0f)\n" +
                "    return 0\n" +
                "}\n", "ffi.f32.bil");
            CaseAssertions.CheckTrue("f32 面 fn 类型：void + rigi_string* 首参 + float",
                f32Ll.Contains("declare void @rigi_f32_to_string(ptr, float)"), f32Ll);

            var refLl = EmitLlFromSource(
                "pub class User {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"make_user\")\n" +
                "native func make_user(): User\n" +
                "pub func main(): i32 {\n" +
                "    var u = make_user()\n" +
                "    return 0\n" +
                "}\n", "ffi.ref.bil");
            CaseAssertions.CheckTrue("用户引用返回 fn 类型：void + 槽指针首参",
                refLl.Contains("declare void @rigi_make_user(ptr)"), refLl);
            CaseAssertions.CheckTrue("用户引用返回调用点 alloca 16B 对齐槽",
                refLl.Contains("alloca { i64, i64 }") && refLl.Contains("align 16"), refLl);

            var anyLl = EmitLlFromSource(
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"box_any\")\n" +
                "native func box_any(value: Any): String\n" +
                "pub func main(): i32 {\n" +
                "    var s = box_any((1 as Any))\n" +
                "    return 0\n" +
                "}\n", "ffi.any.bil");
            CaseAssertions.CheckTrue("Any 参数 fn 类型：String out 首参 + Any 槽指针",
                anyLl.Contains("declare void @rigi_box_any(ptr, ptr)"), anyLl);

            var anyRetLl = EmitLlFromSource(
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"make_any\")\n" +
                "native func make_any(): Any\n" +
                "pub func main(): i32 {\n" +
                "    var a = make_any()\n" +
                "    return 0\n" +
                "}\n", "ffi.anyret.bil");
            CaseAssertions.CheckTrue("Any 返回 fn 类型：void + 槽指针首参",
                anyRetLl.Contains("declare void @rigi_make_any(ptr)"), anyRetLl);
            CaseAssertions.CheckTrue("Any 返回调用点 alloca 16B 对齐槽",
                anyRetLl.Contains("alloca { i64, i64 }") && anyRetLl.Contains("align 16"),
                anyRetLl);

            // L6：非 rigi_rt 库放行——C 符号 = symbol 原文（无 rigi_ 前缀），
            // 参数/返回 ABI 与 rigi_rt 面同一套；链接输入归 native --link
            var libcLl = EmitLlFromSource(
                "@NativeLibrary(\"libc\")\n" +
                "@NativeSymbol(\"abs\")\n" +
                "native func abs(x: i32): i32\n" +
                "pub func main(): i32 {\n" +
                "    return abs(-1)\n" +
                "}\n", "ffi.userlib.bil");
            CaseAssertions.CheckTrue("非 rigi_rt 库 fn 声明：C 符号原文无前缀",
                libcLl.Contains("declare i32 @abs(i32)"), libcLl);
            CaseAssertions.CheckTrue("非 rigi_rt 库调用形状",
                libcLl.Contains("call i32 @abs(i32"), libcLl);

            var userStringLl = EmitLlFromSource(
                "@NativeLibrary(\"mylib\")\n" +
                "@NativeSymbol(\"my_echo\")\n" +
                "native func my_echo(text: String): String\n" +
                "pub func main(): i32 {\n" +
                "    var s = my_echo(\"x\")\n" +
                "    return 0\n" +
                "}\n", "ffi.userlib.string.bil");
            CaseAssertions.CheckTrue("非 rigi_rt 库 String 面：void + rigi_string* 出入参",
                userStringLl.Contains("declare void @my_echo(ptr, ptr)"), userStringLl);

            var typeIdLl = EmitLlFromSource(
                "pub class Holder {\n" +
                "    pub init() { }\n" +
                "    pub func tag\\<T>(n: i32): i32 { return n }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h = new Holder()\n" +
                "    return h.tag\\<i32>(1)\n" +
                "}\n", "ffi.typeid.bil");
            CaseAssertions.CheckTrue("typeid ABI：.this 胖引用 → typeid ptr → 普通 i32（§7.2）",
                typeIdLl.Contains(
                    "define internal i32 @\"Holder$tag(n:.i32)@.i32\"({ i64, i64 } %0, ptr %1, i32 %2)"),
                typeIdLl);
            CaseAssertions.CheckTrue("typeid LLVM 表示 = TypeSheet 指针（getid.type 物化）",
                typeIdLl.Contains("store ptr @\"typesheet.core::i32\""), typeIdLl);
        }

    }
}
