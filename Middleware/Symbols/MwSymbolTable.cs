using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;

namespace RigiCompiler.Middleware.Symbols
{
    /// <summary>
    /// MW1 符号表（MW0 骨架）：从已过门禁的 BilModule 建驻留符号图。
    /// 当前登记：类型（含成员）、全局成员、外部引用、enum case（MW4，
    /// 判别值登记期定值）。泛型具化请求、wrapper
    /// 应用标记与资源表的登记随后续 MW 阶段推进在此扩展（wrapper 隐藏存储
    /// 命名见 BIL §5.3 ABI 约定）。
    /// 驻留纪律：同一 canonical 符号在 Local 与 External 段同现时归一为同一
    /// 对象（本地定义优先，外部段只补缺）。
    /// </summary>
    public sealed class MwSymbolTable
    {
        private readonly Dictionary<string, MwTypeSymbol> _types = new(StringComparer.Ordinal);
        // 同名不同元数（Func\<TRet> / Func\<TRet, T0>）：DeclarationKey 索引
        private readonly Dictionary<string, MwTypeSymbol> _typesByDeclKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MwMemberSymbol> _members = new(StringComparer.Ordinal);

        public IReadOnlyCollection<MwTypeSymbol> Types => _types.Values;
        internal IReadOnlyCollection<MwTypeSymbol> Declarations => _typesByDeclKey.Values;
        public IReadOnlyCollection<MwMemberSymbol> Members => _members.Values;

        // 全局成员（Owner 为 null：全局函数/全局字段，§8.4.1）
        public IEnumerable<MwMemberSymbol> GlobalMembers => _members.Values.Where(m => m.Owner == null);

        public MwTypeSymbol? FindType(string canonical) =>
            _types.TryGetValue(canonical, out var symbol) ? symbol : null;

        // 构造类型 / 同名不同元数反查（DeclarationKey = 符号 + 顶层实参个数）
        public MwTypeSymbol? FindTypeByRef(string typeRef)
        {
            var normalized = MwTypeKey.Normalize(typeRef);
            if (_typesByDeclKey.TryGetValue(
                    BilVerificationContext.DeclarationKeyOf(normalized), out var byKey))
            {
                return byKey;
            }
            return FindType(normalized);
        }

        public MwMemberSymbol? FindMember(string canonical) =>
            _members.TryGetValue(canonical, out var symbol) ? symbol : null;

        // MW11a：合成类型注册口（CoroutineSplitPass 的协程 frame 类型；
        // 驻留纪律同 Build——同 canonical 幂等直返，成员双登记进反查表）。
        // 合成类型无泛型参数，declKey 即裸符号
        internal MwTypeSymbol RegisterSyntheticType(BilTypeDeclaration declaration)
        {
            if (declaration.GenericParameters.Count != 0)
            {
                throw new CompilerInternalException(
                    $"合成类型不得带泛型参数: {declaration.Symbol}");
            }
            if (_types.TryGetValue(declaration.Symbol, out var existing))
            {
                return existing;
            }
            var members = new List<MwMemberSymbol>();
            var typeSymbol = new MwTypeSymbol(declaration, isExternal: false, members,
                new List<MwCaseSymbol>());
            _types.Add(declaration.Symbol, typeSymbol);
            _typesByDeclKey.Add(declaration.Symbol, typeSymbol);
            foreach (var memberDecl in declaration.Members.OfType<BilSimpleMemberDeclaration>())
            {
                var memberSymbol = new MwMemberSymbol(memberDecl, typeSymbol, isExternal: false);
                members.Add(memberSymbol);
                if (!_members.ContainsKey(memberDecl.Symbol))
                {
                    _members.Add(memberDecl.Symbol, memberSymbol);
                }
            }
            return typeSymbol;
        }

        public static MwSymbolTable Build(BilModule module)
        {
            var table = new MwSymbolTable();
            // 资源表（enum case 的 discriminant res(R) 定值用）
            var resources = new Dictionary<string, BilResource>(StringComparer.Ordinal);
            foreach (var resource in module.Resources)
            {
                resources.Add(resource.Name, resource);
            }
            table._resources = resources;
            // 先本地段（定义优先），后外部段（缺失才补）
            table.CollectSection(module.LocalSymbols, isExternal: false);
            table.CollectSection(module.ExternalSymbols, isExternal: true);
            return table;
        }

        private IReadOnlyDictionary<string, BilResource> _resources =
            new Dictionary<string, BilResource>(StringComparer.Ordinal);

        private void CollectSection(List<BilSymbolSectionEntry> entries, bool isExternal)
        {
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case BilTypeDeclaration type:
                        // 反查键 = 符号 + 泛型元数（同名不同元数合法共存）；
                        // 本地定义优先，外部段同键只补缺
                        var declKey = type.GenericParameters.Count == 0
                            ? type.Symbol
                            : type.Symbol + "<" + type.GenericParameters.Count + ">";
                        if (_typesByDeclKey.ContainsKey(declKey)) break;
                        var members = new List<MwMemberSymbol>();
                        var cases = new List<MwCaseSymbol>();
                        var typeSymbol = new MwTypeSymbol(type, isExternal, members, cases);
                        _typesByDeclKey.Add(declKey, typeSymbol);
                        // 裸符号表保留首个元数（FindType 旧口径）；其余元数只走 FindTypeByRef
                        if (!_types.ContainsKey(type.Symbol))
                        {
                            _types.Add(type.Symbol, typeSymbol);
                        }
                        foreach (var memberDecl in type.Members.OfType<BilSimpleMemberDeclaration>())
                        {
                            // 合成成员双登记（companion/cell 的 init 族既列
                            // 顶层段又列类型段——发射器合成序所致，VM
                            // IndexMembers 以类型段声明为权威同口径）：
                            // 顶层占位（Owner=null）被类型段条目替换升级，
                            // 否则 type.Members 丢成员（singleton get 合成
                            // 与 new 的 init 匹配依赖完整成员表）
                            if (_members.TryGetValue(memberDecl.Symbol, out var existing))
                            {
                                if (existing.Owner == null && !existing.IsExternal)
                                {
                                    var upgraded = new MwMemberSymbol(memberDecl, typeSymbol,
                                        isExternal);
                                    _members[memberDecl.Symbol] = upgraded;
                                    members.Add(upgraded);
                                }
                                continue;
                            }
                            var memberSymbol = new MwMemberSymbol(memberDecl, typeSymbol, isExternal);
                            members.Add(memberSymbol);
                            _members.Add(memberDecl.Symbol, memberSymbol);
                        }
                        // enum case 登记（§8.5，MW4）：判别值在此定值——
                        // auto = 声明序从 0；res(R) = 非负整数标量资源值
                        foreach (var caseDecl in type.Members.OfType<BilCaseDeclaration>())
                        {
                            cases.Add(new MwCaseSymbol(caseDecl, typeSymbol,
                                ResolveDiscriminant(caseDecl, cases.Count, type.Symbol)));
                        }
                        break;
                    case BilSimpleMemberDeclaration member:
                        if (_members.ContainsKey(member.Symbol)) break;
                        _members.Add(member.Symbol, new MwMemberSymbol(member, null, isExternal));
                        break;
                }
            }
        }

        // 判别值定值（verifier 已查资源存在且为非负整数标量；此处防御）
        private uint ResolveDiscriminant(BilCaseDeclaration caseDecl, int autoIndex, string ownerSymbol)
        {
            if (caseDecl.DiscriminantResource == null)
            {
                return (uint)autoIndex;
            }
            if (_resources.TryGetValue(caseDecl.DiscriminantResource, out var resource)
                && resource is BilScalarResource scalar)
            {
                return (uint)BilScalarLiteral.ParseUnsigned(scalar.LiteralText);
            }
            throw new CompilerInternalException(
                $"enum case 判别值资源不可解析: {caseDecl.DiscriminantResource}（{ownerSymbol}）");
        }
    }
}
