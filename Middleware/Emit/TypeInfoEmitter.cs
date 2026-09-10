using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// TypeInfo 物化：每 TypeSheet 一份全局
    /// {name, sheet, wrappers, wrapperCount, ifaceClosure, ifaceClosureCount, nullableElement, typeIdBound, destroyNative}。
    /// canonical 转义同 sheet；wrappers 来自声明 BilWrappedModifier；
    /// ifaceClosure 来自布局计划的传递 implements 闭包。
    /// </summary>
    internal static class TypeInfoEmitter
    {
        // TypeInfo 结构类型（EmitOne 物化与 ExceptionEmitter 运行期读
        // name 槽互指）：{name, sheet, wrappers, wrapperCount, ifaceClosure,
        // ifaceClosureCount, nullableElement, typeIdBound, destroyNative}
        internal static LLVMTypeRef InfoStructType(LLVMContextRef context)
        {
            var pointer = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
            var fields = new LLVMTypeRef[TypeSheetAbi.InfoFieldCount];
            fields[TypeSheetAbi.InfoFieldName] = StringAbi.ValueType(context);
            fields[TypeSheetAbi.InfoFieldSheet] = pointer;
            fields[TypeSheetAbi.InfoFieldWrappers] = pointer;
            fields[TypeSheetAbi.InfoFieldWrapperCount] = LLVMTypeRef.Int32;
            fields[TypeSheetAbi.InfoFieldIfaceClosure] = pointer;
            fields[TypeSheetAbi.InfoFieldIfaceClosureCount] = LLVMTypeRef.Int32;
            fields[TypeSheetAbi.InfoFieldNullableElement] = pointer;
            fields[TypeSheetAbi.InfoFieldTypeIdBound] = pointer;
            fields[TypeSheetAbi.InfoFieldNativeDestructor] = pointer;
            return context.GetStructType(fields, false);
        }

        internal static void EmitAll(ModuleBuilder.Session session, LayoutPlanTable layout)
        {
            foreach (var plan in layout.Plans)
            {
                EmitOne(session, GenericAbi.PlanKey(plan.Symbol), plan.Symbol.Canonical,
                    plan.Symbol, plan.IfaceClosure);
            }
            foreach (var canonical in TypeLayout.BuiltinSheetCanonicals)
            {
                if (session.TryGetTypeInfo(canonical, out _))
                {
                    continue;
                }
                var description = session.Symbols.FindTypeByRef(canonical);
                EmitOne(session, canonical, canonical, description,
                    description == null ? System.Array.Empty<string>()
                        : VTablePlanner.CollectIfaceClosure(canonical, description, session.Symbols));
            }
        }

        private static void EmitOne(ModuleBuilder.Session session, string key, string name,
            MwTypeSymbol? type, IReadOnlyList<string> ifaceClosure)
        {
            if (!session.TryGetTypeSheet(key, out var sheet)
                && !session.TryGetTypeSheet(name, out sheet))
            {
                throw new CompilerInternalException($"TypeInfo 缺 TypeSheet: {key}");
            }
            var wrappers = CollectWrappers(session, type);
            var ifaces = CollectIfaceSheets(session, ifaceClosure);
            var pointer = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
            var wrapperGlobal = LLVMValueRef.CreateConstPointerNull(pointer);
            if (wrappers.Count > 0)
            {
                wrapperGlobal = AddConstantGlobal(session.Module,
                    LLVMTypeRef.CreateArray(pointer, (uint)wrappers.Count),
                    GenericAbi.EscapeGlobalName("typeinfo.wrappers.", key),
                    LLVMValueRef.CreateConstArray(pointer, wrappers.ToArray()));
            }
            var ifaceGlobal = LLVMValueRef.CreateConstPointerNull(pointer);
            if (ifaces.Count > 0)
            {
                ifaceGlobal = AddConstantGlobal(session.Module,
                    LLVMTypeRef.CreateArray(pointer, (uint)ifaces.Count),
                    GenericAbi.EscapeGlobalName("typeinfo.ifaces.", key),
                    LLVMValueRef.CreateConstArray(pointer, ifaces.ToArray()));
            }
            var nameValue = StringAbi.BuildConstant(session.Module, name,
                "typeinfo.name." + GenericAbi.EscapeGlobalName("", key));
            var infoType = InfoStructType(session.Context);
            var global = session.Module.AddGlobal(infoType,
                GenericAbi.EscapeGlobalName("typeinfo.", key));
            global.Linkage = LLVMLinkage.LLVMInternalLinkage;
            global.IsGlobalConstant = true;
            global.Initializer = session.Context.GetConstStruct(new[]
            {
                nameValue,
                sheet,
                wrapperGlobal,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)wrappers.Count, true),
                ifaceGlobal,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)ifaces.Count, true),
                // 构造 Nullable 的元素类型是实际 sheet，不用名字推断或擦成 Any。
                TypeLayout.TryGetNullableInner(Mir.MirType.Of(key), out var nullableInner)
                    && GenericAbi.IsClosedConstructed(key)
                    ? session.TypeSheetFor(nullableInner.Canonical)
                    : LLVMValueRef.CreateConstPointerNull(pointer),
                TypeLayout.IsTypeId(Mir.MirType.Of(key))
                    ? session.TypeSheetFor(ConstructedTypeCollector.TypeArgumentsOf(key)
                        .FirstOrDefault() ?? "core::Any")
                    : LLVMValueRef.CreateConstPointerNull(pointer),
                EmitNativeDestructor(session, key, type),
            }, false);
            session.RegisterTypeInfo(key, global);
            if (key != name && !session.TryGetTypeInfo(name, out _)
                && session.Layout?.Find(name) is { } aliasPlan
                && GenericAbi.PlanKey(aliasPlan.Symbol) == key)
            {
                session.RegisterTypeInfo(name, global);
            }
        }

        // 协程基础对象独占 gate/Alarm 底座；活动方法、帧和 Lock 保活。
        // 回调只释放原生资源，不调用 Rigi 或再次进入 ARC/GC fence。
        private static LLVMValueRef EmitNativeDestructor(ModuleBuilder.Session session,
            string key, MwTypeSymbol? type)
        {
            var pointer = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
            var declaration = type?.Declaration.Symbol;
            var alarm = declaration == "core.coroutine::EventAlarm";
            if (declaration != "core.coroutine::Mutex"
                && declaration != "core.coroutine::Task"
                && declaration != "core.coroutine::Task<TReturn>"
                && declaration != "core.coroutine::Dispatcher" && !alarm)
                return LLVMValueRef.CreateConstPointerNull(pointer);
            var plan = session.Layout?.Find(key)
                ?? throw new CompilerInternalException("原生资源所有者缺布局: " + key);
            var gate = System.Linq.Enumerable.Single(plan.Fields,
                field => field.Symbol.EndsWith(alarm ? "#handle@.i64" : "#gate@.i64", System.StringComparison.Ordinal));
            var fnType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, new[] { pointer }, false);
            var fn = session.Module.AddFunction(GenericAbi.EscapeGlobalName("native.destroy.", key), fnType);
            fn.Linkage = LLVMLinkage.LLVMInternalLinkage;
            using var builder = session.Context.CreateBuilder();
            builder.PositionAtEnd(fn.AppendBasicBlock("entry"));
            var slot = builder.BuildInBoundsGEP2(LLVMTypeRef.Int8, fn.GetParam(0),
                new[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)gate.Offset) }, "gate.slot");
            var handle = builder.BuildLoad2(LLVMTypeRef.Int64, slot, "gate");
            builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0), slot);
            var (destroy, destroyType) = CallEmitter.DeclareHelperFace(session,
                alarm ? "rigi_alarm_release" : "rigi_sync_mutex_destroy", LLVMTypeRef.Void, new[] { LLVMTypeRef.Int64 });
            builder.BuildCall2(destroyType, destroy, new[] { handle }, "");
            if (declaration == "core.coroutine::Task"
                || declaration == "core.coroutine::Task<TReturn>")
            {
                var failure = System.Linq.Enumerable.Single(plan.Fields,
                    field => field.Symbol.EndsWith("#failureNodeId@.i64", System.StringComparison.Ordinal));
                var failureSlot = builder.BuildInBoundsGEP2(LLVMTypeRef.Int8, fn.GetParam(0),
                    new[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)failure.Offset) }, "failure.slot");
                var node = builder.BuildLoad2(LLVMTypeRef.Int64, failureSlot, "failure.node");
                builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0), failureSlot);
                var (release, releaseType) = CallEmitter.DeclareHelperFace(session,
                    "rigi_failure_release_task", LLVMTypeRef.Void, new[] { LLVMTypeRef.Int64 });
                builder.BuildCall2(releaseType, release, new[] { node }, "");
            }
            builder.BuildRetVoid();
            return fn;
        }

        private static List<LLVMValueRef> CollectWrappers(ModuleBuilder.Session session,
            MwTypeSymbol? type)
        {
            var list = new List<LLVMValueRef>();
            if (type == null)
            {
                return list;
            }
            foreach (var modifier in type.Declaration.Modifiers)
            {
                if (modifier is not BilWrappedModifier wrapped)
                {
                    continue;
                }
                if (session.TryGetTypeSheet(wrapped.WrapperTypeRef, out var sheet)
                    || session.TryGetTypeSheet(MwTypeKey.Normalize(wrapped.WrapperTypeRef),
                        out sheet))
                {
                    list.Add(sheet);
                }
            }
            return list;
        }

        private static List<LLVMValueRef> CollectIfaceSheets(ModuleBuilder.Session session,
            IReadOnlyList<string> ifaceClosure)
        {
            var list = new List<LLVMValueRef>();
            foreach (var iface in ifaceClosure)
            {
                if (session.TryGetTypeSheet(iface, out var sheet)
                    || session.TryGetTypeSheet(MwTypeKey.Normalize(iface), out sheet))
                {
                    list.Add(sheet);
                }
            }
            return list;
        }

        private static LLVMValueRef AddConstantGlobal(LLVMModuleRef module, LLVMTypeRef type,
            string name, LLVMValueRef initializer)
        {
            var global = module.AddGlobal(type, name);
            global.Linkage = LLVMLinkage.LLVMInternalLinkage;
            global.IsGlobalConstant = true;
            global.Initializer = initializer;
            return global;
        }
    }
}
