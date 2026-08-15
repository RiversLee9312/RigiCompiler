using System.Globalization;
using System.Text;

namespace RigiCompiler.Bil.Vm
{
    // 执行期上下文（BIL_VM_DESIGN §4 / BIL_STANDARD §22）：
    // 模块、符号索引、hook 表、stdout/stderr 汇；静态字段加锁存储（V2）。

    public sealed class VmContext
    {
        public BilModule Module { get; }
        public VmHooks Hooks { get; }
        internal BilVerificationContext Types { get; }

        private readonly Dictionary<string, BilFunction> _functions;
        private readonly Dictionary<string, BilSimpleMemberDeclaration> _members;
        private readonly Dictionary<string, BilTypeDeclaration> _types;
        private readonly Dictionary<string, BilTypeDeclaration> _typesByKey;
        private readonly Dictionary<string, BilSimpleMemberDeclaration> _fields;
        private readonly Dictionary<string, BilCaseDeclaration> _cases;
        private readonly Dictionary<string, string> _getters;
        private readonly Dictionary<string, string> _setters;
        private readonly Dictionary<string, VmValue> _statics = new Dictionary<string, VmValue>();
        private readonly object _staticLock = new object();
        private readonly Dictionary<string, VmValue> _singletons = new Dictionary<string, VmValue>();
        private readonly object _singletonLock = new object();
        // singleton 构造在途栈（BIL §8.7，裁定 2）：正在初始化（init 尚未跑完）
        // 的类型，栈序即构造链；预初始化单协程同步执行，仅 InitializeSingletons
        // 期间读写，仍加锁与 _singletons 口径一致
        private readonly List<string> _initializing = new List<string>();
        private readonly object _initializingLock = new object();
        private readonly StringBuilder _stdout = new StringBuilder();
        private readonly StringBuilder _stderr = new StringBuilder();
        private readonly object _stdoutLock = new object();
        private readonly object _stderrLock = new object();

        public VmContext(BilModule module, VmHooks? hooks = null)
        {
            Module = module;
            Types = new BilVerificationContext(module);
            Hooks = hooks ?? VmHooks.CreateStandard();
            _functions = new Dictionary<string, BilFunction>();
            foreach (var function in module.Functions)
            {
                _functions[function.Symbol] = function;
            }
            _members = new Dictionary<string, BilSimpleMemberDeclaration>();
            _types = new Dictionary<string, BilTypeDeclaration>();
            _typesByKey = new Dictionary<string, BilTypeDeclaration>();
            _fields = new Dictionary<string, BilSimpleMemberDeclaration>();
            _cases = new Dictionary<string, BilCaseDeclaration>();
            _getters = new Dictionary<string, string>();
            _setters = new Dictionary<string, string>();
            IndexMembers(module.LocalSymbols);
            IndexMembers(module.ExternalSymbols);
        }

        public string Stdout
        {
            get { lock (_stdoutLock) return _stdout.ToString(); }
        }

        public string Stderr
        {
            get { lock (_stderrLock) return _stderr.ToString(); }
        }

        public void WriteStdout(string text)
        {
            lock (_stdoutLock)
            {
                _stdout.Append(text);
            }
        }

        public void WriteStderr(string text)
        {
            lock (_stderrLock)
            {
                _stderr.Append(text);
            }
        }

        public BilFunction? FindFunction(string symbol)
        {
            return _functions.TryGetValue(symbol, out var function) ? function : null;
        }

        // 接口/基类符号无 fn 体时，按接收者运行时类型沿 extends 链
        // 找最派生的同名实例方法（逻辑虚派发，不模拟 vtable）。
        public BilFunction? FindVirtualFunction(string methodSymbol, IReadOnlyList<VmValue> arguments)
        {
            var direct = FindFunction(methodSymbol);
            if (direct != null)
            {
                return direct;
            }
            if (arguments.Count == 0 || !TryMethodName(methodSymbol, out var methodName))
            {
                return null;
            }
            var current = VmTypeOps.ActualType(arguments[0]);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (visited.Add(StripTypeArguments(current)))
            {
                foreach (var function in _functions.Values)
                {
                    if (!TryMethodName(function.Symbol, out var candidateName)
                        || candidateName != methodName)
                    {
                        continue;
                    }
                    if (!BilVerificationContext.TryParseMethodSymbol(function.Symbol,
                            out var owner, out var isStatic, out _, out _))
                    {
                        continue;
                    }
                    if (isStatic)
                    {
                        continue;
                    }
                    if (TypesEqual(owner, current)
                        || StripTypeArguments(owner) == StripTypeArguments(current))
                    {
                        return function;
                    }
                }
                var declaration = FindType(current);
                if (declaration?.ExtendsType == null)
                {
                    break;
                }
                current = declaration.ExtendsType;
            }
            return null;
        }

        private static bool TryMethodName(string symbol, out string name)
        {
            name = "";
            var dollar = symbol.IndexOf('$');
            if (dollar < 0)
            {
                return false;
            }
            var rest = symbol.Substring(dollar + 1);
            if (rest.StartsWith("$", StringComparison.Ordinal))
            {
                rest = rest.Substring(1);
            }
            if (rest.StartsWith(".static.", StringComparison.Ordinal))
            {
                rest = rest.Substring(".static.".Length);
            }
            var open = rest.IndexOf('(');
            if (open <= 0)
            {
                return false;
            }
            name = rest.Substring(0, open);
            return name.Length > 0;
        }

        public BilFunction FindEntrypoint()
        {
            BilSimpleMemberDeclaration? entry = null;
            foreach (var member in _members.Values)
            {
                if (!HasKeyword(member, BilKeyword.Entrypoint))
                {
                    continue;
                }
                if (entry != null)
                {
                    throw new VmException("模块存在多个 entrypoint fn");
                }
                entry = member;
            }
            if (entry == null)
            {
                throw new VmException("模块没有 entrypoint fn");
            }
            if (!_functions.TryGetValue(entry.Symbol, out var function))
            {
                throw new VmException("entrypoint " + entry.Symbol + " 没有 fn 定义");
            }
            return function;
        }

        public bool IsAsyncMethod(string methodSymbol)
        {
            return _members.TryGetValue(methodSymbol, out var member)
                && HasKeyword(member, BilKeyword.Async);
        }

        public static string FunctionResultType(BilFunction function)
        {
            foreach (var arg in function.Args)
            {
                if (arg.Name == ".return")
                {
                    return arg.TypeRef;
                }
            }
            return ".void";
        }

        public static string TaskTypeRef(string resultType)
        {
            if (resultType == ".void" || resultType == "core::void")
            {
                return "core.coroutine::Task";
            }
            return "core.coroutine::Task<" + resultType + ">";
        }

        public bool TryResolveNative(string methodSymbol, out string library, out string nativeSymbol)
        {
            library = "";
            nativeSymbol = "";
            if (!_members.TryGetValue(methodSymbol, out var member)
                || !HasKeyword(member, BilKeyword.Native))
            {
                return false;
            }
            string? lib = null;
            string? symbol = null;
            foreach (var modifier in member.Modifiers)
            {
                if (modifier is BilNativeLibraryModifier libraryModifier)
                {
                    lib = libraryModifier.Library;
                }
                else if (modifier is BilNativeSymbolModifier symbolModifier)
                {
                    symbol = symbolModifier.Symbol;
                }
            }
            if (lib == null || symbol == null)
            {
                throw new VmException("native 方法 " + methodSymbol + " 缺少 lib/symbol");
            }
            library = lib;
            nativeSymbol = symbol;
            return true;
        }

        public VmValue LoadResource(BilResource resource)
        {
            switch (resource)
            {
                case BilScalarResource scalar:
                    return LoadScalar(scalar);
                case BilNullResource:
                    return VmNull.Instance;
                default:
                    throw new VmException("不支持的资源形态：" + resource.GetType().Name);
            }
        }

        // §16.6/§19.4：switch-table 元素按 selector 类型解析为 VmValue，
        // 再以 cmp.eq 语义与 selector 比较。
        public VmValue LoadSwitchElement(string selectorTypeRef, string literalText)
        {
            var normalized = BilVerificationContext.NormalizeTypeRef(selectorTypeRef);
            var scalar = normalized switch
            {
                "core::i8" => BilScalarType.I8,
                "core::i16" => BilScalarType.I16,
                "core::i32" => BilScalarType.I32,
                "core::i64" => BilScalarType.I64,
                "core::u8" => BilScalarType.U8,
                "core::u16" => BilScalarType.U16,
                "core::u32" => BilScalarType.U32,
                "core::u64" => BilScalarType.U64,
                "core::float" => BilScalarType.F32,
                "core::double" => BilScalarType.F64,
                "core::bool" => BilScalarType.Bool,
                "core::char" => BilScalarType.Char,
                "core::String" => BilScalarType.String,
                _ => throw new VmException("switch-table 不支持 selector 类型：" + selectorTypeRef),
            };
            return LoadScalar(new BilScalarResource("_switch", scalar, literalText));
        }

        public BilTypeDeclaration? FindType(string typeRef)
        {
            if (_types.TryGetValue(typeRef, out var exact))
            {
                return exact;
            }
            return _typesByKey.TryGetValue(DeclarationKeyOf(typeRef), out var byKey) ? byKey : null;
        }

        public BilSimpleMemberDeclaration? FindMember(string symbol)
        {
            return _members.TryGetValue(symbol, out var member) ? member : null;
        }

        public BilSimpleMemberDeclaration? FindField(string symbol)
        {
            return _fields.TryGetValue(symbol, out var field) ? field : null;
        }

        public BilCaseDeclaration? FindCase(string qualifiedName)
        {
            return _cases.TryGetValue(qualifiedName, out var found) ? found : null;
        }

        public bool IsValueType(string typeRef)
        {
            if (IsBuiltinScalar(typeRef) || typeRef == ".string" || typeRef == "core::String")
            {
                return true;
            }
            var declaration = FindType(typeRef);
            if (declaration == null)
            {
                return false;
            }
            return declaration.Kind is BilTypeKind.Struct or BilTypeKind.EnumStruct
                or BilTypeKind.Wrapper;
        }

        public static bool IsArrayType(string typeRef, out string elementType)
        {
            elementType = "";
            if (TryStripConstructor(typeRef, ".array", out elementType)
                || TryStripConstructor(typeRef, "core::Array", out elementType))
            {
                return true;
            }
            return false;
        }

        public bool IsEnumStruct(string typeRef)
        {
            var declaration = FindType(typeRef);
            return declaration != null && declaration.Kind == BilTypeKind.EnumStruct;
        }

        public bool TryFindInit(string typeRef, IReadOnlyList<VmValue> arguments,
            out string initSymbol)
        {
            initSymbol = "";
            var declaration = FindType(typeRef);
            if (declaration == null)
            {
                return arguments.Count == 0;
            }
            var inits = new List<BilSimpleMemberDeclaration>();
            foreach (var member in declaration.Members)
            {
                if (member is BilSimpleMemberDeclaration simple
                    && HasKeyword(simple, BilKeyword.Init))
                {
                    inits.Add(simple);
                }
            }
            if (inits.Count == 0)
            {
                return arguments.Count == 0;
            }
            foreach (var init in inits)
            {
                if (!BilVerificationContext.TryParseMethodSymbol(init.Symbol,
                        out _, out _, out var parameters, out _))
                {
                    continue;
                }
                if (ParametersMatch(parameters, arguments))
                {
                    initSymbol = init.Symbol;
                    return true;
                }
            }
            return false;
        }

        public bool TryFindInitWrapper(string typeRef, out string symbol, out int parameterCount)
        {
            symbol = "";
            parameterCount = 0;
            var declaration = FindType(typeRef);
            if (declaration == null)
            {
                return false;
            }
            foreach (var member in declaration.Members)
            {
                if (member is not BilSimpleMemberDeclaration simple)
                {
                    continue;
                }
                if (MethodNameOf(simple.Symbol) != BilSpellings.InitWrapperMethodName)
                {
                    continue;
                }
                if (!BilVerificationContext.TryParseMethodSymbol(simple.Symbol,
                        out _, out _, out var parameters, out _))
                {
                    continue;
                }
                symbol = simple.Symbol;
                parameterCount = parameters.Count;
                return true;
            }
            return false;
        }

        public bool TryFindAccessor(string fieldSymbol, BilAccessorKind kind,
            string currentFunction, out string methodSymbol)
        {
            methodSymbol = "";
            var table = kind == BilAccessorKind.Getter ? _getters : _setters;
            if (!table.TryGetValue(fieldSymbol, out var found))
            {
                return false;
            }
            if (found == currentFunction)
            {
                return false;
            }
            methodSymbol = found;
            return true;
        }

        // 字段声明上的 wrapped(W) 应用标记，按声明序 outer→inner 收集
        // （§8.3.1：可重复出现，顺序即派发链嵌套序；无标记返回空列表）
        public IReadOnlyList<string> CollectWrappedWrappers(string fieldSymbol)
        {
            var field = FindField(fieldSymbol);
            var result = new List<string>();
            if (field == null)
            {
                return result;
            }
            foreach (var modifier in field.Modifiers)
            {
                if (modifier is BilWrappedModifier wrapped)
                {
                    result.Add(wrapped.WrapperTypeRef);
                }
            }
            return result;
        }

        // 类型声明上的 wrapped(W) 应用标记（Entity wrapper），按声明序
        // outer→inner 收集（§8.3.1：与字段 Value wrapper 标记同源，挂类型声明）
        public IReadOnlyList<string> CollectEntityWrappers(string typeRef)
        {
            var result = new List<string>();
            var declaration = FindType(typeRef);
            if (declaration == null)
            {
                return result;
            }
            foreach (var modifier in declaration.Modifiers)
            {
                if (modifier is BilWrappedModifier wrapped)
                {
                    result.Add(wrapped.WrapperTypeRef);
                }
            }
            return result;
        }

        // 字段符号的简单名（Host#name@T → name），用于 .proxy.get.<名>/.
        // proxy.set.<名> 的 specific proxy 命中
        public static string FieldSimpleName(string fieldSymbol)
        {
            var hash = fieldSymbol.IndexOf('#');
            var at = fieldSymbol.LastIndexOf('@');
            if (hash < 0 || at <= hash)
            {
                return "";
            }
            var name = fieldSymbol.Substring(hash + 1, at - hash - 1);
            if (name.StartsWith(".static.", StringComparison.Ordinal))
            {
                name = name.Substring(".static.".Length);
            }
            return name;
        }

        // 找 value wrapper 的 proxy 模板 fn 符号（.proxy.get / .proxy.set；无则 null）。
        // 仅匹配带 wrapper-proxy 修饰符的成员，防与普通同名方法误判
        public string? FindWrapperProxy(string wrapperType, string proxyName)
        {
            var declaration = FindType(wrapperType);
            if (declaration == null)
            {
                return null;
            }
            foreach (var member in declaration.Members)
            {
                if (member is not BilSimpleMemberDeclaration simple
                    || simple.Kind != BilMemberKind.Method)
                {
                    continue;
                }
                if (MethodNameOf(simple.Symbol) != proxyName)
                {
                    continue;
                }
                foreach (var modifier in simple.Modifiers)
                {
                    if (modifier is BilWrapperProxyModifier)
                    {
                        return simple.Symbol;
                    }
                }
            }
            return null;
        }

        // 当前 fn 是否为给定字段所属类型的 init（声明带 Init 关键字修饰）。
        // 构造期一次性赋值豁免 proxy 链（§14.3 / §21.8：cell 子类的
        // init(value) 会写带 wrapped 标记的 value 字段，只带 get proxy 的
        // wrapper 也必须能初始化 const/ReadonlyCell）
        public bool IsInitFunctionOf(string functionSymbol, string fieldOwner)
        {
            if (!BilVerificationContext.TryParseMethodSymbol(functionSymbol,
                    out var owner, out _, out _, out _))
            {
                return false;
            }
            if (!TypesEqual(owner, fieldOwner)
                && BilVerificationContext.StripTypeArguments(owner)
                    != BilVerificationContext.StripTypeArguments(fieldOwner))
            {
                return false;
            }
            if (!_members.TryGetValue(functionSymbol, out var member))
            {
                return false;
            }
            foreach (var modifier in member.Modifiers)
            {
                if (modifier is BilKeywordModifier keyword && keyword.Keyword == BilKeyword.Init)
                {
                    return true;
                }
            }
            return false;
        }

        public string? FindIndexOperator(string collectionType, bool isGet)
        {
            var operatorName = isGet ? "getAtIndex" : "setAtIndex";
            var needle = "$$" + operatorName + "(";
            var declaration = FindType(collectionType);
            var hosts = new HashSet<string>();
            var current = declaration;
            while (current != null)
            {
                hosts.Add(current.Symbol);
                if (current.ExtendsType == null)
                {
                    break;
                }
                current = FindType(current.ExtendsType);
            }
            hosts.Add(StripTypeArguments(collectionType));
            foreach (var member in _members.Values)
            {
                if (!member.Symbol.Contains(needle))
                {
                    continue;
                }
                if (!BilVerificationContext.TryParseMethodSymbol(member.Symbol,
                        out var owner, out _, out _, out _))
                {
                    continue;
                }
                if (hosts.Contains(owner) || hosts.Contains(StripTypeArguments(owner)))
                {
                    return member.Symbol;
                }
            }
            return null;
        }

        public IReadOnlyList<BilSimpleMemberDeclaration> CollectInstanceFields(string typeRef)
        {
            var fields = new List<BilSimpleMemberDeclaration>();
            var seen = new HashSet<string>();
            var current = FindType(typeRef);
            while (current != null)
            {
                foreach (var member in current.Members)
                {
                    if (member is not BilSimpleMemberDeclaration simple
                        || simple.Kind != BilMemberKind.Field)
                    {
                        continue;
                    }
                    if (seen.Add(simple.Symbol))
                    {
                        fields.Add(simple);
                    }
                }
                if (current.ExtendsType == null)
                {
                    break;
                }
                current = FindType(current.ExtendsType);
            }
            var owner = StripTypeArguments(typeRef);
            foreach (var field in _fields.Values)
            {
                if (field.Kind != BilMemberKind.Field)
                {
                    continue;
                }
                if (!BilVerificationContext.TryParseFieldSymbol(field.Symbol,
                        out var fieldOwner, out var isStatic, out _))
                {
                    continue;
                }
                if (isStatic || fieldOwner != owner || !seen.Add(field.Symbol))
                {
                    continue;
                }
                fields.Add(field);
            }
            return fields;
        }

        public VmValue ZeroOf(string typeRef)
        {
            if (IsArrayType(typeRef, out _))
            {
                return VmNull.Instance;
            }
            switch (typeRef)
            {
                case ".i8": case "core::i8": return new VmI8(0);
                case ".i16": case "core::i16": return new VmI16(0);
                case ".i32": case "core::i32": return new VmI32(0);
                case ".i64": case "core::i64": return new VmI64(0);
                case ".u8": case "core::u8": return new VmU8(0);
                case ".u16": case "core::u16": return new VmU16(0);
                case ".u32": case "core::u32": return new VmU32(0);
                case ".u64": case "core::u64": return new VmU64(0);
                case ".f32": case "core::float": return new VmF32(0);
                case ".f64": case "core::double": return new VmF64(0);
                case ".bool": case "core::bool": return new VmBool(false);
                case ".char": case "core::char": return new VmChar('\0');
                case ".string": case "core::String": return new VmString("");
                case ".void": return VmVoid.Instance;
                case ".null": return VmNull.Instance;
            }
            if (typeRef.StartsWith(".nullable<", StringComparison.Ordinal)
                || typeRef.StartsWith("core::Nullable<", StringComparison.Ordinal)
                || typeRef == ".any" || typeRef == "core::Any"
                || typeRef == ".object" || typeRef == "core::Object")
            {
                return VmNull.Instance;
            }
            var declaration = FindType(typeRef);
            if (declaration != null
                && declaration.Kind is BilTypeKind.Struct or BilTypeKind.Wrapper)
            {
                return AllocateObject(typeRef);
            }
            return VmNull.Instance;
        }

        public VmObject AllocateObject(string typeRef)
        {
            var valueType = IsValueType(typeRef);
            var instance = new VmObject(typeRef, valueType);
            foreach (var field in CollectInstanceFields(typeRef))
            {
                if (!BilVerificationContext.TryParseFieldSymbol(field.Symbol,
                        out _, out _, out var fieldType))
                {
                    continue;
                }
                instance.WriteField(field.Symbol, ZeroOf(fieldType));
            }
            return instance;
        }

        // ===== singleton（BIL §8.2 / §8.7）=====

        // 类型是否 singleton（含 companion）。VM 语义：singleton 全模块
        // 唯一实例，main 前由 InitializeSingletons 急切初始化；运行期
        // new type(singleton) 返回同一份已初始化实例（不再重跑 init）
        public bool IsSingletonType(string typeRef)
        {
            var declaration = FindType(typeRef);
            if (declaration == null) return false;
            foreach (var modifier in declaration.Modifiers)
            {
                if (modifier is BilKeywordModifier keyword
                    && keyword.Keyword == BilKeyword.Singleton)
                {
                    return true;
                }
            }
            return false;
        }

        // 取已缓存 singleton（null = 尚未构造）
        public VmValue? GetSingleton(string typeRef)
        {
            lock (_singletonLock)
            {
                return _singletons.TryGetValue(typeRef, out var value) ? value : null;
            }
        }

        // 登记 singleton 实例（幂等：仅首次写入——构造期间登记即标记
        // 在途，递归/环引用命中同一份半成品实例）
        public void RegisterSingleton(string typeRef, VmValue instance)
        {
            lock (_singletonLock)
            {
                if (!_singletons.ContainsKey(typeRef))
                {
                    _singletons[typeRef] = instance;
                }
            }
        }

        // 是否正在初始化（init 在途，尚未登记为已就绪）
        public bool IsInitializing(string typeRef)
        {
            lock (_initializingLock)
            {
                return _initializing.Contains(typeRef);
            }
        }

        // 进入构造：压入在途栈（幂等语义由 New 的环检测前置保证——同一
        // 类型不会重复入栈）
        public void BeginInitializing(string typeRef)
        {
            lock (_initializingLock)
            {
                _initializing.Add(typeRef);
            }
        }

        // 离开构造：弹出最近一次入栈的本类型条目
        public void EndInitializing(string typeRef)
        {
            lock (_initializingLock)
            {
                var index = _initializing.LastIndexOf(typeRef);
                if (index >= 0)
                {
                    _initializing.RemoveAt(index);
                }
            }
        }

        // 循环依赖异常（裁定 2）：链 = 在途栈中首次出现 typeRef 起的子链 +
        // typeRef 自身，报清晰循环链而非栈溢出
        public VmException SingletonCycleException(string typeRef)
        {
            var chain = new List<string>();
            lock (_initializingLock)
            {
                var index = _initializing.IndexOf(typeRef);
                for (var i = index < 0 ? 0 : index; i < _initializing.Count; i++)
                {
                    chain.Add(_initializing[i]);
                }
            }
            chain.Add(typeRef);
            return new VmException("singleton 初始化循环依赖：" + string.Join(" → ", chain));
        }

        // main 前急切初始化全部 singleton（§8.7）：构造 → init 跑完
        // （companion 的 init 即完成 cell 构造与 wrapper 安装）。初始化
        // 依赖（companion init 引用别的 singleton）由 New 的递归构造触发；
        // 本循环按声明序逐一补全尚未构造者
        public void InitializeSingletons(VmExecutor executor)
        {
            foreach (var declaration in _types.Values)
            {
                var singleton = false;
                foreach (var modifier in declaration.Modifiers)
                {
                    if (modifier is BilKeywordModifier keyword
                        && keyword.Keyword == BilKeyword.Singleton)
                    {
                        singleton = true;
                        break;
                    }
                }
                if (!singleton) continue;
                var typeRef = declaration.Symbol;
                if (GetSingleton(typeRef) != null) continue;
                ConstructSingleton(executor, typeRef);
            }
        }

        // 同步构造单个 singleton：借一次性协程跑 new（分配 + init +
        // ..init.wrapper），驱动至构造帧完全退回引导帧
        private void ConstructSingleton(VmExecutor executor, string typeRef)
        {
            var coroutine = new VmCoroutine(executor, "core.coroutine::Task");
            coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Running);
            coroutine.PushFrame(SingletonBootstrapFunction, Array.Empty<VmValue>(), null);
            BilDataExecution.New(this, coroutine, typeRef, SingletonBootstrapTarget,
                Array.Empty<BilVariableOperand>(), null);
            while (coroutine.CallStack.Count > 1 && coroutine.State == VmCoroutineState.Running)
            {
                coroutine.Step(this);
            }
            if (coroutine.State != VmCoroutineState.Running)
            {
                throw coroutine.Failure ?? new VmException("singleton 初始化失败：" + typeRef);
            }
        }

        // singleton 构造的引导帧与目标槽：New 需要一帧写入目标变量，逐
        // singleton 复用同一静态引导 fn（只承载目标槽，不含用户指令）
        private static readonly BilFunction SingletonBootstrapFunction = CreateSingletonBootstrap();
        private static readonly BilVariableOperand SingletonBootstrapTarget = BilOp.Var(".singleton");

        private static BilFunction CreateSingletonBootstrap()
        {
            var function = new BilFunction("..singleton.init");
            function.Args.Add(new BilArgDeclaration(".return", ".void"));
            function.Vars.Add(new BilVarDeclaration(".any", ".singleton"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new RetInstruction());
            function.Blocks.Add(entry);
            return function;
        }

        public VmValue ReadStaticField(string fieldSymbol)
        {
            lock (_staticLock)
            {
                if (_statics.TryGetValue(fieldSymbol, out var value))
                {
                    return value;
                }
                var fieldType = ".any";
                if (BilVerificationContext.TryParseFieldSymbol(fieldSymbol,
                        out _, out _, out var parsed))
                {
                    fieldType = parsed;
                }
                var zero = ZeroOf(fieldType);
                _statics[fieldSymbol] = zero;
                return zero;
            }
        }

        public void WriteStaticField(string fieldSymbol, VmValue value)
        {
            lock (_staticLock)
            {
                _statics[fieldSymbol] = value;
            }
        }

        public static string HiddenEntityKey(string wrapperType) =>
            ".wrapper.entity:" + wrapperType;

        public static string HiddenFieldKey(string fieldSymbol, string wrapperType) =>
            ".wrapper.field:" + fieldSymbol + ":" + wrapperType;

        public static string HiddenMethodKey(string methodSymbol, string wrapperType) =>
            ".wrapper.method:" + methodSymbol + ":" + wrapperType;

        public VmException CastFailed(string message)
        {
            return LanguageException("core::CastException", message);
        }

        public VmException NoSuchMethod(string message)
        {
            return LanguageException("core::NoSuchMethodException", message);
        }

        public VmException LanguageException(string typeRef, string message)
        {
            var instance = AllocateObject(typeRef);
            instance.WriteField("core::Exception#message@.string", new VmString(message));
            return new VmException(message, instance);
        }

        public string? FindOperator(string ownerType, string operatorName,
            IReadOnlyList<VmValue> valueArguments)
        {
            return FindOperatorMember(ownerType, operatorName, valueArguments, callOnly: false);
        }

        public string? FindCallOperator(string ownerType, IReadOnlyList<VmValue> valueArguments)
        {
            return FindOperatorMember(ownerType, "call", valueArguments, callOnly: true);
        }

        private string? FindOperatorMember(string ownerType, string operatorName,
            IReadOnlyList<VmValue> valueArguments, bool callOnly)
        {
            var current = ownerType;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (visited.Add(StripTypeArguments(current)))
            {
                var declaration = FindType(current);
                if (declaration == null)
                {
                    break;
                }
                var found = MatchOperatorOn(declaration, operatorName, valueArguments);
                if (found != null)
                {
                    return found;
                }
                if (declaration.ExtendsType == null)
                {
                    break;
                }
                current = declaration.ExtendsType;
            }
            var needle = "$" + operatorName + "(";
            var hosts = visited;
            hosts.Add(StripTypeArguments(ownerType));
            foreach (var member in _members.Values)
            {
                if (!member.Symbol.Contains(needle, StringComparison.Ordinal))
                {
                    continue;
                }
                if (callOnly
                    && !member.Modifiers.Any(m => m is BilOperatorModifier { Name: "call" })
                    && !member.Symbol.Contains("$call(", StringComparison.Ordinal))
                {
                    continue;
                }
                if (!BilVerificationContext.TryParseMethodSymbol(member.Symbol,
                        out var owner, out _, out _, out _))
                {
                    continue;
                }
                if (!hosts.Contains(owner) && !hosts.Contains(StripTypeArguments(owner)))
                {
                    continue;
                }
                if (OperatorParamsMatch(member.Symbol, valueArguments))
                {
                    return member.Symbol;
                }
            }
            return null;
        }

        private string? MatchOperatorOn(BilTypeDeclaration declaration, string operatorName,
            IReadOnlyList<VmValue> valueArguments)
        {
            foreach (var member in declaration.Members)
            {
                if (member is not BilSimpleMemberDeclaration simple)
                {
                    continue;
                }
                var isOperator = false;
                foreach (var modifier in simple.Modifiers)
                {
                    if (modifier is BilOperatorModifier op && op.Name == operatorName)
                    {
                        isOperator = true;
                        break;
                    }
                }
                if (!isOperator && !simple.Symbol.Contains("$" + operatorName + "(",
                        StringComparison.Ordinal))
                {
                    continue;
                }
                if (OperatorParamsMatch(simple.Symbol, valueArguments))
                {
                    return simple.Symbol;
                }
            }
            return null;
        }

        private bool OperatorParamsMatch(string methodSymbol, IReadOnlyList<VmValue> valueArguments)
        {
            if (!BilVerificationContext.TryParseMethodSymbol(methodSymbol,
                    out _, out _, out var parameters, out _))
            {
                return false;
            }
            var ordinary = new List<(string Name, string TypeRef)>();
            foreach (var parameter in parameters)
            {
                if (parameter.Name.StartsWith(".generic.", StringComparison.Ordinal)
                    || parameter.Name.StartsWith(".vargs.", StringComparison.Ordinal)
                    || parameter.Name.StartsWith(".kwargs.", StringComparison.Ordinal))
                {
                    continue;
                }
                ordinary.Add(parameter);
            }
            var genericHidden = 0;
            var function = FindFunction(methodSymbol);
            if (function != null)
            {
                foreach (var arg in function.Args)
                {
                    if (arg.Name.StartsWith(".generic.", StringComparison.Ordinal))
                    {
                        genericHidden++;
                    }
                }
            }
            if (valueArguments.Count != genericHidden + ordinary.Count)
            {
                return false;
            }
            for (var i = 0; i < ordinary.Count; i++)
            {
                if (!TypesEqual(ordinary[i].TypeRef, valueArguments[genericHidden + i].TypeRef))
                {
                    return false;
                }
            }
            return true;
        }

        public static int RequireIndex(VmValue index)
        {
            return index switch
            {
                VmI8 v => v.Value,
                VmI16 v => v.Value,
                VmI32 v => v.Value,
                VmI64 v => checked((int)v.Value),
                VmU8 v => v.Value,
                VmU16 v => v.Value,
                VmU32 v => checked((int)v.Value),
                VmU64 v => checked((int)v.Value),
                _ => throw new VmException("数组下标不是整数：" + index.TypeRef),
            };
        }

        private void IndexMembers(IEnumerable<BilSymbolSectionEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (entry is BilSimpleMemberDeclaration member)
                {
                    IndexSimple(member);
                }
                else if (entry is BilTypeDeclaration type)
                {
                    _types[type.Symbol] = type;
                    var key = type.Symbol + "`" + type.GenericParameters.Count;
                    if (!_typesByKey.ContainsKey(key))
                    {
                        _typesByKey[key] = type;
                    }
                    foreach (var nested in type.Members)
                    {
                        if (nested is BilSimpleMemberDeclaration nestedMember)
                        {
                            IndexSimple(nestedMember);
                        }
                        else if (nested is BilCaseDeclaration caseDecl)
                        {
                            _cases[caseDecl.QualifiedName] = caseDecl;
                        }
                    }
                }
                else if (entry is BilCaseDeclaration topCase)
                {
                    _cases[topCase.QualifiedName] = topCase;
                }
            }
        }

        private void IndexSimple(BilSimpleMemberDeclaration member)
        {
            _members[member.Symbol] = member;
            if (member.Kind is BilMemberKind.Field or BilMemberKind.StaticField)
            {
                _fields[member.Symbol] = member;
            }
            foreach (var modifier in member.Modifiers)
            {
                if (modifier is not BilAccessorModifier accessor)
                {
                    continue;
                }
                if (accessor.Kind == BilAccessorKind.Getter)
                {
                    _getters[accessor.FieldSymbol] = member.Symbol;
                }
                else
                {
                    _setters[accessor.FieldSymbol] = member.Symbol;
                }
            }
        }

        private static bool ParametersMatch(List<(string Name, string TypeRef)> parameters,
            IReadOnlyList<VmValue> arguments)
        {
            if (parameters.Count != arguments.Count)
            {
                return false;
            }
            for (var i = 0; i < parameters.Count; i++)
            {
                if (!TypesEqual(parameters[i].TypeRef, arguments[i].TypeRef))
                {
                    return false;
                }
            }
            return true;
        }

        internal static bool TypesEqual(string left, string right)
        {
            return left == right
                || NormalizeType(left) == NormalizeType(right);
        }

        private static string NormalizeType(string typeRef)
        {
            return typeRef switch
            {
                "core::i8" => ".i8",
                "core::i16" => ".i16",
                "core::i32" => ".i32",
                "core::i64" => ".i64",
                "core::u8" => ".u8",
                "core::u16" => ".u16",
                "core::u32" => ".u32",
                "core::u64" => ".u64",
                "core::float" => ".f32",
                "core::double" => ".f64",
                "core::bool" => ".bool",
                "core::char" => ".char",
                "core::String" => ".string",
                "core::Any" => ".any",
                "core::Object" => ".object",
                "core::ValueType" => ".valuetype",
                _ => typeRef,
            };
        }

        private static bool IsBuiltinScalar(string typeRef)
        {
            return typeRef is ".i8" or ".i16" or ".i32" or ".i64"
                or ".u8" or ".u16" or ".u32" or ".u64"
                or ".f32" or ".f64" or ".bool" or ".char"
                or "core::i8" or "core::i16" or "core::i32" or "core::i64"
                or "core::u8" or "core::u16" or "core::u32" or "core::u64"
                or "core::float" or "core::double" or "core::bool" or "core::char";
        }

        private static string DeclarationKeyOf(string typeRef)
        {
            var angle = typeRef.IndexOf('<');
            if (angle < 0)
            {
                return typeRef + "`0";
            }
            var inner = typeRef.Substring(angle + 1, typeRef.Length - angle - 2);
            return typeRef.Substring(0, angle) + "`" + CountTopLevel(inner);
        }

        private static string StripTypeArguments(string typeRef)
        {
            var angle = typeRef.IndexOf('<');
            return angle < 0 ? typeRef : typeRef.Substring(0, angle);
        }

        private static bool TryStripConstructor(string typeRef, string head, out string inner)
        {
            inner = "";
            var prefix = head + "<";
            if (!typeRef.StartsWith(prefix, StringComparison.Ordinal) || !typeRef.EndsWith(">"))
            {
                return false;
            }
            inner = typeRef.Substring(prefix.Length, typeRef.Length - prefix.Length - 1);
            return true;
        }

        private static int CountTopLevel(string text)
        {
            if (text.Length == 0)
            {
                return 0;
            }
            var count = 1;
            var depth = 0;
            foreach (var ch in text)
            {
                if (ch == '<') depth++;
                else if (ch == '>') depth--;
                else if (ch == ',' && depth == 0) count++;
            }
            return count;
        }

        internal static string MethodNameOf(string symbol)
        {
            var dollar = symbol.IndexOf('$');
            if (dollar < 0)
            {
                return "";
            }
            var rest = symbol.Substring(dollar + 1);
            if (rest.StartsWith("$"))
            {
                rest = rest.Substring(1);
            }
            if (rest.StartsWith(".static."))
            {
                rest = rest.Substring(".static.".Length);
            }
            var end = rest.Length;
            var paren = rest.IndexOf('(');
            if (paren >= 0 && paren < end)
            {
                end = paren;
            }
            var at = rest.IndexOf('@');
            if (at >= 0 && at < end)
            {
                end = at;
            }
            return rest.Substring(0, end);
        }

        private static bool HasKeyword(BilSimpleMemberDeclaration member, BilKeyword keyword)
        {
            foreach (var modifier in member.Modifiers)
            {
                if (modifier is BilKeywordModifier keywordModifier
                    && keywordModifier.Keyword == keyword)
                {
                    return true;
                }
            }
            return false;
        }

        private static VmValue LoadScalar(BilScalarResource resource)
        {
            var text = resource.LiteralText;
            switch (resource.Type)
            {
                case BilScalarType.String:
                    return new VmString(DecodeStringLiteral(text));
                case BilScalarType.Bool:
                    return new VmBool(text == "true");
                case BilScalarType.Char:
                    return new VmChar(DecodeCharLiteral(text));
                case BilScalarType.I8:
                    return new VmI8(sbyte.Parse(text, CultureInfo.InvariantCulture));
                case BilScalarType.I16:
                    return new VmI16(short.Parse(text, CultureInfo.InvariantCulture));
                case BilScalarType.I32:
                    return new VmI32(int.Parse(text, CultureInfo.InvariantCulture));
                case BilScalarType.I64:
                    return new VmI64(long.Parse(text, CultureInfo.InvariantCulture));
                case BilScalarType.U8:
                    return new VmU8(byte.Parse(text, CultureInfo.InvariantCulture));
                case BilScalarType.U16:
                    return new VmU16(ushort.Parse(text, CultureInfo.InvariantCulture));
                case BilScalarType.U32:
                    return new VmU32(uint.Parse(text, CultureInfo.InvariantCulture));
                case BilScalarType.U64:
                    return new VmU64(ulong.Parse(text, CultureInfo.InvariantCulture));
                case BilScalarType.F32:
                    return new VmF32(float.Parse(text, CultureInfo.InvariantCulture));
                case BilScalarType.F64:
                    return new VmF64(double.Parse(text, CultureInfo.InvariantCulture));
                default:
                    throw new VmException("不支持的标量资源类型：" + resource.Type);
            }
        }

        private static string DecodeStringLiteral(string literalText)
        {
            if (literalText.Length < 2 || literalText[0] != '"' || literalText[^1] != '"')
            {
                throw new VmException("非法字符串资源字面量：" + literalText);
            }
            return Unescape(literalText.Substring(1, literalText.Length - 2));
        }

        private static char DecodeCharLiteral(string literalText)
        {
            if (literalText.Length < 2 || literalText[0] != '\'' || literalText[^1] != '\'')
            {
                throw new VmException("非法字符资源字面量：" + literalText);
            }
            var inner = Unescape(literalText.Substring(1, literalText.Length - 2));
            if (inner.Length != 1)
            {
                throw new VmException("字符资源不是单字符：" + literalText);
            }
            return inner[0];
        }

        private static string Unescape(string escaped)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < escaped.Length; i++)
            {
                if (escaped[i] != '\\')
                {
                    sb.Append(escaped[i]);
                    continue;
                }
                if (i + 1 >= escaped.Length)
                {
                    throw new VmException("字符串资源转义不完整");
                }
                i++;
                sb.Append(escaped[i] switch
                {
                    '\\' => '\\',
                    '"' => '"',
                    '\'' => '\'',
                    '$' => '$',
                    'a' => '\a',
                    'b' => '\b',
                    't' => '\t',
                    'n' => '\n',
                    'v' => '\v',
                    'f' => '\f',
                    'r' => '\r',
                    _ => throw new VmException("未知转义 \\" + escaped[i]),
                });
            }
            return sb.ToString();
        }
    }
}
