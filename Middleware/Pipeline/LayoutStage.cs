using RigiCompiler.Middleware.Layout;

namespace RigiCompiler.Middleware.Pipeline
{
    /// <summary>
    /// 布局阶段（MW4 批 1）。读：Symbols（全部非 External 类型）+ BIL
    /// fn 体（刀6：Method wrapper 槽从 ..init.wrapper 安装指令重建）；
    /// 写：Layout（LayoutEngine.Build 的布局计划表）。排在 MirBuild 之前
    ///（MirReachability 的虚调用/new 可达闭包要查 vtable 计划；
    /// TypeSheet 发射是其另一消费者）。
    /// </summary>
    public sealed class LayoutStage : IMwStage
    {
        public string Name => "Layout";

        public void Run(MwContext context)
        {
            var constructed = ConstructedTypeCollector.Collect(context);
            var bodies = new System.Collections.Generic.HashSet<string>(
                System.StringComparer.Ordinal);
            foreach (var function in context.Module.Functions)
            {
                bodies.Add(function.Symbol);
            }
            // MW10 刀6：Method wrapper 隐藏槽由 ..init.wrapper 体的
            // new.wrapper.method 安装指令重建（BIL 方法声明不保留 wrapped
            // 修饰符），槽钥匙归方法声明类名下
            var methodSlots = HiddenStoragePlanner.CollectMethodSlots(context.Module);
            context.Layout = LayoutEngine.Build(context.Symbols, constructed, bodies,
                methodSlots);
        }
    }
}
