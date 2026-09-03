/*
 * gexc.c（MW12b）：全局异常通道队列实现（gexc.h 契约）。
 * 队列 = track_malloc 动态数组（TypeSheet* 元素）+ head 游标（FIFO）；
 * 并发入队（release 多线程路径）经 atomic 自旋闸互斥。默认打印文本与
 * stdlib core.GlobalExceptionHandler.dispatch 的空注册表分支同一文本：
 *   core::UndisposedResourceException: 对象在销毁前从未调用 dispose()：<类型全名>
 */
#include "gexc.h"

#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#define gexc_cpu_relax() SwitchToThread()
#else
#include <sched.h>
#define gexc_cpu_relax() sched_yield()
#endif

static const RigiTypeSheet **gexc_queue = NULL;
static size_t gexc_head = 0;
static size_t gexc_len = 0;
static size_t gexc_cap = 0;
static _Atomic int gexc_gate = 0;

static void gexc_lock(void)
{
    while (atomic_exchange_explicit(&gexc_gate, 1, memory_order_acquire) != 0)
    {
        gexc_cpu_relax();
    }
}

static void gexc_unlock(void)
{
    atomic_store_explicit(&gexc_gate, 0, memory_order_release);
}

static void gexc_default_print(const rigi_string *name)
{
    static const char prefix[] =
        "core::UndisposedResourceException: 对象在销毁前从未调用 dispose()：";
    fwrite(prefix, 1, sizeof(prefix) - 1, stderr);
    if (name != NULL && name->len > 0)
    {
        fwrite(name->data, 1, (size_t)name->len, stderr);
    }
    fputc('\n', stderr);
    fflush(stderr);
}

void rigi_gexc_report_undisposed(const RigiTypeSheet *sheet)
{
    gexc_lock();
    if (gexc_head > 0 && gexc_head == gexc_len)
    {
        /* take 排空后的游标归位（复用已分配容量） */
        gexc_head = 0;
        gexc_len = 0;
    }
    if (gexc_len == gexc_cap)
    {
        size_t newCap = gexc_cap == 0 ? 16 : gexc_cap * 2;
        const RigiTypeSheet **grown = (const RigiTypeSheet **)rigi_track_malloc(
            newCap * sizeof(const RigiTypeSheet *));
        if (gexc_len > gexc_head)
        {
            memcpy(grown, gexc_queue + gexc_head,
                (gexc_len - gexc_head) * sizeof(const RigiTypeSheet *));
        }
        rigi_track_free(gexc_queue);
        gexc_queue = grown;
        gexc_len -= gexc_head;
        gexc_head = 0;
        gexc_cap = newCap;
    }
    gexc_queue[gexc_len++] = sheet;
    gexc_unlock();
}

int32_t rigi_gexc_take(rigi_string *out)
{
    const RigiTypeSheet *sheet;
    const rigi_string *name;
    gexc_lock();
    if (gexc_head >= gexc_len)
    {
        gexc_unlock();
        return 0;
    }
    sheet = gexc_queue[gexc_head++];
    /* TypeInfo.name 借用拷出：IMMORTAL 字面量块随全局 sheet 永生，
     * 不 acquire；调用方（entry stub drain）也不 release */
    name = sheet->typeInfoId != NULL ? &sheet->typeInfoId->name : NULL;
    out->data = name != NULL ? name->data : NULL;
    out->len = name != NULL ? name->len : 0;
    gexc_unlock();
    return 1;
}

void rigi_gexc_flush_default(void)
{
    size_t i;
    const RigiTypeSheet **queue;
    gexc_lock();
    queue = gexc_queue;
    for (i = gexc_head; i < gexc_len; i++)
    {
        const RigiTypeSheet *sheet = queue[i];
        gexc_default_print(sheet->typeInfoId != NULL
            ? &sheet->typeInfoId->name : NULL);
    }
    gexc_queue = NULL;
    gexc_head = 0;
    gexc_len = 0;
    gexc_cap = 0;
    gexc_unlock();
    /* 队列缓冲在锁外归还（memtrack 口径：atexit 末段 mem_report 之前清零） */
    rigi_track_free(queue);
}

/* ------------------------------------------------------------------ */
/* 处理器注册表：动态数组（RigiFatRef +1 持有）+ atomic_flag 自旋闸      */
/* （failreg.c 同形态）；注册序 = 下标序。残余条目随进程退出经 atexit    */
/* 链释放（注册时机在首次登记——LIFO 先于 shim.c 预注册四件，条目析构     */
/* 若触发末批 undisposed 事件仍由 gexc_flush 兜底打印）。                */
/* ------------------------------------------------------------------ */
static RigiFatRef *gexc_handlers = NULL;
static size_t gexc_handlers_len = 0;
static size_t gexc_handlers_cap = 0;
static atomic_flag gexc_handlers_gate = ATOMIC_FLAG_INIT;
static int gexc_handlers_cleanup_registered = 0;

static void gexc_handlers_lock(void)
{
    while (atomic_flag_test_and_set_explicit(&gexc_handlers_gate,
               memory_order_acquire))
    {
    }
}

static void gexc_handlers_unlock(void)
{
    atomic_flag_clear_explicit(&gexc_handlers_gate, memory_order_release);
}

static void gexc_handlers_cleanup(void)
{
    size_t i;
    gexc_handlers_lock();
    for (i = 0; i < gexc_handlers_len; i++)
    {
        if (gexc_handlers[i].payload != 0)
        {
            /* tag2 对象：payload 即对象地址，头内 rc/sheet 自足 */
            rigi_release_shared((void *)(uintptr_t)gexc_handlers[i].payload);
        }
    }
    rigi_track_free(gexc_handlers);
    gexc_handlers = NULL;
    gexc_handlers_len = 0;
    gexc_handlers_cap = 0;
    gexc_handlers_unlock();
}

int64_t rigi_gexc_register_handler(const RigiFatRef *handler)
{
    size_t index;
    if (handler == NULL)
    {
        fprintf(stderr, "rigi_rt: rigi_gexc_register_handler 参数为 NULL（编译器 bug）\n");
        abort();
    }
    gexc_handlers_lock();
    if (!gexc_handlers_cleanup_registered)
    {
        gexc_handlers_cleanup_registered = 1;
        atexit(gexc_handlers_cleanup);
    }
    if (gexc_handlers_len == gexc_handlers_cap)
    {
        size_t newCap = gexc_handlers_cap == 0 ? 8 : gexc_handlers_cap * 2;
        RigiFatRef *grown = (RigiFatRef *)rigi_track_malloc(
            newCap * sizeof(RigiFatRef));
        if (gexc_handlers_len > 0)
        {
            memcpy(grown, gexc_handlers, gexc_handlers_len * sizeof(RigiFatRef));
        }
        rigi_track_free(gexc_handlers);
        gexc_handlers = grown;
        gexc_handlers_cap = newCap;
    }
    index = gexc_handlers_len++;
    gexc_handlers[index] = *handler;
    if (handler->payload != 0)
    {
        rigi_acquire_shared((void *)(uintptr_t)handler->payload);
    }
    gexc_handlers_unlock();
    return (int64_t)index;
}

int64_t rigi_gexc_handler_count(void)
{
    int64_t count;
    gexc_handlers_lock();
    count = (int64_t)gexc_handlers_len;
    gexc_handlers_unlock();
    return count;
}

void rigi_gexc_handler_at(RigiFatRef *out, int64_t index)
{
    if (out == NULL)
    {
        fprintf(stderr, "rigi_rt: rigi_gexc_handler_at 参数为 NULL（编译器 bug）\n");
        abort();
    }
    gexc_handlers_lock();
    if (index < 0 || (size_t)index >= gexc_handlers_len)
    {
        gexc_handlers_unlock();
        fprintf(stderr, "rigi_rt: rigi_gexc_handler_at 下标 %lld 越界（编译器 bug）\n",
            (long long)index);
        abort();
    }
    *out = gexc_handlers[index];
    if (out->payload != 0)
    {
        rigi_acquire_shared((void *)(uintptr_t)out->payload);
    }
    gexc_handlers_unlock();
}
