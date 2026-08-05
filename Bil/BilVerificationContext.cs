using System.Collections.Generic;

namespace LatteCompiler.Bil
{
    // BIL 验证器共享索引与解析工具（M58）：为 BilVerifier 各检查类别提供
    // 模块级符号索引、函数级变量/block 索引、canonical 符号解析与类型引用工具。
    // 只依赖 Bil/ 目录（模型以字符串为符号身份，验证器在此之上重建可解析性）。

    // 模块级索引：资源/类型/成员符号集合与声明反查。重复条目索引取第一个
    // （重复本身由 §21.1/§21.2 检查报错，索引构建不得因此崩溃）。
    internal sealed class BilVerificationContext
    {
        public BilModule Module { get; }

        // 资源（§4.2）：按名索引 + 引用集合（成员资格检查用——操作数持对象
        // 引用，但引用的资源可能不属于本模块）
        public Dictionary<string, BilResource> ResourcesByName { get; } =
            new Dictionary<string, BilResource>();
        public HashSet<BilResource> ResourceSet { get; } =
            new HashSet<BilResource>(ReferenceEqualityComparer.Instance);

        // 类型声明（§8.2，local + external）：符号集合 + 声明反查
        public HashSet<string> TypeSymbols { get; } = new HashSet<string>();
        public Dictionary<string, BilTypeDeclaration> TypeDeclarations { get; } =
            new Dictionary<string, BilTypeDeclaration>();

        // 成员声明（§8.3–§8.5，含类型体成员与 §8.4.1 段内裸条目）
        public HashSet<string> MethodSymbols { get; } = new HashSet<string>();
        public HashSet<string> FieldSymbols { get; } = new HashSet<string>();
        public Dictionary<string, BilSimpleMemberDeclaration> MethodDeclarations { get; } =
            new Dictionary<string, BilSimpleMemberDeclaration>();
        public Dictionary<string, BilSimpleMemberDeclaration> FieldDeclarations { get; } =
            new Dictionary<string, BilSimpleMemberDeclaration>();
        public Dictionary<string, BilCaseDeclaration> CaseDeclarations { get; } =
            new Dictionary<string, BilCaseDeclaration>();

        // LocalSymbols 侧方法声明单独留一份（§9.1：fn 定义必须对应本地声明）
        public HashSet<string> LocalMethodSymbols { get; } = new HashSet<string>();

        // 全部简单成员声明的平铺（带宿主类型符号，null = 段内裸条目）——
        // 供声明侧规则（native/static 一致性/修饰符矩阵）遍历
        public List<(string? OwnerType, BilSimpleMemberDeclaration Declaration, bool IsLocal)>
            MemberEntries { get; } =
                new List<(string?, BilSimpleMemberDeclaration, bool)>();

        public BilVerificationContext(BilModule module)
        {
            Module = module;
            foreach (var resource in module.Resources)
            {
                if (!ResourcesByName.ContainsKey(resource.Name))
                {
                    ResourcesByName.Add(resource.Name, resource);
                }
                ResourceSet.Add(resource);
            }
            IndexSymbolSection(module.LocalSymbols, isLocal: true);
            IndexSymbolSection(module.ExternalSymbols, isLocal: false);
            IndexPredefinedSymbols();
        }

        // 语言预定义符号（与 Semantic BootstrapSymbols 对齐的 BIL 视图）：
        // 类型层级根与基元类型由编译器硬编码构造，从不进 LocalSymbols/
        // ExternalSymbols——验证器把它们当作内建环境，仅补充可解析性
        // （不生成声明对象，声明侧规则与 extends 链检查对其降级通过）
        private void IndexPredefinedSymbols()
        {
            foreach (var type in PredefinedTypes)
            {
                TypeSymbols.Add(type);
            }
            foreach (var method in PredefinedMethods)
            {
                MethodSymbols.Add(method);
            }
        }

        private static readonly string[] PredefinedTypes =
        {
            // 类型层级根（SYNTAX §3.1，含异常根）
            "core::Any", "core::Object", "core::ValueType", "core::Enum",
            "core::Wrapper", "core::Exception",
            // 基元类型 canonical（BIL 别名之外的引用形态，如 ext 方法宿主）
            "core::i8", "core::i16", "core::i32", "core::i64",
            "core::u8", "core::u16", "core::u32", "core::u64",
            "core::float", "core::double", "core::bool", "core::char", "core::String",
            // 泛型内建（§3.1.2 特权类型；BIL 多经 .typeid/.nullable 构造头引用）
            "core::Type", "core::Span", "core::Nullable", "core::Box",
        };

        private static readonly string[] PredefinedMethods =
        {
            // toString 机制（SYNTAX §3.8）：Any 接口承诺 + Object open native
            // 默认实现（RUNTIME §26/BIL §22.5 内建 hook）
            "core::Any$toString()@.string",
            "core::Object$toString()@.string",
        };

        private void IndexSymbolSection(List<BilSymbolSectionEntry> section, bool isLocal)
        {
            foreach (var entry in section)
            {
                switch (entry)
                {
                    case BilTypeDeclaration type:
                        TypeSymbols.Add(type.Symbol);
                        if (!TypeDeclarations.ContainsKey(type.Symbol))
                        {
                            TypeDeclarations.Add(type.Symbol, type);
                        }
                        foreach (var member in type.Members)
                        {
                            IndexMember(type.Symbol, member, isLocal);
                        }
                        break;
                    case BilMemberDeclaration member:
                        IndexMember(null, member, isLocal);
                        break;
                }
            }
        }

        private void IndexMember(string? ownerType, BilMemberDeclaration member, bool isLocal)
        {
            switch (member)
            {
                case BilSimpleMemberDeclaration simple:
                    MemberEntries.Add((ownerType, simple, isLocal));
                    if (simple.Kind is BilMemberKind.Method or BilMemberKind.StaticMethod)
                    {
                        MethodSymbols.Add(simple.Symbol);
                        if (isLocal)
                        {
                            LocalMethodSymbols.Add(simple.Symbol);
                        }
                        if (!MethodDeclarations.ContainsKey(simple.Symbol))
                        {
                            MethodDeclarations.Add(simple.Symbol, simple);
                        }
                    }
                    else
                    {
                        FieldSymbols.Add(simple.Symbol);
                        if (!FieldDeclarations.ContainsKey(simple.Symbol))
                        {
                            FieldDeclarations.Add(simple.Symbol, simple);
                        }
                    }
                    break;
                case BilCaseDeclaration caseDeclaration:
                    if (!CaseDeclarations.ContainsKey(caseDeclaration.QualifiedName))
                    {
                        CaseDeclarations.Add(caseDeclaration.QualifiedName, caseDeclaration);
                    }
                    break;
            }
        }

        // ===== §6 类型引用工具 =====

        // §6.2 固定内建类型 + §6.3 无边界的 .typeid（≡ .typeid<.any>）
        private static readonly HashSet<string> BuiltinTypes = new HashSet<string>
        {
            ".void",
            ".i8", ".i16", ".i32", ".i64",
            ".u8", ".u16", ".u32", ".u64",
            ".f32", ".f64",
            ".bool", ".char", ".string",
            ".any", ".object", ".valuetype",
            ".breakid", ".typeid",
        };

        // §6.3 标准类型构造头（构造形式的类型引用免检声明，基类型递归检查）
        private static readonly string[] TypeConstructors =
        {
            ".array<", ".map<", ".pair<", ".nullable<",
            ".typeid<", ".fieldid<", ".methodid<", ".generic<",
        };

        public static bool IsBuiltinType(string typeRef) => BuiltinTypes.Contains(typeRef);

        // 类型引用可解析（§21.2）：内建 / 构造形式（.generic<...> 内部为
        // typeid 位置表达式，免检；其余构造头递归检查基类型）/ 用户 canonical
        // 类型（剥泛型实参后的基名 ∈ 类型符号集合）
        public bool IsResolvableTypeRef(string typeRef)
        {
            if (IsBuiltinType(typeRef))
            {
                return true;
            }
            foreach (var constructor in TypeConstructors)
            {
                if (typeRef.StartsWith(constructor) && typeRef.EndsWith(">"))
                {
                    if (constructor == ".generic<")
                    {
                        return true;
                    }
                    var inner = typeRef.Substring(
                        constructor.Length, typeRef.Length - constructor.Length - 1);
                    foreach (var part in SplitTopLevel(inner))
                    {
                        if (!IsResolvableTypeRef(part))
                        {
                            return false;
                        }
                    }
                    return true;
                }
            }
            return TypeSymbols.Contains(StripTypeArguments(typeRef));
        }

        // 剥泛型实参后缀（"com.example::Box<.i32>" → "com.example::Box"）——
        // 反查类型声明用（声明符号不带实参）
        public static string StripTypeArguments(string typeRef)
        {
            var angle = typeRef.IndexOf('<');
            return angle >= 0 ? typeRef.Substring(0, angle) : typeRef;
        }

        // 类型引用基名：剥泛型实参与命名空间限定与前导点
        // （"core.collections::IEnumerator<.i32>" → "IEnumerator"）
        public static string TypeBaseName(string typeRef)
        {
            var name = typeRef;
            var angle = name.IndexOf('<');
            if (angle >= 0)
            {
                name = name.Substring(0, angle);
            }
            var ns = name.LastIndexOf("::");
            if (ns >= 0)
            {
                name = name.Substring(ns + 2);
            }
            return name.TrimStart('.');
        }

        // 类型兼容判定（防误报降级）：文本相等；任一侧含 .generic<（typeid
        // 位置表达式，无法静态判定）；剥基名大小写不敏感相等（内建别名 ↔
        // canonical：".i32" ↔ "core::i32"、".any" ↔ "core::Any"）
        public static bool TypesCompatible(string actual, string expected)
        {
            if (actual == expected)
            {
                return true;
            }
            if (actual.Contains(".generic<") || expected.Contains(".generic<"))
            {
                return true;
            }
            return string.Equals(TypeBaseName(actual), TypeBaseName(expected),
                System.StringComparison.OrdinalIgnoreCase);
        }

        // 资源的值类型（§19；无法判定的形态返回 null——调用方跳过严格匹配）：
        // 标量 → 对应内建类型；null 资源 → .nullable<T>；raw.hex/raw.bin 与
        // 复合资源（发射器尚未产出 load）跳过
        public static string? ResourceValueType(BilResource resource)
        {
            switch (resource)
            {
                case BilScalarResource scalar:
                    if (scalar.Type is BilScalarType.RawHex or BilScalarType.RawBin)
                    {
                        return null;
                    }
                    return "." + BilSpellings.Of(scalar.Type);
                case BilNullResource nullResource:
                    return ".nullable<" + nullResource.TypeRef + ">";
                default:
                    return null;
            }
        }

        // 赋值兼容的宿主判定（§13.3/§15.1：字段/方法的宿主对象可以是
        // owner 的派生类或接口实现——BIL 的视图一致性靠 §6.5 cast 表达，
        // 但继承字段访问与继承方法调用直接以前端上色后的符号发射）。
        // 沿 extends/implements 链判定；查不到声明或链断降级通过
        public bool IsAssignableTo(string typeRef, string ownerRef)
        {
            if (TypesCompatible(typeRef, ownerRef))
            {
                return true;
            }
            var visited = new HashSet<string>();
            var pending = new Stack<string>();
            pending.Push(StripTypeArguments(typeRef));
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!visited.Add(current) || !TypeDeclarations.TryGetValue(current, out var declaration))
                {
                    continue;   // 链断：该支降级（不继续追溯）
                }
                if (declaration.ExtendsType != null)
                {
                    if (TypesCompatible(declaration.ExtendsType, ownerRef))
                    {
                        return true;
                    }
                    pending.Push(StripTypeArguments(declaration.ExtendsType));
                }
                foreach (var interfaceType in declaration.ImplementsTypes)
                {
                    if (TypesCompatible(interfaceType, ownerRef))
                    {
                        return true;
                    }
                    pending.Push(StripTypeArguments(interfaceType));
                }
            }
            // 全程未命中：若起点本身查不到声明则属降级场景，否则确实不可赋值
            return !TypeDeclarations.ContainsKey(StripTypeArguments(typeRef));
        }

        // ===== §5.2 canonical 符号解析 =====

        // 顶层逗号切分（按 <> 深度；方法参数/泛型实参列表共用）
        public static List<string> SplitTopLevel(string text)
        {
            var parts = new List<string>();
            var depth = 0;
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                switch (text[i])
                {
                    case '<': depth++; break;
                    case '>': depth--; break;
                    case ',' when depth == 0:
                        parts.Add(text.Substring(start, i - start).Trim());
                        start = i + 1;
                        break;
                }
            }
            var tail = text.Substring(start).Trim();
            if (tail.Length > 0)
            {
                parts.Add(tail);
            }
            return parts;
        }

        // 方法符号解析：`[ns::][Owner]$[.static.]name(p:T,...)@Ret` 与
        // 运算符形态 `$$name(...)`、访问器形态 `$[.static].get.名@T` /
        // `$[.static].set.名@T`（无参数段，§5.2——访问器的 @T 对 getter
        // 是返回类型、对 setter 是 value 参数类型，形态语义由调用方经
        // TryParseAccessorForm 区分）。失败返回 false（malformed 由符号
        // 检查另报，解析器保持全兜底）。
        public static bool TryParseMethodSymbol(string symbol,
            out string owner, out bool isStatic,
            out List<(string Name, string TypeRef)> parameters, out string returnType)
        {
            owner = "";
            isStatic = false;
            parameters = new List<(string, string)>();
            returnType = "";
            var dollar = symbol.IndexOf('$');
            if (dollar < 0)
            {
                return false;
            }
            owner = symbol.Substring(0, dollar);
            var rest = symbol.Substring(dollar + 1);
            if (rest.StartsWith("$"))
            {
                rest = rest.Substring(1);   // $$ 运算符形态
            }
            if (rest.StartsWith(".static."))
            {
                isStatic = true;
                rest = rest.Substring(".static.".Length);
            }
            var openParen = rest.IndexOf('(');
            if (openParen < 0)
            {
                // 无参数段：仅访问器形态合法（.get.名@T / .set.名@T）
                if (!rest.StartsWith(".get.") && !rest.StartsWith(".set."))
                {
                    return false;
                }
                var at = rest.LastIndexOf('@');
                if (at < 0)
                {
                    return false;
                }
                returnType = rest.Substring(at + 1);
                return returnType.Length > 0;
            }
            var depth = 0;
            var closeParen = -1;
            for (var i = openParen; i < rest.Length; i++)
            {
                if (rest[i] == '(') depth++;
                if (rest[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeParen = i;
                        break;
                    }
                }
            }
            if (closeParen < 0 || closeParen + 1 >= rest.Length || rest[closeParen + 1] != '@')
            {
                return false;
            }
            returnType = rest.Substring(closeParen + 2);
            var parameterText = rest.Substring(openParen + 1, closeParen - openParen - 1);
            foreach (var part in SplitTopLevel(parameterText))
            {
                var colon = part.IndexOf(':');
                if (colon < 0)
                {
                    return false;
                }
                parameters.Add((part.Substring(0, colon).Trim(), part.Substring(colon + 1).Trim()));
            }
            return true;
        }

        // 字段符号解析：`[ns::][Owner]#[.static.]name@Type`（§5.2/§5.3：
        // 最后一个 @ 分隔字段类型，.wrapper. 全名内可能含 @ 之外的任意内容）
        public static bool TryParseFieldSymbol(string symbol,
            out string owner, out bool isStatic, out string fieldType)
        {
            owner = "";
            isStatic = false;
            fieldType = "";
            var hash = symbol.IndexOf('#');
            var at = symbol.LastIndexOf('@');
            if (hash < 0 || at < 0 || at < hash)
            {
                return false;
            }
            owner = symbol.Substring(0, hash);
            var name = symbol.Substring(hash + 1, at - hash - 1);
            isStatic = name.StartsWith(".static.");
            fieldType = symbol.Substring(at + 1);
            return true;
        }

        // 访问器符号形态判定（§5.2：完整 canonical 方法符号中
        // `$[.static].get.名` / `$[.static].set.名` 形态）；命中时
        // isSetter 给出 getter/setter 二态
        public static bool TryParseAccessorForm(string symbol, out bool isSetter)
        {
            isSetter = false;
            var dollar = symbol.IndexOf('$');
            if (dollar < 0)
            {
                return false;
            }
            var rest = symbol.Substring(dollar + 1);
            if (rest.StartsWith(".static."))
            {
                rest = rest.Substring(".static.".Length);
            }
            if (rest.StartsWith(".set."))
            {
                isSetter = true;
                return true;
            }
            return rest.StartsWith(".get.");
        }
    }

    // 函数级索引与遍历：变量类型环境（.args 除 .return + .vars）、.breakid
    // 变量集合、block 成员资格集合、返回类型；全指令深度优先枚举（沿块引用
    // 下钻，visited 防结构环死循环——环本身由 §21.5 检查报错）。
    internal sealed class BilFunctionContext
    {
        public BilVerificationContext Module { get; }
        public BilFunction Function { get; }

        public Dictionary<string, string> VariableTypes { get; } = new Dictionary<string, string>();
        public HashSet<string> BreakIdVariables { get; } = new HashSet<string>();
        public HashSet<BilBlock> BlockSet { get; } =
            new HashSet<BilBlock>(ReferenceEqualityComparer.Instance);
        public string? ReturnType { get; }

        public BilFunctionContext(BilVerificationContext module, BilFunction function)
        {
            Module = module;
            Function = function;
            foreach (var arg in function.Args)
            {
                if (arg.Name == ".return")
                {
                    continue;
                }
                if (!VariableTypes.ContainsKey(arg.Name))
                {
                    VariableTypes.Add(arg.Name, arg.TypeRef);
                }
            }
            foreach (var variable in function.Vars)
            {
                if (!VariableTypes.ContainsKey(variable.Name))
                {
                    VariableTypes.Add(variable.Name, variable.TypeRef);
                }
                if (variable.TypeRef == ".breakid")
                {
                    BreakIdVariables.Add(variable.Name);
                }
            }
            foreach (var block in function.Blocks)
            {
                BlockSet.Add(block);
            }
            foreach (var arg in function.Args)
            {
                if (arg.Name == ".return")
                {
                    ReturnType = arg.TypeRef;
                    break;
                }
            }
        }

        // fn 内全部指令枚举：fn.Blocks 各块出发沿块引用下钻（块不嵌套定义，
        // 指令嵌套引用顶层块）；visited 以引用相等判重，结构环只展开一次
        public IEnumerable<(BilBlock Block, BilInstruction Instruction)> AllInstructions()
        {
            var visited = new HashSet<BilBlock>(ReferenceEqualityComparer.Instance);
            foreach (var block in Function.Blocks)
            {
                foreach (var item in EnumerateBlock(block, visited))
                {
                    yield return item;
                }
            }
        }

        private static IEnumerable<(BilBlock, BilInstruction)> EnumerateBlock(
            BilBlock block, HashSet<BilBlock> visited)
        {
            if (!visited.Add(block))
            {
                yield break;
            }
            foreach (var instruction in block.Instructions)
            {
                yield return (block, instruction);
                foreach (var referenced in BilVerifier.ReferencedBlocks(instruction))
                {
                    foreach (var item in EnumerateBlock(referenced, visited))
                    {
                        yield return item;
                    }
                }
            }
        }
    }
}
