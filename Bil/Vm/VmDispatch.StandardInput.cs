using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace RigiCompiler.Bil.Vm
{
    internal sealed partial class VmDispatch
    {
        // StandardInput 职责；与主文件共享同一类型、字段及生命周期。

        internal VmValue StdinReadStart(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 4 || args[1] is not VmI32 offset
                || args[2] is not VmI32 count || args[3] is not VmI64 wake)
            {
                throw new VmException("stdin_read_start 需要 (Span<u8>, i32, i32, i64)");
            }
            var value = args[0] is VmAny any ? any.Payload : args[0];
            if (value is not VmSpan span || span.ElementType != ".u8")
            {
                throw new VmException("stdin_read_start 第一参数必须是 Span<u8>");
            }
            var start = offset.Value;
            var length = count.Value;
            if (start < 0 || length < 0 || start > span.Length - length)
            {
                throw new VmException("stdin_read_start 区间越界：offset=" + start
                    + " count=" + length + " 长度=" + span.Length);
            }
            lock (_stdinGate)
            {
                if (_stdinEof)
                {
                    return new VmI32(1);  // EOF 粘滞短路（Rigi 层直接返回 0）
                }
                if (_stdinInflight)
                {
                    throw new VmException("stdin 已有在途读（共享位置并发读违反契约）");
                }
                _stdinInflight = true;
            }
            // 专用后台线程（非线程池——阻塞读可能占住线程任意久；
            // IsBackground 保证进程退出不被拖累）。Span 挂起期间借用
            //（§4.4）：调用协程挂起持有引用，后台线程写入无回收风险
            var reader = new Thread(() => StdinReadBody(span, start, length, wake.Value))
            {
                IsBackground = true,
                Name = "rigi-stdin",
            };
            reader.Start();
            return new VmI32(0);
        }

        // 后台读线程体：阻塞读一次（Stream.Read 返回 0 = EOF），写回
        // Span 后在闸内登记结果，再触发唤醒事件（登记先于 signal——
        // 恢复协程必见结果）。stdin 不可用（已关闭/无效句柄）抛异常
        // 按 EOF 处理——对齐 native 面 read 的 EBADF→EOF 口径：进程
        // 无 stdin 即「已关闭」（§4.4 EOF 路径确定性）
        private void StdinReadBody(VmSpan span, int start, int length, long wake)
        {
            int n;
            byte[] buffer = new byte[length];
            try
            {
                // 原始字节流（不经 Console.In 的 TextReader——避免其
                // 内部缓冲预读吞掉后续字节）
                _stdinStream ??= Console.OpenStandardInput();
                n = _stdinStream.Read(buffer, 0, length);
                if (n < 0)
                {
                    n = 0;
                }
            }
            catch
            {
                n = 0;
            }
            for (var i = 0; i < n; i++)
            {
                span.Elements[start + i] = new VmU8(buffer[i]);
            }
            lock (_stdinGate)
            {
                if (n == 0)
                {
                    _stdinEof = true;
                }
                _stdinResult = n;
                _stdinInflight = false;
            }
            EventSignalCore(wake);
        }

        internal VmValue StdinReadTake(IReadOnlyList<VmValue> args)
        {
            lock (_stdinGate)
            {
                // take 只发生在事件唤醒之后：signal 前结果已在闸内登记，
                // 未登记即到取属时序 bug（防御诊断，正常路径不可达）
                if (!_stdinEof && _stdinInflight)
                {
                    throw new VmException("stdin_read_take：结果未就绪（时序 bug）");
                }
                return new VmI32(_stdinResult);
            }
        }

    }
}
