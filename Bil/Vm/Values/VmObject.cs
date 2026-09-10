namespace RigiCompiler.Bil.Vm
{
    // 引用/值类型实例（BIL_VM_DESIGN §3.2 / BIL_STANDARD §22.1）：
    // 运行时类型以 canonical 符号为身份；字段字典以字段符号为键。
    // class 引用语义（Copy 返回自身）；struct/wrapper 深拷贝。
    // wrapper 隐藏存储（§5.3）不进 BIL 字段表，由 Hidden 槽承载。

    internal interface IVmFieldHost
    {
        string TypeRef { get; }
        bool TryReadField(string fieldSymbol, out VmValue value);
        void WriteField(string fieldSymbol, VmValue value);
        bool TryReadHidden(string key, out VmValue value);
        void WriteHidden(string key, VmValue value);
        // 隐藏存储写入序（§14.4：new.wrapper.* 安装序即 wrapper 链 outer→inner）
        IReadOnlyList<string> HiddenKeysInOrder { get; }
        IVmFieldHost DeepCopySlots();
    }

    internal sealed class VmInstanceSlots : IVmFieldHost
    {
        private readonly Dictionary<string, VmValue> _fields = new Dictionary<string, VmValue>();
        private readonly Dictionary<string, VmValue> _hidden = new Dictionary<string, VmValue>();
        private readonly List<string> _hiddenOrder = new List<string>();

        public string TypeRef { get; }

        public VmInstanceSlots(string typeRef)
        {
            TypeRef = typeRef;
        }

        public bool TryReadField(string fieldSymbol, out VmValue value)
        {
            return _fields.TryGetValue(fieldSymbol, out value!);
        }

        public void WriteField(string fieldSymbol, VmValue value)
        {
            _fields[fieldSymbol] = value;
        }

        public bool TryReadHidden(string key, out VmValue value)
        {
            return _hidden.TryGetValue(key, out value!);
        }

        public void WriteHidden(string key, VmValue value)
        {
            if (!_hidden.ContainsKey(key))
            {
                _hiddenOrder.Add(key);
            }
            _hidden[key] = value;
        }

        public IReadOnlyList<string> HiddenKeysInOrder => _hiddenOrder;

        public IVmFieldHost DeepCopySlots()
        {
            var copy = new VmInstanceSlots(TypeRef);
            foreach (var pair in _fields)
            {
                copy._fields[pair.Key] = pair.Value.Copy();
            }
            foreach (var pair in _hidden)
            {
                copy._hidden[pair.Key] = pair.Value.Copy();
            }
            copy._hiddenOrder.AddRange(_hiddenOrder);
            return copy;
        }
    }

    public sealed class VmObject : VmValue, IVmFieldHost
    {
        private readonly IVmFieldHost _slots;
        private readonly bool _valueType;
        // MW12b §25.2：undisposed 销毁检查。判定在 AllocateObject 构造期
        // 完成——实现闭包含 core::IDisposable 的 class 引用对象才挂事件
        // 队列引用（struct/wrapper 值拷贝与 native 只对 class 计划检查的
        // 口径一致，不追踪）；dispose 槽目标进入即置位 DisposedMarked。
        // 「一被回收就爆炸」：VM=.NET GC 与 native=ARC+macroGC 事件时机
        // 天然不同，语义本就禁止假设底层 GC 实现
        private readonly VmUndisposedTracker? _undisposedTracker;
        // 解释器线程写、终结器线程读（volatile 保证跨线程可见）
        internal volatile bool DisposedMarked;
        internal bool IsDisposalTracked => _undisposedTracker != null;

        // capability 强引用不属于用户字段或 wrapper 隐藏字段枚举。
        // VM 的对象图由 CLR 保活；引用随 Handle 自身回收，无 dispose 面。
        internal VmValue? HandleTarget { get; set; }
        internal bool HandleMutable { get; set; }
        internal int HandleKind { get; set; }
        // 通用原生资源所有权快照；终结器不读取解释器字段槽。
        internal System.Action<long, long>? NativeResourceRelease { get; set; }
        private long _nativeGate;
        private long _nativeCoroutine;
        internal VmCoroutine? TaskRuntimeState { get; set; }

        public override string TypeRef => _slots.TypeRef;
        public bool IsValueType => _valueType;

        public VmObject(string typeRef, bool valueType)
            : this(typeRef, valueType, undisposedTracker: null)
        {
        }

        internal VmObject(string typeRef, bool valueType,
            VmUndisposedTracker? undisposedTracker)
        {
            _slots = new VmInstanceSlots(typeRef);
            _valueType = valueType;
            _undisposedTracker = undisposedTracker;
        }

        private VmObject(IVmFieldHost slots, bool valueType)
        {
            _slots = slots;
            _valueType = valueType;
        }

        // 销毁时检查：从未经 dispose 调用就被 .NET GC 回收 → 入队
        // undisposed 事件（类型 canonical 名）。finalizer 只做入队——
        // 触碰 VM 解释器状态在终结器线程上不安全；同一对象 finalizer
        // 只跑一次，天然防重复上报
        ~VmObject()
        {
            NativeResourceRelease?.Invoke(
                System.Threading.Interlocked.Exchange(ref _nativeGate, 0),
                System.Threading.Interlocked.Exchange(ref _nativeCoroutine, 0));
            if (_undisposedTracker != null && !DisposedMarked)
            {
                _undisposedTracker.Enqueue(TypeRef);
            }
        }

        public override VmValue Copy()
        {
            if (!_valueType)
            {
                return this;
            }
            return new VmObject(_slots.DeepCopySlots(), valueType: true);
        }

        public override string ToStandardText() => TypeRef;

        public bool TryReadField(string fieldSymbol, out VmValue value)
        {
            return _slots.TryReadField(fieldSymbol, out value);
        }

        public void WriteField(string fieldSymbol, VmValue value)
        {
            _slots.WriteField(fieldSymbol, value);
            if (NativeResourceRelease != null && value is VmI64 handle)
            {
                if (fieldSymbol.EndsWith("#gate@.i64", System.StringComparison.Ordinal))
                    System.Threading.Interlocked.Exchange(ref _nativeGate, handle.Value);
                else if (fieldSymbol.EndsWith("#handle@.i64", System.StringComparison.Ordinal))
                    System.Threading.Interlocked.Exchange(ref _nativeCoroutine, handle.Value);
            }
        }

        public bool TryReadHidden(string key, out VmValue value)
        {
            return _slots.TryReadHidden(key, out value);
        }

        public void WriteHidden(string key, VmValue value)
        {
            _slots.WriteHidden(key, value);
        }

        public IReadOnlyList<string> HiddenKeysInOrder => _slots.HiddenKeysInOrder;

        IVmFieldHost IVmFieldHost.DeepCopySlots() => _slots.DeepCopySlots();

        internal static bool TryAsHost(VmValue value, out IVmFieldHost host)
        {
            if (value is IVmFieldHost fieldHost)
            {
                host = fieldHost;
                return true;
            }
            host = null!;
            return false;
        }
    }
}
