using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace RigiCompiler;

/// <summary>源身份与链接投影分开；低级 compile 保持原单编译单元语义。</summary>
internal static class ModuleOrigin
{
    private sealed record FileOrigin(string ModuleId, string FileId);
    private static readonly ConditionalWeakTable<RootASTNode, FileOrigin> Files = new();
    internal static string Hash(string moduleId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(moduleId))).ToLowerInvariant();
    internal static void Register(CompilationUnit unit)
    {
        if (!unit.IsModuleCompilation) return;
        foreach (var file in unit.SourceFiles)
            Files.AddOrUpdate(file, new(unit.ModuleIdentity, file.Span?.sourceName ?? "<source>"));
    }
    internal static string? OfFile(RootASTNode? file) => file != null && Files.TryGetValue(file, out var origin) ? origin.ModuleId : null;
    internal static void Assign(SemanticSymbol symbol, CompilationUnit unit, ASTNode? syntax = null)
    {
        if (!unit.IsModuleCompilation || symbol.IsImported || symbol is TypeSymbol { IsBuiltin: true }) return;
        symbol.OriginModuleId ??= unit.ModuleIdentity;
        symbol.OriginFileId ??= syntax?.Span?.sourceName ?? symbol.SourceFile?.Span?.sourceName;
    }
    internal static void AssignDeclarations(CompilationUnit unit, IEnumerable<TypeSymbol> syntheticOwners)
    {
        if (!unit.IsModuleCompilation) return;
        var seen = new HashSet<SemanticSymbol>();
        void Type(TypeSymbol type)
        {
            if (!seen.Add(type) || type.IsImported) return;
            Assign(type, unit);
            foreach (var child in type.NestedTypes) Type(child);
            foreach (var field in type.Fields) Assign(field, unit);
            foreach (var method in type.Methods) Assign(method, unit);
            foreach (var item in type.Cases) Assign(item, unit);
            foreach (var iface in type.Interfaces) Type(iface.ConstructedFrom ?? iface);
        }
        void Namespace(NamespaceSymbol ns)
        {
            foreach (var type in ns.Types) Type(type);
            foreach (var field in ns.Fields) Assign(field, unit);
            foreach (var method in ns.Methods) Assign(method, unit);
            foreach (var child in ns.ChildNamespaces) Namespace(child);
        }
        Namespace(unit.Symbols.GlobalNamespace);
        foreach (var owner in syntheticOwners)
            for (var type = owner; type != null; type = type.DeclaringType) Type(type);
    }
    internal static string LinkedName(SemanticSymbol symbol) => symbol.Accessibility != Accessibility.Public
        && symbol.OriginModuleId != null ? symbol.Name + "__m_" + Hash(symbol.OriginModuleId) : symbol.Name;
}
