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

        // 实现级指令步数上限：0 = 不限制（默认）。每条 Step 计 1（含嵌套）。
        public long MaxSteps { get; set; }
        private long _steps;

        private readonly Dictionary<string, BilFunction> _functions;
        private readonly Dictionary<string, BilSimpleMemberDeclaration> _members;
        private readonly Dictionary<string, BilTypeDeclaration> _types;
        private readonly Dictionary<string, BilTypeDeclaration> _typesByKey;
        private readonly Dictionary<string, BilSimpleMemberDeclaration> _fields;
        private readonly Dictionary<string, BilCaseDeclaration> _cases;
        private readonly Dictionary<string, string> _getters;
        private readonly Dictionary<string, string> _setters;
        // 访问器符号 → 字段符号逆向表（与 _getters/_setters 同建于
        // IndexSimple；TryFindFieldForAccessor 用）
        private readonly Dictionary<string, string> _accessorFields;
        private readonly Dictionary<string, VmValue> _statics = new Dictionary<string, VmValue>();
        private readonly object _staticLock = new object();
        // 逻辑 TypeSheet 缓存（声明 key → 拍平 sheet；可重入锁——Build 沿
        // extends 递归会重入 SheetOf，VM 多 Worker 并发触发）
        private readonly Dictionary<string, VmTypeSheet> _sheets =
            new Dictionary<string, VmTypeSheet>(StringComparer.Ordinal);
        private readonly object _sheetLock = new object();
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
            _accessorFields = new Dictionary<string, string>();
            IndexMembers(module.LocalSymbols);
            IndexMembers(module.ExternalSymbols);
        }

        // 每执行一条 BIL 指令调用一次；超限抛 VmStepLimitException（受控）。
        internal void AccountStep()
        {
            if (MaxSteps <= 0) return;
            long n = Interlocked.Increment(ref _steps);
            if (n > MaxSteps)
            {
                throw new VmStepLimitException(MaxSteps);
            }
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

        // ===== 逻辑 TypeSheet（RUNTIME §6–§9 拍平等价物，VmTypeSheet.cs）=====

        // 声明 → 拍平 sheet（缓存 + 可重入锁：VM 多 Worker 并发触发构建）；
        // 声明缺失返回 null（构造类型剥实参后按其声明）
        internal VmTypeSheet? SheetOf(string typeRef)
        {
            var declaration = FindType(typeRef);
            if (declaration == null)
            {
                return null;
            }
            var key = VmTypeSheetBuilder.TypeKeyOf(declaration);
            lock (_sheetLock)
            {
                if (_sheets.TryGetValue(key, out var cached))
                {
                    return cached;
                }
                var sheet = VmTypeSheetBuilder.Build(this, declaration,
                    new HashSet<string>(StringComparer.Ordinal));
                _sheets[key] = sheet;
                return sheet;
            }
        }

        // 统一方法派发入口（替换旧的按名线性扫描）：静态符号经 owner 声明
        // sheet 换算 offset，再取 receiver 实际类型 sheet 的槽实现（虚派发）；
        // owner 是接口时 offset = iMap 段基址 + 接口内相对 offset（§8）。
        // 不可派发（无 receiver/static/全局符号）退回 FindFunction 直查；
        // owner 无声明（core::Exception 等预定义根）时按签名在实际类型槽
        // 防御扫描（等价旧名匹配路径，如 getMessage 多态）。
        internal BilFunction? ResolveDispatch(string staticSymbol, VmValue? receiver)
        {
            var impl = ResolveDispatchSymbol(staticSymbol, receiver);
            var function = FindFunction(impl);
            // 槽解析成功但实现 fn 缺失时报实现符号；直查兜底（impl 即静态
            // 符号）维持原语义返回 null，由调用方抛「找不到 fn 定义」
            if (function == null
                && !string.Equals(impl, staticSymbol, StringComparison.Ordinal))
            {
                throw new VmException("派发到的实现缺少 fn 定义：" + impl);
            }
            return function;
        }

        // 虚派发符号解析（bug O1）：与 ResolveDispatch 同一套 sheet 换算，
        // 但只到「实际类型槽的实现符号」为止——Method wrapper 的隐藏存储键
        // 按安装侧 canonical 是声明 override 的类型符号（Child$work），调用侧
        // invoke 带的却是静态符号（Base$work / 接口符号），wrapper 链收集前
        // 先经此换算对齐（RUNTIME §7 虚派发 + §14.9 override 重复声明）。
        // 不可派发/兜底路径原样返回静态符号。
        internal string ResolveDispatchSymbol(string staticSymbol, VmValue? receiver)
        {
            if (receiver == null
                || !BilVerificationContext.TryParseMethodSymbol(staticSymbol,
                    out var owner, out var isStatic, out _, out _)
                || isStatic
                || owner.Length == 0)
            {
                return staticSymbol;
            }
            var actualType = VmTypeOps.ActualType(receiver);
            var actualSheet = SheetOf(actualType);
            var ownerDeclaration = FindType(owner);
            if (ownerDeclaration != null && actualSheet != null)
            {
                var ownerSheet = SheetOf(ownerDeclaration.Symbol);
                if (ownerSheet != null
                    && ownerSheet.OffsetBySymbol.TryGetValue(staticSymbol, out var offset))
                {
                    if (ownerDeclaration.Kind == BilTypeKind.Interface)
                    {
                        // 接口派发（§8）：接口内相对 offset + iMap 段基址；iMap
                        // 未命中（异常模块形态）按接口成员签名防御扫描
                        if (actualSheet.InterfaceBase.TryGetValue(
                                VmTypeSheetBuilder.TypeKeyOf(ownerDeclaration), out var baseOffset))
                        {
                            offset += baseOffset;
                        }
                        else
                        {
                            offset = FindSlotBySignature(actualSheet, staticSymbol);
                            if (offset < 0)
                            {
                                return staticSymbol;
                            }
                        }
                    }
                    return ResolveSlotSymbol(actualSheet, offset, staticSymbol, actualType);
                }
            }
            if (owner.Length > 0 && actualSheet != null)
            {
                var offset = FindSlotBySignature(actualSheet, staticSymbol);
                if (offset >= 0)
                {
                    return ResolveSlotSymbol(actualSheet, offset, staticSymbol, actualType);
                }
            }
            return staticSymbol;
        }

        // 槽 offset → 实现符号（泛型代入形态的实现经名字兼容回退；抽象/接口
        // 方法无实现抛清晰 VmException）
        private string ResolveSlotSymbol(VmTypeSheet sheet, int offset, string staticSymbol,
            string actualType)
        {
            var impl = sheet.Slots[offset].ImplSymbol
                ?? VmTypeSheetBuilder.FindCompatibleImpl(sheet.Slots, staticSymbol);
            if (impl == null)
            {
                throw new VmException("抽象/接口方法无实现：" + staticSymbol
                    + "（receiver 实际类型 " + actualType + "）");
            }
            return impl;
        }

        private static int FindSlotBySignature(VmTypeSheet sheet, string staticSymbol)
        {
            var signatureKey = VmTypeSheetBuilder.SignatureKeyOf(staticSymbol);
            for (var i = 0; i < sheet.Slots.Count; i++)
            {
                if (sheet.Slots[i].SignatureKey == signatureKey)
                {
                    return i;
                }
            }
            return -1;
        }

        // callable 协议（§15.3）：receiver 实际类型 sheet 中参数与实参值匹配的
        // $$call 槽（搜索空间 = 拍平槽列表，含继承；sheet 不可用时退回声明链
        // 扫描兜底）
        internal string? FindCallTarget(string receiverType, IReadOnlyList<VmValue> valueArguments)
        {
            var sheet = SheetOf(receiverType);
            if (sheet != null)
            {
                for (var i = 0; i < sheet.Slots.Count; i++)
                {
                    var slot = sheet.Slots[i];
                    if (slot.ImplSymbol == null
                        || !slot.SignatureKey.StartsWith("$call(", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (OperatorParamsMatch(slot.ImplSymbol, valueArguments))
                    {
                        return slot.ImplSymbol;
                    }
                }
                return null;
            }
            return FindOperatorMember(receiverType, "call", valueArguments, callOnly: true);
        }

        // 符号是否 init 声明（成员带 Init 关键字修饰）
        internal bool IsInitMethod(string methodSymbol)
        {
            return _members.TryGetValue(methodSymbol, out var member)
                && HasKeyword(member, BilKeyword.Init);
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
            IReadOnlyList<string> argumentStaticTypes, out string initSymbol)
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
            // 构造泛型宿主（§14.1 严格匹配的 VM 侧落地）：init 声明在定义级
            // 符号上，参数类型含 .generic<$.generic.T> 占位——按构造实参代入
            // 后再与实参**静态类型** TypesEqual（编译期已 cast 到声明类型；
            // 不是按运行期 typeid 可赋值性再 ranking）
            var substitution = VmTypeSheetBuilder.BuildSubstitution(typeRef, declaration);
            foreach (var init in inits)
            {
                if (!BilVerificationContext.TryParseMethodSymbol(init.Symbol,
                        out _, out _, out var parameters, out _))
                {
                    continue;
                }
                if (substitution != null)
                {
                    parameters = parameters.ConvertAll(p =>
                        (p.Name, VmTypeSheetBuilder.SubstituteGenericArguments(
                            p.TypeRef, substitution)));
                }
                if (ParametersMatch(parameters, arguments, argumentStaticTypes))
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

        // 直查 getter/setter 表，不排除当前 fn（链末落点调 setter）
        public bool TryFindAccessorDirect(string fieldSymbol, BilAccessorKind kind,
            out string methodSymbol)
        {
            methodSymbol = "";
            var table = kind == BilAccessorKind.Getter ? _getters : _setters;
            if (fieldSymbol.Length == 0 || !table.TryGetValue(fieldSymbol, out var found))
            {
                return false;
            }
            methodSymbol = found;
            return true;
        }

        // 访问器符号 → 字段符号反查（getter(FIELD)/setter(FIELD) 修饰符正向
        // 表的逆向）：Entity wildcard 环 inner 以 $get/$set 访问器符号重路由
        // 为 Get/Set 链时恢复链末字段目标——链可能起自访问器方法调用的
        // 拦截（如构造期 init/..init.field.* 走 setter），FieldSymbol 原本为空
        public bool TryFindFieldForAccessor(string accessorSymbol, out string fieldSymbol)
        {
            if (_accessorFields.TryGetValue(accessorSymbol, out var found))
            {
                fieldSymbol = found;
                return true;
            }
            fieldSymbol = "";
            return false;
        }

        // 当前 fn 是否为 fieldSymbol 的 getter（含 cell getValue 回退）
        public bool IsGetterOf(string currentFunction, string fieldSymbol)
        {
            var member = FindMember(currentFunction);
            if (member != null)
            {
                foreach (var modifier in member.Modifiers)
                {
                    if (modifier is BilAccessorModifier accessor
                        && accessor.Kind == BilAccessorKind.Getter
                        && accessor.FieldSymbol == fieldSymbol)
                    {
                        return true;
                    }
                }
            }
            if (!BilVerificationContext.TryParseMethodSymbol(currentFunction,
                    out var owner, out _, out _, out _))
            {
                return false;
            }
            if (MethodNameOf(currentFunction) != "getValue" || !IsCellTypeRef(owner))
            {
                return false;
            }
            if (!BilVerificationContext.TryParseFieldSymbol(fieldSymbol,
                    out _, out _, out var fieldType))
            {
                return false;
            }
            return fieldSymbol == owner + "#value@" + fieldType;
        }

        // ..value → 真实 backing 字段。非 ..value 返回 false；是 ..value 但
        // 不在 setter 上下文则抛
        public bool TryResolveBackingValue(string fieldSymbol, string currentFunction,
            out string backingField)
        {
            backingField = "";
            if (FieldSimpleName(fieldSymbol) != BilSpellings.BackingValueFieldName)
            {
                return false;
            }
            if (!BilVerificationContext.TryParseFieldSymbol(fieldSymbol,
                    out var owner, out var isStatic, out var fieldType))
            {
                throw new VmException("..value 字段符号不可解析：" + fieldSymbol);
            }
            var member = FindMember(currentFunction);
            if (member != null)
            {
                foreach (var modifier in member.Modifiers)
                {
                    if (modifier is not BilAccessorModifier accessor
                        || accessor.Kind != BilAccessorKind.Setter)
                    {
                        continue;
                    }
                    if (!BilVerificationContext.TryParseFieldSymbol(accessor.FieldSymbol,
                            out var fieldOwner, out var fieldStatic, out var declaredType))
                    {
                        continue;
                    }
                    if (TypesEqual(fieldOwner, owner) && fieldStatic == isStatic
                        && TypesEqual(declaredType, fieldType))
                    {
                        backingField = accessor.FieldSymbol;
                        return true;
                    }
                }
            }
            if (BilVerificationContext.TryParseMethodSymbol(currentFunction,
                    out var fnOwner, out _, out _, out _))
            {
                var name = MethodNameOf(currentFunction);
                if ((name == "setValue" || name == "getValue") && IsCellTypeRef(fnOwner))
                {
                    backingField = fnOwner + "#value@" + fieldType;
                    return true;
                }
            }
            throw new VmException("..value 只能出现在 setter 上下文：" + fieldSymbol);
        }

        // 类型名（去命名空间 / 泛型实参）是否为 cell 隐藏子类
        internal static bool IsCellTypeRef(string typeRef)
        {
            var name = typeRef;
            var generic = name.IndexOf('<');
            if (generic >= 0)
            {
                name = name.Substring(0, generic);
            }
            var sep = name.LastIndexOf("::", StringComparison.Ordinal);
            if (sep >= 0)
            {
                name = name.Substring(sep + 2);
            }
            return name.StartsWith("..cell..", StringComparison.Ordinal);
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
        // wrapper 也必须能初始化 const/ReadonlyCell）。
        // 新 init 原则（§9.7 修订）：编译器合成的构造期写入方法族
        // （..init.wrapper / ..init.field.*）同样豁免——子类合成方法写
        // 继承字段（字段 owner 为基类）是缝合协议的常态，不按 owner 限定
        public bool IsInitFunctionOf(string functionSymbol, string fieldOwner)
        {
            if (!BilVerificationContext.TryParseMethodSymbol(functionSymbol,
                    out var owner, out _, out _, out _))
            {
                return false;
            }
            var name = MethodNameOf(functionSymbol);
            if (name == BilSpellings.InitWrapperMethodName
                || name.StartsWith(BilSpellings.InitFieldMethodPrefix,
                    StringComparison.Ordinal))
            {
                return true;
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

        // N1（§8.4.1/§9.3，新 init 原则）：全局/静态字段的声明初始值——
        // 编译器为含初始值的模块合成 ..globals.init 全局 fn（体内按声明序
        // set.field.static），本方法在 singleton 初始化之后、main 之前
        // 同步驱动它跑完（参照 §8.7 companion 统一设计：静态初值的执行
        // 时机归 VM 启动序列；多模块合并时逐 fn 各跑一次）
        public void InvokeGlobalInitializers(VmExecutor executor)
        {
            foreach (var function in _functions.Values)
            {
                if (MethodNameOf(function.Symbol) != BilSpellings.GlobalsInitFunctionName)
                {
                    continue;
                }
                var coroutine = new VmCoroutine(executor, "core.coroutine::Task");
                coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Running);
                coroutine.PushFrame(function, Array.Empty<VmValue>(), null);
                while (coroutine.CallStack.Count > 0
                    && coroutine.State == VmCoroutineState.Running)
                {
                    coroutine.Step(this);
                }
                if (coroutine.State != VmCoroutineState.Running
                    && coroutine.State != VmCoroutineState.Completed)
                {
                    throw coroutine.Failure
                        ?? new VmException("全局字段初始化失败：" + function.Symbol);
                }
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

        public VmException DividedByZero(string message)
        {
            return LanguageException("core::DividedByZeroException", message);
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
            // 运算符/间接调用传入的是值实参，不含 hidden typeid。
            // 泛型占位由 TypesEqual 的 .generic< 降级匹配；typeid 在
            // 命中后由 InjectOperatorTypeIds 从实参类型结构推断补入。
            if (valueArguments.Count != ordinary.Count)
            {
                return false;
            }
            for (var i = 0; i < ordinary.Count; i++)
            {
                if (!TypeAssignable(valueArguments[i].TypeRef, ordinary[i].TypeRef))
                {
                    return false;
                }
            }
            return true;
        }

        // 运算符实参：实际 typeid 可赋给形参类型（精确相等、.generic 占位、
        // 或沿 extends/implements 闭包命中——A 实现 Addable 时 plus(Addable) 可派发）
        private bool TypeAssignable(string from, string to)
        {
            if (TypesEqual(from, to)) return true;
            if (to is ".any" or "core::Any") return true;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            return TypeAssignableWalk(from, to, visited);
        }

        private bool TypeAssignableWalk(string current, string to, HashSet<string> visited)
        {
            if (!visited.Add(StripTypeArguments(current) + "\0" + current)) return false;
            if (TypesEqual(current, to)) return true;
            var declaration = FindType(current);
            if (declaration == null) return false;
            if (declaration.ExtendsType != null
                && TypeAssignableWalk(declaration.ExtendsType, to, visited))
            {
                return true;
            }
            foreach (var iface in declaration.ImplementsTypes)
            {
                if (TypeAssignableWalk(iface, to, visited)) return true;
            }
            return false;
        }

        // 类级 .generic.* 未出现在 invoke 实参时，按 .this 构造形态的实参
        // 位序补齐（与 EmitFunction 外层类型参数在前、方法自有在后一致）。
        public static IReadOnlyList<VmValue> AlignGenericHiddenArgs(BilFunction function,
            IReadOnlyList<VmValue> arguments)
        {
            var slots = new List<BilArgDeclaration>();
            foreach (var arg in function.Args)
            {
                if (arg.Name != ".return") slots.Add(arg);
            }
            if (arguments.Count == slots.Count) return arguments;

            var genericSlots = new List<BilArgDeclaration>();
            var hasThis = false;
            var ordinaryCount = 0;
            foreach (var slot in slots)
            {
                if (slot.Name == ".this")
                {
                    hasThis = true;
                    continue;
                }
                if (slot.Name.StartsWith(".generic.", StringComparison.Ordinal))
                {
                    genericSlots.Add(slot);
                    continue;
                }
                ordinaryCount++;
            }
            if (genericSlots.Count == 0) return arguments;

            var thisOffset = hasThis ? 1 : 0;
            if (arguments.Count < thisOffset) return arguments;
            var restCount = arguments.Count - thisOffset;
            if (restCount < ordinaryCount) return arguments;
            var passedHidden = restCount - ordinaryCount;
            if (passedHidden < 0 || passedHidden > genericSlots.Count) return arguments;

            var inferred = new List<string>();
            if (hasThis && arguments.Count > 0)
            {
                var typeArgs = TypeArgsOf(arguments[0].TypeRef);
                if (typeArgs != null) inferred.AddRange(typeArgs);
            }

            var result = new List<VmValue>(slots.Count);
            var restIndex = thisOffset;
            var inferredIndex = 0;
            var hiddenConsumed = 0;
            foreach (var slot in slots)
            {
                if (slot.Name == ".this")
                {
                    result.Add(arguments[0]);
                    continue;
                }
                if (slot.Name.StartsWith(".generic.", StringComparison.Ordinal))
                {
                    if (passedHidden == genericSlots.Count)
                    {
                        result.Add(arguments[restIndex++]);
                    }
                    else if (inferredIndex < inferred.Count
                        && hiddenConsumed < genericSlots.Count - passedHidden)
                    {
                        result.Add(new VmTypeId(inferred[inferredIndex++]));
                    }
                    else if (restIndex < arguments.Count
                        && arguments[restIndex] is VmTypeId)
                    {
                        result.Add(arguments[restIndex++]);
                    }
                    else if (inferredIndex < inferred.Count)
                    {
                        result.Add(new VmTypeId(inferred[inferredIndex++]));
                    }
                    else
                    {
                        result.Add(new VmTypeId(".any"));
                    }
                    hiddenConsumed++;
                    continue;
                }
                if (restIndex < arguments.Count)
                {
                    result.Add(arguments[restIndex++]);
                }
                else
                {
                    return arguments;
                }
            }
            return result;
        }

        // 按 fn .args 序在 .this 之后插入推断的 .generic.* typeid，
        // 使 PushFrame 实参个数与泛型 operator 签名对齐。
        public IReadOnlyList<VmValue> InjectOperatorTypeIds(string methodSymbol,
            IReadOnlyList<VmValue> args)
        {
            var function = FindFunction(methodSymbol);
            if (function == null) return args;
            var slots = new List<BilArgDeclaration>();
            foreach (var arg in function.Args)
            {
                if (arg.Name != ".return") slots.Add(arg);
            }
            var genericCount = 0;
            foreach (var slot in slots)
            {
                if (slot.Name.StartsWith(".generic.", StringComparison.Ordinal))
                {
                    genericCount++;
                }
            }
            if (genericCount == 0) return args;

            var thisCount = 0;
            foreach (var slot in slots)
            {
                if (slot.Name == ".this") thisCount++;
            }
            var ordinaryArgs = new List<VmValue>();
            for (var i = thisCount; i < args.Count; i++)
            {
                ordinaryArgs.Add(args[i]);
            }
            var bindings = InferGenericBindings(function, ordinaryArgs);
            var result = new List<VmValue>(slots.Count);
            var ordinaryIndex = 0;
            var thisIndex = 0;
            foreach (var slot in slots)
            {
                if (slot.Name == ".this")
                {
                    result.Add(thisIndex < args.Count ? args[thisIndex++] : VmNull.Instance);
                    continue;
                }
                if (slot.Name.StartsWith(".generic.", StringComparison.Ordinal))
                {
                    var name = slot.Name.Substring(".generic.".Length);
                    result.Add(new VmTypeId(bindings.TryGetValue(name, out var typeRef)
                        ? typeRef : ".any"));
                    continue;
                }
                if (slot.Name.StartsWith(".vargs.", StringComparison.Ordinal)
                    || slot.Name.StartsWith(".kwargs.", StringComparison.Ordinal))
                {
                    continue;
                }
                result.Add(ordinaryIndex < ordinaryArgs.Count
                    ? ordinaryArgs[ordinaryIndex++] : VmNull.Instance);
            }
            return result;
        }

        private static Dictionary<string, string> InferGenericBindings(BilFunction function,
            IReadOnlyList<VmValue> ordinaryArgs)
        {
            var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
            var ordinary = new List<BilArgDeclaration>();
            foreach (var arg in function.Args)
            {
                if (arg.Name == ".return" || arg.Name == ".this"
                    || arg.Name.StartsWith(".generic.", StringComparison.Ordinal)
                    || arg.Name.StartsWith(".vargs.", StringComparison.Ordinal)
                    || arg.Name.StartsWith(".kwargs.", StringComparison.Ordinal))
                {
                    continue;
                }
                ordinary.Add(arg);
            }
            for (var i = 0; i < ordinary.Count && i < ordinaryArgs.Count; i++)
            {
                UnifyTypeRef(ordinary[i].TypeRef, ordinaryArgs[i].TypeRef, bindings);
            }
            return bindings;
        }

        private static void UnifyTypeRef(string pattern, string actual,
            Dictionary<string, string> bindings)
        {
            if (TryParseGenericPlaceholder(pattern, out var name))
            {
                if (!bindings.ContainsKey(name)) bindings[name] = actual;
                return;
            }
            var patternArgs = TypeArgsOf(pattern);
            var actualArgs = TypeArgsOf(actual);
            if (patternArgs == null || actualArgs == null
                || patternArgs.Count != actualArgs.Count)
            {
                return;
            }
            var patternHead = BilVerificationContext.NormalizeTypeRef(
                BilVerificationContext.StripTypeArguments(pattern));
            var actualHead = BilVerificationContext.NormalizeTypeRef(
                BilVerificationContext.StripTypeArguments(actual));
            if (patternHead != actualHead) return;
            for (var i = 0; i < patternArgs.Count; i++)
            {
                UnifyTypeRef(patternArgs[i], actualArgs[i], bindings);
            }
        }

        private static bool TryParseGenericPlaceholder(string typeRef, out string name)
        {
            name = "";
            const string hidden = ".generic<$.generic.";
            if (typeRef.StartsWith(hidden, StringComparison.Ordinal) && typeRef.EndsWith(">"))
            {
                name = typeRef.Substring(hidden.Length, typeRef.Length - hidden.Length - 1);
                return name.Length > 0 && name.IndexOf('<') < 0;
            }
            const string shortForm = ".generic<";
            if (typeRef.StartsWith(shortForm, StringComparison.Ordinal) && typeRef.EndsWith(">"))
            {
                name = typeRef.Substring(shortForm.Length, typeRef.Length - shortForm.Length - 1);
                return name.Length > 0 && name.IndexOf('<') < 0 && !name.StartsWith("$");
            }
            return false;
        }

        private static List<string>? TypeArgsOf(string typeRef)
        {
            var angle = typeRef.IndexOf('<');
            if (angle < 0 || !typeRef.EndsWith(">")) return null;
            return SplitTopLevelArgs(typeRef.Substring(angle + 1, typeRef.Length - angle - 2));
        }

        private static List<string> SplitTopLevelArgs(string inner)
        {
            var parts = new List<string>();
            var depth = 0;
            var start = 0;
            for (var i = 0; i < inner.Length; i++)
            {
                var c = inner[i];
                if (c == '<') depth++;
                else if (c == '>') depth--;
                else if (c == ',' && depth == 0)
                {
                    parts.Add(inner.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            if (start <= inner.Length)
            {
                var last = inner.Substring(start).Trim();
                if (last.Length > 0) parts.Add(last);
            }
            return parts;
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
                _accessorFields[member.Symbol] = accessor.FieldSymbol;
            }
        }

        private bool ParametersMatch(List<(string Name, string TypeRef)> parameters,
            IReadOnlyList<VmValue> arguments, IReadOnlyList<string> argumentStaticTypes)
        {
            if (parameters.Count != arguments.Count
                || argumentStaticTypes.Count != arguments.Count)
            {
                return false;
            }
            for (var i = 0; i < parameters.Count; i++)
            {
                // §14.1 / RUNTIME §11：对编译期已解析入口的验证——cast 后
                // 静态类型与形参 TypesEqual（不是运行期 typeid 可赋值性）
                if (!TypesEqual(argumentStaticTypes[i], parameters[i].TypeRef))
                {
                    return false;
                }
            }
            return true;
        }

        // BIL 变量声明类型（.args / .vars）；查不到时回落运行期 TypeRef。
        // init / super 匹配用静态类型，不用对象头 typeid。
        internal static string LookupStaticType(BilFunction function, string name,
            string fallback)
        {
            foreach (var arg in function.Args)
            {
                if (arg.Name == name)
                {
                    return arg.TypeRef;
                }
            }
            foreach (var variable in function.Vars)
            {
                if (variable.Name == name)
                {
                    return variable.TypeRef;
                }
            }
            return fallback;
        }

        internal static string[] ArgumentStaticTypes(BilFunction function,
            IReadOnlyList<BilVariableOperand> operands, IReadOnlyList<VmValue> values)
        {
            var types = new string[operands.Count];
            for (var i = 0; i < operands.Count; i++)
            {
                types[i] = LookupStaticType(function, operands[i].Name, values[i].TypeRef);
            }
            return types;
        }

        // 任一侧含 .generic<（函数泛型占位，无法静态判定）降级通过
        // ——与 BilVerificationContext.TypesCompatible 一致
        internal static bool TypesEqual(string left, string right)
        {
            if (left == right)
            {
                return true;
            }
            if (left.Contains(".generic<", StringComparison.Ordinal)
                || right.Contains(".generic<", StringComparison.Ordinal))
            {
                return true;
            }
            return NormalizeType(left) == NormalizeType(right);
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

        internal static bool HasKeyword(BilSimpleMemberDeclaration member, BilKeyword keyword)
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
