// Rigi 标准库：core.io 标准输出/错误输出流（STDLIB §4.4「已确定的
// 标准流与控制台契约」，施工块 2-4b1）。
//   - StandardStreams：标准流工厂——standardOutput()/standardError()
//     每次调用返回一个新的包装对象（local）；
//   - StdOutputStream / StdErrorStream（priv）：进程标准输出/错误输出
//     的借用包装对象。关闭对象只结束该对象的使用，不关闭进程标准
//     句柄，也不影响其他包装对象（§4.4：可独立关闭的借用包装对象）。
//     标准流不可定位，不实现 ISeekableStream。
// 语义（§4.4）：
//   - write 循环调用底层原生面直到写完全部请求范围（底层短写兜底；
//     OutputStream.write 契约：成功返回即写完全部）；count == 0 校验
//     通过后直接成功，不写出任何字节；
//   - flush 调对应原生 flush：fflush 完成 C 运行时缓冲写出；stdout
//     重定向到常规文件时由原生面完成系统持久化刷新（POSIX fsync /
//     Windows FlushFileBuffers，实现依据见 shim.c 助手注释）；终端/
//     管道按设备能力 fflush 即可；
//   - 底层写失败（写不动/刷新失败）先 markFaulted 再抛
//     core.IOException（§4.4：系统 I/O 错误保留操作名称及错误约定；
//     实际 I/O 失败进入故障态，只允许清理）；
//   - 关闭或故障后 write/flush 抛 core.IllegalStateException（基类
//     三态规则）；dispose 幂等由基类封闭支架保证；disposeCore 无系统
//     资源可释放（进程标准句柄不归包装对象所有）。
// 经标准库写入同一路标准流的操作与 Console.println 走同一 C 写通道
// 与刷新助手（shim.c），行原子性与刷新语义统一（§4.4 标准流段）。
// 标准输入（块 2-4b2）：standardInput() 返回 StdInputStream 借用包装——
// 阻塞读卸载到宿主后台线程（绝不占 Worker/Compute 同步阻塞），协程经
// yield EventAlarm 挂起等待、读完成后由后台线程触发事件唤醒（§19.3
// 原子握手）；EOF（stdin 关闭/空输入）返回 0，不使流进入故障态。
namespace core.io

// 标准流工厂（§4.4）：仅静态入口，不暴露构造语义
pub class StandardStreams {
    // 标准输出包装对象：每次调用返回新实例（各实例可独立关闭，
    // 关闭不影响进程标准句柄与其他包装对象）
    pub static func standardOutput(): OutputStream {
        return new StdOutputStream()
    }

    // 标准错误输出包装对象：同上（stderr 通道独立）
    pub static func standardError(): OutputStream {
        return new StdErrorStream()
    }

    // 标准输入包装对象（块 2-4b2）：每次调用返回新实例；多个包装对象
    // 共享同一输入位置（stdin fd 全局序——包装层不自建缓冲，每次 read
    // 直达宿主 native 面，天然共享；§4.4：不能当作独立输入源使用）。
    // 关闭包装只结束该对象的使用，不关 stdin（同输出借用契约）
    pub static func standardInput(): InputStream {
        return new StdInputStream()
    }
}

// 标准输出包装对象（§4.4 借用包装契约）：借用进程标准输出句柄，
// 不拥有、不关闭。local 类型，同一实例不支持并发或重入操作。
// C 符号 = rigi_ + @NativeSymbol 短名直拼（RuntimeFaces 映射规则）
priv class StdOutputStream : OutputStream {
    // 写出缓冲区指定范围，返回实际写出字节数（0 也属合法返回——
    // 由包装层判失败以兑现「成功返回即写完全部」契约）
    @NativeLibrary("rigi_rt")
    @NativeSymbol("stdout_write")
    priv static native func stdoutWrite(buffer: Span\<u8>, offset: i32, count: i32): i32

    // 刷新：返回 0 成功 / 非 0 失败（错误码约定见 shim.c）
    @NativeLibrary("rigi_rt")
    @NativeSymbol("stdout_flush")
    priv static native func stdoutFlush(): i32

    // 单次写出（基类契约）：状态闸门先于范围校验；循环补齐底层短写
    pub override func write(buffer: Span\<u8>, offset: i32, count: i32) {
        ensureOpen()
        checkRange(buffer, offset, count)
        var done: i32 = 0
        while (done < count) {
            const n = stdoutWrite(buffer, offset + done, count - done)
            // n <= 0：底层写不动（仍有剩余字节）——按实际 I/O 失败
            // 处理，防死循环（§4.4：失败不保证回滚已写出的字节）
            if (n <= 0) {
                markFaulted()
                throw new core.IOException("标准输出写出失败（stdout_write 返回 ${n}）")
            }
            done = done + n
        }
    }

    // 刷新（§4.4 flush 契约）：fflush + 重定向到常规文件时的系统
    // 持久化刷新（原生面内完成）；失败置故障并抛 core.IOException
    pub override func flush() {
        ensureOpen()
        const r = stdoutFlush()
        if (r != 0) {
            markFaulted()
            throw new core.IOException("标准输出刷新失败（stdout_flush 返回 ${r}）")
        }
    }

    // 收尾钩子（基类 dispose 支架保证幂等）：无自身资源可清理——
    // 进程标准句柄不归包装对象所有（§4.4：关闭对象只结束该对象的
    // 使用）；关闭态由支架记录，后续 write/flush 由 ensureOpen 拒绝
    protected override func disposeCore() { }
}

// 标准错误输出包装对象：与 StdOutputStream 对称（§4.4：stdout 与
// stderr 分别保证行原子性，不承诺二者的统一顺序）
priv class StdErrorStream : OutputStream {
    @NativeLibrary("rigi_rt")
    @NativeSymbol("stderr_write")
    priv static native func stderrWrite(buffer: Span\<u8>, offset: i32, count: i32): i32

    @NativeLibrary("rigi_rt")
    @NativeSymbol("stderr_flush")
    priv static native func stderrFlush(): i32

    pub override func write(buffer: Span\<u8>, offset: i32, count: i32) {
        ensureOpen()
        checkRange(buffer, offset, count)
        var done: i32 = 0
        while (done < count) {
            const n = stderrWrite(buffer, offset + done, count - done)
            if (n <= 0) {
                markFaulted()
                throw new core.IOException("标准错误输出写出失败（stderr_write 返回 ${n}）")
            }
            done = done + n
        }
    }

    pub override func flush() {
        ensureOpen()
        const r = stderrFlush()
        if (r != 0) {
            markFaulted()
            throw new core.IOException("标准错误输出刷新失败（stderr_flush 返回 ${r}）")
        }
    }

    protected override func disposeCore() { }
}

// ===== 标准输入（块 2-4b2）=====

// 一次性唤醒事件（对齐 core.messaging QueueWakeup 先例）：signal 为
// protected，触发来自宿主后台读线程经 native event_signal 面直达底座
//（native 线程不能执行 Rigi 代码），本类只承担「可 yield 的 EventAlarm
// 身份」。每轮 read 新建一枚——粘滞事件 signal 即终态不可复位，复用
// 会让下一轮读的 yield 立即通过而读尚未完成（每轮一次性的底座在对象
// 不可达后随进程退出由 atexit 兜底清扫，与 QueueWakeup 同口径）。
priv shared class StdinWake : core.coroutine.EventAlarm {
}

// 标准输入包装对象（§4.4 借用包装契约）：借用进程 stdin，不拥有、不
// 关闭。local 类型，同一实例不支持并发或重入；进程级同一时刻至多一个
// 在途读（跨包装对象并发读同属被禁止的共享位置并发使用，宿主面防御
// 诊断）。挂起语义（§4.4：暂时无数据应挂起等待，内部等待可挂起当前
// 协程并释放 Worker，恢复后继续同一次调用，不另建协程——普通 func 内
// 挂起，见函数文档 §4.5）：read 启动即返的异步原语把阻塞读卸载到宿主
// 后台线程（不在 Compute/任何 Worker 上同步阻塞），随后 yield 一次性
// EventAlarm 挂起；读完成后后台线程经 event_signal 触发 §19.3 原子
// 握手唤醒（先 signal 后 yield 由粘滞语义兜底，无丢失窗口）。
priv class StdInputStream : InputStream {
    // 启动一次 stdin 读（启动即返）：把 buffer[offset..offset+count)
    // 的读请求卸载到宿主后台线程，完成后该线程触发 wakeHandle 事件。
    // 返回 0 = 已卸载（挂起等唤醒后 stdinReadTake 取结果）；
    // 返回 1 = stdin 已 EOF（此前某次读已到达末尾——粘滞短路，不挂起
    // 也不登记请求；EOF 是全局序状态，跨包装对象共享）
    @NativeLibrary("rigi_rt")
    @NativeSymbol("stdin_read_start")
    priv static native func stdinReadStart(buffer: Span\<u8>, offset: i32,
        count: i32, wakeHandle: i64): i32

    // 取上一次读的结果：唤醒后调用（结果写入与事件触发之间有宿主闸
    // 的先后序保证，恢复必见已完成结果）。返回实际读取字节数（0 = EOF）
    // ；负值 = I/O 错误
    @NativeLibrary("rigi_rt")
    @NativeSymbol("stdin_read_take")
    priv static native func stdinReadTake(): i32

    // 单次读取（基类契约）：状态闸门先于范围校验；count == 0 校验通过
    // 后直接返回 0，不消费输入，也不证明已到 EOF
    pub override func read(buffer: Span\<u8>, offset: i32, count: i32): i32 {
        ensureOpen()
        checkRange(buffer, offset, count)
        if (count == 0) { return 0 }
        // 每轮读一枚一次性粘滞事件（见 StdinWake 注释）；ensureHandle
        // 懒建底座并取句柄（internal = 模块内可见，stdlib 单模块）
        const wake = new StdinWake()
        const h = wake.ensureHandle()
        const rc = stdinReadStart(buffer, offset, count, h)
        if (rc == 1) { return 0 }
        // 挂起等待：无数据时不占 Worker（§4.4 基本读写契约）；读完成
        // 由后台线程 signal 唤醒（粘滞兜底先 signal 后 yield 的窗口）
        yield (wake as core.coroutine.EventAlarm)
        const n = stdinReadTake()
        if (n < 0) {
            // 实际 I/O 失败先 markFaulted 再抛（§4.4：故障后只允许清理）；
            // 普通 EOF（n == 0）与参数校验失败不置故障
            markFaulted()
            throw new core.IOException("标准输入读取失败（stdin_read 返回 ${n}）")
        }
        return n
    }

    // 收尾钩子（基类 dispose 支架保证幂等）：无自身资源可清理——
    // stdin 不归包装对象所有（§4.4：关闭对象只结束该对象的使用）；
    // 关闭态由支架记录，后续 read 由 ensureOpen 拒绝
    protected override func disposeCore() { }
}
