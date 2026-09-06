/*
 * macrogc.c（MW12）：macroGC —— 候选驱动的 Bacon-Rajan 循环收集器 +
 * RUNTIME §23 ownership fence 的 native 落地。
 *
 * 算法（MIDDLEWARE §4.5）：同步三阶段 markGray / scan / collectWhite，
 * 显式 trace 栈防深递归爆栈；候选从 release 路径登记（从不枚举栈/全局根
 * ——外部活跃性由 RC 计数本身表达）；白色集合先攒后统一清理，清理前对
 * 存活子引用补偿计数（防析构副作用破坏遍历的既定偏离）；收集期间禁止
 * 析构路径二次登记候选；回收前经 rigi_dispose_check 执行 §25 检查
 * （MW12b 填充真检查，本阶段挂点保持 no-op 语义）。
 *
 * fence（RUNTIME §23）：进入 pass 冻结全部托管引用 acquire/release
 * （local 与 shared）。§23.3 隐藏 yield GCAlarm 的 native 降级 =
 * region_enter 内阻塞等平台事件：region 内禁止挂起点（同步函数无法
 * 降级状态机），阻塞期间协程不迁移，故 cFlag 用 OS 线程槽即满足
 * §23.1「绑定 Coroutine」的身份要求（Worker-local 缓存式实现，正确性
 * 不依赖挂起前后的 Worker 绑定）。GC 协程 = 常驻专用线程（内置 GC
 * Executor 的 native 形态），GCWakeAlarm/GCAlarm = 双平台手动复位事件
 * （§19.3 sticky/幂等语义），不经 Rigi Dispatcher 通道，不进 stdlib。
 *
 * 正确性关键（推导备忘）：
 *   - markGray 对灰对象的每条出边做 rc--；scan 对 rc>0 的灰做 scanBlack
 *     全闭包 rc++ 回补；白色对象的入边只可能来自灰变白对象（若存在
 *     未灰化前驱则 rc 不会归 0；若存在黑化前驱则 scanBlack 会连带救回）。
 *   - 补偿阶段在清理前对白→存活边逐一 rc++，使清理阶段的正常 release
 *     不会把存活者错减到 0（即清理期不会发生级联析构，遍历结构稳定）。
 *   - 白→白边在清理时整条跳过（双方同批释放），markGray 的减量随对象
 *     释放一并湮灭，不影响任何存活计数。
 */
#ifdef _WIN32
#define _CRT_SECURE_NO_WARNINGS
#endif
#include "macrogc.h"

#include <stdatomic.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#else
#include <pthread.h>
#include <sched.h>
#include <time.h>
#endif

/* ------------------------------------------------------------------ */
/* 平台薄封装：手动复位事件 / 常驻线程 / 让步 / 单调钟                  */
/* ------------------------------------------------------------------ */

typedef struct
{
#ifdef _WIN32
    HANDLE evt;
#else
    pthread_mutex_t mu;
    pthread_cond_t cv;
    int signaled;
#endif
} GcEvent;

static void gc_event_init(GcEvent *e)
{
#ifdef _WIN32
    e->evt = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (e->evt == NULL)
    {
        fprintf(stderr, "rigi_rt: CreateEvent 失败（环境耗尽）\n");
        abort();
    }
#else
    if (pthread_mutex_init(&e->mu, NULL) != 0
        || pthread_cond_init(&e->cv, NULL) != 0)
    {
        fprintf(stderr, "rigi_rt: pthread 同步原语初始化失败（环境耗尽）\n");
        abort();
    }
    e->signaled = 0;
#endif
}

static void gc_event_destroy(GcEvent *e)
{
#ifdef _WIN32
    if (e->evt != NULL)
    {
        CloseHandle(e->evt);
        e->evt = NULL;
    }
#else
    pthread_cond_destroy(&e->cv);
    pthread_mutex_destroy(&e->mu);
#endif
}

static void gc_event_signal(GcEvent *e)
{
#ifdef _WIN32
    SetEvent(e->evt);
#else
    pthread_mutex_lock(&e->mu);
    e->signaled = 1;
    pthread_cond_broadcast(&e->cv);
    pthread_mutex_unlock(&e->mu);
#endif
}

static void gc_event_reset(GcEvent *e)
{
#ifdef _WIN32
    ResetEvent(e->evt);
#else
    pthread_mutex_lock(&e->mu);
    e->signaled = 0;
    pthread_mutex_unlock(&e->mu);
#endif
}

static void gc_event_wait(GcEvent *e)
{
#ifdef _WIN32
    WaitForSingleObject(e->evt, INFINITE);
#else
    pthread_mutex_lock(&e->mu);
    while (!e->signaled)
    {
        pthread_cond_wait(&e->cv, &e->mu);
    }
    pthread_mutex_unlock(&e->mu);
#endif
}

static void gc_cpu_relax(void)
{
#ifdef _WIN32
    SwitchToThread();
#else
    sched_yield();
#endif
}

static uint64_t gc_now_ms(void)
{
#ifdef _WIN32
    return (uint64_t)GetTickCount64();
#else
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000u + (uint64_t)ts.tv_nsec / 1000000u;
#endif
}

/* ------------------------------------------------------------------ */
/* 全局状态                                                             */
/* ------------------------------------------------------------------ */

/* gcFlag：cache-line 独占对齐原子（§23.1），全局唯一 */
union GcFlagLine
{
    _Atomic uint32_t value;
    char pad[64];
};
static _Alignas(64) union GcFlagLine gc_flag_u; /* 静态零初始化 = IDLE */
#define gc_flag (gc_flag_u.value)

/* cFlag 注册表：每执行线程懒认领一枚 cache-line 独占槽（认领后常驻；
 * 本运行时的线程集 = main + 有界 Worker，256 槽上限足够，耗尽即亮红灯）。
 * claimed 与 value 分离：GC 只巡视已认领槽。sizeof==64 + 数组基址 64
 * 对齐 ⇒ 每槽独占一 cache line。 */
#define RIGI_GC_MAX_CFLAG 256
typedef union
{
    struct
    {
        _Atomic uint32_t claimed;
        _Atomic uint32_t value;
    } s;
    char pad[64];
} GcFlagSlot;
static _Alignas(64) GcFlagSlot gc_cflags[RIGI_GC_MAX_CFLAG];
static _Thread_local _Atomic uint32_t *gc_my_cflag = NULL;

/* 候选账本：swap-remove 经对象头索引位域定位。
 * 账本锁：release 路径在 region 内执行，但 region 只 fence 不跨线程互斥
 * （多线程可同时处于 PROCESSING），shared 对象的并发 release 会并发登记——
 * 账本操作必须自旋锁保护（短临界区；pass 期间图已冻结，锁无争用）。 */
typedef struct
{
    void *object;
    long size;
} GcCandidate;
static GcCandidate *gc_ledger = NULL;
static size_t gc_ledger_len = 0;
static size_t gc_ledger_cap = 0;
static _Atomic int gc_ledger_lock = 0;
#define RIGI_GC_LEDGER_MAX (1u << 24) /* 索引位域 24bit 上限 */

static void gc_ledger_acquire(void)
{
    while (atomic_exchange_explicit(&gc_ledger_lock, 1, memory_order_acquire)
        != 0)
    {
        gc_cpu_relax();
    }
}

static void gc_ledger_release(void)
{
    atomic_store_explicit(&gc_ledger_lock, 0, memory_order_release);
}

/* 债务与触发（RUNTIME §22.3）：全局原子债务 + 阈值 + 原子 pending 去重 */
static _Atomic long gc_debt = 0;
static long gc_threshold = 1L << 20; /* 默认 1 MiB，RIGI_RT_GC_THRESHOLD 覆盖 */
static _Atomic int gc_pending = 0;
static _Atomic int gc_stop = 0;
static _Atomic int gc_in_collect = 0;
static _Atomic long gc_fence_waits = 0; /* ENTERING 阻塞次数（诊断口径） */
static int gc_started = 0;
static int gc_off = 0;    /* RIGI_RT_GC_OFF=1：纯 ARC 对照（诊断用） */
static int gc_trace_on = 0;  /* RIGI_RT_GC_TRACE=1：候选/收集全链路追踪（诊断用） */
static int gc_stats_on = 0; /* 按轮统计，避免逐对象日志扰动压力运行。 */
static uint64_t gc_stats_passes = 0;
static uint64_t gc_stats_slots = 0;
static uint64_t gc_stats_skipped = 0;

/* 压力入口采样只读原子状态，不开逐轮 stderr 诊断。 */
int64_t rigi_gc_active(void)
{
    return atomic_load_explicit(&gc_in_collect, memory_order_relaxed);
}

int64_t rigi_gc_debt_bytes(void)
{
    return atomic_load_explicit(&gc_debt, memory_order_relaxed);
}

/* GC 协程承载线程与双 Alarm 平台事件 */
static GcEvent gc_wake;   /* GCWakeAlarm：release 阈值触发 → GC 协程 */
static GcEvent gc_alarm;  /* GCAlarm：pass 结束 → 唤醒 ENTERING 等待者 */
#ifdef _WIN32
static HANDLE gc_thread = NULL;
#else
static pthread_t gc_thread;
static int gc_thread_valid = 0;
#endif

/* ------------------------------------------------------------------ */
/* 小工具：rc 原子访问 / 颜色位 / 可变向量（trace 栈与白色清单共用）     */
/* ------------------------------------------------------------------ */

static uint32_t gc_rc_load(const RigiObjectHeader *h)
{
    return atomic_load_explicit(
        (const _Atomic uint32_t *)&h->rc, memory_order_relaxed);
}

static void gc_rc_store(RigiObjectHeader *h, uint32_t v)
{
    atomic_store_explicit((_Atomic uint32_t *)&h->rc, v, memory_order_relaxed);
}

static uint32_t gc_color(const RigiObjectHeader *h)
{
    return atomic_load_explicit((const _Atomic uint32_t *)&h->packedFlags,
        memory_order_relaxed) & RIGI_GC_COLOR_MASK;
}

static void gc_flags_replace(RigiObjectHeader *h, uint32_t mask, uint32_t value)
{
    _Atomic uint32_t *flags = (_Atomic uint32_t *)&h->packedFlags;
    uint32_t old = atomic_load_explicit(flags, memory_order_relaxed);
    while (!atomic_compare_exchange_weak_explicit(flags, &old,
        (old & ~mask) | value, memory_order_relaxed, memory_order_relaxed)) { }
}

static void gc_set_color(RigiObjectHeader *h, uint32_t color)
{
    gc_flags_replace(h, RIGI_GC_COLOR_MASK, color);
}

typedef struct
{
    void **items;
    size_t len;
    size_t cap;
} GcVec;

static void gc_vec_push(GcVec *v, void *p)
{
    if (v->len == v->cap)
    {
        size_t ncap = v->cap != 0 ? v->cap * 2 : 256;
        void **nbuf = (void **)rigi_track_malloc(ncap * sizeof(void *));
        if (v->items != NULL)
        {
            memcpy(nbuf, v->items, v->len * sizeof(void *));
            rigi_track_free(v->items);
        }
        v->items = nbuf;
        v->cap = ncap;
    }
    v->items[v->len++] = p;
}

static void *gc_vec_pop(GcVec *v)
{
    return v->items[--v->len];
}

static void gc_vec_destroy(GcVec *v)
{
    rigi_track_free(v->items);
    v->items = NULL;
    v->len = 0;
    v->cap = 0;
}

/* GC 线程自用的 trace 栈与白色清单（单线程复用，随 shutdown 释放） */
static GcVec gc_trace_stack;
static GcVec gc_whites;

/* ------------------------------------------------------------------ */
/* 子引用遍历：与 rigi_destruct 的走查严格同构（改结构须双侧同步）。     */
/* 对每个 tag2 对象子引用回调 fn(child, ctx)；tag1 盒递归走查其 refMap    */
/* （盒是唯一持有的裸块，不构成环成员，但其内容引用是宿主的图边）；       */
/* STRING 槽无对象出边，跳过。payload==0 的空引用自然跳过。              */
/* ------------------------------------------------------------------ */

typedef void (*GcChildFn)(void *child, void *ctx);

static void gc_trace_fat(uint64_t type_id, uint64_t payload, GcChildFn fn, void *ctx);

static void gc_trace_block(void *ptr, const RigiTypeSheet *sheet,
    GcChildFn fn, void *ctx)
{
    const char *base;
    size_t cursor;
    uint32_t i;
    if (ptr == NULL || sheet == NULL || sheet->refMap == NULL)
    {
        return;
    }
    base = (const char *)ptr;
    cursor = 0;
    for (i = 0; i < sheet->refMapSize; i++)
    {
        uint16_t entry = sheet->refMap[i];
        uint16_t kind = (uint16_t)(entry >> RIGI_REFMAP_KIND_SHIFT);
        uint16_t hops = (uint16_t)(entry & RIGI_REFMAP_HOP_MASK);
        cursor += (size_t)hops * 16u;
        if (kind != RIGI_REFMAP_STRING)
        {
            uint64_t type_id = *(const uint64_t *)(base + cursor);
            uint64_t payload = *(const uint64_t *)(base + cursor + 8);
            gc_trace_fat(type_id, payload, fn, ctx);
        }
        cursor += 16u;
    }
}

static void gc_trace_fat(uint64_t type_id, uint64_t payload, GcChildFn fn, void *ctx)
{
    uint64_t tag = type_id >> RIGI_TAG_SHIFT;
    const RigiTypeSheet *sheet;
    if (payload == 0)
    {
        return;
    }
    if (tag == RIGI_TAG_OBJECT)
    {
        fn((void *)(uintptr_t)payload, ctx);
        return;
    }
    if (tag == RIGI_TAG_HEAP_VALUE)
    {
        sheet = (const RigiTypeSheet *)(uintptr_t)(type_id & RIGI_SHEET_MASK);
        gc_trace_block((void *)(uintptr_t)payload, sheet, fn, ctx);
    }
}

static void gc_trace(void *object, const RigiTypeSheet *sheet,
    GcChildFn fn, void *ctx)
{
    const char *base = (const char *)object;
    size_t cursor;
    uint32_t i;

    if (sheet != NULL && (sheet->typeFlags & RIGI_TYPE_ARRAY) != 0)
    {
        const RigiTypeSheet *elemSheet =
            *(RigiTypeSheet *const *)((char *)object + 16);
        int32_t len = *(int32_t *)((char *)object + 24);
        const char *elems = (const char *)object + 32;
        int32_t stride;
        int32_t idx;
        // 无引用内联元素没有对象图出边；不逐槽空扫大数值缓冲区。
        if (elemSheet != NULL
            && (elemSheet->typeFlags & RIGI_TYPE_INLINE_VALUE) != 0
            && elemSheet->refMapSize == 0
            && (elemSheet->typeFlags & RIGI_TYPE_STRING) == 0)
        {
            if (gc_stats_on) gc_stats_skipped += (uint64_t)len;
            return;
        }
        if (gc_stats_on) gc_stats_slots += (uint64_t)len;
        if (elemSheet != NULL
            && (elemSheet->typeFlags & RIGI_TYPE_INLINE_VALUE) != 0
            && elemSheet->typeSize > 0)
        {
            stride = (int32_t)elemSheet->typeSize;
        }
        else
        {
            stride = 16;
        }
        for (idx = 0; idx < len; idx++)
        {
            void *elem = (void *)(elems + (size_t)idx * (size_t)stride);
            if (elemSheet == NULL)
            {
                uint64_t type_id = *(const uint64_t *)elem;
                uint64_t payload = *(const uint64_t *)((const char *)elem + 8);
                gc_trace_fat(type_id, payload, fn, ctx);
                continue;
            }
            if ((elemSheet->typeFlags & RIGI_TYPE_STRING) != 0)
            {
                uint64_t elem_tid = *(const uint64_t *)elem;
                uint64_t elem_tag = elem_tid >> RIGI_TAG_SHIFT;
                if (elem_tag == RIGI_TAG_HEAP_VALUE || elem_tag == RIGI_TAG_OBJECT)
                {
                    uint64_t elem_pl = *(const uint64_t *)((const char *)elem + 8);
                    gc_trace_fat(elem_tid, elem_pl, fn, ctx);
                }
                /* 真 String ABI 槽：无对象出边 */
            }
            else if ((elemSheet->typeFlags & RIGI_TYPE_INLINE_VALUE) != 0)
            {
                if (elemSheet->refMapSize > 0)
                {
                    gc_trace_block(elem, elemSheet, fn, ctx);
                }
            }
            else
            {
                uint64_t type_id = *(const uint64_t *)elem;
                uint64_t payload = *(const uint64_t *)((const char *)elem + 8);
                gc_trace_fat(type_id, payload, fn, ctx);
            }
        }
        return;
    }

    if (sheet == NULL || sheet->refMap == NULL)
    {
        return;
    }
    cursor = sizeof(RigiObjectHeader);
    for (i = 0; i < sheet->refMapSize; i++)
    {
        uint16_t entry = sheet->refMap[i];
        uint16_t kind = (uint16_t)(entry >> RIGI_REFMAP_KIND_SHIFT);
        uint16_t hops = (uint16_t)(entry & RIGI_REFMAP_HOP_MASK);
        cursor += (size_t)hops * 16u;
        if (kind != RIGI_REFMAP_STRING)
        {
            uint64_t type_id = *(const uint64_t *)(base + cursor);
            uint64_t payload = *(const uint64_t *)(base + cursor + 8);
            gc_trace_fat(type_id, payload, fn, ctx);
        }
        cursor += 16u;
    }
}

/* ------------------------------------------------------------------ */
/* 三阶段染色（全部显式栈迭代；子引用 rc 访问一律 relaxed 原子）          */
/* ------------------------------------------------------------------ */

static void gc_mark_gray_child(void *child, void *ctx)
{
    RigiObjectHeader *ch = (RigiObjectHeader *)child;
    GcVec *st = (GcVec *)ctx;
    gc_rc_store(ch, gc_rc_load(ch) - 1u);
    if (gc_color(ch) != RIGI_GC_GRAY)
    {
        gc_set_color(ch, RIGI_GC_GRAY);
        gc_vec_push(st, child);
    }
}

static void gc_mark_gray(void *root, GcVec *st)
{
    RigiObjectHeader *h = (RigiObjectHeader *)root;
    if (gc_color(h) == RIGI_GC_GRAY)
    {
        return;
    }
    gc_set_color(h, RIGI_GC_GRAY);
    gc_vec_push(st, root);
    while (st->len > 0)
    {
        void *o = gc_vec_pop(st);
        RigiObjectHeader *oh = (RigiObjectHeader *)o;
        gc_trace(o, oh->typeId, gc_mark_gray_child, st);
    }
}

static void gc_scan_black_child(void *child, void *ctx)
{
    RigiObjectHeader *ch = (RigiObjectHeader *)child;
    GcVec *st = (GcVec *)ctx;
    gc_rc_store(ch, gc_rc_load(ch) + 1u);
    if (gc_color(ch) != RIGI_GC_BLACK)
    {
        gc_set_color(ch, RIGI_GC_BLACK);
        gc_vec_push(st, child);
    }
}

static void gc_scan_black(void *root, GcVec *st)
{
    RigiObjectHeader *h = (RigiObjectHeader *)root;
    if (gc_color(h) == RIGI_GC_BLACK)
    {
        return;
    }
    gc_set_color(h, RIGI_GC_BLACK);
    gc_vec_push(st, root);
    while (st->len > 0)
    {
        void *o = gc_vec_pop(st);
        RigiObjectHeader *oh = (RigiObjectHeader *)o;
        gc_trace(o, oh->typeId, gc_scan_black_child, st);
    }
}

static void gc_scan_push_gray_child(void *child, void *ctx)
{
    if (gc_color((RigiObjectHeader *)child) == RIGI_GC_GRAY)
    {
        gc_vec_push((GcVec *)ctx, child);
    }
}

static void gc_scan(void *root, GcVec *scan_st, GcVec *black_st)
{
    gc_vec_push(scan_st, root);
    while (scan_st->len > 0)
    {
        void *o = gc_vec_pop(scan_st);
        RigiObjectHeader *oh = (RigiObjectHeader *)o;
        if (gc_color(oh) != RIGI_GC_GRAY)
        {
            continue;
        }
        if (gc_rc_load(oh) > 0)
        {
            gc_scan_black(o, black_st);
        }
        else
        {
            gc_set_color(oh, RIGI_GC_WHITE);
            gc_trace(o, oh->typeId, gc_scan_push_gray_child, scan_st);
        }
    }
}

/* ------------------------------------------------------------------ */
/* collectWhite：先攒（GRAY 复用为「已入白色集合」标记——scan 结束后     */
/* 无真 GRAY 存活）→ 补偿存活子引用 → 统一清理                          */
/* ------------------------------------------------------------------ */

static void gc_gather_push_white_child(void *child, void *ctx)
{
    if (gc_color((RigiObjectHeader *)child) == RIGI_GC_WHITE)
    {
        gc_vec_push((GcVec *)ctx, child);
    }
}

static void gc_gather_white(void *root, GcVec *st, GcVec *whites)
{
    gc_vec_push(st, root);
    while (st->len > 0)
    {
        void *o = gc_vec_pop(st);
        RigiObjectHeader *oh = (RigiObjectHeader *)o;
        if (gc_color(oh) != RIGI_GC_WHITE)
        {
            continue;
        }
        gc_set_color(oh, RIGI_GC_GRAY); /* 入白色集合标记 */
        gc_vec_push(whites, o);
        gc_trace(o, oh->typeId, gc_gather_push_white_child, st);
    }
}

static void gc_compensate_child(void *child, void *ctx)
{
    RigiObjectHeader *ch = (RigiObjectHeader *)child;
    (void)ctx;
    if (gc_color(ch) != RIGI_GC_GRAY)
    {
        /* 存活子引用：补偿 markGray 的减量（白→白边由同批释放湮灭） */
        gc_rc_store(ch, gc_rc_load(ch) + 1u);
    }
}

/* 清理期的原始释放：不走公共 release 面（fence 冻结期公共面会自锁），
 * 不登记候选（gc_in_collect 双保险），白色同胞边整条跳过。级联析构在
 * 补偿不变量下不会发生，此处递归仅作防御兜底。 */
static void gc_teardown_fat(uint64_t type_id, uint64_t payload);

static void gc_teardown_ex(void *object, const RigiTypeSheet *sheet, int free_self);

static void gc_teardown_block(void *ptr, const RigiTypeSheet *sheet)
{
    const char *base;
    size_t cursor;
    uint32_t i;
    if (ptr == NULL || sheet == NULL || sheet->refMap == NULL)
    {
        return;
    }
    base = (const char *)ptr;
    cursor = 0;
    for (i = 0; i < sheet->refMapSize; i++)
    {
        uint16_t entry = sheet->refMap[i];
        uint16_t kind = (uint16_t)(entry >> RIGI_REFMAP_KIND_SHIFT);
        uint16_t hops = (uint16_t)(entry & RIGI_REFMAP_HOP_MASK);
        cursor += (size_t)hops * 16u;
        if (kind == RIGI_REFMAP_STRING)
        {
            rigi_string_release_unfenced(*(char *const *)(base + cursor));
        }
        else
        {
            uint64_t type_id = *(const uint64_t *)(base + cursor);
            uint64_t payload = *(const uint64_t *)(base + cursor + 8);
            gc_teardown_fat(type_id, payload);
        }
        cursor += 16u;
    }
}

static void gc_teardown(void *object, const RigiTypeSheet *sheet)
{
    gc_teardown_ex(object, sheet, 1);
}

/* free_self=0：只走边（dispose 检查 + 子引用释放），本体留给统一释放段——
 * 白色批清理两段式的第一段；free_self=1：走边并即放本体（级联兜底用） */
static void gc_teardown_ex(void *object, const RigiTypeSheet *sheet, int free_self)
{
    const char *base = (const char *)object;
    size_t cursor;
    uint32_t i;

    if (sheet != NULL && (sheet->typeFlags & RIGI_TYPE_ARRAY) != 0)
    {
        const RigiTypeSheet *elemSheet =
            *(RigiTypeSheet *const *)((char *)object + 16);
        int32_t len = *(int32_t *)((char *)object + 24);
        const char *elems = (const char *)object + 32;
        int32_t stride;
        int32_t idx;
        if (elemSheet != NULL
            && (elemSheet->typeFlags & RIGI_TYPE_INLINE_VALUE) != 0
            && elemSheet->typeSize > 0)
        {
            stride = (int32_t)elemSheet->typeSize;
        }
        else
        {
            stride = 16;
        }
        for (idx = 0; idx < len; idx++)
        {
            void *elem = (void *)(elems + (size_t)idx * (size_t)stride);
            if (elemSheet == NULL)
            {
                uint64_t type_id = *(const uint64_t *)elem;
                uint64_t payload = *(const uint64_t *)((const char *)elem + 8);
                gc_teardown_fat(type_id, payload);
                continue;
            }
            if ((elemSheet->typeFlags & RIGI_TYPE_STRING) != 0)
            {
                uint64_t elem_tid = *(const uint64_t *)elem;
                uint64_t elem_tag = elem_tid >> RIGI_TAG_SHIFT;
                if (elem_tag == RIGI_TAG_HEAP_VALUE || elem_tag == RIGI_TAG_OBJECT)
                {
                    uint64_t elem_pl = *(const uint64_t *)((const char *)elem + 8);
                    gc_teardown_fat(elem_tid, elem_pl);
                }
                else
                {
                    rigi_string_release_unfenced(*(char *const *)elem);
                }
            }
            else if ((elemSheet->typeFlags & RIGI_TYPE_INLINE_VALUE) != 0)
            {
                if (elemSheet->refMapSize > 0
                    || (elemSheet->typeFlags & RIGI_TYPE_RICH) != 0)
                {
                    gc_teardown_block(elem, elemSheet);
                }
            }
            else
            {
                uint64_t type_id = *(const uint64_t *)elem;
                uint64_t payload = *(const uint64_t *)((const char *)elem + 8);
                gc_teardown_fat(type_id, payload);
            }
        }
        if (free_self != 0)
        {
            rigi_track_free(object);
        }
        return;
    }

    /* §25 IDisposable 合约检查挂点（macroGC 清理步入点） */
    rigi_dispose_check(object, sheet);
    rigi_native_resources_destroy(object, sheet);
    if (sheet == NULL || sheet->refMap == NULL)
    {
        if (free_self != 0)
        {
            rigi_track_free(object);
        }
        return;
    }
    cursor = sizeof(RigiObjectHeader);
    for (i = 0; i < sheet->refMapSize; i++)
    {
        uint16_t entry = sheet->refMap[i];
        uint16_t kind = (uint16_t)(entry >> RIGI_REFMAP_KIND_SHIFT);
        uint16_t hops = (uint16_t)(entry & RIGI_REFMAP_HOP_MASK);
        cursor += (size_t)hops * 16u;
        if (kind == RIGI_REFMAP_STRING)
        {
            rigi_string_release_unfenced(*(char *const *)(base + cursor));
        }
        else
        {
            uint64_t type_id = *(const uint64_t *)(base + cursor);
            uint64_t payload = *(const uint64_t *)(base + cursor + 8);
            gc_teardown_fat(type_id, payload);
        }
        cursor += 16u;
    }
    if (free_self != 0)
    {
        rigi_track_free(object);
    }
}

static void gc_teardown_fat(uint64_t type_id, uint64_t payload)
{
    uint64_t tag = type_id >> RIGI_TAG_SHIFT;
    const RigiTypeSheet *sheet;
    RigiObjectHeader *ch;
    uint32_t rc;
    if (payload == 0)
    {
        return;
    }
    if (tag == RIGI_TAG_OBJECT)
    {
        ch = (RigiObjectHeader *)(uintptr_t)payload;
        if (gc_color(ch) == RIGI_GC_GRAY)
        {
            return; /* 白色同胞：同批释放，边整条跳过 */
        }
        rc = gc_rc_load(ch);
        if (rc <= 1u)
        {
            /* 补偿不变量下 rc==0 不会出现、rc==1 减后归零的级联也不会
             * 出现（存活者必有外部/黑化前驱）；递归兜底防御 */
            gc_teardown((void *)ch, ch->typeId);
            return;
        }
        gc_rc_store(ch, rc - 1u);
        return;
    }
    if (tag == RIGI_TAG_HEAP_VALUE)
    {
        sheet = (const RigiTypeSheet *)(uintptr_t)(type_id & RIGI_SHEET_MASK);
        gc_teardown_block((void *)(uintptr_t)payload, sheet);
        rigi_track_free((void *)(uintptr_t)payload);
    }
}

/* ------------------------------------------------------------------ */
/* fence：cFlag 认领 + §23.3 双重检查（region_enter/exit 最外层调用）     */
/* ------------------------------------------------------------------ */

static _Atomic uint32_t *gc_claim_cflag(void)
{
    uint32_t i;
    if (gc_my_cflag != NULL)
    {
        return gc_my_cflag;
    }
    for (i = 0; i < RIGI_GC_MAX_CFLAG; i++)
    {
        uint32_t expected = 0;
        if (atomic_compare_exchange_strong_explicit(
                &gc_cflags[i].s.claimed, &expected, 1u,
                memory_order_acq_rel, memory_order_relaxed))
        {
            gc_my_cflag = &gc_cflags[i].s.value;
            if (gc_trace_on)
            {
#ifdef _WIN32
                fprintf(stderr, "[gc] cflag slot %u claimed by tid=%lu\n",
                    (unsigned)i, (unsigned long)GetCurrentThreadId());
#else
                fprintf(stderr, "[gc] cflag slot %u claimed\n", (unsigned)i);
#endif
            }
            return gc_my_cflag;
        }
    }
    fprintf(stderr, "rigi_rt: cFlag 注册表耗尽（>%u 执行线程）\n",
        (unsigned)RIGI_GC_MAX_CFLAG);
    abort();
}

void rigi_gc_region_fence_enter(void)
{
    _Atomic uint32_t *my = gc_claim_cflag();
    for (;;)
    {
        /* §23.3 双重检查：先查 gcFlag，置 PROCESSING 后 fence 再复查；
         * 任一时刻见非 IDLE 即退 ENTERING 并阻塞等 GCAlarm，醒后完整重检 */
        atomic_thread_fence(memory_order_seq_cst);
        if (atomic_load_explicit(&gc_flag, memory_order_seq_cst) != RIGI_GC_IDLE)
        {
            atomic_fetch_add_explicit(&gc_fence_waits, 1, memory_order_relaxed);
            atomic_store_explicit(my, RIGI_CF_ENTERING, memory_order_seq_cst);
            while (atomic_load_explicit(&gc_flag, memory_order_seq_cst)
                != RIGI_GC_IDLE)
            {
                gc_event_wait(&gc_alarm);
            }
            continue;
        }
        atomic_store_explicit(my, RIGI_CF_PROCESSING, memory_order_seq_cst);
        atomic_thread_fence(memory_order_seq_cst);
        if (atomic_load_explicit(&gc_flag, memory_order_seq_cst) == RIGI_GC_IDLE)
        {
            return;
        }
        atomic_fetch_add_explicit(&gc_fence_waits, 1, memory_order_relaxed);
        atomic_store_explicit(my, RIGI_CF_ENTERING, memory_order_seq_cst);
        while (atomic_load_explicit(&gc_flag, memory_order_seq_cst)
            != RIGI_GC_IDLE)
        {
            gc_event_wait(&gc_alarm);
        }
    }
}

void rigi_gc_region_fence_exit(void)
{
    /* fence 保证区域内引用图/RC 写入先于 IDLE 发布（§23.3 末两条） */
    atomic_thread_fence(memory_order_seq_cst);
    atomic_store_explicit(gc_my_cflag, RIGI_CF_IDLE, memory_order_seq_cst);
}

/* ------------------------------------------------------------------ */
/* 候选登记与摘除（release / 析构路径钩子）                               */
/* ------------------------------------------------------------------ */

static long gc_object_size(void *object, const RigiTypeSheet *sheet)
{
    if ((sheet->typeFlags & RIGI_TYPE_ARRAY) != 0)
    {
        const RigiTypeSheet *elemSheet =
            *(RigiTypeSheet *const *)((char *)object + 16);
        int32_t len = *(int32_t *)((char *)object + 24);
        long stride = (elemSheet != NULL
                && (elemSheet->typeFlags & RIGI_TYPE_INLINE_VALUE) != 0
                && elemSheet->typeSize > 0)
            ? (long)elemSheet->typeSize
            : 16L;
        return 32L + (long)len * stride;
    }
    return (long)sheet->typeSize;
}

/* 无引用出边的对象不可能成环（§22.3 候选定义的廉价过滤） */
static bool gc_may_cycle(void *object, const RigiTypeSheet *sheet)
{
    uint32_t i;
    if ((sheet->typeFlags & RIGI_TYPE_ARRAY) != 0)
    {
        const RigiTypeSheet *elemSheet =
            *(RigiTypeSheet *const *)((char *)object + 16);
        if (elemSheet == NULL)
        {
            return true; /* 16B 胖槽元素 */
        }
        if ((elemSheet->typeFlags & RIGI_TYPE_INLINE_VALUE) != 0)
        {
            return elemSheet->refMapSize > 0;
        }
        return true; /* 对象/胖引用/盒装元素 */
    }
    for (i = 0; i < sheet->refMapSize; i++)
    {
        if ((sheet->refMap[i] >> RIGI_REFMAP_KIND_SHIFT) == RIGI_REFMAP_FATREF)
        {
            return true;
        }
    }
    return false;
}

static void gc_note_release_locked(void *object, const RigiTypeSheet *sheet)
{
    RigiObjectHeader *h = (RigiObjectHeader *)object;
    long size;
    uint32_t idx;
    long debt;
    int expected;

    if (object == NULL || sheet == NULL)
    {
        return;
    }
    if (gc_trace_on)
    {
        const RigiTypeInfo *ti = sheet->typeInfoId;
        fprintf(stderr, "[gc] rel? %p type=%.*s flags=0x%x refMapSize=%u\n",
            object, ti != NULL ? (int)ti->name.len : 0,
            ti != NULL && ti->name.data != NULL ? ti->name.data : "",
            (unsigned)sheet->typeFlags, (unsigned)sheet->refMapSize);
    }
    if (atomic_load_explicit(&gc_in_collect, memory_order_relaxed) != 0)
    {
        return; /* 收集期间禁止二次登记（公共面已被 fence 冻结，双保险） */
    }
    if (gc_off != 0)
    {
        return; /* 纯 ARC 对照（诊断用） */
    }
    if (gc_color(h) == RIGI_GC_PURPLE)
    {
        return; /* 同一对象一轮候选账本只计一次 */
    }
    if (!gc_may_cycle(object, sheet))
    {
        return;
    }
    size = gc_object_size(object, sheet);
    if (gc_ledger_len >= RIGI_GC_LEDGER_MAX)
    {
        fprintf(stderr, "rigi_rt: 候选账本越上限（%u 条）\n",
            (unsigned)RIGI_GC_LEDGER_MAX);
        abort();
    }
    if (gc_ledger_len == gc_ledger_cap)
    {
        size_t ncap = gc_ledger_cap != 0 ? gc_ledger_cap * 2 : 256;
        GcCandidate *nbuf =
            (GcCandidate *)rigi_track_malloc(ncap * sizeof(GcCandidate));
        if (gc_ledger != NULL)
        {
            memcpy(nbuf, gc_ledger, gc_ledger_len * sizeof(GcCandidate));
            rigi_track_free(gc_ledger);
        }
        gc_ledger = nbuf;
        gc_ledger_cap = ncap;
    }
    idx = (uint32_t)gc_ledger_len;
    gc_ledger[gc_ledger_len].object = object;
    gc_ledger[gc_ledger_len].size = size;
    gc_ledger_len++;
    gc_flags_replace(h, RIGI_GC_COLOR_MASK | (0xFFFFFFu << RIGI_GC_INDEX_SHIFT),
        RIGI_GC_PURPLE | (idx << RIGI_GC_INDEX_SHIFT));
    if (gc_trace_on)
    {
        const RigiTypeInfo *ti = sheet->typeInfoId;
        fprintf(stderr, "[gc] cand+ %p type=%.*s size=%ld idx=%u\n",
            object, ti != NULL ? (int)ti->name.len : 0,
            ti != NULL && ti->name.data != NULL ? ti->name.data : "",
            size, (unsigned)idx);
    }
    debt = atomic_fetch_add_explicit(&gc_debt, size, memory_order_relaxed)
        + size;
    if (debt > gc_threshold && gc_started)
    {
        expected = 0;
        if (atomic_compare_exchange_strong_explicit(&gc_pending, &expected, 1,
                memory_order_acq_rel, memory_order_relaxed))
        {
            gc_event_signal(&gc_wake);
        }
    }
}

void rigi_gc_note_release(void *object, const RigiTypeSheet *sheet)
{
    gc_ledger_acquire();
    gc_note_release_locked(object, sheet);
    gc_ledger_release();
}

uint32_t rigi_gc_release_shared(void *object)
{
    RigiObjectHeader *h = (RigiObjectHeader *)object;
    uint32_t old;
    /* 减量与非零候选登记不可被另一线程的最后一次 release 分开。 */
    gc_ledger_acquire();
    old = atomic_fetch_sub_explicit((_Atomic uint32_t *)&h->rc, 1, memory_order_acq_rel);
    if (old > 1) { gc_note_release_locked(object, h->typeId); }
    gc_ledger_release();
    return old;
}

void rigi_gc_forget_candidate(void *object)
{
    RigiObjectHeader *h = (RigiObjectHeader *)object;
    uint32_t pf;
    uint32_t idx;
    uint32_t last;
    RigiObjectHeader *moved;

    /* 别的线程 swap-remove 会搬动本对象；颜色与索引必须在同一锁内读。 */
    gc_ledger_acquire();
    pf = atomic_load_explicit((_Atomic uint32_t *)&h->packedFlags, memory_order_relaxed);
    if ((pf & RIGI_GC_COLOR_MASK) != RIGI_GC_PURPLE)
    {
        gc_ledger_release();
        return;
    }
    idx = pf >> RIGI_GC_INDEX_SHIFT;
    if (gc_trace_on)
    {
        fprintf(stderr, "[gc] cand- %p idx=%u len=%zu\n",
            object, (unsigned)idx, gc_ledger_len);
    }
    if (idx >= gc_ledger_len || gc_ledger[idx].object != object)
    {
        fprintf(stderr, "rigi_rt: 候选账本索引损坏（%p idx=%u len=%zu）\n",
            object, (unsigned)idx, gc_ledger_len);
        abort();
    }
    atomic_fetch_sub_explicit(&gc_debt, gc_ledger[idx].size,
        memory_order_relaxed);
    last = (uint32_t)gc_ledger_len - 1u;
    if (idx != last)
    {
        gc_ledger[idx] = gc_ledger[last];
        moved = (RigiObjectHeader *)gc_ledger[idx].object;
        gc_flags_replace(moved, 0xFFFFFFu << RIGI_GC_INDEX_SHIFT,
            idx << RIGI_GC_INDEX_SHIFT);
    }
    gc_ledger_len--;
    gc_flags_replace(h, RIGI_GC_COLOR_MASK | (0xFFFFFFu << RIGI_GC_INDEX_SHIFT), 0);
    gc_ledger_release();
}

/* ------------------------------------------------------------------ */
/* §23.2 六步握手 + 单轮收集                                            */
/* ------------------------------------------------------------------ */

static void gc_drain_cflags(void)
{
    uint64_t start = gc_now_ms();
    for (;;)
    {
        bool busy = false;
        uint32_t i;
        for (i = 0; i < RIGI_GC_MAX_CFLAG; i++)
        {
            if (atomic_load_explicit(&gc_cflags[i].s.claimed,
                    memory_order_acquire) != 0
                && atomic_load_explicit(&gc_cflags[i].s.value,
                    memory_order_seq_cst) == RIGI_CF_PROCESSING)
            {
                busy = true;
                break;
            }
        }
        if (!busy)
        {
            return;
        }
        gc_cpu_relax();
        if (gc_now_ms() - start > 30000u)
        {
            fprintf(stderr, "rigi_rt: macroGC 排空 cFlag 超时——region 不变量"
                "破坏（内部含挂起点或可抛出调用）\n");
            abort();
        }
    }
}

static void gc_pass(void)
{
    size_t i;
    long round_debt = 0;
    uint32_t expected = RIGI_GC_IDLE;
    uint64_t started = gc_stats_on ? gc_now_ms() : 0;
    uint64_t marked;
    uint64_t scanned;

    if (gc_trace_on)
    {
        fprintf(stderr, "[gc] pass begin ledger=%zu debt=%ld\n",
            gc_ledger_len,
            atomic_load_explicit(&gc_debt, memory_order_relaxed));
    }

    /* （1）STARTING 独占启动 + GCAlarm 复位；单一 GC 协程，CAS 恒成功 */
    gc_event_reset(&gc_alarm);
    if (!atomic_compare_exchange_strong_explicit(&gc_flag, &expected,
            RIGI_GC_STARTING, memory_order_seq_cst, memory_order_seq_cst))
    {
        fprintf(stderr, "rigi_rt: gcFlag 协议破坏（STARTING 竞争）\n");
        abort();
    }
    /* （2）fence：巡视/清理指令不得重排到 STARTING 发布之前 */
    atomic_thread_fence(memory_order_seq_cst);
    /* （3）自旋巡视全部已注册 cFlag 至无 PROCESSING（region 短且无挂起
     * 点，排空有界；ENTERING/IDLE 不阻止） */
    gc_drain_cflags();
    /* （4）PROCESSING：托管引用图与 ARC 计数对普通 Coroutine 冻结 */
    atomic_store_explicit(&gc_flag, RIGI_GC_PROCESSING, memory_order_seq_cst);

    /* （5）染色与清理（账本锁全程：与 fence 冻结配合，双保险） */
    gc_ledger_acquire();
    atomic_store_explicit(&gc_in_collect, 1, memory_order_relaxed);
    for (i = 0; i < gc_ledger_len; i++)
    {
        round_debt += gc_ledger[i].size;
        gc_mark_gray(gc_ledger[i].object, &gc_trace_stack);
    }
    if (gc_trace_on)
    {
        fprintf(stderr, "[gc] markGray done\n");
    }
    marked = gc_stats_on ? gc_now_ms() : 0;
    for (i = 0; i < gc_ledger_len; i++)
    {
        gc_scan(gc_ledger[i].object, &gc_trace_stack, &gc_whites);
    }
    /* 白色集合先攒：借 gc_whites 当 scan 的 black_st 用完后此处清空复用 */
    gc_whites.len = 0;
    for (i = 0; i < gc_ledger_len; i++)
    {
        if (gc_color((RigiObjectHeader *)gc_ledger[i].object)
            == RIGI_GC_WHITE)
        {
            gc_gather_white(gc_ledger[i].object, &gc_trace_stack, &gc_whites);
        }
    }
    if (gc_trace_on)
    {
        fprintf(stderr, "[gc] scan+gather done whites=%zu\n", gc_whites.len);
    }
    scanned = gc_stats_on ? gc_now_ms() : 0;
    /* 补偿：清理前对存活子引用逐一 rc++ */
    for (i = 0; i < gc_whites.len; i++)
    {
        RigiObjectHeader *wh = (RigiObjectHeader *)gc_whites.items[i];
        gc_trace(gc_whites.items[i], wh->typeId, gc_compensate_child, NULL);
    }
    /* 统一清理（含 §25 检查挂点）两段式：先全量走边（dispose 检查 +
     * 子引用释放），再统一放本体。白色同胞边的跳过判定读邻居头颜色，
     * 逐条 teardown 即放会把已释放内存留给后续同胞的颜色检查——堆块
     * 被 CRT 回收/复用后读到野值（UAF），10 万级白色批必崩（MW12c
     * stress 暴露）；两段式保证走边期全部白色本体仍存活 */
    for (i = 0; i < gc_whites.len; i++)
    {
        RigiObjectHeader *wh = (RigiObjectHeader *)gc_whites.items[i];
        if (gc_trace_on)
        {
            const RigiTypeInfo *ti = wh->typeId != NULL ? wh->typeId->typeInfoId : NULL;
            if (ti != NULL)
            {
                fprintf(stderr, "[gc] teardown %p type=%.*s\n",
                    gc_whites.items[i], (int)ti->name.len,
                    ti->name.data != NULL ? ti->name.data : "");
            }
            else
            {
                fprintf(stderr, "[gc] teardown %p type=<anon>\n",
                    gc_whites.items[i]);
            }
        }
        gc_teardown_ex(gc_whites.items[i], wh->typeId, 0);
    }
    for (i = 0; i < gc_whites.len; i++)
    {
        rigi_track_free(gc_whites.items[i]);
    }
    gc_whites.len = 0;
    atomic_store_explicit(&gc_in_collect, 0, memory_order_relaxed);

    /* 账本整轮出清，债务按本轮处理量扣除（保留新积累部分） */
    gc_ledger_len = 0;
    {
        long cur = atomic_load_explicit(&gc_debt, memory_order_relaxed);
        long sub = round_debt < cur ? round_debt : cur;
        atomic_fetch_sub_explicit(&gc_debt, sub, memory_order_relaxed);
    }
    gc_ledger_release();

    /* 结束前 fence：清理与元数据写入不得重排到 IDLE 发布之后 */
    atomic_thread_fence(memory_order_seq_cst);
    /* （6）发布 IDLE + 触发 GCAlarm 唤醒全部 ENTERING 等待者 */
    atomic_store_explicit(&gc_flag, RIGI_GC_IDLE, memory_order_seq_cst);
    gc_event_signal(&gc_alarm);
    if (gc_stats_on)
    {
        uint64_t finished = gc_now_ms();
        gc_stats_passes++;
        if (finished - started >= 100 || gc_stats_passes % 100 == 0)
            fprintf(stderr, "[gc-stats] passes=%llu gray-ms=%llu scan-ms=%llu teardown-ms=%llu slots=%llu skipped=%llu\n",
                (unsigned long long)gc_stats_passes, (unsigned long long)(marked - started),
                (unsigned long long)(scanned - marked), (unsigned long long)(finished - scanned),
                (unsigned long long)gc_stats_slots, (unsigned long long)gc_stats_skipped);
    }
}

/* ------------------------------------------------------------------ */
/* GC 协程承载线程与生命周期                                             */
/* ------------------------------------------------------------------ */

static void gc_thread_main(void)
{
    for (;;)
    {
        gc_event_wait(&gc_wake);
        gc_event_reset(&gc_wake);
        atomic_store_explicit(&gc_pending, 0, memory_order_relaxed);
        if (gc_trace_on)
        {
            fprintf(stderr, "[gc] thread woke\n");
        }
        if (gc_off != 0)
        {
            if (atomic_load_explicit(&gc_stop, memory_order_acquire) != 0)
            {
                return;
            }
            continue;
        }
        /* 唤醒后处理候选：正常路径仅在债务仍超阈值时续轮（§23.2 末条
         * 仍超门槛立即再来一轮）；stop 时也必须把账本收干才许退出——
         * 关闭信号若在连续 pass 期间到达（如 main 退出批量登记洪峰撞上
         * 某轮 pass 收尾），只查债务会放走「上一轮收干后、while 复查前」
         * 新登记的尾批候选（实测 AOT 套件 16 路并发下泄漏 ~25%） */
        for (;;)
        {
            gc_pass();
            if (atomic_load_explicit(&gc_stop, memory_order_acquire) != 0)
            {
                int remaining;
                gc_ledger_acquire();
                remaining = gc_ledger_len != 0;
                gc_ledger_release();
                if (remaining) { continue; }
                return;
            }
            /* 正常运行按债务阈值续轮，不能追赶 mutator 新添的单条候选
             * 反复冻结整图。stop 在此后到达会 signal wake，外层再收终轮。 */
            if (atomic_load_explicit(&gc_debt, memory_order_relaxed) <= gc_threshold)
            {
                break;
            }
        }
    }
}

#ifdef _WIN32
static DWORD WINAPI gc_thread_trampoline(LPVOID arg)
{
    (void)arg;
    gc_thread_main();
    return 0;
}
#else
static void *gc_thread_trampoline(void *arg)
{
    (void)arg;
    gc_thread_main();
    return NULL;
}
#endif

void rigi_gc_init(void)
{
    const char *env;
    if (gc_started)
    {
        return;
    }
    env = getenv("RIGI_RT_GC_THRESHOLD");
    if (env != NULL && env[0] != '\0')
    {
        long v = strtol(env, NULL, 10);
        if (v > 0)
        {
            gc_threshold = v;
        }
    }
    env = getenv("RIGI_RT_GC_OFF");
    if (env != NULL && env[0] == '1')
    {
        gc_off = 1;
    }
    env = getenv("RIGI_RT_GC_TRACE");
    if (env != NULL && env[0] == '1')
    {
        gc_trace_on = 1;
    }
    env = getenv("RIGI_RT_GC_STATS");
    gc_stats_on = env != NULL && env[0] == '1';
    gc_event_init(&gc_wake);
    gc_event_init(&gc_alarm);
#ifdef _WIN32
    gc_thread = CreateThread(NULL, 0, gc_thread_trampoline, NULL, 0, NULL);
    if (gc_thread == NULL)
    {
        fprintf(stderr, "rigi_rt: GC 线程创建失败（环境耗尽）\n");
        abort();
    }
#else
    if (pthread_create(&gc_thread, NULL, gc_thread_trampoline, NULL) != 0)
    {
        fprintf(stderr, "rigi_rt: GC 线程创建失败（环境耗尽）\n");
        abort();
    }
    gc_thread_valid = 1;
#endif
    gc_started = 1;
}

void rigi_gc_shutdown(void)
{
    if (!gc_started)
    {
        return;
    }
    if (gc_trace_on)
    {
        fprintf(stderr, "[gc] shutdown begin (fence_waits=%ld)\n",
            atomic_load_explicit(&gc_fence_waits, memory_order_relaxed));
    }
    /* 请求终轮并唤醒（终轮由 gc_thread_main 的 stop 分支前 gc_pass 承担：
     * 全局槽释放产生的末批候选在此兜底收集，保证 memtrack 零泄漏口径） */
    atomic_store_explicit(&gc_stop, 1, memory_order_release);
    gc_event_signal(&gc_wake);
#ifdef _WIN32
    WaitForSingleObject(gc_thread, INFINITE);
    CloseHandle(gc_thread);
    gc_thread = NULL;
#else
    pthread_join(gc_thread, NULL);
    gc_thread_valid = 0;
#endif
    gc_started = 0;
    /* GC 自持资源全部走台账配对释放（shutdown 先于 mem_report 执行） */
    gc_vec_destroy(&gc_trace_stack);
    gc_vec_destroy(&gc_whites);
    rigi_track_free(gc_ledger);
    gc_ledger = NULL;
    gc_ledger_len = 0;
    gc_ledger_cap = 0;
    gc_event_destroy(&gc_wake);
    gc_event_destroy(&gc_alarm);
}
