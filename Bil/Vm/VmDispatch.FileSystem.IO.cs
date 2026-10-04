using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace RigiCompiler.Bil.Vm
{
    internal sealed partial class VmDispatch
    {
        // FileSystem.IO 职责；与主文件共享同一类型、字段及生命周期。

        // fs_read_start（挂起读启动即返，stdin 先例直复刻）：把阻塞读
        // 交给专用后台线程（绝不在 Worker/解释线程上同步阻塞），完成
        // 写回 Span 并登记结果后经 EventSignalCore 触发事件——挂起协程
        // 由 §19.3 原子握手唤醒。EOF 不粘滞（§4.5.6：读到当前 EOF 返回
        // 0，之后再次读取可以看到新增内容），每轮 start 都真读
        internal VmValue FsReadStart(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 5 || args[0] is not VmI64 handle
                || args[2] is not VmI32 offset || args[3] is not VmI32 count
                || args[4] is not VmI64 wake)
            {
                throw new VmException(
                    "fs_read_start 需要 (i64, Span<u8>, i32, i32, i64)");
            }
            var value = args[1] is VmAny any ? any.Payload : args[1];
            if (value is not VmSpan span || span.ElementType != ".u8")
            {
                throw new VmException("fs_read_start 第二参数必须是 Span<u8>");
            }
            var start = offset.Value;
            var length = count.Value;
            if (start < 0 || length < 0 || start > span.Length - length)
            {
                throw new VmException("fs_read_start 区间越界：offset=" + start
                    + " count=" + length + " 长度=" + span.Length);
            }
            VmFsFile file;
            FileStream stream;
            lock (_fsGate)
            {
                if (!_fsFiles.TryGetValue(handle.Value, out file!))
                {
                    throw new VmException("fs_read_start 无效句柄");
                }
                if (file.ReadInflight)
                {
                    throw new VmException(
                        "fs_read_start：已有在途读（并发/重入违反契约）");
                }
                stream = RequireFsStream(file, "fs_read_start");
                file.ReadInflight = true;
            }
            // 专用后台线程（非线程池——阻塞读可能占住线程任意久；
            // IsBackground 保证进程退出不被拖累）。Span 挂起期间借用
            //（§3.2：调用协程挂起持有引用，后台线程写入无回收风险——
            // stdin Span 借用先例）
            var reader = new Thread(
                () => FsReadBody(file, stream, span, start, length, wake.Value))
            {
                IsBackground = true,
                Name = "rigi-fs-read",
            };
            reader.Start();
            return new VmI32(0);
        }

        // 后台读线程体：阻塞读一次（Stream.Read 返回 0 = 当前 EOF），
        // 写回 Span 后在闸内登记结果，再触发唤醒事件（登记先于
        // signal——恢复协程必见结果；native 面同序）
        private void FsReadBody(VmFsFile file, FileStream stream,
            VmSpan span, int start, int length, long wake)
        {
            int n;
            var buffer = new byte[length];
            try
            {
                n = stream.Read(buffer, 0, length);
                if (n < 0) { n = 0; }
            }
            catch (Exception ex)
            {
                // I/O 失败登记负归一码（Rigi 层映射 FileSystemException；
                // 与 native 面「结果登记先于事件触发」同序）
                lock (_fsGate)
                {
                    file.ReadResult = -MapFsError(ex);
                    file.ReadInflight = false;
                }
                EventSignalCore(wake);
                return;
            }
            for (var i = 0; i < n; i++)
            {
                span.Elements[start + i] = new VmU8(buffer[i]);
            }
            lock (_fsGate)
            {
                file.ReadResult = n;
                file.ReadInflight = false;
            }
            EventSignalCore(wake);
        }

        internal VmValue FsReadTake(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 1 || args[0] is not VmI64 handle)
            {
                throw new VmException("fs_read_take 需要 (i64)");
            }
            lock (_fsGate)
            {
                if (!_fsFiles.TryGetValue(handle.Value, out var file))
                {
                    throw new VmException("fs_read_take 无效句柄");
                }
                // take 只发生在事件唤醒之后：signal 前结果已在闸内登记，
                // 未登记即到取属时序 bug（防御诊断，正常路径不可达）
                if (file.ReadInflight)
                {
                    throw new VmException("fs_read_take：结果未就绪（时序 bug）");
                }
                return new VmI32(file.ReadResult);
            }
        }

        // ===== fs 原语族阶段 2（写/flush 挂起 + 定位/长度/信息查询/
        // 创建删除/移动/目录枚举同步；rigi_rt fs.c 镜像，双宿主同语义）=====

        // i32 小端写入出参 Span（dirread/realpath 的 meta 协议）
        private static void WriteFsI32Le(VmSpan span, int offset, int value)
        {
            for (var i = 0; i < 4; i++)
            {
                span.Elements[offset + i] =
                    new VmU8((byte)(value >> (i * 8)));
            }
        }

        private static VmString RequireFsString(string hook,
            IReadOnlyList<VmValue> args, int index)
        {
            if (args.Count <= index)
            {
                throw new VmException(hook + "：参数不足");
            }
            var v = args[index] is VmAny any ? any.Payload : args[index];
            if (v is not VmString s)
            {
                throw new VmException(hook + "：参数 " + index
                    + " 必须是 String");
            }
            return s;
        }

        private static VmSpan RequireFsSpan(string hook,
            IReadOnlyList<VmValue> args, int index)
        {
            if (args.Count <= index)
            {
                throw new VmException(hook + "：参数不足");
            }
            var v = args[index] is VmAny any ? any.Payload : args[index];
            if (v is not VmSpan s || s.ElementType != ".u8")
            {
                throw new VmException(hook + "：参数 " + index
                    + " 必须是 Span<u8>");
            }
            return s;
        }

        private long RequireFsHandle(string hook, IReadOnlyList<VmValue> args)
        {
            if (args.Count < 1 || args[0] is not VmI64 handle)
            {
                throw new VmException(hook + "：第一参数需要 i64 句柄");
            }
            lock (_fsGate)
            {
                if (!_fsFiles.ContainsKey(handle.Value))
                {
                    throw new VmException(hook + " 无效句柄");
                }
            }
            return handle.Value;
        }

        // fs_write_start（挂起写，read 同款两段式）：buffer[start..start+
        // count) 卸载到专用后台线程，完成后写回登记并触发事件。
        // 非追加 = Stream.Write 全量语义（返回写满的 count）；追加 =
        // AppendWriteCore 单次系统调用（允许短写，两者 Rigi 层 7-3 流层
        // 均循环补齐——正常本地文件写实际全量）
        internal VmValue FsWriteStart(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 5 || args[0] is not VmI64 handle
                || args[2] is not VmI32 offset || args[3] is not VmI32 count
                || args[4] is not VmI64 wake)
            {
                throw new VmException(
                    "fs_write_start 需要 (i64, Span<u8>, i32, i32, i64)");
            }
            var value = args[1] is VmAny any ? any.Payload : args[1];
            if (value is not VmSpan span || span.ElementType != ".u8")
            {
                throw new VmException("fs_write_start 第二参数必须是 Span<u8>");
            }
            var start = offset.Value;
            var length = count.Value;
            if (start < 0 || length < 0 || start > span.Length - length)
            {
                throw new VmException("fs_write_start 区间越界：offset="
                    + start + " count=" + length + " 长度=" + span.Length);
            }
            VmFsFile file;
            FileStream stream;
            lock (_fsGate)
            {
                if (!_fsFiles.TryGetValue(handle.Value, out file!))
                {
                    throw new VmException("fs_write_start 无效句柄");
                }
                if (file.WriteInflight)
                {
                    throw new VmException(
                        "fs_write_start：已有在途写（并发/重入违反契约）");
                }
                stream = RequireFsStream(file, "fs_write_start");
                file.WriteInflight = true;
            }
            var writer = new Thread(
                () => FsWriteBody(file, stream, span, start, length,
                    wake.Value))
            {
                IsBackground = true,
                Name = "rigi-fs-write",
            };
            writer.Start();
            return new VmI32(0);
        }

        // 后台写线程体：阻塞写一次（全量或抛），写回登记后触发事件
        //（登记先于 signal——恢复协程必见结果，native 面同序）
        private void FsWriteBody(VmFsFile file, FileStream stream,
            VmSpan span, int start, int length, long wake)
        {
            var buffer = new byte[length];
            for (var i = 0; i < length; i++)
            {
                buffer[i] = ((VmU8)span.Elements[start + i]).Value;
            }
            int n;
            try
            {
                if (file.AppendMode)
                {
                    // §4.5.6 系统追加：写入位置由 OS 在本系统调用内原子
                    // 选择到当时末尾（append-only 句柄/O_APPEND，见
                    // OpenAppendStream 证据注释）——不能用用户态
                    // Seek(End)+Write 模拟：两步之间其他进程/句柄可增长
                    // 文件，本笔会落在过期位置覆盖其数据（探针
                    // playground/vm_append_atomic 固定交错复现）。返回
                    // 实际写出字节（允许短写，Rigi 层循环补齐）
                    n = AppendWriteCore(stream, buffer, length);
                }
                else
                {
                    stream.Write(buffer, 0, length);
                    n = length;
                }
            }
            catch (Exception ex)
            {
                lock (_fsGate)
                {
                    file.WriteResult = -MapFsError(ex);
                    file.WriteInflight = false;
                }
                EventSignalCore(wake);
                return;
            }
            lock (_fsGate)
            {
                file.WriteResult = n;
                file.WriteInflight = false;
            }
            EventSignalCore(wake);
        }

        internal VmValue FsWriteTake(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 1 || args[0] is not VmI64 handle)
            {
                throw new VmException("fs_write_take 需要 (i64)");
            }
            lock (_fsGate)
            {
                if (!_fsFiles.TryGetValue(handle.Value, out var file))
                {
                    throw new VmException("fs_write_take 无效句柄");
                }
                if (file.WriteInflight)
                {
                    throw new VmException(
                        "fs_write_take：结果未就绪（时序 bug）");
                }
                return new VmI32(file.WriteResult);
            }
        }

        // fs_flush_start（挂起 flush）：Flush(flushToDisk: true) 等价
        // FlushFileBuffers（§4.5.6 系统持久化刷新，非库缓冲提交）
        internal VmValue FsFlushStart(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 2 || args[0] is not VmI64 handle
                || args[1] is not VmI64 wake)
            {
                throw new VmException("fs_flush_start 需要 (i64, i64)");
            }
            VmFsFile file;
            FileStream stream;
            lock (_fsGate)
            {
                if (!_fsFiles.TryGetValue(handle.Value, out file!))
                {
                    throw new VmException("fs_flush_start 无效句柄");
                }
                if (file.FlushInflight)
                {
                    throw new VmException(
                        "fs_flush_start：已有在途 flush（并发/重入违反契约）");
                }
                stream = RequireFsStream(file, "fs_flush_start");
                file.FlushInflight = true;
            }
            var flusher = new Thread(() => FsFlushBody(file, stream,
                wake.Value))
            {
                IsBackground = true,
                Name = "rigi-fs-flush",
            };
            flusher.Start();
            return new VmI32(0);
        }

        private void FsFlushBody(VmFsFile file, FileStream stream, long wake)
        {
            int rc;
            try
            {
                stream.Flush(true);
                rc = 0;
            }
            catch (Exception ex)
            {
                rc = -MapFsError(ex);
            }
            lock (_fsGate)
            {
                file.FlushResult = rc;
                file.FlushInflight = false;
            }
            EventSignalCore(wake);
        }

        internal VmValue FsFlushTake(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 1 || args[0] is not VmI64 handle)
            {
                throw new VmException("fs_flush_take 需要 (i64)");
            }
            lock (_fsGate)
            {
                if (!_fsFiles.TryGetValue(handle.Value, out var file))
                {
                    throw new VmException("fs_flush_take 无效句柄");
                }
                if (file.FlushInflight)
                {
                    throw new VmException(
                        "fs_flush_take：结果未就绪（时序 bug）");
                }
                return new VmI32(file.FlushResult);
            }
        }

        // fs_seek：whence 0=Begin 1=Current 2=End（SeekOrigin 同值）；out
        // 写新绝对位置。结果位置为负 → 宿主错误归 22（Rigi 层按范围错误
        // 抛 OutOfBoundException，§4.5.9）
        internal VmValue FsSeek(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 4 || args[0] is not VmI64 handle
                || args[1] is not VmI64 offset || args[2] is not VmI32 whence)
            {
                throw new VmException("fs_seek 需要 (i64, i64, i32, Span<u8>)");
            }
            var outSpan = RequireFsSpan("fs_seek", args, 3);
            var w = whence.Value;
            if (w < 0 || w > 2)
            {
                return new VmI32(-22);
            }
            try
            {
                var stream = RequireFsStream(FsFileOf(handle.Value, "fs_seek"),
                    "fs_seek");
                var newPos = stream.Seek(offset.Value, (SeekOrigin)w);
                WriteFsI64Le(outSpan, newPos);
                return new VmI32(0);
            }
            catch (Exception ex) when (ex is IOException
                or NotSupportedException or ArgumentException
                or OutOfMemoryException or ObjectDisposedException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

        private VmFsFile FsFileOf(long handle, string hook)
        {
            lock (_fsGate)
            {
                if (!_fsFiles.TryGetValue(handle, out var file))
                {
                    throw new VmException(hook + " 无效句柄");
                }
                return file;
            }
        }

        internal VmValue FsTell(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 2 || args[0] is not VmI64 handle)
            {
                throw new VmException("fs_tell 需要 (i64, Span<u8>)");
            }
            var outSpan = RequireFsSpan("fs_tell", args, 1);
            try
            {
                var stream = RequireFsStream(FsFileOf(handle.Value, "fs_tell"),
                    "fs_tell");
                WriteFsI64Le(outSpan, stream.Position);
                return new VmI32(0);
            }
            catch (Exception ex) when (ex is IOException
                or NotSupportedException or ArgumentException
                or OutOfMemoryException or ObjectDisposedException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

        internal VmValue FsGetLength(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 2 || args[0] is not VmI64 handle)
            {
                throw new VmException("fs_get_length 需要 (i64, Span<u8>)");
            }
            var outSpan = RequireFsSpan("fs_get_length", args, 1);
            try
            {
                var stream = RequireFsStream(
                    FsFileOf(handle.Value, "fs_get_length"), "fs_get_length");
                WriteFsI64Le(outSpan, stream.Length);
                return new VmI32(0);
            }
            catch (Exception ex) when (ex is IOException
                or NotSupportedException or ArgumentException
                or OutOfMemoryException or ObjectDisposedException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

        // fs_set_length：缩短截断/增长补零，成功后游标保持不变——即使已
        // 在新末尾之后（§4.5.6 明文契约）。增长部分显式写零：不假定宿主
        // SetLength 保证扩展区域内容（.NET 依赖 NTFS 语义，FAT 不保证）
        internal VmValue FsSetLength(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 2 || args[0] is not VmI64 handle
                || args[1] is not VmI64 length)
            {
                throw new VmException("fs_set_length 需要 (i64, i64)");
            }
            if (length.Value < 0)
            {
                return new VmI32(-22); // 负长度：Rigi 层先行拦截的防御
            }
            try
            {
                var stream = RequireFsStream(
                    FsFileOf(handle.Value, "fs_set_length"), "fs_set_length");
                var pos = stream.Position;
                var oldLen = stream.Length;
                if (length.Value > oldLen)
                {
                    stream.Seek(oldLen, SeekOrigin.Begin);
                    var zeros = new byte[4096];
                    var remain = length.Value - oldLen;
                    while (remain > 0)
                    {
                        var chunk = (int)Math.Min(zeros.Length, remain);
                        stream.Write(zeros, 0, chunk);
                        remain -= chunk;
                    }
                }
                stream.SetLength(length.Value);
                stream.Seek(pos, SeekOrigin.Begin);
                return new VmI32(0);
            }
            catch (Exception ex) when (ex is IOException
                or NotSupportedException or ArgumentException
                or OutOfMemoryException or ObjectDisposedException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

    }
}
