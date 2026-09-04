using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Mir
{
    // MIR 函数与模块（MIDDLEWARE_ARCHITECTURE §3 MW3：BIL 结构化块 → CFG
    // 基本块的确定性直译；具名局部 → alloca 槽，SSA 提升交给 LLVM mem2reg）。

    // 一次编译会话的 MIR 产物：逐函数平铺，MwContext.Mir 挂载
    public sealed class MirModule
    {
        public IReadOnlyList<MirFunction> Functions { get; }

        internal MirModule(IReadOnlyList<MirFunction> functions)
        {
            Functions = functions as List<MirFunction> ?? new List<MirFunction>(functions);
        }

        internal List<MirFunction> FunctionList => (List<MirFunction>)Functions;

        internal void AddFunction(MirFunction fn) => FunctionList.Add(fn);
    }

    public sealed class MirFunction
    {
        // 符号身份（驻留，与 MwSymbolTable 同一对象图）
        public MwMemberSymbol Symbol { get; }
        // 返回类型（.args 的 .return 条目）
        public MirType ReturnType { get; }
        // 参数（保序，不含 .return；含 .this / .generic.* / .vargs.* / .kwargs.*）
        public IReadOnlyList<MirLocal> Parameters { get; }
        // 全部具名局部（参数 + .vars + pass 合成槽），名称唯一
        public IReadOnlyList<MirLocal> Locals => _locals;
        public IReadOnlyList<MirBlock> Blocks { get; }
        // 入口函数标记（§8.4 成员级 entrypoint 修饰符）
        public bool IsEntrypoint { get; }
        // MW11a：async 成员（BIL fn 的 async 关键字随符号带入）——
        // CoroutineSplitPass 的处理对象；split 后 stub 保留此标记
        public bool IsAsync { get; }
        // MW11a：协程 resume 状态机 fn（CoroutineSplitPass 合成）——
        // RcInjection 传播垫尾部分叉（棒5a：失败终态序列 + ret DONE）
        // 与 frame 参数借用约定的识别标记
        public bool IsCoroutineResume { get; }
        // MW11b 棒3：PollingAlarm 探测 fn（$mw.poll_probe，CoroutineSplitPass
        // 懒合成）——棒5a 起是普通 MIR fn（native 不再回调，探测由恢复块
        // 直调）；RcInjection 传播垫尾分叉（releases + ret -1，pending
        // 保持置位由恢复块失败尾取走）与 alarm 参数借用约定的识别标记
        public bool IsPollProbe { get; }
        // B-1（非 async 挂起点栈式跨界）：tainted 普通 fn 的 resume
        // 状态机 fn（CoroutineSplitPass 合成）——与 async resume 共用
        // i32(ptr) C ABI 与 frame 借用约定，但 frame 所有权归调用方
        //（不经 MirCoroutineCreate move），DONE 出口不做最终 release；
        // 传播垫尾 = release 配平 + ret FAILED（pending 保持置位沿
        // 调用链上传），无 Task 终态序列
        public bool IsPlainResume { get; }

        private readonly List<MirLocal> _locals;
        private readonly Dictionary<string, MirLocal> _localMap;

        internal MirFunction(MwMemberSymbol symbol, MirType returnType,
            IReadOnlyList<MirLocal> parameters, IReadOnlyList<MirLocal> locals,
            IReadOnlyList<MirBlock> blocks, bool isEntrypoint,
            bool isAsync = false, bool isCoroutineResume = false,
            bool isPollProbe = false, bool isPlainResume = false)
        {
            Symbol = symbol;
            ReturnType = returnType;
            Parameters = parameters;
            _locals = locals as List<MirLocal> ?? new List<MirLocal>(locals);
            Blocks = blocks;
            IsEntrypoint = isEntrypoint;
            IsAsync = isAsync;
            IsCoroutineResume = isCoroutineResume;
            IsPollProbe = isPollProbe;
            IsPlainResume = isPlainResume;
            _localMap = new Dictionary<string, MirLocal>(System.StringComparer.Ordinal);
            foreach (var local in _locals)
            {
                _localMap.Add(local.Name, local);
            }
        }

        public MirLocal FindLocal(string name) => _localMap[name];

        internal bool TryFindLocal(string name, out MirLocal local) =>
            _localMap.TryGetValue(name, out local!);

        internal void AddLocal(MirLocal local)
        {
            _locals.Add(local);
            _localMap.Add(local.Name, local);
        }

        // pass 合成块追加（RcInjection 传播垫；Blocks 底层即构建期 List）
        internal void AddBlock(MirBlock block) => ((List<MirBlock>)Blocks).Add(block);
    }

    // 具名局部（发射期落 alloca 槽）
    public sealed class MirLocal
    {
        public string Name { get; }
        public MirType Type { get; internal set; }

        internal MirLocal(string name, MirType type)
        {
            Name = name;
            Type = type;
        }
    }

    // CFG 基本块：顺序指令 + 终结符（BIL 结构化块在本层拍平为 Br/CondBr/
    // Switch/Ret/Unreachable 终结的基本块图，天然 reducible）
    public sealed class MirBlock
    {
        public string Id { get; }
        // 仅 MirBuilder 构建期与 Passes/ 改写期可写，Emit 只读消费
        private readonly List<MirInst> _instructions;
        public IReadOnlyList<MirInst> Instructions => _instructions;
        internal List<MirInst> InstructionList => _instructions;
        public MirTerminator Terminator { get; internal set; }

        internal MirBlock(string id, List<MirInst> instructions, MirTerminator terminator)
        {
            Id = id;
            _instructions = instructions;
            Terminator = terminator;
        }
    }
}
