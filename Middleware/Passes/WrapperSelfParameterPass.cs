using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;

namespace RigiCompiler.Middleware.Passes
{
    // proxy 烘焙已知精确宿主形状，self 因而直接使用该形状的参数，
    // 不需要 Any 类型擦除、实例回指、相对地址或特殊 ARC 槽。
    // 在协程切分前完成：宿主参数由普通帧保存及 refMap 规则管理寿命。
    internal sealed class WrapperSelfParameterPass : IMwStage
    {
        internal const string SelfParameter = ProxyBakeSupport.InnerHostLocal;
        public string Name => "WrapperSelfParameter";

        public void Run(MwContext context)
        {
            var mir = context.Mir ?? throw new CompilerInternalException("wrapper 参数化要求 MIR");
            var needsHost = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < mir.FunctionList.Count; index++)
            {
                var fn = mir.FunctionList[index];
                if (!fn.Blocks.Any(b => b.Instructions.Any(i => i is MirGetSelf))) continue;
                if (!fn.TryFindLocal(ProxyBakeSupport.InnerHostLocal, out var host))
                    throw new MwNotSupportedException("未特化的 wrapper proxy 不能直接发射: " + fn.Symbol.Canonical);
                var parameters = new List<MirLocal>(fn.Parameters) { host };
                var locals = new List<MirLocal>(fn.Locals);
                foreach (var block in fn.Blocks)
                    for (var i = 0; i < block.InstructionList.Count; i++)
                        if (block.InstructionList[i] is MirGetSelf read)
                        {
                            if (read.Target == SelfParameter)
                            {
                                // inner 必须直接使用宿主参数；复制值宿主会让被代理方法的写入丢失。
                                block.InstructionList.RemoveAt(i--);
                                continue;
                            }
                            // 所有 get.self 的类型已由模板特化代入；不允许借此转换布局。
                            if (fn.FindLocal(read.Target).Type.Canonical != host.Type.Canonical)
                                throw new MwNotSupportedException("wrapper self 类型与宿主不一致: " + fn.Symbol.Canonical);
                            block.InstructionList[i] = new MirCopyLocal(new MirLocalOperand(SelfParameter), read.Target);
                        }
                mir.FunctionList[index] = new MirFunction(fn.Symbol, fn.ReturnType, parameters,
                    locals, fn.Blocks, fn.IsEntrypoint, fn.IsAsync, fn.IsCoroutineResume,
                    fn.IsPollProbe, fn.IsPlainResume);
                needsHost.Add(fn.Symbol.Canonical);
            }
            foreach (var fn in mir.Functions)
            {
                // 烘焙临时的宿主来源必须唯一；不能按指令遍历顺序猜分支状态。
                var hosts = new Dictionary<string, MirOperand>(StringComparer.Ordinal);
                var conflicts = new HashSet<string>(StringComparer.Ordinal);
                foreach (var block in fn.Blocks)
                    foreach (var inst in block.Instructions)
                    {
                        switch (inst)
                        {
                            case MirGetWrapper v: Bind(v.Target, v.Host); break;
                            case MirGetWrapperField v: Bind(v.Target, v.Host); break;
                            case MirGetWrapperAddr v: Bind(v.Target, v.Host); break;
                            case MirGetWrapperFieldAddr v: Bind(v.Target, v.Host); break;
                            case MirGetWrapperMethodAddr v: Bind(v.Target, v.Host); break;
                        }
                    }
                foreach (var block in fn.Blocks)
                    for (var i = 0; i < block.InstructionList.Count; i++)
                    {
                        if (block.InstructionList[i] is not MirCall call
                            || !needsHost.Contains(call.Target.Canonical)) continue;
                        if (call.Args.Count == 0 || call.Args[0] is not MirLocalOperand receiver
                            || conflicts.Contains(receiver.Name)
                            || !hosts.TryGetValue(receiver.Name, out var host))
                            throw new MwNotSupportedException("wrapper 调用缺少唯一宿主实参: " + call.Target.Canonical);
                        var args = new List<MirOperand>(call.Args) { host };
                        block.InstructionList[i] = new MirCall(call.Target, args, call.Result,
                            call.ExcTarget, call.OperatorDispatch, call.HostConstructedRef);
                    }

                void Bind(string name, MirOperand host)
                {
                    if (hosts.TryGetValue(name, out var previous)
                        && (previous is not MirLocalOperand a || host is not MirLocalOperand b || a.Name != b.Name))
                        conflicts.Add(name);
                    else hosts[name] = host;
                }
            }
        }
    }
}
