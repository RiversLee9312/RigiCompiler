using System.Collections.Generic;

namespace LatteCompiler.Bil
{
    // BilVerifier 符号检查（§20.2 符号验证 + §20.7 泛型与参数包 + §20.8
    // 可见性与类型属性的声明侧）。指令内的符号引用可解析性在 Types.cs
    // 逐指令遍历时检查。

    public static partial class BilVerifier
    {
        // ===== §20.2 符号验证（声明侧）+ §20.8 声明侧 =====
        private static void VerifyDeclarations(BilVerificationContext context,
            List<BilVerificationError> errors)
        {
            // 段内符号不重复（类型符号与成员符号各自唯一）
            VerifySectionDuplicates(context.Module.LocalSymbols, "LocalSymbols", errors);
            VerifySectionDuplicates(context.Module.ExternalSymbols, "ExternalSymbols", errors);

            // fn 定义必须对应 LocalSymbols 方法声明（§9.1）；符号形态合法
            var fnSymbols = new HashSet<string>();
            foreach (var function in context.Module.Functions)
            {
                if (!context.LocalMethodSymbols.Contains(function.Symbol))
                {
                    errors.Add(new BilVerificationError("20.2", function.Symbol,
                        "fn 定义在 LocalSymbols 中没有对应方法声明"));
                }
                if (!BilVerificationContext.TryParseMethodSymbol(function.Symbol,
                        out _, out _, out _, out _))
                {
                    errors.Add(new BilVerificationError("20.1", function.Symbol,
                        "fn 符号不符合 canonical 方法符号语法（§5.2）"));
                }
                if (!fnSymbols.Add(function.Symbol))
                {
                    errors.Add(new BilVerificationError("20.2", function.Symbol, "fn 定义重复"));
                }
            }

            // 资源类型引用可解析（§20.3：所有资源有类型）
            foreach (var resource in context.Module.Resources)
            {
                switch (resource)
                {
                    case BilNullResource nullResource:
                        if (!context.IsResolvableTypeRef(nullResource.TypeRef))
                        {
                            errors.Add(new BilVerificationError("20.2", "Resources",
                                $"null 资源 \"{resource.Name}\" 的类型不可解析 \"{nullResource.TypeRef}\""));
                        }
                        break;
                    case BilSwitchTableResource switchTable:
                        if (!context.IsResolvableTypeRef(switchTable.SelectorTypeRef))
                        {
                            errors.Add(new BilVerificationError("20.2", "Resources",
                                $"switch-table \"{resource.Name}\" 的 selector 类型不可解析 " +
                                $"\"{switchTable.SelectorTypeRef}\""));
                        }
                        break;
                    case BilCatchTableResource catchTable:
                        foreach (var entry in catchTable.Entries)
                        {
                            if (!context.IsResolvableTypeRef(entry.ExceptionType.TypeRef))
                            {
                                errors.Add(new BilVerificationError("20.2", "Resources",
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
                errors.Add(new BilVerificationError("20.2", "LocalSymbols",
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
                    && context.TypeDeclarations.TryGetValue(qualifiedName.Substring(0, dot), out var owner)
                    && owner.Kind != BilTypeKind.EnumStruct)
                {
                    errors.Add(new BilVerificationError("20.2", qualifiedName,
                        $"case 声明的宿主类型不是 enum-struct（{BilSpellings.Of(owner.Kind)}）"));
                }
                if (caseDeclaration.DiscriminantResource != null
                    && !context.ResourcesByName.ContainsKey(caseDeclaration.DiscriminantResource))
                {
                    errors.Add(new BilVerificationError("20.2", qualifiedName,
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
                        if (!symbols.Add(type.Symbol))
                        {
                            errors.Add(new BilVerificationError("20.2", sectionName,
                                $"类型符号重复 \"{type.Symbol}\""));
                        }
                        var memberSymbols = new HashSet<string>();
                        foreach (var member in type.Members)
                        {
                            var memberSymbol = MemberSymbolOf(member);
                            if (memberSymbol != null && !memberSymbols.Add(memberSymbol))
                            {
                                errors.Add(new BilVerificationError("20.2", sectionName,
                                    $"成员符号重复 \"{memberSymbol}\""));
                            }
                        }
                        break;
                    case BilMemberDeclaration member:
                        var symbol = MemberSymbolOf(member);
                        if (symbol != null && !symbols.Add(symbol))
                        {
                            errors.Add(new BilVerificationError("20.2", sectionName,
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

            // §8.3/§8.4：声明关键字与符号中 .static. 标记必须一致
            var isMethod = declaration.Kind is BilMemberKind.Method or BilMemberKind.StaticMethod;
            var symbolStatic = false;
            var parsed = isMethod
                ? BilVerificationContext.TryParseMethodSymbol(symbol, out _, out symbolStatic, out _, out _)
                : BilVerificationContext.TryParseFieldSymbol(symbol, out _, out symbolStatic, out _);
            if (!parsed)
            {
                errors.Add(new BilVerificationError("20.1", symbol,
                    "成员符号不符合 canonical 符号语法（§5.2）"));
            }
            else
            {
                var expectStatic = declaration.Kind is BilMemberKind.StaticMethod
                    or BilMemberKind.StaticField;
                if (expectStatic != symbolStatic)
                {
                    errors.Add(new BilVerificationError("20.2", symbol,
                        $"声明关键字 {BilSpellings.Of(declaration.Kind)} 与符号 .static. 标记不一致"));
                }
            }

            // 修饰符不得重复（同访问级两次 / 同关键字两次）
            VerifyModifierDuplicates(declaration.Modifiers, symbol, errors);

            if (!isMethod)
            {
                // §8.3：backing 与 computed 是互斥的存储形态标记
                if (HasKeyword(declaration, BilKeyword.Backing)
                    && HasKeyword(declaration, BilKeyword.Computed))
                {
                    errors.Add(new BilVerificationError("20.8", symbol,
                        "backing 与 computed 不得共存"));
                }
                return;
            }

            // §8.4/§20.8 访问器修饰合法性：getter(FIELD)/setter(FIELD) 的
            // FIELD 必须可解析为已声明字段符号（local + external 声明集合）；
            // 修饰与方法符号形态必须一致（getter ↔ $.get. 形态、
            // setter ↔ $.set. 形态，§5.2）
            foreach (var modifier in declaration.Modifiers)
            {
                if (modifier is not BilAccessorModifier accessor) continue;
                if (!context.FieldSymbols.Contains(accessor.FieldSymbol))
                {
                    errors.Add(new BilVerificationError("20.8", symbol,
                        $"访问器修饰引用的字段符号不可解析 \"{accessor.FieldSymbol}\""));
                }
                if (!BilVerificationContext.TryParseAccessorForm(symbol, out var setterForm)
                    || setterForm != (accessor.Kind == BilAccessorKind.Setter))
                {
                    errors.Add(new BilVerificationError("20.8", symbol,
                        $"{BilSpellings.Of(accessor.Kind)}(...) 修饰与方法符号形态不符" +
                        "（应为 $[.static].get.名 / $[.static].set.名，§5.2）"));
                }
            }

            // §20.2：native 方法不得有 fn 定义，且必须恰好各带一个
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
                    errors.Add(new BilVerificationError("20.2", symbol,
                        "native 方法必须恰好各带一个 symbol(\"...\") 与 lib(\"...\") 修饰符"));
                }
                if (hasBody)
                {
                    errors.Add(new BilVerificationError("20.2", symbol,
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
                    errors.Add(new BilVerificationError("20.2", symbol,
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
                errors.Add(new BilVerificationError("20.8", type.Symbol,
                    $"rich 仅适用于 struct/enum-struct/wrapper（{BilSpellings.Of(type.Kind)}）"));
            }
            if (type.Kind == BilTypeKind.Wrapper && !rich)
            {
                errors.Add(new BilVerificationError("20.8", type.Symbol,
                    "wrapper 类型必须显式带 rich"));
            }
            if (type.Kind == BilTypeKind.EnumStruct && open)
            {
                errors.Add(new BilVerificationError("20.8", type.Symbol, "enum-struct 不得 open"));
            }
            if (type.Kind is BilTypeKind.Struct or BilTypeKind.EnumStruct
                && !rich && (open || abstractKeyword))
            {
                errors.Add(new BilVerificationError("20.8", type.Symbol,
                    "非 rich struct/enum-struct 不得带 open 或 abstract"));
            }
            if (singleton && !shared)
            {
                errors.Add(new BilVerificationError("20.8", type.Symbol,
                    "singleton 类型必须同时带 shared"));
            }

            // extends/implements 类型可解析（§20.2）
            if (type.ExtendsType != null && !context.IsResolvableTypeRef(type.ExtendsType))
            {
                errors.Add(new BilVerificationError("20.2", type.Symbol,
                    $"extends 类型不可解析 \"{type.ExtendsType}\""));
            }
            foreach (var interfaceType in type.ImplementsTypes)
            {
                if (!context.IsResolvableTypeRef(interfaceType))
                {
                    errors.Add(new BilVerificationError("20.2", type.Symbol,
                        $"implements 类型不可解析 \"{interfaceType}\""));
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
            var accessorKindsSeen = new HashSet<BilAccessorKind>();
            foreach (var modifier in modifiers)
            {
                switch (modifier)
                {
                    case BilAccessibilityModifier:
                        if (accessibilitySeen)
                        {
                            errors.Add(new BilVerificationError("20.8", context, "访问修饰符重复"));
                        }
                        accessibilitySeen = true;
                        break;
                    case BilKeywordModifier keyword:
                        if (!keywordsSeen.Add(keyword.Keyword))
                        {
                            errors.Add(new BilVerificationError("20.8", context,
                                $"修饰符重复 \"{BilSpellings.Of(keyword.Keyword)}\""));
                        }
                        break;
                    case BilOperatorModifier:
                        if (operatorSeen)
                        {
                            errors.Add(new BilVerificationError("20.8", context, "operator 修饰符重复"));
                        }
                        operatorSeen = true;
                        break;
                    case BilNativeSymbolModifier:
                        if (nativeSymbolSeen)
                        {
                            errors.Add(new BilVerificationError("20.8", context, "symbol 修饰符重复"));
                        }
                        nativeSymbolSeen = true;
                        break;
                    case BilNativeLibraryModifier:
                        if (nativeLibrarySeen)
                        {
                            errors.Add(new BilVerificationError("20.8", context, "lib 修饰符重复"));
                        }
                        nativeLibrarySeen = true;
                        break;
                    case BilAccessorModifier accessor:
                        if (!accessorKindsSeen.Add(accessor.Kind))
                        {
                            errors.Add(new BilVerificationError("20.8", context,
                                $"{BilSpellings.Of(accessor.Kind)} 修饰符重复"));
                        }
                        break;
                }
            }
        }

        // ===== §20.2（fn 级）+ §20.7 泛型与参数包 =====
        // .args 顺序（§7.2）与签名一致性（§9.2：参数名称和顺序必须与方法
        // 符号的规范签名一致；hidden 参数与符号互相比对的部分跳过）
        private static void VerifyFunctionSignature(BilFunctionContext context,
            List<BilVerificationError> errors)
        {
            var function = context.Function;
            // §20.3：所有变量（.args/.vars）的类型引用必须可解析
            foreach (var arg in function.Args)
            {
                if (!context.Module.IsResolvableTypeRef(arg.TypeRef))
                {
                    errors.Add(new BilVerificationError("20.2", function.Symbol,
                        $"参数 \"{arg.Name}\" 的类型不可解析 \"{arg.TypeRef}\""));
                }
            }
            foreach (var variable in function.Vars)
            {
                if (!context.Module.IsResolvableTypeRef(variable.TypeRef))
                {
                    errors.Add(new BilVerificationError("20.2", function.Symbol,
                        $"局部变量 \"{variable.Name}\" 的类型不可解析 \"{variable.TypeRef}\""));
                }
            }
            if (!BilVerificationContext.TryParseMethodSymbol(function.Symbol,
                    out var owner, out var isStatic, out var parameters, out var returnType))
            {
                return;   // malformed 已由声明侧 §20.1 报
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
                    errors.Add(new BilVerificationError("20.2", function.Symbol,
                        $"setter fn 的 .return 必须为 .void（实际 \"{context.ReturnType}\"）"));
                }
            }
            else if (context.ReturnType != null
                && !BilVerificationContext.TypesCompatible(context.ReturnType, returnType))
            {
                errors.Add(new BilVerificationError("20.2", function.Symbol,
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
                    errors.Add(new BilVerificationError("20.7", function.Symbol,
                        "实例方法 .args 缺少 .this（应位于 .return 之后）"));
                }
                else if (!BilVerificationContext.TypesCompatible(args[index].TypeRef, owner))
                {
                    errors.Add(new BilVerificationError("20.7", function.Symbol,
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
                    errors.Add(new BilVerificationError("20.7", function.Symbol,
                        ".this 位置不符合 §7.2 顺序"));
                    continue;
                }
                if (argPhase < phase)
                {
                    errors.Add(new BilVerificationError("20.7", function.Symbol,
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
                    errors.Add(new BilVerificationError("20.7", function.Symbol,
                        $"setter fn 普通参数个数 {plainArgs.Count} 不符（应恰好一个 value 参数）"));
                    return;
                }
                if (!BilVerificationContext.TypesCompatible(plainArgs[0].TypeRef, returnType))
                {
                    errors.Add(new BilVerificationError("20.7", function.Symbol,
                        $"setter value 参数类型 \"{plainArgs[0].TypeRef}\" 与方法符号 " +
                        $"\"{returnType}\" 不一致"));
                }
                return;
            }
            if (plainArgs.Count != expected.Count)
            {
                errors.Add(new BilVerificationError("20.7", function.Symbol,
                    $"普通参数个数 {plainArgs.Count} 与方法符号参数个数 {expected.Count} 不一致"));
                return;
            }
            for (var i = 0; i < plainArgs.Count; i++)
            {
                if (plainArgs[i].Name != expected[i].Name
                    || !BilVerificationContext.TypesCompatible(plainArgs[i].TypeRef, expected[i].TypeRef))
                {
                    errors.Add(new BilVerificationError("20.7", function.Symbol,
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
    }
}
