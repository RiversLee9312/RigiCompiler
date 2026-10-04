using System.Globalization;
using System.IO;
using System.Text;

namespace RigiCompiler.Bil.Vm
{
    public sealed partial class VmContext
    {
        // Symbols 职责；与主文件共享同一类型、字段及生命周期。

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

    }
}
