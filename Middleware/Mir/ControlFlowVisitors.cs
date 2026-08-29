using RigiCompiler.Bil;

namespace RigiCompiler.Middleware.Mir
{
    // BIL→MIR 控制流簇：if/loop/switch/call-block 的 RegionScope 在
    // Enter 压栈、Exit 弹栈 + 封 merge；try/throw 委托 TryExpander。

    internal sealed class IfLowering : MirLowerVisitor<IfLowering, IfInstruction>
    {
        private string _mergeId = "";

        protected override void Enter(IfInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            _mergeId = flow.SyntheticId("if.end");
            flow.Terminate(new MirCondBranch(flow.Local(inst.Condition), inst.ThenBlock.Id,
                inst.ElseBlock?.Id ?? _mergeId));
            flow.PushScope(new RegionScope(inst.BreakId.Name, _mergeId, null));
        }

        protected override void VisitCore(IfInstruction inst, FlowBuilder flow)
        {
            flow.EmitChildBlock(inst.ThenBlock, _mergeId);
            if (inst.ElseBlock != null)
            {
                flow.EmitChildBlock(inst.ElseBlock, _mergeId);
            }
        }

        protected override void Exit(IfInstruction inst, FlowBuilder flow)
        {
            flow.PopScope();
            flow.SealAndStart(_mergeId);
        }
    }

    internal sealed class LoopLowering : MirLowerVisitor<LoopLowering, LoopInstruction>
    {
        private string _exitId = "";

        protected override void Enter(LoopInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            _exitId = flow.SyntheticId("loop.end");
            var continueId = inst.EnumBlock?.Id ?? inst.Judge.Id;
            flow.PushScope(new RegionScope(inst.BreakId.Name, _exitId, continueId));
        }

        protected override void VisitCore(LoopInstruction inst, FlowBuilder flow)
        {
            var continueId = inst.EnumBlock?.Id ?? inst.Judge.Id;
            if (inst.IsRev)
            {
                flow.Terminate(new MirBranch(inst.Body.Id));
                flow.EmitChildBlock(inst.Body, continueId);
                if (inst.EnumBlock != null)
                {
                    flow.EmitChildBlock(inst.EnumBlock, inst.Judge.Id);
                }
                flow.SealAndStart(inst.Judge.Id);
                if (flow.EmitBlock(inst.Judge))
                {
                    flow.Terminate(new MirCondBranch(flow.Local(inst.Condition), inst.Body.Id,
                        _exitId));
                }
            }
            else
            {
                flow.Terminate(new MirBranch(inst.Judge.Id));
                flow.SealAndStart(inst.Judge.Id);
                if (flow.EmitBlock(inst.Judge))
                {
                    flow.Terminate(new MirCondBranch(flow.Local(inst.Condition), inst.Body.Id,
                        _exitId));
                }
                flow.EmitChildBlock(inst.Body, continueId);
                if (inst.EnumBlock != null)
                {
                    flow.EmitChildBlock(inst.EnumBlock, inst.Judge.Id);
                }
            }
        }

        protected override void Exit(LoopInstruction inst, FlowBuilder flow)
        {
            flow.PopScope();
            flow.SealAndStart(_exitId);
        }
    }

    internal sealed class SwitchLowering : MirLowerVisitor<SwitchLowering, SwitchInstruction>
    {
        private string _mergeId = "";

        protected override void Enter(SwitchInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            if (inst.Table is not BilSwitchTableResource table)
            {
                throw new CompilerInternalException($"switch 的表不是 switch-table（fn {flow.FnSymbol}）");
            }
            _mergeId = flow.SyntheticId("switch.end");
            var itemTargets = new System.Collections.Generic.List<string>(inst.ItemBlocks.Count);
            foreach (var item in inst.ItemBlocks)
            {
                itemTargets.Add(item.Id);
            }
            flow.Terminate(new MirSwitch(flow.Local(inst.Selector), table, itemTargets,
                inst.DefaultBlock.Id));
            flow.PushScope(new RegionScope(inst.BreakId.Name, _mergeId, null));
        }

        protected override void VisitCore(SwitchInstruction inst, FlowBuilder flow)
        {
            foreach (var item in inst.ItemBlocks)
            {
                flow.EmitChildBlock(item, _mergeId);
            }
            flow.EmitChildBlock(inst.DefaultBlock, _mergeId);
        }

        protected override void Exit(SwitchInstruction inst, FlowBuilder flow)
        {
            flow.PopScope();
            flow.SealAndStart(_mergeId);
        }
    }

    internal sealed class CallBlockLowering : MirLowerVisitor<CallBlockLowering, CallBlockInstruction>
    {
        private string _mergeId = "";

        protected override void Enter(CallBlockInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            _mergeId = flow.SyntheticId("call.end");
            flow.Terminate(new MirBranch(inst.Block.Id));
            flow.PushScope(new RegionScope(inst.BreakId.Name, _mergeId, null));
        }

        protected override void VisitCore(CallBlockInstruction inst, FlowBuilder flow)
        {
            flow.EmitChildBlock(inst.Block, _mergeId);
        }

        protected override void Exit(CallBlockInstruction inst, FlowBuilder flow)
        {
            flow.PopScope();
            flow.SealAndStart(_mergeId);
        }
    }

    internal sealed class BreakLowering : MirLowerVisitor<BreakLowering, BreakInstruction>
    {
        protected override void VisitCore(BreakInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Terminate(new MirBranch(flow.Tries.ResolveBreak(inst.BreakId.Name)));
        }
    }

    internal sealed class ContinueLowering : MirLowerVisitor<ContinueLowering, ContinueInstruction>
    {
        protected override void VisitCore(ContinueInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Terminate(new MirBranch(flow.Tries.ResolveContinue(inst.BreakId.Name)));
        }
    }

    internal sealed class RetLowering : MirLowerVisitor<RetLowering, RetInstruction>
    {
        protected override void VisitCore(RetInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Tries.EmitReturn(inst.Value);
        }
    }

    internal sealed class HintLowering : MirLowerVisitor<HintLowering, HintInstruction>
    {
        protected override void VisitCore(HintInstruction inst, FlowBuilder flow)
        {
            // §18 route dispatcher 标注，codegen 无语义
        }
    }

    internal sealed class TryLowering : MirLowerVisitor<TryLowering, TryInstruction>
    {
        protected override void VisitCore(TryInstruction inst, FlowBuilder flow)
        {
            flow.Tries.ExpandTry(inst);
        }
    }

    internal sealed class ThrowLowering : MirLowerVisitor<ThrowLowering, ThrowInstruction>
    {
        protected override void VisitCore(ThrowInstruction inst, FlowBuilder flow)
        {
            flow.Tries.EmitThrow(inst);
        }
    }
}
