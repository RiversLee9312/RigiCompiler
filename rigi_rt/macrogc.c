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
 *
 * Phase 1.3 候选强引用协议（终态释放免账本锁）：
 *   - 核心不变量：PURPLE 在册 ⟹ rc = U + 1（账本持有 +1 强引用）；
 *     非在册 ⟹ rc = U。登记（pin-before-sub）：may-cycle 对象的 release
 *     先 fetch_add(rc) 取 pin 再减用户引用，锁内 append 成功则 pin 无偿
 *     转为账本引用（rc 不再变），未登记则归还 pin——pin 在登记全程驻留
 *     rc ⟹ 登记窗口对象必活，「登记到已 free 指针」窗口封闭。
 *   - 终态判定免锁：非在册对象 old==2 蕴含 U前=1（本线程独占）∧ 无在飞
 *     pin ∧ 账本引用不在（任一在即 old ≥ 3）⟹ 归还 pin 后 rc 归 0 且
 *     他人无引用可凭介入 ⟹ 终态析构与账本零交互。在册对象（PURPLE 快
 *     路径）old==2 ⟺ 最后一次用户引用释放：进锁摘除候选 + 归还账本
 *     引用后立即析构——确定性释放语义（§22.2）不变，账本锁只余此罕见
 *     路径与登记路径；rigi_destruct 头部的无条件 forget 随之移除
 *     （非在册析构占绝对多数，对它们 forget 是 provably no-op）。
 *   - pass 开始时（锁内、fence 冻结后、markGray 前）逐候选归还账本 +1，
 *     此后染色期 rc == 真实外部引用数，scan 的 rc>0 判活锚点与补偿/
 *     teardown 逻辑与旧协议完全同构；活者染色被覆写为非 PURPLE、收尾
 *     len=0 出册，不变量闭环。
 */
#ifdef _WIN32
#define _CRT_SECURE_NO_WARNINGS
#endif
#include "macrogc.h"
#include "stringfmt.h"
#include "shell.h"
#include "cohandle.h" /* Phase 3d-1：per-协程局部账本槽位 + TLS 协程上下文 */

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
 * 账本锁：只保护账本结构操作本身（登记 append / 摘除 swap-remove / pass
 * 全程持有）——release 路径在 region 内执行，但 region 只 fence 不跨线程
 * 互斥（多线程可同时处于 PROCESSING），shared 对象的并发登记会并发
 * append。Phase 1.3 起 release 的减量与终态判定不再进锁（pin-before-sub
 * 协议保证「登记到已 free 指针」窗口封闭，见 rigi_gc_release_shared），
 * 只有走登记路径的 release 才取锁（短临界区；pass 期间图已冻结，锁无
 * 争用）。 */
typedef struct
{
    void *object;
    int64_t size;
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
static _Atomic int64_t gc_debt = 0;
static int64_t gc_threshold = INT64_C(1) << 20; /* 默认 1 MiB，RIGI_RT_GC_THRESHOLD 覆盖 */
static _Atomic int gc_pending = 0;
static _Atomic int gc_stop = 0;
static _Atomic int gc_closed = 0; /* shutdown 完成后候选账本不可复活。 */
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

    /* 3b-β 壳锚代理边（设计补全）：capability 的 target 引用成分物理
     * 在壳（壳锚，count 会计）——refMap=0 断开了 GC 图的 capability→
     * target 边，「target 自指 Handle」的环会因壳锚成分无人扣减而恒
     * 黑化（保守泄漏）。本特判把壳锚作为 capability 的代理图边接回
     * 三阶段协议：markGray 扣 target 的壳锚成分、scanBlack/补偿原路
     * 加回、白色批清理经 capability 壳分支的 raw_release 配对（白色
     * 同胞边整条跳过）——与 β 前 refMap[0] 边的图形/计数语义一致。 */
    if (rigi_handle_is_capability(object, sheet))
    {
        uint64_t target_type = 0;
        uint64_t target_payload = 0;
        void *shell = rigi_shell_of_capability(object);
        rigi_shell_target_of(shell, &target_type, &target_payload);
        gc_trace_fat(target_type, target_payload, fn, ctx);
        return;
    }

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

    /* 3b-β：capability 壳析构（GC 冻结期分流挂点，裁定 #8）——不投
     * 消息（fence 冻结期公共 release 面/Dispatcher 自锁），就地原始
     * 清理：壳计数减一 + 摘表 + target 经 raw_release（白色同胞边
     * 整条跳过，与白色批清理协议一致）拆解。§25 dispose 检查对本
     * 类型是 no-op（capability 非 DISPOSABLE），无需再走下方通用段 */
    if (rigi_handle_is_capability(object, sheet))
    {
        rigi_shell_gc_release_capability(object, rigi_gc_raw_release_fat);
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

/* 3b-β：原始胖引用释放的导出包装（壳冻结期清理回调 rigi_shell_gc_
 * release_capability 用）——语义 = gc_teardown_fat：白色同胞边整条
 * 跳过 + rc 原始减，不走公共 release 面（fence 冻结期会自锁） */
void rigi_gc_raw_release_fat(uint64_t type_id, uint64_t payload)
{
    gc_teardown_fat(type_id, payload);
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
        /* §23.3 双重检查（Phase 1.1 fence 快路径）：
         * 先查 gcFlag，置 PROCESSING 后复查；任一时刻见非 IDLE 即退
         * ENTERING 并阻塞等 GCAlarm，醒后完整重检。
         *
         * 第一次预检（快路径初筛）：acquire load——x86_64 TSO 的普通
         * mov 天然具备 acquire 序，故编译为普通 mov。预检只是廉价初筛，
         * 即使读到过期 IDLE 也由下面的 Dekker 握手兜底，因此不再需要
         * 旧实现的预检前 seq_cst fence（mfence）——这正是本阶段砍掉的
         * 串行化开销之一（每次 release 少 1×mfence）。 */
        if (atomic_load_explicit(&gc_flag, memory_order_acquire)
            != RIGI_GC_IDLE)
        {
            /* 慢路径：语义与旧实现一致（seq_cst 访问 + 阻塞等 GCAlarm） */
            atomic_fetch_add_explicit(&gc_fence_waits, 1, memory_order_relaxed);
            atomic_store_explicit(my, RIGI_CF_ENTERING, memory_order_seq_cst);
            while (atomic_load_explicit(&gc_flag, memory_order_seq_cst)
                != RIGI_GC_IDLE)
            {
                gc_event_wait(&gc_alarm);
            }
            continue;
        }
        /* 发布 PROCESSING：seq_cst RMW。x86_64 上 clang/gcc 编译为
         * xchg（locked），locked 指令自带全栅栏（含 StoreLoad），故不再
         * 补 mfence——旧实现「seq_cst store + seq_cst fence」在 x86 上
         * 本就是 xchg + mfence 两条，这里显式化为一次原子交换，砍掉
         * 第二处 mfence。 */
        atomic_exchange_explicit(my, RIGI_CF_PROCESSING, memory_order_seq_cst);
        /* Dekker 二次检查（正确性锚点，不可砍）：与 GC 侧「发布
         * gcFlag=PROCESSING → seq_cst 重读 cFlag」（gc_pass 步骤 4 + 5）
         * 对称——双方各自「先写自己的标志、再读对方的标志」，C11 层面
         * 靠两侧均为 seq_cst 操作、同入 seq_cst 总序 S，保证至少一方
         * 读到对方写入（relaxed load 不入 S，理论上可读到过期值，
         * 故此 load 必须 seq_cst）；硬件层面 xchg 排空本核 store buffer
         * 且 TSO 多副本原子，后续 load 不会读到 xchg 之前的旧值——
         * x86_64 上该 seq_cst load 同样只是普通 mov，零额外开销。
         * 重读仍见 IDLE 才允许进入 region。 */
        if (atomic_load_explicit(&gc_flag, memory_order_seq_cst)
            == RIGI_GC_IDLE)
        {
            return;
        }
        /* 与 GC 的握手竞态失败：退 ENTERING 阻塞等 GCAlarm，醒后完整重检 */
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
    /* 发布 IDLE：release store。release 语义 = StoreStore + LoadStore，
     * 同时约束编译器（区域内引用图/RC 写入不得重排到本 store 之后，
     * §23.3 末两条）与硬件——x86_64 TSO 的普通 mov 天然满足两者，
     * 故编译为普通 mov，砍掉旧实现的 mfence（第三处）与 xchg（store
     * 原本也编译为 xchg）；GC 的 drain 用 seq_cst load 读到 IDLE 即与
     * 本 release store 构成 synchronizes-with，区域内写入对 GC 可见。 */
    atomic_store_explicit(gc_my_cflag, RIGI_CF_IDLE, memory_order_release);
}

/* ------------------------------------------------------------------ */
/* 候选登记与摘除（release / 析构路径钩子）                               */
/* ------------------------------------------------------------------ */

static int64_t gc_object_size(void *object, const RigiTypeSheet *sheet)
{
    if ((sheet->typeFlags & RIGI_TYPE_ARRAY) != 0)
    {
        const RigiTypeSheet *elemSheet =
            *(RigiTypeSheet *const *)((char *)object + 16);
        int32_t len = *(int32_t *)((char *)object + 24);
        int64_t stride = (elemSheet != NULL
                && (elemSheet->typeFlags & RIGI_TYPE_INLINE_VALUE) != 0
                && elemSheet->typeSize > 0)
            ? (int64_t)elemSheet->typeSize
            : 16L;
        return INT64_C(32) + (int64_t)len * stride;
    }
    return (int64_t)sheet->typeSize;
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

/* 锁内登记段（gc_ledger_lock 已持有）：closed / 收集期 / PURPLE 复检 +
 * append + 置 PURPLE|idx + 债务累计 + 阈值唤醒。
 * 返回 1 = 已登记（调用方驻留 rc 的 pin 自此转为账本引用）；0 = 未登记
 * （先到者已置 PURPLE / 收集期禁登记——调用方须自行归还自己的 pin）。
 * may_cycle、gc_off 与 NULL 检查在锁外完成（只读 TypeSheet 静态元数据
 * 与启动期常量）；锁内只做必须与 append 原子的复检。 */
static int gc_note_release_locked(void *object, const RigiTypeSheet *sheet)
{
    RigiObjectHeader *h = (RigiObjectHeader *)object;
    int64_t size;
    uint32_t idx;
    int64_t debt;
    int expected;

    if (atomic_load_explicit(&gc_closed, memory_order_relaxed))
    {
        fprintf(stderr, "rigi_rt: GC shutdown 后登记候选（运行时生命周期错误）\n");
        abort();
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
        return 0; /* 收集期间禁止二次登记（公共面已被 fence 冻结，双保险） */
    }
    if (gc_color(h) == RIGI_GC_PURPLE)
    {
        return 0; /* 锁内复检：并发双检的失败方在此退出，一轮只计一次 */
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
        fprintf(stderr, "[gc] cand+ %p type=%.*s size=%lld idx=%u\n",
            object, ti != NULL ? (int)ti->name.len : 0,
            ti != NULL && ti->name.data != NULL ? ti->name.data : "",
            (long long)size, (unsigned)idx);
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
    return 1;
}

/* 锁外判定 + 锁内登记。返回 1 = 本次调用完成登记；0 = 无需/未能登记
 * （空参 / gc_off / 锁外 PURPLE 快检命中 / 叶类型 / 锁内复检退出）。
 * may_cycle 只读 TypeSheet 静态元数据、gc_off 为启动期常量，均可在锁外
 * 判定；PURPLE 锁外快检只是免锁初筛，锁内复检兜底并发双检。
 * 注意：本函数不管理 rc——「账本持 +1」的引用由调用方驻留的 pin 转化
 * （rigi_gc_release_shared）或由调用方语境自带（直接登记入口）。 */
int rigi_gc_note_release(void *object, const RigiTypeSheet *sheet)
{
    RigiObjectHeader *h = (RigiObjectHeader *)object;
    int registered;
    if (object == NULL || sheet == NULL)
    {
        return 0;
    }
    if (gc_off != 0)
    {
        return 0; /* 纯 ARC 对照（诊断用） */
    }
    if (gc_color(h) == RIGI_GC_PURPLE)
    {
        return 0; /* 同一对象一轮候选账本只计一次 */
    }
    if (!gc_may_cycle(object, sheet))
    {
        return 0; /* 无引用出边不可能成环（§22.3 廉价过滤） */
    }
    gc_ledger_acquire();
    registered = gc_note_release_locked(object, sheet);
    gc_ledger_release();
    return registered;
}

uint32_t rigi_gc_release_shared(void *object)
{
    RigiObjectHeader *h = (RigiObjectHeader *)object;
    uint32_t old;
    /* Phase 1.3 pin-before-sub 协议（正确性推导见文件头「候选强引用
     * 协议」节）。快路径 1：在册候选——账本持 +1 ⟹ rc = U + 1（本线程
     * 的用户引用是 U 的一部分），plain sub 绝不把 rc 减穿 0。读色允许
     * 「过期」：PURPLE→非 PURPLE 只发生在 pass 期，与 mutator region
     * 握手互斥且 fence 保证 IDLE 发布先行于 mutator 重入 ⟹ mutator 读到
     * PURPLE 时对象必真在册必活。
     * old==2 ⟺ U前=1 且账本引用在（1+1 恰好用满预算）⟹ 最后一次用户
     * 引用释放：进锁摘除候选并归还账本引用后立即终态析构——确定性
     * 释放语义（§22.2）在此保持，取账本锁仅此罕见路径（对象从多引用
     * 走到最后一个的死亡时刻）；old ≥ 3 非终态免锁返回。 */
    if (gc_color(h) == RIGI_GC_PURPLE)
    {
        old = atomic_fetch_sub_explicit(
            (_Atomic uint32_t *)&h->rc, 1, memory_order_acq_rel);
        if (old == 2)
        {
            /* U=1 时本线程独占（他人无引用可凭介入/再 pin），锁内复检
             * 必见在册（pass 被 region 互斥、并发终态被 U=1 独占排除）；
             * forget 返回 0 的防御分支理论上不可达，此时不做账本引用
             * 归还（对象不在册 ⟹ 无账本引用可还）。 */
            if (rigi_gc_forget_candidate(object))
            {
                atomic_fetch_sub_explicit(
                    (_Atomic uint32_t *)&h->rc, 1, memory_order_acq_rel);
            }
            return 1; /* 通知调用方终态析构（确定性，同旧协议） */
        }
        return old;
    }
    /* 快路径 2：叶类型（无对象出边）永不登记、永不 PURPLE，其终态
     * 判定天然免锁。may_cycle 读 sheet 静态元数据，锁外安全。 */
    if (!gc_may_cycle(object, h->typeId))
    {
        return atomic_fetch_sub_explicit(
            (_Atomic uint32_t *)&h->rc, 1, memory_order_acq_rel);
    }
    /* may-cycle 且未见 PURPLE：完整 pin 协议。① pin 先驻留 rc（≥1），
     * 覆盖「减量 → 登记」全程——取代旧协议「减量与登记同锁」的防竞态
     * （旧注释：减量不可被另一线程的最后一次 release 分开）。 */
    atomic_fetch_add_explicit(
        (_Atomic uint32_t *)&h->rc, 1, memory_order_acq_rel);
    /* ② 释放用户引用 */
    old = atomic_fetch_sub_explicit(
        (_Atomic uint32_t *)&h->rc, 1, memory_order_acq_rel);
    if (old == 2)
    {
        /* old 含本线程的 pin：2 = U前(1) + pin(1) 恰好用满预算 ⟹
         * U前=1（本线程独占）∧ 无在飞 pin ∧ 账本引用不在（任一存在
         * 即 old ≥ 3）⟹ 非在册。归还 pin 后 rc 归 0，其他线程不持有
         * 引用、无法获得引用介入 ⟹ 全程无锁的终态判定安全。 */
        atomic_fetch_sub_explicit(
            (_Atomic uint32_t *)&h->rc, 1, memory_order_acq_rel);
        return 1; /* 通知调用方终态析构 */
    }
    /* old > 1：尝试把 pin 无偿转为账本引用（登记成功则 rc 不再变化，
     * 对象进入「在册 ⟹ rc = U + 1」态）；未登记（并发先到者已置
     * PURPLE / 收集期禁登记）则归还多余 pin，对象保持 rc = U。 */
    if (!rigi_gc_note_release(object, h->typeId))
    {
        atomic_fetch_sub_explicit(
            (_Atomic uint32_t *)&h->rc, 1, memory_order_acq_rel);
    }
    return old;
}

/* 候选摘除（swap-remove + 债务回减），返回 1 = 本次摘除、0 = 本来就
 * 不在册。Phase 1.3 起的唯一 mutator 调用方是 rigi_gc_release_shared 的
 * 在册终态路径（PURPLE 快路径 old==2：U前=1 且账本 +1 在——摘除后归还
 * 账本引用并立即终态析构，确定性释放语义与旧协议一致）；rigi_destruct
 * 不再无条件调用（非在册析构占绝对多数，forget 对它是 provably no-op，
 * 白取一遍锁）。亦保留为测试/诊断入口（NativeE2E gc_debt 计量例直接
 * 驱动 swap-remove 与债务回减的正确性）。 */
int rigi_gc_forget_candidate(void *object)
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
        return 0;
    }
    idx = pf >> RIGI_GC_INDEX_SHIFT;
    if (gc_trace_on)
    {
        fprintf(stderr, "[gc] cand- %p idx=%u len=%zu\n",
            object, (unsigned)idx, gc_ledger_len);
    }
    if (idx >= gc_ledger_len || gc_ledger[idx].object != object)
    {
        const RigiTypeInfo *dbg_ti = h->typeId != NULL ? h->typeId->typeInfoId : NULL;
        fprintf(stderr, "rigi_rt: 候选账本索引损坏（%p idx=%u len=%zu pf=%x type=%.*s slot=%p）\n",
            object, (unsigned)idx, gc_ledger_len, pf,
            dbg_ti != NULL ? (int)dbg_ti->name.len : 0,
            dbg_ti != NULL && dbg_ti->name.data != NULL ? dbg_ti->name.data : "?",
            idx < gc_ledger_len ? gc_ledger[idx].object : NULL);
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
    return 1;
}

/* Phase 3a：local → shared 会计提升原语（冷路径，契约见 macrogc.h；
 * 3a 无调用方——纯埋点。fetch_or 单方向置位，与 rigi_mark_disposed
 * 同形态，天然不触碰颜色/索引/disposed 位；release 序支撑 3b/3c 的
 * 「翻位 → 通知他线程触碰」发布链）。 */
void rigi_gc_promote_shared_accounting(void *object)
{
    if (object != NULL)
    {
        atomic_fetch_or_explicit(
            (_Atomic uint32_t *)&((RigiObjectHeader *)object)->packedFlags,
            RIGI_PF_SHARED_ACCOUNTING, memory_order_release);
    }
}

/* ------------------------------------------------------------------ */
/* Phase 3c：子图 shared 会计提升 walker（共用面）                      */
/* shell.c（属主终止 teardown / 死属主兜底）与 failreg.c（失败发布点）  */
/* 共用。从 3b-α/β 的 shell.c 本地 walker 提取，两处修正：              */
/*   ① 对象 refMap 走查起点改为 sizeof(RigiObjectHeader)——refMap hops  */
/*      以对象头为基准（RefMapBuilder.BuildRefMap scanStart=16；        */
/*      gc_trace/rigi_destruct 对象路径同口径），旧实现从 0 起步为      */
/*      off-by-16B（漏翻全部引用槽 + 误读前槽当胖引用）；               */
/*   ② 补数组元素表分支（数组 sheet 的 refMap 为空，旧实现遇数组即      */
/*      止步，元素子图整体漏翻）。                                      */
/* ------------------------------------------------------------------ */

/* 工作项：待展开的胖引用（tag2 对象或 tag1 堆值盒）。 */
typedef struct GcPromoFat
{
    uint64_t type_id;
    uint64_t payload;
} GcPromoFat;

typedef struct
{
    GcPromoFat *items;
    size_t len;
    size_t cap;
} GcPromoStack;

/* visited 集合（对象指针线性扫）：环断 + 跨根共享子图去重。不依赖
 * RIGI_PF_SHARED_ACCOUNTING 位去重——位已置 ≠ 子图已展开（同批其他根
 * 可能尚未经过本对象），独立集合语义自洽。 */
typedef struct
{
    void **items;
    size_t len;
    size_t cap;
} GcPromoSeen;

typedef struct
{
    GcPromoStack stack;
    GcPromoSeen seen;
} GcPromo;

static void gc_promo_stack_push(GcPromoStack *st, uint64_t type_id,
                                uint64_t payload)
{
    if (st->len == st->cap)
    {
        size_t ncap = st->cap != 0 ? st->cap * 2 : 64;
        GcPromoFat *nbuf = (GcPromoFat *)rigi_track_malloc(
            ncap * sizeof(GcPromoFat));
        if (st->items != NULL)
        {
            memcpy(nbuf, st->items, st->len * sizeof(GcPromoFat));
            rigi_track_free(st->items);
        }
        st->items = nbuf;
        st->cap = ncap;
    }
    st->items[st->len].type_id = type_id;
    st->items[st->len].payload = payload;
    st->len++;
}

static int gc_promo_seen_has(const GcPromoSeen *seen, const void *obj)
{
    size_t i;
    for (i = 0; i < seen->len; i++)
    {
        if (seen->items[i] == obj) return 1;
    }
    return 0;
}

static void gc_promo_seen_add(GcPromoSeen *seen, void *obj)
{
    if (seen->len == seen->cap)
    {
        size_t ncap = seen->cap != 0 ? seen->cap * 2 : 64;
        void **nbuf = (void **)rigi_track_malloc(ncap * sizeof(void *));
        if (seen->items != NULL)
        {
            memcpy(nbuf, seen->items, seen->len * sizeof(void *));
            rigi_track_free(seen->items);
        }
        seen->items = nbuf;
        seen->cap = ncap;
    }
    seen->items[seen->len++] = obj;
}

static void gc_promo_init(GcPromo *p)
{
    p->stack.items = NULL;
    p->stack.len = 0;
    p->stack.cap = 0;
    p->seen.items = NULL;
    p->seen.len = 0;
    p->seen.cap = 0;
}

static void gc_promo_free(GcPromo *p)
{
    if (p->stack.items != NULL) rigi_track_free(p->stack.items);
    if (p->seen.items != NULL) rigi_track_free(p->seen.items);
    gc_promo_init(p);
}

/* refMap 驱动的子引用展开：非 STRING 槽 → 子胖引用入栈；STRING 槽无
 * 对象出边跳过。start = 槽基准偏移（对象 = sizeof(RigiObjectHeader)，
 * 堆值盒/内联值元素 = 0）——hops 以 start 为基准折算（RefMapBuilder
 * scanStart 口径），与 gc_trace(_block) / rigi_destruct / rigi_value_walk
 * 的走查严格同构（改结构须多侧同步）。 */
static void gc_promo_refmap(GcPromo *p, const RigiTypeSheet *sheet,
                            const void *block, size_t start)
{
    const char *base = (const char *)block;
    size_t cursor = start;
    uint32_t i;
    if (sheet == NULL || sheet->refMap == NULL)
    {
        return;
    }
    for (i = 0; i < sheet->refMapSize; i++)
    {
        uint16_t entry = sheet->refMap[i];
        uint16_t kind = (uint16_t)(entry >> RIGI_REFMAP_KIND_SHIFT);
        uint16_t hops = (uint16_t)(entry & RIGI_REFMAP_HOP_MASK);
        cursor += (size_t)hops * 16u;
        if (kind != RIGI_REFMAP_STRING)
        {
            uint64_t tid = *(const uint64_t *)(base + cursor);
            uint64_t pl = *(const uint64_t *)(base + cursor + 8);
            gc_promo_stack_push(&p->stack, tid, pl);
        }
        cursor += 16u;
    }
}

/* 数组元素表展开（同构 gc_trace / rigi_destruct 的数组分支；改结构须
 * 三侧同步）。数组本体已按 RIGI_TYPE_ARRAY 恒 shared 会计（分配点置
 * 位，promote 幂等）；展开只为触及元素子图中的 local 对象。 */
static void gc_promo_array(GcPromo *p, const void *object)
{
    const RigiTypeSheet *elemSheet =
        *(const RigiTypeSheet *const *)((const char *)object + 16);
    int32_t len = *(const int32_t *)((const char *)object + 24);
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
        const void *elem = elems + (size_t)idx * (size_t)stride;
        if (elemSheet == NULL)
        {
            /* new List<K> 开放构造未写入隐藏 T：元素仍按 16B 胖槽 */
            uint64_t tid = *(const uint64_t *)elem;
            uint64_t pl = *(const uint64_t *)((const char *)elem + 8);
            gc_promo_stack_push(&p->stack, tid, pl);
            continue;
        }
        if ((elemSheet->typeFlags & RIGI_TYPE_STRING) != 0)
        {
            /* 泛型 List<String> 对 String 仍按胖引用写入（tag1 盒）；
             * 真 String ABI 槽无对象出边 */
            uint64_t tid = *(const uint64_t *)elem;
            uint64_t tag = tid >> RIGI_TAG_SHIFT;
            if (tag == RIGI_TAG_HEAP_VALUE || tag == RIGI_TAG_OBJECT)
            {
                uint64_t pl = *(const uint64_t *)((const char *)elem + 8);
                gc_promo_stack_push(&p->stack, tid, pl);
            }
        }
        else if ((elemSheet->typeFlags & RIGI_TYPE_INLINE_VALUE) != 0)
        {
            /* 内联值元素：refMap 以元素首为基准展开内嵌引用 */
            gc_promo_refmap(p, elemSheet, elem, 0);
        }
        else
        {
            uint64_t tid = *(const uint64_t *)elem;
            uint64_t pl = *(const uint64_t *)((const char *)elem + 8);
            gc_promo_stack_push(&p->stack, tid, pl);
        }
    }
}

/* Phase 3d-1：promote 翻位配套的局部账本摘除（定义见账本内核段） */
void rigi_gc_local_ledger_detach(void *object);

/* 机制让步对象识别（$mw. 前缀；定义见账本内核段） */
static bool lgc_is_mechanism_object(const RigiTypeSheet *sheet);

void rigi_gc_promote_subgraph(uint64_t type_id, uint64_t payload)
{
    GcPromo promo;
    gc_promo_init(&promo);
    gc_promo_stack_push(&promo.stack, type_id, payload);
    while (promo.stack.len > 0)
    {
        GcPromoFat w = promo.stack.items[--promo.stack.len];
        uint64_t tag;
        if (w.payload == 0)
        {
            continue; /* 空引用 */
        }
        tag = w.type_id >> RIGI_TAG_SHIFT;
        if (tag == RIGI_TAG_OBJECT)
        {
            void *obj = (void *)(uintptr_t)w.payload;
            const RigiTypeSheet *sheet;
            uint32_t pf;
            if (gc_promo_seen_has(&promo.seen, obj))
            {
                continue; /* 环断 + 跨根共享子图去重 */
            }
            gc_promo_seen_add(&promo.seen, obj);
            /* Phase 3d-1：位未置才翻位 + 摘局部账本条目。已 shared 会计
             * 的对象整只跳过——翻位幂等无意义，且其 PURPLE 若在则属
             * 全局候选账本（shared release 路径登记），局部摘除逻辑
             * 不得触碰。位读 relaxed 一次 load：promote 单调置位，读到
             * 旧值 0 ⟹ 对象此刻仍只被本语境（属主/死属主单点）触碰。 */
            pf = atomic_load_explicit(
                (_Atomic uint32_t *)&((RigiObjectHeader *)obj)->packedFlags,
                memory_order_relaxed);
            if ((pf & RIGI_PF_SHARED_ACCOUNTING) == 0)
            {
                sheet = ((const RigiObjectHeader *)obj)->typeId;
                if (sheet == NULL || !lgc_is_mechanism_object(sheet))
                {
                    /* 先摘局部账本条目（plain 归还 +1——此刻对象仍
                     * local 会计、无并发触碰），再翻位。顺序不可颠倒：
                     * detach 内部有 SHARED 防御（shared 会计对象的
                     * PURPLE 属全局账本，不得触碰），若先翻位，刚置的
                     * SHARED 位会让 detach 自我拦截，条目残留——对象
                     * 此后按 shared 路径终态释放时会拿局部 idx 查全局
                     * 账本（索引损坏 abort）或悬垂泄漏。 */
                    rigi_gc_local_ledger_detach(obj);
                    rigi_gc_promote_shared_accounting(obj);
                }
                /* 机制对象（$mw. 前缀）跳过翻位与局部账本摘除：恒守
                 * local 会计与兜底账本归属（段首注释），否则出现
                 * 「SHARED + 局部 PURPLE」混合态引发全局账本索引错
                 * 乱。子图展开照常——其持有的 local 子对象仍需翻位。 */
            }
            sheet = ((const RigiObjectHeader *)obj)->typeId;
            if (sheet != NULL && (sheet->typeFlags & RIGI_TYPE_ARRAY) != 0)
            {
                gc_promo_array(&promo, obj);
            }
            else
            {
                gc_promo_refmap(&promo, sheet, obj,
                    sizeof(RigiObjectHeader));
            }
        }
        else if (tag == RIGI_TAG_HEAP_VALUE)
        {
            const RigiTypeSheet *sheet = (const RigiTypeSheet *)(uintptr_t)
                (w.type_id & RIGI_SHEET_MASK);
            /* tag1 盒本体非 GC 对象不 promote（gc_trace 同口径；盒不
             * 构成环成员），只展开其内容引用 */
            gc_promo_refmap(&promo, sheet, (const void *)(uintptr_t)
                w.payload, 0);
        }
        /* tag0（内联值）：无堆出边 */
    }
    gc_promo_free(&promo);
}

/* ------------------------------------------------------------------ */
/* Phase 3d-1：per-协程 local 候选账本 + 属主协作收集                    */
/* ------------------------------------------------------------------ */
/* split-heap 收益兑现。与全局 macroGC（gc_pass）的关系：               */
/*   - 图不相交：全局 pass 只收 shared 会计闭包（shared 不引用 local，  */
/*     SYNTAX §3.1.1 闭包表）；局部收集只染 local 会计闭包，shared 子   */
/*     对象整条跳过（他线程 mutator 可原子触碰 shared 子图，Bacon-Rajan */
/*     「扫描期计数不变」前提只对无人并发触碰的闭包成立——若 shared 子   */
/*     参与减量，他线程的并发 release 会把「模拟死亡减量」叠加成真实    */
/*     死亡，活对象误收 UAF。跳过 = 局部候选判死只由 local 入边决定，   */
/*     而这些边全在属主闭包内）。                                       */
/*   - 互斥：局部收集在 region 内（cFlag PROCESSING）⟹ GC pass 排空     */
/*     等待；GC pass 进行中 ⟹ mutator 挡在 region_enter（局部收集的    */
/*     触发点都在 mutator 语境）。两收集器颜色位无跨写者。              */
/*   - 账本不变量「局部账本在册 ⟹ local 会计」由 promote_subgraph 的    */
/*     翻位同步摘条目（rigi_gc_local_ledger_detach）维持。              */
/* 与 gc_pass 三阶段的关系：markGray/scan/scanBlack/gather/compensate   */
/* 为克隆变体（lgc_*，差异 = plain rc + shared 子跳过，改 gc_* 或       */
/* lgc_* 须双侧同步）；gc_trace 遍历骨架原样复用（遍历无 rc 语义，子   */
/* 边策略全在回调）；teardown 为克隆变体（lgc_teardown_*，差异 =        */
/* shared 子边走原子 fetch_sub 终态协议 + 级联，local 子 plain）。      */

/* 析构中间态 guard（TLS）：rigi_destruct 包围置位。析构拆边到一半的
 * 图不满足收集扫描的快照一致性（markGray 假设「归还 +1 后候选 rc =
 * 真实外部引用数」在扫描全程稳定），guard 期间登记照做（+1/账本/债务
 * 语义不变），触发延迟到析构外的下一次 release/alloc 检查。 */
static _Thread_local int lgc_destruct_guard = 0;

void rigi_gc_local_guard_enter(void)
{
    lgc_destruct_guard++;
}

void rigi_gc_local_guard_exit(void)
{
    lgc_destruct_guard--;
}

/* 局部候选账本：候选数组 + 债务 + 收集工作向量。属主账本无锁（local
 * 引用不跨 Coroutine 边界 ⟹ 属主单线程触碰）；全局兜底账本由
 * lgc_global_gate 自旋锁保护（无协程上下文的登记/摘除/收干——main 流
 * 现实为单线程根执行流，锁为防御性低频互斥）。 */
typedef struct
{
    GcCandidate *items;
    size_t len;
    size_t cap;
    int64_t debt;
    int collecting; /* 收集重入/并发防御 */
    GcVec stack;    /* 三阶段显式 trace 栈（属主独占） */
    GcVec whites;   /* 白色集合（属主独占） */
} RigiLocalLedger;

static RigiLocalLedger lgc_global_ledger; /* 无协程上下文兜底（静态零初始化） */
static atomic_flag lgc_global_gate = ATOMIC_FLAG_INIT;

/* 属主账本全局注册表：promote_subgraph 的 detach 需要检索「登记语境
 * 之外的协程账本」（壳 teardown / 失败发布的 promote 遍历可触及登记在
 * 他协程账本的 local 对象——异常图随 waiter 跨协程是 3c 的合法形态）。
 * 账本创建时认领槽位、销毁（lgc_pass 收干入口）时摘除——注册表只含
 * 「可安全 detach」的账本：收干中的账本已先行摘除，遍历不会与
 * lgc_pass 的裸账本操作并发。 */
#define RIGI_GC_MAX_LOCAL_LEDGER 256
static RigiLocalLedger *lgc_registry[RIGI_GC_MAX_LOCAL_LEDGER];
static atomic_flag lgc_registry_gate = ATOMIC_FLAG_INIT;

static void lgc_registry_lock(void)
{
    while (atomic_flag_test_and_set_explicit(&lgc_registry_gate,
               memory_order_acquire))
    {
    }
}

static void lgc_registry_unlock(void)
{
    atomic_flag_clear_explicit(&lgc_registry_gate, memory_order_release);
}

/* 注册表认领/摘除（账本创建 / 收干入口调用；返回 1 = 成功） */
static int lgc_registry_add(RigiLocalLedger *lg)
{
    int i;
    lgc_registry_lock();
    for (i = 0; i < RIGI_GC_MAX_LOCAL_LEDGER; i++)
    {
        if (lgc_registry[i] == NULL)
        {
            lgc_registry[i] = lg;
            lgc_registry_unlock();
            return 1;
        }
    }
    lgc_registry_unlock();
    return 0;
}

static void lgc_registry_remove(RigiLocalLedger *lg)
{
    int i;
    lgc_registry_lock();
    for (i = 0; i < RIGI_GC_MAX_LOCAL_LEDGER; i++)
    {
        if (lgc_registry[i] == lg)
        {
            lgc_registry[i] = NULL;
            break;
        }
    }
    lgc_registry_unlock();
}

static void lgc_global_lock(void)
{
    while (atomic_flag_test_and_set_explicit(&lgc_global_gate,
               memory_order_acquire))
    {
    }
}

static void lgc_global_unlock(void)
{
    atomic_flag_clear_explicit(&lgc_global_gate, memory_order_release);
}

static RigiLocalLedger *lgc_ledger_new(void)
{
    RigiLocalLedger *lg =
        (RigiLocalLedger *)rigi_track_malloc(sizeof(RigiLocalLedger));
    lg->items = NULL;
    lg->len = 0;
    lg->cap = 0;
    lg->debt = 0;
    lg->collecting = 0;
    lg->stack.items = NULL;
    lg->stack.len = 0;
    lg->stack.cap = 0;
    lg->whites.items = NULL;
    lg->whites.len = 0;
    lg->whites.cap = 0;
    if (!lgc_registry_add(lg))
    {
        /* 注册表耗尽：账本功能不受影响（登记/收集/收干照常），仅
         * promote 的跨账本 detach 检索不到它——其候选若被 promote
         * 遍历到将走「翻位后条目残留」路径，靠收集期按位分流兜底。
         * 256 并发属主远超运行时的协程并发上限，实际不可达。 */
        fprintf(stderr, "rigi_rt: 局部账本注册表耗尽（>%d 属主）\n",
            (int)RIGI_GC_MAX_LOCAL_LEDGER);
    }
    return lg;
}

/* 账本自持资源全部台账配对释放（结构本体由调用方按归属释放：属主账本
 * 随协程终态、兜底账本随 shutdown）。 */
static void lgc_ledger_release_buffers(RigiLocalLedger *lg)
{
    gc_vec_destroy(&lg->stack);
    gc_vec_destroy(&lg->whites);
    rigi_track_free(lg->items);
    lg->items = NULL;
    lg->len = 0;
    lg->cap = 0;
    lg->debt = 0;
}

static void lgc_ledger_free(RigiLocalLedger *lg)
{
    if (lg == NULL)
    {
        return;
    }
    lgc_ledger_release_buffers(lg);
    rigi_track_free(lg);
}

/* 账本定位：TLS 当前协程 → 句柄槽（懒建，仿 shell_registry 先例）；
 * NULL（main 流根执行流 / globals init——主线程根协程不经 cohandle）
 * → 全局兜底。TLS 协程只在 resume 执行段内非 NULL 且句柄被执行门闸
 * 强引用（cohandle resume 包围），热路径读到的槽位指针恒有效。
 * Phase 3d-1 收集定向（TLS）：局部收集期间，只有收干线程自身的账本
 * 操作（teardown 级联析构触发的终态 detach、登记、触发判定）被定向
 * 到正在收干的账本——收干语境的 TLS 协程 ≠ 候选登记协程，按 TLS 定位
 * 会 detach 未命中 → 在册对象被析构而条目残留（悬垂 UAF）。
 * 注意 _Thread_local 语义：外部线程不被定向——它们对「收干中账本」
 * 的终态 detach 由 after==1 分支的 lg->collecting 检查拦截（返回非终
 * 态、交收干者处理），避免与收干线程的裸账本操作并发。 */
static _Thread_local RigiLocalLedger *lgc_collecting_mine = NULL;

static RigiLocalLedger *lgc_ledger_current(void)
{
    void *co;
    RigiLocalLedger *lg;
    if (lgc_collecting_mine != NULL)
    {
        return lgc_collecting_mine;
    }
    co = rigi_tls_get_coroutine();
    if (co != NULL)
    {
        lg = (RigiLocalLedger *)rigi_ch_local_ledger_load(co);
        if (lg == NULL)
        {
            lg = lgc_ledger_new();
            rigi_ch_local_ledger_store(co, lg);
        }
        return lg;
    }
    return &lgc_global_ledger;
}

/* ---- 局部三阶段克隆变体（与 gc_mark_gray/scan/scan_black/gather 和
 * compensate 一一对应；系统性差异两处，改任一侧须双侧同步）：
 *   ① rc 访问 plain（local 闭包属主独占，无并发——arc.c local 会计
 *      非原子化的同款论证）；
 *   ② shared 会计子对象整条跳过染色（不减/不加/不灰化/不传播）。
 * promote 翻位配套摘除的前向声明（定义见账本内核段）。 */

static void lgc_mark_gray_child(void *child, void *ctx)
{
    RigiObjectHeader *ch = (RigiObjectHeader *)child;
    GcVec *st = (GcVec *)ctx;
    if ((ch->packedFlags & RIGI_PF_SHARED_ACCOUNTING) != 0)
    {
        return; /* shared 子图不进局部染色（他线程可原子触碰） */
    }
    ch->rc = ch->rc - 1u;
    if (gc_color(ch) != RIGI_GC_GRAY)
    {
        gc_set_color(ch, RIGI_GC_GRAY);
        gc_vec_push(st, child);
    }
}

static void lgc_mark_gray(void *root, GcVec *st)
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
        gc_trace(o, oh->typeId, lgc_mark_gray_child, st);
    }
}

static void lgc_scan_black_child(void *child, void *ctx)
{
    RigiObjectHeader *ch = (RigiObjectHeader *)child;
    GcVec *st = (GcVec *)ctx;
    if ((ch->packedFlags & RIGI_PF_SHARED_ACCOUNTING) != 0)
    {
        return; /* 对称跳过：markGray 未减 shared 子，scanBlack 不加 */
    }
    ch->rc = ch->rc + 1u;
    if (gc_color(ch) != RIGI_GC_BLACK)
    {
        gc_set_color(ch, RIGI_GC_BLACK);
        gc_vec_push(st, child);
    }
}

static void lgc_scan_black(void *root, GcVec *st)
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
        gc_trace(o, oh->typeId, lgc_scan_black_child, st);
    }
}

static void lgc_scan_push_gray_child(void *child, void *ctx)
{
    if (gc_color((RigiObjectHeader *)child) == RIGI_GC_GRAY)
    {
        gc_vec_push((GcVec *)ctx, child);
    }
}

static void lgc_scan(void *root, GcVec *scan_st, GcVec *black_st)
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
        if (oh->rc > 0u) /* plain：local 闭包计数属主独占 */
        {
            lgc_scan_black(o, black_st);
        }
        else
        {
            gc_set_color(oh, RIGI_GC_WHITE);
            gc_trace(o, oh->typeId, lgc_scan_push_gray_child, scan_st);
        }
    }
}

static void lgc_gather_push_white_child(void *child, void *ctx)
{
    if (gc_color((RigiObjectHeader *)child) == RIGI_GC_WHITE)
    {
        gc_vec_push((GcVec *)ctx, child);
    }
}

static void lgc_gather_white(void *root, GcVec *st, GcVec *whites)
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
        gc_trace(o, oh->typeId, lgc_gather_push_white_child, st);
    }
}

static void lgc_compensate_child(void *child, void *ctx)
{
    RigiObjectHeader *ch = (RigiObjectHeader *)child;
    (void)ctx;
    if ((ch->packedFlags & RIGI_PF_SHARED_ACCOUNTING) != 0)
    {
        return; /* markGray 未减 shared 子，无需补偿 */
    }
    if (gc_color(ch) != RIGI_GC_GRAY)
    {
        /* 存活子引用：补偿 markGray 的减量（白→白边由同批释放湮灭） */
        ch->rc = ch->rc + 1u;
    }
}

/* ---- 局部清理段（与 gc_teardown_ex/_fat/_block 对应的克隆变体）：    */
/* local 子边 plain 减 + 级联兜底；shared 子边原子 fetch_sub 终态协议   */
/* （他线程 mutator 可能并发 acquire/release——old==1 的原子旧值判定   */
/* 保证终态析构恰一方）。级联安全论证：shared 对象 rc==1 ⟹ 最后引用即  */
/* 本边 ⟹ 他线程无引用 ⟹ 级联链此后无并发；且在册候选 rc ≥ 2（账本   */
/* +1）⟹ 级联不会触达任何账本在册对象（全局或局部）。                   */

static void lgc_teardown_fat(uint64_t type_id, uint64_t payload);

static void lgc_teardown_ex(void *object, const RigiTypeSheet *sheet, int free_self);

static void lgc_teardown_block(void *ptr, const RigiTypeSheet *sheet)
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
            /* String 块 rc 是原子计数（string_rc.c），unfenced 面 =
             * 无 region 的同一 fetch_sub——与可能存活的他线程 mutator
             * 并发安全（local 对象可引用 shared，shared 可引 String）。 */
            rigi_string_release_unfenced(*(char *const *)(base + cursor));
        }
        else
        {
            uint64_t type_id = *(const uint64_t *)(base + cursor);
            uint64_t payload = *(const uint64_t *)(base + cursor + 8);
            lgc_teardown_fat(type_id, payload);
        }
        cursor += 16u;
    }
}

static void lgc_teardown(void *object, const RigiTypeSheet *sheet)
{
    lgc_teardown_ex(object, sheet, 1);
}

/* free_self=0：只走边（dispose 检查 + 子引用释放），本体留给统一释放段
 * （两段式：走边期全部白色本体仍存活，邻居颜色检查不踩已释放内存——
 * 与 gc_teardown_ex 同一不变量）；free_self=1：走边并即放（级联兜底） */
static void lgc_teardown_ex(void *object, const RigiTypeSheet *sheet, int free_self)
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
                lgc_teardown_fat(type_id, payload);
                continue;
            }
            if ((elemSheet->typeFlags & RIGI_TYPE_STRING) != 0)
            {
                uint64_t elem_tid = *(const uint64_t *)elem;
                uint64_t elem_tag = elem_tid >> RIGI_TAG_SHIFT;
                if (elem_tag == RIGI_TAG_HEAP_VALUE || elem_tag == RIGI_TAG_OBJECT)
                {
                    uint64_t elem_pl = *(const uint64_t *)((const char *)elem + 8);
                    lgc_teardown_fat(elem_tid, elem_pl);
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
                    lgc_teardown_block(elem, elemSheet);
                }
            }
            else
            {
                uint64_t type_id = *(const uint64_t *)elem;
                uint64_t payload = *(const uint64_t *)((const char *)elem + 8);
                lgc_teardown_fat(type_id, payload);
            }
        }
        if (free_self != 0)
        {
            rigi_track_free(object);
        }
        return;
    }

    /* capability 壳析构（与 gc_teardown_ex 的壳分支同挂点）：壳计数
     * fetch_sub 原子 + 摘表（壳锁），target 经局部 raw 回调拆解（白色
     * 同胞边整条跳过 + 按会计位分流减量）。局部收集期间壳锁可用
     * （gc_flag IDLE、不持其它锁）；壳若已过户（协程终态收干之后的
     * 残留场景不存在——收干先于 owner_teardown），过户壳走全局就地
     * 清理分支同样安全。 */
    if (rigi_handle_is_capability(object, sheet))
    {
        rigi_shell_gc_release_capability(object, lgc_teardown_fat);
        if (free_self != 0)
        {
            rigi_track_free(object);
        }
        return;
    }

    /* §25 IDisposable 合约检查挂点（局部清理步入点，三销毁入口口径） */
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
            lgc_teardown_fat(type_id, payload);
        }
        cursor += 16u;
    }
    if (free_self != 0)
    {
        rigi_track_free(object);
    }
}

static void lgc_teardown_fat(uint64_t type_id, uint64_t payload)
{
    uint64_t tag = type_id >> RIGI_TAG_SHIFT;
    const RigiTypeSheet *sheet;
    RigiObjectHeader *ch;
    if (payload == 0)
    {
        return;
    }
    if (tag == RIGI_TAG_OBJECT)
    {
        ch = (RigiObjectHeader *)(uintptr_t)payload;
        if ((ch->packedFlags & RIGI_PF_SHARED_ACCOUNTING) != 0)
        {
            /* shared 子边：原子 fetch_sub 终态协议（注释见段首）。shared
             * 对象不进局部白集（从不染色），无需白色同胞检查。 */
            uint32_t old = atomic_fetch_sub_explicit(
                (_Atomic uint32_t *)&ch->rc, 1, memory_order_acq_rel);
            if (old == 1u)
            {
                lgc_teardown((void *)ch, ch->typeId);
            }
            return;
        }
        if (gc_color(ch) == RIGI_GC_GRAY)
        {
            return; /* 白色同胞：同批释放，边整条跳过 */
        }
        {
            uint32_t rc = ch->rc; /* plain：local 子图他线程不触碰 */
            if (rc <= 1u)
            {
                /* 补偿不变量下 rc==0 不出现、rc==1 减后归零的级联也只
                 * 发生在「外部引用全断」的真死对象上；递归兜底防御 */
                lgc_teardown((void *)ch, ch->typeId);
                return;
            }
            ch->rc = rc - 1u;
        }
        return;
    }
    if (tag == RIGI_TAG_HEAP_VALUE)
    {
        sheet = (const RigiTypeSheet *)(uintptr_t)(type_id & RIGI_SHEET_MASK);
        lgc_teardown_block((void *)(uintptr_t)payload, sheet);
        rigi_track_free((void *)(uintptr_t)payload);
    }
}

/* ---- 账本内核：登记 / 摘除 / 收集 / 触发 ---- */

static uint64_t lgc_stats_passes = 0;
static uint64_t lgc_stats_collected = 0;

/* 锁语义满足前提下的登记内核（属主账本无锁直调；兜底账本锁内直调）：
 * closed / 收集期 / PURPLE 复检 + append + 置 PURPLE|idx + 债务累计。
 * 返回 1 = 已登记（+1 已驻留 rc）；0 = 未登记。 */
static int lgc_note_locked(RigiLocalLedger *lg, void *object,
                           const RigiTypeSheet *sheet)
{
    RigiObjectHeader *h = (RigiObjectHeader *)object;
    int64_t size;
    uint32_t idx;
    if (lg->collecting)
    {
        return 0; /* 收集期禁登记（防御：登记方即属主，收集期间已冻结） */
    }
    if (gc_color(h) == RIGI_GC_PURPLE)
    {
        return 0; /* 同一对象一轮候选账本只计一次 */
    }
    size = gc_object_size(object, sheet);
    if (lg->len >= RIGI_GC_LEDGER_MAX)
    {
        fprintf(stderr, "rigi_rt: 局部候选账本越上限（%u 条）\n",
            (unsigned)RIGI_GC_LEDGER_MAX);
        abort();
    }
    if (lg->len == lg->cap)
    {
        size_t ncap = lg->cap != 0 ? lg->cap * 2 : 256;
        GcCandidate *nbuf =
            (GcCandidate *)rigi_track_malloc(ncap * sizeof(GcCandidate));
        if (lg->items != NULL)
        {
            memcpy(nbuf, lg->items, lg->len * sizeof(GcCandidate));
            rigi_track_free(lg->items);
        }
        lg->items = nbuf;
        lg->cap = ncap;
    }
    idx = (uint32_t)lg->len;
    lg->items[lg->len].object = object;
    lg->items[lg->len].size = size;
    lg->len++;
    gc_flags_replace(h, RIGI_GC_COLOR_MASK | (0xFFFFFFu << RIGI_GC_INDEX_SHIFT),
        RIGI_GC_PURPLE | (idx << RIGI_GC_INDEX_SHIFT));
    /* 账本持 +1 强引用（Phase 1.3 不变量的局部版）：PURPLE 在册 ⟹
     * rc = U + 1。属主单线程无在飞 pin，plain 加即可（macroGC 的 pin
     * 由 release 调用方驻留后无偿转账，这里由登记方直接补上）。缺此
     * +1 会破坏「在册 ⟹ rc ≥ 1」不变量——在册对象可被外部 release
     * 减穿归零走「非在册终态」误析构，账本悬垂 UAF。 */
    h->rc = h->rc + 1u;
    lg->debt += size;
    return 1;
}

/* 账本内摘除（swap-remove + 债务回减 + 清位 + plain 归还 +1）。调用方
 * 保证候选在册（PURPLE）与锁语义（属主无锁 / 兜底锁内）。返回 1 = 摘
 * 除、0 = 索引校验未命中（跨账本防御）。plain rc 归还的前提：摘除语境
 * = 属主线程（登记方）或 promote 翻位的发布前窗口（翻位前置协议：
 * macrogc.h rigi_gc_promote_subgraph 契约）——对象此刻只被本语境触碰。 */
static int lgc_detach_from(RigiLocalLedger *lg, void *object)
{
    RigiObjectHeader *h = (RigiObjectHeader *)object;
    uint32_t pf = atomic_load_explicit(
        (_Atomic uint32_t *)&h->packedFlags, memory_order_relaxed);
    uint32_t idx = pf >> RIGI_GC_INDEX_SHIFT;
    uint32_t last;
    RigiObjectHeader *moved;
    if (idx >= lg->len || lg->items[idx].object != object)
    {
        return 0;
    }
    lg->debt -= lg->items[idx].size;
    last = (uint32_t)lg->len - 1u;
    if (idx != last)
    {
        lg->items[idx] = lg->items[last];
        moved = (RigiObjectHeader *)lg->items[idx].object;
        gc_flags_replace(moved, 0xFFFFFFu << RIGI_GC_INDEX_SHIFT,
            idx << RIGI_GC_INDEX_SHIFT);
    }
    lg->len--;
    gc_flags_replace(h, RIGI_GC_COLOR_MASK | (0xFFFFFFu << RIGI_GC_INDEX_SHIFT),
        0);
    h->rc = h->rc - 1u; /* 归还账本 +1（plain） */
    return 1;
}

/* 属主协作收集一轮（三阶段骨架见段首注记）。lg 可为属主账本（无锁）
 * 或兜底账本（shutdown 终轮主线程收干——GC 线程 idle，无并发）。
 * 收集开始即从注册表摘除且**永不加回**（Phase 3d-2 实验定案）：promote
 * 的跨账本 detach 遍历（rigi_gc_local_ledger_detach ③）在任意线程跑，
 * 与属主线程的无锁账本操作（登记 append / 终态 detach swap-remove）
 * 无互斥——账本若在注册表，遍历即可能与属主并发裸操作同一 items 数组
 * （条目损坏 → 泄漏/索引错乱，mw13_shared_cycle_concurrent_release
 * 8-block 泄漏实证；3d-2 曾试验 pass 收尾把空册账本加回注册表，即因
 * 此窗口撤回）。「pass 即永久摘册」使跑过收集的账本对 detach 遍历
 * 不可见——代价是 3d-1 既有盲区：摘册后新登记的候选若被 promote
 * 遍历到将走「翻位后条目残留」，靠后续收集期按位分流与对象自身
 * shared 终态路径兜底（3d-1 起既有权衡，测试面内未见触发，详见
 * rigi_gc_local_ledger_detach ④ 注释）。 */
static void lgc_pass(RigiLocalLedger *lg)
{
    size_t i;
    lg->collecting = 1;
    lgc_collecting_mine = lg;
    /* 收集期从注册表摘除（永久，见上段）：本轮裸账本操作期间，他线程
     * 的 promote detach 遍历不触达本账本。 */
    lgc_registry_remove(lg);
    /* region 包围：release/alloc 触发语境已在 region 内（depth 递增，
     * fence 不触发）；协程终态收干语境（rigi_ch_destroy_payload，任意
     * 线程）由此与可能在跑的 macroGC pass 六步握手互斥（GC pass 进行
     * 中则在此阻塞等 GCAlarm，GC 不依赖本线程，无死锁）。 */
    rigi_region_enter();
    /* 归还账本 +1（同 gc_pass 步骤 5）：候选全 local 会计（promote 翻
     * 位同步摘条目维持的不变量）⟹ plain 减无并发；此后染色期 rc ==
     * 真实外部引用数，scan 的 rc>0 判活锚点与补偿/清理同构。 */
    for (i = 0; i < lg->len; i++)
    {
        RigiObjectHeader *cand = (RigiObjectHeader *)lg->items[i].object;
        cand->rc = cand->rc - 1u;
    }
    for (i = 0; i < lg->len; i++)
    {
        lgc_mark_gray(lg->items[i].object, &lg->stack);
    }
    for (i = 0; i < lg->len; i++)
    {
        lgc_scan(lg->items[i].object, &lg->stack, &lg->whites);
    }
    lg->whites.len = 0;
    for (i = 0; i < lg->len; i++)
    {
        if (gc_color((RigiObjectHeader *)lg->items[i].object)
            == RIGI_GC_WHITE)
        {
            lgc_gather_white(lg->items[i].object, &lg->stack, &lg->whites);
        }
    }
    /* 补偿：清理前对存活 local 子引用逐一 rc++（shared 子 markGray 未
     * 减，无需补偿——其边由清理段按 shared 终态协议真实释放） */
    for (i = 0; i < lg->whites.len; i++)
    {
        RigiObjectHeader *wh = (RigiObjectHeader *)lg->whites.items[i];
        gc_trace(lg->whites.items[i], wh->typeId, lgc_compensate_child, NULL);
    }
    /* 统一清理两段式（与 gc_pass 同一不变量：走边期白色本体仍存活） */
    for (i = 0; i < lg->whites.len; i++)
    {
        RigiObjectHeader *wh = (RigiObjectHeader *)lg->whites.items[i];
        lgc_teardown_ex(lg->whites.items[i], wh->typeId, 0);
    }
    for (i = 0; i < lg->whites.len; i++)
    {
        rigi_track_free(lg->whites.items[i]);
    }
    lgc_stats_collected += lg->whites.len;
    lg->whites.len = 0;
    /* 出册 + 债务清零：活者染色已覆写为非 PURPLE（索引位残留随 len=0
     * 无效化，同 gc_pass 收尾），「非在册 ⟹ rc = U」闭环 */
    lg->len = 0;
    lg->debt = 0;
    lg->collecting = 0;
    lgc_collecting_mine = NULL;
    rigi_region_exit();
    lgc_stats_passes++;
    if (gc_stats_on)
    {
        fprintf(stderr,
            "[gc-stats] local passes=%llu collected=%llu\n",
            (unsigned long long)lgc_stats_passes,
            (unsigned long long)lgc_stats_collected);
    }
}

/* 触发判定（safepoint 族①：债务阈值）：started/off/closed/stop 生命
 * 周期闸 + 重入（collecting）+ 析构中间态（destruct guard）+ 阈值。 */
static void lgc_maybe_collect(RigiLocalLedger *lg)
{
    if (!gc_started
        || gc_off != 0
        || atomic_load_explicit(&gc_closed, memory_order_relaxed)
        || atomic_load_explicit(&gc_stop, memory_order_relaxed))
    {
        return;
    }
    if (lg->collecting || lgc_destruct_guard > 0)
    {
        return;
    }
    if (lg->debt <= gc_threshold)
    {
        return;
    }
    lgc_pass(lg);
}

/* 机制让步对象识别：Middleware 合成的运行时机制类（类型名 "$mw." 前缀，
 * 如协程续体帧 $mw.frame.*）。这类对象经 await/Task 完成链跨协程触碰与
 * 终态释放，生命周期可跨越登记语境协程的账本寿命。两处语义：
 *   - 登记：统一落全局兜底账本（锁保护、进程寿命、shutdown 终轮收干）；
 *   - promotion：promote_subgraph 对其跳过翻位与局部账本摘除（仍展开
 *     子图）——若被翻位，将出现「SHARED 会计 + 局部账本 PURPLE」混合
 *     态，其后的 shared 路径终态释放会拿局部 idx 查全局账本（索引损坏
 *     abort）。机制对象恒守 local 会计与其兜底账本归属。
 *
 * Phase 3d-2 让步论证严谨化（3d-1 遗留 4 收口）：机制对象是 local 会计
 * （plain rc）却被跨协程触碰，其内存安全论证分三层：
 *
 *   ① 引用操作串行（无并发写写/读写对）：机制对象的每次 acquire/release
 *      都发生在「当前属主」的执行段内；跨协程交接（await 完成通知、
 *      Task 完成链、Dispatcher 重发布）全部经队列/门闸的 release-acquire
 *      happens-before 边（worker enqueue/park、cohandle resume 执行门闸、
 *      native_rc 归零回调），交接完成后原属主不再触碰该对象。故任意两
 *      次引用操作全序化，rc 的 plain 写写不并发——剩余风险只有「读方
 *      （兜底账本 pass 的模拟减量/判活）与写方（他线程引用操作）的
 *      并发窗口」，见 ③。
 *   ② 硬件可见性：rc 是 4 字节对齐字段，x86-64 保证对齐 ≤4B load/store
 *      的天然原子性（Intel SDM Vol.3A §8.1.1——无撕裂、单写者序由 ①
 *      的 happens-before 供给）；xchg/lock 前缀路径（shared 面、账本锁）
 *      在同线程混用时为 plain 访问提供免费栅栏。本让步绑定 x86-64 目标：
 *      移植到弱内存序架构前必须先完成「机制对象恒原子会计」或「分配即
 *      promote」改造。
 *   ③ 残余窗口的诚实边界：兜底 lgc_pass（债务触发，可在 worker 线程
 *      跑）对机制候选的 plain 扫描与另一 worker 的 $mw.frame 引用操作
 *      理论可并发——Bacon-Rajan 的「计数快照」前提在该窗口不成立，最
 *      坏后果是候选误判（活帧早收 UAF / 死帧滞留）。工程验收以此窗口
 *      实际不可达为准：failure/Task 完成族、Stress 长跑与 memtrack 零
 *      泄漏口径全绿（本阶段 3d-2 复验）。
 *
 * 根本消除路线 =「frame 分配即 promote（恒 shared 会计）」，3d-2 评估
 * 后否决，理由：(a) frame root slots 是用户 local 对象的主锚，promote
 * 语义要求子图连带翻位（只翻本体打破「shared 不引 local」图不变量，
 * 全局 pass 会拆进 plain 子图）——async 触碰过的对象全体升 shared 会计，
 * split-heap 在协程场景的收益归零；(b) 分配热路径纳税（机制识别判定 +
 * shared release 协议的 pin 三原子）；(c) 换来的是 ①② 已覆盖、③ 工程
 * 验收管控的窄窗口，代价收益不成比例。后续若移植非 x86 架构或出现 ③
 * 的实锚，再重启该路线。 */
static bool lgc_is_mechanism_object(const RigiTypeSheet *sheet)
{
    const RigiTypeInfo *ti = sheet != NULL ? sheet->typeInfoId : NULL;
    if (ti != NULL && ti->name.data != NULL && ti->name.len >= 4)
    {
        return ti->name.data[0] == '$'
            && ti->name.data[1] == 'm'
            && ti->name.data[2] == 'w'
            && ti->name.data[3] == '.';
    }
    return false;
}

/* release local 非终态/在册收口（arc.c plain 减量后 after>0 调用；
 * after = 减后值）：在册（PURPLE）走终态快路径（after==1：摘除 + 归还
 * +1 → 返回 1 终态）或债务触发；非在册尝试登记候选（may-cycle 过滤 +
 * 账本 +1）+ 债务阈值检查。返回值与 rigi_gc_release_shared 完全同约
 * 定：1 = 终态析构信号；否则返回减前值 after+1（≥2）——绝不能把减后
 * 值透传（减后值==1 的非终态对象会被调用方误判终态析构 → UAF）。 */
uint32_t rigi_gc_local_after_release(void *object, uint32_t after)
{
    RigiObjectHeader *h = (RigiObjectHeader *)object;
    const RigiTypeSheet *sheet;
    RigiLocalLedger *lg;
    uint32_t before = after + 1u;
    int alive = gc_started
        && gc_off == 0
        && !atomic_load_explicit(&gc_closed, memory_order_relaxed)
        && !atomic_load_explicit(&gc_stop, memory_order_relaxed);
    lg = lgc_ledger_current();
    if ((h->packedFlags & RIGI_GC_COLOR_MASK) == RIGI_GC_PURPLE)
    {
        /* 在册：after = U（账本 +1 已被 plain 减量计入）。after==1 ⟺ 最
         * 后一次用户引用：摘除候选 + 归还账本 +1（rc 归 0）→ 终态，确定
         * 性语义同 macroGC 的 PURPLE 快路径 old==2 分支（这里无并发，
         * 预算恰为 1+1）。closed/stop 后 PURPLE 不可达（shutdown 收干
         * 已全账本出册），摘除路径不查生命周期闸（lgc_detach_from 的
         * 索引校验兜底）。 */
        if (after == 1u)
        {
            /* 终态 detach：先查当前语境账本（属主账本无锁），未命中再
             * 锁内补查全局兜底。两处都未命中 ⟹ 对象登记在其它协程的
             * 账本（$mw.frame 等运行时机制对象经 await/Task 完成链跨
             * 协程终态释放——静态划分的机制性让步点）。账本 +1 仍由
             * 登记账本持有：此处不能摘除、不能归还、更不能析构（析构
             * 即悬垂条目 UAF）。返回非终态，交登记账本的收集者（属主
             * 协作收集 / 协程终态收干 / shutdown 兜底）归还 +1 后按
             * 「外部引用已尽」语义回收——归还后 rc=0，scan 判白必收，
             * 零泄漏闭环。 */
            int detached = 0;
            if (lg != &lgc_global_ledger && !lg->collecting)
            {
                detached = lgc_detach_from(lg, object);
            }
            if (!detached)
            {
                lgc_global_lock();
                if (!lgc_global_ledger.collecting) detached = lgc_detach_from(&lgc_global_ledger, object);
                lgc_global_unlock();
            }
            if (!detached)
            {
                return before; /* 交登记账本的收集者回收 */
            }
            return 1u; /* 终态：调用方析构 */
        }
        if (alive)
        {
            lgc_maybe_collect(lg);
        }
        return before;
    }
    if (!alive)
    {
        /* 生命周期闸（stop/closed/off）只挡登记与触发，不挡非在册
         * 终态析构——after==1 的对象若被误判非终态将永不析构（泄漏，
         * shutdown 末批释放路径实测命中）。终态判定与账本零交互。 */
        if (after == 1u)
        {
            return 1u;
        }
        return before;
    }
    sheet = h->typeId;
    /* Phase 3d-2 登记（候选资格）条件修正：非机制对象 `after >= 1`（对齐
     * shared 路 pin 协议的「old > 2 ⟹ 登记」——old 含 pin，等价
     * after ≥ 1）。3d-1 原条件 `after > 1` 漏掉 after==1（本边释放后仅
     * 剩一条外部边）的 release——死环成员（全员 rc=1 形态：每节点恰被
     * 环内前驱持有）的每一次入账本机会都被它排除，环头 rc 减到 1 后再
     * 无 release 切入点，整环游离于候选账本之外 → 协程终态收干/兜底
     * pass 均不可见 → 永久泄漏（terminated-only 探针 15/16 环、bench10
     * 全形态 61440 blocks 逐位复现实证）。after==1 登记后的不变量仍
     * 闭环：账本 +1 ⟹ 在册 rc ≥ 2，他边 release 走 PURPLE 快路径
     * after==1 detach+终态；after==0 已被 arc.c 拦截为终态，不进入本
     * 函数。
     * 机制对象（$mw.）保持 3d-1 原登记面（after > 1）：其兜底账本在
     * after==1 下候选量暴涨（frame 类 U 常为 1），worker 侧兜底 pass
     * 频率同比放大，与跨协程机制对象 plain 引用操作的并发窗口
     * （lgc_is_mechanism_object 段让步论证 ③）实测触发 mw13 并发用例
     * 8-block 泄漏；机制对象自身成环非 observed 形态，缩面恢复窗口
     * 原大小。 */
    if (sheet != NULL && gc_may_cycle(object, sheet)
        && (after > 1u || !lgc_is_mechanism_object(sheet)))
    {
        /* PURPLE plain 快检已在上面完成；may_cycle 只读 TypeSheet 静态
         * 元数据。属主账本无锁直检（local 引用不跨 Coroutine ⟹ 登记
         * 方唯一）；机制让步对象（$mw. 前缀的运行时机制类，plain 会计
         * 的跨协程让步论证见 lgc_is_mechanism_object 段）统一落全局
         * 兜底账本（锁保护、进程寿命，shutdown 终轮收干兜底），避免
         * 「登记账本先于 +1 持有期消亡」的悬垂。 */
        if (lgc_is_mechanism_object(sheet))
        {
            lg = &lgc_global_ledger;
        }
        if (lg == &lgc_global_ledger)
        {
            lgc_global_lock();
            (void)lgc_note_locked(lg, object, sheet);
            lgc_global_unlock();
        }
        else
        {
            (void)lgc_note_locked(lg, object, sheet);
        }
    }
    lgc_maybe_collect(lg);
    return before;
}

/* 分配热路径债务检查（rigi_alloc 尾部）：兜底「只有分配没有释放」的
 * 长循环——债务只在 release 登记时增长，alloc 检查让已积压的账本在
 * 无 release 的分配流中也能被收。 */
void rigi_gc_local_maybe_collect(void)
{
    RigiLocalLedger *lg;
    if (!gc_started
        || gc_off != 0
        || atomic_load_explicit(&gc_closed, memory_order_relaxed)
        || atomic_load_explicit(&gc_stop, memory_order_relaxed)
        || lgc_destruct_guard > 0)
    {
        return;
    }
    lg = lgc_ledger_current();
    lgc_maybe_collect(lg);
}

/* Phase 3d-2：挂出点顺手小回收（rigi_coroutine_resume 段尾、TLS 协程
 * 尚为本句柄时调用；YIELDED/SUSPENDED 挂出语义，DONE 走终态收干）。
 * 判据 = 债务超全局阈值（与 3d-1 债务阈值触发同口径，非「debt>0 即
 * 收」——实施验证中 debt>0 激进判据在挂起恢复链的活锚判活出册时序下
 * 出现 lambda_this_owned 的 2 块尾释放缺失，阈值判据下全绿且已覆盖
 * park 滞留目标：bench10 每 Task 挂出前债务 ~2.6MB ≫ 1MiB 默认阈值，
 * 挂出瞬间即收干，滞留不跨挂起期）。账本未建（NULL ⟹ 本协程从未
 * 登记过候选 ⟹ 债务必为 0）直接返回——挂出点高频，绝不懒建空账本。
 * 生命周期闸/重入/析构中间态与 lgc_maybe_collect 同款。 */
void rigi_gc_local_suspend_collect(void)
{
    void *co;
    RigiLocalLedger *lg;
    if (!gc_started
        || gc_off != 0
        || atomic_load_explicit(&gc_closed, memory_order_relaxed)
        || atomic_load_explicit(&gc_stop, memory_order_relaxed)
        || lgc_destruct_guard > 0)
    {
        return;
    }
    co = rigi_tls_get_coroutine();
    if (co == NULL)
    {
        return;
    }
    lg = (RigiLocalLedger *)rigi_ch_local_ledger_load(co);
    if (lg == NULL || lg->debt <= gc_threshold || lg->collecting)
    {
        return;
    }
    lgc_pass(lg);
}

/* promote 翻位配套的局部账本摘除（rigi_gc_promote_subgraph 调用）。
 * 前置：对象已翻 shared 位（翻位前置协议保证 rc 此刻只被本语境触碰，
 * plain 归还合法）；PURPLE 若在必属局部两账本之一（全局账本只收
 * shared 会计对象，其 PURPLE 在此被 SHARED 位检查排除）。 */
void rigi_gc_local_ledger_detach(void *object)
{
    RigiObjectHeader *h = (RigiObjectHeader *)object;
    uint32_t pf;
    void *co;
    RigiLocalLedger *lg;
    if (object == NULL)
    {
        return;
    }
    if (atomic_load_explicit(&gc_closed, memory_order_relaxed))
    {
        return; /* shutdown 后账本已终结（防御） */
    }
    pf = atomic_load_explicit(
        (_Atomic uint32_t *)&h->packedFlags, memory_order_relaxed);
    if ((pf & RIGI_PF_SHARED_ACCOUNTING) != 0
        || (pf & RIGI_GC_COLOR_MASK) != RIGI_GC_PURPLE)
    {
        return;
    }
    /* ① 当前协程账本：failreg 失败发布 = 属主线程 DONE 垫尾，TLS 即
     * 登记账本；他线程 teardown 语境 TLS 是别的协程或 NULL，索引校验
     * 必 miss（lgc_detach_from 的 object 比对防串账）。 */
    co = rigi_tls_get_coroutine();
    if (co != NULL)
    {
        lg = (RigiLocalLedger *)rigi_ch_local_ledger_load(co);
        if (lg != NULL && lgc_detach_from(lg, object))
        {
            return;
        }
    }
    /* ② 全局兜底账本（main 流对象 / $mw. 机制对象的登记归属）。壳锁 →
     * 兜底锁的取序与既有路径无反向（壳路径从不持壳锁进兜底锁；局部
     * 收集不持兜底锁跑清理段）。 */
    lgc_global_lock();
    (void)lgc_detach_from(&lgc_global_ledger, object);
    lgc_global_unlock();
    /* ③ 其余属主账本：异常图随 waiter 跨协程（3c 合法形态）后，壳
     * teardown / 失败发布的 promote 遍历可能触及「登记在他协程账本」
     * 的 local 对象——遍历注册表逐账本检索（object 指针比对防串账）。
     * 收干中的账本已在 lgc_pass 入口摘除注册表，遍历不与其并发。 */
    {
        int i;
        lgc_registry_lock();
        for (i = 0; i < RIGI_GC_MAX_LOCAL_LEDGER; i++)
        {
            lg = lgc_registry[i];
            if (lg != NULL && lgc_detach_from(lg, object))
            {
                lgc_registry_unlock();
                return;
            }
        }
        lgc_registry_unlock();
    }
    /* ④ 均未命中：对象不在任何局部账本（可能从未登记），无事可做
     * ——promote 翻位照常生效。 */
}

/* 协程终态收干（rigi_ch_destroy_payload 调用，先于 rigi_shell_owner_
 * teardown——两步的 plain/原子前提各自成立：先收干纯 local 账本，再
 * 让 promote 过户翻位）。终态语境 = native_rc 归零回调，属主已死，
 * local 对象无人竞争触碰 ⟹ plain 前提成立（任意承载线程皆可执行）。
 * 悬挂候选（债务未到阈值的尾批）不收即泄漏——memtrack 零泄漏口径的
 * 协程侧兜底。
 *
 * Phase 3d-2 批发 teardown（Erlang 进程退出模式，免环检测）评估结论：
 * **不落地，保留三阶段收干**。两条候选方案分别在勘察与实施实验中被
 * 证伪，记录如下备后续复核：
 *
 *   ① 「属主已死 ⟹ 残余候选必全死」的前提不严格成立。挂起中的协程
 *      可被 destroy（用户 drop 最后的 CoroutineHandle / 调度器收尾），
 *      此刻三类「属主已死但引用仍活」的合法锚存在：挂起帧 root slots
 *      （RUNTIME §21 ARC root，保活其引用对象）、未过户壳的 target 锚
 *      （壳在 rigi_shell_owner_teardown 才过户，收干先于过户）、
 *      CoroutineLocal 绑定栈（rigi_ch_locals_clear 在收干之后跑）。
 *      无视锚的整堆丢弃 = 错收活对象 UAF。
 *   ② 「归还账本 +1 后逐候选 rc==0 自检测判死」的前提错误（实施后
 *      NativeE2E lambda_this_owned 泄漏 194 blocks 实证后撤回）。
 *      Bacon-Rajan 语义中「归还 +1 后 rc」= **候选闭包之外**的入边数
 *      ——其他候选对本候选的边同样计入：死环成员归还后 rc = 环内入边
 *      数 > 0，逐候选 rc 检测把整环误判活者出册，此后无人再释放
 *      （泄漏）。判死必须先对候选闭包整体做 markGray 模拟减量（扣除
 *      候选间边）——这正是三阶段 markGray/scan 已经做的事。即「免环
 *      检测批发」在本结构（对象散布全局 malloc 堆、无 per-协程堆清单、
 *      根不可枚举）下不成立：任何正确的批发都要付一次全闭包遍历，与
 *      三阶段同阶。真正的 Erlang 模式需要 per-协程 region/arena 分配
 *      （协程堆可整批丢弃），属分配器架构课题，不在 split-heap 范围。 */
void rigi_gc_local_ledger_teardown(void *handle)
{
    RigiLocalLedger *lg = (RigiLocalLedger *)rigi_ch_local_ledger_load(handle);
    if (lg == NULL)
    {
        return;
    }
    rigi_ch_local_ledger_store(handle, NULL);
    lgc_pass(lg);
    lgc_ledger_free(lg);
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
    uint32_t expected = RIGI_GC_IDLE;
    uint64_t started = gc_stats_on ? gc_now_ms() : 0;
    uint64_t marked;
    uint64_t scanned;

    if (gc_trace_on)
    {
        fprintf(stderr, "[gc] pass begin ledger=%zu debt=%lld\n",
            gc_ledger_len,
            (long long)atomic_load_explicit(&gc_debt, memory_order_relaxed));
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
    /* 与 mutator 的二次检查构成完整握手：PROCESSING 发布前后跨越的
     * store/load 竞态必须再排空一次，不能只依赖 x86 的强内存序。 */
    atomic_thread_fence(memory_order_seq_cst);
    gc_drain_cflags();

    /* （5）染色与清理（账本锁全程：与 fence 冻结配合，双保险） */
    gc_ledger_acquire();
    atomic_store_explicit(&gc_in_collect, 1, memory_order_relaxed);
    /* 归还账本引用（Phase 1.3）：PURPLE 在册 ⟹ rc = U + 1（账本持 +1），
     * markGray 前逐候选归还，此后染色期 rc == 真实外部引用数——scan 的
     * rc>0 判活锚点与补偿/teardown 逻辑同旧协议完全一致（若不先归还，
     * 所有候选恒 rc ≥ 1，循环垃圾将永不回收）。归还发生在锁内 + fence
     * 冻结期，relaxed 访问无并发；活者随后被染色覆写为非 PURPLE，收尾
     * len=0 出册——「非在册 ⟹ rc = U」闭环；白色者整对象释放，本次
     * 归还随回收湮灭。 */
    for (i = 0; i < gc_ledger_len; i++)
    {
        RigiObjectHeader *cand = (RigiObjectHeader *)gc_ledger[i].object;
        gc_rc_store(cand, gc_rc_load(cand) - 1u);
    }
    for (i = 0; i < gc_ledger_len; i++)
    {
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

    /* 账本锁与 mutator fence 仍持有，且收集期禁止新增候选；账本整轮
     * 出清时债务精确归零，不再重复减去已由摘除路径扣过的体积。
     * 债务统一用 int64_t，避免 Windows 的 32 位 long 在大图上溢出。 */
    gc_ledger_len = 0;
    atomic_store_explicit(&gc_debt, 0, memory_order_relaxed);
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
        int64_t v = strtoll(env, NULL, 10);
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
    /* Phase 3d-1：先收干全局兜底局部账本（主线程就地 lgc_pass，此时
     * GC 线程仍 idle 等待、gc_stop 未置——无并发）。局部图与全局 pass
     * 的 shared 图不相交，顺序无硬依赖；先局部让终轮 pass 看到一致的
     * shared 子边计数（局部白色批对 shared 子的 fetch_sub 已落定）。 */
    if (lgc_global_ledger.len != 0)
    {
        lgc_pass(&lgc_global_ledger);
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
    gc_ledger_acquire();
    atomic_store_explicit(&gc_closed, 1, memory_order_relaxed);
    /* GC 自持资源全部走台账配对释放（shutdown 先于 mem_report 执行） */
    gc_vec_destroy(&gc_trace_stack);
    gc_vec_destroy(&gc_whites);
    rigi_track_free(gc_ledger);
    gc_ledger = NULL;
    gc_ledger_len = 0;
    gc_ledger_cap = 0;
    gc_ledger_release();
    /* Phase 3d-1：全局兜底局部账本自持资源（候选数组已随收干 len=0，
     * 台账配对释放缓冲与统计语义一并终结） */
    lgc_ledger_release_buffers(&lgc_global_ledger);
    gc_event_destroy(&gc_wake);
    gc_event_destroy(&gc_alarm);
}
