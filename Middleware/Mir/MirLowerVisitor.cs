namespace RigiCompiler.Middleware.Mir
{
    /// <summary>
    /// BIL→MIR 翻译 visitor 的 CRTP 基类：静态 Visit 唯一入口，Enter/Exit
    /// finally 配对。控制流 visitor 在 Enter 压 RegionScope、Exit 弹栈，
    /// 避免手工 Push/Pop 在异常路径泄漏。子类只写 VisitCore。
    /// </summary>
    internal abstract class MirLowerVisitor<TSelf, TInst>
        where TSelf : MirLowerVisitor<TSelf, TInst>, new()
        where TInst : RigiCompiler.Bil.BilInstruction
    {
        public static void Visit(TInst inst, FlowBuilder flow)
        {
            var visitor = new TSelf();
            try
            {
                visitor.Enter(inst, flow);
                visitor.VisitCore(inst, flow);
            }
            finally
            {
                visitor.Exit(inst, flow);
            }
        }

        protected virtual void Enter(TInst inst, FlowBuilder flow) { }

        protected abstract void VisitCore(TInst inst, FlowBuilder flow);

        protected virtual void Exit(TInst inst, FlowBuilder flow) { }
    }
}
