using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;

namespace RigiCompiler.Middleware.Passes
{
    // Any/Object 的默认文本 helper 不知道 Rigi override。先在 MIR 中
    // 按实际对象类型派发，未命中才调用 helper；回调可挂起，必须让
    // CoroutineSplit 与 ARC 看到真实调用边，不能从 C 偷调 Rigi 函数。
    public sealed class BuiltinToStringDispatchPass : IMwStage
    {
        public string Name => "BuiltinToStringDispatch";
        private const string Native = "core::$any_to_string(value:.any)@.string";
        internal const string DispatchCanonical = "$mw.any.toString(value:.any)@.string";

        public void Run(MwContext context)
        {
            var mir = context.Mir!;
            var functions = mir.Functions.ToDictionary(fn => fn.Symbol.Canonical, StringComparer.Ordinal);
            var sites = mir.Functions.SelectMany(fn => fn.Blocks)
                .SelectMany(block => block.InstructionList.Select((inst, index) => (block, inst, index)))
                .Where(site => site.inst is MirCall call && call.Target.Canonical == Native).ToList();
            if (sites.Count == 0) return;
            var candidates = context.Layout!.Plans
                .Where(plan => plan.Kind == TypeLayoutKind.Class)
                .Select(plan => (plan, method: plan.VTableSlots.LastOrDefault(slot =>
                    slot.EndsWith("$toString()@.string", StringComparison.Ordinal)
                    && functions.ContainsKey(slot))))
                .Where(candidate => candidate.method != null)
                .OrderByDescending(candidate => Depth(candidate.plan))
                .ThenBy(candidate => candidate.plan.Symbol.Canonical, StringComparer.Ordinal).ToList();
            if (candidates.Count == 0) return;
            var symbol = ProxyBakeSupport.SyntheticMember(DispatchCanonical, null);
            var value = new MirLocal("value", MirType.Of(".any"));
            var result = new MirLocal("result", MirType.Of(".string"));
            var fn = new MirFunction(symbol, result.Type, new List<MirLocal> { value },
                new List<MirLocal> { value, result }, new List<MirBlock>(), false);
            var receiver = new MirLocalOperand(value.Name);
            for (var i = 0; i < candidates.Count; i++)
            {
                var (plan, method) = candidates[i];
                var check = "check." + i;
                var hit = "hit." + i;
                var next = i + 1 == candidates.Count ? "fallback" : "check." + (i + 1);
                var condition = new MirLocal("condition." + i, MirType.Of(".bool"));
                var typed = new MirLocal("typed." + i, MirType.Of(plan.Symbol.Canonical));
                fn.AddLocal(condition);
                fn.AddLocal(typed);
                fn.AddBlock(new MirBlock(i == 0 ? "entry" : check,
                    new List<MirInst> { new MirTypeCheck(MirTypeCheckKind.Is, receiver,
                        plan.Symbol.Canonical, null, condition.Name) },
                    new MirCondBranch(new MirLocalOperand(condition.Name), hit, next)));
                fn.AddBlock(new MirBlock(hit, new List<MirInst>
                {
                    new MirCopyLocal(receiver, typed.Name),
                    new MirCall(functions[method!].Symbol,
                        new List<MirOperand> { new MirLocalOperand(typed.Name) }, result.Name),
                }, new MirRet(new MirLocalOperand(result.Name))));
            }
            var native = ((MirCall)sites[0].inst).Target;
            fn.AddBlock(new MirBlock("fallback", new List<MirInst>
            {
                new MirCall(native, new List<MirOperand> { receiver }, result.Name),
            }, new MirRet(new MirLocalOperand(result.Name))));
            foreach (var (block, inst, index) in sites)
            {
                var call = (MirCall)inst;
                block.InstructionList[index] = new MirCall(symbol, call.Args, call.Result, call.ExcTarget);
            }
            mir.AddFunction(fn);
        }

        private static int Depth(TypeLayoutPlan plan)
        {
            var depth = 0;
            for (var parent = plan.BasePlan; parent != null; parent = parent.BasePlan) depth++;
            return depth;
        }
    }
}
