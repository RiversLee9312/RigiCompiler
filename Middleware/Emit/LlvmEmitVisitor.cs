using RigiCompiler.Middleware.Mir;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// MIR→LLVM 翻译 visitor 的 CRTP 基类：静态 Visit 唯一入口，Enter/Exit
    /// finally 配对。施工目标（builder / 槽表）挂在 Session.Ctx 上。本层
    /// 无 RegionScope，Enter/Exit 默认空；子类只写 VisitCore。
    /// </summary>
    internal abstract class LlvmEmitVisitor<TSelf, TInst>
        where TSelf : LlvmEmitVisitor<TSelf, TInst>, new()
        where TInst : MirInst
    {
        public static void Visit(TInst inst, ModuleBuilder.Session session)
        {
            var visitor = new TSelf();
            try
            {
                visitor.Enter(inst, session);
                visitor.VisitCore(inst, session);
            }
            finally
            {
                visitor.Exit(inst, session);
            }
        }

        protected virtual void Enter(TInst inst, ModuleBuilder.Session session) { }

        protected abstract void VisitCore(TInst inst, ModuleBuilder.Session session);

        protected virtual void Exit(TInst inst, ModuleBuilder.Session session) { }
    }
}
