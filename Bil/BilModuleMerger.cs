using System.Collections.Generic;
using System.Linq;

namespace RigiCompiler.Bil
{
    /// <summary>
    /// 多文件 BIL 模块合并器（§17 命名空间切分的消费侧）：把若干切片/文件
    /// 合并为单个 BilModule。Resources/符号段/Functions 拼接；符号/函数名
    /// 重复即失败。资源例外：同一次编译切出的多个切片可共享同一资源
    ///（同名同内容）——按内容相同去重；同名不同内容才算真重复。Metadata
    /// 同键去重（冲突保留先见者），非重复判定名字空间。
    /// 调用方：vm 命令（合并执行）与 Middleware Gate（多文件门禁）。
    /// </summary>
    public static class BilModuleMerger
    {
        // 三个独立名字空间：资源名 / 符号段条目键（类型按元数、成员按符号）/
        // 函数符号——方法的声明与 fn 定义同符号但分属不同空间，不互为重复
        public static bool Merge(BilModule target, BilModule source, out string? duplicate)
        {
            duplicate = null;
            var resourceNames = new Dictionary<string, BilResource>();
            var symbolKeys = new HashSet<string>();
            var functionSymbols = new HashSet<string>();
            foreach (var resource in target.Resources) resourceNames.Add(resource.Name, resource);
            foreach (var entry in target.LocalSymbols) CollectKeys(entry, symbolKeys);
            foreach (var entry in target.ExternalSymbols) CollectKeys(entry, symbolKeys);
            foreach (var function in target.Functions) functionSymbols.Add(function.Symbol);

            foreach (var resource in source.Resources)
            {
                if (resourceNames.TryGetValue(resource.Name, out var existing))
                {
                    if (SameResource(existing, resource)) continue;
                    duplicate = $"资源 \"{resource.Name}\"";
                    return false;
                }
                resourceNames.Add(resource.Name, resource);
                target.Resources.Add(resource);
            }
            foreach (var entry in source.LocalSymbols)
            {
                foreach (var key in KeysOf(entry))
                {
                    if (!symbolKeys.Add(key)) { duplicate = key; return false; }
                }
                target.LocalSymbols.Add(entry);
            }
            foreach (var entry in source.ExternalSymbols)
            {
                foreach (var key in KeysOf(entry))
                {
                    if (!symbolKeys.Add(key)) { duplicate = key; return false; }
                }
                target.ExternalSymbols.Add(entry);
            }
            foreach (var function in source.Functions)
            {
                if (!functionSymbols.Add(function.Symbol))
                {
                    duplicate = function.Symbol;
                    return false;
                }
                target.Functions.Add(function);
            }
            // Metadata（§4.1 程序集级信息，非符号名字空间）：同键同值去重，
            // 冲突保留先见者——同名切片共享同一 module 键是常态
            foreach (var entry in source.Metadata)
            {
                var exists = false;
                foreach (var existing in target.Metadata)
                {
                    if (existing.Key == entry.Key)
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                {
                    target.Metadata.Add(entry);
                }
            }
            return true;
        }

        private static IEnumerable<string> KeysOf(BilSymbolSectionEntry entry)
        {
            switch (entry)
            {
                case BilTypeDeclaration type:
                    yield return type.GenericParameters.Count == 0
                        ? type.Symbol : type.Symbol + "<" + type.GenericParameters.Count + ">";
                    break;
                case BilSimpleMemberDeclaration member:
                    yield return member.Symbol;
                    break;
                case BilCaseDeclaration caseDeclaration:
                    yield return caseDeclaration.QualifiedName;
                    break;
            }
        }

        private static void CollectKeys(BilSymbolSectionEntry entry, HashSet<string> keys)
        {
            foreach (var key in KeysOf(entry)) keys.Add(key);
        }

        // §17 切片共享资源的同内容判定：同对象恒真；否则按种类逐字段比较
        //（catch-table 条目持 block 引用，按渲染文本比较）
        private static bool SameResource(BilResource a, BilResource b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is BilScalarResource scalarA && b is BilScalarResource scalarB)
            {
                return scalarA.Type == scalarB.Type && scalarA.LiteralText == scalarB.LiteralText;
            }
            if (a is BilNullResource nullA && b is BilNullResource nullB)
            {
                return nullA.TypeRef == nullB.TypeRef;
            }
            if (a is BilCollectionResource collectionA && b is BilCollectionResource collectionB)
            {
                return collectionA.Header == collectionB.Header
                    && collectionA.Elements.SequenceEqual(collectionB.Elements);
            }
            if (a is BilSwitchTableResource switchA && b is BilSwitchTableResource switchB)
            {
                return switchA.SelectorTypeRef == switchB.SelectorTypeRef
                    && switchA.Elements.SequenceEqual(switchB.Elements);
            }
            if (a is BilCatchTableResource catchA && b is BilCatchTableResource catchB)
            {
                return catchA.Entries.Count == catchB.Entries.Count
                    && catchA.Entries.Zip(catchB.Entries).All(pair =>
                        pair.First.Render() == pair.Second.Render());
            }
            return false;
        }
    }
}
