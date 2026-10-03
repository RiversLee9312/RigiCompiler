using System.Collections.Generic;
using System.Globalization;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Passes
{
    /// <summary>
    /// wildcard proxy 胖值 ABI 的 MIR 构造辅助（对齐 VM BuildProxyArgs /
    /// BuildRingInvokeArgs / UnboxConcreteArgs）。烘焙 pass 用其拼
    /// trampoline、specific→wildcard inner 打包与 wildcard 环/router 的
    /// 解包回调。.generic 类型包（TNamedArgs/TUnnamedArgs）按 Any 擦除
    /// 形态处理：native 不物化 typeid 包（VM 恒填空包，环内 inner 重写后
    /// 亦无消费方）。
    /// </summary>
    internal static class ProxyWildcardAbi
    {
        private static int _resourceCounter;

        // 胖值 ABI 包类型（§14.7：具名包 = Pair<String, Any> 数组，
        // 位置包 = Any 数组）
        internal static readonly MirType NamedPackType =
            MirType.Of(".array<.pair<.string, .any>>");
        internal static readonly MirType UnnamedPackType = MirType.Of(".array<.any>");
        internal static readonly MirType AnyType = MirType.Of(".any");
        internal static readonly MirType StringType = MirType.Of(".string");
        internal static readonly MirType BoolType = MirType.Of(".bool");
        internal static readonly MirType I32Type = MirType.Of(".i32");

        // Method wrapper wildcard ABI（§14.4）的具名包元素：
        // core::Pair<String, Any>（.pair 构造头别名经 Normalize 投影）
        internal const string PairTypeRef = "core::Pair<.string, .any>";
        internal const string PairKeyFieldSymbol = "core::Pair#key@.generic<$.generic.TKey>";
        internal const string PairValueFieldSymbol = "core::Pair#value@.generic<$.generic.TValue>";
        internal static readonly MirType PairType = MirType.Of(PairTypeRef);
        internal static readonly MirType NullablePairType =
            MirType.Of(".nullable<core::Pair<.string, .any>>");

        internal static BilScalarResource AddStringResource(MwContext context, string text)
        {
            var escaped = "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            var name = "$mw.proxy.str." + (_resourceCounter++).ToString(CultureInfo.InvariantCulture);
            var resource = new BilScalarResource(name, BilScalarType.String, escaped);
            context.Module.Resources.Add(resource);
            return resource;
        }

        // 位置包下标常量（MirGetArray 的下标操作数只能是局部）
        internal static BilScalarResource AddI32Resource(MwContext context, int value)
        {
            var name = "$mw.proxy.i32." + (_resourceCounter++).ToString(CultureInfo.InvariantCulture);
            var resource = new BilScalarResource(name, BilScalarType.I32,
                value.ToString(CultureInfo.InvariantCulture));
            context.Module.Resources.Add(resource);
            return resource;
        }

        // MW11c 棒5a：协程改造面的 i64/bool 常量（同 i32 口径）
        internal static BilScalarResource AddI64Resource(MwContext context, long value)
        {
            var name = "$mw.proxy.i64." + (_resourceCounter++).ToString(CultureInfo.InvariantCulture);
            var resource = new BilScalarResource(name, BilScalarType.I64,
                value.ToString(CultureInfo.InvariantCulture));
            context.Module.Resources.Add(resource);
            return resource;
        }

        internal static BilScalarResource AddBoolResource(MwContext context, bool value)
        {
            var name = "$mw.proxy.bool." + (_resourceCounter++).ToString(CultureInfo.InvariantCulture);
            var resource = new BilScalarResource(name, BilScalarType.Bool,
                value ? "true" : "false");
            context.Module.Resources.Add(resource);
            return resource;
        }

        // void 落点的 .any 零值胖引用（null type(.any)：双段零，
        // ResourceEmitter 对齐 VM Any 空形态）
        internal static BilNullResource AddNullAnyResource(MwContext context)
        {
            var name = "$mw.proxy.null." + (_resourceCounter++).ToString(CultureInfo.InvariantCulture);
            var resource = new BilNullResource(name, ".any");
            context.Module.Resources.Add(resource);
            return resource;
        }

        // pass 合成局部（唯一名 + 注册进 fn 局部表）
        internal static string FreshLocal(MirFunction fn, string prefix, MirType type)
        {
            var n = 0;
            while (fn.TryFindLocal(prefix + n, out _))
            {
                n++;
            }
            var name = prefix + n;
            fn.AddLocal(new MirLocal(name, type));
            return name;
        }

        // 装箱判定（TypeOpVisitors CastLowering 同口径）：标量/String/
        // typeid/用户值类型走 MirBoxAny；引用与胖值（.any/.object/数组/
        // Nullable 等 Object 后代）恒等拷贝
        internal static bool IsBoxableValue(MwContext context, MirType type) =>
            MirBuilder.IsScalarOrString(type) || TypeLayout.IsTypeId(type)
            || context.Symbols.FindType(type.Canonical) is { Declaration.Kind:
                BilTypeKind.Struct or BilTypeKind.EnumStruct };

        internal static void EmitBoxToAny(MwContext context, List<MirInst> insts,
            MirOperand source, MirType sourceType, string target)
        {
            if (!sourceType.IsAnyOrObject && IsBoxableValue(context, sourceType))
            {
                insts.Add(new MirBoxAny(source, target));
                return;
            }
            insts.Add(new MirCopyLocal(source, target));
        }

        internal static void EmitUnboxFromAny(MwContext context, List<MirInst> insts,
            MirOperand source, string target, MirType targetType)
        {
            if (!targetType.IsAnyOrObject && IsBoxableValue(context, targetType))
            {
                insts.Add(new MirUnboxAny(source, target));
                return;
            }
            insts.Add(new MirCopyLocal(source, target));
        }

        internal static void EmitEmptyNamedPack(List<MirInst> insts, string target)
        {
            insts.Add(new MirNewArray(NamedPackType, System.Array.Empty<MirOperand>(), target));
        }

        // 具体实参 → 位置包（逐参装箱 .any，类型感知：值类型 box、
        // 引用/胖值直通——对齐 VM BoxConcreteArgs 的 VmAny 包装）
        internal static void EmitUnnamedPack(MwContext context, MirFunction fn,
            List<MirInst> insts, IReadOnlyList<MirOperand> concreteArgs,
            IReadOnlyList<MirType> argTypes, string target, string tempPrefix)
        {
            var elems = new List<MirOperand>(concreteArgs.Count);
            for (var i = 0; i < concreteArgs.Count; i++)
            {
                var boxed = FreshLocal(fn, tempPrefix + ".box.", AnyType);
                EmitBoxToAny(context, insts, concreteArgs[i], argTypes[i], boxed);
                elems.Add(new MirLocalOperand(boxed));
            }
            insts.Add(new MirNewArray(UnnamedPackType, elems, target));
        }

        // 位置包 → 具体实参（逐参 MirGetArray 取元素 + MirUnboxAny 拆回
        // 声明类型——对齐 VM UnboxConcreteArgs 的 Payload 还原；targetFn
        // 提供值形参类型表，hostMember 供类级 typeid 判定——wrapped/合成
        // fn 符号 owner 为 null，类级信息以原宿主成员符号为准）。遗6
        // 泛型宿主成员：方法级 .generic.* typeid 隐藏形参随值参同包
        //（声明序居值参前，VM TryStartMethodChain 的 concrete=args[1..]
        // 全量装箱同口径），解包到 .typeid 经 MirUnboxAny；类级 typeid
        // 不进包（终态实参拼装由 EmitTerminalArgs 从宿主隐藏字段补齐）。
        internal static List<MirOperand> EmitUnpackArgs(MwContext context, MirFunction fn,
            List<MirInst> insts, MirOperand unnamedPack, MirFunction targetFn,
            MwMemberSymbol hostMember)
        {
            var args = new List<MirOperand>();
            var index = 0;
            foreach (var parameter in targetFn.Parameters)
            {
                if (parameter.Name == ".this" || IsClassLevelTypeIdParam(hostMember, parameter.Name))
                {
                    continue;
                }
                var indexLocal = FreshLocal(fn, "$mw.wc.idx.", I32Type);
                insts.Add(new MirLoadResource(AddI32Resource(context, index), indexLocal));
                var elem = FreshLocal(fn, "$mw.wc.elem.", AnyType);
                insts.Add(new MirGetArray(unnamedPack, new MirLocalOperand(indexLocal),
                    UnnamedPackType, elem));
                var arg = FreshLocal(fn, "$mw.wc.arg.", parameter.Type);
                EmitUnboxFromAny(context, insts, new MirLocalOperand(elem), arg, parameter.Type);
                args.Add(new MirLocalOperand(arg));
                index++;
            }
            return args;
        }

        // ===== Method wrapper wildcard（§14.4）具名包 =====

        // core::Pair<String, Any> 构造解析（具名包逐项 new；init 族可达性
        // 由 MirReachability.EnqueueMethodWrapperPackSupport 预入队）
        private static (MwTypeSymbol Type, MwMemberSymbol Init, MwMemberSymbol? InitWrapper)
            ResolvePairConstruction(MwContext context)
        {
            var template = context.Symbols.FindTypeByRef(PairTypeRef)
                ?? throw new CompilerInternalException(
                    "core::Pair 类型缺失（Method wrapper 具名包依赖）");
            var init = MirBuilder.ResolveInit(context.Symbols, template,
                new[] { ".string", ".any" }, skipReceiver: 0, constructedTypeRef: PairTypeRef);
            var initWrapper = context.Symbols.FindInitWrapper(template, 0);
            var canonical = MwTypeKey.Normalize(PairTypeRef);
            var type = canonical == template.Canonical
                ? template
                : new MwTypeSymbol(canonical, template);
            return (type, init, initWrapper);
        }

        // 具体实参 → 具名包（按方法形参名序逐项 Pair（名资源 + 值装箱
        // .any）后装入数组——VM BoxNamedArgs 同口径）
        internal static void EmitNamedPack(MwContext context, MirFunction fn,
            List<MirInst> insts, IReadOnlyList<MirOperand> concreteArgs,
            IReadOnlyList<MirType> argTypes, IReadOnlyList<string> argNames, string target,
            string tempPrefix)
        {
            if (concreteArgs.Count == 0)
            {
                EmitEmptyNamedPack(insts, target);
                return;
            }
            var (pairType, pairInit, pairInitWrapper) = ResolvePairConstruction(context);
            var elems = new List<MirOperand>(concreteArgs.Count);
            for (var i = 0; i < concreteArgs.Count; i++)
            {
                var nameLocal = FreshLocal(fn, tempPrefix + ".pn.", StringType);
                insts.Add(new MirLoadResource(AddStringResource(context, argNames[i]), nameLocal));
                var boxed = FreshLocal(fn, tempPrefix + ".pb.", AnyType);
                EmitBoxToAny(context, insts, concreteArgs[i], argTypes[i], boxed);
                var pair = FreshLocal(fn, tempPrefix + ".pp.", PairType);
                insts.Add(new MirNewObject(pairType, pairInitWrapper, pairInit,
                    new List<MirOperand>
                    {
                        new MirLocalOperand(nameLocal), new MirLocalOperand(boxed),
                    }, pair));
                elems.Add(new MirLocalOperand(pair));
            }
            insts.Add(new MirNewArray(NamedPackType, elems, target));
        }

        // $mw.named.lookup 合成 fn canonical（模块级唯一，懒建一次——
        // CallWildcardLoweringPass 的 $mw.call???.dispatch 同形态）
        private static readonly string NamedLookupCanonical =
            "$mw.named.lookup(pack:" + NamedPackType.Canonical + ",name:.string)@.any";

        // 具名包按名查找合成 fn（VM ReadNamedPairs 同口径）：
        // $mw.named.lookup(pack, name)@.any —— 线性扫包，Pair key 与
        // 目标名做 string 内容相等（MirBinaryIntrinsic CmpEq .string 经
        // rigi_string_compare 既有路径），命中取 value（.any 直通）；
        // 缺名补 null（VM UnboxNamedArgs 缺名补 VmNull 同口径——.any
        // 零值胖引用）。环链是热路径但正确性优先，小循环归 LLVM 内联。
        internal static MwMemberSymbol EnsureNamedLookup(MwContext context, MirModule mir)
        {
            foreach (var existing in mir.Functions)
            {
                if (existing.Symbol.Canonical == NamedLookupCanonical)
                {
                    return existing.Symbol;
                }
            }
            var symbol = ProxyBakeSupport.SyntheticMember(NamedLookupCanonical, owner: null);
            var parameters = new List<MirLocal>
            {
                new MirLocal("pack", NamedPackType),
                new MirLocal("name", StringType),
            };
            var fn = new MirFunction(symbol, AnyType, parameters,
                new List<MirLocal>(parameters), new List<MirBlock>(), false);
            var packOp = new MirLocalOperand("pack");
            var nameOp = new MirLocalOperand("name");

            // entry：包长度 + 下标归零 → 判定环
            var length = FreshLocal(fn, "$mw.nl.len.", I32Type);
            var index = FreshLocal(fn, "$mw.nl.i.", I32Type);
            fn.AddBlock(new MirBlock("entry", new List<MirInst>
            {
                new MirGetField(packOp, TypeLayout.ArrayLengthField, length),
                new MirLoadResource(AddI32Resource(context, 0), index),
            }, new MirBranch("mw.nl.cond")));

            // cond：i < length → 比对支 / 缺名支
            var inRange = FreshLocal(fn, "$mw.nl.lt.", BoolType);
            fn.AddBlock(new MirBlock("mw.nl.cond", new List<MirInst>
            {
                new MirBinaryIntrinsic(BilBinaryOp.CmpLt, new MirLocalOperand(index),
                    new MirLocalOperand(length), I32Type, I32Type, BoolType, inRange),
            }, new MirCondBranch(new MirLocalOperand(inRange), "mw.nl.body", "mw.nl.miss")));

            // body：取元素拆 Pair → key 与目标名内容相等比对
            var elem = FreshLocal(fn, "$mw.nl.elem.", NullablePairType);
            var pair = FreshLocal(fn, "$mw.nl.pair.", PairType);
            var key = FreshLocal(fn, "$mw.nl.key.", StringType);
            var equal = FreshLocal(fn, "$mw.nl.eq.", BoolType);
            fn.AddBlock(new MirBlock("mw.nl.body", new List<MirInst>
            {
                new MirGetArray(packOp, new MirLocalOperand(index), NamedPackType, elem),
                new MirUnwrapNullable(new MirLocalOperand(elem), PairType, pair),
                new MirGetField(new MirLocalOperand(pair), PairKeyFieldSymbol, key),
                new MirBinaryIntrinsic(BilBinaryOp.CmpEq, new MirLocalOperand(key), nameOp,
                    StringType, StringType, BoolType, equal),
            }, new MirCondBranch(new MirLocalOperand(equal), "mw.nl.hit", "mw.nl.next")));

            // next：i += 1 → 回判定环
            var one = FreshLocal(fn, "$mw.nl.one.", I32Type);
            fn.AddBlock(new MirBlock("mw.nl.next", new List<MirInst>
            {
                new MirLoadResource(AddI32Resource(context, 1), one),
                new MirBinaryIntrinsic(BilBinaryOp.Add, new MirLocalOperand(index),
                    new MirLocalOperand(one), I32Type, I32Type, I32Type, index),
            }, new MirBranch("mw.nl.cond")));

            // hit：取 value（.any 胖值直通）返回
            var value = FreshLocal(fn, "$mw.nl.value.", AnyType);
            fn.AddBlock(new MirBlock("mw.nl.hit", new List<MirInst>
            {
                new MirGetField(new MirLocalOperand(pair), PairValueFieldSymbol, value),
            }, new MirRet(new MirLocalOperand(value))));

            // miss：缺名补 null（.any 零值胖引用）
            var missing = FreshLocal(fn, "$mw.nl.null.", AnyType);
            fn.AddBlock(new MirBlock("mw.nl.miss", new List<MirInst>
            {
                new MirLoadResource(AddNullAnyResource(context), missing),
            }, new MirRet(new MirLocalOperand(missing))));

            mir.AddFunction(fn);
            return symbol;
        }

        // 具名包 → 具体实参（按名还原，对齐 VM UnboxNamedArgs：逐具名
        // 形参经 $mw.named.lookup 扫包按 Pair key 命中取 value 拆箱；
        // 缺名补 null——lookup 缺名支返 .any 零值胖引用，拆回声明类型
        // 走既有 EmitUnboxFromAny 口径）
        internal static List<MirOperand> EmitUnpackNamedArgs(MwContext context, MirModule mir,
            MirFunction fn, List<MirInst> insts, MirOperand namedPack, MirFunction targetFn)
        {
            var lookup = EnsureNamedLookup(context, mir);
            var args = new List<MirOperand>();
            foreach (var parameter in targetFn.Parameters)
            {
                if (parameter.Name == ".this"
                    || parameter.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                {
                    continue;
                }
                var nameLocal = FreshLocal(fn, "$mw.mw.pn.", StringType);
                insts.Add(new MirLoadResource(AddStringResource(context, parameter.Name),
                    nameLocal));
                var value = FreshLocal(fn, "$mw.mw.pv.", AnyType);
                insts.Add(new MirCall(lookup, new List<MirOperand>
                {
                    namedPack, new MirLocalOperand(nameLocal),
                }, value));
                var arg = FreshLocal(fn, "$mw.mw.arg.", parameter.Type);
                EmitUnboxFromAny(context, insts, new MirLocalOperand(value), arg, parameter.Type);
                args.Add(new MirLocalOperand(arg));
            }
            return args;
        }

        // 原方法/运算符 fn 的打包形参表（跳过 .this 与类级 typeid；
        // 遗6：方法级 .generic.* typeid 隐藏形参保留——声明序居值参前，
        // 对齐 VM 调用点 args[1..] 全量进 unnamed 位置包的口径）
        internal static List<MirOperand> ConcreteArgsFromMethod(MirFunction original)
        {
            var list = new List<MirOperand>();
            foreach (var parameter in original.Parameters)
            {
                if (parameter.Name == ".this" || IsClassLevelTypeIdParam(original, parameter.Name))
                {
                    continue;
                }
                list.Add(new MirLocalOperand(parameter.Name));
            }
            return list;
        }

        internal static List<MirType> ConcreteArgTypesFromMethod(MirFunction original)
        {
            var list = new List<MirType>();
            foreach (var parameter in original.Parameters)
            {
                if (parameter.Name == ".this" || IsClassLevelTypeIdParam(original, parameter.Name))
                {
                    continue;
                }
                list.Add(parameter.Type);
            }
            return list;
        }

        // 类级 typeid 隐藏形参判定（GenericAbi 薄壳：owner 泛型参数名命中）
        private static bool IsClassLevelTypeIdParam(MirFunction fn, string paramName) =>
            paramName.StartsWith(".generic.", System.StringComparison.Ordinal)
            && GenericAbi.IsClassLevelTypeId(fn.Symbol, paramName);

        // 类级 typeid 隐藏形参判定（按宿主符号：wrapped/合成 fn 符号 owner
        // 为 null，类级信息以原宿主成员符号为准）
        private static bool IsClassLevelTypeIdParam(MwMemberSymbol hostMember, string paramName) =>
            paramName.StartsWith(".generic.", System.StringComparison.Ordinal)
            && GenericAbi.IsClassLevelTypeId(hostMember, paramName);

        // 终态/直调实参拼装（遗6 泛型宿主成员）：按目标 fn 参数序——
        // .this → hostOp；类级 typeid → 宿主隐藏 typeid 字段就地读
        //（MirGetField，对齐 VM PushFrame 从 .this 构造形态重注入类级
        // .generic.*）；方法级 typeid 与值参 → 位置包按声明序解包
        //（EmitUnpackArgs，与 trampoline 打包口径对偶）。hostMember 为
        // 原宿主成员符号（类级判定与隐藏字段名的依据）。
        internal static List<MirOperand> EmitTerminalArgs(MwContext context, MirFunction fn,
            List<MirInst> insts, MirOperand hostOp, MirOperand unnamedPack,
            MirFunction targetFn, MwMemberSymbol hostMember)
        {
            var unpacked = EmitUnpackArgs(context, fn, insts, unnamedPack, targetFn, hostMember);
            var args = new List<MirOperand>();
            var index = 0;
            foreach (var parameter in targetFn.Parameters)
            {
                if (parameter.Name == ".this")
                {
                    args.Add(hostOp);
                    continue;
                }
                if (IsClassLevelTypeIdParam(hostMember, parameter.Name))
                {
                    var tid = FreshLocal(fn, "$mw.wc.tid.", MirType.Of(".typeid"));
                    insts.Add(new MirGetClassTypeArgument(hostOp, GenericAbi.PlanKey(hostMember.Owner!),
                        parameter.Name.Substring(".generic.".Length), tid));
                    args.Add(new MirLocalOperand(tid));
                    continue;
                }
                args.Add(unpacked[index++]);
            }
            return args;
        }

        // 按 proxy 模板 fn 形参序拼 invoke 实参
        internal static List<MirOperand> BuildInvokeArgs(MirFunction proxyFn,
            MirOperand wrapperInstance, string? symbolLocal, string? returnTypeIdLocal,
            string? namedPackLocal, string? unnamedPackLocal,
            IReadOnlyList<MirOperand>? concreteArgs)
        {
            var args = new List<MirOperand>();
            var concreteIndex = 0;
            foreach (var parameter in proxyFn.Parameters)
            {
                if (parameter.Name == ".return")
                {
                    continue;
                }
                if (parameter.Name == ".this")
                {
                    args.Add(wrapperInstance);
                    continue;
                }
                if (parameter.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                {
                    if (IsPackType(parameter.Type.Canonical))
                    {
                        args.Add(new MirLocalOperand(
                            namedPackLocal ?? unnamedPackLocal
                            ?? throw new CompilerInternalException(
                                "wildcard proxy 缺泛型包实参")));
                    }
                    else
                    {
                        args.Add(new MirLocalOperand(
                            returnTypeIdLocal ?? throw new CompilerInternalException(
                                "wildcard proxy 缺 .generic 返回 typeid")));
                    }
                    continue;
                }
                if (parameter.Name == "symbol" || parameter.Name == ".name")
                {
                    args.Add(new MirLocalOperand(
                        symbolLocal ?? throw new CompilerInternalException(
                            "wildcard proxy 缺 symbol")));
                    continue;
                }
                if (parameter.Name.StartsWith(".kwargs.", System.StringComparison.Ordinal))
                {
                    args.Add(new MirLocalOperand(
                        namedPackLocal ?? throw new CompilerInternalException(
                            "wildcard proxy 缺 named 包")));
                    continue;
                }
                if (parameter.Name.StartsWith(".vargs.", System.StringComparison.Ordinal))
                {
                    args.Add(new MirLocalOperand(
                        unnamedPackLocal ?? throw new CompilerInternalException(
                            "wildcard proxy 缺 unnamed 包")));
                    continue;
                }
                if (concreteArgs != null && concreteIndex < concreteArgs.Count)
                {
                    args.Add(concreteArgs[concreteIndex++]);
                }
                else
                {
                    args.Add(new MirLocalOperand(parameter.Name));
                }
            }
            return args;
        }

        // 首环 wildcard 的原名槽 trampoline 打包：symbol = 宿主成员
        // canonical 资源、named 空包、unnamed = 全部值实参装箱；按特化环
        // 形参序拼调用实参（VM BuildRingInvokeArgs 的 nextWildcard 分支同
        // 口径）。.generic 三包已随环特化擦除/代入，无需 typeid 物化。
        internal static List<MirOperand> EmitWildcardTrampolineSetup(MwContext context,
            MirFunction trampoline, List<MirInst> insts, MirFunction proxyFn,
            MirFunction original, string memberSymbol, MirOperand wrapperInstance)
        {
            var symbolLocal = FreshLocal(trampoline, "$mw.tramp.sym.", StringType);
            var namedLocal = FreshLocal(trampoline, "$mw.tramp.named.", NamedPackType);
            var unnamedLocal = FreshLocal(trampoline, "$mw.tramp.unnamed.", UnnamedPackType);
            insts.Add(new MirLoadResource(AddStringResource(context, memberSymbol), symbolLocal));
            EmitEmptyNamedPack(insts, namedLocal);
            EmitUnnamedPack(context, trampoline, insts, ConcreteArgsFromMethod(original),
                ConcreteArgTypesFromMethod(original), unnamedLocal, "$mw.tramp");
            return BuildInvokeArgs(proxyFn, wrapperInstance, symbolLocal, null,
                namedLocal, unnamedLocal, null);
        }

        // wildcard 模板的方法级泛型形参分类（VM BuildProxyArgs 同口径）：
        // 包形态（.array/.map）→ 擦除集（native 不物化 typeid 包）；
        // 标量 typeid（TReturn）→ 代入返回类型（void 擦除为 .any——
        // void 无 TypeSheet 且环/包 ABI 以胖值承载结果）
        internal static void ClassifyGenericParams(BilFunction template, string returnTypeRef,
            out Dictionary<string, string> extraSubst, out HashSet<string> erasedPacks)
        {
            extraSubst = new Dictionary<string, string>(System.StringComparer.Ordinal);
            erasedPacks = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var arg in template.Args)
            {
                if (!arg.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                {
                    continue;
                }
                var name = arg.Name.Substring(".generic.".Length);
                if (IsPackType(arg.TypeRef))
                {
                    erasedPacks.Add(name);
                }
                else
                {
                    extraSubst[name] = returnTypeRef;
                }
            }
        }

        internal static bool IsPackType(string typeRef) =>
            typeRef.StartsWith(".array<", System.StringComparison.Ordinal)
            || typeRef.StartsWith("core::Array<", System.StringComparison.Ordinal)
            || typeRef.StartsWith(".map<", System.StringComparison.Ordinal)
            || typeRef.StartsWith("core::Map<", System.StringComparison.Ordinal);
    }
}
