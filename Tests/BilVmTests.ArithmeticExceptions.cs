using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // ArithmeticExceptions 职责；与主文件共享同一类型、字段及生命周期。

        // 异常 getMessage 三场景：失败 cast 的 CastException 消息含类型信息；
        // wrapper 降级未路由的 NoSuchMethodException 消息含请求 symbol；
        // 用户子类 override getMessage 返回自定义串并多态打印。
        private static void TestExceptionGetMessage()
        {
            var cast = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var a: Animal = new Animal()\n" +
                "        var d = (a as Dog)\n" +
                "        return 0\n" +
                "    } catch (e: core.CastException) {\n" +
                "        core.io.Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n");
            CheckOk("CastException 被 catch", cast);
            CheckI32("CastException catch 返回 7", cast, 7);
            CaseAssertions.CheckTrue("getMessage 含源类型",
                cast.Stdout.Contains("Animal") == true, cast.Stdout);
            CaseAssertions.CheckTrue("getMessage 含目标类型",
                cast.Stdout.Contains("Dog") == true, cast.Stdout);

            const string service =
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@W\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "}\n";
            var downgrade = Run(service +
                "pub func main(): i32 {\n" +
                "    var service = new Service()\n" +
                "    try {\n" +
                "        service.fetchUserById(42)\n" +
                "        return 0\n" +
                "    } catch (e: core.NoSuchMethodException) {\n" +
                "        core.io.Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n");
            CheckOk("NoSuchMethodException 被 catch", downgrade);
            CheckI32("NoSuchMethodException catch 返回 7", downgrade, 7);
            CaseAssertions.CheckTrue("getMessage 含请求 symbol",
                downgrade.Stdout.Contains("Service$fetchUserById") == true,
                downgrade.Stdout);

            var custom = Run(
                "pub open class MyException : core.Exception {\n" +
                "    pub init()\n" +
                "    pub override func getMessage(): String { return \"custom-message\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new MyException()\n" +
                "        return 0\n" +
                "    } catch (e: core.Exception) {\n" +
                "        core.io.Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n");
            CheckOk("用户异常 override getMessage 被 catch", custom);
            CaseAssertions.Check("多态 getMessage stdout", custom.Stdout, "custom-message\n");
            CheckI32("用户异常 catch 返回 7", custom, 7);
        }

        // 整数除零（SYNTAX §8.1 / BIL §11.2）：抛语言级
        // core.DividedByZeroException——try/catch 可捕获并读 getMessage；
        // 未捕获则异常对象沿帧链传播；u64 同例；float/double 除零保持
        // IEEE 754（inf/NaN），不抛。
        private static void TestIntegerDivisionByZero()
        {
            var caught = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var a: i32 = 10\n" +
                "        var b: i32 = 0\n" +
                "        var c = (a / b)\n" +
                "        return 0\n" +
                "    } catch (e: core.DividedByZeroException) {\n" +
                "        core.io.Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n");
            CheckOk("i32 除零被 catch", caught);
            CheckI32("除零 catch 返回 7", caught, 7);
            CaseAssertions.Check("除零 getMessage stdout", caught.Stdout, "整数除以零\n");

            var uncaught = Run(
                "pub func main(): i32 {\n" +
                "    var a: i32 = 10\n" +
                "    var b: i32 = 0\n" +
                "    return (a / b)\n" +
                "}\n");
            CaseAssertions.CheckTrue("未捕获除零抛 DividedByZeroException",
                uncaught.Exception?.ExceptionObject is VmObject divObj
                && divObj.TypeRef.Contains("DividedByZeroException")
                && uncaught.Exception.Message.Contains("整数除以零"),
                uncaught.Exception?.ToString() ?? "<null>");
            // MW9b：顶层未捕获格式对齐 native reporter「{类型全名}: {message}」
            CaseAssertions.Check("未捕获除零顶层格式",
                uncaught.Exception?.Message ?? "",
                "core::DividedByZeroException: 整数除以零");

            var u64 = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var a: u64 = 10UL\n" +
                "        var b: u64 = 0UL\n" +
                "        var c = (a / b)\n" +
                "        return 0\n" +
                "    } catch (e: core.DividedByZeroException) {\n" +
                "        return 9\n" +
                "    }\n" +
                "}\n");
            CheckOk("u64 除零被 catch", u64);
            CheckI32("u64 除零 catch 返回 9", u64, 9);

            var f64 = Run(
                "pub func main(): double {\n" +
                "    var a: double = 1.0\n" +
                "    var b: double = 0.0\n" +
                "    return (a / b)\n" +
                "}\n");
            CheckOk("double 除零不抛", f64);
            CaseAssertions.CheckTrue("double 除零得 +Inf",
                f64.ReturnValue is VmF64 inf && double.IsPositiveInfinity(inf.Value),
                f64.ReturnValue?.ToStandardText() ?? "<null>");
            var nan = Run(
                "pub func main(): double {\n" +
                "    var a: double = 0.0\n" +
                "    var b: double = 0.0\n" +
                "    return (a / b)\n" +
                "}\n");
            CheckOk("double 零除零不抛", nan);
            CaseAssertions.CheckTrue("double 零除零得 NaN",
                nan.ReturnValue is VmF64 n && double.IsNaN(n.Value),
                nan.ReturnValue?.ToStandardText() ?? "<null>");
        }

        // 整数/浮点取模（§11.2 mod）：①-⑧ 按本文件头「frontend 不可达
        // 形态用直接构造的 BilModule」惯例驱动 VM（保留作 VM 层直测）；
        // ⑨-⑪ frontend % 运算符已通，走源码全管线端到端。覆盖：
        // ① i32 模零被 try/catch 捕获（core::DividedByZeroException，与
        // 除零同异常同消息）；② 未捕获传播的异常对象与顶层格式；③ u64
        // 正常取模；④ i64 负数操作数（结果符号随被除数）；⑤ i64 MIN % -1
        // = 0；⑥ f64 模零得 NaN 不抛；⑦ f64 正常取模；⑧ mod 拼写经
        // BilWriter/BilReader 往返可回读执行；⑨ 源码 a % b（i32）；
        // ⑩ 源码 a %= b 复合赋值；⑪ 源码 try/catch 捕获模零。
        private static void TestIntegerModulo()
        {
            // ① i32 模零：try/catch 捕获（catch-table 唯一条目即
            // core::DividedByZeroException，命中 handler 即证明类型匹配）
            var caughtModule = ModModuleSkeleton(".i32", out var caughtMain, out var caughtEntry);
            caughtModule.Resources.Add(new BilScalarResource("R_A", BilScalarType.I32, "10"));
            caughtModule.Resources.Add(new BilScalarResource("R_Z", BilScalarType.I32, "0"));
            caughtModule.Resources.Add(new BilScalarResource("R_7", BilScalarType.I32, "7"));
            var caughtBody = new BilBlock("try0-body");
            var caughtHandler = new BilBlock("try0-catch0");
            caughtMain.Blocks.Add(caughtBody);
            caughtMain.Blocks.Add(caughtHandler);
            var caughtTable = new BilCatchTableResource("R_CT",
                new[] { new BilCatchEntry(
                    new BilTypeOperand("core::DividedByZeroException"), caughtHandler) });
            caughtModule.Resources.Add(caughtTable);
            caughtEntry.Instructions.Add(new LoadInstruction(
                caughtModule.Resources[0], BilOp.Var("a")));
            caughtEntry.Instructions.Add(new LoadInstruction(
                caughtModule.Resources[1], BilOp.Var("b")));
            caughtEntry.Instructions.Add(new TryInstruction(caughtBody, BilOp.Var("slot"),
                caughtTable, null, BilOp.Var("b0")));
            caughtEntry.Instructions.Add(new RetInstruction(BilOp.Var("a")));
            caughtBody.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Mod,
                BilOp.Var("a"), BilOp.Var("b"), BilOp.Var("r")));
            caughtHandler.Instructions.Add(new LoadInstruction(
                caughtModule.Resources[2], BilOp.Var("r")));
            caughtHandler.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            var caught = BilVm.Run(caughtModule);
            CheckOk("i32 模零被 try/catch 捕获", caught);
            CheckI32("DividedByZeroException catch 返回 7", caught, 7);

            // ② i32 模零未捕获：异常对象沿帧链传播（与除零同型同消息）
            var uncaughtModule = ModModuleSkeleton(".i32", out var uncaughtMain, out var uncaughtEntry);
            uncaughtModule.Resources.Add(new BilScalarResource("R_A", BilScalarType.I32, "10"));
            uncaughtModule.Resources.Add(new BilScalarResource("R_Z", BilScalarType.I32, "0"));
            uncaughtEntry.Instructions.Add(new LoadInstruction(
                uncaughtModule.Resources[0], BilOp.Var("a")));
            uncaughtEntry.Instructions.Add(new LoadInstruction(
                uncaughtModule.Resources[1], BilOp.Var("b")));
            uncaughtEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Mod,
                BilOp.Var("a"), BilOp.Var("b"), BilOp.Var("r")));
            uncaughtEntry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            var uncaught = BilVm.Run(uncaughtModule);
            CaseAssertions.CheckTrue("未捕获模零抛 DividedByZeroException",
                uncaught.Exception?.ExceptionObject is VmObject divObj
                && divObj.TypeRef.Contains("DividedByZeroException")
                && uncaught.Exception.Message.Contains("整数除以零"),
                uncaught.Exception?.ToString() ?? "<null>");
            CaseAssertions.Check("未捕获模零顶层格式",
                uncaught.Exception?.Message ?? "",
                "core::DividedByZeroException: 整数除以零");

            // ③ u64 正常取模：10 % 3 = 1
            var u64Module = ModModuleSkeleton(".u64", out var u64Main, out var u64Entry);
            u64Module.Resources.Add(new BilScalarResource("R_UA", BilScalarType.U64, "10"));
            u64Module.Resources.Add(new BilScalarResource("R_UB", BilScalarType.U64, "3"));
            u64Entry.Instructions.Add(new LoadInstruction(
                u64Module.Resources[0], BilOp.Var("a")));
            u64Entry.Instructions.Add(new LoadInstruction(
                u64Module.Resources[1], BilOp.Var("b")));
            u64Entry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Mod,
                BilOp.Var("a"), BilOp.Var("b"), BilOp.Var("r")));
            u64Entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            var u64 = BilVm.Run(u64Module);
            CheckOk("u64 取模无异常", u64);
            CaseAssertions.CheckTrue("u64 10 % 3 = 1",
                u64.ReturnValue is VmU64 u && u.Value == 1UL,
                u64.ReturnValue?.ToStandardText() ?? "<null>");

            // ④ i64 负数操作数：截断取余符号随被除数（-7 % 3 = -1、
            // 7 % -3 = 1，同 C# %）
            CheckOk("i64 -7 % 3 无异常", RunI64Modulo("-7", "3", out var negLeft));
            CaseAssertions.CheckTrue("i64 -7 % 3 = -1（符号随被除数）",
                negLeft is VmI64 negL && negL.Value == -1,
                negLeft?.ToStandardText() ?? "<null>");
            CheckOk("i64 7 % -3 无异常", RunI64Modulo("7", "-3", out var negRight));
            CaseAssertions.CheckTrue("i64 7 % -3 = 1（符号随被除数）",
                negRight is VmI64 negR && negR.Value == 1,
                negRight?.ToStandardText() ?? "<null>");

            // ⑤ i64 MIN % -1 = 0（C# long % 天然得 0，无回绕陷阱）
            CheckOk("i64 MIN % -1 无异常", RunI64Modulo("-9223372036854775808", "-1", out var minMod));
            CaseAssertions.CheckTrue("i64 MIN % -1 = 0",
                minMod is VmI64 zero && zero.Value == 0,
                minMod?.ToStandardText() ?? "<null>");

            // ⑥ f64 模零：IEEE 754 截断余数得 NaN，不抛
            var nanModule = ModModuleSkeleton(".f64", out var nanMain, out var nanEntry);
            nanModule.Resources.Add(new BilScalarResource("R_FA", BilScalarType.F64, "7.5"));
            nanModule.Resources.Add(new BilScalarResource("R_FZ", BilScalarType.F64, "0.0"));
            nanEntry.Instructions.Add(new LoadInstruction(
                nanModule.Resources[0], BilOp.Var("a")));
            nanEntry.Instructions.Add(new LoadInstruction(
                nanModule.Resources[1], BilOp.Var("b")));
            nanEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Mod,
                BilOp.Var("a"), BilOp.Var("b"), BilOp.Var("r")));
            nanEntry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            var f64Nan = BilVm.Run(nanModule);
            CheckOk("f64 模零不抛", f64Nan);
            CaseAssertions.CheckTrue("f64 模零得 NaN",
                f64Nan.ReturnValue is VmF64 nanValue && double.IsNaN(nanValue.Value),
                f64Nan.ReturnValue?.ToStandardText() ?? "<null>");

            // ⑦ f64 正常取模：7.5 % 2.0 = 1.5
            var f64Module = ModModuleSkeleton(".f64", out var f64Main, out var f64Entry);
            f64Module.Resources.Add(new BilScalarResource("R_FA", BilScalarType.F64, "7.5"));
            f64Module.Resources.Add(new BilScalarResource("R_FB", BilScalarType.F64, "2.0"));
            f64Entry.Instructions.Add(new LoadInstruction(
                f64Module.Resources[0], BilOp.Var("a")));
            f64Entry.Instructions.Add(new LoadInstruction(
                f64Module.Resources[1], BilOp.Var("b")));
            f64Entry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Mod,
                BilOp.Var("a"), BilOp.Var("b"), BilOp.Var("r")));
            f64Entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            var f64 = BilVm.Run(f64Module);
            CheckOk("f64 取模无异常", f64);
            CaseAssertions.CheckTrue("f64 7.5 % 2.0 = 1.5",
                f64.ReturnValue is VmF64 remainder && remainder.Value == 1.5,
                f64.ReturnValue?.ToStandardText() ?? "<null>");

            // ⑧ mod 拼写往返：BilWriter 文本经 BilReader 回读后 VM 同值
            var u64Written = BilWriter.Write(u64Module);
            var reparsed = BilReader.Read(u64Written);
            CaseAssertions.CheckTrue("mod 指令出现在 BIL 文本",
                u64Written.Contains("mod "), u64Written);
            var roundtrip = BilVm.Run(reparsed);
            CheckOk("mod 模块回读 VM 运行", roundtrip);
            CaseAssertions.CheckTrue("回读后 u64 10 % 3 = 1",
                roundtrip.ReturnValue is VmU64 rtValue && rtValue.Value == 1UL,
                roundtrip.ReturnValue?.ToStandardText() ?? "<null>");

            // ⑨ 源码级端到端：a % b（i32 正常值）经 Parser → Binder →
            // Lowering → BIL mod 全管线
            var srcMod = Run(
                "pub func main(): i32 {\n" +
                "    var a: i32 = 10\n" +
                "    var b: i32 = 3\n" +
                "    return (a % b)\n" +
                "}\n");
            CheckOk("源码 i32 取模", srcMod);
            CheckI32("源码 i32 10 % 3 = 1", srcMod, 1);

            // ⑩ 源码级 %= 复合赋值（脱糖 a = a % b）
            var srcCompound = Run(
                "pub func main(): i32 {\n" +
                "    var a: i32 = 10\n" +
                "    var b: i32 = 3\n" +
                "    a %= b\n" +
                "    return a\n" +
                "}\n");
            CheckOk("源码 %= 复合赋值", srcCompound);
            CheckI32("源码 %= 后 a = 1", srcCompound, 1);

            // ⑪ 源码级模零捕获：与除零同异常同消息，try/catch 可捕获
            var srcZero = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var a: i32 = 10\n" +
                "        var b: i32 = 0\n" +
                "        var c = (a % b)\n" +
                "        return 0\n" +
                "    } catch (e: core.DividedByZeroException) {\n" +
                "        core.io.Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n");
            CheckOk("源码 i32 模零被 catch", srcZero);
            CheckI32("源码模零 catch 返回 7", srcZero, 7);
            CaseAssertions.Check("源码模零 getMessage stdout", srcZero.Stdout, "整数除以零\n");
        }

        // mod i64 用例辅助：left % right 的独立手工模块，返回值经 result
        // 传出（VmValue 可空——异常时为 null）
        private static BilVmResult RunI64Modulo(string left, string right, out VmValue? result)
        {
            var module = ModModuleSkeleton(".i64", out var main, out var entry);
            module.Resources.Add(new BilScalarResource("R_LA", BilScalarType.I64, left));
            module.Resources.Add(new BilScalarResource("R_LB", BilScalarType.I64, right));
            entry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("a")));
            entry.Instructions.Add(new LoadInstruction(module.Resources[1], BilOp.Var("b")));
            entry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Mod,
                BilOp.Var("a"), BilOp.Var("b"), BilOp.Var("r")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            var run = BilVm.Run(module);
            result = run.ReturnValue;
            return run;
        }

        // mod 用例手工模块骨架：main()@.t（entrypoint）+ 变量 a/b/r（类型
        // t，.return 同型）+ slot(.any)/b0(.breakid)（try 用例的异常槽与
        // break capability）；资源由各用例自行追加
        private static BilModule ModModuleSkeleton(string typeRef,
            out BilFunction main, out BilBlock entry)
        {
            var module = new BilModule();
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@" + typeRef,
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));
            main = new BilFunction("$main()@" + typeRef);
            main.Args.Add(new BilArgDeclaration(".return", typeRef));
            main.Vars.Add(new BilVarDeclaration(typeRef, "a"));
            main.Vars.Add(new BilVarDeclaration(typeRef, "b"));
            main.Vars.Add(new BilVarDeclaration(typeRef, "r"));
            main.Vars.Add(new BilVarDeclaration(".any", "slot"));
            main.Vars.Add(new BilVarDeclaration(".breakid", "b0"));
            entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

    }
}
