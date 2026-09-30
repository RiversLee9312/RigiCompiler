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
                module.DataLayout = LlvmHost.HostDataLayout;
                new Session(module, mir, context.Layout, context.Symbols, context.Module,
                    context.Singletons, context.BorrowedReturnSymbols).EmitAll();
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
                System.Collections.Generic.IReadOnlyList<Binding.SingletonEntry>? singletons = null,
                System.Collections.Generic.HashSet<string>? borrowedReturnSymbols = null)
            {
                Env = new LlvmEmitEnvironment(module, mir, layout, symbols, bilModule);
                Ctx = new LlvmEmitContext();
                Singletons = singletons
                    ?? System.Array.Empty<Binding.SingletonEntry>();
                // 3b-δ1：RcInjection 挂载的借用返回集合（null = 无借用标记）；
                // EmitBody 按当前 fn 重算借用槽缓存
                BorrowedReturnSymbols = borrowedReturnSymbols;
            }

            internal LlvmEmitEnvironment Env { get; }
            internal LlvmEmitContext Ctx { get; }

            // MW10 刀5：singleton 三态/缓存合成槽与急切初始化调用表
            //（SingletonLoweringPass 挂载；空 = 模块无 singleton）
            internal System.Collections.Generic.IReadOnlyList<Binding.SingletonEntry> Singletons
            { get; }

            // 3b-δ1：借用返回集合（RcInjection 挂载）+ 当前 fn 的借用槽
            // 缓存（EmitBody 进入时重算；cast 读侧按此裸取）
            internal System.Collections.Generic.HashSet<string>? BorrowedReturnSymbols
            { get; }
            internal System.Collections.Generic.HashSet<string>? BorrowedSlots
            { get; private set; }

            internal bool IsBorrowedSlot(string slot) =>
                BorrowedSlots?.Contains(slot) == true;

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
                FatValueSlotAbi.DeclareAll(this);
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
                FatValueSlotAbi.EmitAll(this, builder);
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
                "core.coroutine::Dispatcher$publishNative(token:.i64)@.void";

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
                    // B-1：tainted main 已 split 为 spawn stub（ret 热
                    // Task；IsAsync 标记），返回类型约束由 EmitEntryStub
                    // 的根驱动变体接管
                    if (!fn.IsAsync && !fn.ReturnType.IsVoid && fn.ReturnType.Key != "i32")
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
                        // G1：值类型泛型宿主的实例成员保留类级 typeid 参数
                        //（无对象头隐藏槽可自取，调用点按 §7.2 序直传）；
                        // 嵌套类外层宿主链 GP 同属类级（Symbols 判定，#02）
                        if (GenericAbi.IsClassLevelTypeId(Symbols, fn.Symbol, fn.Parameters[i].Name)
                            && !GenericAbi.KeepsClassTypeIdInAbi(fn.Symbol,
                                fn.Parameters.Count > 0 && fn.Parameters[0].Name == ".this"))
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
            // B-1 根驱动变体：main tainted（含挂起点）时 CoroutineSplit
            //      已把它 split 成 spawn stub（IsAsync 标记，ret 热
            //      Task）——本 stub 调 main() 即发布主协程进
            //      Dispatcher（对齐 VM BilVm.Run 的 main 协程化），
            //      drain 段跑到 quiescence 后调 $mw.main.settle 取回
            //      结果/重抛 main 失败（重抛置 pending → 收进
            //      entry.exc，失败汇总序不变）
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
                // B-1：tainted main 的 spawn stub 标记（ret 热 Task）
                var mainIsCoroutine = entrypoint.Mir.IsAsync;

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
                // B-1：tainted main 的热 Task 持有槽（drain 后 settle
                // 消费并归还）
                LLVMValueRef mainTaskSlot = default;

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
                if (mainIsCoroutine)
                {
                    // B-1 根驱动：main 已是 spawn stub——调用即建 frame/
                    // cohandle 并 noteSpawn+publish 进 Dispatcher（结果
                    // 待 drain 后 settle 取回，此处先落 0 占位）
                    var mainTask = builder.BuildCall2(entrypoint.Type, entrypoint.Value,
                        System.Array.Empty<LLVMValueRef>(), "main.task");
                    mainTaskSlot = builder.BuildAlloca(
                        TypeLayout.FatReferenceType(Context), "entry.task");
                    builder.BuildStore(mainTask, mainTaskSlot);
                    builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false),
                        resultSlot);
                }
                else if (entrypoint.Mir.ReturnType.IsVoid)
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

                // MW12b §25.2：undisposed 全局异常事件 drain（派发时机
                // 定稿：main/drain 之后、失败汇总之前）
                EmitGexcDrain(builder, stub, reporterAvailable, takeEntry);

                // B-1 根驱动收尾：drain 至 quiescence 后主协程已终态
                // ——settle 读 Task 取 main 结果（写 resultSlot）/重抛
                // main 失败（pending 置位，随后收进 entry.exc）；Task
                // 胖引用随 settle 归还
                if (mainIsCoroutine)
                {
                    var settleCanonical = Passes.CoroutineSplitPass.MainSettleCanonicalOf(
                        entrypoint.Mir.ReturnType.Canonical);
                    if (!TryGetFunction(settleCanonical, out var settleFn))
                    {
                        throw new CompilerInternalException(
                            "main settle fn 未合成: " + settleCanonical);
                    }
                    var heldTask = builder.BuildLoad2(
                        TypeLayout.FatReferenceType(Context), mainTaskSlot, "entry.task.v");
                    var settled = builder.BuildCall2(settleFn.Type, settleFn.Value,
                        new[] { heldTask }, "entry.settle");
                    builder.BuildStore(settled, resultSlot);
                    ArcEmitter.EmitReleaseFatValue(this, builder, heldTask);
                    EmitEntryPendingCheck(builder, stub, reporterAvailable, takeEntry);
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
                // 3b-δ1：当前 fn 的借用槽缓存（义务层真相源在 RcInjection，
                // 此处只供 CastEmitter 读侧豁免判定）
                BorrowedSlots = BorrowedReturnSymbols is null
                    ? null
                    : Passes.RcInjectionPass.DeriveBorrowedSlots(
                        fn, BorrowedReturnSymbols);
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
                // 烘焙后的原始体可能没有声明 owner；receiver 的真实参数形状才是 ABI 事实。
                var valueThis = fn.Parameters.FirstOrDefault(p => p.Name == ".this") is { } receiver
                    && IsInlineValueType(receiver.Type, out _);
                foreach (var local in fn.Locals)
                {
                    if ((valueThis && local.Name == ".this")
                        || (local.Name == Passes.WrapperSelfParameterPass.SelfParameter
                            && fn.Parameters.Contains(local) && IsInlineValueType(local.Type, out _)))
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
                    // G1：值类型泛型宿主实例成员的类级 typeid 是真实 LLVM
                    // 参数（调用点直传）；class 宿主与值类型静态成员剔除
                    //（前者 prologue 自取；后者 §9.2.3 不用，落 Any 兜底）
                    if (GenericAbi.IsClassLevelTypeId(Symbols, fn.Symbol, parameter.Name)
                        && !GenericAbi.KeepsClassTypeIdInAbi(fn.Symbol,
                            fn.Parameters.Count > 0 && fn.Parameters[0].Name == ".this"))
                    {
                        if (GenericAbi.IsValueTypeOwner(fn.Symbol.Owner))
                        {
                            // 值类型静态成员的类级 typeid 形参（BIL 声明但
                            // §9.2.3 不可用）：落 core::Any sheet 常量，对齐
                            // VM AlignGenericHiddenArgs 缺省 .any 口径
                            builder.BuildStore(
                                TypeSheetFor("core::Any"), slots[parameter.Name].Slot);
                        }
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
                    if ((valueThis && parameter.Name == ".this")
                        || (parameter.Name == Passes.WrapperSelfParameterPass.SelfParameter
                            && IsInlineValueType(parameter.Type, out _)))
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
                EmitDisposeMarkPrologue(builder, emitted, slots);

                foreach (var block in fn.Blocks)
                {
                    builder.PositionAtEnd(blockRefs[block.Id]);
                    for (var instIndex = 0; instIndex < block.Instructions.Count; instIndex++)
                    {
                        var inst = block.Instructions[instIndex];
                        if (instIndex + 2 < block.Instructions.Count
                            && inst is MirReleaseSlot release
                            && block.Instructions[instIndex + 1] is MirCopyLocal copy
                            && block.Instructions[instIndex + 2] is MirAcquireSlot acquire
                            && release.Local == copy.Target
                            && acquire.Local == copy.Target)
                        {
                            ArcEmitter.EmitManagedCopy(this, release, copy, acquire);
                            instIndex += 2;
                            continue;
                        }
                        // Phase 1.2 region 合并：块首连续参数 acquire 段与块尾
                        // 连续出口 release 段（ret 出口/传播垫共用形态）各外包
                        // 一对 region 进出；段内全为单槽 ARC 运行时调用（无挂
                        // 起点、无可抛调用，§23.3 两条不变量静态成立），级联
                        // 复用当前 region 是 §23.3 明文形态。段长 < 2 不外包。
                        if (inst is MirAcquireSlot && instIndex == 0)
                        {
                            var run = ArcRunLength(block.Instructions, 0, isAcquire: true);
                            if (run >= 2)
                            {
                                EmitRegionWrappedArcRun(builder, block.Instructions, 0, run);
                                instIndex = run - 1;
                                continue;
                            }
                        }
                        if (inst is MirReleaseSlot)
                        {
                            var run = ArcRunLength(block.Instructions, instIndex, isAcquire: false);
                            if (run >= 2)
                            {
                                EmitRegionWrappedArcRun(builder, block.Instructions, instIndex, run);
                                instIndex += run - 1;
                                continue;
                            }
                        }
                        LlvmEmitDispatchers.Visit(inst, this);
                    }
                    if (block.Id == "mw.state.0" && fn.RestoredEntrySource is { } source
                        && Layout?.DisposeImplementations.Contains(source) == true)
                        EmitDisposeReceiverMark(builder, slots);
                    TerminatorEmitter.Emit(this, builder, slots, blockRefs, emitted.Value, fn, block.Terminator);
                }
                }
                finally
                {
                    Ctx.EndBody();
                }
            }

            // ===== Phase 1.2：连续单槽 ARC 段的 region 合并 =====

            // 自 start 起（acquire 段仅限块首）连续同槽类 ARC 指令的段长；
            // 遇到任意其他指令即止。release 段天然要求延伸到块尾语义之外时
            // 中断——RcInjection 的出口序列/传播垫正位于块尾，块中前置
            // release（后随可抛产出指令）永远成不了段，保证不越过可抛点。
            private int ArcRunLength(IReadOnlyList<MirInst> insts, int start,
                bool isAcquire)
            {
                var length = 0;
                for (var i = start; i < insts.Count; i++)
                {
                    if (isAcquire
                        ? insts[i] is not MirAcquireSlot
                        : insts[i] is not MirReleaseSlot)
                    {
                        break;
                    }
                    length++;
                }
                return length;
            }

            // [start, start+length) 的连续单槽 acquire/release 外包一对
            // region 进出逐条发射（§23.3「复合引用操作可合并为同一
            // acquire/release region」）；段内条目用免显式 region 包裹的
            // 槽操作原语（AcquireSlotBody/ReleaseSlotBody）——单发路径的
            // 显式进出在段内是冗余的，外层一对即段的完整 region 契约
            //（槽清零与释放同处一个 region 的不变量由外层满足），内层
            // 也不再付 arc.c 的 TLS 嵌套计数。
            private void EmitRegionWrappedArcRun(LLVMBuilderRef builder,
                IReadOnlyList<MirInst> insts, int start, int length)
            {
                ArcEmitter.CallRegionFace(this, builder, RuntimeFaces.RegionEnter);
                for (var i = start; i < start + length; i++)
                {
                    switch (insts[i])
                    {
                        case MirAcquireSlot acquire:
                            ArcEmitter.AcquireSlotBody(this, builder, Slots, acquire.Local);
                            break;
                        case MirReleaseSlot release:
                            ArcEmitter.ReleaseSlotBody(this, builder, Slots, release.Local);
                            break;
                        default:
                            throw new CompilerInternalException(
                                $"region 合并段混入非单槽 ARC 指令: {insts[i].GetType().Name}");
                    }
                }
                ArcEmitter.CallRegionFace(this, builder, RuntimeFaces.RegionExit);
            }

            // ===== MW12b §25.2：dispose 进入置位 =====

            // fn 是 core::IDisposable.dispose 的实现槽目标（Layout 期按
            // iMap 段基址 + 接口壳槽序收集的 DisposeImplementations 成员）
            // 时，prologue 发射 rigi_mark_disposed(this)——进入即置位
            //（调用了但抛异常也算负责过；挂起中的 dispose 对象被 frame
            // 保持不进销毁检查，单 bit 足够）。槽目标身份判定天然兼容
            // wrapper 烘焙外移体（$.mwrapped. 中缀 trampoline 即槽目标）
            // 与 async dispose（槽目标 = stub 原符号，调用即进入；
            // resume 合成 fn 不在集合）
            private void EmitDisposeMarkPrologue(LLVMBuilderRef builder, EmittedFunction emitted,
                Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots)
            {
                if (Layout == null
                    || !Layout.DisposeImplementations.Contains(emitted.Mir.Symbol.Canonical)
                    || !slots.ContainsKey(".this"))
                {
                    return;
                }
                EmitDisposeReceiverMark(builder, slots);
            }

            private void EmitDisposeReceiverMark(LLVMBuilderRef builder,
                Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots)
            {
                // class 宿主的 .this 是胖引用槽（EmitClassTypeIdPrologue
                // 同款物化形态）：payload 半 = 对象裸指针
                var fat = LoadLocal(builder, slots, new MirLocalOperand(".this"));
                var obj = builder.BuildIntToPtr(
                    builder.BuildExtractValue(fat, 1, "this.payload"),
                    BytePointer(), "this.obj");
                var (markFn, markType) = CallEmitter.DeclareHelperFace(this,
                    RuntimeFaces.MarkDisposed, LLVMTypeRef.Void, new[] { BytePointer() });
                builder.BuildCall2(markType, markFn, new[] { obj }, "");
            }

            // ===== MW12b §25.2：entry stub 全局异常事件 drain =====

            // undisposed 事件 drain（main/drain 之后、失败汇总之前）：
            // rigi_gexc_take 弹空为止——逐条构造
            // core::UndisposedResourceException（真 alloc+init，不 raise）→
            // 调 core::GlobalExceptionHandler.dispatch（Rigi 静态方法；
            // 每次调用后 EmitEntryPendingCheck 同款 pending 检查，处理器
            // 抛异常进正常失败汇总）。dispatch/init/TypeSheet 任一缺失
            //（模块未收编该运行时段）→ 整个 drain 不发射。
            // 晚到事件（globals_cleanup 与 GC 终轮收集阶段入队）不经本
            // 段，由 rigi_rt gexc.c 的 atexit flush 默认打印
            private void EmitGexcDrain(LLVMBuilderRef builder, LLVMValueRef stub,
                bool reporterAvailable, LLVMBasicBlockRef takeEntry)
            {
                MwMemberSymbol? dispatch = null;
                if (Symbols.FindTypeByRef("core::GlobalExceptionHandler") is { } handlerType)
                {
                    foreach (var member in handlerType.Members)
                    {
                        if (member.Declaration.Kind is BilMemberKind.Method
                                or BilMemberKind.StaticMethod
                            && member.SignatureKey == ".static.dispatch(exc:core::Exception)")
                        {
                            dispatch = member;
                            break;
                        }
                    }
                }
                var excType = Symbols.FindTypeByRef("core::UndisposedResourceException");
                MwMemberSymbol? init = null;
                if (excType != null)
                {
                    foreach (var member in excType.Members)
                    {
                        if (member.HasKeyword(BilKeyword.Init)
                            && CanonicalSignature.Parse(member.Canonical).Parameters.Count == 1)
                        {
                            init = member;
                            break;
                        }
                    }
                }
                if (dispatch == null || excType == null || init == null
                    || !TryGetFunction(dispatch.Canonical, out var dispatchFn)
                    || !TryGetFunction(init.Canonical, out _)
                    || !TryGetTypeSheet(excType.Canonical, out _))
                {
                    return;
                }
                var wrapper = Symbols.FindMember(
                    excType.Canonical + "$..init.wrapper()@.void");

                var nameSlot = builder.BuildAlloca(StringAbi.ValueType(Context), "gexc.name");
                var loop = stub.AppendBasicBlock("gexc.loop");
                var bodyBlock = stub.AppendBasicBlock("gexc.body");
                var done = stub.AppendBasicBlock("gexc.done");
                builder.BuildBr(loop);
                builder.PositionAtEnd(loop);
                var (takeFn, takeType) = CallEmitter.DeclareHelperFace(this,
                    RuntimeFaces.GexcTake, LLVMTypeRef.Int32,
                    new[] { StringAbi.PointerType(Context) });
                var got = builder.BuildCall2(takeType, takeFn, new[] { nameSlot }, "gexc.take");
                var has = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, got,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false), "gexc.has");
                builder.BuildCondBr(has, bodyBlock, done);
                builder.PositionAtEnd(bodyBlock);
                // 类型全名是 TypeInfo.name 的借用拷出（IMMORTAL 字面量块，
                // 不 acquire 不 release——init 体内的字段赋值 acquire 对
                // IMMORTAL 是 no-op）
                var name = builder.BuildLoad2(StringAbi.ValueType(Context), nameSlot,
                    "gexc.type");
                var emptySlots = new Dictionary<string, (LLVMValueRef Slot, MirLocal Local)>(
                    System.StringComparer.Ordinal);
                var fat = NewEmitter.EmitAllocAndInit(this, builder, emptySlots,
                    excType.Canonical, wrapper, init, new[] { name });
                builder.BuildCall2(dispatchFn.Type, dispatchFn.Value, new[] { fat }, "");
                // 构造侧 +1 归还（dispatch 形参借用；release 触发析构时
                // 异常类型非 DISPOSABLE，不会回流事件）
                ArcEmitter.EmitReleaseFatValue(this, builder, fat);
                EmitEntryPendingCheck(builder, stub, reporterAvailable, takeEntry);
                builder.BuildBr(loop);
                builder.PositionAtEnd(done);
            }

            // 类级 .generic.X 初值：从 .this 隐藏 typeid 字段装入（不进调用约定）
            private void EmitClassTypeIdPrologue(LLVMBuilderRef builder, EmittedFunction emitted,
                Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots)
            {
                var owner = emitted.Mir.Symbol.Owner;
                if (owner == null || !slots.ContainsKey(".this"))
                {
                    return;
                }
                // 嵌套类外层宿主链 GP 兜底（review-20260910 #02）：帧形参含
                // 外层宿主 GP，但 class 实例只物化自身隐藏 typeid 槽——外层
                // 槽无实例来源，落 core::Any sheet 常量（对齐 VM
                // AlignGenericHiddenArgs 未捕获回落 .any 口径；值类型静态
                // 成员分支同先例）。仅 class 宿主：值类型实例成员的类级
                // typeid 是真实 LLVM 参数（参数落槽循环已存），值类型静态
                // 成员已在该循环落 Any
                if (!GenericAbi.IsValueTypeOwner(owner))
                {
                    foreach (var parameter in emitted.Mir.Parameters)
                    {
                        if (!parameter.Name.StartsWith(".generic.",
                                System.StringComparison.Ordinal)
                            || owner.Declaration.GenericParameters.Contains(
                                parameter.Name.Substring(".generic.".Length))
                            || !GenericAbi.IsClassLevelTypeId(Symbols, emitted.Mir.Symbol,
                                parameter.Name)
                            || !slots.ContainsKey(parameter.Name))
                        {
                            continue;
                        }
                        builder.BuildStore(TypeSheetFor("core::Any"),
                            slots[parameter.Name].Slot);
                    }
                }
                if (owner.Declaration.GenericParameters.Count == 0
                    || Layout?.Find(GenericAbi.PlanKey(owner)) is not { } plan)
                {
                    return;
                }
                // G1：值类型宿主无对象头隐藏槽（类级 typeid 调用点直传，
                // 参数已落槽）——不得对值类型 .this（裸指针）做胖引用解包
                if (plan.HiddenTypeIdSlots.Count == 0 && owner.Canonical != TypeLayout.ArrayTypeCanonical)
                {
                    return;
                }
                var fat = LoadLocal(builder, slots, new MirLocalOperand(".this"));
                foreach (var paramName in owner.Declaration.GenericParameters)
                {
                    var localName = ".generic." + paramName;
                    if (!slots.ContainsKey(localName))
                    {
                        continue;
                    }
                    var ptr = TypeIdEmitter.ReadClassArgument(this, builder, fat, GenericAbi.PlanKey(owner), paramName);
                    builder.BuildStore(ptr, slots[localName].Slot);
                }
            }
        }
    }
}
