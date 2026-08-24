using System.Collections.Generic;
using RigiCompiler.Bil;

namespace RigiCompiler.Middleware
{
    // MIR 函数与模块（MIDDLEWARE_ARCHITECTURE §3 MW3：BIL 结构化块 → CFG
    // 基本块的确定性直译；具名局部 → alloca 槽，SSA 提升交给 LLVM mem2reg）。

    // 一次编译会话的 MIR 产物：逐函数平铺，MwContext.Mir 挂载
    public sealed class MirModule
    {
        public IReadOnlyList<MirFunction> Functions { get; }

        internal MirModule(IReadOnlyList<MirFunction> functions)
        {
            Functions = functions;
        }
    }

    public sealed class MirFunction
    {
        // 符号身份（驻留，与 MwSymbolTable 同一对象图）
        public MwMemberSymbol Symbol { get; }
        // 返回类型（.args 的 .return 条目）
        public MirType ReturnType { get; }
        // 普通参数（保序，不含 .return；MW1 拒绝 .this/泛型隐藏参数/可变包）
        public IReadOnlyList<MirLocal> Parameters { get; }
        // 全部具名局部（参数 + .vars），名称唯一
        public IReadOnlyList<MirLocal> Locals { get; }
        public IReadOnlyList<MirBlock> Blocks { get; }
        // 入口函数标记（§8.4 成员级 entrypoint 修饰符）
        public bool IsEntrypoint { get; }

        private readonly Dictionary<string, MirLocal> _localMap;

        internal MirFunction(MwMemberSymbol symbol, MirType returnType,
            IReadOnlyList<MirLocal> parameters, IReadOnlyList<MirLocal> locals,
            IReadOnlyList<MirBlock> blocks, bool isEntrypoint)
        {
            Symbol = symbol;
            ReturnType = returnType;
            Parameters = parameters;
            Locals = locals;
            Blocks = blocks;
            IsEntrypoint = isEntrypoint;
            _localMap = new Dictionary<string, MirLocal>(System.StringComparer.Ordinal);
            foreach (var local in locals)
            {
                _localMap.Add(local.Name, local);
            }
        }

        public MirLocal FindLocal(string name) => _localMap[name];
    }

    // 具名局部（发射期落 alloca 槽）
    public sealed class MirLocal
    {
        public string Name { get; }
        public MirType Type { get; }

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
        public IReadOnlyList<MirInst> Instructions { get; }
        public MirTerminator Terminator { get; }

        internal MirBlock(string id, IReadOnlyList<MirInst> instructions, MirTerminator terminator)
        {
            Id = id;
            Instructions = instructions;
            Terminator = terminator;
        }
    }
}
