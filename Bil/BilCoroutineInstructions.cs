using RigiCompiler.Bil.Vm;

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

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            // MW11c 棒4a（§18.3）：await 决策进 Rigi 世界——桥调 Task 的
            // registerWaiter Rigi 方法，按返回码继续（终态快读）/挂起
            // （登记 waiter 后在同一 gate 临界区内转 Suspended）
            context.Dispatch.Await(coroutine, coroutine.ReadVar(Task.Name), Result?.Name);
        }
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

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            if (Alarm == null)
            {
                coroutine.YieldBare();
                return;
            }
            coroutine.YieldAlarm(context, coroutine.ReadVar(Alarm.Name));
        }
    }
}
