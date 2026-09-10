using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;

namespace RigiCompiler.Middleware.Passes
{
    // Any/Object 的默认 helper（any_to_string/any_hash）不知道 Rigi override。
    // 先在 MIR 中按实际对象类型派发，未命中才调用 helper；回调可挂起，必须让
    // CoroutineSplit 与 ARC 看到真实调用边，不能从 C 偷调 Rigi 函数。
    // toString/hash 双通道同构：候选槽 = VTableSlots 里以
    // $toString()@.string / $hash()@.i64 结尾且有 fn 体的槽；没有任何
    // override 时分派链为空——调用点保持直调 helper（即 fallback 行为）。
    public sealed class BuiltinToStringDispatchPass : IMwStage
    {
        public string Name => "BuiltinToStringDispatch";
        private const string ToStringNative = "core::$any_to_string(value:.any)@.string";
        internal const string DispatchCanonical = "$mw.any.toString(value:.any)@.string";
        private const string HashNative = "core::$any_hash(value:.any)@.i64";
        internal const string HashDispatchCanonical = "$mw.any.hash(value:.any)@.i64";

        public void Run(MwContext context)
        {
            SynthesizeDispatch(context, ToStringNative, "$toString()@.string",
                DispatchCanonical, ".string");
            SynthesizeDispatch(context, HashNative, "$hash()@.i64",
                HashDispatchCanonical, ".i64");
        }

        // 单通道合成：扫 helper 调用点 + override 候选，按继承深度降序组
        // type.is 检查链；未命中 fallback 直调 helper（native 面语义）。
        // 无调用点或无候选（空链）时不发射任何 fn，调用点原样直调 helper。
        private static void SynthesizeDispatch(MwContext context, string native,
            string slotSuffix, string dispatchCanonical, string resultType)
        {
            var mir = context.Mir!;
            var functions = mir.Functions.ToDictionary(fn => fn.Symbol.Canonical, StringComparer.Ordinal);
            var sites = mir.Functions.SelectMany(owner => owner.Blocks.SelectMany(block =>
                    block.InstructionList.Select((inst, index) => (owner, block, inst, index))))
                .Where(site => site.inst is MirCall call && call.Target.Canonical == native
                    && !IsPrimitiveBox(site.owner, site.block, site.index, call)).ToList();
            if (sites.Count == 0) return;
            var candidates = context.Layout!.Plans
                .Where(plan => plan.Kind == TypeLayoutKind.Class)
                .Select(plan => (plan, method: plan.VTableSlots.LastOrDefault(slot =>
                    slot.EndsWith(slotSuffix, StringComparison.Ordinal)
                    && functions.ContainsKey(slot))))
                .Where(candidate => candidate.method != null)
                .OrderByDescending(candidate => Depth(candidate.plan))
                .ThenBy(candidate => candidate.plan.Symbol.Canonical, StringComparer.Ordinal).ToList();
            if (candidates.Count == 0) return;
            var symbol = ProxyBakeSupport.SyntheticMember(dispatchCanonical, null);
            var value = new MirLocal("value", MirType.Of(".any"));
            var result = new MirLocal("result", MirType.Of(resultType));
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
            var nativeTarget = ((MirCall)sites[0].inst).Target;
            fn.AddBlock(new MirBlock("fallback", new List<MirInst>
            {
                new MirCall(nativeTarget, new List<MirOperand> { receiver }, result.Name),
            }, new MirRet(new MirLocalOperand(result.Name))));
            foreach (var (_, block, inst, index) in sites)
            {
                var call = (MirCall)inst;
                block.InstructionList[index] = new MirCall(symbol, call.Args, call.Result, call.ExcTarget);
            }
            mir.AddFunction(fn);
        }

        // 紧邻的装箱已证明接收者是封闭内建值，不可能命中用户类 override。
        // 保留原 native 调用，避免整数格式化被无关的可挂起覆写传染。
        // 只利用本基本块的直接定义；Any、开放泛型与用户值类型仍走完整派发。
        private static bool IsPrimitiveBox(MirFunction owner, MirBlock block, int index,
            MirCall call)
        {
            if (index == 0 || call.Args.Count != 1
                || call.Args[0] is not MirLocalOperand argument
                || block.InstructionList[index - 1] is not MirBoxAny box
                || box.Target != argument.Name || box.Source is not MirLocalOperand source)
                return false;
            var type = owner.FindLocal(source.Name).Type;
            if (TypeLayout.IsTypeId(type)) return false;
            return TypeLayout.ClassifyElement(type, null).Kind
                is ArrayElementKind.Scalar or ArrayElementKind.String;
        }

        private static int Depth(TypeLayoutPlan plan)
        {
            var depth = 0;
            for (var parent = plan.BasePlan; parent != null; parent = parent.BasePlan) depth++;
            return depth;
        }
    }
}
