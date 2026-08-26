using System.Collections.Generic;
using RigiCompiler.Bil;

namespace RigiCompiler.Middleware.Symbols
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
        // enum case（仅 enum struct 非空；§8.5，MW4 起登记）
        public IReadOnlyList<MwCaseSymbol> Cases { get; }

        internal MwTypeSymbol(BilTypeDeclaration declaration, bool isExternal,
            IReadOnlyList<MwMemberSymbol> members, IReadOnlyList<MwCaseSymbol> cases)
            : base(declaration.Symbol)
        {
            Declaration = declaration;
            IsExternal = isExternal;
            Members = members;
            Cases = cases;
        }

        // 构造类型计划的独立符号（canonical = 闭合构造形态；成员/声明复用模板）
        internal MwTypeSymbol(string constructedCanonical, MwTypeSymbol template)
            : base(constructedCanonical)
        {
            Declaration = template.Declaration;
            IsExternal = template.IsExternal;
            Members = template.Members;
            Cases = template.Cases;
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

        // 声明修饰符查询（entrypoint/native 等关键字修饰符的存在性判定）
        public bool HasKeyword(BilKeyword keyword)
        {
            foreach (var modifier in Declaration.Modifiers)
            {
                if (modifier is BilKeywordModifier keywordModifier && keywordModifier.Keyword == keyword)
                {
                    return true;
                }
            }
            return false;
        }

        // 虚槽签名键：名 + 参数段（不含返回类型与宿主）——
        // Dog$speak()@.string → speak()；override/槽匹配/接口实现查找用
        public string SignatureKey
        {
            get
            {
                var dollar = Canonical.IndexOf('$');
                var at = Canonical.IndexOf('@');
                if (dollar < 0 || at < 0 || at < dollar)
                {
                    throw new CompilerInternalException($"方法符号形状非法: {Canonical}");
                }
                return Canonical.Substring(dollar + 1, at - dollar - 1);
            }
        }

        // vtable 成员资格（VM VmTypeSheet.IsVirtualMember 同口径，MW4）：
        // 实例方法；排除 init/ext/static 与除 $$call 外的运算符（callable
        // 协议例外：operator call 可 override，入表）。Binding 的派发分类
        // 与 Layout 的 vtable 槽分配共用此判定
        public bool IsVirtualMember
        {
            get
            {
                if (Declaration.Kind != BilMemberKind.Method
                    || HasKeyword(BilKeyword.Init) || HasKeyword(BilKeyword.Ext))
                {
                    return false;
                }
                var dollar = Canonical.IndexOf('$');
                if (dollar < 0)
                {
                    return false;
                }
                var rest = Canonical.Substring(dollar + 1);
                if (rest.StartsWith(".static.", System.StringComparison.Ordinal))
                {
                    return false;
                }
                if (rest.Length > 0 && rest[0] == '$')
                {
                    return rest.StartsWith("$call(", System.StringComparison.Ordinal);
                }
                return true;
            }
        }
    }

    /// <summary>
    /// enum case 符号（BIL §8.5，MW4 布局/判别值表用）：挂在宿主 enum
    /// struct 的 Cases 下（不进成员表——case 不是可调用/可寻址成员）。
    /// 判别值在登记期解析定值：auto = 声明序从 0；res(R) = 非负整数标量
    /// 资源的字面量值。
    /// </summary>
    public sealed class MwCaseSymbol : MwSymbol
    {
        public BilCaseDeclaration Declaration { get; }
        public MwTypeSymbol Owner { get; }
        public uint Discriminant { get; }
        // 洞签名（名:类型 保序；无洞为空表）
        public IReadOnlyList<(string Name, string TypeRef)> Parameters { get; }

        internal MwCaseSymbol(BilCaseDeclaration declaration, MwTypeSymbol owner,
            uint discriminant)
            : base(CanonicalOf(declaration))
        {
            Declaration = declaration;
            Owner = owner;
            Discriminant = discriminant;
            var parameters = new List<(string, string)>(declaration.Parameters.Count);
            foreach (var parameter in declaration.Parameters)
            {
                parameters.Add((parameter.Name, parameter.TypeRef));
            }
            Parameters = parameters;
        }

        // canonical 形态：Direction.North() / Direction.Failed(errorCode:.i32)
        private static string CanonicalOf(BilCaseDeclaration declaration)
        {
            var builder = new System.Text.StringBuilder(declaration.QualifiedName);
            builder.Append('(');
            for (var i = 0; i < declaration.Parameters.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }
                builder.Append(declaration.Parameters[i].Name)
                    .Append(':').Append(declaration.Parameters[i].TypeRef);
            }
            builder.Append(')');
            return builder.ToString();
        }
    }
}
