namespace RigiCompiler.Bil.Vm
{
    // Coroutine（BIL_VM_DESIGN §4.1 / RUNTIME §17 / BIL_STANDARD §16）：
    // 调用帧链 + 块执行栈 + 逻辑状态机；状态以 Interlocked 原子转换。
    // V4：块帧携带 loop/switch/try 区域状态；控制流经压/弹块帧与
    // 待处理 completion 展开，不递归 C# 调用栈。

    public enum VmCoroutineState
    {
        Created = 0,
        Runnable = 1,
        Running = 2,
        Suspended = 3,
        Completed = 4,
        Failed = 5,
        Cancelled = 6,
    }

    public enum VmRegionKind
    {
        Plain,
        Loop,
        Switch,
        Try,
        If,
        Call,
    }

    public enum VmLoopPhase
    {
        Judge,
        Body,
        Enumerator,
    }

    public enum VmTryPhase
    {
        Body,
        Catch,
        Finally,
    }

    public enum VmCompletionKind
    {
        Normal,
        Return,
        Throw,
        Break,
        Continue,
    }

    public sealed class VmCompletion
    {
        public VmCompletionKind Kind { get; }
        public VmValue? Value { get; }
        public VmException? Failure { get; }
        public VmBreakId? BreakId { get; }

        private VmCompletion(VmCompletionKind kind, VmValue? value, VmException? failure,
            VmBreakId? breakId)
        {
            Kind = kind;
            Value = value;
            Failure = failure;
            BreakId = breakId;
        }

        public static readonly VmCompletion Normal = new VmCompletion(
            VmCompletionKind.Normal, null, null, null);

        public static VmCompletion Return(VmValue? value) =>
            new VmCompletion(VmCompletionKind.Return, value, null, null);

        public static VmCompletion Throw(VmException failure) =>
            new VmCompletion(VmCompletionKind.Throw, failure.ExceptionObject, failure, null);

        public static VmCompletion Break(VmBreakId breakId) =>
            new VmCompletion(VmCompletionKind.Break, null, null, breakId);

        public static VmCompletion Continue(VmBreakId breakId) =>
            new VmCompletion(VmCompletionKind.Continue, null, null, breakId);

        public bool IsAbrupt => Kind != VmCompletionKind.Normal;
    }

    public sealed class VmBlockFrame
    {
        public BilBlock? Block { get; }
        public int InstructionIndex { get; set; }
        public VmRegionKind Kind { get; }
        public LoopInstruction? Loop { get; private set; }
        public VmLoopPhase LoopPhase { get; set; }
        public SwitchInstruction? Switch { get; private set; }
        public TryInstruction? Try { get; private set; }
        public VmTryPhase TryPhase { get; set; }
        public IfInstruction? If { get; private set; }
        public CallBlockInstruction? Call { get; private set; }
        public VmCompletion? SavedCompletion { get; set; }
        public VmBreakId? BreakId { get; set; }

        public bool IsRegion => Kind != VmRegionKind.Plain;

        private VmBlockFrame(BilBlock? block, VmRegionKind kind)
        {
            Block = block;
            Kind = kind;
            InstructionIndex = 0;
        }

        public static VmBlockFrame Plain(BilBlock block) =>
            new VmBlockFrame(block, VmRegionKind.Plain);

        public static VmBlockFrame LoopRegion(LoopInstruction instruction)
        {
            return new VmBlockFrame(null, VmRegionKind.Loop) { Loop = instruction };
        }

        public static VmBlockFrame SwitchRegion(SwitchInstruction instruction)
        {
            return new VmBlockFrame(null, VmRegionKind.Switch) { Switch = instruction };
        }

        public static VmBlockFrame TryRegion(TryInstruction instruction)
        {
            return new VmBlockFrame(null, VmRegionKind.Try)
            {
                Try = instruction,
                TryPhase = VmTryPhase.Body,
            };
        }

        public static VmBlockFrame IfRegion(IfInstruction instruction)
        {
            return new VmBlockFrame(null, VmRegionKind.If) { If = instruction };
        }

        public static VmBlockFrame CallRegion(CallBlockInstruction instruction)
        {
            return new VmBlockFrame(null, VmRegionKind.Call) { Call = instruction };
        }
    }

    public sealed class VmCallFrame
    {
        public BilFunction Function { get; }
        public Dictionary<string, VmValue> Slots { get; }
        public string? ResultSlot { get; }
        public Stack<VmBlockFrame> BlockStack { get; }

        public VmCallFrame(BilFunction function, string? resultSlot)
        {
            Function = function;
            ResultSlot = resultSlot;
            Slots = new Dictionary<string, VmValue>();
            BlockStack = new Stack<VmBlockFrame>();
        }
    }

    public sealed class VmCoroutine
    {
        private int _state = (int)VmCoroutineState.Created;
        private VmCompletion? _pending;
        private VmContext? _context;
        private VmTask? _awaitTask;
        private string? _awaitResultSlot;
        private VmValue? _pollingAlarm;
        private int _pollBackoffMs = 1;
        private Timer? _pollTimer;
        // 唤醒纪元：每次成功挂起（Running→Suspended）递增一次，
        // 标识「第几次挂起」。唤醒方登记时捕获当前纪元，发布失败时以
        // 「仍 Suspended 且纪元未变」区分唤醒丢失与 benign 竞态
        // （见 VmExecutor.PublishWakeup）。
        private long _wakeupEpoch;

        public VmExecutor BoundExecutor { get; }
        public VmTask Task { get; }
        // 执行所有权锁：Execute 的「Runnable→Running 转换 + Settle + Step
        // 循环」整体在此锁内，保证任一时刻至多一个 worker 执行本协程
        // （handoff 单所有者，见 VmExecutor.Execute 与 BIL_VM_DESIGN §4.2）
        internal object SyncRoot { get; } = new object();
        public Stack<VmCallFrame> CallStack { get; }
        // wrapper 派发上下文栈：invoke fn(..inner) 在执行期解析「下一环」
        // （BIL §15.4 / SYNTAX §14.3：set 链 outer→inner 的下一环落点）
        internal Stack<VmWrapperDispatchFrame> WrapperDispatch { get; } =
            new Stack<VmWrapperDispatchFrame>();
        // wrapper get 链延续：getter 返回后把结果过链再写真实目标
        // （§13.3 读路径序：backing → getter → wrapper 链）
        internal sealed class PendingGetChain
        {
            public string TempSlot { get; }
            public string? TargetSlot { get; }
            public VmValue Host { get; }
            public string FieldSymbol { get; }
            public string ElementType { get; }
            public IReadOnlyList<string> Wrappers { get; }
            public IReadOnlyList<string> EntityWrappers { get; }
            public int Depth { get; }

            public PendingGetChain(string tempSlot, string? targetSlot, VmValue host,
                string fieldSymbol, string elementType, IReadOnlyList<string> wrappers,
                IReadOnlyList<string> entityWrappers, int depth)
            {
                TempSlot = tempSlot;
                TargetSlot = targetSlot;
                Host = host;
                FieldSymbol = fieldSymbol;
                ElementType = elementType;
                Wrappers = wrappers;
                EntityWrappers = entityWrappers;
                Depth = depth;
            }
        }

        private readonly List<PendingGetChain> _pendingGetChains = new List<PendingGetChain>();
        private int _getChainCounter;

        public VmValue? Result { get; private set; }
        public VmException? Failure { get; private set; }

        public VmCoroutineState State => (VmCoroutineState)Volatile.Read(ref _state);

        // 当前唤醒纪元（只读视图；递增只发生在 TrySuspend 内）
        public long WakeupEpoch => Volatile.Read(ref _wakeupEpoch);

        public bool HasAbruptCompletion => _pending != null && _pending.IsAbrupt;

        public VmCoroutine(VmExecutor boundExecutor, string taskTypeRef)
        {
            BoundExecutor = boundExecutor;
            Task = new VmTask(taskTypeRef);
            CallStack = new Stack<VmCallFrame>();
        }

        public VmCallFrame CurrentFrame => CallStack.Peek();

        public bool TryTransition(VmCoroutineState expected, VmCoroutineState next)
        {
            return Interlocked.CompareExchange(ref _state, (int)next, (int)expected) == (int)expected;
        }

        // 统一挂起点：CAS Running→Suspended，成功后递增唤醒纪元。
        // 全部进入 Suspended 的转换必须走此方法（VmTask/VmEventAlarm 的
        // TryAwait 锁内 CAS、ProbePolling 轮询挂起），保证「纪元」与
        // 「挂起次数」严格一一对应，唤醒丢失判定才有依据。
        public bool TrySuspend()
        {
            if (!TryTransition(VmCoroutineState.Running, VmCoroutineState.Suspended))
            {
                return false;
            }
            Interlocked.Increment(ref _wakeupEpoch);
            return true;
        }

        public VmValue ReadVar(string name)
        {
            if (!CurrentFrame.Slots.TryGetValue(name, out var value))
            {
                throw new VmException("未初始化变量 $" + name);
            }
            return value;
        }

        public void WriteVar(string name, VmValue value)
        {
            CurrentFrame.Slots[name] = value;
        }

        public void PushFrame(BilFunction function, IReadOnlyList<VmValue> arguments, string? resultSlot)
        {
            var frame = new VmCallFrame(function, resultSlot);
            var parameters = new List<BilArgDeclaration>();
            foreach (var arg in function.Args)
            {
                if (arg.Name != ".return")
                {
                    parameters.Add(arg);
                }
            }
            if (parameters.Count != arguments.Count)
            {
                throw new VmException("实参个数不匹配：" + function.Symbol
                    + "（期望 " + parameters.Count + "，实际 " + arguments.Count + "）");
            }
            for (var i = 0; i < parameters.Count; i++)
            {
                // .this 是 receiver：值类型方法必须原地可变（§7.3），
                // 不得按普通实参深拷贝，否则 init/实例方法写入会丢失。
                frame.Slots[parameters[i].Name] = parameters[i].Name == ".this"
                    ? arguments[i]
                    : arguments[i].Copy();
            }
            frame.BlockStack.Push(VmBlockFrame.Plain(FindEntrypointBlock(function)));
            CallStack.Push(frame);
        }

        // 注册 getter 返回后过 wrapper 链的延续；Depth = getter 帧压入前的
        // 调用栈深度；返回临时结果槽名
        internal string RegisterPendingGetChain(string? targetSlot, VmValue host,
            string fieldSymbol, string elementType, IReadOnlyList<string> wrappers,
            IReadOnlyList<string> entityWrappers)
        {
            var temp = ".getchain." + (_getChainCounter++);
            _pendingGetChains.Add(new PendingGetChain(temp, targetSlot, host, fieldSymbol,
                elementType, wrappers, entityWrappers, CallStack.Count));
            return temp;
        }

        private void DiscardStalePendingGetChains()
        {
            var depth = CallStack.Count;
            _pendingGetChains.RemoveAll(pending => pending.Depth >= depth);
        }

        public void ReturnFromFrame(VmContext? context, VmValue? value)
        {
            if (CallStack.Count == 0)
            {
                throw new VmException("ret 时调用帧链为空");
            }
            var frame = CallStack.Pop();
            var result = value ?? VmVoid.Instance;
            if (CallStack.Count == 0)
            {
                Result = result;
                Task.Complete(result);
                if (!TryTransition(VmCoroutineState.Running, VmCoroutineState.Completed))
                {
                    Interlocked.Exchange(ref _state, (int)VmCoroutineState.Completed);
                }
                BoundExecutor.NotifyTerminal(this);
                return;
            }
            if (frame.ResultSlot != null)
            {
                if (context != null
                    && TryCompletePendingGetChain(context, frame.ResultSlot, result.Copy()))
                {
                    return;
                }
                WriteVar(frame.ResultSlot, result.Copy());
            }
        }

        private bool TryCompletePendingGetChain(VmContext context, string resultSlot,
            VmValue result)
        {
            PendingGetChain? pending = null;
            var index = -1;
            for (var i = 0; i < _pendingGetChains.Count; i++)
            {
                if (_pendingGetChains[i].TempSlot == resultSlot
                    && _pendingGetChains[i].Depth == CallStack.Count)
                {
                    pending = _pendingGetChains[i];
                    index = i;
                    break;
                }
            }
            if (pending == null)
            {
                return false;
            }
            _pendingGetChains.RemoveAt(index);
            if (pending.Wrappers.Count > 0)
            {
                if (!VmWrapperDispatch.ApplyGetChain(context, this, pending.Host,
                        pending.FieldSymbol, pending.ElementType, pending.Wrappers, result,
                        out var final))
                {
                    return true;
                }
                if (pending.TargetSlot != null)
                {
                    WriteVar(pending.TargetSlot, final);
                }
                return true;
            }
            if (pending.EntityWrappers.Count > 0)
            {
                if (!VmWrapperDispatch.ApplyEntityGetChain(context, this, pending.Host,
                        pending.FieldSymbol, pending.ElementType, pending.EntityWrappers,
                        result, out var entityFinal))
                {
                    return true;
                }
                if (pending.TargetSlot != null)
                {
                    WriteVar(pending.TargetSlot, entityFinal);
                }
                return true;
            }
            if (pending.TargetSlot != null)
            {
                WriteVar(pending.TargetSlot, result);
            }
            return true;
        }

        public void Fail(Exception exception)
        {
            if (State is VmCoroutineState.Failed or VmCoroutineState.Completed
                or VmCoroutineState.Cancelled)
            {
                return;
            }
            var vmException = exception as VmException
                ?? new VmException(exception.Message, inner: exception);
            Failure = vmException;
            Task.Fail(vmException);
            Interlocked.Exchange(ref _state, (int)VmCoroutineState.Failed);
            _pending = null;
            BoundExecutor.NotifyTerminal(this);
        }

        public void AwaitTask(VmTask task, string? resultSlot)
        {
            _awaitTask = task;
            _awaitResultSlot = resultSlot;
            if (task.TryAwait(this, out var state, out var result, out var exception))
            {
                return;
            }
            _awaitTask = null;
            _awaitResultSlot = null;
            ApplyTaskOutcome(state, result, exception, resultSlot);
        }

        public void YieldBare()
        {
            BoundExecutor.Publish(this);
        }

        public void YieldAlarm(VmContext context, VmValue alarm)
        {
            if (VmTypeOps.Is(context, this, alarm, "core.coroutine::EventAlarm"))
            {
                if (alarm is not VmEventAlarm eventAlarm)
                {
                    throw new VmException("yield EventAlarm 仅支持 sleep 产生的运行时 Alarm");
                }
                if (eventAlarm.TryAwait(this))
                {
                    return;
                }
                BoundExecutor.Publish(this);
                return;
            }
            if (VmTypeOps.Is(context, this, alarm, "core.coroutine::PollingAlarm"))
            {
                _pollingAlarm = alarm;
                _pollBackoffMs = 1;
                BoundExecutor.Publish(this);
                return;
            }
            throw new VmException("yield 操作数不是 Alarm：" + alarm.TypeRef);
        }

        public bool SettleAfterResume(VmContext context)
        {
            _context = context;
            if (_awaitTask != null)
            {
                var task = _awaitTask;
                var slot = _awaitResultSlot;
                _awaitTask = null;
                _awaitResultSlot = null;
                task.Observe(out var state, out var result, out var exception);
                ApplyTaskOutcome(state, result, exception, slot);
                return State == VmCoroutineState.Running;
            }
            if (_pollingAlarm != null)
            {
                return ProbePolling(context);
            }
            return true;
        }

        public int TakePollDelay()
        {
            var delay = _pollBackoffMs;
            _pollBackoffMs = Math.Min(_pollBackoffMs * 2, VmPolling.MaxBackoffMs);
            return delay;
        }

        public void AttachTimer(Timer timer)
        {
            var previous = _pollTimer;
            _pollTimer = timer;
            previous?.Dispose();
        }

        // 终态统一释放轮询 timer（AttachTimer 的对偶，由 executor
        // NotifyTerminal 这一终态 choke point 调用）。timer 回调可能正在
        // 执行：只用 Dispose()、不用 Dispose(WaitHandle) 阻塞版；置 null
        // 保证幂等并供测试断言
        internal void DisposePollTimer()
        {
            var timer = _pollTimer;
            _pollTimer = null;
            timer?.Dispose();
        }

        // 是否有在途轮询 timer（测试断言用）
        internal bool HasPollTimer => _pollTimer != null;

        public void PushBlock(BilBlock block)
        {
            CurrentFrame.BlockStack.Push(VmBlockFrame.Plain(block));
        }

        private void ApplyTaskOutcome(VmTaskState state, VmValue? result, VmException? exception,
            string? resultSlot)
        {
            switch (state)
            {
                case VmTaskState.Succeeded:
                    if (resultSlot != null)
                    {
                        WriteVar(resultSlot, (result ?? VmVoid.Instance).Copy());
                    }
                    break;
                case VmTaskState.Failed:
                    Complete(VmCompletion.Throw(exception ?? new VmException("Task 失败")));
                    break;
                case VmTaskState.Cancelled:
                    Complete(VmCompletion.Throw(new VmException("Task 已取消")));
                    break;
                default:
                    throw new VmException("恢复时 Task 仍未终态");
            }
        }

        private bool ProbePolling(VmContext context)
        {
            var alarm = _pollingAlarm
                ?? throw new VmException("PollingAlarm 探测缺少对象");
            var function = context.ResolveDispatch(VmPolling.IsReadySymbol, alarm);
            if (function == null)
            {
                throw new VmException("PollingAlarm 没有 isReady");
            }
            var depth = CallStack.Count;
            PushFrame(function, new[] { alarm }, VmPolling.ReadySlot);
            while (CallStack.Count > depth && State == VmCoroutineState.Running)
            {
                Step(context);
            }
            if (State != VmCoroutineState.Running)
            {
                return false;
            }
            if (!CurrentFrame.Slots.ContainsKey(VmPolling.ReadySlot))
            {
                _pollingAlarm = null;
                return true;
            }
            var ready = ReadVar(VmPolling.ReadySlot) is VmBool flag && flag.Value;
            if (ready)
            {
                _pollingAlarm = null;
                return true;
            }
            if (!TrySuspend())
            {
                throw new VmException("PollingAlarm 未就绪时无法挂起");
            }
            BoundExecutor.SchedulePoll(this);
            return false;
        }

        public void Complete(VmCompletion completion)
        {
            _pending = completion;
            Unwind();
        }

        public void Step(VmContext context)
        {
            _context = context;
            if (_pending != null)
            {
                Unwind();
                return;
            }
            if (CallStack.Count == 0)
            {
                if (!TryTransition(VmCoroutineState.Running, VmCoroutineState.Completed))
                {
                    Interlocked.Exchange(ref _state, (int)VmCoroutineState.Completed);
                }
                BoundExecutor.NotifyTerminal(this);
                return;
            }
            var frame = CurrentFrame;
            if (frame.BlockStack.Count == 0)
            {
                throw new VmException("块执行栈为空：" + frame.Function.Symbol);
            }
            var blockFrame = frame.BlockStack.Peek();
            if (blockFrame.IsRegion)
            {
                Complete(VmCompletion.Normal);
                return;
            }
            if (blockFrame.Block == null)
            {
                throw new VmException("普通块帧缺少 block：" + frame.Function.Symbol);
            }
            if (blockFrame.InstructionIndex >= blockFrame.Block.Instructions.Count)
            {
                frame.BlockStack.Pop();
                if (frame.BlockStack.Count == 0)
                {
                    throw new VmException("fn " + frame.Function.Symbol
                        + " 的 block 落到末尾且无 ret");
                }
                if (frame.BlockStack.Peek().IsRegion)
                {
                    Complete(VmCompletion.Normal);
                }
                return;
            }
            var instruction = blockFrame.Block.Instructions[blockFrame.InstructionIndex];
            blockFrame.InstructionIndex++;
            try
            {
                instruction.Execute(context, this);
            }
            catch (VmException exception) when (exception.ExceptionObject != null)
            {
                Complete(VmCompletion.Throw(exception));
            }
        }

        // 结构化 region 的 breakid 统一绑定（§16.5 推广：loop/switch/
        // try/if/call 共用）：创建 VmBreakId 句柄回挂 region 帧，并写入
        // 指令声明的 .breakid 变量
        private void BindRegionBreakId(VmBlockFrame region, BilVariableOperand breakIdOperand,
            bool allowsContinue)
        {
            var breakId = new VmBreakId(region, allowsContinue);
            region.BreakId = breakId;
            WriteVar(breakIdOperand.Name, breakId);
        }

        public void EnterLoop(LoopInstruction instruction)
        {
            var region = VmBlockFrame.LoopRegion(instruction);
            BindRegionBreakId(region, instruction.BreakId, allowsContinue: true);
            CurrentFrame.BlockStack.Push(region);
            region.LoopPhase = instruction.IsRev ? VmLoopPhase.Body : VmLoopPhase.Judge;
            PushLoopPhase(region);
        }

        public void EnterSwitch(SwitchInstruction instruction, int itemIndex)
        {
            var region = VmBlockFrame.SwitchRegion(instruction);
            BindRegionBreakId(region, instruction.BreakId, allowsContinue: false);
            CurrentFrame.BlockStack.Push(region);
            var target = itemIndex >= 0
                ? instruction.ItemBlocks[itemIndex]
                : instruction.DefaultBlock;
            PushBlock(target);
        }

        public void EnterTry(TryInstruction instruction)
        {
            var region = VmBlockFrame.TryRegion(instruction);
            BindRegionBreakId(region, instruction.BreakId, allowsContinue: false);
            CurrentFrame.BlockStack.Push(region);
            PushBlock(instruction.Body);
        }

        public void EnterIf(IfInstruction instruction, bool condition)
        {
            var region = VmBlockFrame.IfRegion(instruction);
            BindRegionBreakId(region, instruction.BreakId, allowsContinue: false);
            CurrentFrame.BlockStack.Push(region);
            // false 且无 else：不压 child——region 裸顶由 Step 的
            // IsRegion 分支 Normal 结束
            if (condition)
            {
                PushBlock(instruction.ThenBlock);
                return;
            }
            if (instruction.ElseBlock != null)
            {
                PushBlock(instruction.ElseBlock);
            }
        }

        public void EnterCall(CallBlockInstruction instruction)
        {
            var region = VmBlockFrame.CallRegion(instruction);
            BindRegionBreakId(region, instruction.BreakId, allowsContinue: false);
            CurrentFrame.BlockStack.Push(region);
            PushBlock(instruction.Block);
        }

        private void Unwind()
        {
            while (_pending != null && State == VmCoroutineState.Running)
            {
                if (CallStack.Count == 0)
                {
                    throw new VmException("展开时调用帧链为空");
                }
                var frame = CurrentFrame;
                if (frame.BlockStack.Count == 0)
                {
                    ExitCallFrame(_context);
                    continue;
                }
                var top = frame.BlockStack.Peek();
                if (top.Kind == VmRegionKind.Try)
                {
                    HandleTryRegion(top);
                    continue;
                }
                if (top.Kind == VmRegionKind.Loop)
                {
                    HandleLoopRegion(top);
                    continue;
                }
                if (top.Kind == VmRegionKind.Switch || top.Kind == VmRegionKind.If
                    || top.Kind == VmRegionKind.Call)
                {
                    HandleBreakableRegion(top);
                    continue;
                }
                frame.BlockStack.Pop();
            }
        }

        private void ExitCallFrame(VmContext? context)
        {
            var pending = _pending ?? VmCompletion.Normal;
            if (pending.Kind == VmCompletionKind.Return)
            {
                // 先清 pending，再写回结果（可能同步推进 get 链 proxy）
                _pending = null;
                ReturnFromFrame(context, pending.Value);
                return;
            }
            if (pending.Kind == VmCompletionKind.Throw)
            {
                if (CallStack.Count <= 1)
                {
                    _pendingGetChains.Clear();
                    Fail(pending.Failure ?? new VmException("未捕获异常", pending.Value));
                    return;
                }
                CallStack.Pop();
                DiscardStalePendingGetChains();
                return;
            }
            if (pending.Kind is VmCompletionKind.Break or VmCompletionKind.Continue)
            {
                throw new VmException("break/continue 越过函数边界");
            }
            throw new VmException("fn " + CurrentFrame.Function.Symbol
                + " 的 block 落到末尾且无 ret");
        }

        private void HandleTryRegion(VmBlockFrame region)
        {
            var instruction = region.Try
                ?? throw new VmException("try 区域缺少指令");
            var pending = _pending ?? VmCompletion.Normal;

            if (region.TryPhase == VmTryPhase.Finally)
            {
                var completion = pending.IsAbrupt
                    ? pending
                    : (region.SavedCompletion ?? VmCompletion.Normal);
                // matching Break 在 finally 完成后于 try 边界消费（finally
                // 自身的 abrupt 覆盖 SavedCompletion，同样参与匹配）
                CurrentFrame.BlockStack.Pop();
                _pending = PropagateAfterRegionPop(region, completion);
                return;
            }

            if (region.TryPhase == VmTryPhase.Body && pending.Kind == VmCompletionKind.Throw
                && pending.Value != null && TryBindCatch(region, pending.Value))
            {
                _pending = null;
                return;
            }

            // §16.7 finally(e) 基础语义：进入 finally 前恒写异常槽——仅
            // pending 为 Throw（带异常对象）时写异常值；Normal/Return/
            // Break/Continue 等一切非 Throw completion 一律写 null
            // （不得遗留上一次的旧值）
            WriteVar(instruction.ExceptionSlot.Name,
                pending.Kind == VmCompletionKind.Throw && pending.Value != null
                    ? pending.Value
                    : VmNull.Instance);

            if (instruction.FinallyBlock != null)
            {
                region.SavedCompletion = pending;
                region.TryPhase = VmTryPhase.Finally;
                _pending = null;
                PushBlock(instruction.FinallyBlock);
                return;
            }

            // 无 finally 直通：matching Break 在 try 边界消费，不匹配则
            // 原样传播（Break 绝不进入 catch matching——上面的绑定条件
            // 限定 Throw）
            CurrentFrame.BlockStack.Pop();
            _pending = PropagateAfterRegionPop(region, pending);
        }

        // region 弹出后的 completion 结算（§16.5 推广）：Break 命中本
        // region 的 breakid → 消费（返回 null，续 region 后下一条）；
        // Normal → 清 pending；其他 abrupt 原样传播。Continue/Return/
        // Throw 不被非 loop region 消费（Continue 不可能匹配——这些
        // region 的 breakid AllowsContinue=false；Return/Throw 无
        // BreakId）
        private VmCompletion? PropagateAfterRegionPop(VmBlockFrame region, VmCompletion completion)
        {
            if (completion.Kind == VmCompletionKind.Break
                && completion.BreakId != null && completion.BreakId.Region == region)
            {
                return null;
            }
            return completion.IsAbrupt ? completion : null;
        }

        private bool TryBindCatch(VmBlockFrame region, VmValue exception)
        {
            var instruction = region.Try
                ?? throw new VmException("try 区域缺少指令");
            if (instruction.CatchTable is not BilCatchTableResource table
                || _context == null)
            {
                return false;
            }
            foreach (var entry in table.Entries)
            {
                if (!VmTypeOps.Is(_context, this, exception, entry.ExceptionType.TypeRef))
                {
                    continue;
                }
                WriteVar(instruction.ExceptionSlot.Name, exception);
                region.TryPhase = VmTryPhase.Catch;
                PushBlock(entry.Handler);
                return true;
            }
            return false;
        }

        private void HandleLoopRegion(VmBlockFrame region)
        {
            var instruction = region.Loop
                ?? throw new VmException("loop 区域缺少指令");
            var pending = _pending ?? VmCompletion.Normal;
            if ((pending.Kind == VmCompletionKind.Break
                    || pending.Kind == VmCompletionKind.Continue)
                && pending.BreakId != null && pending.BreakId.Region == region)
            {
                if (pending.Kind == VmCompletionKind.Break)
                {
                    CurrentFrame.BlockStack.Pop();
                    _pending = null;
                    return;
                }
                _pending = null;
                AdvanceLoopAfterBody(region, instruction);
                return;
            }

            if (pending.Kind != VmCompletionKind.Normal)
            {
                CurrentFrame.BlockStack.Pop();
                return;
            }

            _pending = null;
            if (region.LoopPhase == VmLoopPhase.Judge)
            {
                var condition = ReadVar(instruction.Condition.Name);
                if (condition is not VmBool flag)
                {
                    throw new VmException("loop 条件不是 .bool：" + condition.TypeRef);
                }
                if (instruction.IsRev)
                {
                    if (flag.Value)
                    {
                        region.LoopPhase = VmLoopPhase.Body;
                        PushLoopPhase(region);
                        return;
                    }
                    CurrentFrame.BlockStack.Pop();
                    return;
                }
                if (!flag.Value)
                {
                    CurrentFrame.BlockStack.Pop();
                    return;
                }
                region.LoopPhase = VmLoopPhase.Body;
                PushLoopPhase(region);
                return;
            }

            AdvanceLoopAfterBody(region, instruction);
        }

        private void AdvanceLoopAfterBody(VmBlockFrame region, LoopInstruction instruction)
        {
            if (region.LoopPhase == VmLoopPhase.Body && instruction.EnumBlock != null)
            {
                region.LoopPhase = VmLoopPhase.Enumerator;
                PushLoopPhase(region);
                return;
            }
            region.LoopPhase = VmLoopPhase.Judge;
            PushLoopPhase(region);
        }

        private void PushLoopPhase(VmBlockFrame region)
        {
            var instruction = region.Loop
                ?? throw new VmException("loop 区域缺少指令");
            var block = region.LoopPhase switch
            {
                VmLoopPhase.Judge => instruction.Judge,
                VmLoopPhase.Body => instruction.Body,
                VmLoopPhase.Enumerator => instruction.EnumBlock
                    ?? throw new VmException("loop 无 enumerator block"),
                _ => throw new VmException("未知 loop 相位：" + region.LoopPhase),
            };
            PushBlock(block);
        }

        // switch/if/call region 的展开骨架（§16.5 推广后三者同构）：
        // pop 后经 PropagateAfterRegionPop 结算——matching Break 消费、
        // Normal 清、其他 abrupt 传播
        private void HandleBreakableRegion(VmBlockFrame region)
        {
            var pending = _pending ?? VmCompletion.Normal;
            CurrentFrame.BlockStack.Pop();
            _pending = PropagateAfterRegionPop(region, pending);
        }

        private static BilBlock FindEntrypointBlock(BilFunction function)
        {
            BilBlock? entry = null;
            foreach (var block in function.Blocks)
            {
                if (!block.Modifiers.Contains(BilBlockModifier.Entrypoint))
                {
                    continue;
                }
                if (entry != null)
                {
                    throw new VmException("fn " + function.Symbol + " 有多个 entrypoint block");
                }
                entry = block;
            }
            if (entry == null)
            {
                throw new VmException("fn " + function.Symbol + " 没有 entrypoint block");
            }
            return entry;
        }
    }
}
