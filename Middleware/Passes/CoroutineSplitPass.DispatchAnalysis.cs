using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Passes
{
    public sealed partial class CoroutineSplitPass : IMwStage
    {
        // DispatchAnalysis 职责；与主文件共享同一类型、字段及生命周期。

        // tainted fn 是否 core::Exception 派生链上的 getMessage override
        //（reporter 脊柱虚调目标集）
        private static bool IsExceptionGetMessage(MwContext context, MirFunction fn)
        {
            if (fn.Symbol.SignatureKey != "getMessage()")
            {
                return false;
            }
            var owner = fn.Symbol.Owner;
            for (var depth = 0; owner != null && depth < 64; depth++)
            {
                if (owner.Canonical == "core::Exception")
                {
                    return true;
                }
                if (owner.Declaration.ExtendsType is not { } extendsRef)
                {
                    return false;
                }
                owner = context.Symbols.FindTypeByRef(BilVerificationContext.StripTypeArguments(
                    MwTypeKey.Normalize(extendsRef)));
            }
            return false;
        }
        // 虚派发站点判定（MirCall 的运行期目标非静态唯一）：显式
        // invoke 经 BindCall、intrinsic 运算符经 BindOperatorCall；
        // class 虚/interface iMap 两种形态
        private static bool IsVirtualDispatchSite(MirCall call)
        {
            var binding = call.OperatorDispatch
                ? Binding.ImplBinder.BindOperatorCall(call.Target)
                : Binding.ImplBinder.BindCall(call.Target);
            return binding is Binding.VirtualCallBinding
                or Binding.InterfaceCallBinding;
        }

        // R2-b：invoke.indirect 的静态 $$call 目标解析（MIR 期
        // 同 EmitIndirectInvoke 口径——实参/结果类型取调用点局部
        // 静态类型，沿 extends 链唯一匹配；查不到属 Gate 漏检）
        private static MwMemberSymbol IndirectCallOperatorOf(MwContext context,
            MirFunction fn, MirInvokeIndirect invoke)
        {
            var argTypes = new List<string>(invoke.Args.Count);
            foreach (var arg in invoke.Args)
            {
                if (arg is not MirLocalOperand local)
                {
                    throw new CompilerInternalException("invoke.indirect 实参非局部");
                }
                argTypes.Add(fn.FindLocal(local.Name).Type.Canonical);
            }
            var resultType = invoke.Result != null
                ? fn.FindLocal(invoke.Result).Type.Canonical
                : null;
            return Binding.ImplBinder.BindIndirectCall(context.Symbols,
                invoke.CallTargetType.Canonical, argTypes, resultType,
                context.Module.Functions) is Binding.IndirectCallBinding binding
                ? binding.CallOperator
                : throw new CompilerInternalException(
                    "invoke.indirect 的非预期绑定形态: " + invoke.CallTargetType.Canonical);
        }

        private List<(TypeLayoutPlan ClassPlan, string ImplCanonical)> ClosurePairsOf(
            MwContext context, MwMemberSymbol target, bool operatorDispatch)
        {
            if (_closureCache.TryGetValue(target.Canonical, out var cached))
            {
                return cached;
            }
            var pairs = new List<(TypeLayoutPlan, string)>();
            var binding = operatorDispatch
                ? Binding.ImplBinder.BindOperatorCall(target)
                : Binding.ImplBinder.BindCall(target);
            var query = context.DispatchQuery;
            // 宿主槽表按 PlanKey 查询（同名不同元数模板 canonical
            // 撞键——core::Func<1>/Func<2> 共享 "core::Func" 裸键，
            // 先登记者占用；R2-b 合并模块形态实证槽表错配致闭包
            // 为空）。VirtualCallEmitter.VirtualSlotOf 同口径
            var ownerSlots = target.Owner == null
                ? null : query?.GetVTableSlots(GenericAbi.PlanKey(target.Owner));
            if (query != null && ownerSlots != null)
            {
                var slot = -1;
                for (var i = 0; i < ownerSlots.Count; i++)
                {
                    if (ownerSlots[i] == target.Canonical)
                    {
                        slot = i;
                        break;
                    }
                }
                if (slot >= 0)
                {
                    // R2-b：闭包枚举直走布局计划表（不经字符串查
                    // 询口）——① 同名不同元数模板 canonical 撞键
                    //（core::Func<1>/Func<2> 共享 "core::Func"），
                    // 派生判定按模板声明逐级比对（arity 正确）；
                    // ② 跳过构造计划（臂类键恒为模板 canonical，
                    // 构造实例沿构造基链命中模板臂——R2-a）
                    var ownerTemplate = target.Owner!;
                    switch (binding)
                    {
                        case Binding.VirtualCallBinding:
                            foreach (var plan in context.Layout!.Plans)
                            {
                                if (plan.Kind != TypeLayoutKind.Class
                                    || Layout.ConstructedTypeCollector.IsConstructed(
                                        plan.Symbol.Canonical)
                                    || !DerivesFromTemplate(plan, ownerTemplate)
                                    || slot >= plan.VTableSlots.Count)
                                {
                                    continue;
                                }
                                pairs.Add((plan,
                                    plan.VTableSlots[slot]));
                            }
                            break;
                        case Binding.InterfaceCallBinding:
                            foreach (var plan in context.Layout!.Plans)
                            {
                                if (plan.Kind != TypeLayoutKind.Class
                                    || Layout.ConstructedTypeCollector.IsConstructed(
                                        plan.Symbol.Canonical))
                                {
                                    continue;
                                }
                                var imap = plan.IMap;
                                var slots = plan.VTableSlots;
                                foreach (var (ifaceType, baseOffset) in imap)
                                {
                                    if ((ifaceType == target.Owner!.Canonical
                                            || context.Symbols.FindTypeByRef(ifaceType)
                                                == target.Owner)
                                        && baseOffset + slot < slots.Count)
                                    {
                                        pairs.Add((plan,
                                            slots[baseOffset + slot]));
                                    }
                                }
                            }
                            break;
                    }
                }
            }
            _closureCache.Add(target.Canonical, pairs);
            return pairs;
        }

        // R2-b：arity 正确的模板派生判定——沿 BasePlan 链按
        // 声明身份比对；闭合基类保留模板声明，且不同元数声明各自独立。
        // 不能用闭合 PlanKey 与模板 PlanKey 相等来判定继承关系。
        private static bool DerivesFromTemplate(TypeLayoutPlan plan, MwTypeSymbol ownerTemplate)
        {
            for (var current = plan; current != null; current = current.BasePlan)
            {
                if (current.Symbol.Declaration == ownerTemplate.Declaration)
                {
                    return true;
                }
            }
            return false;
        }

        private List<IndirectInitOverload> ClassInitOverloads(MwContext context,
            MirModule mir)
        {
            if (_classInitOverloads != null)
            {
                return _classInitOverloads;
            }
            var list = new List<IndirectInitOverload>();
            foreach (var fn in mir.Functions)
            {
                if (!fn.Symbol.HasKeyword(BilKeyword.Init)
                    || fn.Symbol.Owner == null
                    || fn.Symbol.Owner.Declaration.Kind != BilTypeKind.Class)
                {
                    continue;
                }
                var template = context.Symbols.FindTypeByRef(fn.Symbol.Owner.Canonical)
                    ?? fn.Symbol.Owner;
                var signature = CanonicalSignature.Parse(fn.Symbol.Canonical);
                var declParams = new List<string>(signature.Parameters.Count);
                foreach (var parameter in signature.Parameters)
                {
                    declParams.Add(parameter.TypeRef);
                }
                // 宿主声明级 ..init.wrapper（字段初始值缝合，可空；
                // DynamicNewEmitter.CollectInits 同钥匙）
                var wrapper = context.Symbols.FindInitWrapper(template, 0);
                list.Add(new IndirectInitOverload
                {
                    InitFn = fn,
                    HostTemplate = template,
                    DeclParamTypeRefs = declParams,
                    Wrapper = wrapper,
                });
            }
            _classInitOverloads = list;
            return list;
        }

        // 重载的全部物化 sheet × 代入后形参列：非泛型宿主 = 本类计
        // 划；泛型宿主 = 模块内全部闭合构造计划（开放模板 sheet 的
        // 分发器恒 ret null——运行期不会选中任何 init，不入臂）。
        // 形参代入镜像 DynamicNewEmitter.CollectInits（Substitute +
        // Normalize）
        private static List<(TypeLayoutPlan Plan, List<string> ParamTypes)> SheetsOf(
            MwContext context, IndirectInitOverload overload)
        {
            if (overload.Sheets != null)
            {
                return overload.Sheets;
            }
            var sheets = new List<(TypeLayoutPlan, List<string>)>();
            if (context.Layout != null)
            {
                foreach (var plan in context.Layout.Plans)
                {
                    if (plan.Kind != TypeLayoutKind.Class)
                    {
                        continue;
                    }
                    var canonical = plan.Symbol.Canonical;
                    bool isSheetOfHost;
                    if (overload.HostTemplate.Declaration.GenericParameters.Count == 0)
                    {
                        isSheetOfHost = canonical == overload.HostTemplate.Canonical;
                    }
                    else
                    {
                        isSheetOfHost =
                            Layout.ConstructedTypeCollector.IsConstructed(canonical)
                            && GenericAbi.IsClosedConstructed(canonical)
                            && BilVerificationContext.StripTypeArguments(
                                MwTypeKey.Normalize(canonical))
                                == overload.HostTemplate.Canonical;
                    }
                    if (!isSheetOfHost)
                    {
                        continue;
                    }
                    var subst = Layout.ConstructedTypeCollector.BuildSubstitution(
                        canonical, plan.Symbol.Declaration);
                    var paramTypes = new List<string>(overload.DeclParamTypeRefs.Count);
                    foreach (var declParam in overload.DeclParamTypeRefs)
                    {
                        paramTypes.Add(MwTypeKey.Normalize(
                            Layout.ConstructedTypeCollector.Substitute(declParam, subst)));
                    }
                    sheets.Add((plan, paramTypes));
                }
            }
            overload.Sheets = sheets;
            return sheets;
        }

        // 站点静态实参形（argSheets 物化同源——发射期按实参局部静
        // 态类型取 ArgToken）
        private static List<string> IndirectStaticArgTypes(MirFunction fn,
            MirNewIndirect inst)
        {
            var argTypes = new List<string>(inst.Args.Count);
            foreach (var arg in inst.Args)
            {
                if (arg is not MirLocalOperand local)
                {
                    throw new CompilerInternalException("new.indirect 实参非局部");
                }
                argTypes.Add(fn.FindLocal(local.Name).Type.Canonical);
            }
            return argTypes;
        }

        private static bool IndirectArgsMatch(IReadOnlyList<string> siteArgTypes,
            IReadOnlyList<string> paramTypes)
        {
            if (siteArgTypes.Count != paramTypes.Count)
            {
                return false;
            }
            for (var i = 0; i < siteArgTypes.Count; i++)
            {
                if (MwTypeKey.Normalize(siteArgTypes[i]) != paramTypes[i])
                {
                    return false;
                }
            }
            return true;
        }

        // 站点相关性：存在 tainted class init 重载的某个物化 sheet
        // 与站点静态实参形精确匹配 → true（调用方传染/站点协议化）。
        // 占位实参（argSheets 运行期物化）且 argc 撞上任一 tainted
        // 重载时匹配不可静态判定 → 受控拒绝
        private bool IndirectInitRelevant(MwContext context, MirFunction fn,
            MirNewIndirect inst, HashSet<string> tainted)
        {
            var overloads = ClassInitOverloads(context,
                context.Mir
                    ?? throw new CompilerInternalException("CoroutineSplit 要求 Mir 已挂载"));
            var argTypes = IndirectStaticArgTypes(fn, inst);
            var hasPlaceholder = argTypes.Any(t =>
                t.Contains(".generic<", System.StringComparison.Ordinal));
            var argcCollision = false;
            foreach (var overload in overloads)
            {
                if (!tainted.Contains(overload.InitFn.Symbol.Canonical))
                {
                    continue;
                }
                foreach (var (_, paramTypes) in SheetsOf(context, overload))
                {
                    if (paramTypes.Count != argTypes.Count)
                    {
                        continue;
                    }
                    argcCollision = true;
                    if (!hasPlaceholder && IndirectArgsMatch(argTypes, paramTypes))
                    {
                        return true;
                    }
                }
            }
            if (argcCollision && hasPlaceholder)
            {
                throw new MwNotSupportedException(
                    "B-2 暂不支持泛型占位实参的 new.indirect 与含挂起点 init 同模块"
                    + "（实参 sheet 匹配运行期不可判定）: " + fn.Symbol.Canonical);
            }
            return false;
        }

        // 臂的派生排除 sheet：物化类计划中严格派生自臂 sheet 的全
        // 部 sheet（精确化——typeid 命中派生 sheet 时其自身分发器
        // 决定选择（无匹配即 NoSuchMethod），不得落本臂）
        private static List<string> DerivedSheetsOf(MwContext context,
            TypeLayoutPlan armPlan)
        {
            var sheets = new List<string>();
            if (context.Layout == null)
            {
                return sheets;
            }
            foreach (var plan in context.Layout.Plans)
            {
                if (plan.Kind != TypeLayoutKind.Class
                    || ReferenceEquals(plan, armPlan)
                    || plan.Symbol.Canonical == armPlan.Symbol.Canonical)
                {
                    continue;
                }
                for (var current = plan.BasePlan; current != null; current = current.BasePlan)
                {
                    if (ReferenceEquals(current, armPlan))
                    {
                        sheets.Add(plan.Symbol.Canonical);
                        break;
                    }
                }
            }
            return sheets;
        }

        // R2-c 站点协议信息回填（PrepareSplit 内、活性分析之前——
        // callee frame 槽须进 fn.Locals）
        private void PrepareIndirectInitSites(MwContext context, MirModule mir,
            MirFunction fn, List<SuspensionPoint> points, HashSet<string> tainted)
        {
            foreach (var point in points)
            {
                if (point.IndirectInit == null)
                {
                    continue;
                }
                var inst = (MirNewIndirect)point.Inst;
                var site = point.IndirectInit;
                if (inst.TypeId is not MirLocalOperand typeIdLocal)
                {
                    throw new CompilerInternalException(
                        "new.indirect 挂起点 typeid 非局部: " + fn.Symbol.Canonical);
                }
                site.TypeIdLocal = typeIdLocal.Name;
                site.TargetLocal = inst.Target;
                site.ExcTarget = inst.ExcTarget;
                site.Original = inst;
                var argTypes = IndirectStaticArgTypes(fn, inst);
                var armIndex = 0;
                foreach (var overload in ClassInitOverloads(context, mir))
                {
                    if (!tainted.Contains(overload.InitFn.Symbol.Canonical))
                    {
                        continue;
                    }
                    foreach (var (plan, paramTypes) in SheetsOf(context, overload))
                    {
                        if (!IndirectArgsMatch(argTypes, paramTypes))
                        {
                            continue;
                        }
                        var callee = overload.InitFn;
                        var frameCanonical = SyntheticTypePlanner.FrameCanonicalOf(
                            callee.Symbol.Canonical);
                        var calleeLocal = "$mw.callee." + point.State + "." + armIndex;
                        armIndex++;
                        fn.AddLocal(new MirLocal(calleeLocal,
                            MirType.Of(frameCanonical)));
                        var entry = new CallSiteInfo
                        {
                            Callee = callee,
                            CalleeLocal = calleeLocal,
                            CalleeFrameCanonical = frameCanonical,
                            ResumeSymbol = ProxyBakeSupport.SyntheticMember(
                                "$mw.resume." + callee.Symbol.Canonical, owner: null),
                            ResultFieldSymbol = null,
                        };
                        // 落参实参 = .this（臂内新建对象落定 Target
                        // 槽）+ 用户实参；宿主构造形态 = 臂 sheet（闭
                        // 合构造 → 类级 typeid 常量合成）
                        var withThis = new List<MirOperand>
                        {
                            new MirLocalOperand(inst.Target),
                        };
                        withThis.AddRange(inst.Args);
                        PlanArgDrops(context, fn, entry, callee, withThis,
                            MwTypeKey.Normalize(plan.Symbol.Canonical));
                        site.Arms.Add(new IndirectInitArm
                        {
                            AllocType = plan.Symbol,
                            SheetCanonical = plan.Symbol.Canonical,
                            InitWrapper = overload.Wrapper,
                            ExclusionSheets = DerivedSheetsOf(context, plan),
                            Impl = entry,
                        });
                    }
                }
                if (site.Arms.Count == 0)
                {
                    throw new CompilerInternalException(
                        "new.indirect 挂起点无命中臂（相关性判定与建臂不一致）: "
                        + fn.Symbol.Canonical);
                }
            }
        }

        // 不可协议化形态拒绝：wrapper/proxy 烘焙产物（$.wrapped./
        // $.mwrapped./$mw. 前缀合成 fn）经 wrapper 派发链/方法地址
        // 间接触达——router/trampoline 的通配 ABI 与值包转发形态无
        // 挂起协议插点（运行期目标集随 wrapper 实例符号表动态决
        // 定，静态闭包不可枚举），保留受控拒绝。R2-b 起 $$call 闭
        // 包体不再拒绝（invoke.indirect 调用点动态分流协议覆盖，
        // 同虚派发臂机制——EmitVirtualCallSplit）
        private static void RejectUnsupportedTaintedShape(MirFunction fn)
        {
            var canonical = fn.Symbol.Canonical;
            // 文本分派是普通 MIR 调用组成的封闭 if 链，没有 wrapper
            // router 的动态包 ABI，可由既有直调/虚调挂起协议完整切分。
            if ((canonical.StartsWith("$mw.", System.StringComparison.Ordinal)
                    && canonical != BuiltinToStringDispatchPass.DispatchCanonical
                    && canonical != BuiltinToStringDispatchPass.HashDispatchCanonical)
                || canonical.Contains("$..init.", System.StringComparison.Ordinal)
                || canonical.Contains(ProxyBakeSupport.WrappedInfix,
                    System.StringComparison.Ordinal)
                || canonical.Contains(ProxyBakeSupport.MwrappedInfix,
                    System.StringComparison.Ordinal))
            {
                throw new MwNotSupportedException(
                    "B-2 暂不支持 proxy/wrapper 烘焙链可达的含挂起点 fn: " + canonical);
            }
        }

    }
}
