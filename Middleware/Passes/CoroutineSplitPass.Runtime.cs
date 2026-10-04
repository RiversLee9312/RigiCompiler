using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Passes
{
    public sealed partial class CoroutineSplitPass : IMwStage
    {
        // Runtime 职责；与主文件共享同一类型、字段及生命周期。

        // ===== Rigi 世界运行时通道符号（棒5a；按名前缀+宿主解析，不
        // 硬编码返回类型拼写）=====
        internal sealed class RuntimeSyms
        {
            internal MwMemberSymbol MutexAcquire = null!;
            internal MwMemberSymbol MutexRelease = null!;
            internal MwMemberSymbol CoroutineCurrent = null!;
            internal MwMemberSymbol CoroutineSetLane = null!;
            internal MwMemberSymbol AlarmWait = null!;
            internal MwMemberSymbol PollArm = null!;
            internal MwMemberSymbol PollPending = null!;
            internal MwMemberSymbol PollSchedule = null!;
            internal MwMemberSymbol PollClear = null!;
            internal MwMemberSymbol FailureRecord = null!;
            internal MwMemberSymbol CoroLocalInherit = null!;
            internal MwMemberSymbol DispatcherGet = null!;
            internal MwMemberSymbol NoteSpawn = null!;
            internal MwMemberSymbol NoteTerminal = null!;
            internal MwMemberSymbol Publish = null!;
            internal MwMemberSymbol PublishAll = null!;
            internal MwMemberSymbol LaneOfCurrent = null!;
        }

        internal static RuntimeSyms ResolveRuntime(MwContext context, MirModule mir)
        {
            // 顶层 native / 模块级助手：Owner=null，挂 GlobalMembers；
            // canonical 形如 core.coroutine::$name(...)（`$` 是无主
            // 成员分隔符，不是名为 `$` 的 companion 类型）
            MwMemberSymbol Global(string namePrefix) =>
                FindCoroutineGlobal(context, namePrefix)
                ?? throw new CompilerInternalException(
                    "stdlib 缺少 native 原语声明: core.coroutine::$" + namePrefix);
            // Dispatcher 成员：名前缀 + 参数个数
            MwMemberSymbol DispatcherFn(string name) =>
                context.CompilerMember(DispatcherCanonical + "$" + name + "(")
                ?? throw new CompilerInternalException(
                    "stdlib 缺少 Dispatcher 通道: " + name);
            var syms = new RuntimeSyms
            {
                MutexAcquire = Global("rigi_sync_mutex_acquire("),
                MutexRelease = Global("rigi_sync_mutex_release("),
                CoroutineCurrent = Global("rigi_coroutine_current("),
                CoroutineSetLane = Global("rigi_coroutine_set_lane("),
                AlarmWait = Global("rigi_alarm_wait("),
                PollArm = Global("rigi_poll_arm("),
                PollPending = Global("rigi_poll_pending("),
                PollSchedule = Global("rigi_poll_schedule("),
                PollClear = Global("rigi_poll_clear("),
                FailureRecord = Global("rigi_failure_record("),
                CoroLocalInherit = Global("rigi_coro_local_inherit("),
                NoteSpawn = DispatcherFn("noteSpawn"),
                NoteTerminal = DispatcherFn("noteTerminal"),
                Publish = DispatcherFn("publishNative"),
                PublishAll = DispatcherFn("publishAll"),
                LaneOfCurrent = DispatcherFn("laneOfCurrent"),
            };
            // Dispatcher singleton get fn（SingletonLowering 已合成）
            var getCanonical = SingletonPlanner.GetFnCanonicalOf(BilCompilerSymbols.Resolve(context.Module, DispatcherCanonical));
            syms.DispatcherGet = mir.Functions.FirstOrDefault(f =>
                f.Symbol.Canonical == getCanonical)?.Symbol
                ?? throw new CompilerInternalException(
                    "Dispatcher singleton get fn 未合成: " + getCanonical);
            return syms;
        }

        private RuntimeSyms Syms(MwContext context, MirModule mir) =>
            _syms ??= ResolveRuntime(context, mir);

        // 顶层（Owner=null）core.coroutine 成员：按 canonical 前缀取
        internal static MwMemberSymbol? FindCoroutineGlobal(MwContext context,
            string namePrefix) =>
            context.CompilerMember("core.coroutine::$" + namePrefix);

        // Task 声明前缀（Task / Task<TReturn> 两套同构声明）
        internal static string TaskPrefixOf(string taskTypeRef) =>
            taskTypeRef.StartsWith("core.coroutine::Task<", System.StringComparison.Ordinal)
                ? "core.coroutine::Task<TReturn>"
                : "core.coroutine::Task";

        // await 交互点的 Task 类型取自被 await 槽，不是当前协程自身返回
        // Task（void Task 的 run() await Task<i32> 的 add() 时两套声明
        // 字段不同：result 只在 Task<TReturn> 上）
        private static string AwaitedTaskTypeRef(MirFunction fn, MirAwait awaitInst)
        {
            var canonical = fn.FindLocal(awaitInst.TaskSlot).Type.Canonical;
            if (!canonical.StartsWith("core.coroutine::Task",
                    System.StringComparison.Ordinal))
            {
                throw new CompilerInternalException(
                    "await 槽不是 Task 类型: " + awaitInst.TaskSlot + " → " + canonical);
            }
            return canonical;
        }

        // 同名不同元数：FindType 裸键只留 arity-0；泛型 Task 走 FindTypeByRef
        internal static MwTypeSymbol RequireTaskType(MwContext context, string taskTypeRef)
        {
            var prefix = TaskPrefixOf(taskTypeRef);
            return context.Symbols.FindTypeByRef(prefix)
                ?? throw new CompilerInternalException("Task 类型符号缺失: " + prefix);
        }

        internal static MwMemberSymbol TaskFn(MwContext context, string taskTypeRef,
            string name)
        {
            var prefix = TaskPrefixOf(taskTypeRef);
            return context.CompilerMember(prefix + "$" + name + "(")
                ?? throw new CompilerInternalException(
                    "stdlib 缺少 Task 通道: " + prefix + "$" + name);
        }

        // EventAlarm 成员通道符号（L8：ensureHandle 懒建默认底座——
        // 用户直继子类 handle==0 时补手动事件粘滞形态，§19.3）
        internal static MwMemberSymbol EventAlarmFn(MwContext context, string name)
        {
            return context.CompilerMember(EventAlarmCanonical + "$" + name + "(")
                ?? throw new CompilerInternalException(
                    "stdlib 缺少 EventAlarm 通道: " + name);
        }

        // Task 字段符号（declaration 形态；FieldEmitter 按宿主段查布局计划）
        internal static string TaskField(MwContext context, string taskTypeRef, string name,
            string typeCanonical) =>
            BilCompilerSymbols.ResolveField(context.Module, TaskPrefixOf(taskTypeRef) + "#" + name + "@" + typeCanonical);

        // frame 的 Task 字段符号（槽类型 = Task 构造形态 canonical）
        internal static string TaskFieldSymbolOf(string frameCanonical,
            string taskTypeCanonical) =>
            SyntheticTypePlanner.FrameFieldSymbol(frameCanonical, TaskSlotName,
                taskTypeCanonical);

        // resume fn 的 frame.task 字段符号还原（RcInjection 传播垫用：
        // 垫在 split 之后合成，从 frame 布局计划按槽名找回）
        internal static string TaskFieldSymbolOfResume(MwContext context, MirFunction fn,
            out string taskTypeRef)
        {
            var frameCanonical = fn.FindLocal(FrameParamName).Type.Canonical;
            var plan = context.Layout?.Find(frameCanonical)
                ?? throw new CompilerInternalException(
                    "resume frame 布局计划缺失: " + frameCanonical);
            foreach (var field in plan.Fields)
            {
                if (field.Symbol.Contains("#" + TaskSlotName + "@",
                        System.StringComparison.Ordinal))
                {
                    taskTypeRef = field.Symbol.Substring(
                        field.Symbol.LastIndexOf('@') + 1);
                    return field.Symbol;
                }
            }
            throw new CompilerInternalException(
                "resume frame 缺 $mw.task 字段: " + frameCanonical);
        }

    }
}
