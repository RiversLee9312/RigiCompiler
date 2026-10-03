using RigiCompiler.Bil;

namespace RigiCompiler.Modules;

/// <summary>最终应用可替换的可信标准库函数集合；普通同名声明没有此权限。</summary>
internal static class ModuleLateHelpers
{
    private static readonly string[] Names = ["decodeAnyValue", "sbKind", "sbLength", "sbElementAt", "sbKeyAt", "sbValueAt",
        "sbTypeName", "sbBuildArray", "sbBuildList", "sbBuildMap", "typeNameOf", "isSerializable", "fieldsOf", "casesOf"];
    internal static TypeSymbol Infrastructure(SymbolGraph graph, string name, TypeSymbol? declaringType = null) => graph.GetNamespace(["core", "serialization"]).Types
        .Concat(graph.ImportedSymbols.Values.OfType<TypeSymbol>()).Distinct().Single(t => t.Name == name
            && t.Namespace?.FullName == "core.serialization" && t.IsCompilerLibrary && ReferenceEquals(t.DeclaringType, declaringType));
    internal static MethodSymbol Decoder(SymbolGraph graph) => graph.GetNamespace(["core", "serialization"]).Methods
        .Where(m => m.Name == "decodeAnyValue" && !m.IsImported && m.IsCompilerLibrary)
        .Concat(graph.ApprovedLateHelpers.Where(m => m.Name == "decodeAnyValue")).Single();
    internal static IReadOnlyList<MethodSymbol> Select(BindEnvironment env, string name)
    {
        var own = env.Unit.Symbols.GetNamespace(["core", "serialization"]).Methods
            .Where(m => m.Name == name && !m.IsImported && m.IsCompilerLibrary).ToArray();
        if (own.Length != 0) return own;
        if (!env.Unit.IsFinalModuleApplication) return [];
        var imported = env.Unit.Symbols.ApprovedLateHelpers.Where(m => m.Name == name).ToArray();
        foreach (var method in imported) env.Unit.Symbols.LateHelperOverrides.Add(CanonicalSymbolPrinter.PrintMethod(method));
        return imported;
    }
    internal static void RegisterTrusted(SymbolGraph graph, BilModule bil)
    {
        var ns = graph.GetNamespace(["core", "serialization"]);
        var parcel = Infrastructure(graph, "Parcel"); var context = Infrastructure(graph, "SerializationGraphContext");
        var fieldInfo = Infrastructure(graph, "FieldInfo"); var caseInfo = Infrastructure(graph, "EnumCaseInfo");
        var any = graph.Bootstrap.Any; var str = graph.Bootstrap.String; var b = graph.Bootstrap;
        TypeSymbol Array(SemanticSymbol t) => graph.GetConstructedType(b.ArrayDefinition, t);
        var nullableAny = graph.GetConstructedType(b.NullableDefinition, any);
        ValidateInfo(fieldInfo, TypeKind.Struct, [("name", str), ("typeName", str), ("nullable", b.Bool)]);
        ValidateInfo(caseInfo, TypeKind.Class, [("name", str), ("fields", Array(fieldInfo))]);
        var candidates = graph.ImportedSymbols.Values.OfType<MethodSymbol>().Where(m => m.Owner == null
            && m.Namespace?.FullName == ns.FullName && m.IsCompilerLibrary && Names.Contains(m.Name)).ToArray();
        foreach (var name in Names)
        {
            var expectedCount = name is "fieldsOf" or "casesOf" ? 3 : name is "typeNameOf" or "isSerializable" ? 2 : 1;
            if (candidates.Count(m => m.Name == name) != expectedCount) throw new ModuleConfigurationException("可信 late helper 数量不符：" + name);
            if (name is "typeNameOf" or "isSerializable" or "fieldsOf" or "casesOf")
            {
                string[] expectedShapes = name == "typeNameOf" ? ["1/0", "1/1"] : name == "isSerializable"
                    ? ["0/1", "1/1"] : ["0/1", "1/0", "1/1"];
                if (!candidates.Where(m => m.Name == name).Select(m => m.GenericParameters.Count + "/" + m.Parameters.Count)
                    .Order(StringComparer.Ordinal).SequenceEqual(expectedShapes))
                    throw new ModuleConfigurationException("可信 late helper overload/GP 元数不符");
            }
        }
        foreach (var m in candidates)
        {
            var generic = m.Name is "typeNameOf" or "isSerializable" or "fieldsOf" or "casesOf" && m.GenericParameters.Count == 1;
            var typeValue = generic ? graph.GetConstructedType(b.TypeDefinition, m.GenericParameters[0]) : null;
            (string Name, SemanticSymbol Type)[] parameters = m.Name switch
            {
                "decodeAnyValue" => [("parcel", parcel), ("context", context), ("id", b.Int64)],
                "sbKind" or "sbLength" or "sbTypeName" => [("value", any)],
                "sbElementAt" or "sbKeyAt" or "sbValueAt" => [("value", any), ("index", b.Int64)],
                "sbBuildArray" or "sbBuildList" => [("elementTypeName", str), ("elements", Array(nullableAny))],
                "sbBuildMap" => [("keyTypeName", str), ("valueTypeName", str), ("keys", Array(nullableAny)), ("values", Array(nullableAny))],
                _ => generic ? m.Parameters.Count == 0 ? [] : [("typeValue", typeValue!)] : [("typeName", str)]
            };
            SemanticSymbol result = m.Name switch
            {
                "sbKind" => b.Int32, "sbLength" => b.Int64, "sbTypeName" or "typeNameOf" => str,
                "sbElementAt" or "sbKeyAt" or "sbValueAt" => nullableAny,
                "isSerializable" => b.Bool, "fieldsOf" => Array(fieldInfo), "casesOf" => Array(caseInfo), _ => any
            };
            var access = m.Name == "decodeAnyValue" ? Accessibility.Private : m.Name.StartsWith("sb", StringComparison.Ordinal)
                ? Accessibility.Internal : Accessibility.Public;
            if (m.Kind != MethodKind.Regular || m.IsStatic || m.IsNative || m.IsAsync || m.IsUnsafe || m.IsAbstract || m.IsOpen
                || m.IsOverride || m.IsEntryPoint || m.ProxyTemplate != null || m.AppliedWrappers.Count != 0 || !m.HasBody
                || m.Accessibility != access || m.GenericParameters.Count != (generic ? 1 : 0)
                || generic && (m.GenericParameters[0].Name != "T" || m.GenericParameters[0].Constraints.Count != 0
                    || m.GenericParameters[0].IsVariadic || m.GenericParameters[0].IsNamedVariadic || m.GenericParameters[0].RequiresSharedSafe)
                || m.Name == "isSerializable" && generic && m.Parameters.Count == 0
                || !ReferenceEquals(m.ReturnType, result) || m.Parameters.Count != parameters.Length
                || m.Parameters.Where((p, i) => p.Name != parameters[i].Name || !ReferenceEquals(p.Type, parameters[i].Type)
                    || p.HasDefaultValue || p.IsVariadic || p.IsNamedVariadic).Any())
                throw new ModuleConfigurationException("可信 late helper 固定签名不符：" + m.Name);
            var canonical = CanonicalSymbolPrinter.PrintMethod(m);
            if (BilCompilerSymbols.Resolve(bil, BilCompilerSymbols.Logical(canonical)) != canonical)
                throw new ModuleConfigurationException("可信 late helper 缺准确 compiler 绑定");
            var declaration = bil.LocalSymbols.OfType<BilSimpleMemberDeclaration>().SingleOrDefault(d => d.Symbol == canonical)
                ?? throw new ModuleConfigurationException("可信 late helper 缺 BIL 声明");
            BilModuleLinker.Compatible(LocalSymbolEmitters.EmitSyntheticMethodDeclaration(m), declaration, bil.Resources);
            if (!bil.Functions.Any(f => f.Symbol == canonical)) throw new ModuleConfigurationException("可信 late helper 缺 BIL body");
        }
        graph.ApprovedLateHelpers.AddRange(candidates);
    }
    private static void ValidateInfo(TypeSymbol type, TypeKind kind, (string Name, SemanticSymbol Type)[] fields)
    {
        var init = type.Methods.SingleOrDefault(m => m.Kind == MethodKind.Init);
        if (type.Kind != kind || type.Accessibility != Accessibility.Public || type.GenericParameters.Count != 0 || type.IsAbstract
            || type.IsUnsafe || type.IsSingleton || type.Fields.Count != fields.Length || init == null || init.IsNative || init.IsAsync
            || init.Accessibility != Accessibility.Public || init.Parameters.Count != fields.Length
            || type.Fields.Where((f, i) => f.Name != fields[i].Name || !ReferenceEquals(f.FieldType, fields[i].Type) || f.IsStatic
                || !f.IsConst || f.Getter != null || f.Setter != null || f.AppliedWrappers.Count != 0).Any()
            || init.Parameters.Where((p, i) => p.Name != fields[i].Name || !ReferenceEquals(p.Type, fields[i].Type)
                || !ReferenceEquals(p.MappedField, type.Fields[i]) || p.HasDefaultValue || p.IsVariadic || p.IsNamedVariadic).Any())
            throw new ModuleConfigurationException("可信反射 metadata 类型/init ABI 不符：" + type.Name);
    }
}
