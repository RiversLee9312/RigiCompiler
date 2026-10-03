using RigiCompiler.Bil;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Modules;

internal static class ModuleEntrypoint
{
    // 入口标记只写入请求私有副本，不修改链接输入或缓存的 API/BIL。
    internal static (BilModule Module, string Canonical) Select(ModuleBuilt built)
    {
        var module = BilModuleLinker.Clone(built.Linked);
        var symbols = MwSymbolTable.Build(module);
        var selected = built.Context.Module.Configuration.SelectProfile(built.Context.ProfileName).Entry
            ?? built.Context.Module.Configuration.Entry;
        // 依赖也可含自己的 main；入口选择属于当前模块，不能误运行依赖入口或因其标记造成歧义。
        var ownFunctions = built.Artifact.ReadBil().Functions.Select(f => f.Symbol).ToHashSet(StringComparer.Ordinal);
        var candidates = module.Functions.Select(f => symbols.FindMember(f.Symbol)!).Where(m =>
            ownFunctions.Contains(m.Canonical) && (selected == null ? m.HasKeyword(BilKeyword.Entrypoint) : m.Canonical == selected)).ToArray();
        if (candidates.Length != 1) throw new ModuleConfigurationException("模块必须选择唯一入口完整 canonical（当前 " + candidates.Length + " 个）");
        var entry = candidates[0];
        var fn = module.Functions.Single(f => f.Symbol == entry.Canonical);
        var args = fn.Args.Where(a => a.Name != ".return").ToArray();
        var signature = CanonicalSignature.Parse(entry.Canonical);
        if (entry.IsExternal || entry.HasKeyword(BilKeyword.Native)
            || entry.Owner != null && (entry.Declaration.Kind != BilMemberKind.StaticMethod || entry.Owner.Declaration.GenericParameters.Count != 0)
            || signature.ReturnTypeRef is not (".void" or ".i32")
            || args.Length != 0 && (args.Length != 1 || args[0].TypeRef != ".array<.string>"))
            throw new ModuleConfigurationException("入口必须是无开放泛型的静态/全局函数，返回 void/i32，参数为空或 Array<String>：" + entry.Canonical);
        BilSimpleMemberDeclaration Mark(BilSimpleMemberDeclaration declaration)
        {
            var modifiers = declaration.Modifiers.Where(m => m is not BilKeywordModifier { Keyword: BilKeyword.Entrypoint }).ToList();
            if (declaration.Symbol == entry.Canonical) modifiers.Add(new BilKeywordModifier(BilKeyword.Entrypoint));
            return new(declaration.Kind, declaration.Symbol, modifiers, declaration.ModifiersOnNextLine);
        }
        for (var i = 0; i < module.LocalSymbols.Count; i++)
            if (module.LocalSymbols[i] is BilSimpleMemberDeclaration declaration) module.LocalSymbols[i] = Mark(declaration);
            else if (module.LocalSymbols[i] is BilTypeDeclaration type)
                for (var j = 0; j < type.Members.Count; j++)
                    if (type.Members[j] is BilSimpleMemberDeclaration member) type.Members[j] = Mark(member);
        return (module, entry.Canonical);
    }
}
