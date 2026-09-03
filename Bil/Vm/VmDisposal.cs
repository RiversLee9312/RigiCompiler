using System.Collections.Concurrent;

namespace RigiCompiler.Bil.Vm
{
    // MW12b §25.2 VM 半场：undisposed 事件队列（rigi_rt gexc.c 事件队列的
    // VM 侧等价物）。IDisposable 对象的 C# finalizer 线程只入队类型
    // canonical 名（与 native 侧 TypeInfo.name 同口径），绝不触解释器
    // 状态；派发时机归 BilVm.Run——main/drain 之后、失败汇总之前逼 GC
    // （Collect + WaitForPendingFinalizers）让终结器事件落队，再逐条
    // 出队真构造 UndisposedResourceException 调 GlobalExceptionHandler.dispatch
    internal sealed class VmUndisposedTracker
    {
        private readonly ConcurrentQueue<string> _pending = new ConcurrentQueue<string>();

        // finalizer 线程唯一允许的动作（线程安全，无解释器状态）
        internal void Enqueue(string typeName)
        {
            _pending.Enqueue(typeName);
        }

        internal List<string> DrainAll()
        {
            var result = new List<string>();
            while (_pending.TryDequeue(out var typeName))
            {
                result.Add(typeName);
            }
            return result;
        }
    }
}
