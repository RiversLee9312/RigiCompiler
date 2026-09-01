using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Mir
{
    /// <summary>
    /// MirBuilder（MW3 层）：BIL 结构化块 → MIR CFG 的确定性直译
    /// （MIDDLEWARE_ARCHITECTURE §3：输入已结构化，无需 Relooper/Stackifier，
    /// 输出天然 reducible CFG）。输入已过 BilVerifier 门禁，故形状不变量
    /// （恰一 entrypoint block、break/continue token 作用域、块恰好被一个
    /// 父 region 引用等）直接依赖，不复查。构建顺序由 MirReachability 给出
    /// （调用图可达闭包，模块级 DCE）。指令翻译归 FlowBuilder +
    /// MirLowerDispatchers + 簇 CRTP，本类是瘦驱动。
    /// </summary>
    public static class MirBuilder
    {
        public static MirModule Build(MwContext context)
        {
            var bySymbol = new Dictionary<string, BilFunction>(System.StringComparer.Ordinal);
            foreach (var bilFn in context.Module.Functions)
            {
                bySymbol.Add(bilFn.Symbol, bilFn);
            }

            var order = new List<MirFunction>();
            var (buildOrder, tentativeInitFamily) = MirReachability.ResolveBuildOrder(context);
            foreach (var symbol in buildOrder)
            {
                try
                {
                    order.Add(BuildFunction(context, bySymbol[symbol]));
                }
                catch (MwNotSupportedException ex) when (tentativeInitFamily.Contains(symbol))
                {
                    Logger.Verbose("Middleware",
                        "试探性跳过 init 族 fn（new.indirect 保守边）: "
                        + symbol + ": " + ex.Message);
                }
            }
            var module = new MirModule(order);
            context.Mir = module;
            return module;
        }

        internal static MirFunction BuildFunction(MwContext context, BilFunction bilFn)
        {
            var symbol = context.Symbols.FindMember(bilFn.Symbol)
                ?? throw new MwNotSupportedException(
                    $"MW1 不支持无符号段声明的 fn（预定义合成体）: {bilFn.Symbol}");

            // .args：.return 在前，其后按 §7.2 序登记——.this / 固定泛型
            // .generic.T（.typeid）/ 泛型包 .generic.<Pack>（.array/.map）/
            // 普通参数 / .vargs. / .kwargs.。包与固定泛型同走声明 TypeRef
            // 落槽（包 = 胖引用）；其余未知隐藏参数形态拒绝
            MirType? returnType = null;
            var parameters = new List<MirLocal>();
            var locals = new List<MirLocal>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var arg in bilFn.Args)
            {
                if (arg.Name == ".return")
                {
                    returnType = MirType.Of(arg.TypeRef);
                    continue;
                }
                if (arg.Name == ".this")
                {
                    // .this 必须在普通参数之前（BIL .args 序保证：.return 后首条）
                    if (parameters.Count != 0)
                    {
                        throw new CompilerInternalException(
                            $".this 不在参数表首位（fn {bilFn.Symbol}）");
                    }
                    AddLocal(parameters, locals, seen, arg.Name, arg.TypeRef, bilFn.Symbol);
                    continue;
                }
                if (IsAdmittedHiddenArg(arg.Name))
                {
                    AddLocal(parameters, locals, seen, arg.Name, arg.TypeRef, bilFn.Symbol);
                    continue;
                }
                if (arg.Name.StartsWith('.'))
                {
                    throw new MwNotSupportedException(
                        $"MW5 不支持隐藏参数 {arg.Name}（fn {bilFn.Symbol}）");
                }
                AddLocal(parameters, locals, seen, arg.Name, arg.TypeRef, bilFn.Symbol);
            }
            if (returnType == null)
            {
                throw new CompilerInternalException($"fn 缺 .return 条目: {bilFn.Symbol}");
            }

            foreach (var varDecl in bilFn.Vars)
            {
                AddLocal(null, locals, seen, varDecl.Name, varDecl.TypeRef, bilFn.Symbol);
            }

            // §9.4：恰一个 entrypoint block（verifier 保证）；防御
            BilBlock? entryBlock = null;
            foreach (var block in bilFn.Blocks)
            {
                if (block.Modifiers.Contains(BilBlockModifier.Entrypoint))
                {
                    entryBlock = block;
                    break;
                }
            }
            if (entryBlock == null)
            {
                throw new CompilerInternalException($"fn 缺 entrypoint block: {bilFn.Symbol}");
            }

            var localMap = new Dictionary<string, MirLocal>(System.StringComparer.Ordinal);
            foreach (var local in locals)
            {
                localMap.Add(local.Name, local);
            }
            var flowBuilder = new FlowBuilder(context, bilFn.Symbol, localMap, returnType);
            var blocks = flowBuilder.Build(entryBlock);
            // try 展开期登记的合成局部（$mw.exc.N 等）并入 fn 局部表
            locals.AddRange(flowBuilder.SyntheticLocals);

            var isEntrypoint = symbol.HasKeyword(BilKeyword.Entrypoint);
            // MW11a：async 关键字随符号带入（CoroutineSplitPass 的处理标记）
            var isAsync = symbol.HasKeyword(BilKeyword.Async);
            return new MirFunction(symbol, returnType, parameters, locals, blocks,
                isEntrypoint, isAsync);
        }

        // cast 子集判定的内建标量/String 键
        internal static bool IsScalarOrString(MirType type) =>
            type.Key is "bool" or "char"
                or "i8" or "i16" or "i32" or "i64"
                or "u8" or "u16" or "u32" or "u64"
                or "float" or "double" or "String";

        // VM TryNumericCast 目标集（含 char；不含 bool）
        internal static bool IsNumericScalar(MirType type) =>
            type.Key is "char"
                or "i8" or "i16" or "i32" or "i64"
                or "u8" or "u16" or "u32" or "u64"
                or "float" or "double";

        // ===== super/init 解析（MirReachability 可达性共用） =====

        // fn(..super) 解析（VM ResolveSuper 同口径）：super init → 直接
        // 基类 init 按实参静态类型精确匹配；super 方法 → 基类链同名签名键
        internal static MwMemberSymbol ResolveSuperCall(MwContext context, string currentFnSymbol,
            IReadOnlyList<string> argTypeRefs)
        {
            var current = context.Symbols.FindMember(currentFnSymbol)
                ?? throw new CompilerInternalException($"super 所在 fn 无符号: {currentFnSymbol}");
            var baseRef = current.Owner?.Declaration.ExtendsType
                ?? throw new MwNotSupportedException($"super 所在 fn 无直接基类: {currentFnSymbol}");
            var baseType = context.Symbols.FindTypeByRef(baseRef)
                ?? throw new MwNotSupportedException($"super 基类声明缺失: {baseRef}");
            if (current.HasKeyword(BilKeyword.Init))
            {
                // argTypeRefs 首位是 .this 接收者，重载匹配跳过
                return ResolveInit(context.Symbols, baseType, argTypeRefs, skipReceiver: 1,
                    constructedTypeRef: baseRef);
            }
            var key = current.SignatureKey;
            for (var type = baseType; type != null; type = BaseOf(context.Symbols, type))
            {
                foreach (var member in type.Members)
                {
                    if (member.IsVirtualMember && member.SignatureKey == key)
                    {
                        return member;
                    }
                }
            }
            throw new MwNotSupportedException($"super 未在基类链命中同名方法: {currentFnSymbol}");
        }

        // init 重载匹配（BIL §14.1/§9.2.2：实参静态类型精确一致、唯一
        // 命中；skipReceiver=1 时 argTypeRefs 首位是 .this 接收者）
        internal static MwMemberSymbol ResolveInit(MwSymbolTable symbols, MwTypeSymbol type,
            IReadOnlyList<string> argTypeRefs, int skipReceiver, string? constructedTypeRef = null)
        {
            var substitution = constructedTypeRef != null
                ? Layout.ConstructedTypeCollector.BuildSubstitution(constructedTypeRef, type.Declaration)
                : null;
            MwMemberSymbol? match = null;
            foreach (var member in type.Members)
            {
                if (!member.HasKeyword(BilKeyword.Init))
                {
                    continue;
                }
                var signature = CanonicalSignature.Parse(member.Canonical);
                if (signature.Parameters.Count != argTypeRefs.Count - skipReceiver)
                {
                    continue;
                }
                var all = true;
                for (var i = 0; i < signature.Parameters.Count; i++)
                {
                    var expected = Layout.ConstructedTypeCollector.Substitute(
                        signature.Parameters[i].TypeRef, substitution);
                    if (!InitArgCompatible(symbols, expected, argTypeRefs[i + skipReceiver],
                        substitution))
                    {
                        all = false;
                        break;
                    }
                }
                if (!all)
                {
                    continue;
                }
                if (match != null)
                {
                    throw new MwNotSupportedException($"init 匹配不唯一: {type.Canonical}");
                }
                match = member;
            }
            return match ?? throw new MwNotSupportedException($"new/super 无匹配 init: {type.Canonical}");
        }

        // init 实参匹配：精确 canonical；构造类型下实参仍带模板占位时
        // 经同一 substitution 代入；冷 Task body 是具体 lambda 类时按
        // ExtendsType 判定（CoroutineSplit 随后改写为工厂）
        private static bool InitArgCompatible(MwSymbolTable symbols, string expected,
            string actual, Dictionary<string, string>? substitution)
        {
            var expectedSubst = Layout.ConstructedTypeCollector.Substitute(expected, substitution);
            var actualSubst = Layout.ConstructedTypeCollector.Substitute(actual, substitution);
            if (substitution != null)
            {
                expectedSubst = SubstituteBareParams(expectedSubst, substitution);
                actualSubst = SubstituteBareParams(actualSubst, substitution);
            }
            if (MwTypeKey.Normalize(expectedSubst) == MwTypeKey.Normalize(actualSubst))
            {
                return true;
            }
            var actualType = symbols.FindTypeByRef(actualSubst)
                ?? symbols.FindTypeByRef(actual);
            var extends = actualType?.Declaration.ExtendsType;
            if (extends == null)
            {
                return false;
            }
            var extendsSubst = Layout.ConstructedTypeCollector.Substitute(extends, substitution);
            if (substitution != null)
            {
                extendsSubst = SubstituteBareParams(extendsSubst, substitution);
            }
            return MwTypeKey.Normalize(extendsSubst) == MwTypeKey.Normalize(expectedSubst);
        }

        // ConstructedTypeCollector.Substitute 只替换 `.generic<…>` 占位；
        // init 签名里的 `AsyncFunc<TReturn>` 是声明形裸参数名，需按构造
        // 代入表改写成实参
        private static string SubstituteBareParams(string typeRef,
            Dictionary<string, string> substitution)
        {
            var result = typeRef;
            foreach (var pair in substitution)
            {
                result = result.Replace("<" + pair.Key + ">", "<" + pair.Value + ">",
                    System.StringComparison.Ordinal);
            }
            return result;
        }

        // ..init.wrapper 按名 + 实参个数解析（VM TryFindInitWrapper 同
        // 口径：成员名 ..init.wrapper 唯一，个数由调用点形态决定——普通
        // new 只取零参，new.wrapped 取与 wrapper 实参同数形态）；
        // 无匹配返回 null
        internal static MwMemberSymbol? FindInitWrapper(MwSymbolTable symbols,
            MwTypeSymbol type, int arity)
        {
            var prefix = type.Canonical + "$" + BilSpellings.InitWrapperMethodName + "(";
            foreach (var member in type.Members)
            {
                if (!member.Canonical.StartsWith(prefix, System.StringComparison.Ordinal))
                {
                    continue;
                }
                if (CanonicalSignature.Parse(member.Canonical).Parameters.Count == arity)
                {
                    return member;
                }
            }
            return null;
        }

        private static MwTypeSymbol? BaseOf(MwSymbolTable symbols, MwTypeSymbol type) =>
            type.Declaration.ExtendsType is { } baseRef ? symbols.FindTypeByRef(baseRef) : null;

        // §7.1 放行：固定泛型 .generic.T、泛型包 .generic.<Pack>、
        // 位置值包 .vargs.<名>、具名值包 .kwargs.<名>。固定/包靠 TypeRef
        // 区分（.typeid vs .array/.map），此处只认前缀。刀6：.name
        //（Method wrapper wildcard .proxy.call 的保留首参，§14.4）——
        // 模板 fn 不进常规 MIR，仅烘焙期 BuildSpecializedBody 经此
        private static bool IsAdmittedHiddenArg(string name) =>
            name.StartsWith(".generic.", System.StringComparison.Ordinal)
            || name.StartsWith(".vargs.", System.StringComparison.Ordinal)
            || name.StartsWith(".kwargs.", System.StringComparison.Ordinal)
            || name == ".name";

        private static void AddLocal(List<MirLocal>? parameters, List<MirLocal> locals,
            HashSet<string> seen, string name, string typeRef, string fnSymbol)
        {
            // .vars 与参数共名属生成方违约（verifier 已查）；此处防御
            if (!seen.Add(name))
            {
                throw new CompilerInternalException($"fn {fnSymbol} 局部重名: {name}");
            }
            var local = new MirLocal(name, MirType.Of(typeRef));
            locals.Add(local);
            parameters?.Add(local);
        }
    }
}
