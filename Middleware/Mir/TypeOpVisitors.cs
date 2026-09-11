using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Mir
{
    // BIL→MIR 类型运算簇：getid / type.is / is.case / cast / wrapper 安装占位。

    internal sealed class GetIdTypeLowering
        : MirLowerVisitor<GetIdTypeLowering, GetIdTypeInstruction>
    {
        protected override void VisitCore(GetIdTypeInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            // L1：登记静态 id 追踪（get.wrapper.indirect 等 indirect 族的
            // lowering 期解析用）；值本身照常经 MirGetTypeId 物化
            flow.NoteTypeId(inst.Target.Name, MwTypeKey.Normalize(inst.TargetType.TypeRef));
            flow.Add(new MirGetTypeId(inst.TargetType.TypeRef, inst.Target.Name));
        }
    }

    // §12.6 取得字段 fieldid：getid.field field(FIELD) TARGET_FIELDID。
    // fieldid 的运行时值在 native 无消费面（indirect 族 lowering 期经
    // 静态追踪解析回字段符号，与直译版同路径）；MIR 节点仅占位登记
    //（发射 = 槽内写 null，槽型 .fieldid → ptr，见 TypeLayout）
    internal sealed class GetIdFieldLowering
        : MirLowerVisitor<GetIdFieldLowering, GetIdFieldInstruction>
    {
        protected override void VisitCore(GetIdFieldInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.NoteFieldId(inst.Target.Name, inst.Field.Symbol);
            flow.Add(new MirGetFieldId(inst.Field.Symbol, inst.Target.Name));
        }
    }

    internal sealed class GetIdVarLowering
        : MirLowerVisitor<GetIdVarLowering, GetIdVarInstruction>
    {
        protected override void VisitCore(GetIdVarInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Add(new MirGetTypeIdVar(flow.Local(inst.Value), inst.Target.Name));
        }
    }

    internal sealed class DirectTypeCheckLowering
        : MirLowerVisitor<DirectTypeCheckLowering, DirectTypeCheckInstruction>
    {
        protected override void VisitCore(DirectTypeCheckInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            EmitTypeCheck(flow, inst.Kind, flow.Local(inst.Value),
                inst.TargetType.TypeRef, null, inst.Target.Name);
        }

        internal static void EmitTypeCheck(FlowBuilder flow, BilTypeCheckKind kind,
            MirOperand value, string? targetTypeRef, MirOperand? targetTypeId, string target)
        {
            if (targetTypeRef != null
                && GenericAbi.TryPlaceholderName(targetTypeRef, out var name))
            {
                targetTypeId = new MirLocalOperand(".generic." + name);
                targetTypeRef = null;
            }
            flow.Add(new MirTypeCheck(MapKind(kind), value, targetTypeRef, targetTypeId, target));
        }

        internal static MirTypeCheckKind MapKind(BilTypeCheckKind kind) =>
            kind switch
            {
                BilTypeCheckKind.Is => MirTypeCheckKind.Is,
                BilTypeCheckKind.Supers => MirTypeCheckKind.Supers,
                BilTypeCheckKind.With => MirTypeCheckKind.With,
                _ => throw new CompilerInternalException("未知 BilTypeCheckKind: " + kind),
            };
    }

    internal sealed class IndirectTypeCheckLowering
        : MirLowerVisitor<IndirectTypeCheckLowering, IndirectTypeCheckInstruction>
    {
        protected override void VisitCore(IndirectTypeCheckInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            DirectTypeCheckLowering.EmitTypeCheck(flow, inst.Kind, flow.Local(inst.Value),
                null, flow.Local(inst.TypeId), inst.Target.Name);
        }
    }

    internal sealed class IsCaseLowering : MirLowerVisitor<IsCaseLowering, IsCaseInstruction>
    {
        protected override void VisitCore(IsCaseInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            var (_, caseSymbol) = flow.ResolveCase(
                FlowBuilder.EnumOwnerOf(inst.Case.QualifiedName), inst.Case.QualifiedName);
            flow.Add(new MirIsCase(caseSymbol, flow.Local(inst.Value), inst.Target.Name));
        }
    }

    // §12.1/§12.2 动态转换（cast.indirect / cast.safe.indirect）：目标
    // sheet 运行期取自 TYPEID_VAR 局部，直译复用 MirCast 的 indirect 形态
    //（CastEmitter.EmitDynamic：装箱源 + rigi_try_cast + 命中改写视图，
    // 落空按 IsSafe 产 null 或抛 CastException——与 VM CastOrThrow/
    // CastSafe(RequireTypeId) 逐条同语义）
    internal sealed class CastIndirectLowering
        : MirLowerVisitor<CastIndirectLowering, CastIndirectInstruction>
    {
        protected override void VisitCore(CastIndirectInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Add(new MirCast(flow.Local(inst.Source), inst.Target.Name, inst.IsSafe,
                null, flow.Local(inst.TypeId), flow.Tries.CurrentExcTarget()));
        }
    }

    internal sealed class CastLowering : MirLowerVisitor<CastLowering, CastInstruction>
    {
        protected override void VisitCore(CastInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            var sourceType = flow.TypeOf(inst.Source.Name);
            var targetType = MirType.Of(inst.TargetType.TypeRef);
            var resultType = flow.TypeOf(inst.Target.Name);
            var excTarget = flow.Tries.CurrentExcTarget();
            // §12.1 用户自定义转换（review-20260910 #03 native 侧编译期
            // 重写；VM VmTypeOps.TryStartUserConversion 同序：castTo 优先、
            // castFrom 兜底、内建最后）。命中即改写为 operator fn 的
            // MirCall（excTarget 透传），不再落 MirCast
            if (UserConversionRewrite.FindConversion(flow.Context.Symbols,
                    sourceType.Canonical, targetType.Canonical, out var isCastTo) is { } op
                && UserConversionRewrite.EmitConversionCall(flow, inst, op, sourceType,
                    targetType, resultType, excTarget, isCastTo))
            {
                return;
            }
            // safe cast 的失败结果是 Nullable，不能走向非空目标槽直接
            // 拆箱的快路（否则既会抛异常，也会按错误尺寸写结果）。
            if (inst.IsSafe)
            {
                flow.Add(new MirCast(flow.Local(inst.Source), inst.Target.Name, true,
                    targetType.Canonical, null, excTarget));
                return;
            }
            if (TypeLayout.TryGetNullableInner(sourceType, out var unwrapInner)
                && unwrapInner.Canonical == targetType.Canonical)
            {
                flow.Add(new MirUnwrapNullable(flow.Local(inst.Source), unwrapInner,
                    inst.Target.Name, excTarget));
                return;
            }
            if (TypeLayout.TryGetNullableInner(targetType, out var wrapInner)
                && wrapInner.Canonical == sourceType.Canonical)
            {
                flow.Add(new MirWrapNullable(flow.Local(inst.Source), wrapInner,
                    inst.Target.Name));
                return;
            }
            if (flow.IsBoxableValueType(sourceType) && targetType.IsAnyOrObject)
            {
                flow.Add(new MirBoxAny(flow.Local(inst.Source), inst.Target.Name));
                return;
            }
            if (sourceType.IsAnyOrObject && flow.IsBoxableValueType(targetType))
            {
                flow.Add(new MirUnboxAny(flow.Local(inst.Source), inst.Target.Name,
                    excTarget));
                return;
            }
            if (GenericAbi.TryPlaceholderName(targetType.Canonical, out var phName))
            {
                flow.Add(new MirCast(flow.Local(inst.Source), inst.Target.Name,
                    inst.IsSafe, null, new MirLocalOperand(".generic." + phName),
                    excTarget));
                return;
            }
            if (TypeLayout.IsGenericPlaceholder(sourceType))
            {
                flow.Add(new MirCast(flow.Local(inst.Source), inst.Target.Name,
                    inst.IsSafe, targetType.Canonical, null, excTarget));
                return;
            }
            if (MirBuilder.IsNumericScalar(sourceType)
                && MirBuilder.IsNumericScalar(targetType))
            {
                if (sourceType.Key == targetType.Key
                    && !TypeLayout.IsNullable(resultType))
                {
                    flow.Add(new MirCopyLocal(flow.Local(inst.Source), inst.Target.Name));
                    return;
                }
                if (sourceType.Key == targetType.Key
                    && TypeLayout.TryGetNullableInner(resultType, out var numInner)
                    && numInner.Canonical == targetType.Canonical)
                {
                    flow.Add(new MirWrapNullable(flow.Local(inst.Source), numInner,
                        inst.Target.Name));
                    return;
                }
                flow.Add(new MirCast(flow.Local(inst.Source), inst.Target.Name,
                    inst.IsSafe, targetType.Canonical, null, excTarget));
                return;
            }
            if (sourceType.Canonical == targetType.Canonical)
            {
                if (TypeLayout.TryGetNullableInner(resultType, out var idInner)
                    && idInner.Canonical == targetType.Canonical)
                {
                    flow.Add(new MirWrapNullable(flow.Local(inst.Source), idInner,
                        inst.Target.Name));
                    return;
                }
                flow.Add(new MirCopyLocal(flow.Local(inst.Source), inst.Target.Name));
                return;
            }
            if (sourceType.IsAnyOrObject || MirBuilder.IsScalarOrString(sourceType)
                || MirBuilder.IsScalarOrString(targetType)
                || TypeLayout.IsTypeId(sourceType) || TypeLayout.IsTypeId(targetType)
                || flow.IsUserValueType(sourceType) || flow.IsUserValueType(targetType))
            {
                flow.Add(new MirCast(flow.Local(inst.Source), inst.Target.Name,
                    inst.IsSafe, targetType.Canonical, null, excTarget));
                return;
            }
            // 型变视图转换（review-20260910 #01 native 侧编译期分支）：
            // source/target 为同一 class 定义的构造类型且逐实参按定义
            // GenericVariances 可赋值时，静态视图改变零成本（class 胖值
            // 引用，对象头带实际身份；rigi_rt typecheck.c rigi_rewrite_view
            // 对 OBJECT 的视图改写同语义）——直接引用拷贝，不落 MirCast
            //（rigi_sheet_is 无泛型实参型变概念，运行期必抛 CastException）
            if (VarianceViewConversion.IsViewConversion(flow.Context.Symbols,
                    sourceType.Canonical, targetType.Canonical))
            {
                flow.Add(new MirCopyLocal(flow.Local(inst.Source), inst.Target.Name));
                return;
            }
            // 非恒等引用转换一律按 BIL cast 语义检查实际 TypeSheet。这里不
            // 推断“内部擦除视图”：BIL/stdlib 与用户代码使用同一安全边界。
            flow.Add(new MirCast(flow.Local(inst.Source), inst.Target.Name,
                inst.IsSafe, targetType.Canonical, null, excTarget));
        }
    }

    // §12.1 用户自定义转换重写（review-20260910 #03 native 侧；VM
    // VmTypeOps.TryStartUserConversion 的编译期投影）：native 的
    // rigi_try_cast 是纯 C helper 无法回调生成代码，故在落 MirCast 前
    // 静态改写。准入：source/target 均静态可知（非 Any/Object 动态
    // 形态、非泛型占位、非 typeid、非可空对——可空对归既有
    // unwrap/wrap 通道）且源不名义可赋目标（恒等/向上/视图改变——
    // VM TypesAssignable 防线同口径，Dur→Dur 恒等 cast 不递归进
    // castFrom）。分派序：源类型链 castTo（零值参泛型 operator，
    // 方法级 typeid 注入目标类型）→ 目标类型链 castFrom（单值参
    // 可赋源，方法级 typeid 注入源类型；receiver 用目标零值——合成
    // 局部经 EmitBody 建槽纪律零初始化即 VM ZeroOf 口径：struct/
    // wrapper 零值实例、class null）。命中 → MirCall
    //（operatorDispatch=true：class/interface 宿主经 vtable/iMap
    // 实际类型派发，对齐 VM 按 ActualType 解析；excTarget 透传原
    // cast 的词法异常边——#13 教训）。safe cast 命中时 operator
    // 结果（目标类型）先落临时槽再 MirWrapNullable 包进可空结果槽。
    // 已知限制：Any/占位等动态形态不在本分支处理（VM 运行期分派，
    // native 回退内建 MirCast）。FindConversion 与 MirReachability
    // 的可达边共用同一查询（防漂移：改写命中集 ⊆ 可达闭包集）
    internal static class UserConversionRewrite
    {
        // 命中即返回转换 operator 符号；isCastTo 区分方向（实参拼装
        // 形态不同）。castTo 命中即终局（VM 同：castTo 存在即用，
        // 不再回退 castFrom）
        internal static MwMemberSymbol? FindConversion(MwSymbolTable symbols,
            string sourceTypeRef, string targetTypeRef, out bool isCastTo)
        {
            isCastTo = false;
            var sourceType = MirType.Of(sourceTypeRef);
            var targetType = MirType.Of(targetTypeRef);
            if (IsDynamicShape(sourceType) || IsDynamicShape(targetType)
                || VarianceViewConversion.ArgumentAssignable(symbols,
                    sourceType.Canonical, targetType.Canonical))
            {
                return null;
            }
            // castTo 优先（源类型零值参 operator）
            if (Binding.ImplBinder.FindUserConversionOperator(symbols, "castTo",
                    sourceType.Canonical, System.Array.Empty<string>()) is { } castTo)
            {
                isCastTo = true;
                return castTo;
            }
            // castFrom 兜底（目标类型实例 operator，raw 可赋源类型）
            return Binding.ImplBinder.FindUserConversionOperator(symbols, "castFrom",
                targetType.Canonical, new[] { sourceType.Canonical });
        }

        private static bool IsDynamicShape(MirType type) =>
            type.IsAnyOrObject
            || TypeLayout.IsGenericPlaceholder(type)
            || TypeLayout.IsTypeId(type)
            || TypeLayout.IsNullable(type);

        internal static bool EmitConversionCall(FlowBuilder flow, CastInstruction inst,
            MwMemberSymbol op, MirType sourceType, MirType targetType, MirType resultType,
            MirBlock? excTarget, bool isCastTo)
        {
            var symbols = flow.Context.Symbols;
            // fn 体不在模块（纯外部声明）时隐藏 typeid 形状不可知，不重写
            BilFunction? callee = null;
            foreach (var fn in flow.Context.Module.Functions)
            {
                if (fn.Symbol == op.Canonical)
                {
                    callee = fn;
                    break;
                }
            }
            if (callee == null || op.Owner == null)
            {
                return false;
            }
            // 返回类型：宿主泛型代入后仍占位的是方法级泛型返回
            //（castTo<TTarget>()）——本调用点的实例化结果即目标类型
            var returnRef = Binding.ImplBinder.SubstituteHostGenerics(
                CanonicalSignature.Parse(op.Canonical).ReturnTypeRef, op.Owner.Declaration,
                isCastTo ? sourceType.Canonical : targetType.Canonical);
            if (returnRef.Contains(".generic<", System.StringComparison.Ordinal))
            {
                returnRef = targetType.Canonical;
            }
            // 固定返回与目标须归一全等，否则属形状误命中（VM 对该形态
            // 不校验返回；native 静态槽不能吞跨类型结果）——落回内建
            if (MwTypeKey.Normalize(returnRef) != MwTypeKey.Normalize(targetType.Canonical))
            {
                return false;
            }
            string? idName = null;
            string RequireTypeId()
            {
                if (idName == null)
                {
                    idName = flow.SyntheticId("cast.tid");
                    flow.RegisterSyntheticLocal(idName, MirType.Of(".typeid"));
                    var idRef = isCastTo ? targetType.Canonical : sourceType.Canonical;
                    flow.NoteTypeId(idName, MwTypeKey.Normalize(idRef));
                    flow.Add(new MirGetTypeId(idRef, idName));
                }
                return idName;
            }
            string? zeroName = null;
            string RequireZero()
            {
                if (zeroName == null)
                {
                    zeroName = flow.SyntheticId("cast.zero");
                    // 零值物化见 EmitBody 建槽纪律（值类型 memset0 /
                    // 胖引用 null-store），无需写指令
                    flow.RegisterSyntheticLocal(zeroName, targetType);
                }
                return zeroName;
            }
            // MIR 实参表 = fn .args 序剥 .return 与类级 typeid（后者由
            // CallEmitter 按宿主形态合成/剔除；§9.2.3 同口径）
            var args = new List<MirOperand>();
            var sawThis = false;
            foreach (var arg in callee.Args)
            {
                if (arg.Name == ".return")
                {
                    continue;
                }
                if (arg.Name == ".this")
                {
                    sawThis = true;
                    args.Add(isCastTo
                        ? flow.Local(inst.Source)
                        : new MirLocalOperand(RequireZero()));
                    continue;
                }
                if (arg.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                {
                    if (GenericAbi.IsClassLevelTypeId(symbols, op, arg.Name))
                    {
                        continue;
                    }
                    args.Add(new MirLocalOperand(RequireTypeId()));
                    continue;
                }
                // 普通值参（castFrom 的 raw；castTo 经零值参匹配约束无此位）
                args.Add(flow.Local(inst.Source));
            }
            if (!sawThis)
            {
                return false;   // 防御：转换 operator 必为实例方法
            }
            if (!inst.IsSafe)
            {
                flow.Add(new MirCall(op, args, inst.Target.Name, excTarget,
                    operatorDispatch: true));
                return true;
            }
            // safe cast：operator 结果先落目标类型临时槽，再包可空进
            // 结果槽（VM InvokeValues 写裸值进动态槽的静态等价）
            var converted = flow.SyntheticId("cast.conv");
            flow.RegisterSyntheticLocal(converted, MirType.Of(returnRef));
            flow.Add(new MirCall(op, args, converted, excTarget, operatorDispatch: true));
            if (TypeLayout.TryGetNullableInner(resultType, out var inner)
                && inner.Canonical == targetType.Canonical)
            {
                flow.Add(new MirWrapNullable(new MirLocalOperand(converted), inner,
                    inst.Target.Name));
                return true;
            }
            // 结果槽非可空形态属前端非常规输出（防御直写）
            flow.Add(new MirCopyLocal(new MirLocalOperand(converted), inst.Target.Name));
            return true;
        }
    }

    // 型变视图转换判定（#01）：source/target 为同一 class 定义的构造类型
    //（剥实参 canonical 相同），逐实参按定义 GenericVariances 可赋值
    //（out：源实参可赋给目标实参；in：反向；none/缺省：归一全等）——
    // VM BilVerificationContext.TypesAssignable 的 out/in 分支同口径。
    // 仅 class：interface 构造的 iMap 段按构造 sheet 指针身份登记
    //（rigi_imap_entry），视图改变后经视图静态类型派发查不到段——接口
    // 型变须待运行期元数据支持（后续里程碑，不在本判定放行）
    file static class VarianceViewConversion
    {
        internal static bool IsViewConversion(MwSymbolTable symbols,
            string sourceCanonical, string targetCanonical)
        {
            var sourceArguments = TypeArgumentsOf(sourceCanonical);
            var targetArguments = TypeArgumentsOf(targetCanonical);
            if (sourceArguments == null || targetArguments == null
                || sourceArguments.Count != targetArguments.Count
                || BilVerificationContext.StripTypeArguments(sourceCanonical)
                    != BilVerificationContext.StripTypeArguments(targetCanonical)
                || symbols.FindTypeByRef(sourceCanonical) is not { } type
                || type.Declaration.Kind != BilTypeKind.Class)
            {
                return false;
            }
            for (var i = 0; i < sourceArguments.Count; i++)
            {
                var variance = i < type.Declaration.GenericVariances.Count
                    ? type.Declaration.GenericVariances[i]
                    : BilGenericVariance.None;
                var assignable = variance switch
                {
                    BilGenericVariance.Out => ArgumentAssignable(symbols,
                        sourceArguments[i], targetArguments[i]),
                    BilGenericVariance.In => ArgumentAssignable(symbols,
                        targetArguments[i], sourceArguments[i]),
                    // none 须归一全等（不走 TypesCompatible：其 .generic 叶
                    // 降级会把不可证的开放实参对放行，编译期判定须保守）
                    _ => MwTypeKey.Normalize(sourceArguments[i])
                        == MwTypeKey.Normalize(targetArguments[i]),
                };
                if (!assignable)
                {
                    return false;
                }
            }
            return true;
        }

        // 型变实参位可赋值（编译期投影，VM TypesAssignable 同口径）：归一
        // 全等 → Any/Object/ValueType 根规则 → 同定义构造型变递归 →
        // extends/implements 闭包。开放泛型占位实参不可静态证明，恒 false
        //（回退运行期 cast，保持既有行为）。#03 用户转换重写复用本作
        // 名义可赋值防线（恒等/向上/视图改变不进 operator 分派）
        internal static bool ArgumentAssignable(MwSymbolTable symbols, string from, string to)
        {
            var normalizedFrom = MwTypeKey.Normalize(from);
            var normalizedTo = MwTypeKey.Normalize(to);
            if (GenericAbi.TryPlaceholderName(normalizedFrom, out _)
                || GenericAbi.TryPlaceholderName(normalizedTo, out _))
            {
                return false;
            }
            if (normalizedFrom == normalizedTo)
            {
                return true;
            }
            // 根规则：Any/Object 是一切类型的公共上界；ValueType 是一切
            // 值类型的公共上界（class 缺省 extends 即继承 Object 同被覆盖）
            if (MwTypeKey.IsAny(normalizedTo) || MwTypeKey.IsObject(normalizedTo))
            {
                return true;
            }
            if (normalizedTo == "core::ValueType" && IsValueTypeRef(symbols, normalizedFrom))
            {
                return true;
            }
            // 同定义构造嵌套（Box<Box<i32>> → Box<Box<Any>>）：型变递归
            if (IsViewConversion(symbols, normalizedFrom, normalizedTo))
            {
                return true;
            }
            // extends/implements 闭包（ImplBinder.TypeAssignableStatic 同式）
            var visited = new HashSet<string>(System.StringComparer.Ordinal);
            var stack = new Stack<string>();
            stack.Push(normalizedFrom);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!visited.Add(BilVerificationContext.StripTypeArguments(current)))
                {
                    continue;
                }
                if (symbols.FindTypeByRef(current) is not { } currentType)
                {
                    continue;
                }
                if (currentType.Declaration.ExtendsType is { } baseRef)
                {
                    var normalizedBase = MwTypeKey.Normalize(baseRef);
                    if (BilVerificationContext.TypesCompatible(normalizedBase, normalizedTo))
                    {
                        return true;
                    }
                    stack.Push(normalizedBase);
                }
                foreach (var iface in currentType.Declaration.ImplementsTypes)
                {
                    var normalizedIface = MwTypeKey.Normalize(iface);
                    if (BilVerificationContext.TypesCompatible(normalizedIface, normalizedTo))
                    {
                        return true;
                    }
                    stack.Push(normalizedIface);
                }
            }
            return false;
        }

        // 值类型判定（ValueType 根规则用；VM IsValueTypeRef 同口径）：内建
        // 标量/String 或声明为 struct/enum-struct/wrapper
        private static bool IsValueTypeRef(MwSymbolTable symbols, string normalized)
        {
            if (normalized is "core::i8" or "core::i16" or "core::i32" or "core::i64"
                or "core::u8" or "core::u16" or "core::u32" or "core::u64"
                or "core::float" or "core::double" or "core::bool" or "core::char"
                or "core::String")
            {
                return true;
            }
            return symbols.FindTypeByRef(normalized) is { Declaration.Kind:
                BilTypeKind.Struct or BilTypeKind.EnumStruct or BilTypeKind.Wrapper };
        }

        // 构造引用的顶层实参表（"Box<core::i32>" → ["core::i32"]；非构造
        // 形态 null）——BilVerificationContext.TypeArgumentsOf 同式（彼处
        // private 不外露，此处按 SplitTopLevel 重组）
        private static List<string>? TypeArgumentsOf(string typeRef)
        {
            var angle = typeRef.IndexOf('<');
            if (angle < 0 || !typeRef.EndsWith(">", System.StringComparison.Ordinal))
            {
                return null;
            }
            return BilVerificationContext.SplitTopLevel(
                typeRef.Substring(angle + 1, typeRef.Length - angle - 2));
        }
    }
}
