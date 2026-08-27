using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// LLVM 模块构建（MIDDLEWARE_ARCHITECTURE §3 MW6 层，MW1 最小落地）：
    /// MIR + 布局计划（TypeLayout）+ 实现绑定（ImplBinder）→ LLVM 模块。
    /// 具名局部落 alloca 槽，SSA 提升交 mem2reg；指令选择是 ImplBinding
    /// 到 LLVM builder 调用的机械映射。运行时面（rigi_rt）声明在此按需
    /// 登记，定义由 bitcode 合并（LlvmBitcode）带入。
    /// 本类是唯一公开入口与发射骨架（声明登记 + 逐块驱动）；各发射分面
    /// 归内部静态类：ResourceEmitter（资源物化）、ScalarEmitter（标量指令
    /// 选择）、CallEmitter（调用与运行时面）、TerminatorEmitter（块终结符），
    /// 共享状态与求值辅助全部经 Session 传递。
    /// </summary>
    public static class ModuleBuilder
    {
        // entrypoint fn 的对外 C 符号（rigi_rt 的 main 调用它）
        public const string EntrySymbol = "rigi_entry";

        public static LLVMModuleRef Build(MwContext context, MirModule mir)
        {
            var module = LLVMModuleRef.CreateWithName(ModuleNameOf(context));
            try
            {
                module.Target = LlvmHost.HostTriple;
                new Session(module, mir, context.Layout, context.Symbols, context.Module).EmitAll();
                return module;
            }
            catch
            {
                module.Dispose();
                throw;
            }
        }

        // 模块名：Metadata 的 module 键（BIL §4.1，字面量原文含引号需去除），
        // 缺省回退 "rigi.module"
        internal static string ModuleNameOf(MwContext context)
        {
            foreach (var entry in context.Module.Metadata)
            {
                if (entry.Key == "module" && entry.Type == BilScalarType.String)
                {
                    return entry.LiteralText.Trim('"');
                }
            }
            return "rigi.module";
        }

        // 发射会话：一次模块构建的共享状态，各分面发射器的唯一上下文
        internal sealed class Session
        {
            private readonly Dictionary<string, EmittedFunction> _functions = new(System.StringComparer.Ordinal);
            private readonly Dictionary<string, (LLVMValueRef Fn, LLVMTypeRef Type)> _faces = new(System.StringComparer.Ordinal);

            internal Session(LLVMModuleRef module, MirModule mir,
                Layout.LayoutPlanTable? layout, Symbols.MwSymbolTable symbols,
                BilModule? bilModule = null)
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
            // MW4 布局计划表（LayoutStage 产物；为 null 时跳过 TypeSheet 发射）
            internal Layout.LayoutPlanTable? Layout { get; }
            // MW1 驻留符号表（静态字段槽发射的枚举源）
            internal Symbols.MwSymbolTable Symbols { get; }
            // BIL fn 定义（BindIndirectCall 读取泛型 $$call 的 hidden 前缀）
            internal System.Collections.Generic.IReadOnlyList<BilFunction>? BilFunctions { get; }

            // 标量运行时检查策略（MW9b-G 抛真异常实现：除零 →
            // DividedByZeroException 沿 MIR 异常边；i64 MIN/-1 溢出保留
            // abort 面）
            internal IScalarCheckPolicy Checks { get; } = new ThrowScalarCheckPolicy();

            // 当前发射中的函数（EmitBody 逐函数置位；检查策略的 guard 块
            // 追加需要宿主函数）
            internal LLVMValueRef CurrentFunction { get; private set; }

            internal void SetCurrentFunction(LLVMValueRef fn) => CurrentFunction = fn;

            // 当前发射中函数的 MIR 块 → LLVM 块映射（EmitBody 置位/清位；
            // MW9a 调用后 pending 检查的异常边目标查询用——继续块为内联
            // 合成块不入本映射，MIR 块 1:1 映射不受影响）
            internal IReadOnlyDictionary<string, LLVMBasicBlockRef>? CurrentBlocks
            { get; private set; }

            internal void SetCurrentBlocks(IReadOnlyDictionary<string, LLVMBasicBlockRef>? blocks) =>
                CurrentBlocks = blocks;

            internal sealed record EmittedFunction(LLVMValueRef Value, LLVMTypeRef Type, MirFunction Mir);

            // ===== 函数值映射（DeclareFunction 登记，CallEmitter 直接调用查） =====

            internal EmittedFunction FunctionOf(string canonical) => _functions[canonical];

            internal bool TryGetFunction(string canonical, out EmittedFunction emitted) =>
                _functions.TryGetValue(canonical, out emitted!);

            // ===== 合成 fn（init 分发器 / ctor thunk；vtable 槽 0 填 ptr） =====

            private readonly Dictionary<string, (LLVMValueRef Fn, LLVMTypeRef Type)> _synthetics =
                new(System.StringComparer.Ordinal);
            private readonly Dictionary<string, LLVMValueRef> _tokens =
                new(System.StringComparer.Ordinal);

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

            // ===== TypeSheet 全局注册表（TypeSheetEmitter 登记；new 指令与
            // interface 派发的 iface sheet 引用查） =====

            private readonly Dictionary<string, LLVMValueRef> _typeSheets = new(System.StringComparer.Ordinal);

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
                var builtin = TypeLayout.BuiltinSheetCanonical(MirType.Of(canonical));
                if (_typeSheets.TryGetValue(builtin, out global))
                {
                    return global;
                }
                throw new CompilerInternalException($"TypeSheet 缺失: {canonical}");
            }

            internal bool TryGetTypeSheet(string canonical, out LLVMValueRef global) =>
                _typeSheets.TryGetValue(canonical, out global)
                || _typeSheets.TryGetValue(MwTypeKey.Normalize(canonical), out global);

            private readonly Dictionary<string, LLVMValueRef> _typeInfos = new(System.StringComparer.Ordinal);

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
                throw new CompilerInternalException($"TypeInfo 缺失: {canonical}");
            }

            internal bool TryGetTypeInfo(string canonical, out LLVMValueRef global) =>
                _typeInfos.TryGetValue(canonical, out global)
                || _typeInfos.TryGetValue(MwTypeKey.Normalize(canonical), out global);

            // ===== 静态字段槽注册表（StaticFieldEmitter 登记/读写查） =====

            private readonly Dictionary<string, LLVMValueRef> _staticFields = new(System.StringComparer.Ordinal);
            private readonly List<(LLVMValueRef Global, MirType Type)> _staticSlots = new();

            internal void RegisterStaticField(string canonical, LLVMValueRef global, MirType type)
            {
                _staticFields.Add(canonical, global);
                _staticSlots.Add((global, type));
            }

            internal LLVMValueRef StaticFieldFor(string canonical) => _staticFields[canonical];

            // ===== 面声明缓存（CallEmitter 的运行时面/native 面登记查重） =====

            internal bool TryGetFace(string symbol, out (LLVMValueRef Fn, LLVMTypeRef Type) face) =>
                _faces.TryGetValue(symbol, out face);

            internal void AddFace(string symbol, LLVMValueRef fn, LLVMTypeRef type) =>
                _faces.Add(symbol, (fn, type));

            // MW9b-G：守卫抛出消息的静态类型名字符串常量（按文本查重，
            // 避免同名全局冲突）
            private readonly Dictionary<string, LLVMValueRef> _textConstants =
                new(System.StringComparer.Ordinal);

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

            // ===== 值类型存储分类（MW4 批 3） =====

            // 本地 struct/enum → 内联 alloca（[size x i8]，对齐 = 计划
            // 对齐，不装箱不上堆）；其余 → TypeLayout.MapType
            internal bool IsInlineValueType(MirType type,
                out Layout.TypeLayoutPlan plan)
            {
                plan = null!;
                // .typeid 合成 Struct 计划仅供 TypeSheet；LLVM 存储仍为 ptr
                if (TypeLayout.IsTypeId(type))
                {
                    return false;
                }
                if (Layout?.Find(type.Canonical) is { } found
                    && found.Kind is RigiCompiler.Middleware.Layout.TypeLayoutKind.Struct
                        or RigiCompiler.Middleware.Layout.TypeLayoutKind.Enum)
                {
                    plan = found;
                    return true;
                }
                return false;
            }

            internal static LLVMTypeRef BytePointer() =>
                LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

            // memcpy/memset 内部函数（值语义深拷贝 / VM ZeroOf 物化用）
            internal void EmitMemCopy(LLVMBuilderRef builder, LLVMValueRef dest,
                LLVMValueRef src, int size)
            {
                var (fn, fnType) = DeclareIntrinsic("llvm.memcpy.p0.p0.i64", LLVMTypeRef.Void,
                    new[] { BytePointer(), BytePointer(), LLVMTypeRef.Int64, LLVMTypeRef.Int1 });
                builder.BuildCall2(fnType, fn, new[]
                {
                    dest, src,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)size, false),
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

            // ===== 跨分面复用的求值辅助 =====

            // 具名局部槽 → 当前值装载（操作数求值的唯一形态：$name）
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

            // 值落临时 alloca 槽取指针（String 的 C 边界传参形态）
            internal LLVMValueRef StoreToTemp(LLVMBuilderRef builder, LLVMValueRef value)
            {
                var slot = builder.BuildAlloca(value.TypeOf, "tmp");
                builder.BuildStore(value, slot);
                return slot;
            }

            // 非标量、非 String、非 typeid 的引用类局部 = 胖引用槽
            private static bool IsFatReferenceLocal(MirType type)
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

            // 进程退出前释放静态托管槽（shim.c atexit 调用）
            private void EmitGlobalsCleanup(LLVMBuilderRef builder)
            {
                var fnType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void,
                    System.Array.Empty<LLVMTypeRef>(), false);
                var fn = Module.AddFunction("rigi_globals_cleanup", fnType);
                builder.PositionAtEnd(fn.AppendBasicBlock("entry"));
                foreach (var (global, fieldType) in _staticSlots)
                {
                    if (fieldType.Key == "String")
                    {
                        var value = builder.BuildLoad2(StringAbi.ValueType(Context), global,
                            "cleanup.str");
                        var data = builder.BuildExtractValue(value, 0, "cleanup.str.data");
                        var (rel, relType) = CallEmitter.DeclareArcFace(this,
                            RuntimeFaces.StringRelease);
                        builder.BuildCall2(relType, rel, new[] { data }, "");
                    }
                    else if (IsInlineValueType(fieldType, out var plan)
                        && (plan.TypeFlags & TypeLayoutPlan.FlagRich) != 0
                        && TryGetTypeSheet(fieldType.Canonical, out var sheet))
                    {
                        var (rel, relType) = CallEmitter.DeclareArcFace(this,
                            RuntimeFaces.ValueRelease);
                        builder.BuildCall2(relType, rel, new[] { global, sheet }, "");
                    }
                    else if (IsFatReferenceLocal(fieldType))
                    {
                        var fat = builder.BuildLoad2(TypeLayout.FatReferenceType(Context),
                            global, "cleanup.ref");
                        var typeId = builder.BuildExtractValue(fat, 0, "cleanup.tid");
                        var payload = builder.BuildExtractValue(fat, 1, "cleanup.pl");
                        var (rel, relType) = CallEmitter.DeclareArcFace(this,
                            RuntimeFaces.RefRelease);
                        builder.BuildCall2(relType, rel, new[] { typeId, payload }, "");
                    }
                }
                builder.BuildRetVoid();
            }

            // ===== 发射骨架：声明登记 + 逐块驱动 =====

            internal void EmitAll()
            {
                // 先声明全部 fn（内部调用可前向引用）；TypeSheet 全局依赖
                // fn 声明值；静态槽与 fn 体随后；rigi_entry 合成 stub 收尾
                foreach (var fn in Mir.Functions)
                {
                    DeclareFunction(fn);
                }
                DynamicNewEmitter.DeclareAll(this);
                if (Layout != null)
                {
                    TypeSheetEmitter.EmitAll(this, Layout);
                }
                StaticFieldEmitter.EmitAll(this);
                var builder = Context.CreateBuilder();
                EmitGlobalsCleanup(builder);
                EmittedFunction? entrypoint = null;
                foreach (var fn in Mir.Functions)
                {
                    var emitted = _functions[fn.Symbol.Canonical];
                    if (fn.IsEntrypoint)
                    {
                        entrypoint = emitted;
                    }
                    EmitBody(builder, emitted);
                }
                DynamicNewEmitter.EmitAll(this, builder);
                if (entrypoint != null)
                {
                    EmitEntryStub(builder, entrypoint);
                }
            }

            private void DeclareFunction(MirFunction fn)
            {
                LLVMTypeRef type;
                string name;
                if (fn.IsEntrypoint)
                {
                    // 入口约束（MW1 定稿保持）：无参数、返回 .i32/.void；
                    // fn 以 canonical 名发射，rigi_entry 为合成 stub
                    if (fn.Parameters.Count != 0)
                    {
                        throw new MwNotSupportedException($"入口函数不得有参数: {fn.Symbol.Canonical}");
                    }
                    if (!fn.ReturnType.IsVoid && fn.ReturnType.Key != "i32")
                    {
                        throw new MwNotSupportedException(
                            $"MW1 入口函数返回类型仅支持 .i32/.void: {fn.Symbol.Canonical}");
                    }
                }
                {
                    // 值类型返回 → 隐藏 out 首参（调用方供槽，callee
                    // memcpy 结果，ret void；内部 ABI，C 边界不涉及）
                    var hasOut = IsInlineValueType(fn.ReturnType, out _);
                    var llvmParams = new System.Collections.Generic.List<LLVMTypeRef>();
                    if (hasOut)
                    {
                        llvmParams.Add(Session.BytePointer());
                    }
                    for (var i = 0; i < fn.Parameters.Count; i++)
                    {
                        if (GenericAbi.IsClassLevelTypeId(fn.Symbol, fn.Parameters[i].Name))
                        {
                            continue;
                        }
                        // 值类型参数（含值类型宿主的 .this）→ 传指针
                        llvmParams.Add(IsInlineValueType(fn.Parameters[i].Type, out _)
                            ? Session.BytePointer()
                            : TypeLayout.MapType(Context, fn.Parameters[i].Type));
                    }
                    type = LLVMTypeRef.CreateFunction(
                        hasOut ? LLVMTypeRef.Void : TypeLayout.MapType(Context, fn.ReturnType),
                        llvmParams.ToArray(), false);
                    name = fn.Symbol.Canonical;
                }
                var value = Module.AddFunction(name, type);
                // 模块内符号（含入口 main 的 canonical fn）不出 .o（合并
                // rigi_rt 后由优化管线内联/裁减）；rigi_entry stub 保持外部
                value.Linkage = LLVMLinkage.LLVMInternalLinkage;
                _functions.Add(fn.Symbol.Canonical, new EmittedFunction(value, type, fn));
            }

            // rigi_entry 合成 stub（MW4 批 4 定稿）：先调 ..globals.init
            //（静态字段初值，存在时）再调用户 main、返回其 i32（.void 包装 0）。
            // MW9a 第 C 棒：两调用后各插 pending 检查，非空汇同一 reporter
            // 块（未捕获："{类型全名}: {message}" 打印 stderr，exit 1）
            private void EmitEntryStub(LLVMBuilderRef builder, EmittedFunction entrypoint)
            {
                var stubType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32,
                    System.Array.Empty<LLVMTypeRef>(), false);
                var stub = Module.AddFunction(EntrySymbol, stubType);
                builder.PositionAtEnd(stub.AppendBasicBlock("entry"));
                SetCurrentFunction(stub);
                var reporterTarget = ResolveUncaughtReporterTarget(out var exceptionSheet);
                LLVMBasicBlockRef? reporter = null;
                if (TryGetFunction("$..globals.init()@.void", out var globalsInit))
                {
                    builder.BuildCall2(globalsInit.Type, globalsInit.Value,
                        System.Array.Empty<LLVMValueRef>(), "");
                    reporter = EmitEntryPendingCheck(builder, stub, reporterTarget != null, reporter);
                }
                if (entrypoint.Mir.ReturnType.IsVoid)
                {
                    builder.BuildCall2(entrypoint.Type, entrypoint.Value,
                        System.Array.Empty<LLVMValueRef>(), "");
                    reporter = EmitEntryPendingCheck(builder, stub, reporterTarget != null, reporter);
                    builder.BuildRet(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false));
                }
                else
                {
                    var result = builder.BuildCall2(entrypoint.Type, entrypoint.Value,
                        System.Array.Empty<LLVMValueRef>(), "main.result");
                    reporter = EmitEntryPendingCheck(builder, stub, reporterTarget != null, reporter);
                    builder.BuildRet(result);
                }
                if (reporter != null)
                {
                    builder.PositionAtEnd(reporter.Value);
                    EmitUncaughtReporterBody(builder, reporterTarget!, exceptionSheet);
                }
            }

            // 入口调用后的 pending 检查：reporter 前提缺失（模块内无异常
            // 类型可达 → 无 MirThrow → pending 恒空）时退回旧序不插检查
            private LLVMBasicBlockRef? EmitEntryPendingCheck(LLVMBuilderRef builder,
                LLVMValueRef stub, bool reporterAvailable, LLVMBasicBlockRef? reporter)
            {
                if (!reporterAvailable)
                {
                    return reporter;
                }
                reporter ??= stub.AppendBasicBlock("entry.uncaught");
                var (pendFn, pendType) = CallEmitter.DeclareHelperFace(this,
                    RuntimeFaces.ExcPending, BytePointer(), System.Array.Empty<LLVMTypeRef>());
                var pending = builder.BuildCall2(pendType, pendFn,
                    System.Array.Empty<LLVMValueRef>(), "entry.pending");
                var hasPending = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, pending,
                    LLVMValueRef.CreateConstNull(BytePointer()), "entry.has");
                var cont = stub.AppendBasicBlock("entry.cont");
                builder.BuildCondBr(hasPending, reporter.Value, cont);
                builder.PositionAtEnd(cont);
                return reporter;
            }

            // 未捕获 reporter 前提解析：core::Exception 类型 / getMessage
            // 虚成员 / 布局计划 / TypeSheet 四件套齐备才可虚派发；任一缺失
            // = 模块内无异常类型可达（pending 恒空），返回 null
            private MwMemberSymbol? ResolveUncaughtReporterTarget(out LLVMValueRef exceptionSheet)
            {
                exceptionSheet = default;
                var exceptionType = Symbols.FindTypeByRef("core::Exception");
                if (exceptionType == null)
                {
                    return null;
                }
                MwMemberSymbol? getMessage = null;
                foreach (var member in exceptionType.Members)
                {
                    if (member.Declaration.Kind == BilMemberKind.Method
                        && member.SignatureKey == "getMessage()")
                    {
                        getMessage = member;
                        break;
                    }
                }
                if (getMessage == null
                    || Layout?.Find(GenericAbi.PlanKey(exceptionType)) == null
                    || !TryGetTypeSheet(exceptionType.Canonical, out var sheet))
                {
                    return null;
                }
                exceptionSheet = sheet;
                return getMessage;
            }

            // reporter 体：take 移入合成槽 → 虚派发 getMessage()（静态
            // 目标 core::Exception.getMessage，实际 override 由对象头
            // vtable 解析；返回 String 值）→ rigi_type_name_of 取实际
            // 类型全名 → rigi_print_err 逐段打印 → 释放异常胖引用与
            // message 字符串 → rigi_exc_halt（exit 1，noreturn）
            private void EmitUncaughtReporterBody(LLVMBuilderRef builder,
                MwMemberSymbol getMessage, LLVMValueRef exceptionSheet)
            {
                var (takeFn, takeType) = CallEmitter.DeclareHelperFace(this,
                    RuntimeFaces.ExcTake, BytePointer(), System.Array.Empty<LLVMTypeRef>());
                var obj = builder.BuildCall2(takeType, takeFn,
                    System.Array.Empty<LLVMValueRef>(), "uncaught.take");
                var slot = builder.BuildAlloca(BytePointer(), "uncaught.slot");
                builder.BuildStore(obj, slot);
                var held = builder.BuildLoad2(BytePointer(), slot, "uncaught.obj");
                // 接收者胖引用：typeid 半按槽静态类型（core::Exception
                // sheet，tag2）；虚派发只读 payload 与对象头，实际实现
                // 由运行期 vtable 解析
                var fat = CallEmitter.BuildFatReference(this, builder, exceptionSheet, held);
                var virtualSlot = CallEmitter.VirtualSlotOf(this, getMessage);
                var entry = CallEmitter.EmitVTableEntry(this, builder, "rigi_vtable_entry", new[]
                {
                    held, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)virtualSlot, false),
                });
                var messageType = LLVMTypeRef.CreateFunction(StringAbi.ValueType(Context),
                    new[] { TypeLayout.FatReferenceType(Context) }, false);
                var message = builder.BuildCall2(messageType, entry, new[] { fat }, "uncaught.msg");
                // 实际类型全名（对象头 sheet → TypeInfo.name，借用拷出不释放）
                var nameSlot = builder.BuildAlloca(StringAbi.ValueType(Context), "uncaught.nameslot");
                var (nameFn, nameFnType) = CallEmitter.DeclareHelperFace(this,
                    RuntimeFaces.TypeNameOf, LLVMTypeRef.Void,
                    new[] { BytePointer(), StringAbi.PointerType(Context) });
                builder.BuildCall2(nameFnType, nameFn, new[] { held, nameSlot }, "");
                var name = builder.BuildLoad2(StringAbi.ValueType(Context), nameSlot,
                    "uncaught.name");
                EmitPrintErr(builder, name);
                EmitPrintErr(builder, StringAbi.BuildConstant(Module, ": ", "uncaught.colon"));
                EmitPrintErr(builder, message);
                EmitPrintErr(builder, StringAbi.BuildConstant(Module, "\n", "uncaught.newline"));
                // message 是 getMessage 的 owned 返回值，打印后归还
                var (strRel, strRelType) = CallEmitter.DeclareArcFace(this,
                    RuntimeFaces.StringRelease);
                builder.BuildCall2(strRelType, strRel,
                    new[] { builder.BuildExtractValue(message, 0, "uncaught.msgdata") }, "");
                ArcEmitter.EmitReleaseFatValue(this, builder, fat);
                var (haltFn, haltType) = CallEmitter.DeclareVoidFace(this, RuntimeFaces.ExcHalt);
                builder.BuildCall2(haltType, haltFn, System.Array.Empty<LLVMValueRef>(), "");
                builder.BuildUnreachable();
            }

            // rigi_print_err(rigi_string*)：{i8*,i64} 值经临时 alloca 中转
            // 传指针（String ABI 的 C 边界形态，native print 调用点同法）
            private void EmitPrintErr(LLVMBuilderRef builder, LLVMValueRef text)
            {
                var (fn, fnType) = CallEmitter.DeclareHelperFace(this, RuntimeFaces.PrintErr,
                    LLVMTypeRef.Void, new[] { StringAbi.PointerType(Context) });
                builder.BuildCall2(fnType, fn, new[] { StoreToTemp(builder, text) }, "");
            }

            private void EmitBody(LLVMBuilderRef builder, EmittedFunction emitted)
            {
                var fn = emitted.Mir;
                CurrentFunction = emitted.Value;
                // 先建全部基本块（终结符按 id 引用，可前向引用），再逐块发射
                var blockRefs = new Dictionary<string, LLVMBasicBlockRef>(System.StringComparer.Ordinal);
                foreach (var block in fn.Blocks)
                {
                    blockRefs.Add(block.Id, emitted.Value.AppendBasicBlock(block.Id));
                }
                SetCurrentBlocks(blockRefs);

                // 具名局部 → alloca 槽（entry 块开头，mem2reg 友好；含参数落槽）。
                // 值类型局部 = 计划尺寸的内联槽；值类型宿主的 .this 不开槽——
                // 槽即传入指针别名（SYNTAX §10 this 别名：字段写原地生效）
                builder.PositionAtEnd(blockRefs[fn.Blocks[0].Id]);
                var slots = new Dictionary<string, (LLVMValueRef Slot, MirLocal Local)>(System.StringComparer.Ordinal);
                var valueThis = fn.Symbol.Owner is { Declaration.Kind:
                    Bil.BilTypeKind.Struct or Bil.BilTypeKind.EnumStruct };
                foreach (var local in fn.Locals)
                {
                    if (valueThis && local.Name == ".this")
                    {
                        continue;
                    }
                    LLVMValueRef slot;
                    if (IsInlineValueType(local.Type, out var localPlan))
                    {
                        slot = builder.BuildAlloca(
                            LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)localPlan.Size), local.Name);
                        slot.Alignment = (uint)localPlan.Alignment;
                        EmitMemSetZero(builder, slot, localPlan.Size);
                    }
                    else
                    {
                        var llvmType = TypeLayout.MapType(Context, local.Type);
                        slot = builder.BuildAlloca(llvmType, local.Name);
                        if (local.Type.Key == "String" || IsFatReferenceLocal(local.Type))
                        {
                            builder.BuildStore(LLVMValueRef.CreateConstNull(llvmType), slot);
                        }
                    }
                    slots.Add(local.Name, (slot, local));
                }
                var llvmIndex = IsInlineValueType(fn.ReturnType, out _) ? 1 : 0;
                for (var i = 0; i < fn.Parameters.Count; i++)
                {
                    var parameter = fn.Parameters[i];
                    if (GenericAbi.IsClassLevelTypeId(fn.Symbol, parameter.Name))
                    {
                        continue;
                    }
                    var llvmParam = emitted.Value.GetParam((uint)llvmIndex++);
                    if (valueThis && parameter.Name == ".this")
                    {
                        slots.Add(parameter.Name, (llvmParam, parameter));
                    }
                    else if (IsInlineValueType(parameter.Type, out _))
                    {
                        // 值类型参数：InitRichValue 对传入值 +1（非 rich 退化为 memcpy）
                        ArcEmitter.EmitInitRichValue(this, builder,
                            slots[parameter.Name].Slot, llvmParam, parameter.Type);
                    }
                    else
                    {
                        builder.BuildStore(llvmParam, slots[parameter.Name].Slot);
                    }
                }
                EmitClassTypeIdPrologue(builder, emitted, slots);

                foreach (var block in fn.Blocks)
                {
                    builder.PositionAtEnd(blockRefs[block.Id]);
                    foreach (var inst in block.Instructions)
                    {
                        EmitInst(builder, slots, inst);
                    }
                    TerminatorEmitter.Emit(this, builder, slots, blockRefs, emitted.Value, fn, block.Terminator);
                }
                SetCurrentBlocks(null);
            }

            // 局部拷贝：值类型 = memcpy 深拷贝（VM Copy 同口径）；
            // 标量/胖引用 = 装载转存
            private void EmitCopyLocal(LLVMBuilderRef builder,
                Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirCopyLocal copy)
            {
                if (copy.Source is MirLocalOperand source
                    && IsInlineValueType(slots[source.Name].Local.Type, out var plan))
                {
                    EmitMemCopy(builder, slots[copy.Target].Slot, slots[source.Name].Slot, plan.Size);
                    return;
                }
                builder.BuildStore(LoadLocal(builder, slots, copy.Source), slots[copy.Target].Slot);
            }

            // 类级 .generic.X 初值：从 .this 隐藏 typeid 字段装入（不进调用约定）
            private void EmitClassTypeIdPrologue(LLVMBuilderRef builder, EmittedFunction emitted,
                Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots)
            {
                var owner = emitted.Mir.Symbol.Owner;
                if (owner == null || owner.Declaration.GenericParameters.Count == 0
                    || !slots.ContainsKey(".this")
                    || Layout?.Find(GenericAbi.PlanKey(owner)) is not { } plan)
                {
                    return;
                }
                var fat = LoadLocal(builder, slots, new MirLocalOperand(".this"));
                var obj = builder.BuildIntToPtr(
                    builder.BuildExtractValue(fat, 1, "this.payload"),
                    LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "this.obj");
                foreach (var (paramName, offset) in plan.HiddenTypeIdSlots)
                {
                    var localName = ".generic." + paramName;
                    if (!slots.ContainsKey(localName))
                    {
                        continue;
                    }
                    var gep = builder.BuildGEP2(LLVMTypeRef.Int8, obj,
                        new[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)offset, false) },
                        "tid.gep");
                    var bits = builder.BuildLoad2(LLVMTypeRef.Int64, gep, "tid.bits");
                    var ptr = builder.BuildIntToPtr(bits,
                        LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "tid.ptr");
                    builder.BuildStore(ptr, slots[localName].Slot);
                }
            }

            // 顺序指令分派：资源物化归 ResourceEmitter，标量运算归
            // ScalarEmitter，调用归 CallEmitter；局部拷贝是槽间搬运，留骨架
            private void EmitInst(LLVMBuilderRef builder,
                Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirInst inst)
            {
                switch (inst)
                {
                    case MirLoadResource load:
                        ResourceEmitter.EmitLoad(this, builder, slots, load);
                        break;
                    case MirCopyLocal copy:
                        EmitCopyLocal(builder, slots, copy);
                        break;
                    case MirBinaryIntrinsic binary:
                        builder.BuildStore(ScalarEmitter.EmitBinary(this, builder, slots, binary),
                            slots[binary.Target].Slot);
                        break;
                    case MirUnaryIntrinsic unary:
                        builder.BuildStore(ScalarEmitter.EmitUnary(this, builder, slots, unary),
                            slots[unary.Target].Slot);
                        break;
                    case MirCall call:
                        CallEmitter.EmitCall(this, builder, slots, call);
                        break;
                    case MirInvokeIndirect invokeIndirect:
                        CallEmitter.EmitIndirectInvoke(this, builder, slots, invokeIndirect);
                        break;
                    case MirSuperCall superCall:
                        CallEmitter.EmitSuperCall(this, builder, slots, superCall);
                        break;
                    case MirNewIndirect newIndirect:
                        DynamicNewEmitter.EmitCall(this, builder, slots, newIndirect);
                        break;
                    case MirNewObject newObject:
                        CallEmitter.EmitNew(this, builder, slots, newObject);
                        break;
                    case MirNewValue newValue:
                        CallEmitter.EmitNewValue(this, builder, slots, newValue);
                        break;
                    case MirNewCase newCase:
                        EnumEmitter.EmitNewCase(this, builder, slots, newCase);
                        break;
                    case MirIsCase isCase:
                        EnumEmitter.EmitIsCase(this, builder, slots, isCase);
                        break;
                    case MirTypeCheck typeCheck:
                        TypeCheckEmitter.Emit(this, builder, slots, typeCheck);
                        break;
                    case MirGetField getField:
                        FieldEmitter.EmitGet(this, builder, slots, getField);
                        break;
                    case MirSetField setField:
                        FieldEmitter.EmitSet(this, builder, slots, setField);
                        break;
                    case MirGetStatic getStatic:
                        StaticFieldEmitter.EmitGet(this, builder, slots, getStatic);
                        break;
                    case MirSetStatic setStatic:
                        StaticFieldEmitter.EmitSet(this, builder, slots, setStatic);
                        break;
                    case MirGetArray getArray:
                        ArrayEmitter.EmitGet(this, builder, slots, getArray);
                        break;
                    case MirSetArray setArray:
                        ArrayEmitter.EmitSet(this, builder, slots, setArray);
                        break;
                    case MirNewArray newArray:
                        ArrayEmitter.EmitNew(this, builder, slots, newArray);
                        break;
                    case MirGetTypeId getTypeId:
                        ArrayEmitter.EmitGetTypeId(this, builder, slots, getTypeId);
                        break;
                    case MirGetTypeIdVar getTypeIdVar:
                        ArrayEmitter.EmitGetTypeIdVar(this, builder, slots, getTypeIdVar);
                        break;
                    case MirWrapNullable wrap:
                        ArrayEmitter.EmitWrap(this, builder, slots, wrap);
                        break;
                    case MirUnwrapNullable unwrap:
                        ArrayEmitter.EmitUnwrap(this, builder, slots, unwrap);
                        break;
                    case MirBoxAny box:
                        BoxEmitter.EmitBox(this, builder, slots, box);
                        break;
                    case MirUnboxAny unbox:
                        BoxEmitter.EmitUnbox(this, builder, slots, unbox);
                        break;
                    case MirCast cast:
                        CastEmitter.Emit(this, builder, slots, cast);
                        break;
                    case MirAcquireSlot acquire:
                        ArcEmitter.EmitAcquireSlot(this, builder, slots, acquire.Local);
                        break;
                    case MirReleaseSlot release:
                        ArcEmitter.EmitReleaseSlot(this, builder, slots, release.Local);
                        break;
                    case MirTakePending takePending:
                        ExceptionEmitter.EmitTakePending(this, builder, slots, takePending);
                        break;
                    case MirThrow throwInst:
                        ExceptionEmitter.EmitThrow(this, builder, slots, throwInst);
                        break;
                    default:
                        throw new CompilerInternalException($"未覆盖的 MIR 指令: {inst.GetType().Name}");
                }
            }
        }
    }
}
