using System.Globalization;
using System.IO;
using System.Text;

namespace RigiCompiler.Bil.Vm
{
    // 执行期上下文（BIL_VM_DESIGN §4 / BIL_STANDARD §22）：
    // 模块、符号索引、hook 表、stdout/stderr 汇；静态字段加锁存储（V2）。

    public sealed partial class VmContext
    {
        public BilModule Module { get; }
        public VmHooks Hooks { get; }
        internal BilVerificationContext Types { get; }
        // MW11c 棒4a（§17.4）：native 原语实现 + 调度桥（Rigi 世界的
        // Dispatcher/Task 与 VM 引擎之间的粘合）
        internal VmDispatch Dispatch { get; }

        // 实现级指令步数上限：0 = 不限制（默认）。每条 Step 计 1（含嵌套）。
        public long MaxSteps { get; set; }
        private long _steps;

        private readonly Dictionary<string, BilFunction> _functions;
        private readonly Dictionary<string, BilSimpleMemberDeclaration> _members;
        private readonly Dictionary<string, BilTypeDeclaration> _types;
        private readonly Dictionary<string, BilTypeDeclaration> _typesByKey;
        private readonly Dictionary<string, BilSimpleMemberDeclaration> _fields;
        private readonly Dictionary<string, BilCaseDeclaration> _cases;
        private readonly Dictionary<string, string> _getters;
        private readonly Dictionary<string, string> _setters;
        // 访问器符号 → 字段符号逆向表（与 _getters/_setters 同建于
        // IndexSimple；TryFindFieldForAccessor 用）
        private readonly Dictionary<string, string> _accessorFields;
        private readonly Dictionary<string, VmValue> _statics = new Dictionary<string, VmValue>();
        private readonly object _staticLock = new object();
        // 逻辑 TypeSheet 缓存（声明 key → 拍平 sheet；可重入锁——Build 沿
        // extends 递归会重入 SheetOf，VM 多 Worker 并发触发）
        private readonly Dictionary<string, VmTypeSheet> _sheets =
            new Dictionary<string, VmTypeSheet>(StringComparer.Ordinal);
        private readonly object _sheetLock = new object();
        private readonly Dictionary<string, VmValue> _singletons = new Dictionary<string, VmValue>();
        private readonly object _singletonLock = new object();
        // singleton 构造在途栈（BIL §8.7，裁定 2）：正在初始化（init 尚未跑完）
        // 的类型，栈序即构造链；预初始化单协程同步执行，仅 InitializeSingletons
        // 期间读写，仍加锁与 _singletons 口径一致
        private readonly List<string> _initializing = new List<string>();
        private readonly object _initializingLock = new object();
        // 两路标准流分别按调用顺序保存字节；UTF-8 仅在读取结果时对整个
        // 通道解码，不能在 write 边界把尚未写完的多字节序列替换掉。
        private readonly MemoryStream _stdout = new MemoryStream();
        private readonly MemoryStream _stderr = new MemoryStream();
        private readonly object _stdoutLock = new object();
        private readonly object _stderrLock = new object();

        public VmContext(BilModule module, VmHooks? hooks = null)
        {
            Module = module;
            Types = new BilVerificationContext(module);
            Hooks = hooks ?? VmHooks.CreateStandard();
            _functions = new Dictionary<string, BilFunction>();
            foreach (var function in module.Functions)
            {
                _functions[function.Symbol] = function;
            }
            _members = new Dictionary<string, BilSimpleMemberDeclaration>();
            _types = new Dictionary<string, BilTypeDeclaration>();
            _typesByKey = new Dictionary<string, BilTypeDeclaration>();
            _fields = new Dictionary<string, BilSimpleMemberDeclaration>();
            _cases = new Dictionary<string, BilCaseDeclaration>();
            _getters = new Dictionary<string, string>();
            _setters = new Dictionary<string, string>();
            _accessorFields = new Dictionary<string, string>();
            IndexMembers(module.LocalSymbols);
            IndexMembers(module.ExternalSymbols);
            Dispatch = new VmDispatch(this);
        }

        // 每执行一条 BIL 指令调用一次；超限抛 VmStepLimitException（受控）。
        internal void AccountStep()
        {
            if (MaxSteps <= 0) return;
            long n = Interlocked.Increment(ref _steps);
            if (n > MaxSteps)
            {
                throw new VmStepLimitException(MaxSteps);
            }
        }

        // 阻塞原语也观察全局预算；某个 Worker 超限后，其余 Worker
        // 不能继续停在 park/同步锁里，否则主调用永远拿不到受控失败。
        internal void CheckStepLimit()
        {
            if (MaxSteps > 0 && Interlocked.Read(ref _steps) > MaxSteps)
                throw new VmStepLimitException(MaxSteps);
        }

        public string Stdout
        {
            get { lock (_stdoutLock) return Encoding.UTF8.GetString(_stdout.ToArray()); }
        }

        public string Stderr
        {
            get { lock (_stderrLock) return Encoding.UTF8.GetString(_stderr.ToArray()); }
        }

        public void WriteStdout(string text)
        {
            // Console 的完整文本与标准流原始字节写入同一通道、同一锁，
            // 因而不会因分别缓存文本/字节而丢掉两种调用的实际先后顺序。
            WriteStdout(Encoding.UTF8.GetBytes(text));
        }

        public void WriteStderr(string text)
        {
            WriteStderr(Encoding.UTF8.GetBytes(text));
        }

        public void WriteStdout(ReadOnlySpan<byte> bytes)
        {
            lock (_stdoutLock)
            {
                _stdout.Write(bytes);
            }
        }

        public void WriteStderr(ReadOnlySpan<byte> bytes)
        {
            lock (_stderrLock)
            {
                _stderr.Write(bytes);
            }
        }

        // singleton 构造的引导帧与目标槽：New 需要一帧写入目标变量，逐
        // singleton 复用同一静态引导 fn（只承载目标槽，不含用户指令）
        private static readonly BilFunction SingletonBootstrapFunction = CreateSingletonBootstrap();
        private static readonly BilVariableOperand SingletonBootstrapTarget = BilOp.Var(".singleton");

        // 嵌套类外层泛型捕获（review-20260910 #02）：嵌套类构造类型的实参
        // 只覆盖自身 GP（.type Ring.RingEnum = class generic(TItem)），外层
        // 宿主 GP 绑定在构造点从当前帧解析，以 .string 数组形态挂实例隐藏槽；
        // 方法帧对齐隐藏 typeid（AlignGenericHiddenArgs）时按外层链序取用
        public const string HiddenOuterGenericsKey = ".outer-generics";

        // undisposed 事件队列（finalizer 入队、Run 收尾派发；语义见
        // VmDisposal.cs 与 VmObject 终结器注释）
        internal VmUndisposedTracker UndisposedTracker { get; } = new VmUndisposedTracker();

        // core::IDisposable 的 canonical（stdlib core/disposable.rg），与
        // native LayoutEngine.DisposableCanonical 同口径
        internal const string DisposableCanonical = "core::IDisposable";

        // dispose 槽目标缓存：类型声明 key → impl fn 符号（null = 实现闭包
        // 不含 IDisposable / 槽无实现）；槽序事实都在 TypeSheet 缓存上，
        // 本缓存只是把「判定 + iMap 换算」摊成一次字典读，防热路径拖慢
        private readonly Dictionary<string, string?> _disposeSlotTargets =
            new Dictionary<string, string?>(StringComparer.Ordinal);
        private readonly object _disposeSlotLock = new object();

    }
}
