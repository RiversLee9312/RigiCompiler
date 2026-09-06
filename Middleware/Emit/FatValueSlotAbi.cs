using LLVMSharp.Interop;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Emit
{
    // Cell、同步 Func/Action 与泛型接口的开放/封闭调用必须共享同一槽 ABI。槽始终传递胖值，
    // override 的真实标量/struct ABI 在入口适配；不能按调用点猜槽机器签名。
    internal static class FatValueSlotAbi
    {
        private const string ValueType = ".generic<$.generic.SlotValue>";
        private static string Name(string canonical) => "fat.slot." + canonical;

        internal static bool IsGenericInterfaceSlot(MwMemberSymbol method)
        {
            if (method.Owner?.Declaration.Kind != BilTypeKind.Interface) return false;
            var signature = CanonicalSignature.Parse(method.Canonical);
            return signature.ReturnTypeRef.Contains(".generic<", StringComparison.Ordinal)
                || signature.Parameters.Any(p => p.TypeRef.Contains(".generic<", StringComparison.Ordinal));
        }

        // 按接口声明寻址适配器，不能把实现方法的普通类槽一起改成接口 ABI。
        private static MwMemberSymbol? InterfaceMethodAt(ModuleBuilder.Session session, TypeLayoutPlan plan, int slot)
        {
            foreach (var (iface, offset) in plan.IMap)
            {
                var methods = session.Layout!.Find(iface)?.VTableSlots;
                if (methods != null && slot >= offset && slot < offset + methods.Count)
                    return session.Symbols.FindMember(methods[slot - offset]);
            }
            return null;
        }

        internal static LLVMValueRef Entry(ModuleBuilder.Session session, TypeLayoutPlan plan, int slot,
            LLVMValueRef ordinary)
        {
            var method = InterfaceMethodAt(session, plan, slot);
            if (method == null) return Entry(session, plan.VTableSlots[slot], ordinary);
            // 非泛型接口仍使用自身机器签名，包括 Cell 同时实现的普通接口。
            return IsGenericInterfaceSlot(method)
                && session.TryGetSynthetic(InterfaceName(method, plan.VTableSlots[slot]), out var thunk)
                ? thunk.Fn : ordinary;
        }

        private static string InterfaceName(MwMemberSymbol method, string implementation) =>
            Name("interface." + method.Canonical + "." + implementation);

        private static CanonicalSignature InterfaceSignature(MirFunction function) =>
            CanonicalSignature.Create(CallEmitter.ExpectedCallParams(function).Skip(1)
                .Select(p => (p.Name, p.Name.StartsWith(".generic.", StringComparison.Ordinal)
                    ? p.Type.Canonical : ValueType)).ToArray(), function.ReturnType.IsVoid ? ".void" : ValueType);

        internal static CanonicalSignature InterfaceSignature(ModuleBuilder.Session session, MwMemberSymbol method)
        {
            CanonicalSignature? agreed = null;
            foreach (var plan in session.Layout!.Plans)
                for (var slot = 0; slot < plan.VTableSlots.Count; slot++)
                    if (InterfaceMethodAt(session, plan, slot)?.Canonical == method.Canonical
                        && session.TryGetFunction(plan.VTableSlots[slot], out var function))
                    {
                        var current = InterfaceSignature(function.Mir);
                        // 普通参数由接口声明约束；方法级 typeid 仅登记在 BIL fn 参数表。
                        // 全部实现必须提供相同隐藏前缀，不能让布局遍历顺序决定公共 ABI。
                        if (agreed != null && (agreed.ReturnTypeRef != current.ReturnTypeRef
                            || !agreed.Parameters.Select(p => p.TypeRef).SequenceEqual(current.Parameters.Select(p => p.TypeRef))))
                            throw new CompilerInternalException($"接口槽实现 ABI 不一致: {method.Canonical}");
                        agreed = current;
                    }
            if (agreed != null) return agreed;
            // 无实现的抽象槽仍能声明；方法级隐藏参数以有体的实际实现为准。
            var signature = CanonicalSignature.Parse(method.Canonical);
            return CanonicalSignature.Create(signature.Parameters.Select(p => (p.Name, ValueType)).ToArray(),
                signature.ReturnTypeRef == ".void" && !method.HasKeyword(BilKeyword.Async) ? ".void" : ValueType);
        }

        private static IEnumerable<(string Name, MirFunction Function, CanonicalSignature Signature)> Adapters(ModuleBuilder.Session session)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var signatures = new Dictionary<string, CanonicalSignature>(StringComparer.Ordinal);
            foreach (var function in session.Mir.Functions)
                if (Applies(session, function.Symbol))
                    yield return (Name(function.Symbol.Canonical), function, Signature(function.Symbol));
            if (session.Layout == null) yield break;
            foreach (var plan in session.Layout!.Plans)
                for (var slot = 0; slot < plan.VTableSlots.Count; slot++)
                {
                    var method = InterfaceMethodAt(session, plan, slot);
                    if (method == null || !IsGenericInterfaceSlot(method)
                        || !session.TryGetFunction(plan.VTableSlots[slot], out var function)) continue;
                    var name = InterfaceName(method, plan.VTableSlots[slot]);
                    if (!signatures.TryGetValue(method.Canonical, out var signature))
                        signatures[method.Canonical] = signature = InterfaceSignature(session, method);
                    if (seen.Add(name)) yield return (name, function.Mir, signature);
                }
        }

        internal static bool Applies(ModuleBuilder.Session session, MwMemberSymbol method)
        {
            if (IsCallable(session, method)) return true;
            if (!(method.SignatureKey.StartsWith("getValue(", StringComparison.Ordinal)
                || method.SignatureKey.StartsWith("setValue(", StringComparison.Ordinal))) return false;
            var signature = CanonicalSignature.Parse(method.Canonical);
            var getter = method.SignatureKey.StartsWith("getValue(", StringComparison.Ordinal);
            if (signature.Parameters.Count != (getter ? 0 : 1)
                || (!getter && signature.ReturnTypeRef != ".void")) return false;
            if (method.Owner?.Canonical is not ("core::Cell" or "core::ReadonlyCell")
                && !method.HasKeyword(BilKeyword.Override)) return false;
            for (var owner = method.Owner; owner != null;
                owner = owner.Declaration.ExtendsType is { } parent
                    ? session.Symbols.FindTypeByRef(parent) : null)
            {
                if (owner.Canonical is "core::Cell" or "core::ReadonlyCell") return true;
            }
            return false;
        }

        internal static bool IsCallable(ModuleBuilder.Session session, MwMemberSymbol method)
        {
            if (!method.SignatureKey.StartsWith("$call(", StringComparison.Ordinal)
                || method.HasKeyword(BilKeyword.Async)) return false;
            for (var owner = method.Owner; owner != null;
                owner = owner.Declaration.ExtendsType is { } parent
                    ? session.Symbols.FindTypeByRef(parent) : null)
            {
                if (owner.Canonical is "core::Func" or "core::Action") return true;
            }
            return false;
        }

        internal static CanonicalSignature Signature(MwMemberSymbol method) =>
            method.SignatureKey.StartsWith("$call(", StringComparison.Ordinal)
                ? CanonicalSignature.Create(CanonicalSignature.Parse(method.Canonical).Parameters
                    .Select(p => (p.Name, ValueType)).ToArray(),
                    CanonicalSignature.Parse(method.Canonical).ReturnTypeRef == ".void" ? ".void" : ValueType)
                : method.SignatureKey.StartsWith("getValue(", StringComparison.Ordinal)
                ? CanonicalSignature.Create(Array.Empty<(string, string)>(), ValueType)
                : CanonicalSignature.Create(new[] { ("value", ValueType) }, ".void");

        internal static LLVMValueRef Entry(ModuleBuilder.Session session, string canonical,
            LLVMValueRef ordinary) => session.TryGetSynthetic(Name(canonical), out var thunk)
                ? thunk.Fn : ordinary;

        internal static void DeclareAll(ModuleBuilder.Session session)
        {
            foreach (var (name, function, signature) in Adapters(session))
            {
                var fat = TypeLayout.FatReferenceType(session.Context);
                var type = LLVMTypeRef.CreateFunction(signature.ReturnTypeRef == ".void"
                    ? LLVMTypeRef.Void : fat,
                    new[] { fat }.Concat(signature.Parameters.Select(p =>
                        TypeLayout.MapType(session.Context, MirType.Of(p.TypeRef)))).ToArray(), false);
                var thunk = session.Module.AddFunction(name, type);
                thunk.Linkage = LLVMLinkage.LLVMInternalLinkage;
                session.RegisterSynthetic(name, thunk, type);
            }
        }

        internal static void EmitAll(ModuleBuilder.Session session, LLVMBuilderRef builder)
        {
            foreach (var (name, function, signature) in Adapters(session))
            {
                if (!session.TryGetSynthetic(name, out var thunk)) continue;
                session.SetCurrentFunction(thunk.Fn);
                builder.PositionAtEnd(thunk.Fn.AppendBasicBlock("entry"));
                var slots = new Dictionary<string, (LLVMValueRef Slot, MirLocal Local)>();
                var args = new List<MirOperand>();
                AddSlot("receiver", MirType.Of(function.Symbol.Owner!.Canonical));
                builder.BuildStore(thunk.Fn.GetParam(0), slots["receiver"].Slot);
                args.Add(new MirLocalOperand("receiver"));
                for (var i = 0; i < signature.Parameters.Count; i++)
                {
                    var valueName = "value" + i;
                    AddSlot(valueName, MirType.Of(signature.Parameters[i].TypeRef));
                    builder.BuildStore(thunk.Fn.GetParam((uint)i + 1), slots[valueName].Slot);
                    args.Add(new MirLocalOperand(valueName));
                }
                var hasResult = !function.ReturnType.IsVoid;
                if (hasResult) AddSlot("result", function.ReturnType);
                var callee = session.FunctionOf(function.Symbol.Canonical);
                var temps = new List<ArcEmitter.RichTemp>();
                var boxed = new List<ArcEmitter.FatTemp>();
                var actualArgs = CallEmitter.MarshalArgs(session, builder, slots, function,
                    args, hasResult ? "result" : null, temps, boxed);
                var result = builder.BuildCall2(callee.Type, callee.Value, actualArgs, "");
                ArcEmitter.DestroyRichTemps(session, builder, temps);
                ArcEmitter.DestroyFatTemps(session, builder, boxed);
                // 不消费 pending；适配器先返回零值，原调用点既有异常边接力。
                var pointer = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
                var (pendingFn, pendingType) = CallEmitter.DeclareHelperFace(session,
                    "rigi_exc_pending", pointer, Array.Empty<LLVMTypeRef>());
                var pending = builder.BuildCall2(pendingType, pendingFn, Array.Empty<LLVMValueRef>(), "pending");
                var failed = thunk.Fn.AppendBasicBlock("failed");
                var success = thunk.Fn.AppendBasicBlock("success");
                builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, pending,
                    LLVMValueRef.CreateConstNull(pointer), "has.pending"), failed, success);
                builder.PositionAtEnd(failed);
                if (hasResult) builder.BuildRet(LLVMValueRef.CreateConstNull(TypeLayout.FatReferenceType(session.Context)));
                else builder.BuildRetVoid();
                builder.PositionAtEnd(success);
                if (!hasResult) { builder.BuildRetVoid(); continue; }
                if (!session.IsInlineValueType(function.ReturnType, out _))
                    builder.BuildStore(result, slots["result"].Slot);
                if (BoxEmitter.NeedsBox(session, function.ReturnType, MirType.Of(ValueType)))
                {
                    var fatResult = BoxEmitter.BoxFromLocal(session, builder, slots, "result");
                    ArcEmitter.EmitReleaseSlot(session, builder, slots, "result");
                    builder.BuildRet(fatResult);
                }
                else builder.BuildRet(builder.BuildLoad2(TypeLayout.FatReferenceType(session.Context),
                    slots["result"].Slot, "result.fat"));

                void AddSlot(string name, MirType type)
                {
                    var inline = session.IsInlineValueType(type, out var plan);
                    var slot = builder.BuildAlloca(inline
                        ? LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)plan.Size)
                        : TypeLayout.MapType(session.Context, type), name);
                    if (inline) slot.Alignment = (uint)plan.Alignment;
                    slots.Add(name, (slot, new MirLocal(name, type)));
                }
            }
        }
    }
}
