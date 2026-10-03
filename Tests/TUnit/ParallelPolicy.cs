using TUnit.Core;
using TUnit.Core.Interfaces;

// 框架发现行可并发；真实 CPU/内存授予仍由共享加权预算控制。
[assembly: ParallelLimiter<RigiCompiler.TUnitTests.ConservativeWorkerLimit>]

namespace RigiCompiler.TUnitTests;

public sealed class ConservativeWorkerLimit : IParallelLimit
{
    public int Limit => RigiCompiler.ResourceBudget.Shared.Capacity.CpuSlots;
}
