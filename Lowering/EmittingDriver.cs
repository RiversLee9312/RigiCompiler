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
            foreach (var body in bodies.Where(b => b.Method.IsSynthetic))
            {
                env.Module.LocalSymbols.Add(
                    LocalSymbolEmitters.EmitSyntheticMethodDeclaration(body.Method));
            }
            // lambda 隐藏类声明（SYNTAX §5.2）：合成类型不进符号图（避免污染
            // 冻结的用户命名空间图），声明由函数体的宿主归属驱动收集——
            // init 体与 $$call 体的 Owner 即隐藏类；只有 bodies 在场的类
            // 才发射声明（降级失败的函数体不产生声明，与 §21.2 fn↔声明
            // 对应检查同口径）
            foreach (var hiddenClass in bodies
                .Select(b => b.Method.Owner)
                .Where(owner => owner?.LambdaClosure != null)
                .Distinct()
                .Cast<TypeSymbol>())
            {
                env.Module.LocalSymbols.Add(
                    LocalSymbolEmitters.EmitSyntheticTypeDeclaration(hiddenClass, env));
            }
            // cell 隐藏子类声明（统一 cell 存储，SYNTAX §5.2/§14.3）：同
            // lambda 收集口径——init/getValue/setValue 体的 Owner 即子类
            foreach (var cellClass in bodies
                .Select(b => b.Method.Owner)
                .Where(owner => owner?.CellStorage != null)
                .Distinct()
                .Cast<TypeSymbol>())
            {
                env.Module.LocalSymbols.Add(
                    LocalSymbolEmitters.EmitSyntheticTypeDeclaration(cellClass, env));
            }
            // 静态 Method wrapper companion singleton（M109b-2，BIL §8.7）
            foreach (var companion in bodies
                .Select(b => b.Method.Owner)
                .Where(owner => owner?.CompanionInfo != null)
                .Distinct()
                .Cast<TypeSymbol>())
            {
                env.Module.LocalSymbols.Add(
                    LocalSymbolEmitters.EmitSyntheticTypeDeclaration(companion, env));
            }
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
        // .args 按 §9.2/§7.2 顺序——.return 在前，实例方法 .this 次之
        // （§7.3：ext 成员同以 .this 表示被扩展值的 receiver），
        // 固定泛型隐藏参数（S9e：.generic.T = .typeid，声明序）随后，
        // 普通参数最后（可变参数包随 S9d）
        private BilFunction? EmitFunction(LoweredFunctionBody body)
        {
            var method = body.Method;
            // S11e 开闸：call??? 降级链符号（router/降级特化/Any.call???）
            // 的 fn 定义全部平铺发射——P3 阶段 2.6 已绑体。Any.call???
            // 宿主内建类型不进符号段（EmitTypeTree 跳过 IsBuiltin）无声明，
            // 其 fn 定义对应检查由 BilVerifier §21.2 的 builtin 宿主豁免
            // 承担（BilVerificationContext.IsPredefinedTypeHost）
            var function = new BilFunction(CanonicalSymbolPrinter.PrintMethod(method));
            // .args（§9.2/§7.2）：.return →（实例）.this → .generic.* → 普通参数
            function.Args.Add(new BilArgDeclaration(".return",
                CanonicalSymbolPrinter.PrintTypeReference(method.ReturnType)));
            if (method.Owner != null && !method.IsStatic)
            {
                function.Args.Add(new BilArgDeclaration(".this",
                    CanonicalSymbolPrinter.PrintType(method.Owner)));
            }
            // 泛型隐藏参数（S9e/S9d-2，§7.1/§7.2 序：固定泛型 → 泛型可变包）：
            // 固定 .generic.T = .typeid；位置包 .generic.TArgs = .array<.typeid>
            // （.typeid 无边界 ≡ .typeid<.any>，投影即 .array<.typeid<.any>>）；
            // 具名包 .generic.TValues = .map<.string, .typeid>（§6.3 标准构造）
            var typeIdType = env.Unit.Symbols.GetConstructedType(
                env.Unit.Symbols.Bootstrap.TypeDefinition, env.Unit.Symbols.Bootstrap.Any);
            foreach (var genericParameter in method.GenericParameters)
            {
                if (genericParameter.IsNamedVariadic)
                {
                    var mapType = env.Unit.Symbols.GetConstructedType(
                        env.Unit.Symbols.Bootstrap.MapDefinition,
                        env.Unit.Symbols.Bootstrap.String, typeIdType);
                    function.Args.Add(new BilArgDeclaration(
                        ".generic." + genericParameter.Name,
                        CanonicalSymbolPrinter.PrintType(mapType)));
                }
                else if (genericParameter.IsVariadic)
                {
                    var arrayType = env.Unit.Symbols.GetConstructedType(
                        env.Unit.Symbols.Bootstrap.ArrayDefinition, typeIdType);
                    function.Args.Add(new BilArgDeclaration(
                        ".generic." + genericParameter.Name,
                        CanonicalSymbolPrinter.PrintType(arrayType)));
                }
                else
                {
                    function.Args.Add(new BilArgDeclaration(
                        ".generic." + genericParameter.Name, ".typeid"));
                }
            }
            foreach (var parameter in method.Parameters)
            {
                if (parameter.IsVariadic || parameter.IsNamedVariadic) continue;
                function.Args.Add(new BilArgDeclaration(parameter.Name,
                    CanonicalSymbolPrinter.PrintTypeReference(parameter.Type)));
            }
            // 可变参数隐藏条目（S9d，§7.1/§7.2 序：普通参数后 vargs → kwargs）：
            // 位置包 .vargs.<名> = .array<.any>、具名包 .kwargs.<名> =
            // .array<.pair<.string, .any>>（值进统一 Any 胖值槽，RUNTIME §10）。
            // 具名可变参数 IsNamedVariadic 同时带 IsVariadic（嵌套语义）——
            // 位置包只收「纯位置」；两趟分发保证源序 named 先于 positional
            // 时仍满足 §7.2（vargs 先于 kwargs）
            foreach (var parameter in method.Parameters)
            {
                if (parameter.IsVariadic && !parameter.IsNamedVariadic)
                {
                    function.Args.Add(new BilArgDeclaration(".vargs." + parameter.Name,
                        ".array<.any>"));
                }
            }
            foreach (var parameter in method.Parameters)
            {
                if (parameter.IsNamedVariadic)
                {
                    function.Args.Add(new BilArgDeclaration(".kwargs." + parameter.Name,
                        ".array<.pair<.string, .any>>"));
                }
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
            // 合成局部是 .breakid capability（§9.3 别名，无 TypeSymbol）。
            // cell 化局部（统一 cell 存储，SYNTAX §5.2/§14.3）：存储类型
            // 为逐变量合成的隐藏子类——.vars 条目按 CellStorage 投影
            foreach (var local in body.Locals)
            {
                function.Vars.Add(new BilVarDeclaration(
                    local.Type == null
                        ? ".breakid"
                        : LocalStorageTypeRef(local), local.Name));
            }
            function.Vars.AddRange(ctx.Temps.TempVars);
            return function;
        }

        // 局部的 BIL 存储类型引用（§9.3 .vars 条目）：cell 化局部投影为
        // 隐藏子类（统一 cell 存储）；普通局部为声明类型 canonical
        private static string LocalStorageTypeRef(LocalSymbol local)
        {
            if (local.CellStorage is { } storage)
            {
                return CanonicalSymbolPrinter.PrintType(storage.CellType);
            }
            return CanonicalSymbolPrinter.PrintType(local.Type!);
        }
    }
}
