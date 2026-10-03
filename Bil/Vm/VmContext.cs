using System.Globalization;
using System.IO;
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
        // MW11c 棒4a（§17.4）：native 原语实现 + 调度桥（Rigi 世界的
        // Dispatcher/Task 与 VM 引擎之间的粘合）
        internal VmDispatch Dispatch { get; }

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
        // 两路标准流分别按调用顺序保存字节；UTF-8 仅在读取结果时对整个
        // 通道解码，不能在 write 边界把尚未写完的多字节序列替换掉。
        private readonly MemoryStream _stdout = new MemoryStream();
        private readonly MemoryStream _stderr = new MemoryStream();
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
            Dispatch = new VmDispatch(this);
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

        // 阻塞原语也观察全局预算；某个 Worker 超限后，其余 Worker
        // 不能继续停在 park/同步锁里，否则主调用永远拿不到受控失败。
        internal void CheckStepLimit()
        {
            if (MaxSteps > 0 && Interlocked.Read(ref _steps) > MaxSteps)
                throw new VmStepLimitException(MaxSteps);
        }

        public string Stdout
        {
            get { lock (_stdoutLock) return Encoding.UTF8.GetString(_stdout.ToArray()); }
        }

        public string Stderr
        {
            get { lock (_stderrLock) return Encoding.UTF8.GetString(_stderr.ToArray()); }
        }

        public void WriteStdout(string text)
        {
            // Console 的完整文本与标准流原始字节写入同一通道、同一锁，
            // 因而不会因分别缓存文本/字节而丢掉两种调用的实际先后顺序。
            WriteStdout(Encoding.UTF8.GetBytes(text));
        }

        public void WriteStderr(string text)
        {
            WriteStderr(Encoding.UTF8.GetBytes(text));
        }

        public void WriteStdout(ReadOnlySpan<byte> bytes)
        {
            lock (_stdoutLock)
            {
                _stdout.Write(bytes);
            }
        }

        public void WriteStderr(ReadOnlySpan<byte> bytes)
        {
            lock (_stderrLock)
            {
                _stderr.Write(bytes);
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
        // owner 无声明（预定义根不进符号段）时按签名在实际类型槽
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
            // MW11d-B2：..toParcel/..fromParcel 按签名在实际类型表上找实现槽
            // （接口符号 owner 含 `..` 时 FindType 可能未命中；签名键不含 owner）
            var memberName = MethodNameOf(staticSymbol);
            if (actualSheet != null
                && (memberName == BilSpellings.ToParcelMethodName
                    || memberName == BilSpellings.FromParcelMethodName))
            {
                var synthOffset = FindSlotBySignature(actualSheet, staticSymbol);
                if (synthOffset >= 0)
                {
                    return ResolveSlotSymbol(actualSheet, synthOffset, staticSymbol, actualType);
                }
            }
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
                    if (OperatorParamsMatch(slot.ImplSymbol, valueArguments,
                            skipGenericPrefix: true, receiverType: receiverType))
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

        // 定位入口 fn（§17）：preferredSymbol 非空 = --entry-point 显式指定
        // （必须命中带 entrypoint 修饰的成员）；缺省自动查找——恰一个选中，
        // 零个/多个分别报错（多入口报文列出候选并提示 --entry-point）
        public BilFunction FindEntrypoint(string? preferredSymbol = null)
        {
            var entries = new List<BilSimpleMemberDeclaration>();
            foreach (var member in _members.Values)
            {
                if (HasKeyword(member, BilKeyword.Entrypoint))
                {
                    entries.Add(member);
                }
            }
            BilSimpleMemberDeclaration? entry;
            if (preferredSymbol != null)
            {
                entry = entries.Find(m => m.Symbol == preferredSymbol)
                    ?? throw new VmException("--entry-point 指定的符号不是 entrypoint 方法: "
                        + preferredSymbol);
            }
            else if (entries.Count == 0)
            {
                throw new VmException("模块没有 entrypoint fn");
            }
            else if (entries.Count > 1)
            {
                throw new VmException("模块存在多个 entrypoint fn（用 --entry-point <符号> 显式指定）："
                    + string.Join("、", entries.ConvertAll(m => m.Symbol)));
            }
            else
            {
                entry = entries[0];
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

        // MW11c 棒4a：运行时桥按符号前缀解析 stdlib 内部 fn
        // （Dispatcher/Task 的 priv 运行时通道；canonical 参数段随形状
        // 微调，前缀锁定「宿主$方法名(」）
        public BilFunction? FindRuntimeFunction(string symbolPrefix)
        {
            var canonical = BilCompilerSymbols.ResolvePrefix(Module, symbolPrefix);
            if (canonical != null) return _functions.GetValueOrDefault(canonical);
            foreach (var function in _functions.Values)
            {
                if (function.Symbol.StartsWith(symbolPrefix, StringComparison.Ordinal))
                {
                    return function;
                }
            }
            return null;
        }

        internal string RuntimeSymbol(string logical) => BilCompilerSymbols.Resolve(Module, logical);
        internal string RuntimeField(string logical) => BilCompilerSymbols.ResolveField(Module, logical);

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
            if (_typesByKey.TryGetValue(DeclarationKeyOf(typeRef), out var byKey)) return byKey;
            // 固定 ABI 的源码声明使用 canonical 名，值仍可持标准别名；只归一别名，不擦除泛型。
            var canonical = BilVerificationContext.NormalizeTypeRef(typeRef);
            return _typesByKey.TryGetValue(DeclarationKeyOf(canonical), out byKey) ? byKey : null;
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
            var normalized = BilVerificationContext.NormalizeTypeRef(typeRef);
            if (normalized == "core::ValueType" || normalized.StartsWith("core::Type<", StringComparison.Ordinal)) return true;
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
                case ".char": case "core::char": return new VmChar(0u);
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
            // MW12b §25.2：class 引用对象且实现闭包含 core::IDisposable
            // 才挂销毁检查（判定结果按类型缓存，热路径仅一次字典读）
            var instance = new VmObject(typeRef, valueType,
                !valueType && DisposeSlotTargetOf(typeRef) != null
                    ? UndisposedTracker : null);
            for (var resourceType = FindType(typeRef); resourceType != null;
                resourceType = resourceType.ExtendsType == null ? null : FindType(resourceType.ExtendsType))
            {
                var logicalOwner = new[] { "core.coroutine::Mutex", "core.coroutine::Dispatcher",
                    "core.coroutine::Task", "core.coroutine::Task<TReturn>" }
                    .SingleOrDefault(name => RuntimeSymbol(name) == resourceType.Symbol);
                if (logicalOwner != null)
                {
                    instance.NativeResourceRelease = Dispatch.ReleaseOwnedResources;
                    var gate = RuntimeField(logicalOwner + "#gate@.i64");
                    instance.NativeGateFieldSuffix = gate[gate.LastIndexOf('#')..];
                    var handle = RuntimeField(logicalOwner + "#handle@.i64");
                    instance.NativeCoroutineFieldSuffix = handle[handle.LastIndexOf('#')..];
                    break;
                }
            }
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
        public void InitializeSingletons()
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
                ConstructSingleton(typeRef);
            }
        }

        // 同步构造单个 singleton：借一次性协程跑 new（分配 + init +
        // ..init.wrapper），驱动至构造帧完全退回引导帧
        private void ConstructSingleton(string typeRef)
        {
            var coroutine = new VmCoroutine(Dispatch);
            coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Running);
            coroutine.PushFrame(SingletonBootstrapFunction, Array.Empty<VmValue>(), null, this);
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
        public void InvokeGlobalInitializers()
        {
            foreach (var symbol in BilModuleInitialization.Order(Module))
            {
                var function = _functions[symbol];
                var coroutine = new VmCoroutine(Dispatch);
                coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Running);
                coroutine.PushFrame(function, Array.Empty<VmValue>(), null, this);
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

        // 嵌套类外层泛型捕获（review-20260910 #02）：嵌套类构造类型的实参
        // 只覆盖自身 GP（.type Ring.RingEnum = class generic(TItem)），外层
        // 宿主 GP 绑定在构造点从当前帧解析，以 .string 数组形态挂实例隐藏槽；
        // 方法帧对齐隐藏 typeid（AlignGenericHiddenArgs）时按外层链序取用
        public const string HiddenOuterGenericsKey = ".outer-generics";

        // 嵌套类外层 GP 名链（最外层在前）：类型符号 "ns::A.B.C" 的宿主前缀
        // 逐级 FindType 收 GenericParameters，按 CollectFrameGenericParameters
        // 同口径按名去重（同名遮蔽只留最外层槽位）
        internal static List<string> OuterGenericParametersOf(VmContext context,
            string typeSymbol)
        {
            var names = new List<string>();
            var nsEnd = typeSymbol.IndexOf("::", StringComparison.Ordinal);
            var nsPrefix = nsEnd >= 0 ? typeSymbol.Substring(0, nsEnd + 2) : "";
            var path = nsEnd >= 0 ? typeSymbol.Substring(nsEnd + 2) : typeSymbol;
            var parts = path.Split('.');
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var depth = 1; depth < parts.Length; depth++)
            {
                var ownerSymbol = nsPrefix + string.Join(".", parts, 0, depth);
                var ownerDecl = context.FindType(ownerSymbol);
                if (ownerDecl == null)
                {
                    continue;
                }
                foreach (var parameter in ownerDecl.GenericParameters)
                {
                    if (seen.Add(parameter))
                    {
                        names.Add(parameter);
                    }
                }
            }
            return names;
        }

        // 构造点捕获外层宿主 GP 的当前帧绑定（找不到外层链或无 GP 时不写）；
        // 构造发生在宿主帧外（外层 GP 未绑定）留空串占位，对齐端回落 .any
        // ——与既有「推不出即 .any」降级口径一致
        internal static void CaptureOuterGenericBindings(VmContext context,
            VmCoroutine coroutine, string typeRef, VmObject instance)
        {
            var declaration = context.FindType(typeRef);
            if (declaration == null)
            {
                return;
            }
            var outerNames = OuterGenericParametersOf(context, declaration.Symbol);
            if (outerNames.Count == 0)
            {
                return;
            }
            var resolved = new List<string>(outerNames.Count);
            foreach (var name in outerNames)
            {
                resolved.Add(VmTypeOps.TryResolveFrameGeneric(coroutine, name, out var bound)
                    ? bound : "");
            }
            var array = new VmArray(".string", resolved.Count, new VmString(""));
            for (var i = 0; i < resolved.Count; i++)
            {
                array.SetAt(i, new VmString(resolved[i]));
            }
            instance.WriteHidden(HiddenOuterGenericsKey, array);
        }

        // MW9b：cast 失败消息模板源码化（stdlib init(fromType,toType)）
        public VmException CastFailed(VmCoroutine coroutine, string fromType, string toType)
        {
            return LanguageException(coroutine, "core::CastException",
                new VmValue[] { new VmString(fromType), new VmString(toType) },
                new[] { ".string", ".string" },
                "无法将 " + fromType + " 转换为 " + toType);
        }

        // 自由文本 NoSuchMethodException（$$call 无匹配 / wrapper 降级未路由）
        public VmException NoSuchMethod(VmCoroutine coroutine, string message)
        {
            return LanguageException(coroutine, "core::NoSuchMethodException",
                new VmValue[] { new VmString(message) }, new[] { ".string" }, message);
        }

        // 无协程上下文（native hook 等）沿用直写字段路径
        public VmException NoSuchMethod(string message)
        {
            return LanguageException("core::NoSuchMethodException", message);
        }

        // MW9b：new.indirect 动态构造失败（stdlib init(typeName)）。
        // init(typeName: String) 与 init(text: String) 同为 (String) 单参
        // 签名——按首形参名精确命中 typeName 重载
        public VmException NoSuchMethodForType(VmCoroutine coroutine, string typeRef)
        {
            return LanguageException(coroutine, "core::NoSuchMethodException",
                new VmValue[] { new VmString(typeRef) }, new[] { ".string" },
                "new.indirect 目标不可构造：不匹配任何 init：" + typeRef,
                initFirstParamName: "typeName");
        }

        // MW9b：整数除零（stdlib 零参 init，模板烘进 Rigi 源码）
        public VmException DividedByZero(VmCoroutine coroutine)
        {
            return LanguageException(coroutine, "core::DividedByZeroException",
                Array.Empty<VmValue>(), Array.Empty<string>(), "整数除以零");
        }

        // MW9b：数组/Span 写越界（stdlib init(index,length)，可捕获）
        public VmException OutOfBounds(VmCoroutine coroutine, long index, long length)
        {
            return LanguageException(coroutine, "core::OutOfBoundException",
                new VmValue[] { new VmI64(index), new VmI64(length) },
                new[] { ".i64", ".i64" },
                "数组下标越界：" + index + "（长度 " + length + "）");
        }

        // MW9b：内置异常 message 源码化——分配对象后按新便捷 init 签名经
        // VM 正常派发调 init（参照用户 new 的 init 调用路径：receiver 打头
        // InvokeValues + 同步 Step 回落到原栈深），消息模板烘在
        // stdlib/core/exceptions.rg，VM/native 两侧天然一致。
        // stdlib init 体是纯赋值 + 插值拼接不会失败；防御性失败（模板漂移、
        // init 匹配落空、派发中 abrupt）回落旧直写字段路径。
        public VmException LanguageException(VmCoroutine coroutine, string typeRef,
            IReadOnlyList<VmValue> initArguments, IReadOnlyList<string> initStaticTypes,
            string fallbackMessage, string? initFirstParamName = null)
        {
            var instance = AllocateObject(typeRef);
            var found = initFirstParamName != null
                ? TryFindInitByFirstParamName(typeRef, initFirstParamName, initArguments,
                    initStaticTypes, out var initSymbol)
                : TryFindInit(typeRef, initArguments, initStaticTypes, out initSymbol);
            if (coroutine.State == VmCoroutineState.Running
                && !coroutine.HasAbruptCompletion
                && found
                && initSymbol.Length > 0)
            {
                try
                {
                    var callArgs = new List<VmValue>(initArguments.Count + 1) { instance };
                    callArgs.AddRange(initArguments);
                    var depth = coroutine.CallStack.Count;
                    BilInvokeExecution.InvokeValues(this, coroutine, initSymbol, callArgs,
                        resultSlot: null);
                    while (coroutine.CallStack.Count > depth
                        && coroutine.State == VmCoroutineState.Running
                        && !coroutine.HasAbruptCompletion)
                    {
                        coroutine.Step(this);
                    }
                    if (coroutine.CallStack.Count == depth
                        && coroutine.State == VmCoroutineState.Running
                        && !coroutine.HasAbruptCompletion
                        && instance.TryReadField(RuntimeField("core::Exception#message@.string"),
                            out var messageValue)
                        && messageValue is VmString messageText)
                    {
                        return new VmException(messageText.Value, instance);
                    }
                }
                catch (VmException)
                {
                    // 回落旧直写字段路径（见上注释：stdlib init 不会失败）
                }
            }
            instance.WriteField(RuntimeField("core::Exception#message@.string"),
                new VmString(fallbackMessage));
            return new VmException(fallbackMessage, instance);
        }

        public VmException LanguageException(string typeRef, string message)
        {
            var instance = AllocateObject(typeRef);
            instance.WriteField(RuntimeField("core::Exception#message@.string"), new VmString(message));
            return new VmException(message, instance);
        }

        // ===== MW12b §25.2 VM 半场：IDisposable 销毁时检查 + 事件派发 =====

        // undisposed 事件队列（finalizer 入队、Run 收尾派发；语义见
        // VmDisposal.cs 与 VmObject 终结器注释）
        internal VmUndisposedTracker UndisposedTracker { get; } = new VmUndisposedTracker();

        // core::IDisposable 的 canonical（stdlib core/disposable.rg），与
        // native LayoutEngine.DisposableCanonical 同口径
        internal const string DisposableCanonical = "core::IDisposable";

        // dispose 槽目标缓存：类型声明 key → impl fn 符号（null = 实现闭包
        // 不含 IDisposable / 槽无实现）；槽序事实都在 TypeSheet 缓存上，
        // 本缓存只是把「判定 + iMap 换算」摊成一次字典读，防热路径拖慢
        private readonly Dictionary<string, string?> _disposeSlotTargets =
            new Dictionary<string, string?>(StringComparer.Ordinal);
        private readonly object _disposeSlotLock = new object();

        // type 的 core::IDisposable.dispose 槽目标 impl 符号——判定口径与
        // native CollectDisposeImplementations 对齐：实现闭包含 IDisposable
        // 的 class，取 iMap 段基址（InterfaceBase 拍平含继承条目，等价
        // native 沿 BasePlan 链上查）+ 接口壳内相对 offset 的槽实现；
        // wrapper 烘焙外移体/async stub 天然兼容（槽目标即其符号）
        internal string? DisposeSlotTargetOf(string typeRef)
        {
            var declaration = FindType(typeRef);
            var key = declaration != null
                ? VmTypeSheetBuilder.TypeKeyOf(declaration) : typeRef;
            lock (_disposeSlotLock)
            {
                if (_disposeSlotTargets.TryGetValue(key, out var cached))
                {
                    return cached;
                }
            }
            var target = ResolveDisposeSlotTarget(declaration, typeRef);
            lock (_disposeSlotLock)
            {
                _disposeSlotTargets[key] = target;
            }
            return target;
        }

        private string? ResolveDisposeSlotTarget(BilTypeDeclaration? declaration,
            string typeRef)
        {
            if (declaration == null || IsValueType(typeRef))
            {
                return null;
            }
            var disposable = FindType(DisposableCanonical);
            if (disposable == null)
            {
                return null;   // 无 stdlib 的合成模块（单元测试形态）
            }
            var sheet = SheetOf(declaration.Symbol);
            var disposableSheet = SheetOf(disposable.Symbol);
            if (sheet == null || disposableSheet == null
                || !sheet.InterfaceBase.TryGetValue(
                       VmTypeSheetBuilder.TypeKeyOf(disposable), out var baseOffset))
            {
                return null;
            }
            // 接口壳内 dispose 的相对 offset（接口自身 sheet 的槽下标）
            string? disposeSymbol = null;
            foreach (var member in disposable.Members)
            {
                if (member is BilSimpleMemberDeclaration simple
                    && VmTypeSheetBuilder.SignatureKeyOf(simple.Symbol)
                        .StartsWith("dispose()@", StringComparison.Ordinal))
                {
                    disposeSymbol = simple.Symbol;
                    break;
                }
            }
            if (disposeSymbol == null
                || !disposableSheet.OffsetBySymbol.TryGetValue(disposeSymbol,
                       out var relative))
            {
                return null;
            }
            var slotIndex = baseOffset + relative;
            if (slotIndex < 0 || slotIndex >= sheet.Slots.Count)
            {
                return null;
            }
            return sheet.Slots[slotIndex].ImplSymbol;
        }

        // dispose 进入即置位（调用侧：BilInvokeExecution.InvokeResolved /
        // PushSuperFrame 压帧前）。按方法符号身份判定——impl 恰为 receiver
        // 实际类型的 dispose 槽目标；调用了但抛异常也算负责过（与 native
        // prologue 置位同语义），async dispose 的 stub 进入即命中
        internal void MarkDisposedIfDisposeImpl(string implSymbol, VmObject receiver)
        {
            if (!receiver.IsDisposalTracked || receiver.DisposedMarked)
            {
                return;
            }
            var target = DisposeSlotTargetOf(receiver.TypeRef);
            if (target != null
                && string.Equals(target, implSymbol, StringComparison.Ordinal))
            {
                receiver.DisposedMarked = true;
            }
        }

        // 派发时机对齐 native entry stub：main/drain 之后、失败汇总之前。
        // 逼 GC（Collect + WaitForPendingFinalizers）让未 dispose 对象的
        // finalizer 入队事件，再逐条经 VM 真构造 UndisposedResourceException
        //（走真 init——同 LanguageException 的正常派发调用机制，此处借
        // 一次性协程同步驱动）并调 GlobalExceptionHandler.dispatch。
        // dispatch 内抛出的异常作为返回值上交，走 Run 的未捕获异常归宿。
        // VM 静态槽（_statics/_singletons）保持根住：不模拟静态槽退出
        // 清理的销毁检查——native 侧晚到事件走 C 默认打印（atexit flush），
        // 两宿主 stdout 都不产生静态末批事件，对拍安全
        internal VmException? CollectAndDispatchUndisposed()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var pending = UndisposedTracker.DrainAll();
            if (pending.Count == 0)
            {
                return null;
            }
            var dispatch = FindRuntimeFunction(
                "core::GlobalExceptionHandler$.static.dispatch(");
            if (dispatch == null)
            {
                return null;   // 无 stdlib 的合成模块无事件通道
            }
            var coroutine = new VmCoroutine(Dispatch);
            coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Running);
            coroutine.PushFrame(SingletonBootstrapFunction, Array.Empty<VmValue>(), null, this);
            foreach (var typeName in pending)
            {
                try
                {
                    var exception = AllocateObject("core::UndisposedResourceException");
                    if (TryFindInit("core::UndisposedResourceException",
                            new VmValue[] { new VmString(typeName) },
                            new[] { ".string" }, out var init)
                        && init.Length > 0)
                    {
                        BilInvokeExecution.InvokeValues(this, coroutine, init,
                            new VmValue[] { exception, new VmString(typeName) },
                            resultSlot: null);
                        StepBackTo(coroutine, 1);
                    }
                    else
                    {
                        // 防御性回落（init 匹配落空）：直写字段，同
                        // LanguageException 回落路径口径
                        exception.WriteField("core::Exception#message@.string",
                            new VmString("对象在销毁前从未调用 dispose()：" + typeName));
                        exception.WriteField(
                            "core::UndisposedResourceException#resourceType@.string",
                            new VmString(typeName));
                    }
                    if (coroutine.State != VmCoroutineState.Running)
                    {
                        return coroutine.Failure
                            ?? new VmException("undisposed 事件构造失败：" + typeName);
                    }
                    BilInvokeExecution.InvokeValues(this, coroutine, dispatch.Symbol,
                        new VmValue[] { exception }, resultSlot: null);
                    StepBackTo(coroutine, 1);
                    if (coroutine.State != VmCoroutineState.Running)
                    {
                        return coroutine.Failure
                            ?? new VmException("undisposed 事件派发失败：" + typeName);
                    }
                }
                catch (VmException failure)
                {
                    return failure;
                }
            }
            return null;
        }

        // 同步驱动协程回落至指定栈深（ConstructSingleton 同式；dispatch
        // 与 stdlib init 均为同步 fn，handler 是同步 Action）
        private void StepBackTo(VmCoroutine coroutine, int depth)
        {
            while (coroutine.CallStack.Count > depth
                && coroutine.State == VmCoroutineState.Running
                && !coroutine.HasAbruptCompletion)
            {
                coroutine.Step(this);
            }
        }

        // 同签名 init 重载的精确甄别（init(text: String) vs
        // init(typeName: String)）：TryFindInit 按声明序先中前者，
        // 这里按首形参名锁定目标重载，其余匹配规则与 TryFindInit 一致
        private bool TryFindInitByFirstParamName(string typeRef, string firstParamName,
            IReadOnlyList<VmValue> arguments, IReadOnlyList<string> argumentStaticTypes,
            out string initSymbol)
        {
            initSymbol = "";
            var declaration = FindType(typeRef);
            if (declaration == null)
            {
                return false;
            }
            foreach (var member in declaration.Members)
            {
                if (member is not BilSimpleMemberDeclaration simple
                    || !HasKeyword(simple, BilKeyword.Init)
                    || !BilVerificationContext.TryParseMethodSymbol(simple.Symbol,
                        out _, out _, out var parameters, out _)
                    || parameters.Count == 0
                    || parameters[0].Name != firstParamName
                    || !ParametersMatch(parameters, arguments, argumentStaticTypes))
                {
                    continue;
                }
                initSymbol = simple.Symbol;
                return true;
            }
            return false;
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
                var found = MatchOperatorOn(declaration, operatorName, valueArguments,
                    skipGenericPrefix: callOnly);
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
                if (OperatorParamsMatch(member.Symbol, valueArguments,
                        skipGenericPrefix: callOnly))
                {
                    return member.Symbol;
                }
            }
            return null;
        }

        private string? MatchOperatorOn(BilTypeDeclaration declaration, string operatorName,
            IReadOnlyList<VmValue> valueArguments, bool skipGenericPrefix)
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
                if (OperatorParamsMatch(simple.Symbol, valueArguments, skipGenericPrefix))
                {
                    return simple.Symbol;
                }
            }
            return null;
        }

        private bool OperatorParamsMatch(string methodSymbol, IReadOnlyList<VmValue> valueArguments,
            bool skipGenericPrefix = false, string? receiverType = null)
        {
            if (!BilVerificationContext.TryParseMethodSymbol(methodSymbol,
                    out _, out _, out var parameters, out _))
            {
                return false;
            }
            var ordinary = new List<(string Name, string TypeRef)>();
            // 泛型宿主的 callable 参数按实际 receiver 具化后匹配。
            var receiverDeclaration = receiverType == null ? null : FindType(receiverType);
            var substitution = receiverDeclaration == null ? null
                : VmTypeSheetBuilder.BuildSubstitution(receiverType!, receiverDeclaration);
            foreach (var parameter in parameters)
            {
                if (parameter.Name.StartsWith(".generic.", StringComparison.Ordinal)
                    || parameter.Name.StartsWith(".vargs.", StringComparison.Ordinal)
                    || parameter.Name.StartsWith(".kwargs.", StringComparison.Ordinal))
                {
                    continue;
                }
                ordinary.Add(substitution == null ? parameter : (parameter.Name,
                    VmTypeSheetBuilder.SubstituteGenericArguments(parameter.TypeRef, substitution)));
            }
            if (skipGenericPrefix)
            {
                // §15.3：泛型 $$call 调用点前部平铺 typeid/包前缀；按 fn
                // 定义侧 .generic.* / 值包 hidden 条目数跳过后再逐值比对
                // （对齐 BilVerifier.TryFindCallOperator）。
                var genericHidden = new List<BilArgDeclaration>();
                var packArguments = new List<BilArgDeclaration>();
                var callee = FindFunction(methodSymbol);
                if (callee != null)
                {
                    foreach (var arg in callee.Args)
                    {
                        if (arg.Name.StartsWith(".generic.", StringComparison.Ordinal))
                        {
                            // 宿主 typeid 由 PushFrame 从 this 注入，不占调用实参数。
                            var name = arg.Name.Substring(".generic.".Length);
                            if (substitution == null || !substitution.ContainsKey(name))
                                genericHidden.Add(arg);
                        }
                        else if (arg.Name.StartsWith(".vargs.", StringComparison.Ordinal)
                            || arg.Name.StartsWith(".kwargs.", StringComparison.Ordinal))
                        {
                            packArguments.Add(arg);
                        }
                    }
                }
                var expectedCount = genericHidden.Count + ordinary.Count + packArguments.Count;
                if (valueArguments.Count != expectedCount)
                {
                    return false;
                }
                var valueStart = genericHidden.Count;
                for (var i = 0; i < ordinary.Count; i++)
                {
                    if (!TypeAssignable(valueArguments[valueStart + i].TypeRef,
                            ordinary[i].TypeRef))
                    {
                        return false;
                    }
                }
                for (var i = 0; i < packArguments.Count; i++)
                {
                    if (!TypeAssignable(
                            valueArguments[valueStart + ordinary.Count + i].TypeRef,
                            packArguments[i].TypeRef))
                    {
                        return false;
                    }
                }
                return true;
            }
            // 运算符：值实参不含 hidden typeid；泛型占位由 TypesEqual
            // 的 .generic< 降级匹配；typeid 在命中后由 InjectOperatorTypeIds
            // 从实参类型结构推断补入。
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
            // 嵌套类型同样需要归一化内建别名（例如 nullable<core::i32>）。
            if (BilVerificationContext.NormalizeTypeRef(from)
                == BilVerificationContext.NormalizeTypeRef(to)) return true;
            if (from == ".null" && BilVerificationContext.NormalizeTypeRef(to)
                    .StartsWith("core::Nullable<", StringComparison.Ordinal)) return true;
            var normalizedTarget = BilVerificationContext.NormalizeTypeRef(to);
            if (normalizedTarget.StartsWith("core::Nullable<", StringComparison.Ordinal))
                return TypeAssignable(from, normalizedTarget.Substring(15, normalizedTarget.Length - 16));
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
            IReadOnlyList<VmValue> arguments, VmContext? context = null)
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

            // 嵌套类修正（review-20260910 #02）：.this 构造实参只覆盖类型
            // 自身的 GP（.type Ring.RingEnum = class generic(TItem)），帧槽
            // 却按「外层宿主链 → 自身 → 方法级」排列——位置直灌会把自身
            // 实参错绑到外层槽。有类型声明可查时改按名绑定：自身槽 ← 实例
            // 实参，外层槽 ← 构造点捕获（New 写入实例隐藏槽），其余（方法
            // 级/未捕获）走既有降级
            Dictionary<string, string>? ownBindings = null;
            HashSet<string>? ownNames = null;
            HashSet<string>? outerNames = null;
            List<string>? outerCaptured = null;
            if (context != null && hasThis && arguments.Count > 0 && inferred.Count > 0)
            {
                var declaration = context.FindType(arguments[0].TypeRef);
                if (declaration != null
                    && declaration.GenericParameters.Count == inferred.Count
                    && declaration.GenericParameters.Count < genericSlots.Count
                    && BilVerificationContext.TryParseMethodSymbol(function.Symbol,
                        out var ownerSymbol, out _, out _, out _))
                {
                    ownBindings = new Dictionary<string, string>(StringComparer.Ordinal);
                    ownNames = new HashSet<string>(StringComparer.Ordinal);
                    for (var i = 0; i < inferred.Count; i++)
                    {
                        ownBindings[declaration.GenericParameters[i]] = inferred[i];
                        ownNames.Add(declaration.GenericParameters[i]);
                    }
                    outerNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var name in OuterGenericParametersOf(context, ownerSymbol))
                    {
                        outerNames.Add(name);
                    }
                    if (arguments[0] is IVmFieldHost fieldHost
                        && fieldHost.TryReadHidden(HiddenOuterGenericsKey, out var captured)
                        && captured is VmArray capturedArray)
                    {
                        outerCaptured = new List<string>(capturedArray.Length);
                        for (var i = 0; i < capturedArray.Length; i++)
                        {
                            outerCaptured.Add(
                                capturedArray.GetAt(i) is VmString text ? text.Value : "");
                        }
                    }
                }
            }

            var result = new List<VmValue>(slots.Count);
            var restIndex = thisOffset;
            var inferredIndex = 0;
            var hiddenConsumed = 0;
            var outerIndex = 0;
            foreach (var slot in slots)
            {
                if (slot.Name == ".this")
                {
                    result.Add(arguments[0]);
                    continue;
                }
                if (slot.Name.StartsWith(".generic.", StringComparison.Ordinal))
                {
                    var genericName = slot.Name.Substring(".generic.".Length);
                    if (passedHidden == genericSlots.Count)
                    {
                        // 调用点显式传足隐藏 typeid：恒按位置直灌
                        result.Add(arguments[restIndex++]);
                    }
                    else if (ownBindings != null && ownNames!.Contains(genericName))
                    {
                        // 类型自身 GP：按名绑定实例构造实参
                        result.Add(new VmTypeId(ownBindings[genericName]));
                    }
                    else if (ownBindings != null && outerNames!.Contains(genericName))
                    {
                        // 外层宿主 GP：读构造点捕获；未捕获/未绑定回落 .any
                        var capturedText = "";
                        if (outerCaptured != null && outerIndex < outerCaptured.Count)
                        {
                            capturedText = outerCaptured[outerIndex];
                        }
                        outerIndex++;
                        result.Add(new VmTypeId(capturedText.Length > 0 ? capturedText : ".any"));
                    }
                    else if (ownBindings != null)
                    {
                        // 方法级 GP：调用点显式传的隐藏 typeid 按序消费
                        // （Repo<T>.mix<U> 只显式传 U 的份额），无传才降级 .any
                        if (restIndex < arguments.Count
                            && arguments[restIndex] is VmTypeId)
                        {
                            result.Add(arguments[restIndex++]);
                        }
                        else
                        {
                            result.Add(new VmTypeId(".any"));
                        }
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
                    // MW11c 棒4a：同名不同元数类型（SYNTAX §15.3，首例 =
                    // stdlib Task / Task<TReturn>）的 declaration.Symbol 相同
                    // ——裸符号键必须归 arity-0 声明（FindType 裸 typeRef 的
                    // 语义即非泛型）；构造 typeRef（带 <>）走 _typesByKey
                    // 的 arity 键不受影响。旧实现后者覆盖前者，导致非泛型
                    // Task 的 sheet/派发全部错绑到 Task<TReturn>（字段键
                    // 错位：waiter 排空读空、唤醒丢失挂死）
                    if (!_types.TryGetValue(type.Symbol, out var existing)
                        || existing.GenericParameters.Count > type.GenericParameters.Count)
                    {
                        _types[type.Symbol] = type;
                    }
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
                    // 泛型元数和实参均参与身份；裸名不是构造类型的 ABI 视图。
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
            // 同一类型的两种合法 BIL 拼写（§6.1 非 compact 形 ".string, .i64"
            // 与 §5.2 符号内嵌 compact 形 ".string,.i64"）必须判等——双泛型
            // 实参的构造类型在此踩过空白差异（review-20260910 #06），归一化
            // 走验证器同一轮子（别名 + 构造头 + 空白全归一）
            return BilVerificationContext.NormalizeTypeRef(left)
                == BilVerificationContext.NormalizeTypeRef(right);
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

        internal static string MethodNameOf(string symbol) => BilLogicalName.Method(symbol);

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
            // 字面量词法解码唯一归口 BilScalarLiteral（§19.1），此处只包装异常
            var text = resource.LiteralText;
            try
            {
                switch (resource.Type)
                {
                    case BilScalarType.String:
                        return new VmString(BilScalarLiteral.DecodeString(text));
                    case BilScalarType.Bool:
                        return new VmBool(text == "true");
                    case BilScalarType.Char:
                        return new VmChar(BilScalarLiteral.DecodeChar(text));
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
                        return new VmU64(BilScalarLiteral.ParseUnsigned(text));
                    case BilScalarType.F32:
                        return new VmF32(float.Parse(text, CultureInfo.InvariantCulture));
                    case BilScalarType.F64:
                        return new VmF64(double.Parse(text, CultureInfo.InvariantCulture));
                    default:
                        throw new VmException("不支持的标量资源类型：" + resource.Type);
                }
            }
            catch (FormatException ex)
            {
                throw new VmException(ex.Message);
            }
        }

    }
}
