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
        // IndirectCalls 职责；与主文件共享同一类型、字段及生命周期。

        // ===== L1：8 条「VM 支持但前端不发射」指令的 BIL 级对拍 =====

        // cast.indirect / cast.safe.indirect（§12.1/§12.2 动态形态）：
        // 上转/下转命中（rigi_try_cast 视图改写），safe 不命中产 null
        //（type.is 观测为 false）；全程与 VM 同 BIL 对拍
        private static void RunIndirectCastCase()
        {
            RunBilCase("cast.indirect 族（BIL 级）", BilWriter.Write(IndirectCastModule(false)));
        }

        // cast.indirect 不命中：VM CastFailed 与 native EmitCastThrow
        // 同型（core::CastException），退出码 1、stderr 关键字对齐
        private static void RunIndirectCastFailCase()
        {
            RunBilFailCase("cast.indirect 不命中抛 CastException（BIL 级）",
                BilWriter.Write(IndirectCastModule(true)), "无法将", "CastException");
        }

        // Animal/Dog（extends）模块：源码骨架（携 stdlib，异常构造/
        // 顶层 reporter 可达）+ $main 入口块清空后直织间接 cast 序列
        //（前端不发射的形态）。failMode = 以 Animal 实例对 .typeid<Dog>
        // 做强制 cast.indirect（不命中路径）
        // G4 落空负例模块（BIL 级）：合法骨架（Addable + add<T> 携
        // stdlib，异常类型/init 可达）+ 手写 Plain（无 operator）——
        // main 改写为 add<Plain>，运行期候选链全落空
        private static string BuildGenericOpMissBil()
        {
            var module = EmitNativeSource(
                "pub interface Addable {\n" +
                "    operator plus(another: Addable): Addable\n" +
                "}\n" +
                "func add\\<T extends Addable>(a: T, b: T): Addable {\n" +
                "    return a + b\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return 0\n" +
                "}\n");
            var plain = new BilTypeDeclaration("Plain", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            plain.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Plain$init()@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            module.LocalSymbols.Add(plain);
            var plainInit = new BilFunction("Plain$init()@.void");
            plainInit.Args.Add(new BilArgDeclaration(".return", ".void"));
            plainInit.Args.Add(new BilArgDeclaration(".this", "Plain"));
            var plainInitBody = new BilBlock("entry", BilBlockModifier.Entrypoint);
            plainInitBody.Instructions.Add(new RetInstruction());
            plainInit.Blocks.Add(plainInitBody);
            module.Functions.Add(plainInit);

            module.Resources.Add(new BilScalarResource("R_Z", BilScalarType.I32, "0"));
            var main = module.Functions.Single(f => f.Symbol == "$main()@.i32");
            var entry = main.Blocks.Single(b => b.Modifiers.Contains(BilBlockModifier.Entrypoint));
            entry.Instructions.Clear();
            main.Vars.Add(new BilVarDeclaration(".typeid", "tid"));
            main.Vars.Add(new BilVarDeclaration("Plain", "p1"));
            main.Vars.Add(new BilVarDeclaration("Plain", "p2"));
            main.Vars.Add(new BilVarDeclaration("Addable", "rr"));
            main.Vars.Add(new BilVarDeclaration(".i32", "rz"));
            entry.Instructions.Add(new GetIdTypeInstruction(BilOp.Type("Plain"),
                BilOp.Var("tid")));
            entry.Instructions.Add(new NewInstruction(BilOp.Type("Plain"), BilOp.Var("p1"),
                Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new NewInstruction(BilOp.Type("Plain"), BilOp.Var("p2"),
                Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new InvokeInstruction(
                BilOp.Fn("$add(a:.generic<$.generic.T>,b:.generic<$.generic.T>)@Addable"),
                BilOp.Var("rr"),
                new[]
                {
                    BilOp.Var("tid"), BilOp.Var("p1"), BilOp.Var("p2"),
                }));
            entry.Instructions.Add(new LoadInstruction(module.Resources[^1],
                BilOp.Var("rz")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("rz")));
            return BilWriter.Write(module);
        }

        private static BilModule IndirectCastModule(bool failMode)
        {
            var module = EmitNativeSource(
                "pub open class Animal {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub class Dog : Animal {\n" +
                "    pub var n: i32\n" +
                "    pub init() { n = 7 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return 0\n" +
                "}\n");
            module.Resources.Add(new BilScalarResource("R_C99", BilScalarType.I32, "99"));
            var main = module.Functions.Single(f => f.Symbol == "$main()@.i32");
            var entry = main.Blocks.Single(b => b.Modifiers.Contains(BilBlockModifier.Entrypoint));
            entry.Instructions.Clear();
            main.Vars.Add(new BilVarDeclaration("Dog", "d"));
            main.Vars.Add(new BilVarDeclaration("Animal", "a"));
            main.Vars.Add(new BilVarDeclaration("Animal", "a2"));
            main.Vars.Add(new BilVarDeclaration("Dog", "back"));
            main.Vars.Add(new BilVarDeclaration(".typeid<Animal>", "ta"));
            main.Vars.Add(new BilVarDeclaration(".typeid<Dog>", "td"));
            main.Vars.Add(new BilVarDeclaration(".nullable<Dog>", "miss"));
            main.Vars.Add(new BilVarDeclaration(".bool", "flag"));
            main.Vars.Add(new BilVarDeclaration(".i32", "r"));
            main.Vars.Add(new BilVarDeclaration(".breakid", "bk"));
            entry.Instructions.Add(new NewInstruction(BilOp.Type("Dog"), BilOp.Var("d"),
                Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new NewInstruction(BilOp.Type("Animal"), BilOp.Var("a2"),
                Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new GetIdTypeInstruction(BilOp.Type("Animal"),
                BilOp.Var("ta")));
            // Dog → Animal 上转（运行期 typeid 命中）
            entry.Instructions.Add(new CastIndirectInstruction(BilOp.Var("d"), BilOp.Var("a"),
                BilOp.Var("ta"), isSafe: false));
            entry.Instructions.Add(new GetIdTypeInstruction(BilOp.Type("Dog"),
                BilOp.Var("td")));
            if (failMode)
            {
                // Animal 实例 → Dog 强制转换：运行期不命中
                entry.Instructions.Add(new LoadInstruction(module.Resources[^1],
                    BilOp.Var("r")));
                entry.Instructions.Add(new CastIndirectInstruction(BilOp.Var("a2"),
                    BilOp.Var("back"), BilOp.Var("td"), isSafe: false));
                entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            }
            else
            {
                // Animal（实为 Dog）→ Dog 下转命中，读回字段验证身份
                entry.Instructions.Add(new CastIndirectInstruction(BilOp.Var("a"),
                    BilOp.Var("back"), BilOp.Var("td"), isSafe: false));
                entry.Instructions.Add(new GetFieldInstruction(BilOp.Var("back"),
                    BilOp.Var("r"), BilOp.Field("Dog#n@.i32")));
                // safe 不命中：Animal 非 Dog → null；type.is 观测 false
                entry.Instructions.Add(new CastIndirectInstruction(BilOp.Var("a2"),
                    BilOp.Var("miss"), BilOp.Var("td"), isSafe: true));
                entry.Instructions.Add(new DirectTypeCheckInstruction(BilTypeCheckKind.Is,
                    BilOp.Var("miss"), BilOp.Type("Dog"), BilOp.Var("flag")));
                var thenBlock = new BilBlock("cast-then");
                thenBlock.Instructions.Add(new LoadInstruction(module.Resources[^1],
                    BilOp.Var("r")));
                thenBlock.Instructions.Add(new RetInstruction(BilOp.Var("r")));
                main.Blocks.Add(thenBlock);
                entry.Instructions.Add(new IfInstruction(BilOp.Var("flag"), thenBlock,
                    null, BilOp.Var("bk")));
                entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            }
            return module;
        }

        // get.wrapper.indirect（§12.4 动态形态，前端无整体取值路径）：
        // Host wrapped(Wrap) + 有参 ..init.wrapper 安装后，经
        // getid.type 的 typeid 间接取 wrapper 值拷贝，读字段验证
        //（BilVmTests.WrapperHostModule 同构手工模块）
        private static void RunGetWrapperIndirectCase()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_L", BilScalarType.I32, "5"));
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
                "$main()@.i32",
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

            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "lv"));
            main.Vars.Add(new BilVarDeclaration("Host", "h"));
            main.Vars.Add(new BilVarDeclaration(".typeid<Wrap>", "wid"));
            main.Vars.Add(new BilVarDeclaration("Wrap", "w"));
            main.Vars.Add(new BilVarDeclaration(".i32", "r"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("lv")));
            entry.Instructions.Add(new NewWrappedInstruction(BilOp.Type("Host"),
                BilOp.Var("h"), new[] { BilOp.Var("lv") },
                Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new GetIdTypeInstruction(BilOp.Type("Wrap"),
                BilOp.Var("wid")));
            entry.Instructions.Add(new GetWrapperIndirectInstruction(BilOp.Var("h"),
                BilOp.Var("wid"), BilOp.Var("w")));
            entry.Instructions.Add(new GetFieldInstruction(BilOp.Var("w"), BilOp.Var("r"),
                BilOp.Field("Wrap#level@.i32")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            RunBilCase("get.wrapper.indirect（BIL 级）", BilWriter.Write(module));
        }

    }
}
