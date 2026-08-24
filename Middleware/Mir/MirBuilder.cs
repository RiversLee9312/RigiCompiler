using System.Collections.Generic;
using RigiCompiler.Bil;

namespace RigiCompiler.Middleware
{
    /// <summary>
    /// MirBuilder（MW3 层）：BIL 结构化块 → MIR CFG 的确定性直译
    /// （MIDDLEWARE_ARCHITECTURE §3：输入已结构化，无需 Relooper/Stackifier，
    /// 输出天然 reducible CFG）。输入已过 BilVerifier 门禁，故形状不变量
    /// （恰一 entrypoint block、break/continue token 作用域、块恰好被一个
    /// 父 region 引用等）直接依赖，不复查。构建顺序由 MirReachability 给出
    /// （调用图可达闭包，模块级 DCE）。
    /// </summary>
    public static class MirBuilder
    {
        public static MirModule Build(MwContext context)
        {
            var bySymbol = new Dictionary<string, BilFunction>(System.StringComparer.Ordinal);
            foreach (var bilFn in context.Module.Functions)
            {
                bySymbol.Add(bilFn.Symbol, bilFn);
            }

            var order = new List<MirFunction>();
            foreach (var symbol in MirReachability.ResolveBuildOrder(context))
            {
                order.Add(BuildFunction(context, bySymbol[symbol]));
            }
            var module = new MirModule(order);
            context.Mir = module;
            return module;
        }

        private static MirFunction BuildFunction(MwContext context, BilFunction bilFn)
        {
            var symbol = context.Symbols.FindMember(bilFn.Symbol)
                ?? throw new MwNotSupportedException(
                    $"MW1 不支持无符号段声明的 fn（预定义合成体）: {bilFn.Symbol}");

            // .args：.return 在前，其后普通参数；MW1 拒绝隐藏参数形态
            MirType? returnType = null;
            var parameters = new List<MirLocal>();
            var locals = new List<MirLocal>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var arg in bilFn.Args)
            {
                if (arg.Name == ".return")
                {
                    returnType = MirType.Of(arg.TypeRef);
                    continue;
                }
                if (arg.Name.StartsWith('.'))
                {
                    throw new MwNotSupportedException(
                        $"MW1 不支持隐藏参数 {arg.Name}（fn {bilFn.Symbol}）");
                }
                AddLocal(parameters, locals, seen, arg.Name, arg.TypeRef, bilFn.Symbol);
            }
            if (returnType == null)
            {
                throw new CompilerInternalException($"fn 缺 .return 条目: {bilFn.Symbol}");
            }

            foreach (var varDecl in bilFn.Vars)
            {
                AddLocal(null, locals, seen, varDecl.Name, varDecl.TypeRef, bilFn.Symbol);
            }

            // §9.4：恰一个 entrypoint block（verifier 保证）；防御
            BilBlock? entryBlock = null;
            foreach (var block in bilFn.Blocks)
            {
                if (block.Modifiers.Contains(BilBlockModifier.Entrypoint))
                {
                    entryBlock = block;
                    break;
                }
            }
            if (entryBlock == null)
            {
                throw new CompilerInternalException($"fn 缺 entrypoint block: {bilFn.Symbol}");
            }

            var localMap = new Dictionary<string, MirLocal>(System.StringComparer.Ordinal);
            foreach (var local in locals)
            {
                localMap.Add(local.Name, local);
            }
            var blocks = new FlowBuilder(context, bilFn.Symbol, localMap).Build(entryBlock);

            var isEntrypoint = symbol.HasKeyword(BilKeyword.Entrypoint);
            return new MirFunction(symbol, returnType, parameters, locals, blocks, isEntrypoint);
        }

        private static void AddLocal(List<MirLocal>? parameters, List<MirLocal> locals,
            HashSet<string> seen, string name, string typeRef, string fnSymbol)
        {
            // .vars 与参数共名属生成方违约（verifier 已查）；此处防御
            if (!seen.Add(name))
            {
                throw new CompilerInternalException($"fn {fnSymbol} 局部重名: {name}");
            }
            var local = new MirLocal(name, MirType.Of(typeRef));
            locals.Add(local);
            parameters?.Add(local);
        }

        // BIL 结构化块 → CFG 直译器：结构化 region 指令（if/loop/switch/
        // call blk）递归展开为基本块图，break/continue 目标在展开期静态
        // 解析（breakid 变量 → 宿 region 的合成块 id；§21.5/§21.6 保证
        // 绑定唯一、作用域正确、continue 只命中 loop）。
        private sealed class FlowBuilder
        {
            private readonly MwContext _context;
            private readonly string _fnSymbol;
            private readonly IReadOnlyDictionary<string, MirLocal> _localMap;
            private readonly List<MirBlock> _blocks = new();
            private readonly List<RegionTarget> _regions = new();   // region 栈，顶在末尾
            private string _currentId = "";
            private List<MirInst> _currentInsts = new();
            private MirTerminator? _terminator;
            private int _syntheticCounter;

            // break/continue 的宿 region：breakid 变量名 → 边界块 id
            // （BreakTarget = region 后汇聚/出口块；ContinueTarget = loop 的
            // enum 块（缺省 judge 块），非 loop region 为 null）
            private sealed record RegionTarget(string BreakIdVar, string BreakTarget, string? ContinueTarget);

            internal FlowBuilder(MwContext context, string fnSymbol,
                IReadOnlyDictionary<string, MirLocal> localMap)
            {
                _context = context;
                _fnSymbol = fnSymbol;
                _localMap = localMap;
            }

            internal IReadOnlyList<MirBlock> Build(BilBlock entryBlock)
            {
                StartNewBlock(entryBlock.Id);
                EmitBlock(entryBlock);
                // 函数尾落出：仅当双分支均终结的汇聚块不可达时发生（entry
                // 落尾已被 verifier 拒绝）；补 unreachable 使块形态合法
                if (_terminator == null)
                {
                    Terminate(new MirUnreachable());
                }
                SealCurrentBlock();
                return _blocks;
            }

            // 译入一个 BIL 块的指令流；返回 true = 指令流结束后控制流自然
            // 落出（当前块未终结，调用者接 region 边界边）
            private bool EmitBlock(BilBlock block)
            {
                foreach (var inst in block.Instructions)
                {
                    EmitInstruction(inst);
                }
                return _terminator == null;
            }

            private void EmitInstruction(BilInstruction inst)
            {
                switch (inst)
                {
                    case IfInstruction ifInst:
                        EmitIf(ifInst);
                        break;
                    case LoopInstruction loop:
                        EmitLoop(loop);
                        break;
                    case SwitchInstruction sw:
                        EmitSwitch(sw);
                        break;
                    case CallBlockInstruction callBlock:
                        EmitCallBlock(callBlock);
                        break;
                    case BreakInstruction brk:
                        EnsureOpen();
                        Terminate(new MirBranch(FindRegion(brk.BreakId.Name).BreakTarget));
                        break;
                    case ContinueInstruction cont:
                        EnsureOpen();
                        var region = FindRegion(cont.BreakId.Name);
                        if (region.ContinueTarget == null)
                        {
                            throw new CompilerInternalException(
                                $"continue 命中非 loop region（fn {_fnSymbol}）");
                        }
                        Terminate(new MirBranch(region.ContinueTarget));
                        break;
                    case RetInstruction ret:
                        EnsureOpen();
                        Terminate(new MirRet(ret.Value == null ? null : Local(ret.Value)));
                        break;
                    case HintInstruction:
                        // §18 route dispatcher 标注，codegen 无语义
                        break;
                    case TryInstruction:
                    case ThrowInstruction:
                        throw new MwNotSupportedException(
                            $"MW3 暂不支持 {inst.Opcode}（随 MW9 异常机制落地）");
                    default:
                        EnsureOpen();
                        EmitSimple(inst);
                        break;
                }
            }

            // §16.2：cond → then / (else|merge)，分支落出汇于 merge
            private void EmitIf(IfInstruction inst)
            {
                EnsureOpen();
                var mergeId = SyntheticId("if.end");
                Terminate(new MirCondBranch(Local(inst.Condition), inst.ThenBlock.Id,
                    inst.ElseBlock?.Id ?? mergeId));
                PushRegion(inst.BreakId.Name, mergeId, null);
                EmitChildBlock(inst.ThenBlock, mergeId);
                if (inst.ElseBlock != null)
                {
                    EmitChildBlock(inst.ElseBlock, mergeId);
                }
                PopRegion();
                SealAndStart(mergeId);
            }

            // §16.3 正向：judge → 读 cond →（false 出）/（true body → enum →
            // judge）；§16.4 反向：body → enum → judge → 读 cond →（true 回
            // body）。continue 恒跳 enum（缺省 judge）再走 judge（§16.5）
            private void EmitLoop(LoopInstruction inst)
            {
                EnsureOpen();
                var exitId = SyntheticId("loop.end");
                var continueId = inst.EnumBlock?.Id ?? inst.Judge.Id;
                PushRegion(inst.BreakId.Name, exitId, continueId);
                if (inst.IsRev)
                {
                    Terminate(new MirBranch(inst.Body.Id));
                    EmitChildBlock(inst.Body, continueId);
                    if (inst.EnumBlock != null)
                    {
                        EmitChildBlock(inst.EnumBlock, inst.Judge.Id);
                    }
                    SealAndStart(inst.Judge.Id);
                    if (EmitBlock(inst.Judge))
                    {
                        Terminate(new MirCondBranch(Local(inst.Condition), inst.Body.Id, exitId));
                    }
                }
                else
                {
                    Terminate(new MirBranch(inst.Judge.Id));
                    SealAndStart(inst.Judge.Id);
                    if (EmitBlock(inst.Judge))
                    {
                        Terminate(new MirCondBranch(Local(inst.Condition), inst.Body.Id, exitId));
                    }
                    EmitChildBlock(inst.Body, continueId);
                    if (inst.EnumBlock != null)
                    {
                        EmitChildBlock(inst.EnumBlock, inst.Judge.Id);
                    }
                }
                PopRegion();
                SealAndStart(exitId);
            }

            // §16.6：常量表匹配（表序首个 cmp.eq 命中），item/default 落出
            // 汇于 merge，无穿透
            private void EmitSwitch(SwitchInstruction inst)
            {
                EnsureOpen();
                if (inst.Table is not BilSwitchTableResource table)
                {
                    throw new CompilerInternalException($"switch 的表不是 switch-table（fn {_fnSymbol}）");
                }
                var mergeId = SyntheticId("switch.end");
                var itemTargets = new List<string>(inst.ItemBlocks.Count);
                foreach (var item in inst.ItemBlocks)
                {
                    itemTargets.Add(item.Id);
                }
                Terminate(new MirSwitch(Local(inst.Selector), table, itemTargets, inst.DefaultBlock.Id));
                PushRegion(inst.BreakId.Name, mergeId, null);
                foreach (var item in inst.ItemBlocks)
                {
                    EmitChildBlock(item, mergeId);
                }
                EmitChildBlock(inst.DefaultBlock, mergeId);
                PopRegion();
                SealAndStart(mergeId);
            }

            // §16.1：进入目标 block，正常落出返回 call 之后；不建调用帧
            private void EmitCallBlock(CallBlockInstruction inst)
            {
                EnsureOpen();
                var mergeId = SyntheticId("call.end");
                Terminate(new MirBranch(inst.Block.Id));
                PushRegion(inst.BreakId.Name, mergeId, null);
                EmitChildBlock(inst.Block, mergeId);
                PopRegion();
                SealAndStart(mergeId);
            }

            // ===== 顺序指令（MW1 面） =====

            private void EmitSimple(BilInstruction inst)
            {
                switch (inst)
                {
                    case LoadInstruction load:
                        _currentInsts.Add(new MirLoadResource(load.Resource, load.Target.Name));
                        break;
                    case SetVarInstruction setVar:
                        _currentInsts.Add(new MirCopyLocal(Local(setVar.Source), setVar.Target.Name));
                        break;
                    case GetVarInstruction getVar:
                        _currentInsts.Add(new MirCopyLocal(Local(getVar.Source), getVar.Target.Name));
                        break;
                    case BinaryIntrinsicInstruction binary:
                        _currentInsts.Add(new MirBinaryIntrinsic(binary.Op,
                            Local(binary.Left), Local(binary.Right),
                            TypeOf(binary.Left.Name), TypeOf(binary.Right.Name),
                            TypeOf(binary.Target.Name), binary.Target.Name));
                        break;
                    case UnaryIntrinsicInstruction unary:
                        _currentInsts.Add(new MirUnaryIntrinsic(unary.Op, Local(unary.Operand),
                            TypeOf(unary.Operand.Name), TypeOf(unary.Target.Name), unary.Target.Name));
                        break;
                    case InvokeInstruction invoke:
                        _currentInsts.Add(new MirCall(ResolveTarget(invoke.Method.Symbol),
                            Locals(invoke.Arguments), invoke.Target.Name));
                        break;
                    case InvokeNoResultInstruction invokeNoResult:
                        _currentInsts.Add(new MirCall(ResolveTarget(invokeNoResult.Method.Symbol),
                            Locals(invokeNoResult.Arguments), null));
                        break;
                    default:
                        throw new MwNotSupportedException($"MW3 不支持指令 {inst.Opcode}（fn {_fnSymbol}）");
                }
            }

            // ===== 块状态机 =====

            // 当前块已被前一指令终结时，同块后续指令不可达（abrupt
            // completion 语义）：开死块继续直译（保持忠实，裁减交 LLVM）
            private void EnsureOpen()
            {
                if (_terminator != null)
                {
                    SealCurrentBlock();
                    StartNewBlock(SyntheticId("dead"));
                }
            }

            // 子 region 块：译入后落出接 exitId 边
            private void EmitChildBlock(BilBlock child, string exitId)
            {
                SealAndStart(child.Id);
                if (EmitBlock(child))
                {
                    Terminate(new MirBranch(exitId));
                }
            }

            private void SealAndStart(string id)
            {
                SealCurrentBlock();
                StartNewBlock(id);
            }

            private void StartNewBlock(string id)
            {
                _currentId = id;
                _currentInsts = new List<MirInst>();
                _terminator = null;
            }

            private void Terminate(MirTerminator terminator)
            {
                _terminator = terminator;
            }

            private void SealCurrentBlock()
            {
                // 封存即基本块定型，必须有终结符；缺失即直译器自身 bug
                if (_terminator == null)
                {
                    throw new CompilerInternalException($"block {_currentId} 未终结即封存（fn {_fnSymbol}）");
                }
                _blocks.Add(new MirBlock(_currentId, _currentInsts, _terminator));
            }

            private string SyntheticId(string kind)
            {
                return "mw." + kind + "." + _syntheticCounter++;
            }

            private void PushRegion(string breakIdVar, string breakTarget, string? continueTarget)
            {
                _regions.Add(new RegionTarget(breakIdVar, breakTarget, continueTarget));
            }

            private void PopRegion()
            {
                _regions.RemoveAt(_regions.Count - 1);
            }

            private RegionTarget FindRegion(string breakIdVar)
            {
                for (var i = _regions.Count - 1; i >= 0; i--)
                {
                    if (_regions[i].BreakIdVar == breakIdVar)
                    {
                        return _regions[i];
                    }
                }
                // verifier §21.5/§21.6 保证 token 作用域正确；防御
                throw new CompilerInternalException($"break/continue token 无宿 region: {breakIdVar}（fn {_fnSymbol}）");
            }

            // ===== 操作数与目标解析 =====

            private MirLocalOperand Local(BilVariableOperand operand) => new(operand.Name);

            private MirType TypeOf(string name) => _localMap[name].Type;

            private MwMemberSymbol ResolveTarget(string symbol)
            {
                return _context.Symbols.FindMember(symbol)
                    ?? throw new MwNotSupportedException(
                        $"MW1 不支持调用无符号段声明的预定义符号: {symbol}（fn {_fnSymbol}）");
            }

            private static List<MirOperand> Locals(IReadOnlyList<BilVariableOperand> operands)
            {
                var list = new List<MirOperand>(operands.Count);
                foreach (var operand in operands)
                {
                    list.Add(new MirLocalOperand(operand.Name));
                }
                return list;
            }
        }
    }
}
