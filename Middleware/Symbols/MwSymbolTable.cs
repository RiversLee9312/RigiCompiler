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
        // 同名不同元数类型可拥有完全相同的成员 canonical；复合键防止后
        // 登记者被静默吞掉。旧的 canonical 索引仅作为无宿主上下文查询。
        private readonly Dictionary<string, MwMemberSymbol> _membersByOwnerDeclKey =
            new(StringComparer.Ordinal);
        private readonly List<MwMemberSymbol> _memberOrder = new();

        public IReadOnlyCollection<MwTypeSymbol> Types => _types.Values;
        internal IReadOnlyCollection<MwTypeSymbol> Declarations => _typesByDeclKey.Values;
        public IReadOnlyCollection<MwMemberSymbol> Members => _memberOrder;

        // 全局成员（Owner 为 null：全局函数/全局字段，§8.4.1）
        public IEnumerable<MwMemberSymbol> GlobalMembers => _memberOrder.Where(m => m.Owner == null);

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

        public MwMemberSymbol? FindMember(string canonical, string ownerTypeRef)
        {
            var key = BilVerificationContext.DeclarationKeyOf(MwTypeKey.Normalize(ownerTypeRef));
            return _membersByOwnerDeclKey.TryGetValue(MemberKey(key, canonical), out var symbol)
                ? symbol : FindMember(canonical);
        }

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
                RegisterMember(memberSymbol, declaration.Symbol);
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
                        if (_typesByDeclKey.ContainsKey(declKey)) continue;
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
                            var compositeKey = MemberKey(declKey, memberDecl.Symbol);
                            _membersByOwnerDeclKey.TryGetValue(compositeKey, out var existing);
                            var placeholderKey = MemberKey("", memberDecl.Symbol);
                            if (existing == null
                                && _membersByOwnerDeclKey.TryGetValue(placeholderKey,
                                    out var globalPlaceholder)
                                && globalPlaceholder.Owner == null
                                && !globalPlaceholder.IsExternal)
                            {
                                existing = globalPlaceholder;
                            }
                            if (existing != null)
                            {
                                if (existing.Owner == null && !existing.IsExternal)
                                {
                                    var upgraded = new MwMemberSymbol(memberDecl, typeSymbol,
                                        existing.IsExternal);
                                    _membersByOwnerDeclKey.Remove(placeholderKey);
                                    _membersByOwnerDeclKey[compositeKey] = upgraded;
                                    if (_members.TryGetValue(memberDecl.Symbol, out var first)
                                        && ReferenceEquals(first, existing))
                                    {
                                        _members[memberDecl.Symbol] = upgraded;
                                    }
                                    _memberOrder.Remove(existing);
                                    _memberOrder.Add(upgraded);
                                    members.Add(upgraded);
                                }
                                continue;
                            }
                            var memberSymbol = new MwMemberSymbol(memberDecl, typeSymbol, isExternal);
                            members.Add(memberSymbol);
                            RegisterMember(memberSymbol, declKey);
                        }
                        // enum case 登记（§8.5，MW4）：判别值在此定值——
                        // auto = 声明序从 0；res(R) = 非负整数标量资源值
                        foreach (var caseDecl in type.Members.OfType<BilCaseDeclaration>())
                        {
                            cases.Add(new MwCaseSymbol(caseDecl, typeSymbol,
                                ResolveDiscriminant(caseDecl, cases, type.Symbol)));
                        }
                        break;
                    case BilSimpleMemberDeclaration member:
                        var global = new MwMemberSymbol(member, null, isExternal);
                        var globalKey = MemberKey("", member.Symbol);
                        if (_membersByOwnerDeclKey.ContainsKey(globalKey)) continue;
                        RegisterMember(global, "");
                        break;
                }
            }
        }

        private static string MemberKey(string ownerDeclKey, string canonical) =>
            ownerDeclKey + "\0" + canonical;

        private void RegisterMember(MwMemberSymbol member, string ownerDeclKey)
        {
            var key = MemberKey(ownerDeclKey, member.Canonical);
            if (!_membersByOwnerDeclKey.TryAdd(key, member))
            {
                throw new CompilerInternalException($"成员符号复合键重复: {member.Canonical}");
            }
            _memberOrder.Add(member);
            _members.TryAdd(member.Canonical, member);
        }

        // 判别值定值（verifier 已查资源存在且为非负整数标量；此处防御）
        private uint ResolveDiscriminant(BilCaseDeclaration caseDecl,
            IReadOnlyList<MwCaseSymbol> precedingCases, string ownerSymbol)
        {
            uint value;
            if (caseDecl.DiscriminantResource == null)
            {
                value = checked((uint)precedingCases.Count);
            }
            else if (_resources.TryGetValue(caseDecl.DiscriminantResource, out var resource)
                && resource is BilScalarResource scalar
                && scalar.Type is BilScalarType.I8 or BilScalarType.I16 or BilScalarType.I32
                    or BilScalarType.I64 or BilScalarType.U8 or BilScalarType.U16
                    or BilScalarType.U32 or BilScalarType.U64)
            {
                try
                {
                    if (scalar.Type is BilScalarType.I8 or BilScalarType.I16
                        or BilScalarType.I32 or BilScalarType.I64)
                    {
                        var signed = BilScalarLiteral.ParseSigned(scalar.LiteralText);
                        if (signed < 0 || signed > uint.MaxValue) throw new OverflowException();
                        value = (uint)signed;
                    }
                    else
                    {
                        var unsigned = BilScalarLiteral.ParseUnsigned(scalar.LiteralText);
                        if (unsigned > uint.MaxValue) throw new OverflowException();
                        value = (uint)unsigned;
                    }
                }
                catch (Exception ex) when (ex is FormatException or OverflowException)
                {
                    throw new CompilerInternalException(
                        $"enum case 判别值越界或格式非法: {scalar.LiteralText}（{ownerSymbol}）");
                }
            }
            else
            {
                throw new CompilerInternalException(
                    $"enum case 判别值资源不可解析或不是整数: {caseDecl.DiscriminantResource}（{ownerSymbol}）");
            }
            if (precedingCases.Any(item => item.Discriminant == value))
            {
                throw new CompilerInternalException(
                    $"enum case 判别值冲突: {value}（{ownerSymbol}）");
            }
            return value;
        }
    }
}
