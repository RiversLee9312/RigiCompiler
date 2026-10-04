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
        // ColdTasks 职责；与主文件共享同一类型、字段及生命周期。

        // ===== ⑤ 冷 Task 构造重写（§18.4；棒5a）=====
        // `new Task(body)` / `new Task<T>(body)`（body 静态类型 = 具体
        // 闭包类）→ $mw.coldtask.* 工厂调用：工厂预建 body $$call 的
        // frame（.this = body）+ cohandle 存 coldHandle + gate 构造即建
        //（对齐真实 init 的 gate 前置），spawn-into 由 Task.startCold/
        // spawnIntoLocked（Rigi 体）在启动时复用 coldHandle。
        // 构造不拷 CoroutineLocal（冷构造 ≠ 启动；继承在 spawnIntoLocked）。
        // body 静态类型不透明（AsyncAction/AsyncFunc 接口形态）跳过工厂，
        // 保留真实 init（coldHandle 保持 0），启动时 bindColdBody 动态绑定。
        private void RewriteColdTaskConstructions(MwContext context, MirModule mir)
        {
            foreach (var fn in mir.Functions.ToList())
            {
                foreach (var block in fn.Blocks)
                {
                    var insts = block.InstructionList;
                    for (var i = 0; i < insts.Count; i++)
                    {
                        if (insts[i] is not MirNewObject newObject
                            || newObject.Args.Count != 1
                            || !IsRealTaskInit(newObject.Init))
                        {
                            continue;
                        }
                        var bodySlot = ((MirLocalOperand)newObject.Args[0]).Name;
                        var bodyType = ConcreteColdBodyType(context, fn, bodySlot);
                        if (IsOpaqueCallableType(bodyType))
                        {
                            continue;
                        }
                        var factory = EnsureColdTaskFactory(context, mir,
                            newObject.Type, bodyType);
                        insts[i] = new MirCall(factory, newObject.Args,
                            newObject.Target, newObject.ExcTarget);
                    }
                }
            }
        }

        // 冷 Task body 的静态槽常是 AsyncFunc<TReturn> 占位（init 形参），
        // 工厂要的是具体 lambda 类（才能找 $$call）。优先取产出该槽的
        // MirNewObject 类型；否则回退槽上声明（已是具体类时）
        private static MirType ConcreteColdBodyType(MwContext context, MirFunction fn,
            string bodySlot)
        {
            var slot = bodySlot;
            for (var hop = 0; hop < 8; hop++)
            {
                string? copiedFrom = null;
                foreach (var block in fn.Blocks)
                {
                    foreach (var inst in block.Instructions)
                    {
                        if (inst is MirNewObject produced && produced.Target == slot)
                        {
                            return MirType.Of(produced.Type.Canonical);
                        }
                        if (inst is MirCast cast && cast.Target == slot
                            && cast.Source is MirLocalOperand fromCast)
                        {
                            copiedFrom = fromCast.Name;
                        }
                        if (inst is MirCopyLocal copy && copy.Target == slot
                            && copy.Source is MirLocalOperand fromCopy)
                        {
                            copiedFrom = fromCopy.Name;
                        }
                    }
                }
                if (copiedFrom == null)
                {
                    break;
                }
                slot = copiedFrom;
            }
            var declared = fn.FindLocal(bodySlot).Type;
            if (IsOpaqueCallableType(declared)
                || context.Symbols.FindType(declared.Canonical) != null)
            {
                return declared;
            }
            throw new MwNotSupportedException(
                "冷 Task 的 body 静态类型不透明（native 半场暂不支持）: "
                + declared.Canonical);
        }

        // AsyncAction / AsyncFunc 接口形态：构造点看不到唯一闭包类
        private static bool IsOpaqueCallableType(MirType type)
        {
            var canonical = type.Canonical;
            return canonical == "core::AsyncAction"
                || canonical.StartsWith("core::AsyncAction<",
                    System.StringComparison.Ordinal)
                || canonical.StartsWith("core::AsyncFunc<",
                    System.StringComparison.Ordinal);
        }

        // 0 参 async $$call（lambda / AsyncAction 子类）：可作为冷 Task body
        private static bool IsZeroArgAsyncCall(MirFunction fn)
        {
            if (fn.Symbol.Owner == null
                || !fn.Symbol.Canonical.Contains("$$call(",
                    System.StringComparison.Ordinal))
            {
                return false;
            }
            var hasThis = false;
            foreach (var parameter in fn.Parameters)
            {
                if (parameter.Name == ".this")
                {
                    hasThis = true;
                    continue;
                }
                if (parameter.Name.StartsWith(".generic.",
                    System.StringComparison.Ordinal))
                {
                    continue;
                }
                return false;
            }
            return hasThis;
        }

        // 沿 extends 链找 $$call 成员（环保护；外部/缺失基类即止）。
        // 与 VM FindCallTarget 拍平 sheet（含继承槽）同语义
        private static MwMemberSymbol? FindCallAlongHierarchy(MwContext context,
            MwTypeSymbol type)
        {
            var guard = new HashSet<string>(System.StringComparer.Ordinal);
            var current = type;
            while (guard.Add(current.Canonical))
            {
                var call = current.Members.FirstOrDefault(m =>
                    m.Canonical.Contains("$$call(", System.StringComparison.Ordinal));
                if (call != null)
                {
                    return call;
                }
                if (current.Declaration.ExtendsType is not { } baseRef
                    || context.Symbols.FindTypeByRef(baseRef) is not
                        { IsExternal: false } baseType)
                {
                    return null;
                }
                current = baseType;
            }
            return null;
        }

        private static bool IsVoidTaskType(string taskTypeRef) =>
            !taskTypeRef.StartsWith("core.coroutine::Task<",
                System.StringComparison.Ordinal);

        private sealed class ColdBindEntry
        {
            internal string OwnerCanonical = "";
            internal string CallCanonical = "";
            internal MwMemberSymbol ResumeSymbol = null!;
            internal string FrameCanonical = "";
            internal MwTypeSymbol FrameType = null!;
            internal MwMemberSymbol FrameInit = null!;
            internal string TaskTypeRef = "";
            internal string TaskConstructionRef = "";
            internal MirType ThisType = null!;
        }

        // Task.bindColdBody / Task<TReturn>.bindColdBody：按模块内已
        // split 的 0 参 async $$call 做 type.is 链，命中则调 $mw.bindcold.*
        // 建 frame + 句柄写入 coldHandle；全不中抛 IllegalStateException
        private void RewriteBindColdBodies(MwContext context, MirModule mir)
        {
            RewriteBindColdBody(context, mir, "core.coroutine::Task", voidTask: true);
            RewriteBindColdBody(context, mir, "core.coroutine::Task<TReturn>",
                voidTask: false);
        }

        private void RewriteBindColdBody(MwContext context, MirModule mir,
            string taskDecl, bool voidTask)
        {
            var prefix = taskDecl + "$bindColdBody(";
            var declaration = context.CompilerMember(prefix);
            var original = mir.Functions.SingleOrDefault(f =>
                f.Symbol.Canonical == declaration?.Canonical);
            if (original == null)
            {
                return;
            }
            var candidates = _coldBinds.Where(e => IsVoidTaskType(e.TaskTypeRef) == voidTask)
                .ToList();
            if (candidates.Count == 0)
            {
                return;
            }
            var thisType = original.Parameters[0].Type;
            var rewritten = new MirFunction(original.Symbol, original.ReturnType,
                original.Parameters, new List<MirLocal>(original.Parameters),
                new List<MirBlock>(), original.IsEntrypoint);
            string Fresh(string namePrefix, MirType type) =>
                ProxyWildcardAbi.FreshLocal(rewritten, namePrefix, type);
            var bodyType = TaskBodyMirTypeOf(context, taskDecl);
            var body = Fresh("$mw.bind.body.", bodyType);
            var thisOp = new MirLocalOperand(".this");
            var entryInsts = new List<MirInst>
            {
                new MirGetField(thisOp, TaskBodyFieldOf(context, taskDecl), body),
            };
            var blocks = new List<MirBlock>();
            var bodyOp = new MirLocalOperand(body);
            for (var k = 0; k < candidates.Count; k++)
            {
                var candidate = candidates[k];
                var checkId = k == 0 ? "entry" : "mw.bc.chk." + k;
                var hitId = "mw.bc.hit." + k;
                var nextId = k + 1 < candidates.Count
                    ? "mw.bc.chk." + (k + 1)
                    : "mw.bc.miss";
                var checkInsts = k == 0 ? entryInsts : new List<MirInst>();
                var cond = Fresh("$mw.bc.is.", Bool);
                checkInsts.Add(new MirTypeCheck(MirTypeCheckKind.Is, bodyOp,
                    candidate.OwnerCanonical, null, cond));
                blocks.Add(new MirBlock(checkId, checkInsts,
                    new MirCondBranch(new MirLocalOperand(cond), hitId, nextId)));

                var hitInsts = new List<MirInst>();
                var typed = Fresh("$mw.bc.t.", candidate.ThisType);
                hitInsts.Add(new MirCopyLocal(bodyOp, typed));
                var helper = EnsureBindColdHelper(context, mir, candidate, thisType);
                var handle = Fresh("$mw.bc.h.", I64);
                hitInsts.Add(new MirCall(helper,
                    new List<MirOperand> { thisOp, new MirLocalOperand(typed) },
                    handle));
                hitInsts.Add(new MirCall(TaskFn(context, taskDecl, "attachCold"),
                    new List<MirOperand> { thisOp, new MirLocalOperand(handle) }, null));
                blocks.Add(new MirBlock(hitId, hitInsts, new MirRet(null)));
            }

            var missInsts = new List<MirInst>();
            EmitThrowIllegalState(context, rewritten, missInsts,
                "冷 Task body 无法 spawn-into：无匹配闭包");
            blocks.Add(new MirBlock("mw.bc.miss", missInsts, new MirRetThrow()));
            foreach (var block in blocks)
            {
                rewritten.AddBlock(block);
            }
            var index = mir.FunctionList.IndexOf(original);
            if (index < 0)
            {
                throw new CompilerInternalException(
                    "bindColdBody 不在模块函数表: " + original.Symbol.Canonical);
            }
            mir.FunctionList[index] = rewritten;
        }

        private static MirType TaskBodyMirTypeOf(MwContext context, string taskTypeRef)
        {
            var field = TaskBodyFieldOf(context, taskTypeRef);
            var at = field.LastIndexOf('@');
            return MirType.Of(at < 0 ? field : field.Substring(at + 1));
        }

        private MwMemberSymbol EnsureBindColdHelper(MwContext context, MirModule mir,
            ColdBindEntry entry, MirType taskThisType)
        {
            var key = entry.TaskTypeRef + "|" + entry.OwnerCanonical;
            if (_bindColdHelpers.TryGetValue(key, out var cached))
            {
                return cached;
            }
            var factoryCanonical = "$mw.bindcold." + key + "()@.i64";
            var symbol = ProxyBakeSupport.SyntheticMember(factoryCanonical, owner: null);
            var taskParam = new MirLocal("task", taskThisType);
            var bodyParam = new MirLocal("body", entry.ThisType);
            var helper = new MirFunction(symbol, I64,
                new List<MirLocal> { taskParam, bodyParam },
                new List<MirLocal> { taskParam, bodyParam },
                new List<MirBlock>(), false);
            string Fresh(string namePrefix, MirType type) =>
                ProxyWildcardAbi.FreshLocal(helper, namePrefix, type);
            var frame = Fresh("$mw.frame.", MirType.Of(entry.FrameCanonical));
            var handle = Fresh("$mw.handle.", I64);
            var insts = new List<MirInst>
            {
                new MirNewObject(entry.FrameType, null, entry.FrameInit,
                    new List<MirOperand>(), frame),
                new MirSetField(new MirLocalOperand("body"), new MirLocalOperand(frame),
                    SyntheticTypePlanner.FrameFieldSymbol(entry.FrameCanonical, ".this",
                        entry.ThisType.Canonical)),
                new MirSetField(new MirLocalOperand("task"), new MirLocalOperand(frame),
                    TaskFieldSymbolOf(entry.FrameCanonical, entry.TaskConstructionRef)),
                new MirCoroutineCreate(frame, entry.ResumeSymbol, handle),
            };
            helper.AddBlock(new MirBlock("entry", insts,
                new MirRet(new MirLocalOperand(handle))));
            mir.AddFunction(helper);
            _bindColdHelpers.Add(key, symbol);
            return symbol;
        }

        private static void EmitThrowIllegalState(MwContext context, MirFunction fn,
            List<MirInst> insts, string message)
        {
            var msg = ProxyWildcardAbi.FreshLocal(fn, "$mw.bc.msg.",
                ProxyWildcardAbi.StringType);
            insts.Add(new MirLoadResource(
                ProxyWildcardAbi.AddStringResource(context, message), msg));
            var excType = context.Symbols.FindTypeByRef("core::IllegalStateException")
                ?? throw new CompilerInternalException("core::IllegalStateException 类型缺失");
            MwMemberSymbol? init = null;
            foreach (var member in excType.Members)
            {
                if (!member.HasKeyword(BilKeyword.Init))
                {
                    continue;
                }
                var signature = CanonicalSignature.Parse(member.Canonical);
                if (signature.Parameters.Count == 1
                    && signature.Parameters[0].Name == "text")
                {
                    init = member;
                    break;
                }
            }
            if (init == null)
            {
                throw new CompilerInternalException(
                    "core::IllegalStateException 缺 init(text: String)");
            }
            var initWrapper = context.Symbols.FindInitWrapper(excType, 0);
            var exc = ProxyWildcardAbi.FreshLocal(fn, "$mw.bc.exc.",
                MirType.Of(excType.Canonical));
            insts.Add(new MirNewObject(excType, initWrapper, init,
                new List<MirOperand> { new MirLocalOperand(msg) }, exc));
            insts.Add(new MirThrow(new MirLocalOperand(exc), null));
        }

        // 真实 Task init 判定（排除本 pass 合成的空 init）
        private bool IsRealTaskInit(MwMemberSymbol? init)
        {
            // L7：Init = null（无 init 声明零参 new）必非 Task init
            if (init == null)
            {
                return false;
            }
            if (_taskEmptyInits.Values.Any(s => s.Canonical == init.Canonical))
            {
                return false;
            }
            var owner = init.Owner?.Canonical;
            return (owner == "core.coroutine::Task"
                || owner == "core.coroutine::Task<TReturn>")
                && init.HasKeyword(BilKeyword.Init);
        }

        private MwMemberSymbol EnsureColdTaskFactory(MwContext context, MirModule mir,
            MwTypeSymbol taskType, MirType bodyType)
        {
            var taskTypeRef = taskType.Canonical;
            var key = taskTypeRef + "|" + bodyType.Canonical;
            if (_coldTaskFactories.TryGetValue(key, out var cached))
            {
                return cached;
            }
            // 闭包的 async $$call → split 产物（frame 类型 + resume fn）。
            // $$call 沿 extends 链解析（与 VM FindCallTarget 拍平 sheet
            // 含继承槽同语义：Sub : Base : AsyncAction 的 $$call 声明在
            // Base，frame/resume 也属 Base 的 split 产物）
            var closureType = context.Symbols.FindType(bodyType.Canonical)
                ?? throw new MwNotSupportedException(
                    "冷 Task 的 body 静态类型不透明（native 半场暂不支持）: "
                    + bodyType.Canonical);
            var call = FindCallAlongHierarchy(context, closureType)
                ?? throw new MwNotSupportedException(
                    "冷 Task 的 body 缺少 $$call 实现: " + bodyType.Canonical);
            // frame 的 .this 槽类型 = $$call 声明宿主（继承命中时为基类），
            // 落字段符号必须按声明宿主拼写而非构造点静态类型
            var callThisCanonical = call.Owner?.Canonical ?? bodyType.Canonical;
            var frameCanonical = SyntheticTypePlanner.FrameCanonicalOf(call.Canonical);
            var frameType = context.Symbols.FindType(frameCanonical)
                ?? throw new MwNotSupportedException(
                    "冷 Task 的 body 不是 async 闭包（无 split frame）: "
                    + call.Canonical);
            var resumeSymbol = ProxyBakeSupport.SyntheticMember(
                "$mw.resume." + call.Canonical, owner: null);
            var frameInit = context.Symbols.FindMember(
                SyntheticTypePlanner.FrameInitCanonicalOf(frameCanonical))
                ?? throw new CompilerInternalException("frame 空 init 缺失: " + frameCanonical);
            var factoryCanonical = "$mw.coldtask." + key + "()@" + taskTypeRef;
            var symbol = ProxyBakeSupport.SyntheticMember(factoryCanonical, owner: null);
            var bodyParam = new MirLocal("body", bodyType);
            var factory = new MirFunction(symbol, MirType.Of(taskTypeRef),
                new List<MirLocal> { bodyParam }, new List<MirLocal> { bodyParam },
                new List<MirBlock>(), false);
            string Fresh(string prefix, MirType type) =>
                ProxyWildcardAbi.FreshLocal(factory, prefix, type);
            var frame = Fresh("$mw.frame.", MirType.Of(frameCanonical));
            var task = Fresh("$mw.task.", MirType.Of(taskTypeRef));
            var gate = Fresh("$mw.gate.", I64);
            var handle = Fresh("$mw.handle.", I64);
            var bodyOp = new MirLocalOperand("body");
            var smutexCreate = FindCoroutineGlobal(context, "rigi_sync_mutex_create(")
                ?? throw new CompilerInternalException("stdlib 缺 rigi_sync_mutex_create");
            var bodyField = TaskBodyFieldOf(context, taskTypeRef);
            var taskConstructionRef = TypeLayout.IsGenericPlaceholder(
                MirType.Of(taskTypeRef))
                ? TaskPrefixOf(taskTypeRef)
                : taskTypeRef;
            var insts = new List<MirInst>
            {
                // frame = new $$call frame；frame..this = body 闭包
                new MirNewObject(frameType, null, frameInit, new List<MirOperand>(), frame),
                new MirSetField(bodyOp, new MirLocalOperand(frame),
                    SyntheticTypePlanner.FrameFieldSymbol(frameCanonical, ".this",
                        callThisCanonical)),
                // Task 对象（合成空 init）+ body/gate/coldHandle 落字段
                new MirNewObject(TaskTypeSymbolOf(context, taskTypeRef),
                    null, TaskEmptyInit(context, mir, taskTypeRef),
                    new List<MirOperand>(), task),
                // resume DONE 经 frame.$mw.task 取回自身 Task（与 eager
                // stub 同通道；缺此字段则 complete 空引用崩）
                new MirSetField(new MirLocalOperand(task), new MirLocalOperand(frame),
                    TaskFieldSymbolOf(frameCanonical, taskConstructionRef)),
                new MirSetField(bodyOp, new MirLocalOperand(task), bodyField),
                new MirCall(smutexCreate, new List<MirOperand>(), gate),
                new MirSetField(new MirLocalOperand(gate), new MirLocalOperand(task),
                    TaskField(context, taskTypeRef, "gate", ".i64")),
                new MirCoroutineCreate(frame, resumeSymbol, handle),
                new MirCall(TaskFn(context, taskTypeRef, "attachCold"),
                    new List<MirOperand> { new MirLocalOperand(task),
                        new MirLocalOperand(handle) }, null),
            };
            factory.AddBlock(new MirBlock("entry", insts,
                new MirRet(new MirLocalOperand(task))));
            mir.AddFunction(factory);
            _coldTaskFactories.Add(key, symbol);
            return symbol;
        }

        // Task.body 字段符号（按简单名查——AsyncAction/AsyncFunc<TReturn>
        // 承载拼写随声明漂移）
        private static string TaskBodyFieldOf(MwContext context, string taskTypeRef)
        {
            var prefix = TaskPrefixOf(taskTypeRef);
            return context.CompilerMember(prefix + "#body@")?.Canonical
                ?? throw new CompilerInternalException("Task 缺 body 字段: " + prefix);
        }

    }
}
