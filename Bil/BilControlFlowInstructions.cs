using System.Collections.Generic;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Bil
{
    // §16 结构化控制流指令（M57 强类型化）。block 引用持有 BilBlock
    // 对象；可选 block 位置（if 的 else / loop 的 enum / try 的 finally）
    // 为可空属性，Operands 合成时 null → none 操作数。

    // §16.8 return：ret [VALUE]
    public sealed class RetInstruction : BilInstruction
    {
        // null = 裸 ret（void 函数或无值返回）
        public BilVariableOperand? Value { get; }

        public RetInstruction(BilVariableOperand? value = null)
        {
            Value = value;
        }

        internal override string Opcode => "ret";
        internal override IReadOnlyList<BilOperand> Operands =>
            Value == null ? (IReadOnlyList<BilOperand>)new BilOperand[0]
                : new BilOperand[] { Value };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            var value = Value == null ? null : coroutine.ReadVar(Value.Name);
            coroutine.Complete(VmCompletion.Return(value));
        }
    }

    // §16.2 条件：if COND blk(THEN) blk(ELSE)|none BREAKID——BREAKID
    // 为 region-exit capability（§16.5 推广：任何结构化 child-region
    // 指令均可被 break 命中），末尾操作数与 loop/switch 同位
    public sealed class IfInstruction : BilInstruction
    {
        public BilVariableOperand Condition { get; }
        public BilBlock ThenBlock { get; }
        public BilBlock? ElseBlock { get; }
        public BilVariableOperand BreakId { get; }

        public IfInstruction(BilVariableOperand condition, BilBlock thenBlock,
            BilBlock? elseBlock, BilVariableOperand breakId)
        {
            Condition = condition;
            ThenBlock = thenBlock;
            ElseBlock = elseBlock;
            BreakId = breakId;
        }

        internal override string Opcode => "if";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[]
            {
                Condition, new BilBlockOperand(ThenBlock),
                ElseBlock != null ? new BilBlockOperand(ElseBlock) : BilNoneOperand.Instance,
                BreakId,
            };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            var condition = coroutine.ReadVar(Condition.Name);
            if (condition is not VmBool flag)
            {
                throw new VmException("if 条件不是 .bool：" + condition.TypeRef);
            }
            coroutine.EnterIf(this, flag.Value);
        }
    }

    // §16.3/§16.4 循环：loop|loop.rev COND blk(BODY) blk(ENUM)|none
    // blk(JUDGE) BREAKID
    public sealed class LoopInstruction : BilInstruction
    {
        public BilVariableOperand Condition { get; }
        public BilBlock Body { get; }
        public BilBlock? EnumBlock { get; }
        public BilBlock Judge { get; }
        public BilVariableOperand BreakId { get; }
        // false = §16.3 正向 loop；true = §16.4 反向 loop.rev
        public bool IsRev { get; }

        public LoopInstruction(BilVariableOperand condition, BilBlock body,
            BilBlock? enumBlock, BilBlock judge, BilVariableOperand breakId, bool isRev)
        {
            Condition = condition;
            Body = body;
            EnumBlock = enumBlock;
            Judge = judge;
            BreakId = breakId;
            IsRev = isRev;
        }

        internal override string Opcode => IsRev ? "loop.rev" : "loop";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[]
            {
                Condition, new BilBlockOperand(Body),
                EnumBlock != null ? new BilBlockOperand(EnumBlock) : BilNoneOperand.Instance,
                new BilBlockOperand(Judge), BreakId,
            };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            coroutine.EnterLoop(this);
        }
    }

    // §16.5 break：break BREAKID
    public sealed class BreakInstruction : BilInstruction
    {
        public BilVariableOperand BreakId { get; }

        public BreakInstruction(BilVariableOperand breakId)
        {
            BreakId = breakId;
        }

        internal override string Opcode => "break";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { BreakId };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            var token = coroutine.ReadVar(BreakId.Name);
            if (token is not VmBreakId breakId)
            {
                throw new VmException("break 操作数不是 .breakid：" + token.TypeRef);
            }
            coroutine.Complete(VmCompletion.Break(breakId));
        }
    }

    // §16.5 continue：continue BREAKID
    public sealed class ContinueInstruction : BilInstruction
    {
        public BilVariableOperand BreakId { get; }

        public ContinueInstruction(BilVariableOperand breakId)
        {
            BreakId = breakId;
        }

        internal override string Opcode => "continue";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { BreakId };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            var token = coroutine.ReadVar(BreakId.Name);
            if (token is not VmBreakId breakId)
            {
                throw new VmException("continue 操作数不是 .breakid：" + token.TypeRef);
            }
            if (!breakId.AllowsContinue)
            {
                throw new VmException("continue 不能引用非 loop 的 .breakid");
            }
            coroutine.Complete(VmCompletion.Continue(breakId));
        }
    }

    // §16.6 switch：switch SELECTOR res(TABLE) [blk(ITEM)...] blk(DEFAULT)
    // BREAKID——规范排版：首行 selector + 表，其后 block 表/default/
    // breakid 各占一行
    public sealed class SwitchInstruction : BilInstruction
    {
        public BilVariableOperand Selector { get; }
        public BilResource Table { get; }
        public IReadOnlyList<BilBlock> ItemBlocks { get; }
        public BilBlock DefaultBlock { get; }
        public BilVariableOperand BreakId { get; }

        public SwitchInstruction(BilVariableOperand selector, BilResource table,
            IReadOnlyList<BilBlock> itemBlocks, BilBlock defaultBlock,
            BilVariableOperand breakId)
        {
            Selector = selector;
            Table = table;
            ItemBlocks = itemBlocks;
            DefaultBlock = defaultBlock;
            BreakId = breakId;
        }

        internal override string Opcode => "switch";
        internal override int FirstLineOperandCount => 2;
        internal override IReadOnlyList<BilOperand> Operands
        {
            get
            {
                var itemOperands = new List<BilOperand>();
                foreach (var itemBlock in ItemBlocks)
                {
                    itemOperands.Add(new BilBlockOperand(itemBlock));
                }
                return new BilOperand[]
                {
                    Selector, new BilResourceOperand(Table),
                    new BilOperandList(itemOperands), new BilBlockOperand(DefaultBlock), BreakId,
                };
            }
        }

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            if (Table is not BilSwitchTableResource table)
            {
                throw new VmException("switch 的表不是 switch-table：" + Table.Name);
            }
            var selector = coroutine.ReadVar(Selector.Name);
            var index = -1;
            for (var i = 0; i < table.Elements.Count; i++)
            {
                var key = context.LoadSwitchElement(table.SelectorTypeRef, table.Elements[i]);
                if (ValuesEqual(selector, key, context, coroutine))
                {
                    index = i;
                    break;
                }
            }
            coroutine.EnterSwitch(this, index);
        }

        private static bool ValuesEqual(VmValue left, VmValue right, VmContext context,
            VmCoroutine coroutine)
        {
            if (left is VmNull || right is VmNull)
            {
                return left is VmNull && right is VmNull;
            }
            if (!VmTypeOps.IsPrimitiveOperand(left) || !VmTypeOps.IsPrimitiveOperand(right))
            {
                return false;
            }
            var result = BilComputeExecution.EvalBinary(BilBinaryOp.CmpEq, left, right,
                context, coroutine);
            return result is VmBool flag && flag.Value;
        }
    }

    // §16.1 block 调用：call blk(BLOCK) BREAKID（不建栈帧，block 落尾
    // 自然返回续 call 的下一条）；BREAKID 为 region-exit capability
    // （§16.5 推广），末尾操作数与 loop/switch 同位
    public sealed class CallBlockInstruction : BilInstruction
    {
        public BilBlock Block { get; }
        public BilVariableOperand BreakId { get; }

        public CallBlockInstruction(BilBlock block, BilVariableOperand breakId)
        {
            Block = block;
            BreakId = breakId;
        }

        internal override string Opcode => "call";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { new BilBlockOperand(Block), BreakId };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            coroutine.EnterCall(this);
        }
    }

    // §16.7 try/catch/finally：try blk(BODY) SLOT res(CATCH_TABLE)
    // blk(FINALLY)|none BREAKID——规范排版：首行 try block，其后异常
    // 变量 / catch 表 / finally / breakid 各占一行；BREAKID 为
    // region-exit capability（§16.5 推广，break 命中 tryId 在 try
    // 边界消费、不进 catch matching）
    public sealed class TryInstruction : BilInstruction
    {
        public BilBlock Body { get; }
        public BilVariableOperand ExceptionSlot { get; }
        public BilResource CatchTable { get; }
        public BilBlock? FinallyBlock { get; }
        public BilVariableOperand BreakId { get; }

        public TryInstruction(BilBlock body, BilVariableOperand exceptionSlot,
            BilResource catchTable, BilBlock? finallyBlock, BilVariableOperand breakId)
        {
            Body = body;
            ExceptionSlot = exceptionSlot;
            CatchTable = catchTable;
            FinallyBlock = finallyBlock;
            BreakId = breakId;
        }

        internal override string Opcode => "try";
        internal override int FirstLineOperandCount => 1;
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[]
            {
                new BilBlockOperand(Body), ExceptionSlot, new BilResourceOperand(CatchTable),
                FinallyBlock != null ? new BilBlockOperand(FinallyBlock)
                    : BilNoneOperand.Instance,
                BreakId,
            };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            coroutine.EnterTry(this);
        }
    }

    // §16.9 throw：throw EXCEPTION
    public sealed class ThrowInstruction : BilInstruction
    {
        public BilVariableOperand Exception { get; }

        public ThrowInstruction(BilVariableOperand exception)
        {
            Exception = exception;
        }

        internal override string Opcode => "throw";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Exception };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            var value = coroutine.ReadVar(Exception.Name);
            coroutine.Complete(VmCompletion.Throw(new VmException(
                "throw " + value.TypeRef, value)));
        }
    }
}
