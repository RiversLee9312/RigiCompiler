using System.Text.Json;
using RigiCompiler.Bil;
using static RigiCompiler.Modules.ModuleInterface;

namespace RigiCompiler.Modules;

/// <summary>先建立全部 owner/符号壳，再填结构引用；GP 永远复用 owner 声明的同一对象。</summary>
internal static class ModuleInterfaceImporter
{
    internal static void Import(SymbolGraph graph, ModuleArtifact artifact)
    {
        try { ImportCore(graph, artifact); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException or BilLinkException)
        { throw new ModuleConfigurationException("模块接口导入失败：" + ex.Message); }
    }
    private static void ImportCore(SymbolGraph graph, ModuleArtifact artifact)
    {
        if (graph.IsFrozen) throw new ModuleConfigurationException("冻结符号图不能导入模块");
        using var json = Validate(artifact);
        var payload = json.RootElement.GetProperty("payload");
        CheckProperties(payload, "declarations", "closedTypes");
        if (!artifact.CompilerOwned)
        {
            // 普通 provider 仅可转递已经由 resolver 导入的可信绑定，不能从 DTO/BIL 自授机制权限。
            var authorized = graph.ImportedArtifacts.Where(a => a.CompilerOwned).SelectMany(a => a.ReadBil().Metadata)
                .Where(IsCompilerBinding).ToDictionary(m => m.Key, StringComparer.Ordinal);
            foreach (var binding in artifact.ReadBil().Metadata.Where(IsCompilerBinding))
                if (!authorized.TryGetValue(binding.Key, out var original) || binding.Type != original.Type
                    || binding.LiteralText != original.LiteralText)
                    throw new ModuleConfigurationException("普通 artifact 不能自授 compiler runtime 绑定");
        }
        var records = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in payload.GetProperty("declarations").EnumerateArray())
        {
            var id = Str(item, "id");
            if (string.IsNullOrWhiteSpace(id) || !records.TryAdd(id, item)) throw new ModuleConfigurationException("接口重复或空声明 ID");
        }
        var symbols = new Dictionary<string, SemanticSymbol>(graph.ImportedSymbols, StringComparer.Ordinal);
        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, item) in records)
        {
            if (!symbols.ContainsKey(id)) continue;
            if (graph.ImportedDeclarations.TryGetValue(id, out var previous) && previous != item.GetRawText())
                throw new ModuleConfigurationException("相同声明 ID 的接口契约不一致：" + id);
            existing.Add(id);
        }
        foreach (var (id, item) in records)
        {
            var builtin = Str(item, "tag") == "type" && Bool(item, "builtin");
            if (!builtin && !existing.Contains(id) && Str(item, "module") != artifact.ModuleId)
                throw new ModuleConfigurationException("接口新声明不能自宣其他 module 来源：" + id);
            if (!artifact.CompilerOwned && !existing.Contains(id) && !builtin && Str(item, "tag") == "type"
                && (Str(item, "alias") != null || Str(item, "constructor") != null || Bool(item, "sharedFromArgument")
                    || item.GetProperty("intrinsics").GetArrayLength() != 0))
                throw new ModuleConfigurationException("普通接口不能自授 builtin alias/constructor/intrinsic/shared ABI");
        }
        // 固定 manifest 不携源码；仅可信 std provider 可以为其叠加成员。
        foreach (var (id, item) in records.Where(p => Str(p.Value, "tag") == "type" && Bool(p.Value, "builtin")))
        {
            var name = Str(item, "name")!;
            if (!graph.Bootstrap.SourceTypes.TryGetValue(name, out var builtin)) throw new ModuleConfigurationException("未知内建接口类型");
            ValidateBuiltin(item, builtin, id);
            if (!symbols.TryGetValue(id, out var old))
            {
                symbols.Add(id, builtin);
                if (artifact.CompilerOwned) Common(builtin, item, id, true);
            }
            else if (!ReferenceEquals(old, builtin)) throw new ModuleConfigurationException("内建声明 ID 重绑定");
        }
        var building = new HashSet<string>(StringComparer.Ordinal);
        NamespaceSymbol? Namespace(JsonElement item) => Str(item, "namespace") is { } ns
            ? graph.GetNamespace(ns.Length == 0 ? [] : ns.Split('.')) : null;
        T? Symbol<T>(string? id) where T : SemanticSymbol => id == null ? null
            : Build(id) as T ?? throw new ModuleConfigurationException("接口引用声明种类错误：" + id);
        SemanticSymbol Build(string id)
        {
            if (symbols.TryGetValue(id, out var known)) return known;
            if (!records.TryGetValue(id, out var item)) throw new ModuleConfigurationException("接口悬空声明 ID：" + id);
            if (!building.Add(id)) throw new ModuleConfigurationException("接口 owner 环：" + id);
            var name = Str(item, "name") ?? throw new ModuleConfigurationException("接口声明 name 为空");
            SemanticSymbol symbol = Str(item, "tag") switch
            {
                "type" => new TypeSymbol(name, EnumValue<TypeKind>(item, "kind"), Namespace(item),
                    declaringType: Symbol<TypeSymbol>(Str(item, "owner")),
                    isRich: Bool(item, "rich"), isShared: Bool(item, "shared"),
                    isValueTypeBranch: Bool(item, "valueBranch"),
                    derivesSharedSafetyFromTypeArgument: Bool(item, "sharedFromArgument"),
                    bilAlias: Str(item, "alias"), bilStandardConstructor: Str(item, "constructor"),
                    intrinsicOps: item.GetProperty("intrinsics").EnumerateArray().Select(v => EnumNumber<BilIntrinsicOp>(v.GetInt32())).ToHashSet()),
                "method" => new MethodSymbol(name, EnumValue<MethodKind>(item, "kind"), Symbol<TypeSymbol>(Str(item, "owner")),
                    Namespace(item), Bool(item, "static"), Bool(item, "native"), extTargetPath: Str(item, "ext"), isAsync: Bool(item, "async")),
                "field" => new FieldSymbol(name, Symbol<TypeSymbol>(Str(item, "owner")), Namespace(item), Bool(item, "static"),
                    extTargetPath: Str(item, "ext"), isConst: Bool(item, "const")),
                "case" => new EnumCaseSymbol(name, Symbol<TypeSymbol>(Str(item, "owner")) ?? throw new ModuleConfigurationException("case 缺 owner")),
                _ => throw new ModuleConfigurationException("未知接口声明 tag")
            };
            symbols.Add(id, symbol); building.Remove(id);
            Common(symbol, item, id, artifact.CompilerOwned);
            return symbol;
        }
        foreach (var id in records.Keys) Build(id);
        // 第一遍尚未构造任何 closed type；GP 的只读 ctor flags 在此先落定。
        var genericsBuilt = new HashSet<string>(existing, StringComparer.Ordinal);
        var genericsBuilding = new HashSet<string>(StringComparer.Ordinal);
        void BuildGenerics(string id)
        {
            if (genericsBuilt.Contains(id)) return;
            if (!genericsBuilding.Add(id)) throw new ModuleConfigurationException("泛型声明身份环");
            var item = records[id];
            var symbol = symbols[id];
            var gps = symbol switch { TypeSymbol t => t.GenericParameters, MethodSymbol m => m.GenericParameters, _ => null };
            if (gps != null)
            {
                var declarations = item.GetProperty("gps").EnumerateArray().ToArray();
                if (symbol is TypeSymbol { IsBuiltin: true })
                {
                    if (gps.Count != declarations.Length) throw new ModuleConfigurationException("内建 GP 元数不符");
                    for (int i = 0; i < declarations.Length; i++)
                    {
                        var identity = declarations[i].GetProperty("identity");
                        if (Str(identity, "ownerId") != id || identity.GetProperty("index").GetInt32() != i)
                            throw new ModuleConfigurationException("内建 GP 固定 owner/index 身份不符");
                    }
                }
                else
                    for (int i = 0; i < declarations.Length; i++)
                    {
                        var gp = declarations[i]; var identity = gp.GetProperty("identity");
                        var ownerId = Str(identity, "ownerId")!; var index = identity.GetProperty("index").GetInt32();
                        if (ownerId == id)
                        {
                            if (index != i) throw new ModuleConfigurationException("GP 自声明 index 不符");
                            gps.Add(new GenericParameterSymbol(Str(gp, "name")!, Bool(gp, "variadic"), Bool(gp, "namedVariadic"), EnumValue<GenericVariance>(gp, "variance")));
                        }
                        else
                        {
                            BuildGenerics(ownerId);
                            var owner = symbols[ownerId];
                            var ownerGps = owner switch { TypeSymbol t => t.GenericParameters, MethodSymbol m => m.GenericParameters,
                                _ => throw new ModuleConfigurationException("借用 GP owner 不是类型/函数") };
                            if (index < 0 || index >= ownerGps.Count) throw new ModuleConfigurationException("借用 GP index 越界");
                            var borrowed = ownerGps[index];
                            if (borrowed.Name != Str(gp, "name") || borrowed.Variance != EnumValue<GenericVariance>(gp, "variance")
                                || borrowed.IsVariadic != Bool(gp, "variadic") || borrowed.IsNamedVariadic != Bool(gp, "namedVariadic"))
                                throw new ModuleConfigurationException("借用 GP flags 不符");
                            gps.Add(borrowed);
                        }
                    }
                for (int i = 0; i < gps.Count; i++)
                {
                    var identity = declarations[i].GetProperty("identity");
                    if (Str(identity, "ownerId") == id && (symbol is not TypeSymbol { IsBuiltin: true } || artifact.CompilerOwned))
                    {
                        gps[i].StableIdentity = id + "/gp/" + i;
                        gps[i].RequiresSharedSafe = Bool(declarations[i], "shared");
                    }
                }
            }
            if (symbol is MethodSymbol method)
                foreach (var parameter in item.GetProperty("parameters").EnumerateArray())
                    method.Parameters.Add(new ParameterSymbol(Str(parameter, "name")!, isVariadic: Bool(parameter, "variadic"),
                        isNamedVariadic: Bool(parameter, "namedVariadic")) { HasDefaultValue = Bool(parameter, "default") });
            genericsBuilding.Remove(id); genericsBuilt.Add(id);
        }
        foreach (var id in records.Keys) BuildGenerics(id);
        SemanticSymbol? Ref(JsonElement reference)
        {
            if (reference.ValueKind == JsonValueKind.Null) return null;
            if (reference.TryGetProperty("generic", out var gp))
            {
                CheckProperties(reference, "generic"); CheckProperties(gp, "ownerId", "index");
                var owner = Build(Str(gp, "ownerId")!);
                var parameters = owner switch { TypeSymbol t => t.GenericParameters, MethodSymbol m => m.GenericParameters,
                    _ => throw new ModuleConfigurationException("GP owner 不是声明类型/函数") };
                var index = gp.GetProperty("index").GetInt32();
                return index >= 0 && index < parameters.Count ? parameters[index] : throw new ModuleConfigurationException("GP index 越界");
            }
            CheckProperties(reference, "typeId", "args");
            var definition = Symbol<TypeSymbol>(Str(reference, "typeId")) ?? throw new ModuleConfigurationException("type ref 缺 ID");
            var args = reference.GetProperty("args").EnumerateArray().Select(v => Ref(v) ?? throw new ModuleConfigurationException("type arg 为空")).ToArray();
            if (args.Length == 0) return definition;
            if (args.Length != definition.GenericParameters.Count) throw new ModuleConfigurationException("构造类型元数不符");
            return graph.GetConstructedType(definition, args);
        }
        TypeSymbol? TypeRef(JsonElement reference) => Ref(reference) is { } symbol
            ? symbol as TypeSymbol ?? throw new ModuleConfigurationException("需要具体类型引用") : null;
        void Apps(JsonElement item, List<WrapperApplication> apps)
        {
            foreach (var app in item.GetProperty("apps").EnumerateArray())
                apps.Add(new WrapperApplication(TypeRef(app) ?? throw new ModuleConfigurationException("wrapper ref 为空"), null));
        }
        foreach (var (id, item) in records)
        {
            if (existing.Contains(id)) continue;
            var symbol = symbols[id];
            var gps = symbol switch { TypeSymbol t => t.GenericParameters, MethodSymbol m => m.GenericParameters, _ => null };
            if (gps != null)
            {
                if (symbol is TypeSymbol { IsBuiltin: true } && !artifact.CompilerOwned) continue;
                var declarations = item.GetProperty("gps").EnumerateArray().ToArray();
                for (int i = 0; i < gps.Count; i++)
                {
                    if (Str(declarations[i].GetProperty("identity"), "ownerId") != id) continue;
                    foreach (var c in declarations[i].GetProperty("constraints").EnumerateArray())
                        gps[i].Constraints.Add(new GenericConstraintInfo(EnumValue<GenericConstraintKind>(c, "kind"),
                            Ref(c.GetProperty("bound")) ?? throw new ModuleConfigurationException("constraint bound 为空")));
                }
            }
            switch (symbol)
            {
                case TypeSymbol t:
                    if (t.IsBuiltin && !artifact.CompilerOwned) break;
                    t.BaseType = TypeRef(item.GetProperty("base"));
                    t.Interfaces.AddRange(item.GetProperty("interfaces").EnumerateArray().Select(v => TypeRef(v)
                        ?? throw new ModuleConfigurationException("interface 引用不能为空")));
                    t.IsUnsafe = Bool(item, "unsafe"); t.IsOpen = Bool(item, "open"); t.IsAbstract = Bool(item, "abstract");
                    t.IsSingleton = Bool(item, "singleton"); t.LikeTarget = Str(item, "like");
                    t.WrapperTarget = item.GetProperty("wrapperTarget").ValueKind == JsonValueKind.Null ? null : EnumValue<WrapperTargetKind>(item, "wrapperTarget");
                    t.IsTerminal = Bool(item, "terminal"); t.IsInternal = Bool(item, "internal"); Apps(item, t.AppliedWrappers);
                    t.Fields.AddRange(item.GetProperty("fields").EnumerateArray().Select(v => Symbol<FieldSymbol>(v.GetString())
                        ?? throw new ModuleConfigurationException("field 引用不能为空")));
                    t.Methods.AddRange(item.GetProperty("methods").EnumerateArray().Select(v => Symbol<MethodSymbol>(v.GetString())
                        ?? throw new ModuleConfigurationException("method 引用不能为空")));
                    t.Cases.AddRange(item.GetProperty("cases").EnumerateArray().Select(v => Symbol<EnumCaseSymbol>(v.GetString())
                        ?? throw new ModuleConfigurationException("case 引用不能为空")));
                    if (t.Fields.Any(f => !ReferenceEquals(f.Owner, t)) || t.Methods.Any(m => !ReferenceEquals(m.Owner, t))
                        || t.Cases.Any(c => !ReferenceEquals(c.Owner, t)))
                        throw new ModuleConfigurationException("type 成员 owner 引用不一致");
                    if (!t.IsBuiltin && !t.IsCompilerLibrary && t.DeclarationSpan != null) graph.ImportedUserHosts.Add(t);
                    break;
                case MethodSymbol m:
                    m.ReturnType = Ref(item.GetProperty("return")); m.IsUnsafe = Bool(item, "unsafe");
                    m.IsSynthetic = Bool(item, "synthetic"); m.HasBody = Bool(item, "body"); m.IsOpen = Bool(item, "open");
                    m.IsAbstract = Bool(item, "abstract"); m.IsOverride = Bool(item, "override");
                    m.NativeSymbol = Str(item, "nativeSymbol"); m.NativeLibrary = Str(item, "nativeLibrary");
                    m.NativeBorrow = Bool(item, "nativeBorrow"); m.IsEntryPoint = Bool(item, "entry");
                    m.ProxyTemplate = item.GetProperty("proxy").ValueKind == JsonValueKind.Null ? null : EnumValue<ProxyTemplateKind>(item, "proxy");
                    m.IsCompanionInstance = Bool(item, "companionInstance"); Apps(item, m.AppliedWrappers);
                    var parameters = item.GetProperty("parameters").EnumerateArray().ToArray();
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        var p = m.Parameters[i]; var value = parameters[i]; p.Type = Ref(value.GetProperty("type"))
                            ?? throw new ModuleConfigurationException("parameter 类型不能为空");
                        p.DefaultFactory = Symbol<MethodSymbol>(Str(value, "factory")); p.MappedField = Symbol<FieldSymbol>(Str(value, "mappedField"));
                        p.StableIdentity = id + "/parameter/" + i;
                    }
                    break;
                case FieldSymbol f:
                    f.FieldType = Ref(item.GetProperty("type")) ?? throw new ModuleConfigurationException("field 类型不能为空"); f.Getter = Symbol<MethodSymbol>(Str(item, "getter"));
                    f.Setter = Symbol<MethodSymbol>(Str(item, "setter")); f.HasBackingStorage = Bool(item, "backing");
                    f.IsOpen = Bool(item, "open"); f.IsOverride = Bool(item, "override");
                    f.OverriddenField = Symbol<FieldSymbol>(Str(item, "overridden"));
                    f.CompanionCellField = Symbol<FieldSymbol>(Str(item, "companionCell")); Apps(item, f.AppliedWrappers);
                    break;
                case EnumCaseSymbol c:
                    c.Discriminant = item.GetProperty("discriminant").ValueKind == JsonValueKind.Null ? null : item.GetProperty("discriminant").GetDecimal();
                    c.ResolvedInit = Symbol<MethodSymbol>(Str(item, "init")); c.CaseFactory = Symbol<MethodSymbol>(Str(item, "factory"));
                    c.FixedArgumentFactories = item.GetProperty("fixedFactories").ValueKind == JsonValueKind.Null ? null
                        : item.GetProperty("fixedFactories").EnumerateArray().Select(v => Symbol<MethodSymbol>(v.GetString())).ToArray();
                    if (item.GetProperty("holes").ValueKind != JsonValueKind.Null)
                        c.HoleParameters = item.GetProperty("holes").EnumerateArray().Select(v => new EnumCaseHoleParameter(Str(v, "name")!,
                            Ref(v.GetProperty("type")) ?? throw new ModuleConfigurationException("hole type 为空"), v.GetProperty("index").GetInt32())).ToList();
                    break;
            }
        }
        foreach (var symbol in symbols.Values)
        {
            if (symbol is MethodSymbol method)
                foreach (var p in method.Parameters.Where(p => p.HasDefaultValue))
                    if (p.DefaultFactory is not { } factory || factory.Kind != MethodKind.Regular || factory.Owner != null
                        || factory.IsNative || factory.IsAsync || !factory.HasBody || !factory.IsSynthetic || factory.Parameters.Count != 0
                        || !ReferenceEquals(factory.ReturnType, p.Type) || factory.OriginModuleId != method.OriginModuleId
                        || !factory.GenericParameters.SequenceEqual((method.IsStatic ? [] : method.Owner?.GenericParameters ?? []).Concat(method.GenericParameters).Distinct()))
                        throw new ModuleConfigurationException("默认参数 factory ABI/来源不符: " + CanonicalSymbolPrinter.PrintMethod(method) + "/" + p.Name);
            if (symbol is EnumCaseSymbol item && (item.CaseFactory is not { } caseFactory || caseFactory.Owner != null
                || caseFactory.IsNative || caseFactory.IsAsync || !caseFactory.HasBody || !caseFactory.IsSynthetic
                || !ReferenceEquals(caseFactory.ReturnType, item.Owner) || caseFactory.OriginModuleId != item.OriginModuleId
                || caseFactory.GenericParameters.Count != 0 || item.HoleParameters == null
                || item.HoleParameters.Count == 0 && (caseFactory.Parameters.Count != 0 || item.FixedArgumentFactories != null)
                || item.HoleParameters.Count != 0 && (item.ResolvedInit == null || item.FixedArgumentFactories == null
                    || caseFactory.Parameters.Count != item.ResolvedInit.Parameters.Count || item.FixedArgumentFactories.Count != caseFactory.Parameters.Count
                    || caseFactory.Parameters.Where((p, i) => p.Name != item.ResolvedInit.Parameters[i].Name || !ReferenceEquals(p.Type, item.ResolvedInit.Parameters[i].Type)).Any())))
                throw new ModuleConfigurationException("enum case factory ABI/来源不符");
            if (symbol is EnumCaseSymbol { HoleParameters.Count: > 0, FixedArgumentFactories: { } fixedFactories } c)
            {
                // 洞接口是构造参数的有序子集；不可凭 fixed helper 的空槽自授另一类型或名字。
                int previousIndex = -1;
                foreach (var hole in c.HoleParameters)
                {
                    if (hole.InitParameterIndex <= previousIndex || hole.InitParameterIndex < 0
                        || hole.InitParameterIndex >= c.ResolvedInit!.Parameters.Count)
                        throw new ModuleConfigurationException("enum hole index 必须在 init 范围内且唯一有序");
                    var parameter = c.ResolvedInit.Parameters[hole.InitParameterIndex];
                    if (hole.Name != parameter.Name || !ReferenceEquals(hole.Type, parameter.Type))
                        throw new ModuleConfigurationException("enum hole 名称/类型与 init 不符");
                    previousIndex = hole.InitParameterIndex;
                }
                for (int i = 0; i < fixedFactories.Count; i++)
                {
                    var hole = c.HoleParameters.Any(h => h.InitParameterIndex == i);
                    var helper = fixedFactories[i];
                    if (hole ? helper != null : helper == null || helper.Owner != null || helper.IsNative || helper.IsAsync
                        || !helper.IsSynthetic || !helper.HasBody || helper.GenericParameters.Count != 0 || helper.Parameters.Count != 0
                        || helper.OriginModuleId != c.OriginModuleId || !ReferenceEquals(helper.ReturnType, c.ResolvedInit!.Parameters[i].Type))
                        throw new ModuleConfigurationException("enum 固定位置 factory ABI/来源不符");
                }
            }
        }
        // 查找容器不挂 link-only 顶层定义；引用图和 BIL 完整保留实现依赖。
        foreach (var (id, item) in records)
        {
            if (existing.Contains(id)) continue;
            var s = symbols[id];
            switch (s)
            {
                case TypeSymbol { IsBuiltin: false } t when !t.IsLinkOnly:
                    var types = t.DeclaringType?.NestedTypes ?? t.Namespace!.Types;
                    if (types.Any(other => other.Name == t.Name && other.GenericParameters.Count == t.GenericParameters.Count))
                        throw new ModuleConfigurationException("重复公开类型：" + CanonicalSymbolPrinter.PrintType(t));
                    types.Add(t); break;
                case MethodSymbol { Owner: null, Namespace: { } ns } m when !m.IsLinkOnly:
                    if (ns.Methods.Any(other => CanonicalSymbolPrinter.PrintMethod(other) == CanonicalSymbolPrinter.PrintMethod(m)))
                        throw new ModuleConfigurationException("重复公开函数：" + m.Name);
                    ns.Methods.Add(m); break;
                case FieldSymbol { Owner: null, Namespace: { } ns } f when !f.IsLinkOnly:
                    if (ns.Fields.Any(other => other.Name == f.Name)) throw new ModuleConfigurationException("重复公开字段：" + f.Name);
                    ns.Fields.Add(f); break;
            }
            graph.ImportedSymbols.TryAdd(id, s); graph.ImportedDeclarations.TryAdd(id, item.GetRawText());
        }
        foreach (var closed in payload.GetProperty("closedTypes").EnumerateArray()) Ref(closed);
        graph.BackfillConstructedBaseTypes();
        if (artifact.CompilerOwned)
        {
            ValidateTrustedGraph(graph, artifact.ReadBil());
            ModuleLateHelpers.RegisterTrusted(graph, artifact.ReadBil());
            graph.Bootstrap.RefreshCallWildcard();
        }
        if (graph.ImportedArtifacts.Any(a => a.ModuleId == artifact.ModuleId)) throw new ModuleConfigurationException("重复接口导入模块");
        graph.ImportedArtifacts.Add(artifact);
    }
    private static bool IsCompilerBinding(BilMetadataEntry entry) => entry.Key.StartsWith(BilCompilerHelpers.MetadataPrefix, StringComparison.Ordinal)
        || entry.Key.StartsWith(BilCompilerSymbols.MetadataPrefix, StringComparison.Ordinal);
    private static T EnumValue<T>(JsonElement item, string key) where T : struct, Enum => EnumNumber<T>(item.GetProperty(key).GetInt32());
    private static T EnumNumber<T>(int value) where T : struct, Enum => Enum.IsDefined(typeof(T), value)
        ? (T)Enum.ToObject(typeof(T), value) : throw new ModuleConfigurationException("接口枚举值无效");
    private static void Common(SemanticSymbol symbol, JsonElement item, string id, bool trusted)
    {
        symbol.StableIdentity = id; symbol.Accessibility = EnumValue<Accessibility>(item, "access");
        symbol.OriginModuleId = Str(item, "module"); symbol.OriginFileId = Str(item, "file");
        symbol.IsImported = true; symbol.IsLinkOnly = symbol.Accessibility is Accessibility.Private or Accessibility.Internal;
        symbol.IsCompilerLibrary = trusted && Bool(item, "compilerLibrary");
        var span = item.GetProperty("span");
        if (span.ValueKind == JsonValueKind.Null) return;
        CheckProperties(span, "source", "start", "end");
        CharPosition Position(JsonElement p)
        {
            var values = p.EnumerateArray().ToArray();
            if (values.Length != 3) throw new ModuleConfigurationException("接口 Span 维度无效");
            return new() { line = values[0].GetInt64(), column = values[1].GetInt32(), offset = values[2].GetInt64() };
        }
        symbol.DeclarationSpan = new CharRange { sourceName = Str(span, "source")!, Start = Position(span.GetProperty("start")), End = Position(span.GetProperty("end")) };
    }
    private static void ValidateBuiltin(JsonElement item, TypeSymbol expected, string id)
    {
        if (id != expected.StableIdentity || Str(item, "namespace") != "core" || Str(item, "owner") != null
            || item.GetProperty("kind").GetInt32() != (int)expected.Kind || Bool(item, "rich") != expected.IsRich
            || Bool(item, "shared") != expected.IsShared || Bool(item, "valueBranch") != expected.IsValueTypeBranch
            || Bool(item, "sharedFromArgument") != expected.DerivesSharedSafetyFromTypeArgument
            || Str(item, "alias") != expected.BilAlias || Str(item, "constructor") != expected.BilStandardConstructor
            || !item.GetProperty("intrinsics").EnumerateArray().Select(v => v.GetInt32()).SequenceEqual(expected.IntrinsicOps.OrderBy(v => (int)v).Select(v => (int)v)))
            throw new ModuleConfigurationException("内建接口固定 ABI 不符：" + expected.Name);
        var gps = item.GetProperty("gps").EnumerateArray().ToArray();
        if (gps.Length != expected.GenericParameters.Count) throw new ModuleConfigurationException("内建接口 GP 元数不符：" + expected.Name);
        for (int i = 0; i < gps.Length; i++)
            if (Str(gps[i], "name") != expected.GenericParameters[i].Name || Bool(gps[i], "variadic") || Bool(gps[i], "namedVariadic")
                || Bool(gps[i], "shared") != expected.GenericParameters[i].RequiresSharedSafe
                || gps[i].GetProperty("variance").GetInt32() != (int)expected.GenericParameters[i].Variance)
                throw new ModuleConfigurationException("内建接口 GP 形状不符：" + expected.Name);
    }
    private static void ValidateTrustedGraph(SymbolGraph graph, BilModule bil)
    {
        var fixedGraph = SymbolGraph.CreateArtifactOnly("manifest-validation");
        foreach (var t in graph.Bootstrap.SourceTypes.Values)
        {
            var expected = fixedGraph.Bootstrap.SourceTypes[t.Name];
            if (t.BaseType?.Name != expected.BaseType?.Name || t.BaseType?.IsBuiltin != expected.BaseType?.IsBuiltin
                || t.Accessibility != Accessibility.Public || t.GenericParameters.Any(g => g.RequiresSharedSafe))
                throw new ModuleConfigurationException("可信内建类型继承/访问/共享 GP ABI 不符");
            // P3 为 Serializable 添加编译器接口；它不是用户源新增的物理布局。
            if (t.IsUnsafe || t.IsSingleton || t.Interfaces.Any(i => i.Name != BilSpellings.SerializableIfaceName
                    || i.Namespace?.FullName != "core.serialization" || !i.IsCompilerLibrary)
                || t.Cases.Count != 0 || t.NestedTypes.Count != 0 || t.IsAbstract || t.LikeTarget != null
                || t.WrapperTarget != null || t.IsTerminal || t.IsInternal || t.IsOpen != (t.Name == "Object"))
                throw new ModuleConfigurationException("可信内建类型布局越权：" + t.Name);
            foreach (var f in t.Fields.Where(f => !f.IsStatic && f.ExtTargetPath == null))
                if (t.Name is not ("Span" or "SharedSpan" or "Array") || f.Name != "length" || !f.IsConst
                    || !ReferenceEquals(f.FieldType, graph.Bootstrap.Int32) || f.Getter != null || f.Setter != null)
                    throw new ModuleConfigurationException("可信内建类型实例存储越权");
            for (int i = 0; i < t.GenericParameters.Count; i++)
            {
                var gp = t.GenericParameters[i]; var constrained = t.Name is "Span" or "SharedSpan" or "Box";
                if (constrained ? gp.Constraints.Count != 1 || gp.Constraints[0].Kind != GenericConstraintKind.Extends
                    || !ReferenceEquals(gp.Constraints[0].Bound, graph.Bootstrap.ValueType) : gp.Constraints.Count != 0)
                    throw new ModuleConfigurationException("可信内建 GP 固定约束不符：" + t.Name);
            }
            foreach (var app in t.AppliedWrappers)
            {
                var marker = app.WrapperDefinition;
                if (marker.Kind != TypeKind.Wrapper || marker.IsRich || marker.Fields.Any(f => !f.IsStatic)
                    || marker.AppliedWrappers.Count != 0 || marker.Methods.Any(m => m.ProxyTemplate != null || m.AppliedWrappers.Count != 0))
                    throw new ModuleConfigurationException("可信内建 wrapper 不是纯 marker");
                foreach (var init in marker.Methods.Where(m => m.Kind == MethodKind.Init))
                {
                    var body = bil.Functions.SingleOrDefault(f => f.Symbol == CanonicalSymbolPrinter.PrintMethod(init));
                    if (init.Parameters.Count != 0 || init.IsNative || init.IsAsync || body == null
                        || body.Blocks.SelectMany(b => b.Instructions).Any(i => i is not RetInstruction { Value: null }))
                        throw new ModuleConfigurationException("可信内建 marker ctor 不是空零参 ABI");
                }
            }
        }
        var wildcard = graph.Bootstrap.Any.Methods.SingleOrDefault(m => m.Name == "call???");
        if (wildcard == null || wildcard.Parameters.Count != 3 || !ReferenceEquals(wildcard.ReturnType, graph.Bootstrap.Any)
            || !ReferenceEquals(wildcard.Parameters[0].Type, graph.Bootstrap.String) || wildcard.GenericParameters.Count != 0
            || wildcard.Kind != MethodKind.Regular || wildcard.IsStatic || wildcard.IsNative || wildcard.IsAsync
            || wildcard.Accessibility != Accessibility.Public || wildcard.Parameters.Any(p => p.HasDefaultValue || p.IsVariadic || p.IsNamedVariadic)
            || !ReferenceEquals(wildcard.Parameters[1].Type, BootstrapSymbols.NamedPackType(graph))
            || !ReferenceEquals(wildcard.Parameters[2].Type, graph.GetConstructedType(graph.Bootstrap.ArrayDefinition, graph.Bootstrap.Any))
            || !wildcard.Parameters.Select(p => p.Name).SequenceEqual(["symbol", "namedArgs", "unnamedArgs"]))
            throw new ModuleConfigurationException("可信 Any.call??? 固定 ABI 缺失");
        if (BilCompilerHelpers.Resolve(bil, "any_hash") == null || BilCompilerHelpers.Resolve(bil, "any_to_string") == null)
            throw new ModuleConfigurationException("可信 std 内建 native helper ABI 缺失");
        ValidateSerializableInterface(graph, bil);
        // 重用 P4 声明投影与 linker 的 typed ABI 规则，避免 DTO 签名和 BIL 各自合法却彼此不符。
        var members = bil.LocalSymbols.Concat(bil.ExternalSymbols).SelectMany(d => d is BilTypeDeclaration t
            ? t.Members.OfType<BilSimpleMemberDeclaration>() : d is BilSimpleMemberDeclaration m ? [m] : [])
            .GroupBy(m => m.Symbol).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        foreach (var method in graph.Bootstrap.SourceTypes.Values.SelectMany(t => t.Methods))
        {
            var projected = LocalSymbolEmitters.EmitSyntheticMethodDeclaration(method);
            if (!members.TryGetValue(projected.Symbol, out var actual))
                throw new ModuleConfigurationException("可信内建接口成员缺 BIL 声明：" + projected.Symbol);
            foreach (var declaration in actual) BilModuleLinker.Compatible(projected, declaration, bil.Resources);
        }
        foreach (var name in new[] { "any_hash", "any_to_string" })
        {
            var helper = graph.ImportedSymbols.Values.OfType<MethodSymbol>().SingleOrDefault(m => m.Name == name
                && m.Owner == null && m.Namespace?.FullName == "core" && m.IsCompilerLibrary);
            if (helper == null || CanonicalSymbolPrinter.PrintMethod(helper) != BilCompilerHelpers.Resolve(bil, name)
                || !helper.IsNative || helper.IsAsync || helper.GenericParameters.Count != 0
                || helper.Accessibility != Accessibility.Private)
                throw new ModuleConfigurationException("可信 native helper DTO 契约不符");
            var projected = LocalSymbolEmitters.EmitSyntheticMethodDeclaration(helper);
            foreach (var declaration in members[projected.Symbol]) BilModuleLinker.Compatible(projected, declaration, bil.Resources);
        }
    }
    private static void ValidateSerializableInterface(SymbolGraph graph, BilModule bil)
    {
        var ns = graph.GetNamespace(["core", "serialization"]);
        var iface = ns.Types.SingleOrDefault(t => t.Name == BilSpellings.SerializableIfaceName);
        var parcel = ns.Types.SingleOrDefault(t => t.Name == "Parcel");
        var context = graph.ImportedSymbols.Values.OfType<TypeSymbol>().SingleOrDefault(t => t.Name == "SerializationGraphContext"
            && t.Namespace?.FullName == ns.FullName && t.IsCompilerLibrary);
        if (iface == null || parcel == null || context == null || !iface.IsCompilerLibrary || iface.Kind != TypeKind.Interface
            || iface.Accessibility != Accessibility.Public || !iface.IsAbstract || iface.IsOpen || iface.IsUnsafe
            || iface.IsRich || iface.IsShared || iface.IsSingleton || iface.BaseType != null || iface.Interfaces.Count != 0
            || iface.GenericParameters.Count != 0 || iface.Fields.Count != 0 || iface.Cases.Count != 0
            || iface.NestedTypes.Count != 0 || iface.AppliedWrappers.Count != 0 || iface.Methods.Count != 4)
            throw new ModuleConfigurationException("可信 Serializable 接口固定布局不符");
        (string Name, SemanticSymbol? Return, (string Name, SemanticSymbol Type)[] Parameters)[] shapes =
        [
            (BilSpellings.ToParcelMethodName, parcel, [("loopedRefEnabled", graph.Bootstrap.Bool)]),
            (BilSpellings.FromParcelMethodName, null, [("parcel", parcel), ("loopedRefEnabled", graph.Bootstrap.Bool)]),
            (BilSpellings.EncodeGraphMethodName, parcel, [("context", context)]),
            (BilSpellings.DecodeGraphMethodName, graph.Bootstrap.Any, [("parcel", parcel), ("context", context), ("nodeId", graph.Bootstrap.Int64)])
        ];
        foreach (var shape in shapes)
        {
            var m = iface.Methods.SingleOrDefault(m => m.Name == shape.Name);
            if (m == null || !ReferenceEquals(m.Owner, iface) || m.Kind != MethodKind.Regular || m.IsStatic || m.IsNative
                || m.IsAsync || m.IsUnsafe || !m.IsAbstract || !m.IsSynthetic || m.HasBody || m.IsOpen || m.IsOverride
                || m.Accessibility != Accessibility.Public || m.GenericParameters.Count != 0 || m.AppliedWrappers.Count != 0
                || !ReferenceEquals(m.ReturnType, shape.Return) || m.Parameters.Count != shape.Parameters.Length
                || m.Parameters.Where((p, i) => p.Name != shape.Parameters[i].Name || !ReferenceEquals(p.Type, shape.Parameters[i].Type)
                    || p.HasDefaultValue != (p.Name == "loopedRefEnabled") || p.IsVariadic || p.IsNamedVariadic).Any())
                throw new ModuleConfigurationException("可信 Serializable 接口方法固定 ABI 不符：" + shape.Name);
        }
        var declaration = LocalSymbolEmitters.EmitSyntheticTypeDeclaration(iface,
            new EmitEnvironment(new CompilationUnit(graph, Array.Empty<RootASTNode>()), "interface-validation"));
        var actual = bil.LocalSymbols.OfType<BilTypeDeclaration>().SingleOrDefault(t => t.Symbol == declaration.Symbol)
            ?? throw new ModuleConfigurationException("可信 Serializable 接口缺 BIL 定义");
        BilModuleLinker.Compatible(declaration, actual, bil.Resources);
    }
}
