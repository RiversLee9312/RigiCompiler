using System.Collections.Generic;
using RigiCompiler.Middleware.Passes;

namespace RigiCompiler.Middleware.Pipeline
{
    /// <summary>
    /// 流水线驱动器（单一调度器）：持线性阶段序，依次把 MwContext 交给各
    /// 阶段。翻译 pass 内部唯一 switch 分派到处理类（小改写用内部类；
    /// BIL→MIR / MIR→LLVM 用 CRTP）。MW4 起语言语义 pass 群以 IMwStage
    /// 登记在 MIR 构建之后——pass 之间以 MIR 为唯一交换物
    /// （MIDDLEWARE_ARCHITECTURE §3 MW4）。Emit/链接持有外部资源（LLVM
    /// 模块/工具链进程），属驱动尾，不进流水线，留 Cli 编排。
    /// </summary>
    public sealed class MwPipeline
    {
        // 标准编译管线：BIL（已过门禁）→ 布局（只依赖符号表，先行——
        // MirReachability 的派发闭包要查 vtable 计划）→ MIR 构建 →
        // IndexOperatorLowering → AccessorLowering → FieldProxyBaking →
        // MethodProxyBaking → ProxyBaking → CallWildcardLowering →
        // SingletonLowering → CoroutineSplit → RcInjection。MW10 wrapper 烘焙：
        // AccessorLowering 之后、RcInjection 之前（字段 get/set 链
        //（Value + Entity 双源）先于方法链——后者烘焙体重跑前者钩子，
        // 且 router 的 get/set 分支要求字段链环已烘焙）。
        // MethodProxyBaking（刀6，Method wrapper .proxy.call 链）排在
        // ProxyBaking 之前是组合关键：Entity 烘焙看到的 M fn 已是
        // method trampoline，把它整体外移为 $.wrapped. 并换 Entity
        // trampoline 即天然形成 Entity→Method→raw（$.mwrapped.）三层；
        // 绕过全链的旁路落点指向 $.mwrapped. 最深层原始体。
        // ProxyBaking 已落地 specific + wildcard 方法/运算符链（含 inner
        // 重路由 router，刀4 起为 call??? 预建链末 router）；
        // FieldProxyBaking 落地字段-Value 链与 Entity 字段 get/set 链；
        // CallWildcardLowering 落地 call??? 降级（刀4：entry 环链 +
        // $mw.call???.dispatch 分发 + 调用点改写）；
        // SingletonLowering（刀5）合成 singleton 三态 get fn 并把
        // new type(单例) 改写为 get 调用——排在 RcInjection 之前（get
        // fn 内含托管槽，须被 ARC 配平覆盖），晚于各烘焙 pass（烘焙
        // 产物体内的 new type(单例) 同样须被改写收编）。
        // CoroutineSplit（MW11a 棒2）在 SingletonLowering 之后、
        // RcInjection 之前：async fn 切状态机（stub + resume fn + frame
        // 类型注册），生成的全部代码由 RcInjection 统一 ARC 配平（split
        // 不插 acquire/release；move 槽/frame 借用约定见 RcInjectionPass
        // MW11a 段）。缺 pass 不放空 stub；读写集与相对位置见
        // MIDDLEWARE_ARCHITECTURE §3/§5/§6。
        public static MwPipeline CreateDefault()
        {
            return new MwPipeline()
                .Add(new LayoutStage())
                .Add(new MirBuildStage())
                .Add(new IndexOperatorLoweringPass())
                .Add(new AccessorLoweringPass())
                .Add(new FieldProxyBakingPass())
                .Add(new MethodProxyBakingPass())
                .Add(new ProxyBakingPass())
                .Add(new CallWildcardLoweringPass())
                .Add(new SingletonLoweringPass())
                .Add(new BuiltinToStringDispatchPass())
                .Add(new CoroutineSplitPass())
                .Add(new RcInjectionPass());
        }

        private readonly List<IMwStage> _stages = new();

        public MwPipeline Add(IMwStage stage)
        {
            _stages.Add(stage);
            return this;
        }

        public void Run(MwContext context)
        {
            foreach (var stage in _stages)
            {
                Logger.Verbose("Middleware", $"阶段 {stage.Name}");
                stage.Run(context);
            }
        }
    }
}
