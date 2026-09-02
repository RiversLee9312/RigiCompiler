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
    /// 本类是该翻译 pass 的瘦驱动（声明登记 + 逐块驱动）。指令翻译经
    /// LlvmEmitDispatchers 唯一 switch 分到 CRTP visitor；终结符归
    /// TerminatorEmitter。共享状态经 Session（Env + Ctx）传递。
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
                new Session(module, mir, context.Layout, context.Symbols, context.Module,
                    context.Singletons).EmitAll();
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

        // 发射会话：组合根（对照 BindContext）。模块级登记在 Env，函数级
        // 游标在 Ctx；各 *Emitter 暂仍收 Session，经转发访问，避免一次改
        // 二十个签名。后续 CRTP visitor 直接拿 (env, ctx)。
        internal sealed class Session
        {
            internal Session(LLVMModuleRef module, MirModule mir,
                Layout.LayoutPlanTable? layout, Symbols.MwSymbolTable symbols,
                BilModule? bilModule = null,
                System.Collections.Generic.IReadOnlyList<Binding.SingletonEntry>? singletons = null)
            {
                Env = new LlvmEmitEnvironment(module, mir, layout, symbols, bilModule);
                Ctx = new LlvmEmitContext();
                Singletons = singletons
                    ?? System.Array.Empty<Binding.SingletonEntry>();
            }

            internal LlvmEmitEnvironment Env { get; }
            internal LlvmEmitContext Ctx { get; }

            // MW10 刀5：singleton 三态/缓存合成槽与急切初始化调用表
            //（SingletonLoweringPass 挂载；空 = 模块无 singleton）
            internal System.Collections.Generic.IReadOnlyList<Binding.SingletonEntry> Singletons
            { get; }

            internal LLVMModuleRef Module => Env.Module;
            internal LLVMContextRef Context => Env.Context;
            internal MirModule Mir => Env.Mir;
            internal Layout.LayoutPlanTable? Layout => Env.Layout;
            internal Symbols.MwSymbolTable Symbols => Env.Symbols;
            internal System.Collections.Generic.IReadOnlyList<BilFunction>? BilFunctions =>
                Env.BilFunctions;

            internal IScalarCheckPolicy Checks { get; } = new ThrowScalarCheckPolicy();

            internal LLVMValueRef CurrentFunction
            {
                get => Ctx.CurrentFunction;
                private set => Ctx.SetCurrentFunction(value);
            }

            internal void SetCurrentFunction(LLVMValueRef fn) => Ctx.SetCurrentFunction(fn);

            internal IReadOnlyDictionary<string, LLVMBasicBlockRef>? CurrentBlocks
            {
                get => Ctx.CurrentBlocks;
                private set => Ctx.SetCurrentBlocks(value);
            }

            internal void SetCurrentBlocks(IReadOnlyDictionary<string, LLVMBasicBlockRef>? blocks) =>
                Ctx.SetCurrentBlocks(blocks);

            internal LLVMBuilderRef Builder => Ctx.Builder;

            internal Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> Slots => Ctx.Slots;

            internal EmittedFunction FunctionOf(string canonical) => Env.FunctionOf(canonical);

            internal bool TryGetFunction(string canonical, out EmittedFunction emitted) =>
                Env.TryGetFunction(canonical, out emitted);

            internal void RegisterSynthetic(string name, LLVMValueRef fn, LLVMTypeRef type) =>
                Env.RegisterSynthetic(name, fn, type);

            internal bool TryGetSynthetic(string name, out (LLVMValueRef Fn, LLVMTypeRef Type) value) =>
                Env.TryGetSynthetic(name, out value);

            internal LLVMValueRef InternCanonicalToken(string canonical) =>
                Env.InternCanonicalToken(canonical);

            internal void RegisterTypeSheet(string canonical, LLVMValueRef global) =>
                Env.RegisterTypeSheet(canonical, global);

            internal LLVMValueRef TypeSheetFor(string canonical) => Env.TypeSheetFor(canonical);

            internal bool TryGetTypeSheet(string canonical, out LLVMValueRef global) =>
                Env.TryGetTypeSheet(canonical, out global);

            internal void RegisterTypeInfo(string canonical, LLVMValueRef global) =>
                Env.RegisterTypeInfo(canonical, global);

            internal LLVMValueRef TypeInfoFor(string canonical) => Env.TypeInfoFor(canonical);

            internal bool TryGetTypeInfo(string canonical, out LLVMValueRef global) =>
                Env.TryGetTypeInfo(canonical, out global);

            internal void RegisterStaticField(string canonical, LLVMValueRef global, MirType type) =>
                Env.RegisterStaticField(canonical, global, type);

            internal LLVMValueRef StaticFieldFor(string canonical) => Env.StaticFieldFor(canonical);

            internal bool TryGetFace(string symbol, out (LLVMValueRef Fn, LLVMTypeRef Type) face) =>
                Env.TryGetFace(symbol, out face);

            internal void AddFace(string symbol, LLVMValueRef fn, LLVMTypeRef type) =>
                Env.AddFace(symbol, fn, type);

            internal LLVMValueRef InternStringConstant(string text) =>
                Env.InternStringConstant(text);

            internal bool IsInlineValueType(MirType type, out Layout.TypeLayoutPlan plan) =>
                Env.IsInlineValueType(type, out plan);

            internal bool TryFindValuePlan(string canonical, out Layout.TypeLayoutPlan plan) =>
                Env.TryFindValuePlan(canonical, out plan);

            internal static LLVMTypeRef BytePointer() => LlvmEmitEnvironment.BytePointer();

            internal void EmitMemCopy(LLVMBuilderRef builder, LLVMValueRef dest,
                LLVMValueRef src, int size) =>
                Env.EmitMemCopy(builder, dest, src, size);

            internal void EmitMemCopyN(LLVMBuilderRef builder, LLVMValueRef dest,
                LLVMValueRef src, LLVMValueRef sizeI64) =>
                Env.EmitMemCopyN(builder, dest, src, sizeI64);

            internal void EmitMemSetZero(LLVMBuilderRef builder, LLVMValueRef dest, int size) =>
                Env.EmitMemSetZero(builder, dest, size);

            internal LLVMValueRef LoadLocal(LLVMBuilderRef builder,
                Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirOperand operand) =>
                Env.LoadLocal(builder, slots, operand);

            internal LLVMValueRef StoreToTemp(LLVMBuilderRef builder, LLVMValueRef value) =>
                Env.StoreToTemp(builder, value);

            internal static bool IsFatReferenceLocal(MirType type) =>
                LlvmEmitEnvironment.IsFatReferenceLocal(type);

            // 进程退出前释放静态托管槽（shim.c atexit 调用）
            private void EmitGlobalsCleanup(LLVMBuilderRef builder)
            {
                var fnType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void,
                    System.Array.Empty<LLVMTypeRef>(), false);
                var fn = Module.AddFunction("rigi_globals_cleanup", fnType);
                builder.PositionAtEnd(fn.AppendBasicBlock("entry"));
                foreach (var (global, fieldType) in Env.StaticSlots)
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
                    var emitted = Env.FunctionOf(fn.Symbol.Canonical);
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
                EmitDispatchWrappers(builder);
            }

            // ===== MW11c 棒5a：固定导出符号（spec A 链接策略）=====
            // rigi_rt 的 Worker/定时器原语需要的 Rigi 入口经固定导出符号
            // 传递（stdlib Rigi 无 fn 指针物化通道；shim.c 的 extern
            // rigi_entry 先例）：
            //   rigi_dispatcher_entry  void(void)  —— Worker 线程体入口
            //     （rigi_worker_create(entryFn=0) 的约定入口）：取
            //     Dispatcher singleton → workerLoop(TLS 当前 Worker)。
            //   rigi_dispatch_publish  void(i64)   —— 定时器响铃/轮询
            //     退避回调重发布协程句柄进 Rigi Dispatcher（lane 由
            //     cohandle 槽在 publish 内读取）。
            // 恒发射（外部链接）：无协程程序的 rigi_rt 仍引用两符号，
            // 缺体会链接失败；符号缺失（模块未收编 Dispatcher 运行时段）
            // 时退化为诊断 abort 体（永不被调用——无协程即无定时器/Worker）。
            private const string DispatcherTypeCanonical = "core.coroutine::Dispatcher";
            private const string DispatcherWorkerLoopCanonical =
                "core.coroutine::Dispatcher$workerLoop(worker:.i64)@.void";
            private const string DispatcherPublishCanonical =
                "core.coroutine::Dispatcher$publish(handle:.i64)@.void";

            private void EmitDispatchWrappers(LLVMBuilderRef builder)
            {
                // rigi_dispatcher_entry: void(void)；rigi_dispatch_publish:
                // void(i64 handle)——外部链接恒发射
                var entryType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void,
                    System.Array.Empty<LLVMTypeRef>(), false);
                var entry = Module.AddFunction("rigi_dispatcher_entry", entryType);
                var entryBlock = entry.AppendBasicBlock("entry");
                var publishType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void,
                    new[] { LLVMTypeRef.Int64 }, false);
                var publish = Module.AddFunction("rigi_dispatch_publish", publishType);
                var publishBlock = publish.AppendBasicBlock("entry");

                if (TryGetFunction(Binding.SingletonPlanner.GetFnCanonicalOf(
                        DispatcherTypeCanonical), out var getFn)
                    && TryGetFunction(DispatcherWorkerLoopCanonical, out var loopFn)
                    && TryGetFunction(DispatcherPublishCanonical, out var publishFn))
                {
                    builder.PositionAtEnd(entryBlock);
                    var disp = builder.BuildCall2(getFn.Type, getFn.Value,
                        System.Array.Empty<LLVMValueRef>(), "rt.disp");
                    var (tlsFn, tlsType) = CallEmitter.DeclareHelperFace(this,
                        "rigi_tls_current_context", LLVMTypeRef.Int64,
                        System.Array.Empty<LLVMTypeRef>());
                    var worker = builder.BuildCall2(tlsType, tlsFn,
                        System.Array.Empty<LLVMValueRef>(), "rt.worker");
                    builder.BuildCall2(loopFn.Type, loopFn.Value,
                        new[] { disp, worker }, "");
                    ArcEmitter.EmitReleaseFatValue(this, builder, disp);
                    builder.BuildRetVoid();

                    builder.PositionAtEnd(publishBlock);
                    var disp2 = builder.BuildCall2(getFn.Type, getFn.Value,
                        System.Array.Empty<LLVMValueRef>(), "rt.disp");
                    builder.BuildCall2(publishFn.Type, publishFn.Value,
                        new[] { disp2, publish.GetParam(0) }, "");
                    ArcEmitter.EmitReleaseFatValue(this, builder, disp2);
                    builder.BuildRetVoid();
                    return;
                }

                builder.PositionAtEnd(entryBlock);
                EmitWrapperMissingBody(builder);
                builder.PositionAtEnd(publishBlock);
                EmitWrapperMissingBody(builder);
            }

            // 导出符号缺体兜底：stderr 诊断 + rigi_exc_halt（exit 1，
            // noreturn；模块无异常类型时 ExcHalt 仍由 rigi_rt 提供——
            // shim/eh 面族恒在）
            private void EmitWrapperMissingBody(LLVMBuilderRef builder)
            {
                EmitPrintErr(builder, StringAbi.BuildConstant(Module,
                    "rigi_rt: 导出入口被调用但模块未收编 Dispatcher 运行时段"
                    + "（编译器 bug）\n", "rt.missing"));
                var (haltFn, haltType) = CallEmitter.DeclareVoidFace(this,
                    RuntimeFaces.ExcHalt);
                builder.BuildCall2(haltType, haltFn,
                    System.Array.Empty<LLVMValueRef>(), "");
                builder.BuildUnreachable();
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
                if (fn.IsCoroutineResume)
                {
                    // MW11a 棒3 有意特判：MIR 签名是 胖引用 → i32，
                    // 但 C ABI 必须匹配 RigiResumeFn = i32(ptr)
                    //（rigi_coroutine_create 以函数指针收取，签名不符
                    // 即 UB）；胖引用重构在 EmitBody 的 entry prologue
                    // 完成。棒5a：probe fn 已退为普通 MIR fn（native
                    // 侧不再回调，探测由恢复块直调），无特判
                    if (fn.Parameters.Count != 1
                        || fn.Parameters[0].Name != Passes.CoroutineSplitPass.FrameParamName
                        || fn.ReturnType.Key != "i32")
                    {
                        throw new CompilerInternalException(
                            $"resume fn 签名形态异常: {fn.Symbol.Canonical}");
                    }
                    type = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32,
                        new[] { Session.BytePointer() }, false);
                    name = fn.Symbol.Canonical;
                }
                else
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
                Env.AddFunction(fn.Symbol.Canonical, new EmittedFunction(value, type, fn));
            }

            // rigi_entry 合成 stub：MW11c 棒5a 对齐 VM BilVm.Run 语义——
            //   1. singleton 急切初始化（§8.7，VM 启动序
            //      InitializeSingletons → InvokeGlobalInitializers → main
            //      同口径）→ ..globals.init → main 同步直调（现状保留；
            //      main 之前的 pending 检查维持 fail-fast 直报）
            //   2. main 返回后 pending 非空：rigi_exc_take 收进 entry.exc
            //      合成槽（不立即报告——VM 语义 main 失败仍等 quiescence），
            //      带到第 4 步
            //   3. Dispatcher 主 workerLoop（Rigi 世界调度逻辑，
            //      §17.4：阻塞至 quiescence，fire-and-forget 同被等待；
            //      模块未收编协程运行时段时跳过——无协程程序零开销）→
            //      rigi_main_worker_shutdown（主 Worker 定时器/loop 收尾）
            //   4. 失败汇总（优先级 = main 失败 > 未观察失败，对齐 VM
            //      main.Failure ?? UnobservedFailure）：entry.exc 非空 →
            //      reporter；否则 rigi_failure_take_unobserved（native
            //      失败注册表）有 → 同 reporter；都无 → ret main 返回值
            // reporter 前提缺失（模块内无异常类型可达 → pending 恒空、
            // 不可能有失败）时退回无检查序列（workerLoop 段仍按可达性
            // 发射）
            private void EmitEntryStub(LLVMBuilderRef builder, EmittedFunction entrypoint)
            {
                var stubType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32,
                    System.Array.Empty<LLVMTypeRef>(), false);
                var stub = Module.AddFunction(EntrySymbol, stubType);
                builder.PositionAtEnd(stub.AppendBasicBlock("entry"));
                SetCurrentFunction(stub);
                var reporterTarget = ResolveUncaughtReporterTarget(out var exceptionSheet);
                var reporterAvailable = reporterTarget != null;

                // 合成槽（entry 块 alloca）：entry.exc = 已 take 的异常
                // 对象裸指针（+1 持有）；entry.fat = 未观察失败 out 槽；
                // entry.result = main 返回值（穿过 drain 段后才 ret）
                LLVMValueRef excSlot = default, fatSlot = default;
                LLVMBasicBlockRef reporter = default, takeEntry = default;
                if (reporterAvailable)
                {
                    excSlot = builder.BuildAlloca(BytePointer(), "entry.exc");
                    builder.BuildStore(LLVMValueRef.CreateConstNull(BytePointer()), excSlot);
                    fatSlot = builder.BuildAlloca(TypeLayout.FatReferenceType(Context),
                        "entry.fat");
                    reporter = stub.AppendBasicBlock("entry.uncaught");
                    takeEntry = stub.AppendBasicBlock("entry.take");
                }
                var resultSlot = builder.BuildAlloca(LLVMTypeRef.Int32, "entry.result");

                foreach (var singleton in Singletons)
                {
                    if (!TryGetFunction(singleton.GetFnCanonical, out var getFn))
                    {
                        continue;
                    }
                    var got = builder.BuildCall2(getFn.Type, getFn.Value,
                        System.Array.Empty<LLVMValueRef>(), "singleton.get");
                    ArcEmitter.EmitReleaseFatValue(this, builder, got);
                    EmitEntryPendingCheck(builder, stub, reporterAvailable, takeEntry);
                }
                if (TryGetFunction("$..globals.init()@.void", out var globalsInit))
                {
                    builder.BuildCall2(globalsInit.Type, globalsInit.Value,
                        System.Array.Empty<LLVMValueRef>(), "");
                    EmitEntryPendingCheck(builder, stub, reporterAvailable, takeEntry);
                }
                if (entrypoint.Mir.ReturnType.IsVoid)
                {
                    builder.BuildCall2(entrypoint.Type, entrypoint.Value,
                        System.Array.Empty<LLVMValueRef>(), "");
                    builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false),
                        resultSlot);
                }
                else
                {
                    var result = builder.BuildCall2(entrypoint.Type, entrypoint.Value,
                        System.Array.Empty<LLVMValueRef>(), "main.result");
                    builder.BuildStore(result, resultSlot);
                }

                LLVMBasicBlockRef drain;
                if (reporterAvailable)
                {
                    // main 后 pending 非空 → take 收进 entry.exc（不立即
                    // 报告），空 → 直进 drain 段
                    var (pendFn, pendType) = CallEmitter.DeclareHelperFace(this,
                        RuntimeFaces.ExcPending, BytePointer(), System.Array.Empty<LLVMTypeRef>());
                    var pending = builder.BuildCall2(pendType, pendFn,
                        System.Array.Empty<LLVMValueRef>(), "entry.pending");
                    var hasPending = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, pending,
                        LLVMValueRef.CreateConstNull(BytePointer()), "entry.has");
                    var mainExc = stub.AppendBasicBlock("entry.mainexc");
                    drain = stub.AppendBasicBlock("entry.drain");
                    builder.BuildCondBr(hasPending, mainExc, drain);
                    builder.PositionAtEnd(mainExc);
                    var (takeFn, takeType) = CallEmitter.DeclareHelperFace(this,
                        RuntimeFaces.ExcTake, BytePointer(), System.Array.Empty<LLVMTypeRef>());
                    var mainObj = builder.BuildCall2(takeType, takeFn,
                        System.Array.Empty<LLVMValueRef>(), "entry.main.take");
                    builder.BuildStore(mainObj, excSlot);
                    builder.BuildBr(drain);
                }
                else
                {
                    drain = stub.AppendBasicBlock("entry.drain");
                    builder.BuildBr(drain);
                }

                // drain 段（棒5a）：Dispatcher 主 workerLoop（模块收编
                // 协程运行时段时）→ 主 Worker 收尾；随后失败汇总
                builder.PositionAtEnd(drain);
                if (TryGetFunction(Binding.SingletonPlanner.GetFnCanonicalOf(
                        DispatcherTypeCanonical), out var entryGetFn)
                    && TryGetFunction(DispatcherWorkerLoopCanonical, out var entryLoopFn))
                {
                    var disp = builder.BuildCall2(entryGetFn.Type, entryGetFn.Value,
                        System.Array.Empty<LLVMValueRef>(), "entry.disp");
                    var (loopFn, loopType) = (entryLoopFn.Value, entryLoopFn.Type);
                    builder.BuildCall2(loopType, loopFn,
                        new[] { disp, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false) },
                        "");
                    var (shutdownFn, shutdownType) = CallEmitter.DeclareVoidFace(this,
                        "rigi_main_worker_shutdown");
                    builder.BuildCall2(shutdownType, shutdownFn,
                        System.Array.Empty<LLVMValueRef>(), "");
                    ArcEmitter.EmitReleaseFatValue(this, builder, disp);
                }

                if (reporterAvailable)
                {
                    // 失败汇总：main 失败优先，其次未观察失败，都无 ret
                    var held = builder.BuildLoad2(BytePointer(), excSlot, "entry.held");
                    var hasMainExc = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, held,
                        LLVMValueRef.CreateConstNull(BytePointer()), "entry.hasmain");
                    var unobserved = stub.AppendBasicBlock("entry.unobserved");
                    builder.BuildCondBr(hasMainExc, reporter, unobserved);
                    builder.PositionAtEnd(unobserved);
                    var (unobsFn, unobsType) = CallEmitter.DeclareHelperFace(this,
                        "rigi_failure_take_unobserved", LLVMTypeRef.Int32,
                        new[] { BytePointer() });
                    var found = builder.BuildCall2(unobsType, unobsFn,
                        new[] { fatSlot }, "entry.unobs");
                    var hasUnobs = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, found,
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false),
                        "entry.hasunobs");
                    var unobsHit = stub.AppendBasicBlock("entry.unobs.hit");
                    var retBlock = stub.AppendBasicBlock("entry.ret");
                    builder.BuildCondBr(hasUnobs, unobsHit, retBlock);
                    builder.PositionAtEnd(unobsHit);
                    // +1 随 out 移交：payload 存 entry.exc（reporter 体统一
                    // 按 core::Exception 静态型装箱并归还）
                    var unobsFat = builder.BuildLoad2(
                        TypeLayout.FatReferenceType(Context), fatSlot, "entry.unobs.fat");
                    var unobsObj = builder.BuildIntToPtr(
                        builder.BuildExtractValue(unobsFat, 1, "entry.unobs.pl"),
                        BytePointer(), "entry.unobs.obj");
                    builder.BuildStore(unobsObj, excSlot);
                    builder.BuildBr(reporter);
                    builder.PositionAtEnd(retBlock);
                    builder.BuildRet(builder.BuildLoad2(LLVMTypeRef.Int32, resultSlot,
                        "entry.ret.v"));

                    // pre-main fail-fast 入口：take → 存 entry.exc → reporter
                    builder.PositionAtEnd(takeEntry);
                    var (takeFn2, takeType2) = CallEmitter.DeclareHelperFace(this,
                        RuntimeFaces.ExcTake, BytePointer(), System.Array.Empty<LLVMTypeRef>());
                    var preObj = builder.BuildCall2(takeType2, takeFn2,
                        System.Array.Empty<LLVMValueRef>(), "entry.pre.take");
                    builder.BuildStore(preObj, excSlot);
                    builder.BuildBr(reporter);

                    builder.PositionAtEnd(reporter);
                    EmitUncaughtReporterBody(builder, reporterTarget!, exceptionSheet, excSlot);
                }
                else
                {
                    builder.BuildRet(builder.BuildLoad2(LLVMTypeRef.Int32, resultSlot,
                        "entry.ret.v"));
                }
            }

            // main 之前调用的 pending 检查（singleton 构造/globals.init
            // fail-fast，MW9a 现状保留）：非空跳 entry.take（take 收槽后
            // 落 reporter），空落内联合成继续块。reporter 前提缺失
            //（pending 恒空）时不插检查
            private void EmitEntryPendingCheck(LLVMBuilderRef builder,
                LLVMValueRef stub, bool reporterAvailable, LLVMBasicBlockRef takeEntry)
            {
                if (!reporterAvailable)
                {
                    return;
                }
                var (pendFn, pendType) = CallEmitter.DeclareHelperFace(this,
                    RuntimeFaces.ExcPending, BytePointer(), System.Array.Empty<LLVMTypeRef>());
                var pending = builder.BuildCall2(pendType, pendFn,
                    System.Array.Empty<LLVMValueRef>(), "entry.pending");
                var hasPending = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, pending,
                    LLVMValueRef.CreateConstNull(BytePointer()), "entry.has");
                var cont = stub.AppendBasicBlock("entry.cont");
                builder.BuildCondBr(hasPending, takeEntry, cont);
                builder.PositionAtEnd(cont);
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

            // reporter 体：从 entry.exc 合成槽取异常对象（+1 已随
            // take/unobserved out 移交本槽；三个来源入口共用本块）→
            // 虚派发 getMessage()（静态目标 core::Exception.getMessage，
            // 实际 override 由对象头 vtable 解析；返回 String 值）→
            // rigi_type_name_of 取实际类型全名 → rigi_print_err 逐段
            // 打印 → 释放异常胖引用与 message 字符串 → rigi_exc_halt
            //（exit 1，noreturn）
            private void EmitUncaughtReporterBody(LLVMBuilderRef builder,
                MwMemberSymbol getMessage, LLVMValueRef exceptionSheet, LLVMValueRef excSlot)
            {
                var held = builder.BuildLoad2(BytePointer(), excSlot, "uncaught.obj");
                // 接收者胖引用：typeid 半按槽静态类型（core::Exception
                // sheet，tag2）；虚派发只读 payload 与对象头，实际实现
                // 由运行期 vtable 解析
                var fat = CallEmitter.BuildFatReference(this, builder, exceptionSheet, held);
                var virtualSlot = VirtualCallEmitter.VirtualSlotOf(this, getMessage);
                var entry = VirtualCallEmitter.EmitVTableEntry(this, builder, "rigi_vtable_entry", new[]
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
                // 先建全部基本块（终结符按 id 引用，可前向引用），再逐块发射
                var blockRefs = new Dictionary<string, LLVMBasicBlockRef>(System.StringComparer.Ordinal);
                foreach (var block in fn.Blocks)
                {
                    blockRefs.Add(block.Id, emitted.Value.AppendBasicBlock(block.Id));
                }

                // 具名局部 → alloca 槽（entry 块开头，mem2reg 友好；含参数落槽）。
                // 值类型局部 = 计划尺寸的内联槽；值类型宿主的 .this 不开槽——
                // 槽即传入指针别名（SYNTAX §10 this 别名：字段写原地生效）
                builder.PositionAtEnd(blockRefs[fn.Blocks[0].Id]);
                var slots = new Dictionary<string, (LLVMValueRef Slot, MirLocal Local)>(System.StringComparer.Ordinal);
                var valueThis = fn.Symbol.Owner is { Declaration.Kind:
                    Bil.BilTypeKind.Struct or Bil.BilTypeKind.EnumStruct
                    or Bil.BilTypeKind.Wrapper };
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
                Ctx.BeginBody(emitted.Value, builder, slots, blockRefs);
                try
                {
                var llvmIndex = IsInlineValueType(fn.ReturnType, out _) ? 1 : 0;
                for (var i = 0; i < fn.Parameters.Count; i++)
                {
                    var parameter = fn.Parameters[i];
                    if (GenericAbi.IsClassLevelTypeId(fn.Symbol, parameter.Name))
                    {
                        continue;
                    }
                    var llvmParam = emitted.Value.GetParam((uint)llvmIndex++);
                    if (fn.IsCoroutineResume)
                    {
                        // resume prologue（DeclareFunction 特判的对偶）：
                        // 裸 ptr 参数重构为 frame 胖引用（typeid 半 = frame
                        // sheet 常量 tag2 编码，payload = 参数 ptr）落
                        // $mw.frame 槽，随后按普通 MIR 体发射
                        var frameSheet = TypeSheetFor(parameter.Type.Canonical);
                        var frameFat = BoxEmitter.PackObject(this, builder,
                            frameSheet, llvmParam);
                        builder.BuildStore(frameFat, slots[parameter.Name].Slot);
                        continue;
                    }
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
                        LlvmEmitDispatchers.Visit(inst, this);
                    }
                    TerminatorEmitter.Emit(this, builder, slots, blockRefs, emitted.Value, fn, block.Terminator);
                }
                }
                finally
                {
                    Ctx.EndBody();
                }
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
        }
    }
}
