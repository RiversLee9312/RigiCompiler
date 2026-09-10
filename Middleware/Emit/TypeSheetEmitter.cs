using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// TypeSheet 发射（RUNTIME §6 原样）：每非 External 类型四个全局——
    /// vtable / iMap / refMap / TypeSheet。typeInfoId 指向 TypeInfo 全局
    ///（TypeInfoEmitter）；baseTypeId 指向基类 TypeSheet（interface/wrapper
    /// 取 ExtendsType）；vTable 未进 MIR 的方法为 null。
    /// 字段序见 TypeSheetAbi（arc.h RigiTypeSheet 逐位镜像）；DynamicNewEmitter
    /// 读槽 0 分发器时按 TypeSheetAbi.FieldVTable 内联 GEP。
    /// </summary>
    internal static class TypeSheetEmitter
    {
        // TypeSheet 字段序见 TypeSheetAbi（与 arc.h 镜像）；本类只填 LLVM 结构与常量。
        // DynamicNewEmitter 读槽 0 分发器时按 TypeSheetAbi.FieldVTable 内联 GEP。

        internal static LLVMTypeRef SheetStructType(LLVMContextRef context)
        {
            var pointer = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
            var i32 = LLVMTypeRef.Int32;
            var fields = new LLVMTypeRef[TypeSheetAbi.SheetFieldCount];
            fields[TypeSheetAbi.FieldTypeInfoId] = pointer;
            fields[TypeSheetAbi.FieldBaseTypeId] = pointer;
            fields[TypeSheetAbi.FieldTypeSize] = i32;
            fields[TypeSheetAbi.FieldTypeFlags] = i32;
            fields[TypeSheetAbi.FieldVTableSize] = i32;
            fields[TypeSheetAbi.FieldVTable] = pointer;
            fields[TypeSheetAbi.FieldIMapSize] = i32;
            fields[TypeSheetAbi.FieldIMap] = pointer;
            fields[TypeSheetAbi.FieldRefMapSize] = i32;
            fields[TypeSheetAbi.FieldRefMap] = pointer;
            return context.GetStructType(fields, false);
        }

        internal static void EmitAll(ModuleBuilder.Session session, LayoutPlanTable layout)
        {
            var module = session.Module;
            var context = session.Context;
            var pointer = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
            var i32 = LLVMTypeRef.Int32;
            var i16 = LLVMTypeRef.Int16;
            var sheetType = SheetStructType(context);
            var nullPointer = LLVMValueRef.CreateConstPointerNull(pointer);

            var sheetGlobals = new Dictionary<string, LLVMValueRef>(System.StringComparer.Ordinal);
            foreach (var plan in layout.Plans)
            {
                var key = GenericAbi.PlanKey(plan.Symbol);
                var global = module.AddGlobal(sheetType,
                    GenericAbi.EscapeGlobalName("typesheet.", key));
                global.Linkage = LLVMLinkage.LLVMInternalLinkage;
                global.IsGlobalConstant = true;
                sheetGlobals.Add(key, global);
                session.RegisterTypeSheet(key, global);
                if (key != plan.Symbol.Canonical && !sheetGlobals.ContainsKey(plan.Symbol.Canonical)
                    && ReferenceEquals(layout.Find(plan.Symbol.Canonical), plan))
                {
                    sheetGlobals.Add(plan.Symbol.Canonical, global);
                    session.RegisterTypeSheet(plan.Symbol.Canonical, global);
                }
            }

            foreach (var canonical in TypeLayout.BuiltinSheetCanonicals)
            {
                if (sheetGlobals.ContainsKey(canonical))
                {
                    continue;
                }
                var global = module.AddGlobal(sheetType, "typesheet." + canonical);
                global.Linkage = LLVMLinkage.LLVMInternalLinkage;
                global.IsGlobalConstant = true;
                sheetGlobals.Add(canonical, global);
                session.RegisterTypeSheet(canonical, global);
            }
            TypeInfoEmitter.EmitAll(session, layout);

            foreach (var canonical in TypeLayout.BuiltinSheetCanonicals)
            {
                var (size, flags) = TypeLayout.BuiltinSheetLayout(canonical);
                var builtinSheet = sheetGlobals[canonical];
                var refMap = nullPointer;
                var refMapSize = 0;
                if (canonical == "core::String")
                {
                    // kind1 | 跳数 0：String 槽自身偏移 0
                    var entry = LLVMValueRef.CreateConstInt(i16,
                        TypeLayout.EncodeRefMap(TypeLayout.RefMapKindString, 0), false);
                    refMap = AddConstantGlobal(module,
                        LLVMTypeRef.CreateArray(i16, 1),
                        "typesheet.refmap.core::String",
                        LLVMValueRef.CreateConstArray(i16, new[] { entry }));
                    refMapSize = 1;
                }
                builtinSheet.Initializer = BuildSheetConst(context, i32,
                    session.TypeInfoFor(canonical), nullPointer, size, flags,
                    0, nullPointer, 0, nullPointer, refMapSize, refMap);
            }
            foreach (var plan in layout.Plans)
            {
                var info = session.TypeInfoFor(GenericAbi.PlanKey(plan.Symbol));
                var baseSheet = ResolveBaseSheet(plan, sheetGlobals, nullPointer);
                if (plan.Kind is TypeLayoutKind.Interface)
                {
                    var shell = sheetGlobals[GenericAbi.PlanKey(plan.Symbol)];
                    shell.Initializer = BuildSheetConst(context, i32, info, baseSheet,
                        0, plan.TypeFlags, 0, nullPointer, 0, nullPointer, 0, nullPointer);
                    continue;
                }
                var vTable = nullPointer;
                if (plan.VTableSlots.Count > 0)
                {
                    var entries = new LLVMValueRef[plan.VTableSlots.Count];
                    for (var i = 0; i < entries.Length; i++)
                    {
                        if (plan.VTableSlots[i] == LayoutEngine.InitDispatchSlot)
                        {
                            entries[i] = session.TryGetSynthetic(
                                DynamicNewEmitter.DispatcherName(plan), out var syn)
                                ? syn.Fn
                                : nullPointer;
                            continue;
                        }
                        entries[i] = session.TryGetFunction(plan.VTableSlots[i], out var emitted)
                            ? FatValueSlotAbi.Entry(session, plan, i, emitted.Value)
                            : nullPointer;
                    }
                    vTable = AddConstantGlobal(module,
                        LLVMTypeRef.CreateArray(pointer, (uint)entries.Length),
                        GenericAbi.EscapeGlobalName("typesheet.vtable.", GenericAbi.PlanKey(plan.Symbol)),
                        LLVMValueRef.CreateConstArray(pointer, entries));
                }

                var iMap = nullPointer;
                if (plan.IMap.Count > 0)
                {
                    var pairType = context.GetStructType(new[] { pointer, i32 }, false);
                    var entries = new LLVMValueRef[plan.IMap.Count];
                    for (var i = 0; i < entries.Length; i++)
                    {
                        var ifaceSheet = sheetGlobals.TryGetValue(plan.IMap[i].InterfaceType, out var iface)
                            ? iface
                            : nullPointer;
                        entries[i] = context.GetConstStruct(new[]
                        {
                            ifaceSheet,
                            LLVMValueRef.CreateConstInt(i32, (uint)plan.IMap[i].BaseOffset, false),
                        }, false);
                    }
                    iMap = AddConstantGlobal(module,
                        LLVMTypeRef.CreateArray(pairType, (uint)entries.Length),
                        GenericAbi.EscapeGlobalName("typesheet.imap.", GenericAbi.PlanKey(plan.Symbol)),
                        LLVMValueRef.CreateConstArray(pairType, entries));
                }

                var refMap = nullPointer;
                if (plan.RefMap.Length > 0)
                {
                    var entries = new LLVMValueRef[plan.RefMap.Length];
                    for (var i = 0; i < entries.Length; i++)
                    {
                        entries[i] = LLVMValueRef.CreateConstInt(i16, plan.RefMap[i], false);
                    }
                    refMap = AddConstantGlobal(module,
                        LLVMTypeRef.CreateArray(i16, (uint)entries.Length),
                        GenericAbi.EscapeGlobalName("typesheet.refmap.", GenericAbi.PlanKey(plan.Symbol)),
                        LLVMValueRef.CreateConstArray(i16, entries));
                }

                var sheet = sheetGlobals[GenericAbi.PlanKey(plan.Symbol)];
                sheet.Initializer = BuildSheetConst(context, i32, info, baseSheet,
                    plan.Size, plan.TypeFlags,
                    plan.VTableSlots.Count, vTable,
                    plan.IMap.Count, iMap,
                    plan.RefMap.Length, refMap);
            }
        }

        private static LLVMValueRef ResolveBaseSheet(TypeLayoutPlan plan,
            Dictionary<string, LLVMValueRef> sheetGlobals, LLVMValueRef nullPointer)
        {
            if (plan.BasePlan != null)
            {
                return sheetGlobals[GenericAbi.PlanKey(plan.BasePlan.Symbol)];
            }
            var extends = plan.Symbol.Declaration.ExtendsType;
            if (string.IsNullOrEmpty(extends))
            {
                // BIL 的 class 可省略默认 Object 基类；运行期祖先链必须
                // 仍包含这个真实父类型，否则开放泛型中 Node → Object
                // 的检查会失败。只补默认根，不擦除任何构造类型实参。
                if (plan.Kind == TypeLayoutKind.Class
                    && (plan.TypeFlags & TypeLayoutPlan.FlagInlineValue) == 0
                    && !MwTypeKey.IsObject(plan.Symbol.Canonical)
                    && !MwTypeKey.IsAny(plan.Symbol.Canonical)
                    && sheetGlobals.TryGetValue("core::Object", out var objectSheet))
                    return objectSheet;
                return nullPointer;
            }
            var normalized = MwTypeKey.Normalize(extends);
            if (sheetGlobals.TryGetValue(normalized, out var sheet)
                || sheetGlobals.TryGetValue(extends, out sheet))
            {
                return sheet;
            }
            return nullPointer;
        }

        private static LLVMValueRef BuildSheetConst(LLVMContextRef context, LLVMTypeRef i32,
            LLVMValueRef typeInfo, LLVMValueRef baseSheet, int size, uint flags,
            int vTableSize, LLVMValueRef vTable, int iMapSize, LLVMValueRef iMap,
            int refMapSize, LLVMValueRef refMap)
        {
            return context.GetConstStruct(new[]
            {
                typeInfo,
                baseSheet,
                LLVMValueRef.CreateConstInt(i32, (uint)size, false),
                LLVMValueRef.CreateConstInt(i32, flags, false),
                LLVMValueRef.CreateConstInt(i32, (uint)vTableSize, false),
                vTable,
                LLVMValueRef.CreateConstInt(i32, (uint)iMapSize, false),
                iMap,
                LLVMValueRef.CreateConstInt(i32, (uint)refMapSize, false),
                refMap,
            }, false);
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
