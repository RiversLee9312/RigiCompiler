using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // ResourceLifetime 职责；与主文件共享同一类型、字段及生命周期。

        private static void TestUsingDisposeOrder()
        {
            var result = Run(
                "class Tracer implements core.IDisposable {\n" +
                "    pub var name: String\n" +
                "    pub init(_ -> name)\n" +
                "    pub override func dispose() {\n" +
                "        core.io.Console.println(name)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    seq using(const a = new Tracer(\"a\"))\n" +
                "    using(const b = new Tracer(\"b\")) {\n" +
                "        core.io.Console.println(\"body\")\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("using 清理", result);
            CaseAssertions.Check("using 逆序 dispose", result.Stdout, "body\nb\na\n");
            CheckI32("using 返回", result, 0);
        }

        private static void TestLoopEnumeratorDirectModule()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_0", BilScalarType.I32, "0"));
            module.Resources.Add(new BilScalarResource("R_3", BilScalarType.I32, "3"));
            module.Resources.Add(new BilScalarResource("R_1", BilScalarType.I32, "1"));
            module.Resources.Add(new BilScalarResource("R_10", BilScalarType.I32, "10"));
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));
            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "i"));
            main.Vars.Add(new BilVarDeclaration(".i32", "sum"));
            main.Vars.Add(new BilVarDeclaration(".i32", "one"));
            main.Vars.Add(new BilVarDeclaration(".i32", "ten"));
            main.Vars.Add(new BilVarDeclaration(".i32", "limit"));
            main.Vars.Add(new BilVarDeclaration(".bool", "c"));
            main.Vars.Add(new BilVarDeclaration(".breakid", "b"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            var body = new BilBlock("loop0-body");
            var enumBlock = new BilBlock("loop0-enum");
            var judge = new BilBlock("loop0-judge");
            entry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("i")));
            entry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("sum")));
            entry.Instructions.Add(new LoadInstruction(module.Resources[2], BilOp.Var("one")));
            entry.Instructions.Add(new LoadInstruction(module.Resources[3], BilOp.Var("ten")));
            entry.Instructions.Add(new LoadInstruction(module.Resources[1], BilOp.Var("limit")));
            entry.Instructions.Add(new LoopInstruction(BilOp.Var("c"), body, enumBlock, judge,
                BilOp.Var("b"), isRev: false));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("sum")));
            judge.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.CmpLt,
                BilOp.Var("i"), BilOp.Var("limit"), BilOp.Var("c")));
            body.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("sum"), BilOp.Var("ten"), BilOp.Var("sum")));
            enumBlock.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("i"), BilOp.Var("one"), BilOp.Var("i")));
            main.Blocks.Add(entry);
            main.Blocks.Add(body);
            main.Blocks.Add(enumBlock);
            main.Blocks.Add(judge);
            module.Functions.Add(main);
            var result = BilVm.Run(module);
            CheckOk("loop 三 block", result);
            CheckI32("judge/body/enum 协议", result, 30);
        }

    }
}
