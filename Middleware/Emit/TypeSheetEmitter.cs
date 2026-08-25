using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Layout;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// TypeSheet 发射（MW4 批 1，RUNTIME §6 原样）：每非 External 类型
    /// 四个全局——vtable（fnptr 数组）/iMap（{iface sheet, u32 base offset}
    /// 对数组）/refMap（u16 跳数数组）/TypeSheet 结构体（internal 链接、
    /// 常量）。typeInfoId 恒 null（MW8 TypeInfo 定稿）；baseTypeId 指向
    /// 基类 TypeSheet 全局（无/不可解析为 null；struct/enum 的 ValueType
    /// 内建 sheet 随 stdlib 实体化后补）；vTable 条目引用对应 LLVM fn，
    /// 未进 MIR 的方法（不可达/abstract）本批为 null（MW5 调用 ABI 的
    /// 可达性扩编兜底）；iMap 按声明序（加载期按地址排序的语义待首个
    /// 消费者批定稿）。
    /// </summary>
    internal static class TypeSheetEmitter
    {
        internal static void EmitAll(ModuleBuilder.Session session, LayoutPlanTable layout)
        {
            var module = session.Module;
            var context = session.Context;
            var pointer = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
            var i32 = LLVMTypeRef.Int32;
            var i16 = LLVMTypeRef.Int16;
            // §6 字段序：typeInfoId / baseTypeId / typeSize / typeFlags /
            // vTableSize / vTable / iMapSize / iMap / refMapSize / refMap
            var sheetType = context.GetStructType(new[]
            {
                pointer, pointer, i32, i32, i32, pointer, i32, pointer, i32, pointer,
            }, false);
            var nullPointer = LLVMValueRef.CreateConstPointerNull(pointer);

            // 先建全部 TypeSheet 全局（baseTypeId 交叉引用可前向），再回填
            var sheetGlobals = new Dictionary<string, LLVMValueRef>(System.StringComparer.Ordinal);
            foreach (var plan in layout.Plans)
            {
                var global = module.AddGlobal(sheetType, "typesheet." + plan.Symbol.Canonical);
                global.Linkage = LLVMLinkage.LLVMInternalLinkage;
                global.IsGlobalConstant = true;
                sheetGlobals.Add(plan.Symbol.Canonical, global);
                session.RegisterTypeSheet(plan.Symbol.Canonical, global);
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
                var (size, flags) = TypeLayout.BuiltinSheetLayout(canonical);
                global.Initializer = context.GetConstStruct(new[]
                {
                    nullPointer, nullPointer,
                    LLVMValueRef.CreateConstInt(i32, (uint)size, false),
                    LLVMValueRef.CreateConstInt(i32, flags, false),
                    LLVMValueRef.CreateConstInt(i32, 0, false), nullPointer,
                    LLVMValueRef.CreateConstInt(i32, 0, false), nullPointer,
                    LLVMValueRef.CreateConstInt(i32, 0, false), nullPointer,
                }, false);
                sheetGlobals.Add(canonical, global);
                session.RegisterTypeSheet(canonical, global);
            }
            foreach (var plan in layout.Plans)
            {
                // interface 空壳：只有地址身份（iMap 键）有意义，内容全零
                if (plan.Kind == TypeLayoutKind.Interface)
                {
                    var shell = sheetGlobals[plan.Symbol.Canonical];
                    shell.Initializer = context.GetConstStruct(new[]
                    {
                        nullPointer, nullPointer,
                        LLVMValueRef.CreateConstInt(i32, 0, false),
                        LLVMValueRef.CreateConstInt(i32, plan.TypeFlags, false),
                        LLVMValueRef.CreateConstInt(i32, 0, false), nullPointer,
                        LLVMValueRef.CreateConstInt(i32, 0, false), nullPointer,
                        LLVMValueRef.CreateConstInt(i32, 0, false), nullPointer,
                    }, false);
                    continue;
                }
                // vtable 全局（fnptr 数组；未进 MIR 的方法为 null）
                var vTable = nullPointer;
                if (plan.VTableSlots.Count > 0)
                {
                    var entries = new LLVMValueRef[plan.VTableSlots.Count];
                    for (var i = 0; i < entries.Length; i++)
                    {
                        entries[i] = session.TryGetFunction(plan.VTableSlots[i], out var emitted)
                            ? emitted.Value
                            : nullPointer;
                    }
                    vTable = AddConstantGlobal(module,
                        LLVMTypeRef.CreateArray(pointer, (uint)entries.Length),
                        "typesheet.vtable." + plan.Symbol.Canonical,
                        LLVMValueRef.CreateConstArray(pointer, entries));
                }

                // iMap 全局（{iface TypeSheet*, u32 base offset} 对数组）
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
                        "typesheet.imap." + plan.Symbol.Canonical,
                        LLVMValueRef.CreateConstArray(pairType, entries));
                }

                // refMap 全局（u16 跳数数组）
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
                        "typesheet.refmap." + plan.Symbol.Canonical,
                        LLVMValueRef.CreateConstArray(i16, entries));
                }

                var baseSheet = plan.BasePlan != null
                    ? sheetGlobals[plan.BasePlan.Symbol.Canonical]
                    : nullPointer;
                var sheet = sheetGlobals[plan.Symbol.Canonical];
                sheet.Initializer = context.GetConstStruct(new[]
                {
                    nullPointer,   // typeInfoId（MW8 前恒 null 占位）
                    baseSheet,
                    LLVMValueRef.CreateConstInt(i32, (uint)plan.Size, false),
                    LLVMValueRef.CreateConstInt(i32, plan.TypeFlags, false),
                    LLVMValueRef.CreateConstInt(i32, (uint)plan.VTableSlots.Count, false),
                    vTable,
                    LLVMValueRef.CreateConstInt(i32, (uint)plan.IMap.Count, false),
                    iMap,
                    LLVMValueRef.CreateConstInt(i32, (uint)plan.RefMap.Length, false),
                    refMap,
                }, false);
            }
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
