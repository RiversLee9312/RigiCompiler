using RigiCompiler.Bil;

namespace RigiCompiler
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
            // 无 body 的合成接口也须在声明/canonical 发射之前具有真实来源。
            ModuleOrigin.AssignDeclarations(env.Unit, bodies.Select(b => b.Method.Owner).OfType<TypeSymbol>());
            // 合成声明不一定经过 P1 Map；投影前补来源，保持源名不变。
            foreach (var body in bodies)
            {
                ModuleOrigin.Assign(body.Method, env.Unit);
                for (var owner = body.Method.Owner; owner != null; owner = owner.DeclaringType)
                    ModuleOrigin.Assign(owner, env.Unit);
                if (env.Unit.IsModuleCompilation && BilLogicalName.IsGlobalInitializerName(body.Method.Name))
                    env.Module.Metadata.Add(new BilMetadataEntry(BilModuleInitialization.MetadataPrefix + ModuleOrigin.Hash(env.Unit.ModuleIdentity),
                        BilScalarType.String, "\"" + CanonicalSymbolPrinter.PrintMethod(body.Method) + "\""));
            }
            // §4.1：源模块名（LiteralText 含引号；moduleName 由编译器内部给定）
            env.Module.Metadata.Add(new BilMetadataEntry("module", BilScalarType.String,
                $"\"{env.ModuleName}\""));
            // 仅编译器自携 intrinsics 声明有权登记 helper，用户同名私有函数不获得权限。
            foreach (var helper in env.Unit.Symbols.GetNamespace(["core"]).Methods.Where(m =>
                m.Name is "any_hash" or "any_to_string" && m.IsCompilerLibrary && !m.IsImported
                && m.IsNative && m.NativeLibrary == "rigi_rt" && m.NativeSymbol == m.Name))
                env.Module.Metadata.Add(new BilMetadataEntry(BilCompilerHelpers.MetadataPrefix + helper.Name,
                    BilScalarType.String, "\"" + CanonicalSymbolPrinter.PrintMethod(helper) + "\""));
            LocalSymbolEmitters.EmitNamespace(env.Unit.Symbols.GlobalNamespace, env);
            LocalSymbolEmitters.EmitBuiltinExtMembers(env);
            LocalSymbolEmitters.EmitBuiltinNativeMethods(env);
            foreach (var body in bodies.Where(b => b.Method.IsSynthetic))
            {
                env.CurrentSliceNs = LocalSymbolEmitters.SliceNsOf(body.Method);
                env.AddLocalSymbol(
                    LocalSymbolEmitters.EmitSyntheticMethodDeclaration(body.Method));
            }
            foreach (var body in bodies.Where(b => b.Method.IsImported
                && env.Unit.Symbols.LateHelperOverrides.Contains(CanonicalSymbolPrinter.PrintMethod(b.Method))))
                env.AddLocalSymbol(LocalSymbolEmitters.EmitSyntheticMethodDeclaration(body.Method));
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
                env.CurrentSliceNs = LocalSymbolEmitters.RootNsOf(hiddenClass);
                env.AddLocalSymbol(
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
                env.CurrentSliceNs = LocalSymbolEmitters.RootNsOf(cellClass);
                env.AddLocalSymbol(
                    LocalSymbolEmitters.EmitSyntheticTypeDeclaration(cellClass, env));
            }
            // 静态 Method wrapper companion singleton（M109b-2，BIL §8.7）：
            // 仅 companion 自身（CompanionInfo 自指）——宿主类也持 CompanionInfo
            // 作反向链接，不在此重复发射
            foreach (var companion in bodies
                .Select(b => b.Method.Owner)
                .Where(owner => owner?.CompanionInfo != null
                    && ReferenceEquals(owner.CompanionInfo.CompanionType, owner))
                .Distinct()
                .Cast<TypeSymbol>())
            {
                env.CurrentSliceNs = LocalSymbolEmitters.RootNsOf(companion);
                env.AddLocalSymbol(
                    LocalSymbolEmitters.EmitSyntheticTypeDeclaration(companion, env));
            }
            // 声明/枚举判别值完成后，函数独占资源池和发射环境；不共享 R_N 计数。
            var chunks = CompilerJobs.MapDiagnosed(env.Unit, bodies.Count, index =>
            {
                var local = new EmitEnvironment(env.Unit, env.ModuleName)
                {
                    CurrentSliceNs = LocalSymbolEmitters.SliceNsOf(bodies[index].Method),
                };
                var function = new EmittingDriver(local, Array.Empty<LoweredFunctionBody>())
                    .EmitFunction(bodies[index]);
                return (Function: function, Environment: local);
            }, phase: "emitting.P4b.functions");
            // 按函数序回放跨种类首次出现序，复用旧模块级 intern 键。
            foreach (var chunk in chunks)
            {
                env.CurrentSliceNs = chunk.Environment.CurrentSliceNs;
                var resources = ImportResources(chunk.Environment.Module.Resources);
                if (chunk.Function == null) continue;
                foreach (var block in chunk.Function.Blocks)
                    for (var index = 0; index < block.Instructions.Count; index++)
                        block.Instructions[index] = RemapResource(block.Instructions[index], resources);
                env.AddFunction(chunk.Function);
            }
            // 导入接口不重新发射裁剪过的语义描述：保留 provider 的完整 typed ABI。
            // enum 判别资源按本模块资源池偏移克隆，绝不改缓存对象。
            foreach (var artifact in env.Unit.Symbols.ImportedArtifacts)
            {
                var imported = BilModuleLinker.Clone(artifact.ReadBil(), env.Module.Resources.Count);
                env.Module.Resources.AddRange(imported.Resources);
                foreach (var declaration in imported.LocalSymbols.Concat(imported.ExternalSymbols))
                {
                    var key = BilModuleLinker.Key(declaration);
                    if (!env.Module.ExternalSymbols.Any(d => BilModuleLinker.Key(d) == key))
                        env.AddExternalSymbol(declaration);
                }
                if (artifact.CompilerOwned)
                    foreach (var helper in imported.Metadata.Where(m => m.Key.StartsWith(BilCompilerHelpers.MetadataPrefix, StringComparison.Ordinal)
                        || m.Key.StartsWith(BilCompilerSymbols.MetadataPrefix, StringComparison.Ordinal)))
                        env.Module.Metadata.Add(helper);
            }
            // §17 切片收尾：回填各切片实际引用的共享资源（单文件自足）
            env.FinalizeSlices();
            return env.Module;
        }

        private Dictionary<BilResource, BilResource> ImportResources(IReadOnlyList<BilResource> resources)
        {
            var map = new Dictionary<BilResource, BilResource>();
            foreach (var resource in resources)
            {
                BilResource global;
                var name = "R_" + env.Module.Resources.Count;
                switch (resource)
                {
                    case BilScalarResource scalar:
                        global = EmittingFacility.RegisterScalarResource(scalar.Type, scalar.LiteralText, env);
                        break;
                    case BilNullResource nil:
                        if (!env.NullKeys.TryGetValue(nil.TypeRef, out var existingNull))
                        {
                            existingNull = new BilNullResource(name, nil.TypeRef);
                            env.NullKeys.Add(nil.TypeRef, existingNull);
                            env.AddResource(existingNull);
                        }
                        global = existingNull;
                        break;
                    case BilSwitchTableResource table:
                        var key = table.SelectorTypeRef + "|" + string.Join(",", table.Elements);
                        if (!env.SwitchTableKeys.TryGetValue(key, out var existingTable))
                        {
                            existingTable = new BilSwitchTableResource(name, table.SelectorTypeRef, table.Elements);
                            env.SwitchTableKeys.Add(key, existingTable);
                            env.AddResource(existingTable);
                        }
                        global = existingTable;
                        break;
                    case BilCatchTableResource catches:
                        // handler 对象属于当前函数，跨函数不能 intern。
                        global = new BilCatchTableResource(name, catches.Entries);
                        env.AddResource(global);
                        break;
                    case BilCollectionResource collection:
                        global = new BilCollectionResource(name, collection.Header, collection.Elements, collection.Multiline);
                        env.AddResource(global);
                        break;
                    default: throw new CompilerInternalException("未知函数资源类型: " + resource.GetType().Name);
                }
                map.Add(resource, global);
            }
            return map;
        }

        private static BilInstruction RemapResource(BilInstruction instruction,
            IReadOnlyDictionary<BilResource, BilResource> resources)
        {
            BilResource Resolve(BilResource resource) => resources.TryGetValue(resource, out var global) ? global : resource;
            BilInstruction replacement = instruction switch
            {
                LoadInstruction load => new LoadInstruction(Resolve(load.Resource), load.Target),
                HintInstruction hint => new HintInstruction(Resolve(hint.Resource)),
                SwitchInstruction select => new SwitchInstruction(select.Selector, Resolve(select.Table),
                    select.ItemBlocks, select.DefaultBlock, select.BreakId),
                TryInstruction attempt => new TryInstruction(attempt.Body, attempt.ExceptionSlot,
                    Resolve(attempt.CatchTable), attempt.FinallyBlock, attempt.BreakId),
                _ => instruction,
            };
            replacement.Origin = instruction.Origin;
            return replacement;
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
                    CanonicalSymbolPrinter.PrintInstanceSelfType(method.Owner)));
            }
            // 泛型隐藏参数（S9e/S9d-2，§7.1/§7.2 序：固定泛型 → 泛型可变包）：
            // 固定 .generic.T = .typeid；位置包 .generic.TArgs = .array<.typeid>
            // （.typeid 无边界 ≡ .typeid<.any>，投影即 .array<.typeid<.any>>）；
            // 具名包 .generic.TValues = .map<.string, .typeid>（§6.3 标准构造）。
            // 类/外层嵌套类型的泛型参数也打进方法帧（BIL §7.2 / RUNTIME §10）：
            // 方法体里 arrayOf\<TItem\> / T() 等要引用 $.generic.TItem；调用点
            // 可省略这些 typeid，VM 从 .this 构造形态注入。
            var typeIdType = env.Unit.Symbols.GetConstructedType(
                env.Unit.Symbols.Bootstrap.TypeDefinition, env.Unit.Symbols.Bootstrap.Any);
            foreach (var genericParameter in CollectFrameGenericParameters(method))
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
            // 同名局部唯一化（BIL §9.3 .vars 函数内唯一；兄弟作用域
            // 同名局部改名）——须在指令发射前建表，引用侧同表解析
            ctx.BuildLocalRenames(body.Locals);
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
                        : LocalStorageTypeRef(local), ctx.VariableNameOf(local)));
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

        // 方法帧可见的固定/可变泛型参数：外层类型（含嵌套声明链）在前，
        // 方法自有参数在后；同名由方法级遮蔽（不重复打进 .args）。
        private static List<GenericParameterSymbol> CollectFrameGenericParameters(
            MethodSymbol method)
        {
            // 静态成员没有宿主实例，也不能引用类级型参；否则多出的
            // typeid 槽会错位吞掉工厂的方法级实参与普通 source 参数。
            if (method.IsStatic) return new List<GenericParameterSymbol>(method.GenericParameters);
            var methodNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var genericParameter in method.GenericParameters)
            {
                methodNames.Add(genericParameter.Name);
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<GenericParameterSymbol>();
            var owners = new List<TypeSymbol>();
            for (var owner = method.Owner; owner != null; owner = owner.DeclaringType)
            {
                owners.Add(owner);
            }
            for (var i = owners.Count - 1; i >= 0; i--)
            {
                foreach (var genericParameter in owners[i].GenericParameters)
                {
                    if (methodNames.Contains(genericParameter.Name))
                    {
                        continue;
                    }
                    if (seen.Add(genericParameter.Name))
                    {
                        result.Add(genericParameter);
                    }
                }
            }
            result.AddRange(method.GenericParameters);
            return result;
        }
    }
}
