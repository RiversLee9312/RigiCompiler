namespace RigiCompiler.Bil;

/// <summary>编译器标准库 helper 绑定记录；使用准确 canonical 与 native ABI，不做同名搜索。</summary>
public static class BilCompilerHelpers
{
    public const string MetadataPrefix = "compiler.helper.";
    public static string? Resolve(BilModule module, string name)
    {
        if (name is not ("any_hash" or "any_to_string")) throw new ArgumentException("未知内建 helper", nameof(name));
        var resultType = name == "any_hash" ? ".i64" : ".string";
        var entries = module.Metadata.Where(m => m.Key == MetadataPrefix + name).ToArray();
        if (entries.Length > 1) throw new BilLinkException("重复 compiler helper 绑定");
        var canonical = entries.Length == 0 ? "core::$" + name + "(value:.any)@" + resultType
            : entries[0].Type == BilScalarType.String ? BilScalarLiteral.DecodeString(entries[0].LiteralText)
            : throw new BilLinkException("compiler helper 绑定必须为 string");
        var expected = "core::$" + name;
        var open = canonical.IndexOf('(');
        if (open < 0 || BilLogicalName.Of(canonical[..open]) != expected
            || canonical[open..] != "(value:.any)@" + resultType)
            throw new BilLinkException("compiler helper canonical ABI 不符");
        var declarations = module.LocalSymbols.Concat(module.ExternalSymbols)
            .OfType<BilSimpleMemberDeclaration>().Where(m => m.Symbol == canonical).ToArray();
        if (declarations.Length == 0)
        {
            if (entries.Length != 0) throw new BilLinkException("compiler helper 缺声明");
            return null;
        }
        foreach (var declaration in declarations)
            if (!declaration.Modifiers.OfType<BilKeywordModifier>().Any(m => m.Keyword == BilKeyword.Native)
                || declaration.Modifiers.OfType<BilNativeSymbolModifier>().SingleOrDefault()?.Symbol != name
                || declaration.Modifiers.OfType<BilNativeLibraryModifier>().SingleOrDefault()?.Library != "rigi_rt")
                throw new BilLinkException("compiler helper native ABI 不符");
        return canonical;
    }
}
