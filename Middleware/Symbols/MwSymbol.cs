using System.Collections.Generic;
using RigiCompiler.Bil;

namespace RigiCompiler.Middleware
{
    /// <summary>
    /// Middleware 驻留符号基类（MIDDLEWARE_ARCHITECTURE §3 MW1）：BIL canonical
    /// 字符串经 MwSymbolTable intern 为对象，引用相等即身份相等（与中端符号图
    /// 同一纪律）。BIL 侧「以字符串为身份」的约定在 Gate 之后收敛为本对象图。
    /// </summary>
    public abstract class MwSymbol
    {
        // canonical 符号文本（BIL §5）：类型完整名 / 成员 canonical symbol
        public string Canonical { get; }

        private protected MwSymbol(string canonical)
        {
            Canonical = canonical;
        }

        public override string ToString() => Canonical;
    }

    /// <summary>
    /// 类型符号：对应 BIL .type 声明。IsExternal 标记仅外部引用（本地无定义）。
    /// </summary>
    public sealed class MwTypeSymbol : MwSymbol
    {
        public BilTypeDeclaration Declaration { get; }
        public bool IsExternal { get; }
        public IReadOnlyList<MwMemberSymbol> Members { get; }

        internal MwTypeSymbol(BilTypeDeclaration declaration, bool isExternal,
            IReadOnlyList<MwMemberSymbol> members)
            : base(declaration.Symbol)
        {
            Declaration = declaration;
            IsExternal = isExternal;
            Members = members;
        }
    }

    /// <summary>
    /// 成员符号：字段/方法声明（BilSimpleMemberDeclaration 共形态，BIL §8.3/§8.4）。
    /// Owner 为 null 表示全局成员（§8.4.1 全局函数/全局字段）。
    /// </summary>
    public sealed class MwMemberSymbol : MwSymbol
    {
        public BilSimpleMemberDeclaration Declaration { get; }
        public MwTypeSymbol? Owner { get; }
        public bool IsExternal { get; }

        internal MwMemberSymbol(BilSimpleMemberDeclaration declaration,
            MwTypeSymbol? owner, bool isExternal)
            : base(declaration.Symbol)
        {
            Declaration = declaration;
            Owner = owner;
            IsExternal = isExternal;
        }
    }
}
