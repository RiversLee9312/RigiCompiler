namespace RigiCompiler.Bil;

public sealed class BilLinkException(string message) : InvalidOperationException(message);

/// <summary>独立程序集链接：先克隆，再核验 typed ABI，成功才返回新模块。</summary>
public static class BilModuleLinker
{
    public static BilModule Link(IReadOnlyList<BilModule> modules)
    {
        var result = new BilModule();
        var local = new Dictionary<string, BilSymbolSectionEntry>(StringComparer.Ordinal);
        var external = new Dictionary<string, BilSymbolSectionEntry>(StringComparer.Ordinal);
        var functions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var original in modules)
        {
            if (original.BilVersion != result.BilVersion) throw new BilLinkException("BIL version 不相容");
            var module = Clone(original, result.Resources.Count);
            result.Resources.AddRange(module.Resources);
            foreach (var declaration in module.LocalSymbols)
            {
                var key = Key(declaration);
                if (!local.TryAdd(key, declaration)) throw new BilLinkException("重复本地定义：" + key);
                if (external.Remove(key, out var requirement)) Compatible(requirement, declaration, result.Resources);
                result.LocalSymbols.Add(declaration);
            }
            foreach (var declaration in module.ExternalSymbols)
            {
                var key = Key(declaration);
                if (local.TryGetValue(key, out var implementation)) Compatible(declaration, implementation, result.Resources);
                else if (external.TryGetValue(key, out var previous))
                {
                    // 两份外部契约必须等价；不能以先见者掩盖 ABI 分歧。
                    Compatible(previous, declaration, result.Resources);
                    Compatible(declaration, previous, result.Resources);
                }
                else external.Add(key, declaration);
            }
            foreach (var function in module.Functions)
            {
                if (!functions.Add(function.Symbol)) throw new BilLinkException("重复函数体：" + function.Symbol);
                result.Functions.Add(function);
            }
            foreach (var metadata in module.Metadata)
            {
                if (metadata.Key == "module") continue;
                var previous = result.Metadata.Find(m => m.Key == metadata.Key);
                if (previous != null && (previous.Type != metadata.Type || previous.LiteralText != metadata.LiteralText))
                    throw new BilLinkException("metadata 冲突：" + metadata.Key);
                if (previous == null) result.Metadata.Add(metadata);
            }
        }
        result.ExternalSymbols.AddRange(external.Values);
        return result;
    }

    // Writer/Reader 负责全部指令族和函数局部块克隆；下方按原对象映射重建资源，
    // 特别是 catch handler，绝不按跨函数可能重复的 block.Id 寻找。
    internal static BilModule Clone(BilModule original, int resourceOffset = 0)
    {
        var clone = BilReader.Read(BilWriter.Write(original));
        var blocks = new Dictionary<BilBlock, BilBlock>();
        for (int f = 0; f < original.Functions.Count; f++)
            for (int b = 0; b < original.Functions[f].Blocks.Count; b++)
                blocks.Add(original.Functions[f].Blocks[b], clone.Functions[f].Blocks[b]);
        var resources = new Dictionary<BilResource, BilResource>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resource in original.Resources)
        {
            var name = "R_" + (resourceOffset + resources.Count);
            if (!names.TryAdd(resource.Name, name)) throw new BilLinkException("重复资源：" + resource.Name);
            BilResource copy = resource switch
            {
                BilScalarResource scalar => new BilScalarResource(name, scalar.Type, scalar.LiteralText),
                BilNullResource nil => new BilNullResource(name, nil.TypeRef),
                BilCollectionResource collection => new BilCollectionResource(name, collection.Header, collection.Elements.ToArray(), collection.Multiline),
                BilSwitchTableResource table => new BilSwitchTableResource(name, table.SelectorTypeRef, table.Elements.ToArray()),
                BilCatchTableResource catches => new BilCatchTableResource(name, catches.Entries.Select(e =>
                    new BilCatchEntry(new BilTypeOperand(e.ExceptionType.TypeRef), blocks.TryGetValue(e.Handler, out var block)
                        ? block : new BilBlock(e.Handler.Id))).ToArray()),
                _ => throw new BilLinkException("未知 resource kind")
            };
            resources.Add(resource, copy);
        }
        clone.Resources.Clear(); clone.Resources.AddRange(resources.Values);
        BilResource Resource(BilResource source) => resources.TryGetValue(source, out var copy) ? copy
            : throw new BilLinkException("指令资源未声明：" + source.Name);
        BilResource CatchResource(BilResource source)
        {
            if (source is not BilCatchTableResource catches || !names.TryGetValue(source.Name, out var name))
                throw new BilLinkException("try catch-table 未声明");
            return new BilCatchTableResource(name, catches.Entries.Select(e =>
                new BilCatchEntry(new BilTypeOperand(e.ExceptionType.TypeRef), blocks.TryGetValue(e.Handler, out var block)
                    ? block : throw new BilLinkException("try catch handler 不属于其模块函数"))).ToArray());
        }
        for (int f = 0; f < original.Functions.Count; f++)
            for (int b = 0; b < original.Functions[f].Blocks.Count; b++)
            {
                var from = original.Functions[f].Blocks[b];
                var to = clone.Functions[f].Blocks[b];
                for (int i = 0; i < from.Instructions.Count; i++)
                {
                    var instruction = from.Instructions[i];
                    var parsed = to.Instructions[i];
                    var replacement = instruction switch
                    {
                        LoadInstruction load => new LoadInstruction(Resource(load.Resource), load.Target),
                        HintInstruction hint => new HintInstruction(Resource(hint.Resource)),
                        SwitchInstruction select => new SwitchInstruction(select.Selector, Resource(select.Table),
                            select.ItemBlocks.Select(block => blocks[block]).ToArray(), blocks[select.DefaultBlock], select.BreakId),
                        TryInstruction attempt => new TryInstruction(blocks[attempt.Body], attempt.ExceptionSlot,
                            CatchResource(attempt.CatchTable), attempt.FinallyBlock == null ? null : blocks[attempt.FinallyBlock], attempt.BreakId),
                        _ => parsed,
                    };
                    replacement.Origin = instruction.Origin;
                    to.Instructions[i] = replacement;
                }
            }
        BilSymbolSectionEntry RemapCase(BilSymbolSectionEntry entry, int caseOrdinal = 0)
        {
            if (entry is BilCaseDeclaration c)
            {
                string name;
                if (c.DiscriminantResource == null)
                {
                    // auto 的实际 ABI 是所在 enum 的 case 声明序，不是 null。
                    name = "R_" + (resourceOffset + clone.Resources.Count);
                    clone.Resources.Add(new BilScalarResource(name, BilScalarType.U32,
                        caseOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
                else if (!names.TryGetValue(c.DiscriminantResource, out name!))
                    throw new BilLinkException("enum discriminant 资源未声明");
                return new BilCaseDeclaration(c.QualifiedName, c.Parameters.ToArray(), name);
            }
            if (entry is BilTypeDeclaration type)
            {
                var ordinal = 0;
                for (int i = 0; i < type.Members.Count; i++)
                {
                    var isCase = type.Members[i] is BilCaseDeclaration;
                    type.Members[i] = (BilMemberDeclaration)RemapCase(type.Members[i], ordinal);
                    if (isCase) ordinal++;
                }
            }
            return entry;
        }
        for (int i = 0; i < clone.LocalSymbols.Count; i++) clone.LocalSymbols[i] = RemapCase(clone.LocalSymbols[i]);
        for (int i = 0; i < clone.ExternalSymbols.Count; i++) clone.ExternalSymbols[i] = RemapCase(clone.ExternalSymbols[i]);
        return clone;
    }

    internal static string Key(BilSymbolSectionEntry declaration) => declaration switch
    {
        BilTypeDeclaration t => t.Symbol + "<" + t.GenericParameters.Count + ">",
        BilSimpleMemberDeclaration m => m.Symbol,
        BilCaseDeclaration c => c.QualifiedName,
        _ => throw new BilLinkException("未知 declaration kind")
    };
    internal static void Compatible(BilSymbolSectionEntry requirement, BilSymbolSectionEntry implementation,
        IReadOnlyList<BilResource> resources)
    {
        var compatible = (requirement, implementation) switch
        {
            (BilTypeDeclaration a, BilTypeDeclaration b) => a.Kind == b.Kind && a.ExtendsType == b.ExtendsType
                && a.ImplementsTypes.SequenceEqual(b.ImplementsTypes) && a.GenericParameters.SequenceEqual(b.GenericParameters)
                && a.GenericVariances.SequenceEqual(b.GenericVariances) && Modifiers(a.Modifiers, b.Modifiers),
            (BilSimpleMemberDeclaration a, BilSimpleMemberDeclaration b) => a.Kind == b.Kind
                && a.Symbol == b.Symbol && Modifiers(a.Modifiers, b.Modifiers),
            (BilCaseDeclaration a, BilCaseDeclaration b) => a.QualifiedName == b.QualifiedName
                && a.Parameters.Select(p => (p.Name, p.TypeRef)).SequenceEqual(b.Parameters.Select(p => (p.Name, p.TypeRef)))
                && Discriminant(a, resources) == Discriminant(b, resources),
            _ => false
        };
        if (!compatible) throw new BilLinkException("typed ABI 不相容：" + Key(requirement));
        if (requirement is BilTypeDeclaration requiredType && implementation is BilTypeDeclaration actualType)
            foreach (var member in requiredType.Members)
            {
                var actual = actualType.Members.FirstOrDefault(m => Key(m) == Key(member));
                if (actual == null) throw new BilLinkException("缺少契约成员：" + Key(member));
                Compatible(member, actual, resources);
            }
    }
    private static bool Modifiers(IEnumerable<BilModifier> a, IEnumerable<BilModifier> b)
    {
        // wrapped 顺序承载派发语义；其它修饰符比较集合，不比较排版位。
        return a.OfType<BilWrappedModifier>().Select(m => m.WrapperTypeRef)
                .SequenceEqual(b.OfType<BilWrappedModifier>().Select(m => m.WrapperTypeRef))
            && a.Where(m => m is not BilWrappedModifier).Select(m => m.Render()).Order(StringComparer.Ordinal)
                .SequenceEqual(b.Where(m => m is not BilWrappedModifier).Select(m => m.Render()).Order(StringComparer.Ordinal));
    }
    private static uint Discriminant(BilCaseDeclaration declaration, IReadOnlyList<BilResource> resources)
    {
        if (declaration.DiscriminantResource == null) throw new BilLinkException("enum auto 未规范化");
        var resource = resources.FirstOrDefault(r => r.Name == declaration.DiscriminantResource);
        if (resource is not BilScalarResource scalar || scalar.Type is not (BilScalarType.I8 or BilScalarType.I16 or BilScalarType.I32 or BilScalarType.I64
            or BilScalarType.U8 or BilScalarType.U16 or BilScalarType.U32 or BilScalarType.U64))
            throw new BilLinkException("enum discriminant 必须是整数资源");
        try
        {
            return scalar.Type is BilScalarType.I8 or BilScalarType.I16 or BilScalarType.I32 or BilScalarType.I64
                ? checked((uint)BilScalarLiteral.ParseSigned(scalar.LiteralText)) : checked((uint)BilScalarLiteral.ParseUnsigned(scalar.LiteralText));
        }
        catch (Exception ex) when (ex is FormatException or OverflowException) { throw new BilLinkException("enum discriminant 必须是非负 uint 范围整数"); }
    }
}
