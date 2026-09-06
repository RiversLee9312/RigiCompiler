using System.Collections.Generic;

namespace RigiCompiler.Bil
{
    // BIL 符号声明（BIL_STANDARD §8）：LocalSymbols 声明本程序集定义的
    // 类型及成员；ExternalSymbols 声明使用但由他处定义的符号（§8.6：
    // 含验证调用与访问所需的完整语义签名，形态与本地声明相同）。
    // M57 起种类/修饰符为强类型（枚举 + BilModifier 子类），拼写
    // 唯一定义在 BilSpellings/各 Render 实现。

    // 符号段条目（§8.4.1）：LocalSymbols/ExternalSymbols 段内的条目——
    // 可以是类型声明（.type ... { ... }），也可以是不属于任何类型的
    // 全局函数/全局字段的裸成员声明（裸 .method/.field 直接出现在段内，
    // 不包裹在 .type 中）；两类条目按生成器输出顺序排列
    public abstract class BilSymbolSectionEntry
    {
    }

    // §8.2 类型种类
    public enum BilTypeKind
    {
        Class, Struct, EnumStruct, Interface, Wrapper,
    }

    // §8.3/§8.4 成员声明关键字（字段/方法 × 实例/静态）
    public enum BilMemberKind
    {
        Field, StaticField, Method, StaticMethod,
    }

    // §8.2/§8.3/§8.4 访问修饰符（BIL 是显式 IR——访问全显式输出）
    public enum BilAccessibility
    {
        Public, Protected, Internal, Private,
    }

    // §8.2/§8.3/§8.4 关键字修饰符（无参形态）
    public enum BilKeyword
    {
        Open, Abstract, Singleton, Rich, Shared,          // §8.2 类型
        Ext, Init, Native, Entrypoint,                    // §8.3/§8.4 成员
        Const, Var, Backing, Computed, Readable, Writable,
        CompilerGenerated, Override, Async, Unsafe,
    }

    // §8.4 访问器类别（getter(FIELD)/setter(FIELD) 修饰符的二态）
    public enum BilAccessorKind
    {
        Getter, Setter,
    }

    // §8.4 wrapper-proxy(PROXY_KIND) 修饰符的两态（M88）：
    // specific = 具名 proxy 模板（.proxy.<成员> / .proxy.get.<名> 等）；
    // wildcard = 类别通配 proxy 模板（.proxy.* / .proxy.get.* 等）。
    // 烘焙特化/original/router 归 Middleware，BIL 文本只承载模板
    public enum BilProxyKind
    {
        Specific, Wildcard,
    }

    // 声明修饰符基类（§8.2 类型修饰符 / §8.3 字段修饰符 / §8.4 方法
    // 修饰符）；带参形态 getter(...)/setter(...) 随 S8e 落地，
    // wrapper-proxy(...) 随 S11d 落地，enum-case(...) 随 S14 落地增补
    public abstract class BilModifier
    {
        internal abstract string Render();
    }

    // 访问修饰符（pub/protected/internal/priv）
    public sealed class BilAccessibilityModifier : BilModifier
    {
        public BilAccessibility Accessibility { get; }

        public BilAccessibilityModifier(BilAccessibility accessibility)
        {
            Accessibility = accessibility;
        }

        internal override string Render() => BilSpellings.Of(Accessibility);
    }

    // 关键字修饰符（open/ext/init/native/...）
    public sealed class BilKeywordModifier : BilModifier
    {
        public BilKeyword Keyword { get; }

        public BilKeywordModifier(BilKeyword keyword)
        {
            Keyword = keyword;
        }

        internal override string Render() => BilSpellings.Of(Keyword);
    }

    // operator(名) 修饰符（§8.4：operator 方法标记）
    public sealed class BilOperatorModifier : BilModifier
    {
        public string Name { get; }

        public BilOperatorModifier(string name)
        {
            Name = name;
        }

        internal override string Render() => $"operator({Name})";
    }

    // getter(字段)/setter(字段) 修饰符（§8.4：访问器标记，参数为关联的
    // 逻辑字段 canonical 符号——Bil/ 对 Semantic 零依赖，字符串身份）
    public sealed class BilAccessorModifier : BilModifier
    {
        public BilAccessorKind Kind { get; }
        public string FieldSymbol { get; }

        public BilAccessorModifier(BilAccessorKind kind, string fieldSymbol)
        {
            Kind = kind;
            FieldSymbol = fieldSymbol;
        }

        internal override string Render() => $"{BilSpellings.Of(Kind)}({FieldSymbol})";
    }

    // symbol("...") 修饰符（§8.4：native 符号名，必须与 native 同现）
    public sealed class BilNativeSymbolModifier : BilModifier
    {
        public string Symbol { get; }

        public BilNativeSymbolModifier(string symbol)
        {
            Symbol = symbol;
        }

        internal override string Render() => $"symbol(\"{Symbol}\")";
    }

    // lib("...") 修饰符（§8.4：native 库名，必须与 native 同现）
    public sealed class BilNativeLibraryModifier : BilModifier
    {
        public string Library { get; }

        public BilNativeLibraryModifier(string library)
        {
            Library = library;
        }

        internal override string Render() => $"lib(\"{Library}\")";
    }

    // wrapper-proxy(PROXY_KIND) 修饰符（§8.4，M88）：wrapper 类型内的
    // `.proxy.*` 模板 fn 标记（体内可出现 invoke fn(..inner)/get.self；kind 与
    // 成员形状类别的一致性由 verifier §21.8 复核）
    public sealed class BilWrapperProxyModifier : BilModifier
    {
        public BilProxyKind Kind { get; }

        public BilWrapperProxyModifier(BilProxyKind kind)
        {
            Kind = kind;
        }

        internal override string Render() => $"wrapper-proxy({BilSpellings.Of(Kind)})";
    }

    // wrapped(WRAPPER_TYPE_REF) 修饰符（§8.3.1，M88）：类型/字段声明上的
    // wrapper 应用标记（可重复，声明序 = outer→inner；存储合成归 Middleware）
    public sealed class BilWrappedModifier : BilModifier
    {
        public string WrapperTypeRef { get; }

        public BilWrappedModifier(string wrapperTypeRef)
        {
            WrapperTypeRef = wrapperTypeRef;
        }

        internal override string Render() => $"wrapped({WrapperTypeRef})";
    }

    // 类型声明（§8.2）：
    // .type TYPE_SYMBOL = kind [generic(T1, out T2)] [extends BASE] [implements I, ...]
    //     [modifiers...] { ... }
    // generic(...) 子句（S9e 定稿）：泛型参数名逗号列表（声明序；BIL
    // 只声明名称——约束是编译期概念，使用侧已由编译器检查，运行时
    // 不携带）
    public sealed class BilTypeDeclaration : BilSymbolSectionEntry
    {
        public string Symbol { get; }
        public BilTypeKind Kind { get; }
        public string? ExtendsType { get; set; }
        public List<string> ImplementsTypes { get; } = new List<string>();
        public List<string> GenericParameters { get; } = new List<string>();
        public List<BilGenericVariance> GenericVariances { get; } = new List<BilGenericVariance>();
        // 修饰符（§8.2：pub protected internal priv / open abstract singleton /
        // rich shared；合法性由 frontend 与 verifier 各自对照 SYNTAX 检查）
        public List<BilModifier> Modifiers { get; } = new List<BilModifier>();
        public List<BilMemberDeclaration> Members { get; } = new List<BilMemberDeclaration>();

        public BilTypeDeclaration(string symbol, BilTypeKind kind, params BilModifier[] modifiers)
        {
            Symbol = symbol;
            Kind = kind;
            Modifiers.AddRange(modifiers);
        }
    }

    public enum BilGenericVariance
    {
        None,
        Out,
        In,
    }

    // 成员声明基类：既可出现在类型体 Members 中（§8.3/§8.4），也可作为
    // 段内裸条目（§8.4.1：全局函数/全局字段声明）
    public abstract class BilMemberDeclaration : BilSymbolSectionEntry
    {
    }

    // 字段与方法声明共形态（§8.3/§8.4）：keyword + canonical symbol + 修饰符。
    // Kind 与符号中 .static. 标记的一致性由生成方保证（verifier 复核）
    public sealed class BilSimpleMemberDeclaration : BilMemberDeclaration
    {
        public BilMemberKind Kind { get; }
        public string Symbol { get; }
        // 修饰符（§8.3：pub.../const var/ext/backing computed/readable writable/
        // compiler-generated；§8.4：pub.../ext override abstract/async
        // entrypoint/init/native/symbol(...)/lib(...)/operator(...)/getter(...)/
        // setter(...)/enum-case(...)/wrapper-proxy(...)）
        public IReadOnlyList<BilModifier> Modifiers { get; }
        // §20 wrapper 隐藏字段示例形态：符号与修饰符分两行（数据驱动，
        // writer 不猜列宽阈值）
        public bool ModifiersOnNextLine { get; }

        public BilSimpleMemberDeclaration(BilMemberKind kind, string symbol,
            IReadOnlyList<BilModifier>? modifiers = null, bool modifiersOnNextLine = false)
        {
            Kind = kind;
            Symbol = symbol;
            Modifiers = modifiers ?? new List<BilModifier>();
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
