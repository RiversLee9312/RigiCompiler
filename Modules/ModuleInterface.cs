using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RigiCompiler.Bil;

namespace RigiCompiler.Modules;

/// <summary>已校验的 BIL 与无 AST 接口对；信任由编译器 resolver 持有，不来自 JSON。</summary>
internal sealed record ModuleArtifact(string ModuleId, string InputDigest, string ApiHash,
    byte[] InterfaceBytes, byte[] BilBytes, bool CompilerOwned = false)
{
    internal BilModule ReadBil() => BilReader.Read(Encoding.UTF8.GetString(BilBytes));
}

/// <summary>版本化手工 JSON 协议。开放泛型引用以声明 ID 和 owner/index 表达。</summary>
internal static class ModuleInterface
{
    internal const string CompilerAbi = "rigi-module-abi-1";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static byte[] Bytes(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) node.WriteTo(writer);
        return stream.ToArray();
    }
    internal static ModuleArtifact Export(CompilationUnit unit, IReadOnlyList<BoundFunctionBody> bodies,
        BilModule bil, string inputDigest, bool compilerOwned = false)
    {
        var registry = new ModuleSymbolRegistry(unit.ModuleIdentity);
        registry.Collect(unit, bodies);
        if (compilerOwned)
        {
            if (unit.SourceFiles.Any(f => !f.IsCompilerLibrary)) throw new ModuleConfigurationException("用户源不能生成 compiler-owned artifact");
            // 合成接口/codec 也属于本标准库 provider，不成为用户反射候选。
            foreach (var symbol in registry.Ids.Keys.Where(s => !s.IsImported)) symbol.IsCompilerLibrary = true;
            foreach (var symbol in registry.Ids.Keys.Where(s => s is TypeSymbol or FieldSymbol or MethodSymbol))
                if (symbol.IsCompilerLibrary && !symbol.IsImported)
                    BilCompilerSymbols.Register(bil, CanonicalSymbolPrinter.Print(symbol));
        }
        JsonNode? Ref(SemanticSymbol? symbol)
        {
            if (symbol == null) return null;
            if (symbol is GenericParameterSymbol gp)
            {
                if (!registry.Generics.TryGetValue(gp, out var identity)) throw new ModuleConfigurationException("泛型引用缺声明 owner");
                return new JsonObject { ["generic"] = new JsonObject { ["ownerId"] = identity.Owner, ["index"] = identity.Index } };
            }
            if (symbol is not TypeSymbol type) throw new ModuleConfigurationException("接口类型引用不是类型或泛型参数");
            return new JsonObject { ["typeId"] = registry.Id(type.ConstructedFrom ?? type),
                ["args"] = new JsonArray((type.TypeArguments ?? []).Select(Ref).ToArray()) };
        }
        JsonArray Apps(IEnumerable<WrapperApplication> apps) => new(apps.Select(a => Ref(a.Wrapper)).ToArray());
        JsonArray Gps(IEnumerable<GenericParameterSymbol> parameters) => new(parameters.Select(gp => (JsonNode)new JsonObject
        {
            ["name"] = gp.Name, ["variance"] = (int)gp.Variance, ["variadic"] = gp.IsVariadic,
            ["identity"] = new JsonObject { ["ownerId"] = registry.Generics[gp].Owner, ["index"] = registry.Generics[gp].Index },
            ["namedVariadic"] = gp.IsNamedVariadic, ["shared"] = gp.RequiresSharedSafe,
            ["constraints"] = new JsonArray(gp.Constraints.Select(c => (JsonNode)new JsonObject { ["kind"] = (int)c.Kind, ["bound"] = Ref(c.Bound) }).ToArray())
        }).ToArray());
        JsonNode? Span(CharRange? span) => span is not { } s ? null : new JsonObject
        {
            ["source"] = s.sourceName, ["start"] = new JsonArray(s.Start.line, s.Start.column, s.Start.offset),
            ["end"] = new JsonArray(s.End.line, s.End.column, s.End.offset)
        };
        string? Id(SemanticSymbol? s) => s == null ? null : registry.Id(s);
        var declarations = new JsonArray();
        foreach (var (symbol, id) in registry.Ids.OrderBy(p => p.Value, StringComparer.Ordinal))
        {
            if (symbol is GenericParameterSymbol or ParameterSymbol) continue;
            var item = new JsonObject
            {
                ["id"] = id, ["name"] = symbol.Name, ["access"] = (int)symbol.Accessibility,
                ["module"] = symbol.OriginModuleId, ["file"] = symbol.OriginFileId,
                ["compilerLibrary"] = symbol.IsCompilerLibrary, ["span"] = Span(symbol.DeclarationSpan)
            };
            switch (symbol)
            {
                case TypeSymbol t:
                    item["tag"] = "type"; item["kind"] = (int)t.Kind; item["namespace"] = t.Namespace?.FullName;
                    item["owner"] = Id(t.DeclaringType); item["rich"] = t.IsRich; item["shared"] = t.IsShared;
                    item["builtin"] = t.IsBuiltin; item["valueBranch"] = t.IsValueTypeBranch;
                    item["sharedFromArgument"] = t.DerivesSharedSafetyFromTypeArgument;
                    item["alias"] = t.BilAlias; item["constructor"] = t.BilStandardConstructor;
                    item["intrinsics"] = new JsonArray(t.IntrinsicOps.OrderBy(op => (int)op).Select(op => JsonValue.Create((int)op)).ToArray());
                    item["base"] = Ref(t.BaseType); item["interfaces"] = new JsonArray(t.Interfaces.Select(Ref).ToArray());
                    item["gps"] = Gps(t.GenericParameters); item["unsafe"] = t.IsUnsafe; item["open"] = t.IsOpen;
                    item["abstract"] = t.IsAbstract; item["singleton"] = t.IsSingleton; item["like"] = t.LikeTarget;
                    item["wrapperTarget"] = t.WrapperTarget is { } target ? JsonValue.Create((int)target) : null;
                    item["terminal"] = t.IsTerminal; item["internal"] = t.IsInternal; item["apps"] = Apps(t.AppliedWrappers);
                    item["fields"] = new JsonArray(t.Fields.Select(f => JsonValue.Create(Id(f))).ToArray());
                    item["methods"] = new JsonArray(t.Methods.Select(m => JsonValue.Create(Id(m))).ToArray());
                    item["cases"] = new JsonArray(t.Cases.Select(c => JsonValue.Create(Id(c))).ToArray());
                    break;
                case MethodSymbol m:
                    item["tag"] = "method"; item["kind"] = (int)m.Kind; item["owner"] = Id(m.Owner);
                    item["namespace"] = m.Namespace?.FullName; item["static"] = m.IsStatic; item["native"] = m.IsNative;
                    item["async"] = m.IsAsync; item["ext"] = m.ExtTargetPath; item["return"] = Ref(m.ReturnType);
                    item["gps"] = Gps(m.GenericParameters); item["unsafe"] = m.IsUnsafe; item["synthetic"] = m.IsSynthetic;
                    item["body"] = m.HasBody; item["open"] = m.IsOpen; item["abstract"] = m.IsAbstract;
                    item["override"] = m.IsOverride; item["nativeSymbol"] = m.NativeSymbol; item["nativeLibrary"] = m.NativeLibrary;
                    item["nativeBorrow"] = m.NativeBorrow; item["entry"] = m.IsEntryPoint;
                    item["proxy"] = m.ProxyTemplate is { } proxy ? JsonValue.Create((int)proxy) : null;
                    item["companionInstance"] = m.IsCompanionInstance; item["apps"] = Apps(m.AppliedWrappers);
                    item["parameters"] = new JsonArray(m.Parameters.Select(p => (JsonNode)new JsonObject
                    {
                        ["name"] = p.Name, ["type"] = Ref(p.Type), ["variadic"] = p.IsVariadic,
                        ["namedVariadic"] = p.IsNamedVariadic, ["default"] = p.HasDefaultValue,
                        ["factory"] = Id(p.DefaultFactory), ["mappedField"] = Id(p.MappedField)
                    }).ToArray());
                    break;
                case FieldSymbol f:
                    item["tag"] = "field"; item["owner"] = Id(f.Owner); item["namespace"] = f.Namespace?.FullName;
                    item["static"] = f.IsStatic; item["const"] = f.IsConst; item["ext"] = f.ExtTargetPath;
                    item["type"] = Ref(f.FieldType); item["getter"] = Id(f.Getter); item["setter"] = Id(f.Setter);
                    item["backing"] = f.HasBackingStorage; item["open"] = f.IsOpen; item["override"] = f.IsOverride;
                    item["overridden"] = Id(f.OverriddenField); item["companionCell"] = Id(f.CompanionCellField);
                    item["apps"] = Apps(f.AppliedWrappers);
                    break;
                case EnumCaseSymbol c:
                    item["tag"] = "case"; item["owner"] = Id(c.Owner); item["discriminant"] = c.Discriminant;
                    item["init"] = Id(c.ResolvedInit); item["factory"] = Id(c.CaseFactory);
                    item["fixedFactories"] = c.FixedArgumentFactories == null ? null : new JsonArray(c.FixedArgumentFactories.Select(f => JsonValue.Create(Id(f))).ToArray());
                    item["holes"] = c.HoleParameters == null ? null : new JsonArray(c.HoleParameters.Select(p => (JsonNode)new JsonObject
                        { ["name"] = p.Name, ["type"] = Ref(p.Type), ["index"] = p.InitParameterIndex }).ToArray());
                    break;
                default: throw new ModuleConfigurationException("未知接口声明类型");
            }
            declarations.Add((JsonNode)item);
        }
        bool Closed(SemanticSymbol symbol) => symbol is TypeSymbol t && (t.ConstructedFrom == null
            ? t.GenericParameters.Count == 0 : t.TypeArguments!.All(Closed));
        var payload = new JsonObject { ["declarations"] = declarations,
            ["closedTypes"] = new JsonArray(unit.Symbols.ConstructedTypeSnapshot().Where(Closed).Select(Ref).ToArray()) };
        var bilBytes = Encoding.UTF8.GetBytes(BilWriter.Write(bil));
        var apiHash = Hash(Bytes(payload));
        var document = new JsonObject { ["schema"] = 1, ["compilerAbi"] = CompilerAbi,
            ["moduleId"] = unit.ModuleIdentity, ["inputDigest"] = inputDigest,
            ["bilDigest"] = Hash(bilBytes), ["apiHash"] = apiHash, ["payload"] = payload };
        return new(unit.ModuleIdentity, inputDigest, apiHash, Bytes(document), bilBytes, compilerOwned);
    }

    internal static JsonDocument Validate(ModuleArtifact artifact)
    {
        try
        {
            var json = JsonDocument.Parse(artifact.InterfaceBytes, new JsonDocumentOptions { MaxDepth = 128 });
            var root = json.RootElement;
            CheckProperties(root, "schema", "compilerAbi", "moduleId", "inputDigest", "bilDigest", "apiHash", "payload");
            CheckNoDuplicates(root);
            var payload = root.GetProperty("payload"); CheckProperties(payload, "declarations", "closedTypes");
            foreach (var declaration in payload.GetProperty("declarations").EnumerateArray()) CheckDeclaration(declaration);
            if (root.GetProperty("schema").GetInt32() != 1 || Str(root, "compilerAbi") != CompilerAbi
                || Str(root, "moduleId") != artifact.ModuleId || Str(root, "inputDigest") != artifact.InputDigest
                || Str(root, "bilDigest") != Hash(artifact.BilBytes) || Str(root, "apiHash") != artifact.ApiHash
                || Hash(Encoding.UTF8.GetBytes(root.GetProperty("payload").GetRawText())) != artifact.ApiHash)
                throw new ModuleConfigurationException("接口 schema/compiler ABI/module/input/BIL/API 绑定不符");
            return json;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new ModuleConfigurationException("模块接口格式无效：" + ex.Message); }
    }
    internal static string? Str(JsonElement e, string key) => e.GetProperty(key).ValueKind == JsonValueKind.Null ? null : e.GetProperty(key).GetString();
    internal static bool Bool(JsonElement e, string key) => e.GetProperty(key).GetBoolean();
    internal static void CheckProperties(JsonElement e, params string[] fields)
    {
        var expected = fields.ToHashSet(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject()) if (!expected.Remove(p.Name)) throw new ModuleConfigurationException("接口未知或重复字段：" + p.Name);
        if (expected.Count != 0) throw new ModuleConfigurationException("接口缺字段：" + expected.Order().First());
    }
    private static void CheckDeclaration(JsonElement item)
    {
        string[] common = ["id", "name", "access", "module", "file", "compilerLibrary", "span", "tag"];
        string[] fields = Str(item, "tag") switch
        {
            "type" => ["kind", "namespace", "owner", "rich", "shared", "builtin", "valueBranch", "sharedFromArgument", "alias", "constructor", "intrinsics", "base", "interfaces", "gps", "unsafe", "open", "abstract", "singleton", "like", "wrapperTarget", "terminal", "internal", "apps", "fields", "methods", "cases"],
            "method" => ["kind", "owner", "namespace", "static", "native", "async", "ext", "return", "gps", "unsafe", "synthetic", "body", "open", "abstract", "override", "nativeSymbol", "nativeLibrary", "nativeBorrow", "entry", "proxy", "companionInstance", "apps", "parameters"],
            "field" => ["owner", "namespace", "static", "const", "ext", "type", "getter", "setter", "backing", "open", "override", "overridden", "companionCell", "apps"],
            "case" => ["owner", "discriminant", "init", "factory", "fixedFactories", "holes"],
            _ => throw new ModuleConfigurationException("接口声明 tag 无效")
        };
        CheckProperties(item, common.Concat(fields).ToArray());
        if (item.TryGetProperty("gps", out var gps))
            foreach (var gp in gps.EnumerateArray())
            {
                CheckProperties(gp, "name", "variance", "variadic", "identity", "namedVariadic", "shared", "constraints");
                CheckProperties(gp.GetProperty("identity"), "ownerId", "index");
                foreach (var c in gp.GetProperty("constraints").EnumerateArray()) CheckProperties(c, "kind", "bound");
            }
        if (item.TryGetProperty("parameters", out var parameters))
            foreach (var p in parameters.EnumerateArray()) CheckProperties(p, "name", "type", "variadic", "namedVariadic", "default", "factory", "mappedField");
        if (item.TryGetProperty("holes", out var holes) && holes.ValueKind != JsonValueKind.Null)
            foreach (var p in holes.EnumerateArray()) CheckProperties(p, "name", "type", "index");
    }
    private static void CheckNoDuplicates(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject()) { if (!keys.Add(p.Name)) throw new ModuleConfigurationException("接口重复 JSON key"); CheckNoDuplicates(p.Value); }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var v in e.EnumerateArray()) CheckNoDuplicates(v);
    }
}
