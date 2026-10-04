using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // InstructionFixtures 职责；与主文件共享同一类型、字段及生命周期。

        private static BilModule IndirectBoxModule()
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
            box.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method, "Box$init()@.void",
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
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("v")));
            entry.Instructions.Add(new GetIdTypeInstruction(BilOp.Type("Box"), BilOp.Var("tid")));
            entry.Instructions.Add(new GetIdFieldInstruction(BilOp.Field("Box#n@.i32"),
                BilOp.Var("fid")));
            entry.Instructions.Add(new GetIdFieldInstruction(BilOp.Field("Box#.static.tag@.i32"),
                BilOp.Var("sfid")));
            entry.Instructions.Add(new NewIndirectInstruction(BilOp.Var("tid"), BilOp.Var("obj"),
                Array.Empty<BilVariableOperand>()));
            entry.Instructions.Add(new SetFieldIndirectInstruction(BilOp.Var("v"), BilOp.Var("obj"),
                BilOp.Var("fid")));
            entry.Instructions.Add(new SetFieldStaticIndirectInstruction(BilOp.Var("v"),
                BilOp.Var("tid"), BilOp.Var("sfid")));
            entry.Instructions.Add(new GetFieldIndirectInstruction(BilOp.Var("obj"), BilOp.Var("r"),
                BilOp.Var("fid")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        private static BilModule VectorPlusModule()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_1", BilScalarType.I32, "1"));
            module.Resources.Add(new BilScalarResource("R_3", BilScalarType.I32, "3"));
            var vec = new BilTypeDeclaration("Vec", BilTypeKind.Struct,
                new BilAccessibilityModifier(BilAccessibility.Public));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field, "Vec#x@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field, "Vec#y@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Vec$init(x:.i32,y:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Vec$$plus(other:Vec)@Vec",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilOperatorModifier("plus"),
                }));
            module.LocalSymbols.Add(vec);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));
            var init = new BilFunction("Vec$init(x:.i32,y:.i32)@.void");
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", "Vec"));
            init.Args.Add(new BilArgDeclaration("x", ".i32"));
            init.Args.Add(new BilArgDeclaration("y", ".i32"));
            var initEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            initEntry.Instructions.Add(new SetFieldInstruction(BilOp.Var("x"),
                BilOp.Var(".this"), BilOp.Field("Vec#x@.i32")));
            initEntry.Instructions.Add(new SetFieldInstruction(BilOp.Var("y"),
                BilOp.Var(".this"), BilOp.Field("Vec#y@.i32")));
            initEntry.Instructions.Add(new RetInstruction());
            init.Blocks.Add(initEntry);
            module.Functions.Add(init);
            var plus = new BilFunction("Vec$$plus(other:Vec)@Vec");
            plus.Args.Add(new BilArgDeclaration(".return", "Vec"));
            plus.Args.Add(new BilArgDeclaration(".this", "Vec"));
            plus.Args.Add(new BilArgDeclaration("other", "Vec"));
            plus.Vars.Add(new BilVarDeclaration(".i32", "ax"));
            plus.Vars.Add(new BilVarDeclaration(".i32", "bx"));
            plus.Vars.Add(new BilVarDeclaration(".i32", "sx"));
            plus.Vars.Add(new BilVarDeclaration(".i32", "ay"));
            plus.Vars.Add(new BilVarDeclaration(".i32", "by"));
            plus.Vars.Add(new BilVarDeclaration(".i32", "sy"));
            plus.Vars.Add(new BilVarDeclaration("Vec", "r"));
            var plusEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            plusEntry.Instructions.Add(new GetFieldInstruction(BilOp.Var(".this"),
                BilOp.Var("ax"), BilOp.Field("Vec#x@.i32")));
            plusEntry.Instructions.Add(new GetFieldInstruction(BilOp.Var("other"),
                BilOp.Var("bx"), BilOp.Field("Vec#x@.i32")));
            plusEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("ax"), BilOp.Var("bx"), BilOp.Var("sx")));
            plusEntry.Instructions.Add(new GetFieldInstruction(BilOp.Var(".this"),
                BilOp.Var("ay"), BilOp.Field("Vec#y@.i32")));
            plusEntry.Instructions.Add(new GetFieldInstruction(BilOp.Var("other"),
                BilOp.Var("by"), BilOp.Field("Vec#y@.i32")));
            plusEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("ay"), BilOp.Var("by"), BilOp.Var("sy")));
            plusEntry.Instructions.Add(new NewInstruction(BilOp.Type("Vec"), BilOp.Var("r"),
                new[] { BilOp.Var("sx"), BilOp.Var("sy") }));
            plusEntry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            plus.Blocks.Add(plusEntry);
            module.Functions.Add(plus);
            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "one"));
            main.Vars.Add(new BilVarDeclaration(".i32", "three"));
            main.Vars.Add(new BilVarDeclaration("Vec", "a"));
            main.Vars.Add(new BilVarDeclaration("Vec", "b"));
            main.Vars.Add(new BilVarDeclaration("Vec", "c"));
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(module.Resources[0], BilOp.Var("one")));
            entry.Instructions.Add(new LoadInstruction(module.Resources[1], BilOp.Var("three")));
            entry.Instructions.Add(new NewInstruction(BilOp.Type("Vec"), BilOp.Var("a"),
                new[] { BilOp.Var("one"), BilOp.Var("one") }));
            entry.Instructions.Add(new NewInstruction(BilOp.Type("Vec"), BilOp.Var("b"),
                new[] { BilOp.Var("three"), BilOp.Var("one") }));
            entry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("b"), BilOp.Var("c")));
            entry.Instructions.Add(new GetFieldInstruction(BilOp.Var("c"), BilOp.Var("x"),
                BilOp.Field("Vec#x@.i32")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        // Entity operator 派发直构：Vec 带 wrapped(W)，W 只有 wildcard
        // .proxy.opr.*。frontend 的 `+` 不查用户 operator（TestUserOperatorAdd
        // 同因），此处直接发 add 指令验证 `+` 命中 .proxy.opr.*——proxy 直接
        // 返回 (99,0) 的 Vec，链末原始 plus 不会被走到。
        private static BilModule WrappedVecOperatorProxyModule()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_99", BilScalarType.I32, "99"));
            module.Resources.Add(new BilScalarResource("R_0", BilScalarType.I32, "0"));

            var w = new BilTypeDeclaration("W", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            w.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "W$$.proxy.opr.*(symbol:.string)@Vec",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilOperatorModifier(".proxy.opr.*"),
                    new BilWrapperProxyModifier(BilProxyKind.Wildcard),
                }));
            module.LocalSymbols.Add(w);

            var vec = new BilTypeDeclaration("Vec", BilTypeKind.Struct,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich),
                new BilWrappedModifier("W"));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "Vec#x@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "Vec#y@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Vec$init(x:.i32,y:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Vec$..init.wrapper()@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilKeywordModifier(BilKeyword.CompilerGenerated),
                }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Vec$$plus(other:Vec)@Vec",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilOperatorModifier("plus"),
                }));
            module.LocalSymbols.Add(vec);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));

            var proxy = new BilFunction("W$$.proxy.opr.*(symbol:.string)@Vec");
            proxy.Args.Add(new BilArgDeclaration(".return", "Vec"));
            proxy.Args.Add(new BilArgDeclaration(".this", "W"));
            proxy.Args.Add(new BilArgDeclaration("symbol", ".string"));
            proxy.Args.Add(new BilArgDeclaration(".kwargs.namedArgs",
                ".array<.pair<.string, .any>>"));
            proxy.Args.Add(new BilArgDeclaration(".vargs.unnamedArgs", ".array<.any>"));
            proxy.Vars.Add(new BilVarDeclaration(".i32", "n99"));
            proxy.Vars.Add(new BilVarDeclaration(".i32", "z0"));
            proxy.Vars.Add(new BilVarDeclaration("Vec", "r"));
            var proxyEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            proxyEntry.Instructions.Add(new LoadInstruction(module.Resources[0],
                BilOp.Var("n99")));
            proxyEntry.Instructions.Add(new LoadInstruction(module.Resources[1],
                BilOp.Var("z0")));
            proxyEntry.Instructions.Add(new NewInstruction(BilOp.Type("Vec"),
                BilOp.Var("r"), new[] { BilOp.Var("n99"), BilOp.Var("z0") }));
            proxyEntry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            proxy.Blocks.Add(proxyEntry);
            module.Functions.Add(proxy);

            var init = new BilFunction("Vec$init(x:.i32,y:.i32)@.void");
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", "Vec"));
            init.Args.Add(new BilArgDeclaration("x", ".i32"));
            init.Args.Add(new BilArgDeclaration("y", ".i32"));
            var initEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            initEntry.Instructions.Add(new SetFieldInstruction(BilOp.Var("x"),
                BilOp.Var(".this"), BilOp.Field("Vec#x@.i32")));
            initEntry.Instructions.Add(new SetFieldInstruction(BilOp.Var("y"),
                BilOp.Var(".this"), BilOp.Field("Vec#y@.i32")));
            initEntry.Instructions.Add(new RetInstruction());
            init.Blocks.Add(initEntry);
            module.Functions.Add(init);

            var initWrapper = new BilFunction("Vec$..init.wrapper()@.void");
            initWrapper.Args.Add(new BilArgDeclaration(".return", ".void"));
            initWrapper.Args.Add(new BilArgDeclaration(".this", "Vec"));
            var wrapperEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            wrapperEntry.Instructions.Add(new NewWrapperEntityInstruction(BilOp.Type("W"),
                Array.Empty<BilVariableOperand>()));
            wrapperEntry.Instructions.Add(new RetInstruction());
            initWrapper.Blocks.Add(wrapperEntry);
            module.Functions.Add(initWrapper);

            var plus = new BilFunction("Vec$$plus(other:Vec)@Vec");
            plus.Args.Add(new BilArgDeclaration(".return", "Vec"));
            plus.Args.Add(new BilArgDeclaration(".this", "Vec"));
            plus.Args.Add(new BilArgDeclaration("other", "Vec"));
            plus.Vars.Add(new BilVarDeclaration(".i32", "z0"));
            plus.Vars.Add(new BilVarDeclaration("Vec", "r"));
            var plusEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            plusEntry.Instructions.Add(new LoadInstruction(module.Resources[1],
                BilOp.Var("z0")));
            plusEntry.Instructions.Add(new NewInstruction(BilOp.Type("Vec"),
                BilOp.Var("r"), new[] { BilOp.Var("z0"), BilOp.Var("z0") }));
            plusEntry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            plus.Blocks.Add(plusEntry);
            module.Functions.Add(plus);

            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "n99"));
            main.Vars.Add(new BilVarDeclaration(".i32", "z0"));
            main.Vars.Add(new BilVarDeclaration("Vec", "a"));
            main.Vars.Add(new BilVarDeclaration("Vec", "b"));
            main.Vars.Add(new BilVarDeclaration("Vec", "c"));
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(module.Resources[0],
                BilOp.Var("n99")));
            entry.Instructions.Add(new LoadInstruction(module.Resources[1],
                BilOp.Var("z0")));
            entry.Instructions.Add(new NewWrappedInstruction(BilOp.Type("Vec"),
                BilOp.Var("a"), Array.Empty<BilVariableOperand>(),
                new[] { BilOp.Var("n99"), BilOp.Var("z0") }));
            entry.Instructions.Add(new NewWrappedInstruction(BilOp.Type("Vec"),
                BilOp.Var("b"), Array.Empty<BilVariableOperand>(),
                new[] { BilOp.Var("z0"), BilOp.Var("n99") }));
            entry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("b"), BilOp.Var("c")));
            entry.Instructions.Add(new GetFieldInstruction(BilOp.Var("c"),
                BilOp.Var("x"), BilOp.Field("Vec#x@.i32")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        // != 直构：Vec 带 wrapped(W)，W 的 .proxy.opr.equals 恒 true；`a != b`
        // 由 equals 取反推导，因此结果为 false（原始 equals 返回 false 但不会
        // 被走到）。
        private static BilModule WrappedVecNeModule()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_1", BilScalarType.I32, "1"));
            module.Resources.Add(new BilScalarResource("R_2", BilScalarType.I32, "2"));
            module.Resources.Add(new BilScalarResource("R_TRUE", BilScalarType.Bool, "true"));
            module.Resources.Add(new BilScalarResource("R_FALSE", BilScalarType.Bool, "false"));

            var w = new BilTypeDeclaration("W", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            w.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "W$$.proxy.opr.equals(other:Vec)@.bool",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilOperatorModifier(".proxy.opr.equals"),
                    new BilWrapperProxyModifier(BilProxyKind.Specific),
                }));
            module.LocalSymbols.Add(w);

            var vec = new BilTypeDeclaration("Vec", BilTypeKind.Struct,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich),
                new BilWrappedModifier("W"));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "Vec#x@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "Vec#y@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Vec$init(x:.i32,y:.i32)@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Init),
                }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Vec$..init.wrapper()@.void",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilKeywordModifier(BilKeyword.CompilerGenerated),
                }));
            vec.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Vec$$equals(other:Vec)@.bool",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilOperatorModifier("equals"),
                }));
            module.LocalSymbols.Add(vec);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.bool",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));

            var proxy = new BilFunction("W$$.proxy.opr.equals(other:Vec)@.bool");
            proxy.Args.Add(new BilArgDeclaration(".return", ".bool"));
            proxy.Args.Add(new BilArgDeclaration(".this", "W"));
            proxy.Args.Add(new BilArgDeclaration("other", "Vec"));
            proxy.Vars.Add(new BilVarDeclaration(".bool", "t"));
            var proxyEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            proxyEntry.Instructions.Add(new LoadInstruction(module.Resources[2],
                BilOp.Var("t")));
            proxyEntry.Instructions.Add(new RetInstruction(BilOp.Var("t")));
            proxy.Blocks.Add(proxyEntry);
            module.Functions.Add(proxy);

            var init = new BilFunction("Vec$init(x:.i32,y:.i32)@.void");
            init.Args.Add(new BilArgDeclaration(".return", ".void"));
            init.Args.Add(new BilArgDeclaration(".this", "Vec"));
            init.Args.Add(new BilArgDeclaration("x", ".i32"));
            init.Args.Add(new BilArgDeclaration("y", ".i32"));
            var initEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            initEntry.Instructions.Add(new SetFieldInstruction(BilOp.Var("x"),
                BilOp.Var(".this"), BilOp.Field("Vec#x@.i32")));
            initEntry.Instructions.Add(new SetFieldInstruction(BilOp.Var("y"),
                BilOp.Var(".this"), BilOp.Field("Vec#y@.i32")));
            initEntry.Instructions.Add(new RetInstruction());
            init.Blocks.Add(initEntry);
            module.Functions.Add(init);

            var initWrapper = new BilFunction("Vec$..init.wrapper()@.void");
            initWrapper.Args.Add(new BilArgDeclaration(".return", ".void"));
            initWrapper.Args.Add(new BilArgDeclaration(".this", "Vec"));
            var wrapperEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            wrapperEntry.Instructions.Add(new NewWrapperEntityInstruction(BilOp.Type("W"),
                Array.Empty<BilVariableOperand>()));
            wrapperEntry.Instructions.Add(new RetInstruction());
            initWrapper.Blocks.Add(wrapperEntry);
            module.Functions.Add(initWrapper);

            var equals = new BilFunction("Vec$$equals(other:Vec)@.bool");
            equals.Args.Add(new BilArgDeclaration(".return", ".bool"));
            equals.Args.Add(new BilArgDeclaration(".this", "Vec"));
            equals.Args.Add(new BilArgDeclaration("other", "Vec"));
            equals.Vars.Add(new BilVarDeclaration(".bool", "f"));
            var equalsEntry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            equalsEntry.Instructions.Add(new LoadInstruction(module.Resources[3],
                BilOp.Var("f")));
            equalsEntry.Instructions.Add(new RetInstruction(BilOp.Var("f")));
            equals.Blocks.Add(equalsEntry);
            module.Functions.Add(equals);

            var main = new BilFunction("$main()@.bool");
            main.Args.Add(new BilArgDeclaration(".return", ".bool"));
            main.Vars.Add(new BilVarDeclaration(".i32", "one"));
            main.Vars.Add(new BilVarDeclaration(".i32", "two"));
            main.Vars.Add(new BilVarDeclaration("Vec", "a"));
            main.Vars.Add(new BilVarDeclaration("Vec", "b"));
            main.Vars.Add(new BilVarDeclaration(".bool", "r"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(module.Resources[0],
                BilOp.Var("one")));
            entry.Instructions.Add(new LoadInstruction(module.Resources[1],
                BilOp.Var("two")));
            entry.Instructions.Add(new NewWrappedInstruction(BilOp.Type("Vec"),
                BilOp.Var("a"), Array.Empty<BilVariableOperand>(),
                new[] { BilOp.Var("one"), BilOp.Var("two") }));
            entry.Instructions.Add(new NewWrappedInstruction(BilOp.Type("Vec"),
                BilOp.Var("b"), Array.Empty<BilVariableOperand>(),
                new[] { BilOp.Var("one"), BilOp.Var("two") }));
            entry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.CmpNe,
                BilOp.Var("a"), BilOp.Var("b"), BilOp.Var("r")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("r")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        // Method wrapper .proxy.call 的 get.self 直构：proxy 模板 fn 的
        // .this 是 wrapper 实例，get.self 产出已安装的宿主（Host.n == 42）。
        private static BilModule MethodWrapperGetSelfModule()
        {
            var module = new BilModule();
            var timed = new BilTypeDeclaration("Timed", BilTypeKind.Wrapper,
                new BilAccessibilityModifier(BilAccessibility.Public),
                new BilKeywordModifier(BilKeyword.Rich));
            timed.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "Timed$$.proxy.call(x:.i32)@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Private),
                    new BilOperatorModifier(".proxy.call"),
                    new BilWrapperProxyModifier(BilProxyKind.Specific),
                }));
            module.LocalSymbols.Add(timed);

            var host = new BilTypeDeclaration("Host", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public));
            host.Members.Add(new BilSimpleMemberDeclaration(BilMemberKind.Field,
                "Host#n@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            module.LocalSymbols.Add(host);

            var proxy = new BilFunction("Timed$$.proxy.call(x:.i32)@.i32");
            proxy.Args.Add(new BilArgDeclaration(".return", ".i32"));
            proxy.Args.Add(new BilArgDeclaration(".this", "Timed"));
            proxy.Args.Add(new BilArgDeclaration("x", ".i32"));
            proxy.Vars.Add(new BilVarDeclaration("Host", "s"));
            proxy.Vars.Add(new BilVarDeclaration(".i32", "n"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new GetSelfInstruction(BilOp.Var("s")));
            entry.Instructions.Add(new GetFieldInstruction(BilOp.Var("s"),
                BilOp.Var("n"), BilOp.Field("Host#n@.i32")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("n")));
            proxy.Blocks.Add(entry);
            module.Functions.Add(proxy);
            return module;
        }

        // 未绑定语境下 .generic 参与 cast 的负例直构：fn 没有同名 .generic
        // hidden 实参槽位，执行期类型解析必须抛 VmException（绝不恒等放行）。
        private static BilModule UnboundGenericCastModule()
        {
            var module = new BilModule();
            module.Resources.Add(new BilScalarResource("R_1", BilScalarType.I32, "1"));
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
            main.Vars.Add(new BilVarDeclaration(".generic<$.generic.T>", "r"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new LoadInstruction(module.Resources[0],
                BilOp.Var("n")));
            entry.Instructions.Add(new CastInstruction(BilOp.Var("n"), BilOp.Var("r"),
                BilOp.Type(".generic<$.generic.T>"), isSafe: false));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("n")));
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

    }
}
