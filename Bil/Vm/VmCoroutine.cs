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
        // 随帧保活的宿主调用参数，不属于 wrapper 状态及其字段复制。
        internal VmValue? WrapperSelfArgument { get; set; }
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
        private long _awaitHandle;
        private string? _awaitResultSlot;
        private VmValue? _pollingAlarm;
        private int _pollBackoffMs = 1;
        private Timer? _pollTimer;
        // §19.2 恢复式探测状态（Phase 2.6 语义纠偏）：探测帧已压入且未
        // 弹出标记 + 探测帧之下的基深。探测中途挂起（await/yield/Event）
        // 时帧滞留栈上，唤醒后据此前续探测循环，不再重压新帧
        private bool _probeFrameOnStack;
        private int _probeBaseDepth;

        // MW11c 棒4a（§17.4）：调度逻辑在 Rigi 世界（Dispatcher/Task），
        // 本对象只是执行引擎侧的协程实体：状态机 + 帧链 + 原语桥引用
        internal VmDispatch Dispatch { get; }
        // 调度句柄（0 = ad-hoc：singleton/globals 初始化与桥调用的一次性
        // 协程，不进 Dispatcher 台账）
        public long Handle { get; private set; }
        // 归属 Rigi Task 对象（attachRuntime 由桥触发；ad-hoc 为 null）
        public VmObject? TaskObject { get; private set; }
        // MW11c 棒4b（§17.1/§18.4）：Executor 绑定——创建时继承的默认
        // 绑定（null = MainExecutor）。有效绑定 = Task.executor 预设/换绑
        // 字段 ?? 本默认；恢复发布目标在发布时读取最新绑定（执行段内不
        // 迁移），换绑无需引擎侧动作（VmDispatch.Publish 逐次换算 lane）
        internal VmObject? BoundExecutor { get; set; }
        // CoroutineLocal 绑定栈（§20.2）：顶在列表末尾；get 自顶向下
        // 按键对象身份命中。inherit 只拷每个键的有效顶，不是整段父栈。
        internal List<(VmValue Key, VmValue Value)> Locals { get; } = new();
        // 执行所有权锁：resume 的「Runnable→Running 转换 + Settle + Step
        // 循环」整体在此锁内，保证任一时刻至多一个 worker 执行本协程
        // （handoff 单所有者，见 VmDispatch.ResumeSegment）
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

        public bool HasAbruptCompletion => _pending != null && _pending.IsAbrupt;

        internal VmCoroutine(VmDispatch dispatch)
        {
            Dispatch = dispatch;
            CallStack = new Stack<VmCallFrame>();
        }

        internal void AttachHandle(long handle)
        {
            Handle = handle;
        }

        internal void AttachTaskObject(VmObject taskObject)
        {
            TaskObject = taskObject;
            taskObject.TaskRuntimeState = this;
        }

        // await 登记（VmDispatch.Await 在 task gate 临界区内调用，随后
        // 同一临界区内 TrySuspend）
        private VmCoroutine? _awaitTarget;
        internal void MarkAwaiting(long taskHandle, string? resultSlot, VmCoroutine target)
        {
            _awaitTarget = target;
            _awaitHandle = taskHandle;
            _awaitResultSlot = resultSlot;
        }

        public VmCallFrame CurrentFrame => CallStack.Peek();

        public bool TryTransition(VmCoroutineState expected, VmCoroutineState next)
        {
            return Interlocked.CompareExchange(ref _state, (int)next, (int)expected) == (int)expected;
        }

        // 统一挂起点：CAS Running→Suspended。全部进入 Suspended 的转换
        // 必须走此方法（VmDispatch.Await 的 gate 临界区内、VmEventAlarm
        // 的 TryAwait 锁内、ProbePolling 轮询挂起），挂起与等待登记在
        // 各自的临界区内原子完成，保证唤醒不丢失
        public bool TrySuspend()
        {
            return TryTransition(VmCoroutineState.Running, VmCoroutineState.Suspended);
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

        public void PushFrame(BilFunction function, IReadOnlyList<VmValue> arguments,
            string? resultSlot, VmContext? context = null)
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
            // 类级 .generic.* 未出现在 invoke 实参时，从 .this 构造形态注入
            // （嵌套类外层 GP 还需类型声明/构造点捕获，故 context 透传）
            if (arguments.Count != parameters.Count)
            {
                arguments = VmContext.AlignGenericHiddenArgs(function, arguments, context);
            }
            if (parameters.Count != arguments.Count)
            {
                throw new VmException("实参个数不匹配：" + function.Symbol
                    + "（期望 " + parameters.Count + "，实际 " + arguments.Count + "）");
            }
            for (var i = 0; i < parameters.Count; i++)
            {
                if (parameters[i].Name == ".this" && arguments[i] is VmWrapperReceiver receiver)
                    frame.WrapperSelfArgument = receiver.SelfArgument;
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
                if (!TryTransition(VmCoroutineState.Running, VmCoroutineState.Completed))
                {
                    Interlocked.Exchange(ref _state, (int)VmCoroutineState.Completed);
                }
                Dispatch.OnTerminal(this);
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
            Interlocked.Exchange(ref _state, (int)VmCoroutineState.Failed);
            _pending = null;
            Dispatch.OnTerminal(this);
        }

        public void YieldBare()
        {
            Dispatch.Publish(this, "yield");
        }

        public void YieldAlarm(VmContext context, VmValue alarm)
        {
            if (VmTypeOps.Is(context, this, alarm, "core.coroutine::EventAlarm"))
            {
                // 棒5a：Timer 与 sleep 的 SleepAlarm 统一走 VM 侧定时器
                // 记录通道（EventAlarm 基类 handle 字段 → 排程/waiter/
                // signaled；L8 起 handle==0 的用户直继子类在
                // TryAwaitTimer 内懒建手动事件粘滞底座并回写）；
                // VmEventAlarm 专用表示已退役
                if (alarm is VmObject alarmObject)
                {
                    if (Dispatch.TryAwaitTimer(alarmObject, this))
                    {
                        return;
                    }
                    Dispatch.Publish(this, "yield EventAlarm（已触发）");
                    return;
                }
                throw new VmException("yield EventAlarm 仅支持 sleep/Timer 产生的运行时 Alarm");
            }
            if (VmTypeOps.Is(context, this, alarm, "core.coroutine::PollingAlarm"))
            {
                _pollingAlarm = alarm;
                _pollBackoffMs = 1;
                Dispatch.Publish(this, "yield PollingAlarm");
                return;
            }
            throw new VmException("yield 操作数不是 Alarm：" + alarm.TypeRef);
        }

        public bool SettleAfterResume(VmContext context)
        {
            _context = context;
            if (_awaitHandle != 0)
            {
                var handle = _awaitHandle;
                var slot = _awaitResultSlot;
                _awaitHandle = 0;
                _awaitResultSlot = null;
                try { Dispatch.SettleAwait(this, handle, slot); }
                finally { _awaitTarget = null; }
                if (_pollingAlarm == null)
                {
                    return State == VmCoroutineState.Running;
                }
                // 探测帧内 await 挂起后的唤醒（§19.2 恢复式探测）：结算已
                // 完成，探测帧仍在栈上——落入下方 ProbePolling 续跑段，从
                // 挂起点继续执行 isReady 至完成，再做一次性就绪判定。不得
                // 绕过探测循环直续 yield 点（否则 isReady 返回 false 也
                // 续行，_pollingAlarm 残留引发幽灵探测）
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

        // 唤醒债务标记（与 _pollTimer 配对；死锁判定的在途唤醒源）
        private VmDispatch.WakeupMarker? _pollMarker;

        internal void AttachTimer(Timer timer, VmDispatch.WakeupMarker? marker = null)
        {
            var previous = _pollTimer;
            var previousMarker = _pollMarker;
            _pollTimer = timer;
            _pollMarker = marker;
            previous?.Dispose();
            previousMarker?.Disarm();
        }

        // 终态统一释放轮询 timer（AttachTimer 的对偶，由
        // VmDispatch.OnTerminal 这一终态 choke point 调用）。timer 回调可能正在
        // 执行：只用 Dispose()、不用 Dispose(WaitHandle) 阻塞版；置 null
        // 保证幂等并供测试断言
        internal void DisposePollTimer()
        {
            var timer = _pollTimer;
            var marker = _pollMarker;
            _pollTimer = null;
            _pollMarker = null;
            timer?.Dispose();
            marker?.Disarm();
        }

        // 是否有在途轮询 timer（测试断言用）
        internal bool HasPollTimer => _pollTimer != null;

        public void PushBlock(BilBlock block)
        {
            CurrentFrame.BlockStack.Push(VmBlockFrame.Plain(block));
        }

        private bool ProbePolling(VmContext context)
        {
            // Phase 2.6 语义纠偏（RUNTIME §19.2）：isReady 是普通 Rigi 代码，
            // 允许 await/yield——探测挂起即继续等待，唤醒后在等待协程自己的
            // 恢复块内完成探测；探测返回 false 才退回等待，true 就绪续行。
            var alarm = _pollingAlarm
                ?? throw new VmException("PollingAlarm 探测缺少对象");
            if (!_probeFrameOnStack)
            {
                // 首次探测：解析 isReady 目标并压探测帧。结果槽是一次性值，
                // 先清上一轮残留——若保留上轮 false，isReady 本轮抛出并被
                // yield 点外层 try 捕获后，会被误判成再次未就绪，继续轮询
                // 并在 catch 已离开后让异常击穿协程。
                var function = context.ResolveDispatch(VmPolling.IsReadySymbol, alarm);
                if (function == null)
                {
                    throw new VmException("PollingAlarm 没有 isReady");
                }
                CurrentFrame.Slots.Remove(VmPolling.ReadySlot);
                _probeBaseDepth = CallStack.Count;
                PushFrame(function, new[] { alarm }, VmPolling.ReadySlot);
                _probeFrameOnStack = true;
            }
            // 步进至探测帧弹出（栈深回到基深）或探测中途再次挂起。挂起
            // （await/yield/EventAlarm）时探测帧保留在栈上，唤醒后由
            // SettleAfterResume 重新进入本方法从挂起点续跑，不得重压新帧
            // （重复执行 isReady 副作用、旧帧永久滞留）。
            while (State == VmCoroutineState.Running
                && CallStack.Count > _probeBaseDepth)
            {
                Step(context);
            }
            if (State != VmCoroutineState.Running)
            {
                return false;
            }
            // 探测帧已弹出：一次性就绪判定。结果槽缺失 = 本轮未正常返回
            //（isReady 抛出后沿 yield 点词法 try/catch 展开捕获的路径）：
            // 清轮询状态并放行，异常沿展开后的控制流继续（无捕获则已 Failed）
            _probeFrameOnStack = false;
            if (!CurrentFrame.Slots.TryGetValue(VmPolling.ReadySlot, out var probeResult))
            {
                _pollingAlarm = null;
                return true;
            }
            CurrentFrame.Slots.Remove(VmPolling.ReadySlot);
            if (probeResult is VmBool flag && flag.Value)
            {
                _pollingAlarm = null;
                return true;
            }
            // 未就绪：退避重排，继续 PollingAlarm 等待
            if (!TrySuspend())
            {
                throw new VmException("PollingAlarm 未就绪时无法挂起");
            }
            Dispatch.NoteSuspended(this);
            Dispatch.SchedulePoll(this);
            return false;
        }

        public void Complete(VmCompletion completion)
        {
            _pending = completion;
            Unwind();
        }

        public void Step(VmContext context)
        {
            context.AccountStep();
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
                Dispatch.OnTerminal(this);
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
                    Fail(ComposeUncaughtFailure(context,
                        pending.Failure ?? new VmException("未捕获异常", pending.Value)));
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

        // MW9b：顶层未捕获格式对齐 native reporter——「{实际类型全名}:
        // {message}」（如 core::DividedByZeroException: 整数除以零）。
        // 此刻仍在执行循环内（协程未终态），对异常对象虚派发 getMessage()
        // 取消息串（同 ProbePolling 的同步派发模式）；派发失败先兜底直读
        // core::Exception#message@.string 字段，再兜底保留原 Message。
        // 字段兜底与 native 的 override 语义有分歧留白：用户 override
        // getMessage 且不返回 message 字段时，兜底文本以字段为准。
        // ExceptionObject == null 的基础设施 abort 不走合成，保留原文。
        private VmException ComposeUncaughtFailure(VmContext? context, VmException failure)
        {
            if (context == null || failure.ExceptionObject == null)
            {
                return failure;
            }
            var typeName = VmTypeOps.ActualType(failure.ExceptionObject);
            var message = DispatchGetMessage(context, failure.ExceptionObject)
                ?? ReadMessageField(context, failure.ExceptionObject);
            if (message == null)
            {
                return failure;
            }
            return new VmException(typeName + ": " + message, failure.ExceptionObject);
        }

        private string? DispatchGetMessage(VmContext context, VmValue exceptionValue)
        {
            if (State != VmCoroutineState.Running)
            {
                return null;
            }
            try
            {
                var function = context.ResolveDispatch(
                    "core::Exception$getMessage()@.string", exceptionValue);
                if (function == null)
                {
                    return null;
                }
                // 消息 getter 可以抛出；复用同步隔离桥，保留原异常协程的
                // 栈、Throw pending 和终态，失败后仍由原 receiver 的字段兜底。
                return context.Dispatch.InvokeIsolated(function, new[] { exceptionValue })
                    is VmString text ? text.Value : null;
            }
            catch (VmException)
            {
                return null;
            }
        }

        private static string? ReadMessageField(VmContext context, VmValue exceptionValue)
        {
            return exceptionValue is IVmFieldHost host
                && host.TryReadField(context.RuntimeField("core::Exception#message@.string"), out var value)
                && value is VmString text
                ? text.Value
                : null;
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

        internal void LocalPush(VmValue key, VmValue value) =>
            Locals.Add((key, value));

        internal void LocalPop(VmValue key)
        {
            for (var i = Locals.Count - 1; i >= 0; i--)
            {
                if (SameLocalKey(Locals[i].Key, key))
                {
                    Locals.RemoveAt(i);
                    return;
                }
            }
            throw new VmException("coro_local_pop 栈上无对应键");
        }

        internal VmValue LocalGet(VmValue key)
        {
            for (var i = Locals.Count - 1; i >= 0; i--)
            {
                if (SameLocalKey(Locals[i].Key, key))
                {
                    return Locals[i].Value.Copy();
                }
            }
            return VmNull.Instance;
        }

        internal void InheritLocalsFrom(VmCoroutine source)
        {
            for (var i = source.Locals.Count - 1; i >= 0; i--)
            {
                var pair = source.Locals[i];
                var seen = false;
                foreach (var existing in Locals)
                {
                    if (SameLocalKey(existing.Key, pair.Key))
                    {
                        seen = true;
                        break;
                    }
                }
                if (seen)
                {
                    continue;
                }
                Locals.Add((pair.Key.Copy(), pair.Value.Copy()));
            }
        }

        internal static VmValue UnwrapLocal(VmValue value) =>
            value is VmAny any ? UnwrapLocal(any.Payload) : value;

        internal static bool SameLocalKey(VmValue left, VmValue right) =>
            ReferenceEquals(UnwrapLocal(left), UnwrapLocal(right));

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
