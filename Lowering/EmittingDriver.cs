using LatteCompiler.Bil;

namespace LatteCompiler
{
    // P4b 发射驱动器：metadata module 条目 → LocalSymbols 段（命名空间
    // 平铺 + 内建 ext 成员）→ 逐函数体发射 fn 定义。每个函数新建独立
    // EmitContext（临时变量表与各 block 计数器随函数隔离——替代旧
    // EmitSession 的字段清零；同 BindContext/LowerContext 原则）。
    internal sealed class EmittingDriver
    {
        private readonly EmitEnvironment env;
        private readonly IReadOnlyList<LoweredFunctionBody> bodies;

        public EmittingDriver(EmitEnvironment env, IReadOnlyList<LoweredFunctionBody> bodies)
        {
            this.env = env;
            this.bodies = bodies;
        }

        public BilModule Run()
        {
            // §4.1：源模块名（LiteralText 含引号；moduleName 由编译器内部给定）
            env.Module.Metadata.Add(new BilMetadataEntry("module", BilScalarType.String,
                $"\"{env.ModuleName}\""));
            LocalSymbolEmitters.EmitNamespace(env.Unit.Symbols.GlobalNamespace, env);
            LocalSymbolEmitters.EmitBuiltinExtMembers(env);
            // Resources 在函数发射中按（bodies 顺序 + 树内先序）登记
            foreach (var body in bodies)
            {
                var function = EmitFunction(body);
                if (function != null) env.Module.Functions.Add(function);
            }
            return env.Module;
        }

        // ===== Functions（§9）=====

        // null 返回 = 已诊断跳过。S7c-2 开闸实例方法（含 ext 成员）：
        // .args 按 §9.2 顺序——.return 在前，实例方法 .this 次之
        // （§7.3：ext 成员同以 .this 表示被扩展值的 receiver），
        // 普通参数随后（隐藏参数随 S9 落地）
        private BilFunction? EmitFunction(LoweredFunctionBody body)
        {
            var method = body.Method;
            var function = new BilFunction(CanonicalSymbolPrinter.PrintMethod(method));
            // .args（§9.2）：.return →（实例）.this → 普通参数
            function.Args.Add(new BilArgDeclaration(".return",
                CanonicalSymbolPrinter.PrintTypeReference(method.ReturnType)));
            if (method.Owner != null && !method.IsStatic)
            {
                function.Args.Add(new BilArgDeclaration(".this",
                    CanonicalSymbolPrinter.PrintType(method.Owner)));
            }
            foreach (var parameter in method.Parameters)
            {
                function.Args.Add(new BilArgDeclaration(parameter.Name,
                    CanonicalSymbolPrinter.PrintTypeReference(parameter.Type)));
            }
            // 指令生成（临时变量在生成中登记）：entry block 先行入列，
            // if 分支 block 随 LoweredIfStatement 发射追加（§16.2）、
            // loop 的 body/judge block 随 LoweredLoop 发射追加（§16.3/§16.4）、
            // switch 的 item/default block 随 LoweredSwitch 发射追加（§16.6）、
            // seq 块随 LoweredSeqBlock 发射追加（§16.1）、try 的
            // body/catch/finally block 随 LoweredTryStatement 发射追加（§16.7）
            var ctx = new EmitContext(function);
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            function.Blocks.Add(entry);
            EmitBlockVisitor.Visit(body.Body, entry, ctx, env);
            // §9.4：entrypoint block 不得正常落到末尾——void 函数体无显式
            // return 时补 ret（如 stdlib println）；分支 block 落尾自然
            // 返回引用它的结构化指令，不补 ret
            if (method.ReturnType == null
                && (entry.Instructions.Count == 0
                    || entry.Instructions[entry.Instructions.Count - 1] is not RetInstruction))
            {
                entry.Instructions.Add(new RetInstruction());
            }
            // .vars（§9.3）：Locals 在前、临时变量在后；Type 为 null 的
            // 合成局部是 .breakid capability（§9.3 别名，无 TypeSymbol）
            foreach (var local in body.Locals)
            {
                function.Vars.Add(new BilVarDeclaration(
                    local.Type == null
                        ? ".breakid"
                        : CanonicalSymbolPrinter.PrintType(local.Type), local.Name));
            }
            function.Vars.AddRange(ctx.Temps.TempVars);
            return function;
        }
    }
}
