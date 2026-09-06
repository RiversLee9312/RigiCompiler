/*
 * macrogc.h（MW12）：macroGC —— 候选驱动的 Bacon-Rajan 循环收集器 +
 * RUNTIME §23 ownership fence。
 *
 * 职责边界：
 *   - 候选账本：release 路径减至非零的对象登记为候选（同一对象一轮只计
 *     一次，颜色位即账本在册标记），按对象体积累计全局原子债务
 *     （RUNTIME §22.3）；
 *   - 收集器：同步三阶段 markGray / scan / collectWhite，显式 trace 栈
 *     防深递归爆栈；白色集合先攒后统一清理，清理前对存活子引用补偿计数
 *     （MIDDLEWARE §4.5 既定偏离）；遍历按 refMap/TypeSheet 驱动，从不
 *     枚举栈/全局根；
 *   - fence：gcFlag（IDLE/STARTING/PROCESSING）+ 每执行线程一枚
 *     cache-line 独占 cFlag（IDLE/ENTERING/PROCESSING），§23.3 双重检查
 *     的 native 降级为 region_enter 内阻塞等平台事件（codegen 零变化——
 *     同步函数无法挂起，阻塞期间协程不迁移，TLS cFlag 即满足 §23.1 的
 *     Coroutine 身份要求）；GC 协程由常驻专用线程承载（内置 GC Executor
 *     的 native 形态：自带 Worker、不与用户协程共享调度容量），
 *     GCWakeAlarm/GCAlarm 降级为平台事件（sticky/原子握手/幂等，
 *     §19.3 语义），不经 Rigi Dispatcher 通道；
 *   - §25 检查：三销毁入口（microGC/microSGC rigi_destruct + macroGC
 *     清理步）都过 rigi_dispose_hook 挂点（MW12b 填充真检查）。
 *
 * 线程选型注记：worker.h 的「一律 uv_thread」纪律不适用于 GC 线程——
 * GC 不得依赖可选的 libuv（无 libuv 降级形态下 fence 仍须工作），
 * 故本文件自携 CreateThread / pthread_create 双平台薄封装。
 */
#ifndef RIGI_MACROGC_H
#define RIGI_MACROGC_H

#include "arc.h"

/* gcFlag 状态（RUNTIME §23.1）：全局唯一 */
#define RIGI_GC_IDLE       0u
#define RIGI_GC_STARTING   1u
#define RIGI_GC_PROCESSING 2u

/* cFlag 状态（每执行线程一枚，绑定当前正在执行的 Coroutine 身份） */
#define RIGI_CF_IDLE       0u
#define RIGI_CF_ENTERING   1u
#define RIGI_CF_PROCESSING 2u

/* 对象头 packedFlags 位段（arc.h RigiObjectHeader [12..16) 域）：
 *   [0..1] 颜色：BLACK/WHITE/GRAY/PURPLE（PURPLE = 候选在册）
 *   [2]    MW12b §25 disposed 状态位（dispose 进入即置位，rigi_mark_disposed）
 *   [8..32) 候选账本索引（仅 PURPLE 期间有效，swap-remove 用） */
#define RIGI_GC_COLOR_MASK  0x3u
#define RIGI_GC_BLACK       0u
#define RIGI_GC_WHITE       1u
#define RIGI_GC_GRAY        2u
#define RIGI_GC_PURPLE      3u
#define RIGI_GC_INDEX_SHIFT 8u

/* MW12b：disposed 位（对象可能 shared，读写都走原子；macrogc 既有
 * packedFlags 改写保留高位，天然不碰本位） */
#define RIGI_PF_DISPOSED    0x4u

/* 生命周期（shim.c main 启动序）：init 建 GC 线程与平台事件（幂等）；
 * shutdown 经 atexit 注册在 globals_cleanup 之后、mem_report 之前——
 * 全局槽释放产生的末批候选由终轮收集兜底，保证 memtrack 零泄漏口径。 */
void rigi_gc_init(void);
void rigi_gc_shutdown(void);

/* region 协议支撑（arc.c 最外层 enter/exit 调用）：
 * enter 做 §23.3 双重检查（必要时 ENTERING 阻塞等 GCAlarm，恢复后完整重检）；
 * exit 发布 cFlag=IDLE（fence 保证区域内引用图/RC 写入先行）。 */
void rigi_gc_region_fence_enter(void);
void rigi_gc_region_fence_exit(void);

/* release 路径钩子（region 内调用，减至非零后）：候选登记 + 债务累计 +
 * 阈值比较 + 原子 pending 去重后触发 GCWakeAlarm（§22.3/§23.5 同 region）。 */
void rigi_gc_note_release(void *object, const RigiTypeSheet *sheet);
uint32_t rigi_gc_release_shared(void *object);

/* 析构路径钩子（rigi_destruct 头部）：在册候选摘除（swap-remove + 债务
 * 回减）；非候选零开销（颜色位判别）。 */
void rigi_gc_forget_candidate(void *object);

#endif /* RIGI_MACROGC_H */
