using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;

namespace RigiCompiler.Middleware
{
    /// <summary>
    /// MW1 符号表（MW0 骨架）：从已过门禁的 BilModule 建驻留符号图。
    /// 当前登记：类型（含成员）、全局成员、外部引用。泛型具化请求、wrapper
    /// 应用标记与资源表的登记随后续 MW 阶段推进在此扩展（wrapper 隐藏存储
    /// 命名见 BIL §5.3 ABI 约定；enum case 登记随 MW6 布局推进）。
    /// 驻留纪律：同一 canonical 符号在 Local 与 External 段同现时归一为同一
    /// 对象（本地定义优先，外部段只补缺）。
    /// </summary>
    public sealed class MwSymbolTable
    {
        private readonly Dictionary<string, MwTypeSymbol> _types = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MwMemberSymbol> _members = new(StringComparer.Ordinal);

        public IReadOnlyCollection<MwTypeSymbol> Types => _types.Values;
        public IReadOnlyCollection<MwMemberSymbol> Members => _members.Values;

        // 全局成员（Owner 为 null：全局函数/全局字段，§8.4.1）
        public IEnumerable<MwMemberSymbol> GlobalMembers => _members.Values.Where(m => m.Owner == null);

        public MwTypeSymbol? FindType(string canonical) =>
            _types.TryGetValue(canonical, out var symbol) ? symbol : null;

        public MwMemberSymbol? FindMember(string canonical) =>
            _members.TryGetValue(canonical, out var symbol) ? symbol : null;

        public static MwSymbolTable Build(BilModule module)
        {
            var table = new MwSymbolTable();
            // 先本地段（定义优先），后外部段（缺失才补）
            table.CollectSection(module.LocalSymbols, isExternal: false);
            table.CollectSection(module.ExternalSymbols, isExternal: true);
            return table;
        }

        private void CollectSection(List<BilSymbolSectionEntry> entries, bool isExternal)
        {
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case BilTypeDeclaration type:
                        // 同名类型已登记（本地定义优先）：外部引用归一为同一驻留对象
                        if (_types.ContainsKey(type.Symbol)) break;
                        var members = new List<MwMemberSymbol>();
                        var typeSymbol = new MwTypeSymbol(type, isExternal, members);
                        _types.Add(type.Symbol, typeSymbol);
                        foreach (var memberDecl in type.Members.OfType<BilSimpleMemberDeclaration>())
                        {
                            if (_members.ContainsKey(memberDecl.Symbol)) continue;
                            var memberSymbol = new MwMemberSymbol(memberDecl, typeSymbol, isExternal);
                            members.Add(memberSymbol);
                            _members.Add(memberDecl.Symbol, memberSymbol);
                        }
                        break;
                    case BilSimpleMemberDeclaration member:
                        if (_members.ContainsKey(member.Symbol)) break;
                        _members.Add(member.Symbol, new MwMemberSymbol(member, null, isExternal));
                        break;
                    // BilCaseDeclaration（enum case）的登记随 MW6 enum 布局推进
                }
            }
        }
    }
}
