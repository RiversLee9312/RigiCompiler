namespace RigiCompiler.Bil;

/// <summary>装载协议：metadata 顺序来自依赖先于宿主的 DAG，不属于指令可读数据。</summary>
public static class BilModuleInitialization
{
    public const string MetadataPrefix = "module.init.";
    public static IReadOnlyList<string> Order(BilModule module)
    {
        var initializers = module.Functions.Where(f => BilLogicalName.IsGlobalInitializer(f.Symbol))
            .Select(f => f.Symbol).ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var metadata in module.Metadata.Where(m => m.Key.StartsWith(MetadataPrefix, StringComparison.Ordinal)))
        {
            if (metadata.Type != BilScalarType.String) throw new BilLinkException("module init metadata 要求 string");
            var symbol = BilScalarLiteral.DecodeString(metadata.LiteralText);
            if (!initializers.Remove(symbol)) throw new BilLinkException("module init metadata 缺函数或重复：" + symbol);
            result.Add(symbol);
        }
        // 兼容低级 compile/手写 BIL；独立 module 发射必须登记全部初始化函数。
        result.AddRange(module.Functions.Where(f => initializers.Contains(f.Symbol)).Select(f => f.Symbol));
        return result;
    }
}
