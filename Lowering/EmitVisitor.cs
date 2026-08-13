using RigiCompiler.Bil;

namespace RigiCompiler
{
    // 发射 visitor 的 CRTP 协议基类（M55 visitor 化协议，仿 Lowered 协议）：
    // 静态 Visit 为唯一入口——创建子类实例、模板化管理生命周期（Enter/Exit
    // 配对），子类只实现 VisitCore。
    //
    // 与 Binder/Lowerer 协议的差异：BilEmitter 是下行填充（EmitStatement/
    // EmitBlock 往 BilBlock 塞指令，天然 VisitInto 形态）+ 上行合成
    // （EmitValue 返回操作数文本，BIL §10.1）混合，故签名带施工目标
    // target（BilBlock——当前指令追加目标，随调用点变化）。
    //
    // TResult：语句/块发射为 Unit（无产物，副作用填充 target）；值发射为
    // string（操作数文本）。失败经 EmitEnvironment.Error 落诊断——P4 发射
    // 是机械线性化，诊断仅限「未支持节点形态」。
    internal abstract class EmitVisitor<TSelf, TResult>
        where TSelf : EmitVisitor<TSelf, TResult>, new()
    {
        public static TResult Visit(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var visitor = new TSelf();
            try
            {
                visitor.Enter(node, target, ctx, env);
                return visitor.VisitCore(node, target, ctx, env);
            }
            finally
            {
                visitor.Exit(node, target, ctx, env);
            }
        }

        protected abstract TResult VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env);

        protected virtual void Enter(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env) { }

        protected virtual void Exit(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env) { }
    }
}
