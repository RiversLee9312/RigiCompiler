namespace RigiCompiler.Bil
{
    // §17：await TASK [RESULT]。结果操作数可选，严格保持 Task 先、结果后。
    public sealed class AwaitInstruction : BilInstruction
    {
        public BilVariableOperand Task { get; }
        public BilVariableOperand? Result { get; }

        public AwaitInstruction(BilVariableOperand task, BilVariableOperand? result = null)
        {
            Task = task;
            Result = result;
        }

        internal override string Opcode => "await";
        internal override System.Collections.Generic.IReadOnlyList<BilOperand> Operands =>
            Result == null ? new BilOperand[] { Task } : new BilOperand[] { Task, Result };
    }

    // §17.2：yield [ALARM]。裸 yield 不带任何操作数。
    public sealed class YieldInstruction : BilInstruction
    {
        public BilVariableOperand? Alarm { get; }

        public YieldInstruction(BilVariableOperand? alarm = null)
        {
            Alarm = alarm;
        }

        internal override string Opcode => "yield";
        internal override System.Collections.Generic.IReadOnlyList<BilOperand> Operands =>
            Alarm == null ? System.Array.Empty<BilOperand>() : new BilOperand[] { Alarm };
    }
}
