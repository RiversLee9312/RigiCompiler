using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Emit
{
    // MIR→LLVM 已发射函数（模块级登记；CallEmitter 直接调用查）
    internal sealed record EmittedFunction(LLVMValueRef Value, LLVMTypeRef Type, MirFunction Mir);

    /// <summary>
    /// MIR→LLVM 模块级环境（组合根的模块组件，对照 P4b EmitEnvironment）：
    /// 一次 ModuleBuilder.Build 存活，登记函数/合成 fn/TypeSheet/静态槽/运行时面。
    /// 不含「当前函数」游标——那属 <see cref="LlvmEmitContext"/>。
    /// </summary>
    internal sealed class LlvmEmitEnvironment
    {
        private readonly Dictionary<string, EmittedFunction> _functions = new(System.StringComparer.Ordinal);
        private readonly Dictionary<string, (LLVMValueRef Fn, LLVMTypeRef Type)> _faces = new(System.StringComparer.Ordinal);
        private readonly Dictionary<string, (LLVMValueRef Fn, LLVMTypeRef Type)> _synthetics =
            new(System.StringComparer.Ordinal);
        private readonly Dictionary<string, LLVMValueRef> _tokens = new(System.StringComparer.Ordinal);
        private readonly Dictionary<string, LLVMValueRef> _typeSheets = new(System.StringComparer.Ordinal);
        private readonly Dictionary<string, LLVMValueRef> _typeInfos = new(System.StringComparer.Ordinal);
        private readonly Dictionary<string, LLVMValueRef> _staticFields = new(System.StringComparer.Ordinal);
        private readonly List<(LLVMValueRef Global, MirType Type)> _staticSlots = new();
        private readonly Dictionary<string, LLVMValueRef> _textConstants =
            new(System.StringComparer.Ordinal);

        internal LlvmEmitEnvironment(LLVMModuleRef module, MirModule mir,
            LayoutPlanTable? layout, MwSymbolTable symbols, BilModule? bilModule)
        {
            Module = module;
            Context = module.Context;
            Mir = mir;
            Layout = layout;
            Symbols = symbols;
            BilFunctions = bilModule?.Functions;
        }

        internal LLVMModuleRef Module { get; }
        internal LLVMContextRef Context { get; }
        internal MirModule Mir { get; }
        internal LayoutPlanTable? Layout { get; }
        internal MwSymbolTable Symbols { get; }
        internal IReadOnlyList<BilFunction>? BilFunctions { get; }
        internal IReadOnlyList<(LLVMValueRef Global, MirType Type)> StaticSlots => _staticSlots;

        internal EmittedFunction FunctionOf(string canonical) => _functions[canonical];

        internal bool TryGetFunction(string canonical, out EmittedFunction emitted) =>
            _functions.TryGetValue(canonical, out emitted!);

        internal void AddFunction(string canonical, EmittedFunction emitted) =>
            _functions.Add(canonical, emitted);

        internal void RegisterSynthetic(string name, LLVMValueRef fn, LLVMTypeRef type) =>
            _synthetics.Add(name, (fn, type));

        internal bool TryGetSynthetic(string name, out (LLVMValueRef Fn, LLVMTypeRef Type) value) =>
            _synthetics.TryGetValue(name, out value);

        // 无运行期 sheet 的形参令牌（.nullable<...> 等）：模块内驻留
        // canonical 串指针，调用点与分发器对称物化、指针等比较
        internal LLVMValueRef InternCanonicalToken(string canonical)
        {
            if (_tokens.TryGetValue(canonical, out var existing))
            {
                return existing;
            }
            var bytes = System.Text.Encoding.UTF8.GetBytes(canonical);
            var elems = new LLVMValueRef[bytes.Length + 1];
            for (var i = 0; i < bytes.Length; i++)
            {
                elems[i] = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, bytes[i], false);
            }
            elems[bytes.Length] = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0, false);
            var arrType = LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)elems.Length);
            var global = Module.AddGlobal(arrType,
                GenericAbi.EscapeGlobalName("mw.init.token.", canonical));
            global.Linkage = LLVMLinkage.LLVMInternalLinkage;
            global.IsGlobalConstant = true;
            global.Initializer = LLVMValueRef.CreateConstArray(LLVMTypeRef.Int8, elems);
            var ptr = LLVMValueRef.CreateConstInBoundsGEP2(arrType, global, new[]
            {
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false),
            });
            _tokens.Add(canonical, ptr);
            return ptr;
        }

        internal void RegisterTypeSheet(string canonical, LLVMValueRef global) =>
            _typeSheets.Add(canonical, global);

        internal LLVMValueRef TypeSheetFor(string canonical)
        {
            if (_typeSheets.TryGetValue(canonical, out var global))
            {
                return global;
            }
            var normalized = MwTypeKey.Normalize(canonical);
            if (_typeSheets.TryGetValue(normalized, out global))
            {
                return global;
            }
            var mirType = MirType.Of(canonical);
            var builtin = TypeLayout.BuiltinSheetCanonical(mirType);
            if (_typeSheets.TryGetValue(builtin, out global))
            {
                return global;
            }
            // 构造 Array/Span/Nullable\<占位\> 无独立 sheet，回退定义级 builtin
            if (TypeLayout.IsArray(mirType)
                && _typeSheets.TryGetValue(TypeLayout.ArrayTypeCanonical, out global))
            {
                return global;
            }
            if (TypeLayout.IsSpan(mirType)
                && _typeSheets.TryGetValue(TypeLayout.SpanTypeCanonical, out global))
            {
                return global;
            }
            if (TypeLayout.IsSharedSpan(mirType)
                && _typeSheets.TryGetValue(TypeLayout.SharedSpanTypeCanonical, out global))
            {
                return global;
            }
            if (TypeLayout.IsNullable(mirType)
                && _typeSheets.TryGetValue(TypeLayout.NullableTypeCanonical, out global))
            {
                return global;
            }
            // 不得剥实参回退 arity-0：Task<.generic<T>> 剥成 void Task
            // 后 gate 偏移错（void@72 vs Task<T>@88），泛型 async 空句柄。
            if (Symbols.FindTypeByRef(canonical) is { } template)
            {
                var planKey = GenericAbi.PlanKey(template);
                if (_typeSheets.TryGetValue(planKey, out global)
                    || _typeSheets.TryGetValue(template.Canonical, out global))
                {
                    return global;
                }
            }
            throw new CompilerInternalException($"TypeSheet 缺失: {canonical}");
        }

        internal bool TryGetTypeSheet(string canonical, out LLVMValueRef global) =>
            _typeSheets.TryGetValue(canonical, out global)
            || _typeSheets.TryGetValue(MwTypeKey.Normalize(canonical), out global);

        private bool TryDefinitionSheet(string canonical, out LLVMValueRef global)
        {
            global = default;
            var stripped = BilVerificationContext.StripTypeArguments(canonical);
            if (stripped.Length > 0 && stripped != canonical
                && _typeSheets.TryGetValue(stripped, out global))
            {
                return true;
            }
            var declKey = BilVerificationContext.DeclarationKeyOf(canonical);
            if (declKey != canonical && _typeSheets.TryGetValue(declKey, out global))
            {
                return true;
            }
            if (Symbols.FindTypeByRef(stripped.Length > 0 ? stripped : canonical) is { } def)
            {
                var planKey = GenericAbi.PlanKey(def);
                if (_typeSheets.TryGetValue(planKey, out global)
                    || _typeSheets.TryGetValue(def.Canonical, out global))
                {
                    return true;
                }
            }
            return false;
        }

        internal void RegisterTypeInfo(string canonical, LLVMValueRef global) =>
            _typeInfos.Add(canonical, global);

        internal LLVMValueRef TypeInfoFor(string canonical)
        {
            if (_typeInfos.TryGetValue(canonical, out var global))
            {
                return global;
            }
            var normalized = MwTypeKey.Normalize(canonical);
            if (_typeInfos.TryGetValue(normalized, out global))
            {
                return global;
            }
            var mirType = MirType.Of(canonical);
            if (TypeLayout.IsNullable(mirType)
                && _typeInfos.TryGetValue(TypeLayout.NullableTypeCanonical, out global))
            {
                return global;
            }
            if (TypeLayout.IsArray(mirType)
                && _typeInfos.TryGetValue(TypeLayout.ArrayTypeCanonical, out global))
            {
                return global;
            }
            throw new CompilerInternalException($"TypeInfo 缺失: {canonical}");
        }

        internal bool TryGetTypeInfo(string canonical, out LLVMValueRef global) =>
            _typeInfos.TryGetValue(canonical, out global)
            || _typeInfos.TryGetValue(MwTypeKey.Normalize(canonical), out global);

        internal void RegisterStaticField(string canonical, LLVMValueRef global, MirType type)
        {
            _staticFields.Add(canonical, global);
            _staticSlots.Add((global, type));
        }

        internal LLVMValueRef StaticFieldFor(string canonical) => _staticFields[canonical];

        internal bool TryGetFace(string symbol, out (LLVMValueRef Fn, LLVMTypeRef Type) face) =>
            _faces.TryGetValue(symbol, out face);

        internal void AddFace(string symbol, LLVMValueRef fn, LLVMTypeRef type) =>
            _faces.Add(symbol, (fn, type));

        internal LLVMValueRef InternStringConstant(string text)
        {
            if (_textConstants.TryGetValue(text, out var existing))
            {
                return existing;
            }
            var value = StringAbi.BuildConstant(Module, text,
                "exc.txt." + _textConstants.Count);
            _textConstants.Add(text, value);
            return value;
        }

        internal bool TryFindValuePlan(string canonical, out TypeLayoutPlan plan)
        {
            plan = null!;
            if (Layout?.Find(canonical) is { } found)
            {
                plan = found;
                return true;
            }
            if (Symbols.FindTypeByRef(canonical) is { } template
                && Layout?.Find(GenericAbi.PlanKey(template)) is { } viaTemplate)
            {
                plan = viaTemplate;
                return true;
            }
            return false;
        }

        // 本地 struct/enum → 内联 alloca（[size x i8]，对齐 = 计划
        // 对齐，不装箱不上堆）；其余 → TypeLayout.MapType
        internal bool IsInlineValueType(MirType type, out TypeLayoutPlan plan)
        {
            plan = null!;
            if (TypeLayout.IsTypeId(type))
            {
                return false;
            }
            return TryFindValuePlan(type.Canonical, out plan)
                && CallAbi.ReturnsViaOutPointer(plan);
        }

        internal static LLVMTypeRef BytePointer() =>
            LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

        internal void EmitMemCopy(LLVMBuilderRef builder, LLVMValueRef dest,
            LLVMValueRef src, int size)
        {
            EmitMemCopyN(builder, dest, src,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)size, false));
        }

        internal void EmitMemCopyN(LLVMBuilderRef builder, LLVMValueRef dest,
            LLVMValueRef src, LLVMValueRef sizeI64)
        {
            var (fn, fnType) = DeclareIntrinsic("llvm.memcpy.p0.p0.i64", LLVMTypeRef.Void,
                new[] { BytePointer(), BytePointer(), LLVMTypeRef.Int64, LLVMTypeRef.Int1 });
            builder.BuildCall2(fnType, fn, new[]
            {
                dest, src, sizeI64,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0, false),
            }, "");
        }

        internal void EmitMemSetZero(LLVMBuilderRef builder, LLVMValueRef dest, int size)
        {
            var (fn, fnType) = DeclareIntrinsic("llvm.memset.p0.i64", LLVMTypeRef.Void,
                new[] { BytePointer(), LLVMTypeRef.Int8, LLVMTypeRef.Int64, LLVMTypeRef.Int1 });
            builder.BuildCall2(fnType, fn, new[]
            {
                dest,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0, false),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)size, false),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0, false),
            }, "");
        }

        private (LLVMValueRef Fn, LLVMTypeRef Type) DeclareIntrinsic(string symbol,
            LLVMTypeRef returnType, LLVMTypeRef[] paramTypes)
        {
            if (TryGetFace(symbol, out var cached))
            {
                return cached;
            }
            var type = LLVMTypeRef.CreateFunction(returnType, paramTypes, false);
            var fn = Module.AddFunction(symbol, type);
            AddFace(symbol, fn, type);
            return (fn, type);
        }

        internal LLVMValueRef LoadLocal(LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirOperand operand)
        {
            if (operand is not MirLocalOperand local)
            {
                throw new CompilerInternalException($"未覆盖的操作数形态: {operand.GetType().Name}");
            }
            var (slot, localInfo) = slots[local.Name];
            return builder.BuildLoad2(TypeLayout.MapType(Context, localInfo.Type), slot, local.Name);
        }

        internal LLVMValueRef StoreToTemp(LLVMBuilderRef builder, LLVMValueRef value)
        {
            var slot = BuildEntryAlloca(builder, value.TypeOf, "tmp");
            builder.BuildStore(value, slot);
            return slot;
        }

        // 临时槽一律落当前函数 entry 块开头：mem2reg/SROA 只提升 entry 块
        // alloca，循环体内的内联 alloca 是逐迭代栈消耗（LLVM 无 pass 把非
        // entry 块 alloca 提上 entry；escaping 进调用的更是永不消除）——
        // 100k 级循环会烧穿 1MiB 默认栈（MW12c stress 暴露）。槽即临时，
        // 每轮迭代先写后读，跨迭代复用语义安全
        internal static LLVMValueRef BuildEntryAlloca(LLVMBuilderRef builder,
            LLVMTypeRef type, string name)
        {
            var fn = builder.InsertBlock.Parent;
            var entry = fn.FirstBasicBlock;
            var first = entry.FirstInstruction;
            var tmp = LLVMBuilderRef.Create(type.Context);
            try
            {
                if (first.Handle == System.IntPtr.Zero)
                {
                    tmp.PositionAtEnd(entry);
                }
                else
                {
                    tmp.Position(entry, first);
                }
                return tmp.BuildAlloca(type, name);
            }
            finally
            {
                tmp.Dispose();
            }
        }

        // 非标量、非 String、非 typeid 的引用类局部 = 胖引用槽
        internal static bool IsFatReferenceLocal(MirType type)
        {
            if (type.IsVoid || type.Key == "String" || TypeLayout.IsTypeId(type))
            {
                return false;
            }
            return type.Key is not ("bool" or "char"
                or "i8" or "i16" or "i32" or "i64"
                or "u8" or "u16" or "u32" or "u64"
                or "float" or "double");
        }
    }
}
