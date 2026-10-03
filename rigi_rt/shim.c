/*
 * rigi_rt shim 库 MW1 最小面（MIDDLEWARE_ARCHITECTURE §4.8）——纯 C11，寄生 libc，
 * 跨 win-x64/linux-x64 仅用标准 CRT（stdio.h/stdlib.h/string.h/stdint.h），
 * 不依赖任何 Windows 专用 API。本库经 EmbeddedResource 内嵌进编译器程序集，
 * 由 RigiRtBuilder 现场用 clang 编成 LLVM bitcode（unity build）参与 lld 链接。
 * 调用约定：默认 C 约定。
 */
#include <stdint.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "arc.h"
#include "coroutine.h"
#include "rigi_string.h"
/* stdin 异步读原语（B2-4b2）的事件触发面 rigi_event_signal 声明；
 * uv.h include 由 RIGI_HAS_LIBUV 守卫，本面不引入 uv 依赖 */
#include "worker.h"

/* 原生自递归调用前的轻量栈余量守卫。每个 OS 线程首次进入生成代码时
 * 记录近似栈基准；向任一方向消耗超过 1 MiB 即要求语言层停止递归。
 * 预算与链接器主线程栈保留联动（NativeCommand：win -Wl,/STACK:8388608
 * = 8 MiB；linux 主线程栈由宿主 ulimit 兜底，glibc 默认 8 MiB 同量
 * 级）——守卫预算必须恒小于真实栈保留，否则递归会在守卫触发前先触
 * guard page。预算口径：序列化反射派发（$fieldsOf 合成分派）帧随登
 * 记类型线性增长，递归环实测 ~2.3KiB/层（jsonfix），256 层深度安全
 * 语料（json_read_nested）≈586KiB；512KiB 旧预算已越线（该语料
 * b5-2a/b5-2c 定稿即败），1 MiB 留 ~1.7× 余量。留出余量用于构造并
 * 传播 RuntimeException，避免触及宿主 guard page。 */
static _Thread_local uintptr_t rigi_stack_origin = 0;

#if defined(_MSC_VER)
__declspec(noinline)
#else
__attribute__((noinline))
#endif
int32_t rigi_stack_has_room(void)
{
    volatile unsigned char marker = 0;
    uintptr_t here = (uintptr_t)&marker;
    uintptr_t distance;
    if (rigi_stack_origin == 0)
    {
        rigi_stack_origin = here;
        return 1;
    }
    distance = here > rigi_stack_origin
        ? here - rigi_stack_origin : rigi_stack_origin - here;
    return distance < (uintptr_t)(1024u * 1024u) ? 1 : 0;
}

/* ===== B2-4b1：标准流原语（STDLIB 05-io §4.4 标准流与控制台契约）=====
 * stdlib core/io/stdstreams.rg 的 stdout_write/stderr_write/
 * stdout_flush/stderr_flush 四原生面（priv static native）与 Console
 * print/printErr 的统一写通道。纯 CRT/POSIX 调用，不依赖 libuv，
 * 无降级分支（worker.c 的 #if RIGI_HAS_LIBUV 先例与本面无关）；
 * Windows 在 main 启动期将 CRT 三路标准流设为二进制模式，避免文本
 * 模式改写原始字节；flush 的文件持久化另由 _WIN32 分支处理。 */

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <io.h>
#include <fcntl.h>
#else
#include <errno.h>
#include <unistd.h>
#include <sys/stat.h>
#endif

/* 把 data[0..len) 单次 fwrite 进 stream（不刷新——刷新语义独立成
 * flush 面，行原子性与持久化语义由调用方决定刷新时机）。返回实际
 * 写出字节数；len <= 0 恒返回 0。短写由 Rigi 包装层循环补齐
 *（OutputStream.write 契约：成功返回即写完全部请求范围） */
static int32_t rigi_stdio_write_stream(FILE *stream, const uint8_t *data,
    int32_t len)
{
    if (len <= 0)
    {
        return 0;
    }
    size_t written = fwrite(data, 1, (size_t)len, stream);
    if (written > (size_t)INT32_MAX)
    {
        written = (size_t)INT32_MAX; /* 单次 len 上限 i32，防御性钳制 */
    }
    return (int32_t)written;
}

/* 刷新 stream（§4.4 关闭与刷新契约：flush 成功返回表示此前写入已经
 * 真正完成）：先 fflush 提交 C 运行时缓冲；stdout 重定向到常规文件时
 * 遵守文件流的持久化 flush 契约，追加系统持久化刷新（POSIX fsync /
 * Windows FlushFileBuffers）；终端/管道按实际设备能力完成写出——
 * 持久化调用只对常规文件句柄做，不把文件持久化机械套用到所有句柄
 *（§4.4 文件流段与标准流段）。返回 0 成功 / -1 失败（fflush 失败或
 * 常规文件持久化刷新失败——§4.4：持久化刷新失败必须报告错误） */
static int32_t rigi_stdio_flush_stream(FILE *stream)
{
    if (fflush(stream) != 0)
    {
        return -1;
    }
#if defined(_WIN32)
    /* Windows：仅磁盘文件句柄（FILE_TYPE_DISK）做 FlushFileBuffers；
     * 控制台/管道句柄不适用（GetFileType 过滤）。句柄取自 fd，
     * _get_osfhandle 失败返回 -1（非法句柄值），GetFileType 判空挡下 */
    HANDLE handle = (HANDLE)_get_osfhandle(_fileno(stream));
    if (GetFileType(handle) == FILE_TYPE_DISK
        && FlushFileBuffers(handle) == 0)
    {
        return -1;
    }
#else
    /* POSIX：仅常规文件（S_ISREG）fsync，其余设备不做。防御性忽略
     * EINVAL/ENOTTY/EROFS——fsync 对不支持刷新的对象失败属设备能力
     * 性质，不视为错误（常规文件判断已过滤绝大多数情形） */
    struct stat st;
    if (fstat(fileno(stream), &st) == 0 && S_ISREG(st.st_mode))
    {
        if (fsync(fileno(stream)) != 0 && errno != EINVAL
            && errno != ENOTTY && errno != EROFS)
        {
            return -1;
        }
    }
#endif
    return 0;
}

/* 写 stdout + 刷新（B2-4b1 Console 衔接：与 stdstreams 的
 * stdout_write/stdout_flush 走同一写通道与刷新助手——§4.4：经标准库
 * 写入同一路标准流的操作语义统一，行输出调用结束前完成刷新）。
 * fwrite 直接按 len 输出，不依赖 NUL 结尾；Console 面无失败语义，
 * 写失败静默保序（原行为） */
void rigi_print(const rigi_string *text)
{
    int64_t done = 0;
    while (done < text->len)
    {
        /* i32 分块调用共用写助手（rigi_string.len 为 i64） */
        int32_t chunk = text->len - done > (int64_t)INT32_MAX
            ? INT32_MAX : (int32_t)(text->len - done);
        int32_t n = rigi_stdio_write_stream(stdout,
            (const uint8_t *)text->data + done, chunk);
        if (n <= 0)
        {
            break;
        }
        done += n;
    }
    (void)rigi_stdio_flush_stream(stdout);
}

/* 写 stderr + 刷新（与 rigi_print 同一刷新助手，stderr 通道独立） */
void rigi_print_err(const rigi_string *text)
{
    int64_t done = 0;
    while (done < text->len)
    {
        int32_t chunk = text->len - done > (int64_t)INT32_MAX
            ? INT32_MAX : (int32_t)(text->len - done);
        int32_t n = rigi_stdio_write_stream(stderr,
            (const uint8_t *)text->data + done, chunk);
        if (n <= 0)
        {
            break;
        }
        done += n;
    }
    (void)rigi_stdio_flush_stream(stderr);
}

/* Span<u8> 写面的共用解包（B2-4a ABI：16B 胖引用槽指针，payload =
 * 缓冲区对象基址，与数组同构 32B 前缀——length i32 在 [24..28)，
 * 元素基址 32）。区间越界属编译器 bug（Rigi 包装层 checkRange 先行
 * 校验；VM 侧同形态抛 VmException），诊断 abort——与 span.c
 * rigi_span_u8_echo 同口径 */
static int32_t rigi_stdio_write_span(FILE *stream, const RigiFatRef *buffer,
    int32_t offset, int32_t count)
{
    if (buffer == NULL || buffer->payload == 0)
    {
        fprintf(stderr, "rigi_rt: 标准流 write 收到空缓冲区（编译器 bug）\n");
        abort();
    }
    uint8_t *base = (uint8_t *)(uintptr_t)buffer->payload;
    int32_t length;
    memcpy(&length, base + 24, sizeof(int32_t));
    if (offset < 0 || count < 0 || offset > length - count)
    {
        fprintf(stderr, "rigi_rt: 标准流 write 区间越界：offset=%d count=%d "
            "length=%d（编译器 bug）\n", offset, count, length);
        abort();
    }
    return rigi_stdio_write_stream(stream, base + 32 + offset, count);
}

/* 写 Span<u8> 指定范围到标准输出/错误输出，返回实际写出字节数
 *（stdstreams.rg 的 stdout_write/stderr_write 原生面） */
int32_t rigi_stdout_write(const RigiFatRef *buffer, int32_t offset,
    int32_t count)
{
    return rigi_stdio_write_span(stdout, buffer, offset, count);
}

int32_t rigi_stderr_write(const RigiFatRef *buffer, int32_t offset,
    int32_t count)
{
    return rigi_stdio_write_span(stderr, buffer, offset, count);
}

/* 刷新标准输出/错误输出（stdout_flush/stderr_flush 原生面）；返回
 * 0 成功 / -1 失败（Rigi 包装层转 core.IOException） */
int32_t rigi_stdout_flush(void)
{
    return rigi_stdio_flush_stream(stdout);
}

int32_t rigi_stderr_flush(void)
{
    return rigi_stdio_flush_stream(stderr);
}

/* ===== B2-4b2：标准输入异步读原语（STDLIB 05-io §4.4 基本读写契约
 * + 标准流段）===== * stdlib core/io/stdstreams.rg 的 stdin_read_start/
 * stdin_read_take 原生面。形态为「启动即返」卸载：阻塞 read 交给现起
 * 的 detached 后台线程（绝不占调用协程的 Worker——§4.4：不得在
 * Compute Worker 上同步阻塞；stdin 读频率低，现起线程比常驻 stdio
 * 线程少一份交接队列，简洁优先），完成后经既有 §19.3 事件通道
 * rigi_event_signal（worker.c，任意线程可调）唤醒 yield 挂起的协程。
 * 与 stdout 面不同，本面依赖 event 挂起唤醒机制——无 libuv 形态下
 * rigi_alarm_wait 本就降级 abort（yield 分流先达），故不另设降级分支。
 * 平台差异仅线程原语一处（_WIN32 分支 _beginthreadex，POSIX pthread）；
 * 读取用单次系统调用 read/_read（非 fread——避免 CRT 缓冲把「已有
 * 部分数据」拖成等待填满，read 系语义才有「允许少于请求数」）。 */

#if defined(_WIN32)
#include <errno.h>
#include <process.h>
#else
#include <pthread.h>
#endif
#include <stdatomic.h>

/* 结果闸：临界区极小（拷出 24B 请求参数 / 写回 8B 结果），且并发进
 * 闸者至多三个（读协程、读线程、下一轮读协程），纯 CAS 自旋锁足够，
 * 免去平台条件变量/临界区初始化差异（worker.c 用 uv 闸，本面不依赖
 * uv 原语） */
static _Atomic int rigi_stdin_gate = 0; /* 0 = 空闲 / 1 = 持有 */

static void rigi_stdin_lock(void)
{
    int expected = 0;
    while (!atomic_compare_exchange_weak_explicit(&rigi_stdin_gate, &expected, 1,
        memory_order_acquire, memory_order_relaxed))
    {
        expected = 0;
    }
}

static void rigi_stdin_unlock(void)
{
    atomic_store_explicit(&rigi_stdin_gate, 0, memory_order_release);
}

/* EOF 粘滞旗标：读得 0 字节（或 stdin 句柄不可用）后置位，此后 start
 * 恒短路返回已 EOF（全局序状态，跨包装对象共享——§4.4：多个包装
 * 对象共享同一输入位置）。atomic 读面，start 快路径无锁 */
static _Atomic int rigi_stdin_eof = 0;

/* 在途读状态（自旋闸内）：inflight = 已登记未完成（同一时刻至多一个
 * 在途读——§4.4 同一流实例不支持并发，跨包装对象并发读同属被禁止的
 * 共享位置并发使用，防御诊断 abort）；done = 结果已就绪待 take；请求
 * 参数在闸内拷出后由读线程离闸使用 */
static int rigi_stdin_inflight = 0;
static int rigi_stdin_done = 0;
static int32_t rigi_stdin_result = 0;
static uint8_t *rigi_stdin_dest = NULL;
static int32_t rigi_stdin_want = 0;
static int64_t rigi_stdin_wake = 0;

/* Span<u8> 读面的共用解包（与 rigi_stdio_write_span 同口径）：返回
 * payload 内的目标地址 = 元素基址 32 + offset。区间越界属编译器 bug
 *（Rigi 包装层 checkRange 先行校验），诊断 abort */
static uint8_t *rigi_stdio_read_span_dest(const RigiFatRef *buffer,
    int32_t offset, int32_t count)
{
    if (buffer == NULL || buffer->payload == 0)
    {
        fprintf(stderr, "rigi_rt: 标准流 read 收到空缓冲区（编译器 bug）\n");
        abort();
    }
    uint8_t *base = (uint8_t *)(uintptr_t)buffer->payload;
    int32_t length;
    memcpy(&length, base + 24, sizeof(int32_t));
    if (offset < 0 || count < 0 || offset > length - count)
    {
        fprintf(stderr, "rigi_rt: 标准流 read 区间越界：offset=%d count=%d "
            "length=%d（编译器 bug）\n", offset, count, length);
        abort();
    }
    return base + 32 + offset;
}

#if defined(_WIN32)
/* Windows 读线程体（_beginthreadex 约定）：返回即线程终止，句柄由
 * 创建侧立即关闭（detach 语义——不等待读完成） */
static unsigned __stdcall rigi_stdin_read_main(void *arg)
{
    (void)arg;
#else
static void *rigi_stdin_read_main(void *arg)
{
    (void)arg;
    pthread_detach(pthread_self());
#endif
    rigi_stdin_lock();
    uint8_t *dest = rigi_stdin_dest;
    int32_t want = rigi_stdin_want;
    rigi_stdin_unlock();
    /* 单次系统调用阻塞读：n > 0 实际字节数（允许少于请求数）；0 =
     * EOF；< 0 错误。EINTR（POSIX 信号中断）重试——不是流错误 */
    int64_t n;
    do
    {
#if defined(_WIN32)
        n = _read(0, dest, (unsigned)want);
#else
        n = read(0, dest, (size_t)want);
#endif
    } while (n < 0 && errno == EINTR);
    if (n == 0)
    {
        atomic_store_explicit(&rigi_stdin_eof, 1, memory_order_relaxed);
    }
    else if (n < 0 && errno == EBADF)
    {
        /* stdin 句柄不可用 = 进程没有标准输入（测试宿主继承的空
         * stdin 等）：按「stdin 已关闭」视为 EOF（§4.4 EOF 路径确定性
         * ——与 VM 宿主 Stream.Read 异常→EOF 口径一致），不报 I/O 错误 */
        n = 0;
        atomic_store_explicit(&rigi_stdin_eof, 1, memory_order_relaxed);
    }
    rigi_stdin_lock();
    rigi_stdin_result = (int32_t)n;
    rigi_stdin_done = 1;
    rigi_stdin_inflight = 0;
    rigi_stdin_unlock();
    /* 结果登记先于事件触发（§19.3 握手的 signal 侧顺序——恢复协程
     * 必见结果）；rigi_event_signal 粘滞幂等，闸外调用 */
    rigi_event_signal(rigi_stdin_wake);
#if defined(_WIN32)
    return 0;
#else
    return NULL;
#endif
}

/* 启动一次 stdin 读（stdin_read_start 原生面）：返回 0 = 已卸载后台
 *（Rigi 层挂起等唤醒）；1 = 已 EOF（粘滞短路，Rigi 层直接返回 0，
 * 不登记请求）。Rigi 层 rc==1 恒不调 take，结果槽不被污染 */
int32_t rigi_stdin_read_start(const RigiFatRef *buffer, int32_t offset,
    int32_t count, int64_t wake)
{
    if (atomic_load_explicit(&rigi_stdin_eof, memory_order_relaxed))
    {
        return 1;
    }
    uint8_t *dest = rigi_stdio_read_span_dest(buffer, offset, count);
    rigi_stdin_lock();
    if (rigi_stdin_inflight)
    {
        fprintf(stderr, "rigi_rt: stdin 已有在途读（共享位置并发读违反"
            "契约）\n");
        abort();
    }
    rigi_stdin_dest = dest;
    rigi_stdin_want = count;
    rigi_stdin_wake = wake;
    rigi_stdin_inflight = 1;
    rigi_stdin_unlock();
#if defined(_WIN32)
    /* _beginthreadex 做 CRT 初始化（比 CreateThread 安全）；句柄立即
     * 关闭即 detach——读完成由事件通道异步交付 */
    uintptr_t thread = _beginthreadex(NULL, 0, rigi_stdin_read_main,
        NULL, 0, NULL);
    if (thread == 0)
    {
        fprintf(stderr, "rigi_rt: stdin 读线程创建失败（环境耗尽）\n");
        abort();
    }
    CloseHandle((HANDLE)thread);
#else
    pthread_t thread;
    if (pthread_create(&thread, NULL, rigi_stdin_read_main, NULL) != 0)
    {
        fprintf(stderr, "rigi_rt: stdin 读线程创建失败（环境耗尽）\n");
        abort();
    }
#endif
    return 0;
}

/* 取上一次读的结果（stdin_read_take 原生面）：只发生在事件唤醒之后
 *（结果登记 happens-before signal，恢复必见 done），未就绪即取属
 * 时序 bug，防御诊断 abort。EOF 短路路径恒不进本面 */
int32_t rigi_stdin_read_take(void)
{
    rigi_stdin_lock();
    if (!rigi_stdin_done)
    {
        fprintf(stderr, "rigi_rt: stdin_read_take 结果未就绪（时序 bug）\n");
        abort();
    }
    int32_t result = rigi_stdin_result;
    rigi_stdin_done = 0;
    rigi_stdin_unlock();
    return result;
}

/* 经 rigi_string_new 分配 rc=1 字符串块并拼接 */
void rigi_string_concat(rigi_string *out, const rigi_string *a, const rigi_string *b)
{
    if (out == NULL || a == NULL || b == NULL || a->len < 0 || b->len < 0
        || a->len > INT64_MAX - b->len
        || (a->len > 0 && a->data == NULL) || (b->len > 0 && b->data == NULL))
    {
        abort();
    }
    int64_t len = a->len + b->len;
    char *data = rigi_string_new(len);
    if (len > 0)
    {
        if (a->len > 0)
        {
            memcpy(data, a->data, (size_t)a->len);
        }
        if (b->len > 0)
        {
            memcpy(data + a->len, b->data, (size_t)b->len);
        }
    }
    out->data = data;
    out->len = len;
}

/* 三态字符串比较：UTF-8 字节序字典序（memcmp 按无符号字节）。eq/ne 即内容
 * 相等——UTF-8 编码唯一，字节序列相等当且仅当串相等；BMP 内字典序与 BIL VM
 * 基准（string.CompareOrdinal，UTF-16 码元序）一致；astral 平面（代理对）
 * 的码元序与码点序分歧随 MW7 胖值化定稿 */
int32_t rigi_string_compare(const rigi_string *a, const rigi_string *b)
{
    int64_t shared = a->len < b->len ? a->len : b->len;
    int cmp = shared > 0 ? memcmp(a->data, b->data, (size_t)shared) : 0;
    if (cmp != 0)
    {
        return cmp < 0 ? -1 : 1;
    }
    if (a->len < b->len)
    {
        return -1;
    }
    if (a->len > b->len)
    {
        return 1;
    }
    return 0;
}

/* MW2 占位检查面 rigi_abort_divided_by_zero 已随 MW9b-G 退场（除零改抛
 * 可被 try/catch 捕获的 core.DividedByZeroException，见 eh.c 三面与
 * Middleware Emit/ExceptionEmitter）；同批退场的还有
 * rigi_abort_invalid_cast / rigi_abort_no_such_method（cast/拆箱失败与
 * 动态 new 无匹配 init 分别改抛 core.CastException /
 * core.NoSuchMethodException） */

/* i64 MIN/-1 的 VM 基准行为是基础设施溢出失败（.NET OverflowException
 * 经 VM 包装后的消息原文），i8/i16/i32 则回绕（不走本面） */
_Noreturn void rigi_abort_arithmetic_overflow(void)
{
    static const char message[] = "Arithmetic operation resulted in an overflow.\n";
    fwrite(message, 1, sizeof(message) - 1, stderr);
    fflush(stderr);
    exit(1);
}

/* 由编译器发射（BIL entrypoint fn） */
#ifndef RIGI_LIBRARY
extern int32_t rigi_entry(int32_t argc, char **argv);
#endif
extern void rigi_globals_cleanup(void);

/* macrogc.c（MW12）：GC 协程承载线程 + fence 平台事件。init 在
 * rigi_entry 之前（运行时初始化时创建、进程常驻，RUNTIME §23.2）。
 * gexc.c（MW12b）：全局异常通道 flush——globals_cleanup 与 GC 终轮
 * 收集阶段入队的晚到 undisposed 事件由 C 侧默认打印（不经用户处理器）。
 * atexit 注册序 = mem_report, gexc_flush, gc_shutdown, globals_cleanup
 *（LIFO 执行 = globals_cleanup → gc_shutdown（终轮收集兜底全局槽释放
 * 产生的末批候选）→ gexc_flush（晚到事件默认打印 + 队列缓冲归还，
 * 保住 memtrack 零泄漏口径）→ mem_report）。 */
extern void rigi_gc_init(void);
extern void rigi_gc_shutdown(void);
extern void rigi_gexc_flush_default(void);

#if defined(_WIN32)
/* CRT 的文本模式会把输出 LF 扩为 CRLF，并在 _read 输入时折叠 CRLF、
 * 将 0x1A 当 EOF。进程启动时、任何运行时输出/异步读线程创建前，只设置
 * 一次现有标准 fd 的模式；不替换、关闭句柄，也不在每次 write 时切换。
 * 未挂接的标准 fd 维持原有不可用/EOF 行为。 */
static int rigi_stdio_binary_stream(FILE *stream)
{
    int fd = _fileno(stream);
    return fd < 0 || _setmode(fd, _O_BINARY) != -1;
}
#endif

static void rigi_process_init(void)
{
#if defined(_WIN32)
    if (!rigi_stdio_binary_stream(stdin)
        || !rigi_stdio_binary_stream(stdout)
        || !rigi_stdio_binary_stream(stderr))
    {
        fputs("rigi_rt: 无法设置标准流二进制模式\n", stderr);
        exit(1);
    }
#endif
    atexit(rigi_mem_report);          /* 先注册后执行：报告最后跑 */
    atexit(rigi_gexc_flush_default);  /* LIFO 序：cleanup → gc_shutdown → gexc_flush → report */
    atexit(rigi_gc_shutdown);
    atexit(rigi_globals_cleanup);
    rigi_gc_init();
}

#ifdef RIGI_LIBRARY
/* 唯一 RT owner、同宿主线程同步 ABI；库保持加载直到进程退出。 */
extern void rigi_library_init(void);
static uv_once_t rigi_library_once = UV_ONCE_INIT;
static uv_thread_t rigi_library_owner;
static void rigi_library_start(void)
{
    rigi_library_owner = uv_thread_self();
    rigi_process_init();
    rigi_library_init();
}
void rigi_library_ensure(void)
{
    uv_once(&rigi_library_once, rigi_library_start);
    uv_thread_t caller = uv_thread_self();
    if (!uv_thread_equal(&caller, &rigi_library_owner))
    {
        fputs("rigi_rt: C 导出仅允许初始化它的同一宿主线程调用\n", stderr);
        exit(1);
    }
}
_Noreturn void rigi_library_halt(void)
{
    fputs("rigi_rt: 未捕获的 Rigi 异常\n", stderr);
    exit(1);
}
#else
/* 由入口传入真实闭合 Array<String>/String sheet；每个字符串 +1 移交数组槽。 */
void *rigi_argv_build(int32_t argc, char **argv, const RigiTypeSheet *array_sheet,
    const RigiTypeSheet *string_sheet)
{
    int32_t count = argc > 0 ? argc - 1 : 0;
    /* 先完整验证，再分配；无效 POSIX 字节不会产生半构造的托管参数图。 */
    for (int32_t i = 0; i < count; i++)
    {
        size_t length = strlen(argv[i + 1]);
        if (length > INT32_MAX || !rigi_utf8_validate((const uint8_t *)argv[i + 1], (int32_t)length))
        {
            fputs("rigi_rt: 程序参数不是合法 UTF-8\n", stderr);
            exit(1);
        }
    }
    void *object = rigi_alloc_contiguous(array_sheet, string_sheet, count);
    rigi_string *elements = (rigi_string *)((char *)object + 32);
    for (int32_t i = 0; i < count; i++)
    {
        size_t length = strlen(argv[i + 1]);
        char *data = rigi_string_new((int64_t)length);
        memcpy(data, argv[i + 1], length);
        elements[i].data = data; elements[i].len = (int64_t)length;
    }
    return object;
}
#if defined(_WIN32)
int wmain(int argc, wchar_t **wide_argv)
{
    rigi_process_init();
    char **argv = (char **)calloc((size_t)argc, sizeof(char *));
    if (argv == NULL) abort();
    for (int i = 1; i < argc; i++)
    {
        int length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide_argv[i], -1, NULL, 0, NULL, NULL);
        if (length <= 0) { fputs("rigi_rt: 程序参数不是合法 Unicode\n", stderr); exit(1); }
        argv[i] = (char *)malloc((size_t)length);
        if (argv[i] == NULL) abort();
        if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide_argv[i], -1, argv[i], length, NULL, NULL) != length) abort();
    }
    int code = rigi_entry(argc, argv);
    for (int i = 1; i < argc; i++) free(argv[i]);
    free(argv); return code;
}
#else
int main(int argc, char **argv)
{
    rigi_process_init();
    return rigi_entry(argc, argv);
}
#endif
#endif
