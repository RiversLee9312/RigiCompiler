using System.Globalization;
using System.IO;
using System.Text;

namespace RigiCompiler.Bil.Vm
{
    public sealed partial class VmContext
    {
        // Disposal 职责；与主文件共享同一类型、字段及生命周期。

        // ===== MW12b §25.2 VM 半场：IDisposable 销毁时检查 + 事件派发 =====

        // type 的 core::IDisposable.dispose 槽目标 impl 符号——判定口径与
        // native CollectDisposeImplementations 对齐：实现闭包含 IDisposable
        // 的 class，取 iMap 段基址（InterfaceBase 拍平含继承条目，等价
        // native 沿 BasePlan 链上查）+ 接口壳内相对 offset 的槽实现；
        // wrapper 烘焙外移体/async stub 天然兼容（槽目标即其符号）
        internal string? DisposeSlotTargetOf(string typeRef)
        {
            var declaration = FindType(typeRef);
            var key = declaration != null
                ? VmTypeSheetBuilder.TypeKeyOf(declaration) : typeRef;
            lock (_disposeSlotLock)
            {
                if (_disposeSlotTargets.TryGetValue(key, out var cached))
                {
                    return cached;
                }
            }
            var target = ResolveDisposeSlotTarget(declaration, typeRef);
            lock (_disposeSlotLock)
            {
                _disposeSlotTargets[key] = target;
            }
            return target;
        }

        private string? ResolveDisposeSlotTarget(BilTypeDeclaration? declaration,
            string typeRef)
        {
            if (declaration == null || IsValueType(typeRef))
            {
                return null;
            }
            var disposable = FindType(DisposableCanonical);
            if (disposable == null)
            {
                return null;   // 无 stdlib 的合成模块（单元测试形态）
            }
            var sheet = SheetOf(declaration.Symbol);
            var disposableSheet = SheetOf(disposable.Symbol);
            if (sheet == null || disposableSheet == null
                || !sheet.InterfaceBase.TryGetValue(
                       VmTypeSheetBuilder.TypeKeyOf(disposable), out var baseOffset))
            {
                return null;
            }
            // 接口壳内 dispose 的相对 offset（接口自身 sheet 的槽下标）
            string? disposeSymbol = null;
            foreach (var member in disposable.Members)
            {
                if (member is BilSimpleMemberDeclaration simple
                    && VmTypeSheetBuilder.SignatureKeyOf(simple.Symbol)
                        .StartsWith("dispose()@", StringComparison.Ordinal))
                {
                    disposeSymbol = simple.Symbol;
                    break;
                }
            }
            if (disposeSymbol == null
                || !disposableSheet.OffsetBySymbol.TryGetValue(disposeSymbol,
                       out var relative))
            {
                return null;
            }
            var slotIndex = baseOffset + relative;
            if (slotIndex < 0 || slotIndex >= sheet.Slots.Count)
            {
                return null;
            }
            return sheet.Slots[slotIndex].ImplSymbol;
        }

        // dispose 进入即置位（调用侧：BilInvokeExecution.InvokeResolved /
        // PushSuperFrame 压帧前）。按方法符号身份判定——impl 恰为 receiver
        // 实际类型的 dispose 槽目标；调用了但抛异常也算负责过（与 native
        // prologue 置位同语义），async dispose 的 stub 进入即命中
        internal void MarkDisposedIfDisposeImpl(string implSymbol, VmObject receiver)
        {
            if (!receiver.IsDisposalTracked || receiver.DisposedMarked)
            {
                return;
            }
            var target = DisposeSlotTargetOf(receiver.TypeRef);
            if (target != null
                && string.Equals(target, implSymbol, StringComparison.Ordinal))
            {
                receiver.DisposedMarked = true;
            }
        }

        // 派发时机对齐 native entry stub：main/drain 之后、失败汇总之前。
        // 逼 GC（Collect + WaitForPendingFinalizers）让未 dispose 对象的
        // finalizer 入队事件，再逐条经 VM 真构造 UndisposedResourceException
        //（走真 init——同 LanguageException 的正常派发调用机制，此处借
        // 一次性协程同步驱动）并调 GlobalExceptionHandler.dispatch。
        // dispatch 内抛出的异常作为返回值上交，走 Run 的未捕获异常归宿。
        // VM 静态槽（_statics/_singletons）保持根住：不模拟静态槽退出
        // 清理的销毁检查——native 侧晚到事件走 C 默认打印（atexit flush），
        // 两宿主 stdout 都不产生静态末批事件，对拍安全
        internal VmException? CollectAndDispatchUndisposed()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var pending = UndisposedTracker.DrainAll();
            if (pending.Count == 0)
            {
                return null;
            }
            var dispatch = FindRuntimeFunction(
                "core::GlobalExceptionHandler$.static.dispatch(");
            if (dispatch == null)
            {
                return null;   // 无 stdlib 的合成模块无事件通道
            }
            var coroutine = new VmCoroutine(Dispatch);
            coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Running);
            coroutine.PushFrame(SingletonBootstrapFunction, Array.Empty<VmValue>(), null, this);
            foreach (var typeName in pending)
            {
                try
                {
                    var exception = AllocateObject("core::UndisposedResourceException");
                    if (TryFindInit("core::UndisposedResourceException",
                            new VmValue[] { new VmString(typeName) },
                            new[] { ".string" }, out var init)
                        && init.Length > 0)
                    {
                        BilInvokeExecution.InvokeValues(this, coroutine, init,
                            new VmValue[] { exception, new VmString(typeName) },
                            resultSlot: null);
                        StepBackTo(coroutine, 1);
                    }
                    else
                    {
                        // 防御性回落（init 匹配落空）：直写字段，同
                        // LanguageException 回落路径口径
                        exception.WriteField("core::Exception#message@.string",
                            new VmString("对象在销毁前从未调用 dispose()：" + typeName));
                        exception.WriteField(
                            "core::UndisposedResourceException#resourceType@.string",
                            new VmString(typeName));
                    }
                    if (coroutine.State != VmCoroutineState.Running)
                    {
                        return coroutine.Failure
                            ?? new VmException("undisposed 事件构造失败：" + typeName);
                    }
                    BilInvokeExecution.InvokeValues(this, coroutine, dispatch.Symbol,
                        new VmValue[] { exception }, resultSlot: null);
                    StepBackTo(coroutine, 1);
                    if (coroutine.State != VmCoroutineState.Running)
                    {
                        return coroutine.Failure
                            ?? new VmException("undisposed 事件派发失败：" + typeName);
                    }
                }
                catch (VmException failure)
                {
                    return failure;
                }
            }
            return null;
        }

        // 同步驱动协程回落至指定栈深（ConstructSingleton 同式；dispatch
        // 与 stdlib init 均为同步 fn，handler 是同步 Action）
        private void StepBackTo(VmCoroutine coroutine, int depth)
        {
            while (coroutine.CallStack.Count > depth
                && coroutine.State == VmCoroutineState.Running
                && !coroutine.HasAbruptCompletion)
            {
                coroutine.Step(this);
            }
        }

        // 同签名 init 重载的精确甄别（init(text: String) vs
        // init(typeName: String)）：TryFindInit 按声明序先中前者，
        // 这里按首形参名锁定目标重载，其余匹配规则与 TryFindInit 一致
        private bool TryFindInitByFirstParamName(string typeRef, string firstParamName,
            IReadOnlyList<VmValue> arguments, IReadOnlyList<string> argumentStaticTypes,
            out string initSymbol)
        {
            initSymbol = "";
            var declaration = FindType(typeRef);
            if (declaration == null)
            {
                return false;
            }
            foreach (var member in declaration.Members)
            {
                if (member is not BilSimpleMemberDeclaration simple
                    || !HasKeyword(simple, BilKeyword.Init)
                    || !BilVerificationContext.TryParseMethodSymbol(simple.Symbol,
                        out _, out _, out var parameters, out _)
                    || parameters.Count == 0
                    || parameters[0].Name != firstParamName
                    || !ParametersMatch(parameters, arguments, argumentStaticTypes))
                {
                    continue;
                }
                initSymbol = simple.Symbol;
                return true;
            }
            return false;
        }

    }
}
