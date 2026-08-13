using System.Collections.Generic;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Bil
{
    // §18 提示指令（M64）：hint res(RESOURCE_ID)——向 backend 提供一段可忽略的
    // 提示，资源内容是一段 JSON 文本（schema 由生产/消费方约定，模型不解释内容）。
    // 仅出现在 block 内（指令模型天然如此）；无结果变量、不读写任何变量、
    // 不参与 definite assignment、不是终结指令；删除全部 hint 后 §22.2 可观察
    // 行为不变；VM 执行为 no-op；Middleware 可用可忽略，内容不得影响语义。
    public sealed class HintInstruction : BilInstruction
    {
        public BilResource Resource { get; }

        public HintInstruction(BilResource resource)
        {
            Resource = resource;
        }

        internal override string Opcode => "hint";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { new BilResourceOperand(Resource) };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
        }
    }
}
