namespace RigiCompiler.Middleware.Pipeline
{
    /// <summary>
    /// Middleware 流水线阶段（仿前端层栈纪律的线性化变体，MIDDLEWARE_ARCHITECTURE
    /// §11 Pipeline/）：
    /// - 阶段不做编排、不互相调用；编排唯一归 MwPipeline 驱动器（单一调度器，
    ///   对应前端 Parser.ParseCore 的角色）。
    /// - 阶段之间只经 MwContext 交换产物：每个实现的文档注释须声明读集
    ///   （消费的 context 产物）与写集（挂载的 context 产物）——对应前端
    ///   施工目标协议的单向数据流，禁止任何形式的回传/旁路机制。
    /// - 失败纪律：合法 BIL 超出现阶段实现面 → MwNotSupportedException；
    ///   编译器 bug → CompilerInternalException。
    /// 与前端层栈有意不模仿之处：线性阶段序而非真栈（语法嵌套深度运行期
    /// 才知道，后端阶段编译期确定）；无 Replay；阶段内是 MIR 图遍历而非
    /// 逐 token 状态机。
    /// </summary>
    public interface IMwStage
    {
        // 阶段名（日志/诊断用）
        string Name { get; }

        void Run(MwContext context);
    }
}
