using System.Collections.Generic;

namespace RigiCompiler.Bil
{
    // BilVerifier 符号检查（§21.2 符号验证 + §21.7 泛型与参数包 + §21.8
    // 可见性与类型属性的声明侧）。指令内的符号引用可解析性在 Types.cs
    // 逐指令遍历时检查。

    public static partial class BilVerifier
    {
        // ===== §21.2 符号验证（声明侧）+ §21.8 声明侧 =====
        private static void VerifyDeclarations(BilVerificationContext context,
            List<BilVerificationError> errors)
        {
            // 段内符号不重复（类型符号与成员符号各自唯一）
            VerifySectionDuplicates(context.Module.LocalSymbols, "LocalSymbols", errors);
            VerifySectionDuplicates(context.Module.ExternalSymbols, "ExternalSymbols", errors);

            // fn 定义必须对应 LocalSymbols 方法声明（§9.1）；符号形态合法。
            // S11e：builtin 宿主的编译器合成 fn 定义（如 Any.call??? 默认
            // 实现）无声明可对应——内建类型不进符号段（BilVerificationContext
            // PredefinedTypes 硬编码环境），此处豁免（IsPredefinedTypeHost）
            var fnSymbols = new HashSet<string>();
            foreach (var function in context.Module.Functions)
            {
                if (function.Symbol == BilSpellings.SuperReservedFunction)
                {
                    errors.Add(new BilVerificationError("21.2", function.Symbol,
                        "保留符号 ..super 不得声明为普通 fn"));
                    continue;
                }
                if (!context.LocalMethodSymbols.Contains(function.Symbol)
                    && !IsBuiltinHostedFunction(context, function.Symbol))
                {
                    errors.Add(new BilVerificationError("21.2", function.Symbol,
                        "fn 定义在 LocalSymbols 中没有对应方法声明"));
                }
                if (!BilVerificationContext.TryParseMethodSymbol(function.Symbol,
                        out _, out _, out _, out _))
                {
                    errors.Add(new BilVerificationError("21.1", function.Symbol,
                        "fn 符号不符合 canonical 方法符号语法（§5.2）"));
                }
                if (!fnSymbols.Add(function.Symbol))
                {
                    errors.Add(new BilVerificationError("21.2", function.Symbol, "fn 定义重复"));
                }
            }

            // 资源类型引用可解析（§21.3：所有资源有类型）
            foreach (var resource in context.Module.Resources)
            {
                switch (resource)
                {
                    case BilNullResource nullResource:
                        if (!context.IsResolvableTypeRef(nullResource.TypeRef))
                        {
                            errors.Add(new BilVerificationError("21.2", "Resources",
                                $"null 资源 \"{resource.Name}\" 的类型不可解析 \"{nullResource.TypeRef}\""));
                        }
                        break;
                    case BilSwitchTableResource switchTable:
                        if (!context.IsResolvableTypeRef(switchTable.SelectorTypeRef))
                        {
                            errors.Add(new BilVerificationError("21.2", "Resources",
                                $"switch-table \"{resource.Name}\" 的 selector 类型不可解析 " +
                                $"\"{switchTable.SelectorTypeRef}\""));
                        }
                        break;
                    case BilCatchTableResource catchTable:
                        foreach (var entry in catchTable.Entries)
                        {
                            if (!context.IsResolvableTypeRef(entry.ExceptionType.TypeRef))
                            {
                                errors.Add(new BilVerificationError("21.2", "Resources",
                                    $"catch-table \"{resource.Name}\" 的异常类型不可解析 " +
                                    $"\"{entry.ExceptionType.TypeRef}\""));
                            }
                        }
                        break;
                }
            }

            // 成员声明规则（native/static 一致性/修饰符重复/fn 一一对应）
            var entrypointCount = 0;
            foreach (var (ownerType, declaration, isLocal) in context.MemberEntries)
            {
                VerifyMemberDeclaration(context, ownerType, declaration, isLocal, errors);
                if (isLocal && HasKeyword(declaration, BilKeyword.Entrypoint))
                {
                    entrypointCount++;
                }
            }
            if (entrypointCount > 1)
            {
                errors.Add(new BilVerificationError("21.2", "LocalSymbols",
                    $"entrypoint 方法必须唯一（实际 {entrypointCount}）"));
            }

            // 类型声明（§8.2 修饰符矩阵 + 继承类型可解析）
            foreach (var section in new[] { context.Module.LocalSymbols, context.Module.ExternalSymbols })
            {
                foreach (var entry in section)
                {
                    if (entry is BilTypeDeclaration type)
                    {
                        VerifyTypeDeclaration(context, type, errors);
                    }
                }
            }

            // enum case 声明（§8.5）：宿主必须是 enum-struct（查不到声明时降级跳过）；
            // discriminant res(R) 必须引用 Resources 中已登记的资源
            foreach (var (qualifiedName, caseDeclaration) in context.CaseDeclarations)
            {
                var dot = qualifiedName.LastIndexOf('.');
                if (dot > 0
                    && context.TryGetTypeDeclaration(qualifiedName.Substring(0, dot), out var owner)
                    && owner.Kind != BilTypeKind.EnumStruct)
                {
                    errors.Add(new BilVerificationError("21.2", qualifiedName,
                        $"case 声明的宿主类型不是 enum-struct（{BilSpellings.Of(owner.Kind)}）"));
                }
                if (caseDeclaration.DiscriminantResource != null
                    && !context.ResourcesByName.ContainsKey(caseDeclaration.DiscriminantResource))
                {
                    errors.Add(new BilVerificationError("21.2", qualifiedName,
                        $"discriminant 资源 \"{caseDeclaration.DiscriminantResource}\" 未登记"));
                }
            }
        }

        private static void VerifySectionDuplicates(List<BilSymbolSectionEntry> section,
            string sectionName, List<BilVerificationError> errors)
        {
            var symbols = new HashSet<string>();
            foreach (var entry in section)
            {
                switch (entry)
                {
                    case BilTypeDeclaration type:
                        // S10：类型符号判重键 = canonical + 泛型参数个数——
                        // Task 与 Task\<TResult\> 同名不同元数合法共存（SYNTAX
                        // §15.3，BIL §8.2 generic(...) 子句区分）
                        var typeKey = type.GenericParameters.Count == 0
                            ? type.Symbol
                            : type.Symbol + "<" + type.GenericParameters.Count + ">";
                        if (!symbols.Add(typeKey))
                        {
                            errors.Add(new BilVerificationError("21.2", sectionName,
                                $"类型符号重复 \"{type.Symbol}\""));
                        }
                        var memberSymbols = new HashSet<string>();
                        foreach (var member in type.Members)
                        {
                            var memberSymbol = MemberSymbolOf(member);
                            if (memberSymbol != null && !memberSymbols.Add(memberSymbol))
                            {
                                errors.Add(new BilVerificationError("21.2", sectionName,
                                    $"成员符号重复 \"{memberSymbol}\""));
                            }
                        }
                        break;
                    case BilMemberDeclaration member:
                        var symbol = MemberSymbolOf(member);
                        if (symbol != null && !symbols.Add(symbol))
                        {
                            errors.Add(new BilVerificationError("21.2", sectionName,
                                $"裸成员符号重复 \"{symbol}\""));
                        }
                        break;
                }
            }
        }

        private static string? MemberSymbolOf(BilMemberDeclaration member)
        {
            return member switch
            {
                BilSimpleMemberDeclaration simple => simple.Symbol,
                BilCaseDeclaration caseDeclaration => caseDeclaration.QualifiedName,
                _ => null,
            };
        }

        private static void VerifyMemberDeclaration(BilVerificationContext context,
            string? ownerType, BilSimpleMemberDeclaration declaration, bool isLocal,
            List<BilVerificationError> errors)
        {
            var symbol = declaration.Symbol;

            if (symbol == BilSpellings.SuperReservedFunction)
            {
                errors.Add(new BilVerificationError("21.2", symbol,
                    "保留符号 ..super 不得声明为普通成员"));
                return;
            }

            // §8.3/§8.4：声明关键字与符号中 .static. 标记必须一致
            var isMethod = declaration.Kind is BilMemberKind.Method or BilMemberKind.StaticMethod;
            var symbolStatic = false;
            var parsed = isMethod
                ? BilVerificationContext.TryParseMethodSymbol(symbol, out _, out symbolStatic, out _, out _)
                : BilVerificationContext.TryParseFieldSymbol(symbol, out _, out symbolStatic, out _);
            if (!parsed)
            {
                errors.Add(new BilVerificationError("21.1", symbol,
                    "成员符号不符合 canonical 符号语法（§5.2）"));
            }
            else
            {
                var expectStatic = declaration.Kind is BilMemberKind.StaticMethod
                    or BilMemberKind.StaticField;
                if (expectStatic != symbolStatic)
                {
                    errors.Add(new BilVerificationError("21.2", symbol,
                        $"声明关键字 {BilSpellings.Of(declaration.Kind)} 与符号 .static. 标记不一致"));
                }
            }

            // 修饰符不得重复（同访问级两次 / 同关键字两次）
            VerifyModifierDuplicates(declaration.Modifiers, symbol, errors);

            // §8.3.1/§21.8：wrapped(W) 可出现在类型/字段声明上；方法上非法
            VerifyWrappedModifiers(context, declaration.Modifiers, symbol, allowWrapped: !isMethod,
                errors);

            if (!isMethod)
            {
                // §8.3：backing 与 computed 是互斥的存储形态标记
                if (HasKeyword(declaration, BilKeyword.Backing)
                    && HasKeyword(declaration, BilKeyword.Computed))
                {
                    errors.Add(new BilVerificationError("21.8", symbol,
                        "backing 与 computed 不得共存"));
                }
                return;
            }

            // §8.4/§21.8 访问器修饰合法性：getter(FIELD)/setter(FIELD) 的
            // FIELD 必须可解析为已声明字段符号（local + external 声明集合）；
            // 修饰与方法符号形态必须一致（getter ↔ $.get. 形态、
            // setter ↔ $.set. 形态，§5.2）
            foreach (var modifier in declaration.Modifiers)
            {
                if (modifier is not BilAccessorModifier accessor) continue;
                if (!context.FieldSymbols.Contains(accessor.FieldSymbol))
                {
                    errors.Add(new BilVerificationError("21.8", symbol,
                        $"访问器修饰引用的字段符号不可解析 \"{accessor.FieldSymbol}\""));
                }
                if (!BilVerificationContext.TryParseAccessorForm(symbol, out var setterForm)
                    || setterForm != (accessor.Kind == BilAccessorKind.Setter))
                {
                    errors.Add(new BilVerificationError("21.8", symbol,
                        $"{BilSpellings.Of(accessor.Kind)}(...) 修饰与方法符号形态不符" +
                        "（应为 $[.static].get.名 / $[.static].set.名，§5.2）"));
                }
            }

            // §9.7/§21.8：..init.wrapper 签名与修饰符
            var nameSegment = MethodNameSegment(symbol);
            if (nameSegment == BilSpellings.InitWrapperMethodName)
            {
                if (!BilVerificationContext.TryParseMethodSymbol(symbol,
                        out _, out var initWrapperStatic, out _, out var initWrapperRet)
                    || initWrapperStatic
                    || initWrapperRet != ".void")
                {
                    errors.Add(new BilVerificationError("21.8", symbol,
                        "..init.wrapper 必须是返回 .void 的实例方法（§9.7）"));
                }
                if (!HasKeyword(declaration, BilKeyword.CompilerGenerated)
                    || !HasAccessibility(declaration, BilAccessibility.Private))
                {
                    errors.Add(new BilVerificationError("21.8", symbol,
                        "..init.wrapper 必须带 priv 与 compiler-generated（§9.7）"));
                }
            }
            // §9.7：..init.field.<名> 保留名族——字段初始化器方法，与
            // ..init.wrapper 同形状约束（返回 .void 的零参实例方法 +
            // priv + compiler-generated）；子类 override 版同形
            if (nameSegment != null && nameSegment.StartsWith(
                    BilSpellings.InitFieldMethodPrefix, StringComparison.Ordinal))
            {
                if (!BilVerificationContext.TryParseMethodSymbol(symbol,
                        out _, out var initFieldStatic, out var initFieldParams,
                        out var initFieldRet)
                    || initFieldStatic
                    || initFieldParams.Count != 0
                    || initFieldRet != ".void")
                {
                    errors.Add(new BilVerificationError("21.8", symbol,
                        "..init.field.* 必须是返回 .void 的零参实例方法（§9.7）"));
                }
                if (!HasKeyword(declaration, BilKeyword.CompilerGenerated)
                    || !HasAccessibility(declaration, BilAccessibility.Private))
                {
                    errors.Add(new BilVerificationError("21.8", symbol,
                        "..init.field.* 必须带 priv 与 compiler-generated（§9.7）"));
                }
            }
            // §8.4.1/§9.3（N1）：..globals.init——全局/静态字段初始值 fn，
            // 返回 .void、无参数、无 .this（非实例方法）+ compiler-generated
            if (nameSegment == BilSpellings.GlobalsInitFunctionName)
            {
                if (!BilVerificationContext.TryParseMethodSymbol(symbol,
                        out var globalsInitOwner, out _, out var globalsInitParams,
                        out var globalsInitRet)
                    || globalsInitOwner.Length != 0
                    || globalsInitParams.Count != 0
                    || globalsInitRet != ".void")
                {
                    errors.Add(new BilVerificationError("21.8", symbol,
                        "..globals.init 必须是返回 .void 的无参全局函数（§8.4.1）"));
                }
                if (!HasKeyword(declaration, BilKeyword.CompilerGenerated))
                {
                    errors.Add(new BilVerificationError("21.8", symbol,
                        "..globals.init 必须带 compiler-generated（§8.4.1）"));
                }
            }

            // §8.4/§21.8 wrapper-proxy(PROXY_KIND)（M88）：只允许在 wrapper
            // 类型内、名以 `.proxy.` 开头的方法上；`.proxy.` 名 ↔ 修饰符
            // 双向一致；kind ↔ 形状类别（名以 `.*` 结尾 → wildcard，否则
            // specific）。烘焙特化/original/router 归 Middleware，不再出现
            // 于 BIL 文本
            var isProxyTemplate = nameSegment != null && nameSegment.StartsWith(".proxy.");
            BilWrapperProxyModifier? wrapperProxy = null;
            foreach (var modifier in declaration.Modifiers)
            {
                if (modifier is BilWrapperProxyModifier found) wrapperProxy = found;
            }
            if (wrapperProxy != null && !isProxyTemplate)
            {
                errors.Add(new BilVerificationError("21.8", symbol,
                    "wrapper-proxy(...) 只允许在名以 .proxy. 开头的方法上（§8.4）"));
            }
            if (wrapperProxy == null && isProxyTemplate)
            {
                errors.Add(new BilVerificationError("21.8", symbol,
                    ".proxy. 方法缺少 wrapper-proxy(...) 修饰符（§8.4）"));
            }
            if (isProxyTemplate)
            {
                // §21.2：proxy 模板必须声明在 wrapper 类型内
                if (ownerType == null
                    || !context.TryGetTypeDeclaration(ownerType, out var proxyHost)
                    || proxyHost.Kind != BilTypeKind.Wrapper)
                {
                    errors.Add(new BilVerificationError("21.2", symbol,
                        ".proxy. 方法必须声明在 wrapper 类型内（§8.4）"));
                }
            }
            if (wrapperProxy != null && isProxyTemplate && nameSegment != null)
            {
                var expectWildcard = nameSegment.EndsWith(".*");
                if (expectWildcard && wrapperProxy.Kind != BilProxyKind.Wildcard)
                {
                    errors.Add(new BilVerificationError("21.8", symbol,
                        "通配 proxy（名以 .* 结尾）必须带 wrapper-proxy(wildcard)（§8.4）"));
                }
                if (!expectWildcard && wrapperProxy.Kind != BilProxyKind.Specific)
                {
                    errors.Add(new BilVerificationError("21.8", symbol,
                        "具名 proxy 必须带 wrapper-proxy(specific)（§8.4）"));
                }
            }

            // §21.2：native 方法不得有 fn 定义，且必须恰好各带一个
            // symbol("...") 与 lib("...")；非 native 本地方法必须有 fn 定义
            var isNative = HasKeyword(declaration, BilKeyword.Native);
            var symbolModifierCount = 0;
            var libraryModifierCount = 0;
            foreach (var modifier in declaration.Modifiers)
            {
                if (modifier is BilNativeSymbolModifier) symbolModifierCount++;
                if (modifier is BilNativeLibraryModifier) libraryModifierCount++;
            }
            var hasBody = false;
            foreach (var function in context.Module.Functions)
            {
                if (function.Symbol == symbol)
                {
                    hasBody = true;
                    break;
                }
            }
            if (isNative)
            {
                if (symbolModifierCount != 1 || libraryModifierCount != 1)
                {
                    errors.Add(new BilVerificationError("21.2", symbol,
                        "native 方法必须恰好各带一个 symbol(\"...\") 与 lib(\"...\") 修饰符"));
                }
                if (hasBody)
                {
                    errors.Add(new BilVerificationError("21.2", symbol,
                        "native 方法不得存在 fn 定义"));
                }
            }
            else if (isLocal && !hasBody)
            {
                // 接口成员与 abstract 方法无 body 是声明语义，不是缺失
                var hostIsInterface = ownerType != null
                    && context.TypeDeclarations.TryGetValue(ownerType, out var host)
                    && host.Kind == BilTypeKind.Interface;
                if (!hostIsInterface && !HasKeyword(declaration, BilKeyword.Abstract))
                {
                    errors.Add(new BilVerificationError("21.2", symbol,
                        "非 native 本地方法缺少 fn 定义"));
                }
            }
        }

        private static void VerifyTypeDeclaration(BilVerificationContext context,
            BilTypeDeclaration type, List<BilVerificationError> errors)
        {
            VerifyModifierDuplicates(type.Modifiers, type.Symbol, errors);

            var rich = HasKeyword(type.Modifiers, BilKeyword.Rich);
            var open = HasKeyword(type.Modifiers, BilKeyword.Open);
            var abstractKeyword = HasKeyword(type.Modifiers, BilKeyword.Abstract);
            var singleton = HasKeyword(type.Modifiers, BilKeyword.Singleton);
            var shared = HasKeyword(type.Modifiers, BilKeyword.Shared);

            // §8.2 修饰符矩阵
            if (rich && type.Kind is not (BilTypeKind.Struct or BilTypeKind.EnumStruct
                or BilTypeKind.Wrapper))
            {
                errors.Add(new BilVerificationError("21.8", type.Symbol,
                    $"rich 仅适用于 struct/enum-struct/wrapper（{BilSpellings.Of(type.Kind)}）"));
            }
            if (type.Kind == BilTypeKind.Wrapper && !rich)
            {
                errors.Add(new BilVerificationError("21.8", type.Symbol,
                    "wrapper 类型必须显式带 rich"));
            }
            if (type.Kind == BilTypeKind.EnumStruct && open)
            {
                errors.Add(new BilVerificationError("21.8", type.Symbol, "enum-struct 不得 open"));
            }
            if (type.Kind is BilTypeKind.Struct or BilTypeKind.EnumStruct
                && !rich && (open || abstractKeyword))
            {
                errors.Add(new BilVerificationError("21.8", type.Symbol,
                    "非 rich struct/enum-struct 不得带 open 或 abstract"));
            }
            if (singleton && !shared)
            {
                errors.Add(new BilVerificationError("21.8", type.Symbol,
                    "singleton 类型必须同时带 shared"));
            }

            // §8.7/§21.8：companion（声明类的嵌套类，名以 ..companion 收尾）结构
            if (type.Symbol.EndsWith(BilSpellings.CompanionTypeName, StringComparison.Ordinal))
            {
                if (type.Kind != BilTypeKind.Class)
                {
                    errors.Add(new BilVerificationError("21.8", type.Symbol,
                        "companion 必须是 class（§8.7）"));
                }
                if (!singleton || !shared)
                {
                    errors.Add(new BilVerificationError("21.8", type.Symbol,
                        "companion 必须同时带 singleton 与 shared（§8.7）"));
                }
                var hasInstanceMethod = false;
                foreach (var member in type.Members)
                {
                    if (member is BilSimpleMemberDeclaration simple
                        && simple.Kind == BilMemberKind.Method)
                    {
                        hasInstanceMethod = true;
                        break;
                    }
                }
                if (!hasInstanceMethod)
                {
                    errors.Add(new BilVerificationError("21.8", type.Symbol,
                        "companion 必须至少有一个实例方法（§8.7）"));
                }
            }

            // §9.7：每实体至多一个 ..init.wrapper
            var initWrapperCount = 0;
            foreach (var member in type.Members)
            {
                if (member is BilSimpleMemberDeclaration simple
                    && MethodNameSegment(simple.Symbol) == BilSpellings.InitWrapperMethodName)
                {
                    initWrapperCount++;
                }
            }
            if (initWrapperCount > 1)
            {
                errors.Add(new BilVerificationError("21.8", type.Symbol,
                    "同一实体至多一个 ..init.wrapper 方法（§9.7）"));
            }

            // §8.3.1/§21.8：类型声明上的 wrapped(W)
            VerifyWrappedModifiers(context, type.Modifiers, type.Symbol, allowWrapped: true, errors);

            // extends/implements 类型可解析（§21.2）
            if (type.ExtendsType != null && !context.IsResolvableTypeRef(type.ExtendsType))
            {
                errors.Add(new BilVerificationError("21.2", type.Symbol,
                    $"extends 类型不可解析 \"{type.ExtendsType}\""));
            }
            foreach (var interfaceType in type.ImplementsTypes)
            {
                if (!context.IsResolvableTypeRef(interfaceType))
                {
                    errors.Add(new BilVerificationError("21.2", type.Symbol,
                        $"implements 类型不可解析 \"{interfaceType}\""));
                }
            }
        }

        // §8.3.1 wrapped(WRAPPER_TYPE_REF)：合法位置 = 类型/字段声明；
        // WRAPPER_TYPE_REF 必须是 wrapper 类型（查不到降级通过）
        private static void VerifyWrappedModifiers(BilVerificationContext context,
            IReadOnlyList<BilModifier> modifiers, string location, bool allowWrapped,
            List<BilVerificationError> errors)
        {
            foreach (var modifier in modifiers)
            {
                if (modifier is not BilWrappedModifier wrapped) continue;
                if (!allowWrapped)
                {
                    errors.Add(new BilVerificationError("21.8", location,
                        "wrapped(...) 只允许在类型声明或字段声明上（§8.3.1）"));
                    continue;
                }
                if (!context.IsResolvableTypeRef(wrapped.WrapperTypeRef))
                {
                    // 查不到降级通过（零误报优先）
                    continue;
                }
                if (context.TryGetTypeDeclaration(wrapped.WrapperTypeRef, out var wrapperDecl)
                    && wrapperDecl.Kind != BilTypeKind.Wrapper)
                {
                    errors.Add(new BilVerificationError("21.8", location,
                        $"wrapped(...) 的类型 \"{wrapped.WrapperTypeRef}\" 不是 wrapper 类型（§8.3.1）"));
                }
            }
        }

        private static void VerifyModifierDuplicates(IReadOnlyList<BilModifier> modifiers,
            string context, List<BilVerificationError> errors)
        {
            var accessibilitySeen = false;
            var keywordsSeen = new HashSet<BilKeyword>();
            var operatorSeen = false;
            var nativeSymbolSeen = false;
            var nativeLibrarySeen = false;
            var wrapperProxySeen = false;
            var accessorKindsSeen = new HashSet<BilAccessorKind>();
            foreach (var modifier in modifiers)
            {
                switch (modifier)
                {
                    case BilAccessibilityModifier:
                        if (accessibilitySeen)
                        {
                            errors.Add(new BilVerificationError("21.8", context, "访问修饰符重复"));
                        }
                        accessibilitySeen = true;
                        break;
                    case BilKeywordModifier keyword:
                        if (!keywordsSeen.Add(keyword.Keyword))
                        {
                            errors.Add(new BilVerificationError("21.8", context,
                                $"修饰符重复 \"{BilSpellings.Of(keyword.Keyword)}\""));
                        }
                        break;
                    case BilOperatorModifier:
                        if (operatorSeen)
                        {
                            errors.Add(new BilVerificationError("21.8", context, "operator 修饰符重复"));
                        }
                        operatorSeen = true;
                        break;
                    case BilNativeSymbolModifier:
                        if (nativeSymbolSeen)
                        {
                            errors.Add(new BilVerificationError("21.8", context, "symbol 修饰符重复"));
                        }
                        nativeSymbolSeen = true;
                        break;
                    case BilNativeLibraryModifier:
                        if (nativeLibrarySeen)
                        {
                            errors.Add(new BilVerificationError("21.8", context, "lib 修饰符重复"));
                        }
                        nativeLibrarySeen = true;
                        break;
                    case BilAccessorModifier accessor:
                        if (!accessorKindsSeen.Add(accessor.Kind))
                        {
                            errors.Add(new BilVerificationError("21.8", context,
                                $"{BilSpellings.Of(accessor.Kind)} 修饰符重复"));
                        }
                        break;
                    case BilWrapperProxyModifier:
                        if (wrapperProxySeen)
                        {
                            errors.Add(new BilVerificationError("21.8", context,
                                "wrapper-proxy 修饰符重复"));
                        }
                        wrapperProxySeen = true;
                        break;
                }
            }
        }

        // S11e：fn 定义宿主为预定义内建类型（canonical owner 段 ∈
        // PredefinedTypes）——内建类型不进符号段，其编译器合成成员
        //（如 Any.call??? 默认实现）无本地声明可对应，§9.1 对 builtin
        // 宿主豁免（structural 事实，非用户可伪造的形态）
        private static bool IsBuiltinHostedFunction(BilVerificationContext context, string symbol)
        {
            return BilVerificationContext.TryParseMethodSymbol(symbol,
                    out var owner, out _, out _, out _)
                && context.IsPredefinedTypeHost(owner);
        }

        // 方法名段提取（$ 之后、参数段/@ 之前；$$ 运算符形态跳过第二个
        // $，.static. 前缀跳过）——wrapper-proxy 修饰符按名段判定合成
        // 保留名（S11d）。符号 malformed 时返回 null（由 §21.1 另报）
        private static string? MethodNameSegment(string symbol)
        {
            var dollar = symbol.IndexOf('$');
            if (dollar < 0) return null;
            var rest = symbol.Substring(dollar + 1);
            if (rest.StartsWith("$")) rest = rest.Substring(1);
            if (rest.StartsWith(".static.")) rest = rest.Substring(".static.".Length);
            var end = rest.Length;
            var paren = rest.IndexOf('(');
            if (paren >= 0 && paren < end) end = paren;
            var at = rest.IndexOf('@');
            if (at >= 0 && at < end) end = at;
            return rest.Substring(0, end);
        }

        // ===== §21.2（fn 级）+ §21.7 泛型与参数包 =====
        // .args 顺序（§7.2）与签名一致性（§9.2：参数名称和顺序必须与方法
        // 符号的规范签名一致；hidden 参数与符号互相比对的部分跳过）
        private static void VerifyFunctionSignature(BilFunctionContext context,
            List<BilVerificationError> errors)
        {
            var function = context.Function;
            // §21.3：所有变量（.args/.vars）的类型引用必须可解析
            foreach (var arg in function.Args)
            {
                if (!context.Module.IsResolvableTypeRef(arg.TypeRef))
                {
                    errors.Add(new BilVerificationError("21.2", function.Symbol,
                        $"参数 \"{arg.Name}\" 的类型不可解析 \"{arg.TypeRef}\""));
                }
            }
            foreach (var variable in function.Vars)
            {
                if (!context.Module.IsResolvableTypeRef(variable.TypeRef))
                {
                    errors.Add(new BilVerificationError("21.2", function.Symbol,
                        $"局部变量 \"{variable.Name}\" 的类型不可解析 \"{variable.TypeRef}\""));
                }
            }
            if (!BilVerificationContext.TryParseMethodSymbol(function.Symbol,
                    out var owner, out var isStatic, out var parameters, out var returnType))
            {
                return;   // malformed 已由声明侧 §21.1 报
            }
            // 访问器形态（§5.2 无参数段）：setter 的 @T 是 value 参数类型——
            // .return 恒 .void、恰好一个普通参数且类型与 @T 一致（比对在
            // 下方参数段）；getter 的 @T 即返回类型，走普通比对
            // （零普通参数与符号天然一致）
            var isSetterAccessor = BilVerificationContext.TryParseAccessorForm(function.Symbol,
                out var setterForm) && setterForm;
            if (isSetterAccessor)
            {
                if (context.ReturnType != ".void")
                {
                    errors.Add(new BilVerificationError("21.2", function.Symbol,
                        $"setter fn 的 .return 必须为 .void（实际 \"{context.ReturnType}\"）"));
                }
            }
            else if (context.ReturnType != null
                && !context.Module.TypesAssignable(context.ReturnType, returnType))
            {
                errors.Add(new BilVerificationError("21.2", function.Symbol,
                    $".return 类型 \"{context.ReturnType}\" 与方法符号返回类型 \"{returnType}\" 不一致"));
            }

            var args = function.Args;
            var index = args.Count > 0 && args[0].Name == ".return" ? 1 : 0;

            // receiver（§7.3）：有 owner 的非 static 方法（含全局函数除外的
            // 成员方法）必须带 .this；static 与全局函数不得带。owner 段以
            // "::" 结尾的是命名空间前缀（全局函数/全局字段访问器），无 receiver
            var expectThis = !isStatic && owner.Length > 0 && !owner.EndsWith("::");
            if (expectThis)
            {
                if (index >= args.Count || args[index].Name != ".this")
                {
                    errors.Add(new BilVerificationError("21.7", function.Symbol,
                        "实例方法 .args 缺少 .this（应位于 .return 之后）"));
                }
                else if (!BilVerificationContext.TypesCompatible(args[index].TypeRef, owner))
                {
                    errors.Add(new BilVerificationError("21.7", function.Symbol,
                        $".this 类型 \"{args[index].TypeRef}\" 与宿主类型 \"{owner}\" 不一致"));
                }
                index++;
            }
            // 余下参数按 §7.2 顺序：.generic.* → 普通 → .vargs.* → .kwargs.*
            // （阶段只允许前进）；hidden 参数不参与符号参数比对
            var phase = 0;   // 0=.generic.* 1=普通 2=.vargs.* 3=.kwargs.*
            var plainArgs = new List<BilArgDeclaration>();
            for (var i = index; i < args.Count; i++)
            {
                var name = args[i].Name;
                var argPhase = name.StartsWith(".generic.") ? 0
                    : name.StartsWith(".vargs.") ? 2
                    : name.StartsWith(".kwargs.") ? 3
                    : name == ".this" ? -1
                    : 1;
                if (argPhase < 0)
                {
                    errors.Add(new BilVerificationError("21.7", function.Symbol,
                        ".this 位置不符合 §7.2 顺序"));
                    continue;
                }
                if (argPhase < phase)
                {
                    errors.Add(new BilVerificationError("21.7", function.Symbol,
                        $"参数 \"{name}\" 位置不符合 §7.2 顺序"));
                    continue;
                }
                phase = argPhase;
                if (argPhase == 1)
                {
                    plainArgs.Add(args[i]);
                }
            }

            // 普通参数与方法符号逐项一致（§9.2）；符号中 hidden 形态的参数
            // （.generic./.vargs./.kwargs. 开头）跳过比对
            var expected = new List<(string Name, string TypeRef)>();
            foreach (var parameter in parameters)
            {
                if (parameter.Name.StartsWith(".generic.") || parameter.Name.StartsWith(".vargs.")
                    || parameter.Name.StartsWith(".kwargs."))
                {
                    continue;
                }
                expected.Add(parameter);
            }
            if (isSetterAccessor)
            {
                // setter：符号无参数段——fn 恰好一个普通参数（value），
                // 类型 ≡ 符号 @T（参数名不在符号中，不参与比对）
                if (plainArgs.Count != 1)
                {
                    errors.Add(new BilVerificationError("21.7", function.Symbol,
                        $"setter fn 普通参数个数 {plainArgs.Count} 不符（应恰好一个 value 参数）"));
                    return;
                }
                if (!context.Module.TypesAssignable(plainArgs[0].TypeRef, returnType))
                {
                    errors.Add(new BilVerificationError("21.7", function.Symbol,
                        $"setter value 参数类型 \"{plainArgs[0].TypeRef}\" 与方法符号 " +
                        $"\"{returnType}\" 不一致"));
                }
                return;
            }
            if (plainArgs.Count != expected.Count)
            {
                errors.Add(new BilVerificationError("21.7", function.Symbol,
                    $"普通参数个数 {plainArgs.Count} 与方法符号参数个数 {expected.Count} 不一致"));
                return;
            }
            for (var i = 0; i < plainArgs.Count; i++)
            {
                if (plainArgs[i].Name != expected[i].Name
                    || !context.Module.TypesAssignable(plainArgs[i].TypeRef, expected[i].TypeRef))
                {
                    errors.Add(new BilVerificationError("21.7", function.Symbol,
                        $"参数 {i} \"{plainArgs[i].Name}: {plainArgs[i].TypeRef}\" 与方法符号 " +
                        $"\"{expected[i].Name}: {expected[i].TypeRef}\" 不一致"));
                }
            }
        }

        internal static bool HasKeyword(BilSimpleMemberDeclaration declaration, BilKeyword keyword)
        {
            return HasKeyword(declaration.Modifiers, keyword);
        }

        internal static bool HasKeyword(IReadOnlyList<BilModifier> modifiers, BilKeyword keyword)
        {
            foreach (var modifier in modifiers)
            {
                if (modifier is BilKeywordModifier keywordModifier
                    && keywordModifier.Keyword == keyword)
                {
                    return true;
                }
            }
            return false;
        }

        internal static bool HasAccessibility(BilSimpleMemberDeclaration declaration,
            BilAccessibility accessibility)
        {
            foreach (var modifier in declaration.Modifiers)
            {
                if (modifier is BilAccessibilityModifier access
                    && access.Accessibility == accessibility)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
