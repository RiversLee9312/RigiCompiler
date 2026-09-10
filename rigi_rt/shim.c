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
#include "rigi_string.h"

/* 原生自递归调用前的轻量栈余量守卫。每个 OS 线程首次进入生成代码时
 * 记录近似栈基准；向任一方向消耗超过 512 KiB 即要求语言层停止递归。
 * 留出余量用于构造并传播 RuntimeException，避免触及宿主 guard page。 */
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
    return distance < (uintptr_t)(512u * 1024u) ? 1 : 0;
}

/* 写 stdout + fflush：fwrite 直接按 len 输出，不依赖 NUL 结尾 */
void rigi_print(const rigi_string *text)
{
    if (text->len > 0)
    {
        fwrite(text->data, 1, (size_t)text->len, stdout);
    }
    fflush(stdout);
}

/* 写 stderr + fflush */
void rigi_print_err(const rigi_string *text)
{
    if (text->len > 0)
    {
        fwrite(text->data, 1, (size_t)text->len, stderr);
    }
    fflush(stderr);
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
extern int32_t rigi_entry(void);
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

int main(void)
{
    atexit(rigi_mem_report);          /* 先注册后执行：报告最后跑 */
    atexit(rigi_gexc_flush_default);  /* LIFO 序：cleanup → gc_shutdown → gexc_flush → report */
    atexit(rigi_gc_shutdown);
    atexit(rigi_globals_cleanup);
    rigi_gc_init();
    return rigi_entry();
}
