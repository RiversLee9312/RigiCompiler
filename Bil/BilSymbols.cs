using System.Collections.Generic;

namespace LatteCompiler.Bil
{
    // BIL 符号声明（BIL_STANDARD §8）：LocalSymbols 声明本程序集定义的
    // 类型及成员；ExternalSymbols 声明使用但由他处定义的符号（§8.6：
    // 含验证调用与访问所需的完整语义签名，形态与本地声明相同）。

    // 类型声明（§8.2）：
    // .type TYPE_SYMBOL = kind [extends BASE] [implements I, ...] [modifiers...] { ... }
    // （generic(...) 子句随 S9 泛型落地增补）
    public sealed class BilTypeDeclaration
    {
        public string Symbol { get; }
        // kind 拼写（§8.2）：class / struct / enum-struct / interface / wrapper
        public string Kind { get; }
        public string? ExtendsType { get; set; }
        public List<string> ImplementsTypes { get; } = new List<string>();
        // 修饰符（§8.2：pub protected internal priv / open abstract singleton /
        // rich shared；合法性由 frontend 与 verifier 各自对照 SYNTAX 检查）
        public List<string> Modifiers { get; } = new List<string>();
        public List<BilMemberDeclaration> Members { get; } = new List<BilMemberDeclaration>();

        public BilTypeDeclaration(string symbol, string kind, params string[] modifiers)
        {
            Symbol = symbol;
            Kind = kind;
            Modifiers.AddRange(modifiers);
        }
    }

    public abstract class BilMemberDeclaration
    {
    }

    // 字段与方法声明共形态（§8.3/§8.4）：keyword + canonical symbol + 修饰符。
    // Keyword 取 .field / .static-field / .method / .static-method——声明关键字
    // 与符号中 .static. 标记的一致性由生成方保证（verifier 复核）
    public sealed class BilSimpleMemberDeclaration : BilMemberDeclaration
    {
        public string Keyword { get; }
        public string Symbol { get; }
        // 修饰符（§8.3：pub.../const var/ext/backing computed/readable writable/
        // compiler-generated；§8.4：pub.../static ext override abstract/async
        // entrypoint/init/operator(...)/getter(...)/setter(...)/enum-case(...)/
        // wrapper-proxy(...)）
        public IReadOnlyList<string> Modifiers { get; }
        // §19 wrapper 隐藏字段示例形态：符号与修饰符分两行（数据驱动，
        // writer 不猜列宽阈值）
        public bool ModifiersOnNextLine { get; }

        public BilSimpleMemberDeclaration(string keyword, string symbol,
            IReadOnlyList<string>? modifiers = null, bool modifiersOnNextLine = false)
        {
            Keyword = keyword;
            Symbol = symbol;
            Modifiers = modifiers ?? new List<string>();
            ModifiersOnNextLine = modifiersOnNextLine;
        }
    }

    // enum case 声明（§8.5）：
    // .case ENUM_TYPE_SYMBOL.CaseName(PARAM_NAME: PARAM_TYPE, ...) discriminant auto|res(R)
    public sealed class BilCaseDeclaration : BilMemberDeclaration
    {
        // 源码类型限定全名（frontend 已把 `.Failed` 省略写法解析为完整形态）
        public string QualifiedName { get; }
        public IReadOnlyList<BilCaseParameter> Parameters { get; }
        // 判别值资源；null = discriminant auto
        public string? DiscriminantResource { get; }

        public BilCaseDeclaration(string qualifiedName,
            IReadOnlyList<BilCaseParameter>? parameters = null, string? discriminantResource = null)
        {
            QualifiedName = qualifiedName;
            Parameters = parameters ?? new List<BilCaseParameter>();
            DiscriminantResource = discriminantResource;
        }
    }

    public readonly struct BilCaseParameter
    {
        public string Name { get; }
        public string TypeRef { get; }

        public BilCaseParameter(string name, string typeRef)
        {
            Name = name;
            TypeRef = typeRef;
        }
    }
}
