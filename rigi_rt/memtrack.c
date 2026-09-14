/*
 * 内存台账（MW7a；GC Phase 1.4 分流；GC Phase 2 分配器换面）：每分配 16B 头
 * {size_t size; size_t pad;}，live 块数与字节数按「per-线程 TLS 槽 +
 * 阈值并入」记账：
 *
 * 底层分配面（GC Phase 2，GC_OPTIMIZATION_PLAN §2.4 定案）：track 对的
 * malloc/free 从 UCRT libc 换为 mimalloc（mi_malloc_aligned/mi_free，
 * size-class + 线程缓存；ARC 下 free 点确定、无移动/压缩需求，契合度高）。
 * 契约保持：
 *   - 对外签名不变（rigi_track_malloc/rigi_track_free，arc.h）；RigiMemHdr
 *     16B 头与台账记账全数保留在本包装层，mimalloc 只做底层块管理；
 *   - 16B 对齐：mi_malloc 只保证 sizeof(void*)=8 对齐，而「对象 16B 对齐」
 *     是 rigi_rt 既有契约（arc.h 对象头注释、Any 16B 对齐槽指针）；64 位
 *     UCRT malloc 恰好 16B，换面后改用 mi_malloc_aligned(total, 16) 维持
 *     口径不回退。sizeof(RigiMemHdr)==16，故返回给调用方的「头后指针」
 *     与 mi_malloc_aligned 返回值同为 16B 对齐；
 *   - mimalloc 内部保留/缓存的内存不属于 track 面——零泄漏口径只看本层
 *     账本（mi_free 与 mi_malloc_aligned 一一配对，块归还即账本减量）。
 * mi_* 声明为 extern：rigi_rt 编译期无需 mimalloc 头（RigiRtBuilder 不加
 * -I），符号由链接期 mimalloc 静态库满足（tools/Fetch-Mimalloc.ps1 预取 +
 * MimallocResolver 链入；缺失在 native 编译期明确拒绝）。
 *
 * 记账结构（Phase 1.4）：
 *   - 每 OS 线程首次经台账分配/释放时懒认领一枚 cache-line 独占槽
 *     （macrogc.c gc_claim_cflag 同款认领制：槽位注册表 + CAS 认领 +
 *     TLS 指针缓存；认领后常驻，槽位不回收复用——线程退出后其槽余量
 *     原地保留，由报表聚合时读取）。热路径只做本槽 plain 加减：零
 *     locked 指令、零跨线程 cache line 争用（C4：原 3 个相邻未填充
 *     全局原子逐对象 fetch_add + peak CAS，多线程 false sharing +
 *     locked 竞争，单线程固定 locked 税）。
 *   - 槽内三项累计——分配字节节拍 turnover（含 alloc/free 两侧的
 *     字节流量，churn 型负载净余量近零，用节拍保证并入周期性）、
 *     净余量 pend_bytes/pend_blocks——任一越阈值时一次性原子并入
 *     全局三原子。flush 频率 ≈ 每 64KiB 分配一次，locked 开销被
 *     摊薄三个数量级。
 *   - 跨线程释放产生负增量（A 线程分配的对象在 B 线程释放）：flush
 *     只看不变总和「全局已并入 + 各槽未并入余量 = 真 live」，单槽
 *     余量允许为负，对零泄漏判定无影响。
 *
 * peak 口径（与旧实现的有意差异）：旧口径在每次 alloc 时刻用全局
 * live_bytes 采样，是精确历史峰值；本实现只在「并入点」采样（并入后
 * 的全局 live_bytes 恰为该时刻真 live）。两次并入之间（≤64KiB 分配
 * 节拍）瞬时出现又回落、未被任何并入点覆盖的峰值可能不被捕获——
 * 即峰值精度以并入节拍为限，换取热路径零 locked。稳态存活集（含
 * churn 常驻 live）必然被周期性并入采样到，报表峰值口径仍为
 * 「全局 live_bytes 的历史峰值（并入点采样版）」。零泄漏判定不变：
 * 聚合 live_blocks==0 && live_bytes==0。
 *
 * 默认关闭报告；RIGI_RT_MEMTRACK 置位且聚合 live != 0 时向 stderr
 * 打印并 exit(1)。
 */
#ifdef _WIN32
#define _CRT_SECURE_NO_WARNINGS
#endif
#include "arc.h"

#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct
{
    size_t size;
    size_t pad;
} RigiMemHdr;

/* mimalloc 分配面（GC Phase 2）：extern 声明而非 #include——rigi_rt 编译期
 * 无需 mimalloc 头，符号由链接期静态库满足（见文件头注释）。只取 track 对
 * 实际用到的两个符号；mi_malloc_aligned 的 16B 对齐维持旧 UCRT malloc 口径 */
extern void *mi_malloc_aligned(size_t size, size_t alignment);
extern void mi_free(void *p);

/* 并入阈值（GC Phase 1.4）：节拍 64KiB 分配流量；净余量与块数做负向
 * 与 size-0 分配洪泛的兜底（turnover 只计流量，堵不住单向 free 洪泛） */
#define RIGI_MT_FLUSH_TURNOVER ((int64_t)1 << 16) /* 64 KiB 分配节拍 */
#define RIGI_MT_FLUSH_BLOCKS ((int64_t)1 << 16)   /* 64K 块净余量兜底 */

/* 全局三原子：只承载已并入部分，flush/报表外无热点访问 */
static _Atomic int64_t rigi_live_blocks = 0;
static _Atomic int64_t rigi_live_bytes = 0;
static _Atomic int64_t rigi_peak_bytes = 0;

/* 槽注册表：每执行线程懒认领一枚 cache-line 独占槽（认领后常驻；
 * 本运行时线程集 = main + 有界 Worker + GC 承载线程，256 槽上限
 * 足够，耗尽即亮红灯）。sizeof==64 + 数组基址 64 对齐 ⇒ 每槽独占
 * 一 cache line；认领标记与余量同线（认领是一次性冷路径 CAS，稳态
 * 无人再写 claimed，无 false sharing 实害）。
 *
 * pend_* / turnover 为 plain 字段：仅属主线程读写。跨线程只读发生在
 * 报表/诊断聚合——本运行时 atexit 序（shim.c main：LIFO = worker
 * 清理（uv_thread_join）→ gc_shutdown（join GC 承载线程）→ gexc_flush
 * → mem_report）保证 mem_report 执行时全进程只剩主线程，join 自带
 * happens-before，plain 读无数据竞争；rigi_mem_live_* 中途读为诊断
 * 口径（当前无调用方），见对应注释。 */
#define RIGI_MT_MAX_SLOTS 256
typedef union
{
    struct
    {
        int64_t pend_blocks; /* 未并入余量：可为负（跨线程释放） */
        int64_t pend_bytes;
        int64_t turnover; /* 自上次并入的分配字节流量（并入节拍） */
        _Atomic uint32_t claimed;
    } s;
    char pad[64];
} MtSlot;
static _Alignas(64) MtSlot mt_slots[RIGI_MT_MAX_SLOTS];
static _Thread_local MtSlot *mt_my_slot = NULL;

static MtSlot *mt_claim(void)
{
    uint32_t i;
    for (i = 0; i < RIGI_MT_MAX_SLOTS; i++)
    {
        uint32_t expected = 0;
        if (atomic_compare_exchange_strong_explicit(
                &mt_slots[i].s.claimed, &expected, 1u,
                memory_order_acq_rel, memory_order_relaxed))
        {
            /* claimed 后槽余量字段仍为静态零初始化值，无需再清 */
            mt_my_slot = &mt_slots[i];
            return mt_my_slot;
        }
    }
    fprintf(stderr, "rigi_rt: memtrack 槽注册表耗尽（>%u 执行线程）\n",
        (unsigned)RIGI_MT_MAX_SLOTS);
    abort();
}

/* 认领快路径：TLS 指针命中 = 一次普通 load */
static MtSlot *mt_my(void)
{
    MtSlot *s = mt_my_slot;
    return s != NULL ? s : mt_claim();
}

/* 并入（仅属主线程调用）：先清槽后并全局——若异常退出（worker 线程
 * exit(1) 直达 atexit）时恰有并发活动线程，中间态只会少并（全局暂
 * 缺本次增量）不会多并（把已清零余量重复计入），不会假报泄漏 */
static void mt_flush(MtSlot *s)
{
    int64_t b = s->s.pend_blocks;
    int64_t by = s->s.pend_bytes;
    s->s.pend_blocks = 0;
    s->s.pend_bytes = 0;
    s->s.turnover = 0;
    if (b != 0)
    {
        atomic_fetch_add_explicit(&rigi_live_blocks, b, memory_order_relaxed);
    }
    if (by != 0)
    {
        int64_t live = atomic_fetch_add_explicit(&rigi_live_bytes, by,
            memory_order_relaxed) + by;
        int64_t peak = atomic_load_explicit(&rigi_peak_bytes,
            memory_order_relaxed);
        while (peak < live && !atomic_compare_exchange_weak_explicit(
            &rigi_peak_bytes, &peak, live, memory_order_relaxed,
            memory_order_relaxed)) { }
    }
}

/* 阈值判定（属主本地 plain 读，分支全预测不中，单 op 成本≈1 cycle） */
static void mt_maybe_flush(MtSlot *s)
{
    if (s->s.turnover >= RIGI_MT_FLUSH_TURNOVER
        || s->s.pend_bytes <= -RIGI_MT_FLUSH_TURNOVER
        || s->s.pend_blocks >= RIGI_MT_FLUSH_BLOCKS
        || s->s.pend_blocks <= -RIGI_MT_FLUSH_BLOCKS)
    {
        mt_flush(s);
    }
}

/* 聚合（静默期口径）：全局已并入 + 各已认领槽的未并入余量 = 真 live。
 * 契约：atexit 报表路径全线程已 join（见槽注册表注释），plain 读无
 * 竞争；rigi_mem_live_* 中途诊断读仅 advisory（数值为采样瞬间的
 * 一致性快照下界，无调用方）。 */
static void mt_aggregate(int64_t *blocks, int64_t *bytes)
{
    uint32_t i;
    *blocks = atomic_load_explicit(&rigi_live_blocks, memory_order_relaxed);
    *bytes = atomic_load_explicit(&rigi_live_bytes, memory_order_relaxed);
    for (i = 0; i < RIGI_MT_MAX_SLOTS; i++)
    {
        if (atomic_load_explicit(&mt_slots[i].s.claimed,
            memory_order_relaxed))
        {
            *blocks += mt_slots[i].s.pend_blocks;
            *bytes += mt_slots[i].s.pend_bytes;
        }
    }
}

void *rigi_track_malloc(size_t size)
{
    /* GC Phase 2：mi_malloc_aligned 替换 UCRT malloc。RigiMemHdr=16B，
     * total 头+载荷整体 16B 对齐分配 ⇒ 返回的「头后指针」亦 16B 对齐
     * （对象 16B 对齐契约，见文件头）。失败语义不变：环境耗尽 abort */
    /* GC Phase 2：mi_malloc_aligned 替换 UCRT malloc。RigiMemHdr=16B，
     * total 头+载荷整体 16B 对齐分配 ⇒ 返回的「头后指针」亦 16B 对齐
     * （对象 16B 对齐契约，见文件头）。失败语义不变：环境耗尽 abort */
    RigiMemHdr *hdr = (RigiMemHdr *)mi_malloc_aligned(sizeof(RigiMemHdr) + size, 16);
    MtSlot *s;
    if (hdr == NULL)
    {
        fprintf(stderr, "rigi_rt: out of memory (track_malloc %zu)\n", size);
        abort();
    }
    hdr->size = size;
    hdr->pad = 0;
    /* 热路径：本槽 plain 累加，零 locked、零共享行写（GC Phase 1.4） */
    s = mt_my();
    s->s.pend_blocks += 1;
    s->s.pend_bytes += (int64_t)size;
    s->s.turnover += (int64_t)size;
    mt_maybe_flush(s);
    return (void *)(hdr + 1);
}

void rigi_track_free(void *p)
{
    RigiMemHdr *hdr;
    MtSlot *s;
    size_t size;
    if (p == NULL)
    {
        return;
    }
    hdr = ((RigiMemHdr *)p) - 1;
    size = hdr->size;
    memset(p, 0xDD, size);
    /* 跨线程释放：在「属主」视角记负增量，flush 只看总和（可负） */
    s = mt_my();
    s->s.pend_blocks -= 1;
    s->s.pend_bytes -= (int64_t)size;
    s->s.turnover += (int64_t)size;
    mt_maybe_flush(s);
    /* GC Phase 2：mi_free 与 mi_malloc_aligned 配对归还（mimalloc 按块首
     * 元数据识别 size class，无需本层传 size） */
    mi_free(hdr);
}

void rigi_mem_report(void)
{
    int64_t blocks;
    int64_t bytes;
    if (getenv("RIGI_RT_MEMTRACK") == NULL)
    {
        return;
    }
    mt_aggregate(&blocks, &bytes);
    if (blocks != 0 || bytes != 0)
    {
        fprintf(stderr, "rigi_rt: memory leak: %lld blocks, %lld bytes\n",
            (long long)blocks, (long long)bytes);
        exit(1);
    }
}

/* 通用只读运行期台账：压力测试区分运行期回收与退出清理。
 * 口径 = 全局已并入 + 各槽未并入余量（聚合函数注释的静默期/advisory
 * 契约）；peak 为并入点采样口径（文件头注释）。当前预留接口无调用方，
 * 行为与旧全局原子版对齐以便将来接入。 */
int64_t rigi_mem_live_bytes(void)
{
    int64_t blocks;
    int64_t bytes;
    mt_aggregate(&blocks, &bytes);
    return bytes;
}
int64_t rigi_mem_live_blocks(void)
{
    int64_t blocks;
    int64_t bytes;
    mt_aggregate(&blocks, &bytes);
    return blocks;
}
int64_t rigi_mem_peak_bytes(void)
{ return atomic_load_explicit(&rigi_peak_bytes, memory_order_relaxed); }
