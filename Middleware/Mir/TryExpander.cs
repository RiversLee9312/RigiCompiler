using System.Collections.Generic;
using System.Globalization;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;

namespace RigiCompiler.Middleware.Mir
{
    /// <summary>
    /// TryExpander（MW9a 第 B 棒）：BIL §16.7 五操作数 try 与 §16.9 throw
    /// 的 MIR 子图展开。checked-flag 便携模型：throw/可抛调用经
    /// rigi_exc_raise 置 TLS pending，异常边目标块入口恒为 MirTakePending
    ///（移动语义取回 +1）。形状契约：
    /// - 派发垫 mw.try.N.dispatch：入口 MirTakePending($mw.exc.N)，其后按
    ///   catch-table 表序 is 链（MirTypeCheck + MirCondBranch），命中前置
    ///   垫写 EXC_VAR=$mw.exc.N 后进 catch 块；
    /// - finally 单块双入口：一切离开 completion 经前置小垫
    ///   （mw.try.N.fin.pre.*）分别写 EXC_VAR（Throw 写异常对象，其余恒写
    ///   null）与 $mw.comp.N 路由码，finally 落出进路由器
    ///   （mw.try.N.route 的 MirSwitch）分派：0=正常落出、ret=函数返回、
    ///   brk/cnt=外层目标、throw=重抛外层；
    /// - catch 相位（catch 体内）的异常不再回本 try 派发垫（§16.7 步骤 7：
    ///   catch 再抛视同未命中，不得重进同表匹配），走逃逸垫 mw.try.N.esc
    ///   （先 take 刷新 $mw.exc.N）汇入异常前置垫；
    /// - finally 相位（finally 体内）的 abrupt completion 覆盖待处理
    ///   completion（步骤 10），不经本 try 路由器直解析；
    /// - ExcTarget==null = 传播出函数（propagate 垫创建与解析归 C 棒）。
    /// </summary>
    internal sealed class TryExpander
    {
        private readonly MirBuilder.FlowBuilder _f;
        private string? _retvLocal;   // $mw.retv：fn 级共享（嵌套 try 逐层外跳时值随行）

        internal TryExpander(MirBuilder.FlowBuilder flow)
        {
            _f = flow;
        }

        private enum TryPhase
        {
            Body,
            Catch,
            Finally,
        }

        private enum CompletionKind
        {
            Break,
            Continue,
            Return,
        }

        // 穿越 completion 键：同 try 内同键共享同一路由码与前置垫
        private readonly struct CompletionKey
        {
            internal CompletionKind Kind { get; }
            internal string? Var { get; }

            private CompletionKey(CompletionKind kind, string? var)
            {
                Kind = kind;
                Var = var;
            }

            internal static CompletionKey ForBreak(string var) => new(CompletionKind.Break, var);
            internal static CompletionKey ForContinue(string var) => new(CompletionKind.Continue, var);
            internal static CompletionKey ForReturn() => new(CompletionKind.Return, null);

            internal string Text => Kind switch
            {
                CompletionKind.Break => "B:" + Var,
                CompletionKind.Continue => "C:" + Var,
                _ => "R",
            };

            internal string Suffix => Kind switch
            {
                CompletionKind.Break => "brk",
                CompletionKind.Continue => "cnt",
                _ => "ret",
            };
        }

        // try 作用域：词法栈条目 + 展开期累积的合成块/路由码状态
        private sealed class TryScope : MirBuilder.Scope
        {
            internal int N { get; }
            internal string BreakIdVar { get; }
            internal string ExcVarName { get; }
            internal string ExcVarTypeRef { get; }
            internal bool HasFinally { get; }
            internal string? FinallyId { get; }
            internal TryPhase Phase { get; set; }
            internal int Index { get; set; }   // 词法栈位置（外层解析的递归基点）

            internal string ExcLocal { get; }
            internal string? CompLocal { get; }
            internal string DispatchId { get; }
            internal string EscId { get; }
            internal string FinPreNormalId { get; }
            internal string FinPreExcId { get; }
            internal string RouterId { get; }
            internal string AfterId { get; }
            internal string RetId { get; }
            internal string RethrowId { get; }
            internal string MissId { get; }

            // 派发垫/逃逸垫对象身份先行建立（体内 MirThrow/调用的
            // ExcTarget 持引用），内容在子块译完后填充
            internal MirBlock DispatchBlock { get; }
            internal MirBlock? EscBlock { get; }

            internal int ThrowCode { get; }
            internal int NextCode { get; set; }
            internal bool NeedsRetBlock { get; set; }
            internal List<(int Code, string PadId)> Pads { get; } = new();
            internal List<(int Code, string TargetId)> RouterEntries { get; } = new();
            internal Dictionary<string, (int Code, string PadId)> CodeByKey { get; } =
                new(System.StringComparer.Ordinal);

            internal TryScope(int n, TryInstruction inst, MirType excVarType)
            {
                N = n;
                BreakIdVar = inst.BreakId.Name;
                ExcVarName = inst.ExceptionSlot.Name;
                ExcVarTypeRef = excVarType.Canonical;
                HasFinally = inst.FinallyBlock != null;
                FinallyId = inst.FinallyBlock?.Id;
                ExcLocal = "$mw.exc." + n;
                var prefix = "mw.try." + n + ".";
                DispatchId = prefix + "dispatch";
                EscId = prefix + "esc";
                FinPreNormalId = prefix + "fin.pre.normal";
                FinPreExcId = prefix + "fin.pre.exc";
                RouterId = prefix + "route";
                AfterId = prefix + "end";
                RetId = prefix + "ret";
                RethrowId = prefix + "rethrow";
                MissId = prefix + "miss";
                DispatchBlock = new MirBlock(DispatchId, new List<MirInst>(), new MirUnreachable());
                if (HasFinally)
                {
                    CompLocal = "$mw.comp." + n;
                    EscBlock = new MirBlock(EscId, new List<MirInst>(), new MirUnreachable());
                    // throw 传播路由码恒占 1（派发垫未命中/逃逸垫入边）
                    ThrowCode = 1;
                    NextCode = 2;
                    RouterEntries.Add((ThrowCode, RethrowId));
                }
                else
                {
                    ThrowCode = 0;
                    NextCode = 1;
                }
            }

            internal string ChainId(int i) => "mw.try." + N + ".dispatch." + i;
            internal string CatchPreId(int i) => "mw.try." + N + ".catchpre." + i;
        }

        // ===== try 展开 =====

        internal void ExpandTry(TryInstruction inst)
        {
            _f.EnsureOpen();
            var n = _f.NextSynthetic();
            var scope = new TryScope(n, inst, _f.TypeOf(inst.ExceptionSlot.Name));
            _f.RegisterSyntheticLocal(scope.ExcLocal, MirType.Of("core::Exception"));
            if (scope.CompLocal != null)
            {
                _f.RegisterSyntheticLocal(scope.CompLocal, MirType.Of(".i32"));
            }

            _f.Terminate(new MirBranch(inst.Body.Id));
            scope.Index = _f.Scopes.Count;
            _f.PushScope(scope);
            // 正常落出：有 finally 经正常前置垫（写 null + comp=0），
            // 无 finally 直跳 after-try（现状逻辑不动）
            var normalExit = scope.HasFinally ? scope.FinPreNormalId : scope.AfterId;
            scope.Phase = TryPhase.Body;
            _f.EmitChildBlock(inst.Body, normalExit);
            scope.Phase = TryPhase.Catch;
            foreach (var entry in CatchEntries(inst))
            {
                _f.EmitChildBlock(entry.Handler, normalExit);
            }
            if (inst.FinallyBlock != null)
            {
                scope.Phase = TryPhase.Finally;
                _f.EmitChildBlock(inst.FinallyBlock, scope.RouterId);
            }
            _f.PopScope();

            _f.SealCurrentBlock();
            EmitDispatch(scope, inst);
            if (scope.HasFinally)
            {
                EmitFinallyPads(scope);
                EmitRouter(scope);
            }
            else
            {
                EmitMiss(scope);
            }
            _f.StartNewBlock(scope.AfterId);
        }

        // 派发垫 + is 链 + 命中前置垫（表序即匹配序，§16.7 步骤 4）
        private void EmitDispatch(TryScope scope, TryInstruction inst)
        {
            var entries = CatchEntries(inst);
            var missId = scope.HasFinally ? scope.FinPreExcId : scope.MissId;
            if (entries.Count == 0)
            {
                EmitMirBlock(scope.DispatchId,
                    new List<MirInst> { new MirTakePending(scope.ExcLocal) },
                    new MirBranch(missId), scope.DispatchBlock);
                return;
            }
            for (var i = 0; i < entries.Count; i++)
            {
                var isLocal = "$mw.is." + scope.N + "." + i;
                _f.RegisterSyntheticLocal(isLocal, MirType.Of(".bool"));
                var (targetRef, targetId) = TypeCheckTarget(entries[i].ExceptionType.TypeRef);
                var linkInsts = new List<MirInst>();
                if (i == 0)
                {
                    linkInsts.Add(new MirTakePending(scope.ExcLocal));
                }
                linkInsts.Add(new MirTypeCheck(MirTypeCheckKind.Is,
                    new MirLocalOperand(scope.ExcLocal), targetRef, targetId, isLocal));
                var nextId = i + 1 < entries.Count ? scope.ChainId(i + 1) : missId;
                var cond = new MirCondBranch(new MirLocalOperand(isLocal),
                    scope.CatchPreId(i), nextId);
                EmitMirBlock(i == 0 ? scope.DispatchId : scope.ChainId(i), linkInsts, cond,
                    i == 0 ? scope.DispatchBlock : null);
                // 命中前置垫：EXC_VAR=$mw.exc.N（步骤 5；Exception→Nullable
                // 位模式相同，普通 CopyLocal）
                EmitMirBlock(scope.CatchPreId(i),
                    new List<MirInst>
                    {
                        new MirCopyLocal(new MirLocalOperand(scope.ExcLocal), scope.ExcVarName),
                    },
                    new MirBranch(entries[i].Handler.Id));
            }
        }

        // finally 前置垫群 + 重抛/ret 出口块（§16.7 步骤 3/9/10）
        private void EmitFinallyPads(TryScope scope)
        {
            // 正常前置垫：EXC_VAR 显式写 null、comp=0
            EmitMirBlock(scope.FinPreNormalId, new List<MirInst>
            {
                NullLoad(scope),
                CompLoad(scope, 0),
            }, new MirBranch(scope.FinallyId!));
            // 异常前置垫（派发垫未命中入边：pending 已被派发垫 take，
            // $mw.exc.N 持异常对象）：EXC_VAR 写异常对象（步骤 7）
            EmitMirBlock(scope.FinPreExcId, new List<MirInst>
            {
                new MirCopyLocal(new MirLocalOperand(scope.ExcLocal), scope.ExcVarName),
                CompLoad(scope, scope.ThrowCode),
            }, new MirBranch(scope.FinallyId!));
            // 逃逸垫（catch 相位/内层路由器的异常入边：先 take 刷新
            // $mw.exc.N，再汇入异常前置垫）
            EmitMirBlock(scope.EscId,
                new List<MirInst> { new MirTakePending(scope.ExcLocal) },
                new MirBranch(scope.FinPreExcId), scope.EscBlock);
            // 穿越前置垫（ret/brk/cnt：恒写 null + comp=路由码）
            foreach (var (code, padId) in scope.Pads)
            {
                EmitMirBlock(padId, new List<MirInst>
                {
                    NullLoad(scope),
                    CompLoad(scope, code),
                }, new MirBranch(scope.FinallyId!));
            }
            // 重抛出口（路由器 throw 分支）：pending 再置 TLS 后沿异常边
            // 外传播；外层缺失 = 传播出函数（C 棒解析）
            var outer = OuterExcTarget(scope);
            EmitMirBlock(scope.RethrowId,
                new List<MirInst> { new MirThrow(new MirLocalOperand(scope.ExcLocal), outer) },
                outer != null ? (MirTerminator)new MirBranch(outer.Id) : new MirRetThrow());
            // ret 出口（return 穿越过本 try 时才需要）
            if (scope.NeedsRetBlock)
            {
                EmitMirBlock(scope.RetId, new List<MirInst>(),
                    new MirRet(_retvLocal != null ? new MirLocalOperand(_retvLocal) : null));
            }
        }

        // 路由器：finally 落出按 $mw.comp.N 分派（0=正常落出为 default）
        private void EmitRouter(TryScope scope)
        {
            var elements = new List<string>();
            var targets = new List<string>();
            foreach (var (code, targetId) in scope.RouterEntries)
            {
                elements.Add(code.ToString(CultureInfo.InvariantCulture));
                targets.Add(targetId);
            }
            var table = new BilSwitchTableResource(
                "mw.try." + scope.N + ".sw", ".i32", elements);
            EmitMirBlock(scope.RouterId, new List<MirInst>(),
                new MirSwitch(new MirLocalOperand(scope.CompLocal!), table, targets,
                    scope.AfterId));
        }

        // 无 finally 的未命中出口：直接 MirThrow 外层（步骤 7 的 EXC_VAR
        // 写以 finally 存在为前提，无 finally 无读者，不建垫）
        private void EmitMiss(TryScope scope)
        {
            var outer = OuterExcTarget(scope);
            EmitMirBlock(scope.MissId,
                new List<MirInst> { new MirThrow(new MirLocalOperand(scope.ExcLocal), outer) },
                outer != null ? (MirTerminator)new MirBranch(outer.Id) : new MirRetThrow());
        }

        // ===== throw / completion 解析 =====

        // throw 直译：raise 置 TLS pending 后沿异常边传播；无目标 =
        // 传播出函数（MirRetThrow 收尾，pending 已在 TLS）
        internal void EmitThrow(ThrowInstruction inst)
        {
            _f.EnsureOpen();
            var target = CurrentExcTarget();
            _f.CurrentInsts.Add(new MirThrow(
                new MirLocalOperand(inst.Exception.Name), target));
            _f.Terminate(target != null
                ? (MirTerminator)new MirBranch(target.Id)
                : new MirRetThrow());
        }

        // return：穿越有 finally 的 try 时改道路由器（值先存 $mw.retv，
        // 逐层外跳；嵌套 try 共享同一 fn 级 $mw.retv 保值）
        internal void EmitReturn(BilVariableOperand? value)
        {
            var target = ResolveFrom(_f.Scopes.Count - 1, CompletionKey.ForReturn());
            if (target == null)
            {
                _f.Terminate(new MirRet(value == null ? null : new MirLocalOperand(value.Name)));
                return;
            }
            if (value != null)
            {
                _f.CurrentInsts.Add(new MirCopyLocal(
                    new MirLocalOperand(value.Name), EnsureRetvLocal()));
            }
            _f.Terminate(new MirBranch(target));
        }

        internal string ResolveBreak(string breakIdVar)
        {
            return ResolveFrom(_f.Scopes.Count - 1, CompletionKey.ForBreak(breakIdVar))
                ?? throw new CompilerInternalException(
                    $"break/continue token 无宿 region: {breakIdVar}（fn {_f.FnSymbol}）");
        }

        internal string ResolveContinue(string breakIdVar)
        {
            return ResolveFrom(_f.Scopes.Count - 1, CompletionKey.ForContinue(breakIdVar))
                ?? throw new CompilerInternalException(
                    $"break/continue token 无宿 region: {breakIdVar}（fn {_f.FnSymbol}）");
        }

        // 词法栈自内向外解析 completion 落点：穿越有 finally 的 try
        //（finally 相位除外）改道其前置垫；try 自绑 BREAK_ID 消费为正常
        // 落出；无 finally 的 try 透明。返回 null = 函数级直出
        private string? ResolveFrom(int i, CompletionKey key)
        {
            for (; i >= 0; i--)
            {
                switch (_f.Scopes[i])
                {
                    case MirBuilder.RegionScope region
                        when key.Kind != CompletionKind.Return && region.BreakIdVar == key.Var:
                        if (key.Kind == CompletionKind.Continue && region.ContinueTarget == null)
                        {
                            throw new CompilerInternalException(
                                $"continue 命中非 loop region（fn {_f.FnSymbol}）");
                        }
                        return key.Kind == CompletionKind.Continue
                            ? region.ContinueTarget : region.BreakTarget;
                    case TryScope tryScope:
                        if (key.Var != null && key.Var == tryScope.BreakIdVar)
                        {
                            // continue 只命中 loop（§21.6 verifier 保证）；防御
                            if (key.Kind == CompletionKind.Continue)
                            {
                                throw new CompilerInternalException(
                                    $"continue 命中非 loop region（fn {_f.FnSymbol}）");
                            }
                            return tryScope.HasFinally && tryScope.Phase != TryPhase.Finally
                                ? tryScope.FinPreNormalId : tryScope.AfterId;
                        }
                        if (tryScope.HasFinally && tryScope.Phase != TryPhase.Finally)
                        {
                            return GetPad(tryScope, key);
                        }
                        break;
                }
            }
            return null;
        }

        // 穿越垫分配：同键共享；路由器目标 = 本 try 之外的续解析
        //（逐层外跳；return 直达函数级时经本 try 的 ret 出口块）
        private string GetPad(TryScope scope, CompletionKey key)
        {
            if (scope.CodeByKey.TryGetValue(key.Text, out var existing))
            {
                return existing.PadId;
            }
            var code = scope.NextCode++;
            var continuation = ResolveFrom(scope.Index - 1, key);
            string routerTarget;
            if (continuation != null)
            {
                routerTarget = continuation;
            }
            else
            {
                routerTarget = scope.RetId;
                scope.NeedsRetBlock = true;
            }
            var padId = "mw.try." + scope.N + ".fin.pre." + key.Suffix + code;
            scope.CodeByKey.Add(key.Text, (code, padId));
            scope.Pads.Add((code, padId));
            scope.RouterEntries.Add((code, routerTarget));
            return padId;
        }

        // 当前词法点的异常边目标：body 相位 = 本 try 派发垫；catch 相位
        // = 逃逸垫（catch 再抛视同未命中，不重进同表匹配；无 finally 则
        // 直通外层）；finally 相位 = 外层（覆盖待处理 completion）
        internal MirBlock? CurrentExcTarget() => ExcTargetFrom(_f.Scopes.Count - 1);

        private MirBlock? OuterExcTarget(TryScope scope) => ExcTargetFrom(scope.Index - 1);

        private MirBlock? ExcTargetFrom(int i)
        {
            for (; i >= 0; i--)
            {
                if (_f.Scopes[i] is not TryScope tryScope)
                {
                    continue;
                }
                switch (tryScope.Phase)
                {
                    case TryPhase.Body:
                        return tryScope.DispatchBlock;
                    case TryPhase.Catch:
                        return tryScope.EscBlock ?? ExcTargetFrom(i - 1);
                    default:
                        return ExcTargetFrom(i - 1);
                }
            }
            return null;
        }

        // ===== 小件 =====

        private string EnsureRetvLocal()
        {
            if (_retvLocal == null)
            {
                _retvLocal = "$mw.retv";
                _f.RegisterSyntheticLocal(_retvLocal, _f.ReturnType);
            }
            return _retvLocal;
        }

        // EXC_VAR 写 null：null 资源物化直落目标槽（§16.7 步骤 3）
        private static MirLoadResource NullLoad(TryScope scope)
        {
            return new MirLoadResource(
                new BilNullResource("mw.try." + scope.N + ".null", scope.ExcVarTypeRef),
                scope.ExcVarName);
        }

        private static MirLoadResource CompLoad(TryScope scope, int code)
        {
            return new MirLoadResource(
                new BilScalarResource("mw.try." + scope.N + ".c" + code,
                    BilScalarType.I32, code.ToString(CultureInfo.InvariantCulture)),
                scope.CompLocal!);
        }

        // catch 表项类型 → MirTypeCheck 目标（泛型占位降 typeid 局部，
        // 与 FlowBuilder.EmitTypeCheck 同口径）
        private static (string? Ref, MirOperand? Id) TypeCheckTarget(string typeRef)
        {
            if (GenericAbi.TryPlaceholderName(typeRef, out var name))
            {
                return (null, new MirLocalOperand(".generic." + name));
            }
            return (typeRef, null);
        }

        private static IReadOnlyList<BilCatchEntry> CatchEntries(TryInstruction inst)
        {
            return inst.CatchTable is BilCatchTableResource table
                ? table.Entries
                : throw new CompilerInternalException(
                    $"try 的 catch 表不是 catch-table 资源: {inst.CatchTable.Name}");
        }

        // 合成块落成：预建对象（派发垫/逃逸垫）填充内容，其余直接新建
        private void EmitMirBlock(string id, List<MirInst> insts, MirTerminator terminator,
            MirBlock? precreated = null)
        {
            if (precreated != null)
            {
                precreated.InstructionList.AddRange(insts);
                precreated.Terminator = terminator;
                _f.AppendBlock(precreated);
                return;
            }
            _f.AppendBlock(new MirBlock(id, insts, terminator));
        }
    }
}
