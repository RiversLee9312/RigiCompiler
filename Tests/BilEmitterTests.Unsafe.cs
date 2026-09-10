using RigiCompiler.Bil;

namespace RigiCompiler.Tests
{
    public static partial class BilEmitterTests
    {
        private static void TestUnsafeProjection()
        {
            TestHandleProjection();
            TestHarness.Section("unsafe BIL 投影与权限校验");
            var result = BilTestHarness.EmitBilUnit("unsafe func danger(): i32 { return 7 }\n"
                + "pub func main(): i32 { unsafe volatile seq {\n"
                + "if (true) { danger() }\n}\n return 0 }");
            CheckNoErrors("unsafe 全管线", result.Unit);
            BilTestHarness.CheckBilValid("unsafe 允许嵌套分支调用", result.Module);
            var main = result.Module.Functions.Single(f => f.Symbol == "$main()@.i32");
            var unsafeBlock = main.Blocks.Single(b => b.Modifiers.Contains(BilBlockModifier.Unsafe));
            TestHarness.CheckTrue("正交块修饰符都保留", unsafeBlock.Modifiers.Contains(BilBlockModifier.Volatile));
            var text = BilWriter.Write(result.Module);
            var roundtrip = BilReader.Read(text);
            TestHarness.Check("unsafe Reader/Writer 往返", BilWriter.Write(roundtrip), text);
            BilTestHarness.CheckBilValid("往返后仍合法", roundtrip);

            foreach (var source in new[]
            {
                "unsafe class Secret { pub static func ping(): i32 { return 1 } }\n"
                    + "pub func main(): i32 { return unsafe seq { Secret.ping() } }",
                "unsafe class Secret { }\n"
                    + "pub func main(): i32 { unsafe seq { const t = typeOf(Secret)\n const x = new t() }\n return 0 }",
                "class Secret { pub unsafe init() { } }\n"
                    + "pub func main(): i32 { unsafe seq { const t = typeOf(Secret)\n const x = new t() }\n return 0 }",
                "class Secret { pub unsafe operator call(): i32 { return 1 } }\n"
                    + "pub func main(): i32 { const s = new Secret()\n return unsafe seq { s() } }",
            })
            {
                var gated = BilTestHarness.EmitBilUnit(source);
                CheckNoErrors("unsafe 调用元数据输入", gated.Unit);
                BilTestHarness.CheckBilValid("unsafe 调用元数据允许", gated.Module);
                var gatedMain = gated.Module.Functions.Single(f => f.Symbol == "$main()@.i32");
                // 保留 block 对象身份，引用图仍指向同一块；仅撤销危险权限。
                foreach (var block in gatedMain.Blocks)
                {
                    var modifiers = (BilBlockModifier[])block.Modifiers;
                    for (var i = 0; i < modifiers.Length; i++)
                        if (modifiers[i] == BilBlockModifier.Unsafe) modifiers[i] = BilBlockModifier.Volatile;
                }
                BilTestHarness.CheckBilInvalid("移除权限后调用元数据拒绝", gated.Module, "unsafe");
            }
            // 从安全入口额外引用危险块内的普通子块，不能沿用另一路径的权限。
            var branch = unsafeBlock.Instructions.OfType<IfInstruction>().Single().ThenBlock;
            var entry = main.Blocks.Single(b => b.Modifiers.Contains(BilBlockModifier.Entrypoint));
            var breakId = entry.Instructions.OfType<CallBlockInstruction>().Single().BreakId;
            entry.Instructions.Insert(0, new CallBlockInstruction(branch, breakId));
            BilTestHarness.CheckBilInvalid("共享子块不得洗白安全路径", result.Module, "unsafe 方法调用需要");
        }

        private static void TestHandleProjection()
        {
            TestHarness.Section("Handle 固定 ABI 与 Place 验证边界");
            var result = BilTestHarness.EmitBilUnit("pub func main(): i32 { var n = 1\n"
                + "unsafe seq using(const p = placeOf n) { const h = p.expose()\n"
                + "h.asMutable().store(2)\n }\n return 0 }");
            CheckNoErrors("Handle 全管线", result.Unit);
            BilTestHarness.CheckBilValid("Handle 有效能力流", result.Module);
            var declaration = result.Module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol == ".handle");
            TestHarness.CheckTrue(".handle 唯一且无泛型/可枚举字段",
                declaration.GenericParameters.Count == 0 && declaration.Members.Count == 0
                && !BilWriter.Write(result.Module).Contains(".handle<"));
            foreach (var name in new[] { "core::Handle", "core::MutableHandle" })
            {
                var facade = result.Module.LocalSymbols.OfType<BilTypeDeclaration>()
                    .Single(t => t.Symbol == name);
                TestHarness.CheckTrue(name + " 独立具化声明", facade.GenericParameters.Count == 1);
                TestHarness.CheckTrue(name + " 普通成员体保留",
                    result.Module.Functions.Any(f => f.Symbol.StartsWith(name + "$load(")));
            }
            var roundtrip = BilReader.Read(BilWriter.Write(result.Module));
            BilTestHarness.CheckBilValid(".handle Reader/Writer 往返", roundtrip);
            var main = roundtrip.Functions.Single(f => f.Symbol == "$main()@.i32");
            var block = main.Blocks.Single(b => b.Modifiers.Contains(BilBlockModifier.Unsafe));
            var create = block.Instructions.OfType<NewInstruction>().Single();
            var at = block.Instructions.IndexOf(create);
            block.Instructions[at] = new NewInstruction(new BilTypeOperand(".handle"),
                create.Target, Array.Empty<BilVariableOperand>());
            BilTestHarness.CheckBilInvalid("BIL 禁止 new .handle", roundtrip, ".handle 只能由");
            block.Instructions[at] = new NewInstruction(create.Type, create.Target,
                new[] { create.Arguments[^1], create.Arguments[^1] });
            BilTestHarness.CheckBilInvalid("BIL 禁止临时整数伪造 Place", roundtrip, "稳定 Cell");
            block.Instructions[at] = create;
            var native = roundtrip.LocalSymbols.OfType<BilSimpleMemberDeclaration>()
                .Single(m => m.Symbol == "core::$handle_target(capability:.any)@.any");
            block.Instructions.Add(new InvokeNoResultInstruction(new BilFnOperand(native.Symbol),
                new[] { create.Arguments[0] }));
            BilTestHarness.CheckBilInvalid("BIL 禁止调用隐藏 target", roundtrip, "隐藏机制不能");
        }
    }
}
