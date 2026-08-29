using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Binding
{
    /// <summary>
    /// wrapper 应用索引（MW10）：从符号表收集 Entity / 字段-Value / Method
    /// 的 wrapped(W) 标记（声明序 = outer→inner）。查询设施，不进 MwContext。
    /// 注（刀6）：前端 BIL 的方法声明不保留 wrapped 修饰符——Method
    /// wrapper 应用的规范事实源是 ..init.wrapper 体的 new.wrapper.method
    /// 安装指令（布局 HiddenStoragePlanner.CollectMethodSlots 与
    /// MethodProxyBakingPass.CollectInstalls 消费）；本索引的 Method 面
    /// 只覆盖手写 BIL 的修饰符形态。
    /// Entity 面为继承闭包语义（刀4）：本类声明序在前，沿 extends 链把
    /// 祖先未被重申的应用按基→本追加在后；同 wrapper 定义（模板身份，
    /// 泛型实参不计）派生重申覆盖基类、只装一次——与 §9.7 安装侧
    /// ..init.wrapper 闭包缝合及布局基类槽拷入同口径。合法前端 BIL 经
    /// §14.9 重申约束后本类声明即全闭包，祖先段恒为空；VM 侧
    /// CollectEntityWrappers 只读本类声明，靠同一重申约束等价。
    /// </summary>
    public sealed class WrapperApplicationIndex
    {
        private readonly Dictionary<string, List<string>> _entity = new(System.StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _field = new(System.StringComparer.Ordinal);
        private readonly Dictionary<string, List<(string Method, string Wrapper)>> _method =
            new(System.StringComparer.Ordinal);

        public static WrapperApplicationIndex Build(MwSymbolTable symbols)
        {
            var index = new WrapperApplicationIndex();
            foreach (var type in symbols.Types)
            {
                if (type.IsExternal)
                {
                    continue;
                }
                var entity = CollectWrapped(type.Declaration.Modifiers);
                if (entity.Count > 0)
                {
                    index._entity[type.Canonical] = entity;
                }
                foreach (var member in type.Members)
                {
                    var wrappers = CollectWrapped(member.Declaration.Modifiers);
                    if (wrappers.Count == 0)
                    {
                        continue;
                    }
                    if (member.Declaration.Kind is BilMemberKind.Field or BilMemberKind.StaticField)
                    {
                        index._field[member.Canonical] = wrappers;
                    }
                    else if (member.Declaration.Kind is BilMemberKind.Method
                        or BilMemberKind.StaticMethod)
                    {
                        if (!index._method.TryGetValue(type.Canonical, out var list))
                        {
                            list = new List<(string, string)>();
                            index._method[type.Canonical] = list;
                        }
                        foreach (var wrapper in wrappers)
                        {
                            list.Add((member.Canonical, wrapper));
                        }
                    }
                }
            }
            // Entity 闭包并入：沿 extends 链（基→本）追加未重申的祖先应用。
            // 同定义去重键 = wrapper 模板名（构造实参不计——派生重申的
            // Logged<Child> 覆盖基类的 Logged<Base>）。环保护防手写 BIL
            // extends 成环死循环。
            foreach (var type in symbols.Types)
            {
                if (type.IsExternal)
                {
                    continue;
                }
                index._entity.TryGetValue(type.Canonical, out var own);
                var merged = own == null ? null : new List<string>(own);
                var definitions = new HashSet<string>(System.StringComparer.Ordinal);
                if (own != null)
                {
                    foreach (var wrapperRef in own)
                    {
                        definitions.Add(DefinitionKey(wrapperRef));
                    }
                }
                var chain = new List<MwTypeSymbol>();
                var current = type;
                var guard = new HashSet<string>(System.StringComparer.Ordinal) { type.Canonical };
                while (current.Declaration.ExtendsType is { } baseRef
                    && symbols.FindTypeByRef(baseRef) is { IsExternal: false } baseType
                    && guard.Add(baseType.Canonical))
                {
                    chain.Add(baseType);
                    current = baseType;
                }
                for (var i = chain.Count - 1; i >= 0; i--)
                {
                    foreach (var wrapperRef in CollectWrapped(chain[i].Declaration.Modifiers))
                    {
                        if (definitions.Add(DefinitionKey(wrapperRef)))
                        {
                            (merged ??= new List<string>()).Add(wrapperRef);
                        }
                    }
                }
                if (merged != null && merged.Count > 0)
                {
                    index._entity[type.Canonical] = merged;
                }
            }
            return index;
        }

        // wrapper 定义身份键（同定义重申去重用）：构造实参段不计
        private static string DefinitionKey(string wrapperRef)
        {
            var cut = wrapperRef.IndexOf('<');
            return cut < 0 ? wrapperRef : wrapperRef.Substring(0, cut);
        }

        public IReadOnlyList<string> EntityWrappers(string typeCanonical) =>
            _entity.TryGetValue(typeCanonical, out var list) ? list : System.Array.Empty<string>();

        public IReadOnlyList<string> FieldWrappers(string fieldCanonical) =>
            _field.TryGetValue(fieldCanonical, out var list) ? list : System.Array.Empty<string>();

        public IReadOnlyList<(string Method, string Wrapper)> MethodWrappers(string typeCanonical) =>
            _method.TryGetValue(typeCanonical, out var list)
                ? list
                : System.Array.Empty<(string, string)>();

        public bool HasAny(string typeCanonical) =>
            _entity.ContainsKey(typeCanonical) || _method.ContainsKey(typeCanonical);

        private static List<string> CollectWrapped(IReadOnlyList<BilModifier> modifiers)
        {
            var list = new List<string>();
            foreach (var modifier in modifiers)
            {
                if (modifier is BilWrappedModifier wrapped)
                {
                    list.Add(MwTypeKey.Normalize(wrapped.WrapperTypeRef));
                }
            }
            return list;
        }
    }
}
