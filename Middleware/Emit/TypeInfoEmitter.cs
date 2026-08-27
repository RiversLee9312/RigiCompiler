using System.Collections.Generic;
using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// TypeInfo 物化：每 TypeSheet 一份全局
    /// {name, sheet, wrappers, wrapperCount, ifaceClosure, ifaceClosureCount}。
    /// canonical 转义同 sheet；wrappers 来自声明 BilWrappedModifier；
    /// ifaceClosure 来自布局计划的传递 implements 闭包。
    /// </summary>
    internal static class TypeInfoEmitter
    {
        // TypeInfo 结构类型（EmitOne 物化与 ExceptionEmitter 运行期读
        // name 槽互指）：{name, sheet, wrappers, wrapperCount, ifaceClosure,
        // ifaceClosureCount}
        internal static LLVMTypeRef InfoStructType(LLVMContextRef context)
        {
            var pointer = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
            return context.GetStructType(new[]
            {
                StringAbi.ValueType(context), pointer, pointer, LLVMTypeRef.Int32,
                pointer, LLVMTypeRef.Int32,
            }, false);
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
                EmitOne(session, canonical, canonical, type: null,
                    System.Array.Empty<string>());
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
            }, false);
            session.RegisterTypeInfo(key, global);
            if (key != name && !session.TryGetTypeInfo(name, out _))
            {
                session.RegisterTypeInfo(name, global);
            }
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
