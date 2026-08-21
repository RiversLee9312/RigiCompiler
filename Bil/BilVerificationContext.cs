using System.Collections.Generic;

namespace RigiCompiler.Bil
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

        // 全部简单成员声明的平铺（带宿主类型反查键——符号 + 泛型元数，
        // null = 段内裸条目）——供声明侧规则（native/static 一致性/修饰符
        // 矩阵）遍历与宿主声明反查
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
            foreach (var field in PredefinedFields)
            {
                FieldSymbols.Add(field);
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
            // toString 机制（SYNTAX §3.8 修订）：Any open 承诺 + Object
            // open override 默认实现——均非 native 成员，默认体是编译器合成
            // fn（调 .bootstrap.rg 的 priv 全局 native any_to_string，
            // RUNTIME §26/BIL §22.5 内建 hook 经该全局函数触达）
            "core::Any$toString()@.string",
            "core::Object$toString()@.string",
            // 异常根 getMessage（S10，SYNTAX §8.1）：bootstrap 抽象方法不
            // 落地符号段（EmitTypeTree 跳过 IsBuiltin），但调用点若以
            // core::Exception 静态类型 invoke（catch 到 Exception 基类型）
            // 仍需要可解析；具体子类 override 已各自发射 fn 定义
            "core::Exception$getMessage()@.string",
            // S11e（BIL §15.4）：Any.call??? 链末默认实现——bootstrap 内建
            // 宿主不进 LocalSymbols（EmitTypeTree 跳过 IsBuiltin），其合成
            // 成员 fn 定义已平铺发射（P3 阶段 2.6 绑体：throw new
            // NoSuchMethodException(symbol)），调用点 invoke（降级特化链末
            // 环）需要可解析。签名 = 非泛型胖值 ABI（SYNTAX §14.7：
            // symbol + 具名包 Array<Pair<String, Any>> + 位置包 Array<Any>
            // → Any），与 SynthesizeFatSymbol 发射形态逐字符一致
            "core::Any$call???(symbol:.string," +
                "namedArgs:.array<core::Pair<.string,.any>>," +
                "unnamedArgs:.array<.any>)@.any",
        };

        // 预定义字段（bootstrap 符号不声明的成员面，S10）：异常根 message
        // 字段——异常子类 init 体发射 set.field 引用它（SYNTAX §8.1）
        private static readonly string[] PredefinedFields =
        {
            "core::Exception#message@.string",
            // Array.length（V2.5，RUNTIME §26）：bootstrap const 字段，
            // 内建类型不进符号段，get.field 需要可解析
            "core::Array#length@.i32",
        };

        private void IndexSymbolSection(List<BilSymbolSectionEntry> section, bool isLocal)
        {
            foreach (var entry in section)
            {
                switch (entry)
                {
                    case BilTypeDeclaration type:
                        TypeSymbols.Add(type.Symbol);
                        // S10：反查键 = 符号 + 泛型元数（与 §21.2 判重键同式）
                        var declarationKey = DeclarationKey(type.Symbol, type.GenericParameters.Count);
                        if (!TypeDeclarations.ContainsKey(declarationKey))
                        {
                            TypeDeclarations.Add(declarationKey, type);
                        }
                        foreach (var member in type.Members)
                        {
                            IndexMember(declarationKey, member, isLocal);
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
            ".cell<", ".readonly_cell<",
            ".typeid<", ".fieldid<", ".generic<",
        };

        public static bool IsBuiltinType(string typeRef) => BuiltinTypes.Contains(typeRef);

        // S11e：canonical 宿主段是否预定义内建类型（core::Any 等，PredefinedTypes
        // 集）——内建类型从不进 LocalSymbols/ExternalSymbols，其编译器合成
        // 成员（如 Any.call??? 默认实现）的 fn 定义无声明可对应，§21.2 的
        // fn↔声明检查对 builtin 宿主豁免（结构性事实，非伪造逃生门）
        public bool IsPredefinedTypeHost(string owner)
        {
            return PredefinedTypes.Contains(owner);
        }

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
                    if (constructor == ".fieldid<")
                    {
                        var fieldIdParts = SplitTopLevel(inner);
                        return fieldIdParts.Count == 3
                            && IsResolvableTypeRef(fieldIdParts[0])
                            && IsResolvableTypeRef(fieldIdParts[1])
                            && (fieldIdParts[2] == "instance" || fieldIdParts[2] == "static");
                    }
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

        // 类型声明反查键 = canonical 符号 + 泛型元数（与 §21.2 判重键同式——
        // S10 起 Task 与 Task\<TResult\> 同名不同元数合法共存，裸符号不再唯一）
        private static string DeclarationKey(string symbol, int arity)
        {
            return arity == 0 ? symbol : symbol + "<" + arity + ">";
        }

        // 类型引用 → 反查键：剥实参后缀取基名 + 顶层实参个数
        // （"com.example::Box<.i32>" → "com.example::Box<1>"）
        public static string DeclarationKeyOf(string typeRef)
        {
            var angle = typeRef.IndexOf('<');
            if (angle < 0 || !typeRef.EndsWith(">"))
            {
                return typeRef;
            }
            var inner = typeRef.Substring(angle + 1, typeRef.Length - angle - 2);
            return DeclarationKey(typeRef.Substring(0, angle), SplitTopLevel(inner).Count);
        }

        // 声明反查：按 StripTypeArguments + 实参元数取声明
        // （查不到 = 声明缺失降级场景，调用方按防误报原则跳过）
        public bool TryGetTypeDeclaration(string typeRef, out BilTypeDeclaration declaration)
        {
            return TypeDeclarations.TryGetValue(DeclarationKeyOf(typeRef), out declaration!);
        }

        // ===== §6.4 类型严格相等判定 =====

        // 内建标量别名 → canonical 内建类型名（SYNTAX §3.2/BIL §6.2：同一
        // 类型的两种拼写，属全等）。CanonicalSymbolPrinter 的类型引用位置
        // 恒投影别名（.i32/.string），符号 owner 段与宿主引用恒 canonical
        // （core::i32/core::String）——验证器跨位置比对须经此表归一
        private static readonly Dictionary<string, string> BuiltinScalarAliases =
            new Dictionary<string, string>
            {
                [".i8"] = "core::i8", [".i16"] = "core::i16",
                [".i32"] = "core::i32", [".i64"] = "core::i64",
                [".u8"] = "core::u8", [".u16"] = "core::u16",
                [".u32"] = "core::u32", [".u64"] = "core::u64",
                [".f32"] = "core::float", [".f64"] = "core::double",
                [".bool"] = "core::bool", [".char"] = "core::char",
                [".string"] = "core::String",
                [".any"] = "core::Any", [".object"] = "core::Object",
                [".valuetype"] = "core::ValueType",
            };

        // 标准构造头 → canonical 泛型宿主（BIL §6.3：构造头是源码特权
        // 类型的 BIL 拼写——.array<T> ≡ core::Array<T> 等，同一类型的
        // 两种拼写，属全等）。.fieldid/.methodid 无源码对应类型、
        // .generic< 是 typeid 位置表达式，均不在此表（形态原样比对）
        private static readonly Dictionary<string, string> ConstructorAliases =
            new Dictionary<string, string>
            {
                [".array"] = "core::Array", [".map"] = "core::Map",
                [".pair"] = "core::Pair", [".nullable"] = "core::Nullable",
                [".cell"] = "core::Cell", [".readonly_cell"] = "core::ReadonlyCell",
                [".typeid"] = "core::Type",
            };

        // 类型引用归一化（TypesCompatible 的唯一比较基）：
        // 1. 内建标量别名 → canonical（.i32 → core::i32、.f32 → core::float 等）；
        // 2. 标准构造头 → canonical 泛型宿主，实参递归归一化（构造类型
        //    全等 = 头 canonical 全等 + 实参个数相同 + 逐实参递归全等）；
        // 3. 无边界 .typeid ≡ .typeid<.any>（§6.3）；
        // 4. 其余形态（用户 canonical 类型/闭合泛型/.fieldid/.methodid 等）
        //    头原样，构造实参仍递归归一化。畸形形态（'<' 不配平）原样
        //    返回——malformed 由符号检查另报，归一化保持全兜底不抛
        public static string NormalizeTypeRef(string typeRef)
        {
            var angle = typeRef.IndexOf('<');
            if (angle < 0 || !typeRef.EndsWith(">"))
            {
                if (typeRef == ".typeid")
                {
                    return ConstructorAliases[".typeid"] + "<" + NormalizeTypeRef(".any") + ">";
                }
                return BuiltinScalarAliases.TryGetValue(typeRef, out var scalar) ? scalar : typeRef;
            }
            var head = typeRef.Substring(0, angle);
            var inner = typeRef.Substring(angle + 1, typeRef.Length - angle - 2);
            var normalizedHead = ConstructorAliases.TryGetValue(head, out var canonical)
                ? canonical : head;
            var arguments = SplitTopLevel(inner);
            var normalized = new List<string>(arguments.Count);
            foreach (var argument in arguments)
            {
                normalized.Add(NormalizeTypeRef(argument));
            }
            return normalizedHead + "<" + string.Join(", ", normalized) + ">";
        }

        // 类型兼容判定（§6.4 严格相等的验证器投影——canonical 全等）：
        // 任一侧含 .generic<（typeid 位置表达式，无法静态判定）降级通过；
        // 其余两侧经 NormalizeTypeRef 归一化后字符串全等——内建标量别名 ↔
        // core:: canonical、标准构造头 ↔ canonical 泛型宿主是同一类型的
        // 两种拼写（属全等），其余一概严格：构造类型实参不同不兼容、不同
        // 命名空间的同名类型不兼容、Wrap ≠ Wrap\<T\>。
        // 协变（in/out 类型参数）归后续里程碑，届时在此放宽
        public static bool TypesCompatible(string actual, string expected)
        {
            if (actual.Contains(".generic<") || expected.Contains(".generic<"))
            {
                return true;
            }
            return NormalizeTypeRef(actual) == NormalizeTypeRef(expected);
        }

        // 调用签名的赋值兼容：在严格 canonical 相等之外，消费 BIL 类型声明中
        // 的 in/out 元数据。索引、wrapper 链等专用形态继续使用上面的严格比较。
        public bool TypesAssignable(string actual, string expected)
        {
            if (actual.Contains(".generic<") || expected.Contains(".generic<")) return true;
            var normalizedActual = NormalizeTypeRef(actual);
            var normalizedExpected = NormalizeTypeRef(expected);
            if (normalizedActual == normalizedExpected) return true;
            // 运行期精确类型 → 声明类型：.null / T 均可赋给 .nullable<T>
            // （is/supers 等可赋值性图；init 匹配已改为静态类型 TypesEqual）
            if (normalizedActual == ".null" && IsNullableType(normalizedExpected, out _))
            {
                return true;
            }
            if (IsNullableType(normalizedExpected, out var nullableInner)
                && TypesAssignable(normalizedActual, nullableInner))
            {
                return true;
            }

            var actualArguments = TypeArgumentsOf(normalizedActual);
            var expectedArguments = TypeArgumentsOf(normalizedExpected);
            if (actualArguments != null && expectedArguments != null
                && StripTypeArguments(normalizedActual) == StripTypeArguments(normalizedExpected)
                && actualArguments.Count == expectedArguments.Count
                && TryGetTypeDeclaration(normalizedActual, out var declaration))
            {
                for (var i = 0; i < actualArguments.Count; i++)
                {
                    var variance = i < declaration.GenericVariances.Count
                        ? declaration.GenericVariances[i]
                        : BilGenericVariance.None;
                    if (variance == BilGenericVariance.Out)
                    {
                        if (!TypesAssignable(actualArguments[i], expectedArguments[i])) return false;
                    }
                    else if (variance == BilGenericVariance.In)
                    {
                        if (!TypesAssignable(expectedArguments[i], actualArguments[i])) return false;
                    }
                    else if (NormalizeTypeRef(actualArguments[i])
                        != NormalizeTypeRef(expectedArguments[i]))
                    {
                        return false;
                    }
                }
                return true;
            }

            return IsNominalAssignable(normalizedActual, normalizedExpected,
                new HashSet<string>(StringComparer.Ordinal));
        }

        private bool IsNominalAssignable(string actual, string expected, HashSet<string> visited)
        {
            if (NormalizeTypeRef(actual) == NormalizeTypeRef(expected)) return true;
            // 构造形态 → 自身开放宿主恒可赋值（擦除方向的 cast：B<.i32> → B；
            // 实参信息多于目标，声明级名义包含即成立——实例方法 receiver 的
            // 擦除 cast（BIL §7）沿 extends 链命中构造基类时经此放行）
            if (TypeArgumentsOf(actual) != null
                && StripTypeArguments(actual) == NormalizeTypeRef(expected)) return true;
            if (!visited.Add(DeclarationKeyOf(actual))) return false;
            if (!TryGetTypeDeclaration(actual, out var declaration)) return false;
            if (declaration.ExtendsType != null
                && TypesAssignable(declaration.ExtendsType, expected)) return true;
            return declaration.ImplementsTypes.Any(iface => TypesAssignable(iface, expected));
        }

        private static List<string>? TypeArgumentsOf(string typeRef)
        {
            var angle = typeRef.IndexOf('<');
            if (angle < 0 || !typeRef.EndsWith(">")) return null;
            return SplitTopLevel(typeRef.Substring(angle + 1, typeRef.Length - angle - 2));
        }

        // 归一化后的 .nullable<T> / core::Nullable<T>
        private static bool IsNullableType(string normalized, out string inner)
        {
            const string alias = "core::Nullable<";
            if (normalized.StartsWith(alias, StringComparison.Ordinal) && normalized.EndsWith(">"))
            {
                inner = normalized.Substring(alias.Length, normalized.Length - alias.Length - 1);
                return true;
            }
            inner = "";
            return false;
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

        // 宿主归属判定（IsAssignableTo 专用）：链节点与 owner 按「定义级」
        // 比较——归一化（构造头别名映射，§6.3）后剥泛型实参全等。§5.2 符号
        // 的宿主段恒为定义级 canonical（不带实参），声明侧 ExtendsType 与
        // 类型引用恒带实参，构造类型的成员符号仍是定义级——归属不是类型
        // 相等（§6.4 的 Wrap ≠ Wrap\<T\> 不适用：Box\<.i32\> 的实例成员
        // 符号宿主即 Box）。归一化须在剥实参之前：cell 隐藏子类的
        // ExtendsType 投影为特权拼写 .cell<.i32>，先剥会使别名表失配
        private static bool HostMatches(string chainNodeType, string ownerRef)
        {
            return StripTypeArguments(NormalizeTypeRef(chainNodeType))
                == StripTypeArguments(NormalizeTypeRef(ownerRef));
        }

        // 沿 extends 链解析「宿主在 owner 定义处的构造形态」（成员签名
        // 泛型代入用）：从 typeRef 出发逐跳 ExtendsType，命中定义级归属
        // 时返回该跳的构造形态（含实参）；链断/查不到声明返回 null
        //（调用方降级——不制造验证器错误）
        public string? ResolveConstructedHostForm(string typeRef, string ownerRef)
        {
            var current = NormalizeTypeRef(typeRef);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (visited.Add(current))
            {
                if (HostMatches(current, ownerRef)) return current;
                if (!TryGetTypeDeclaration(current, out var declaration)
                    || declaration.ExtendsType == null)
                {
                    return null;
                }
                current = NormalizeTypeRef(declaration.ExtendsType);
            }
            return null;
        }

        // 赋值兼容的宿主判定（§13.3/§15.1：字段/方法的宿主对象可以是
        // owner 的派生类或接口实现——BIL 的视图一致性靠 §6.5 cast 表达，
        // 但继承字段访问与继承方法调用直接以前端上色后的符号发射）。
        // 沿 extends/implements 链判定；查不到声明或链断降级通过
        public bool IsAssignableTo(string typeRef, string ownerRef)
        {
            if (HostMatches(typeRef, ownerRef))
            {
                return true;
            }
            var visited = new HashSet<string>();
            var pending = new Stack<string>();
            pending.Push(typeRef);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!visited.Add(DeclarationKeyOf(current))
                    || !TryGetTypeDeclaration(current, out var declaration))
                {
                    continue;   // 链断：该支降级（不继续追溯）
                }
                if (declaration.ExtendsType != null)
                {
                    if (HostMatches(declaration.ExtendsType, ownerRef))
                    {
                        return true;
                    }
                    pending.Push(declaration.ExtendsType);
                }
                foreach (var interfaceType in declaration.ImplementsTypes)
                {
                    if (HostMatches(interfaceType, ownerRef))
                    {
                        return true;
                    }
                    pending.Push(interfaceType);
                }
            }
            // 全程未命中：若起点本身查不到声明则属降级场景，否则确实不可赋值
            return !TypeDeclarations.ContainsKey(DeclarationKeyOf(typeRef));
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

        // §21.8 / §14.3：沿 extends 链收集 enum struct 实例字段（静态字段
        // 不在本规则）。构造形态 `EnumType<...>` 经 DeclarationKeyOf 反查
        // 定义级 Kind（SYNTAX §12 允许泛型 enum struct）；字段类型含
        // `.generic<` 的参数位跳过（验证期无法判定是否 enum）。声明查不到
        // 则该跳降级跳过（防误报）。
        public List<string> CollectEnumStructInstanceFields(string typeRef)
        {
            var result = new List<string>();
            var seen = new HashSet<string>();
            var visited = new HashSet<string>();
            var current = typeRef;
            while (visited.Add(DeclarationKeyOf(current)))
            {
                if (!TryGetTypeDeclaration(current, out var declaration))
                {
                    break;
                }
                foreach (var member in declaration.Members)
                {
                    if (member is not BilSimpleMemberDeclaration simple
                        || simple.Kind != BilMemberKind.Field)
                    {
                        continue;
                    }
                    if (!TryParseFieldSymbol(simple.Symbol, out _, out var isStatic,
                            out var fieldType)
                        || isStatic
                        || !IsEnumStructFieldType(fieldType)
                        || !seen.Add(simple.Symbol))
                    {
                        continue;
                    }
                    result.Add(simple.Symbol);
                }
                if (declaration.ExtendsType == null)
                {
                    break;
                }
                current = declaration.ExtendsType;
            }
            return result;
        }

        // 字段类型是否为 enum struct（精确名或构造形态反查 Kind）
        public bool IsEnumStructFieldType(string typeRef)
        {
            if (typeRef.IndexOf(".generic<", StringComparison.Ordinal) >= 0)
            {
                return false;
            }
            return TryGetTypeDeclaration(typeRef, out var declaration)
                && declaration.Kind == BilTypeKind.EnumStruct;
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
        // 指令嵌套引用顶层块）；visited 以引用相等判重，结构环只展开一次；
        // 下钻过滤块成员资格（与 Flow 的 InstructionsInFunction 一致——跨 fn
        // 越权块不展开，其内部错误不级联）
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

        private IEnumerable<(BilBlock, BilInstruction)> EnumerateBlock(
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
                    if (BlockSet.Contains(referenced))
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
}
