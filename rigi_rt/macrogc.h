/*
 * macrogc.h（MW12）：macroGC —— 候选驱动的 Bacon-Rajan 循环收集器 +
 * RUNTIME §23 ownership fence。
 *
 * 职责边界：
 *   - 候选账本：release 路径波及的 may-cycle 对象登记为候选（同一对象
 *     一轮只计一次，颜色位即账本在册标记；Phase 1.3 起账本持 +1 强引用
 *     ——PURPLE 在册 ⟹ rc = U + 1，终态释放与账本零交互），按对象体积
 *     累计全局原子债务（RUNTIME §22.3）；
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
 *   [3]    Phase 3a 会计模式位（split-heap，RIGI_PF_SHARED_ACCOUNTING）
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

/* Phase 3a（GC_OPTIMIZATION_PLAN §2.1）：每实例会计模式位。1 = shared
 * 会计，0 = local 会计（atomicfix 起两路 rc 操作同为原子 RMW）。分配
 * 点按 TypeSheet typeFlags 静态判定置位（rigi_pf_accounting_init：
 * shared/array 类型实例恒置位，local 类型实例不置）；3b Handle 锚定 /
 * 3c 失败 Task 异常图 promotion 经 rigi_gc_promote_shared_accounting
 * 冷路径翻位（local→shared 单调）。
 * Phase 3d-1 曾按两路分道（local = 非原子 rc + per-协程候选账本 +
 * 属主协作收集；shared = pin-before-sub 原子协议 + 全局候选账本 +
 * macroGC pass）。atomicfix（2026-09）定案：local 会计同样原子化，
 * release 两路统一收口 rigi_gc_release_shared（同协议同全局账本）——
 * 本位不再决定计数是否原子，保留用于局部收集/账本归属判别（mutator
 * 登记面已停用、机制封存待重启，见文件尾 3d-1 段）与 promote/detach
 * 的账本归属防御。既有 packedFlags 改写面（gc_flags_replace 只碰
 * COLOR/INDEX mask、disposed 的 fetch_or 0x4）均不触碰本位。 */
#define RIGI_PF_SHARED_ACCOUNTING 0x8u

/* Phase 3a：分配时会计模式静态判定（rigi_alloc / rigi_alloc_contiguous
 * 两个对象分配入口共用；口径与 ref 面 rigi_ref_acquire/ref_release 的
 * SHARED|ARRAY 分流一致——数组/Span 生命周期保守走原子 RC） */
static inline uint32_t rigi_pf_accounting_init(uint32_t typeFlags)
{
    return (typeFlags & (RIGI_TYPE_SHARED | RIGI_TYPE_ARRAY)) != 0
        ? RIGI_PF_SHARED_ACCOUNTING : 0u;
}

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

/* release 路径钩子（region 内调用，Phase 1.3 候选强引用协议）：
 * 账本持有候选的 +1 强引用——PURPLE 在册 ⟹ rc = U + 1，登记窗口对象必
 * 活。release_shared 是 pin-before-sub 完整协议：叶类型与单引用终态
 * （old==1/old==2 无账本）免锁；在册对象 PURPLE 快路径免 pin 直减
 * （原子 sub），old==2（最后一次用户引用）进锁摘除候选并归还账本
 * 引用后立即终态析构（确定性语义同旧协议），old ≥ 3 免锁；非终态
 * 经 note_release
 * 登记（锁外判定 + 锁内 append/置位/债务/唤醒，返回 1 = 本次完成
 * 登记、调用方 pin 转账本引用）。债务累计 + 阈值比较 + 原子 pending
 * 去重后触发 GCWakeAlarm（§22.3/§23.5 同 region）。 */
int rigi_gc_note_release(void *object, const RigiTypeSheet *sheet);
uint32_t rigi_gc_release_shared(void *object);

/* 候选摘除（swap-remove + 债务回减），返回 1 = 本次摘除、0 = 不在册。
 * Phase 1.3 起的 mutator 调用方是 release_shared 的在册终态路径（PURPLE
 * 快路径 old==2 → 摘除 + 归还账本引用 → 立即终态析构，确定性释放语义
 * 不变）；rigi_destruct 不再无条件调用（非在册析构对它是 provably
 * no-op）。非候选零开销（颜色位判别）。 */
int rigi_gc_forget_candidate(void *object);

/* Phase 3a：local → shared 会计提升原语（§2.1 会计模式位 / §2.3 失败
 * Task 异常图 promotion）。原子 fetch_or 置位（release 序，翻位前本
 * 线程的全部写入对「读到置位后」的线程可见——读方需 acquire 语义或
 * 经既有 alarm/dispatch 通道的 happens-before）。冷路径专用：3b Handle
 * 锚定（属主终止过户）/ 3c 失败发布点翻图。3d-1 起有调用方：
 * rigi_gc_promote_subgraph（见下）。
 * 单调不回落：对象一旦 shared 会计永不回 local（3d-1 原案理由是
 * 「非原子会计无法事后追溯重建」；atomicfix 后 local 会计虽已原子化，
 * 单调不回落仍是「局部账本在册 ⟹ local 会计」账本归属判别与图不变
 * 量的前提）。前置协议（3b/3c 落实时必须成立）：翻位瞬间该对象 rc
 * 只被属主线程触碰，翻位与其后的首次非属主触碰之间的同步由发布方
 * 保证。 */
void rigi_gc_promote_shared_accounting(void *object);

/* Phase 3c：子图 shared 会计提升 walker（共用面；shell.c 属主终止
 * teardown / failreg.c 失败发布点共用）。从一枚根胖引用（tag2 对象 /
 * tag1 堆值盒）出发，显式栈（防深递归爆栈）+ visited 集合（环断 +
 * 跨根共享子图去重）展开 refMap 可达图，逐 GC 对象调
 * rigi_gc_promote_shared_accounting（fetch_or 单调置位，已 shared 实例
 * 幂等）。只翻位不改 rc、不取任何锁、不进 region/fence——GC 冻结期
 * 与非冻结期调用语境均安全。数组元素表走查与本文件 gc_trace /
 * arc.c rigi_destruct 的数组分支严格同构（改结构须三侧同步）。
 * capability（.handle，refMap=0 双持有 ABI）不接壳代理边：waiter 经
 * handle_target 只能借用 target（3b-δ1 借用契约，无 acquire/release），
 * 不触碰 target rc——翻位边界 = refMap 可达集（gc_trace 的壳锚代理边
 * 是收集协议的壳锚 rc 成分计数，与此处不是同一图形语义）。
 * 冷路径，按图大小付费；调用方负责翻位前置协议（翻位瞬间该图 rc 只
 * 被属主线程触碰，且翻位完成先于任何非属主触碰——3b teardown 改指
 * 锁内 / 3c 失败发布点「节点入链前、id 未发布」语境满足）。
 * Phase 3d-1 扩展：对位未置的 local 对象，**先摘除局部候选账本条目并
 * 归还账本 +1**（rigi_gc_local_ledger_detach——归还自 atomicfix 起为
 * 原子 RMW，原「plain 操作、翻位前置协议保证此刻无并发触碰」论证
 * 退役），**再翻位**。顺序不可颠倒：detach 内部有 SHARED 防御（shared
 * 会计对象的 PURPLE 属全局账本），先翻位会让 detach 自我拦截、条目
 * 残留。此举维持 3d-1 核心不变量：「局部候选账本在册 ⟹ local 会计」
 * ——已 promote 对象绝不残留局部账本条目（局部收集器 rc 访问自
 * atomicfix 起已原子化，该不变量仍是收集语义前提）。已 shared 会计
 * 对象整只跳过（翻位幂等 + 其 PURPLE 若在则属全局账本，不得触碰）。 */
void rigi_gc_promote_subgraph(uint64_t type_id, uint64_t payload);

/* 3b-β：gc_teardown_fat 的导出包装（壳冻结期清理回调用）：白色同胞
 * 边整条跳过 + rc 原始减，不走公共 release 面（fence 冻结期会自锁）。
 * 仅供 macrogc 清理协议内的 capability 壳路径（shell.c 回调）使用。 */
void rigi_gc_raw_release_fat(uint64_t type_id, uint64_t payload);

/* ================================================================== */
/* Phase 3d-1：local 会计分道 + per-协程候选账本 + 属主协作收集         */
/* （atomicfix 定案：mutator 登记面停用，本段机制封存/保留为兜底）      */
/* ================================================================== */
/* 原案（GC_OPTIMIZATION_PLAN 3d-1）三件套：                            */
/*   ① local 会计去原子化（arc.c rigi_account_acquire/release_local     */
/*      改普通 load/store——前提「local 实例只被属主协程触碰」：类型     */
/*      系统静态划分（SYNTAX §3.1.1 闭包表 + 逃逸闸门）、3b Handle 壳  */
/*      封 capability 借用面、3c 异常图 promotion 发布前翻位）。        */
/*   ② per-协程 local 候选账本：挂 RigiCoHandle（仿 shell_registry      */
/*      先例，懒建）；无协程上下文（main 流 / globals init）落全局兜底  */
/*      账本 + 自旋锁。账本持 +1（PURPLE+索引位与全局账本共用位域）。   */
/*   ③ 属主协作收集：收集者 = 属主自己，触发 = safepoint 债务阈值 /     */
/*      协程终态 / shutdown 终轮兜底收干；算法 = gc_pass 三阶段骨架的   */
/*      局部变体（lgc_*：shared 会计子对象整条跳过染色——Bacon-Rajan    */
/*      的计数前提只对「无人并发触碰的闭包」成立）。                    */
/* atomicfix 定案（2026-09，现行）：① 的「属主独占触碰」前提被运行时   */
/* 调度面系统性实证打破——core.coroutine::CoroutineHandle 经 Task/      */
/* Dispatcher 完成链、编译器生成的闭包捕获 cell（动态命名无法静态穷举） */
/* 经 Task 闭包，均被 main/worker 两端并发触碰；plain rc 丢失更新 ⟹    */
/* rc 收支失衡 ⟹ 提前终态析构 ⟹ UAF（NativeE2E case 455，GC_OFF 100%   */
/* 复现）。定案三点：                                                   */
/*   (a) local 会计 acquire/release 全面原子化；release 统一转发        */
/*       rigi_gc_release_shared——local 与 shared 同协议（pin-before-   */
/*       sub 原子协议）同账本（macroGC 全局候选账本），候选环检测统一   */
/*       交 macroGC 全局 pass（fence 保护）。                            */
/*   (b) mutator 登记面停用：rigi_gc_local_after_release 入口短路       */
/*       （历史实现 #if 0 封存），lgc_note_locked 随之封存——局部账本    */
/*       不再新增候选；局部三阶段（lgc_*）rc 访问亦全部原子化。         */
/*   (c) 防御断言：终态析构前置断言（arc.c rigi_release_by_accounting： */
/*       终态信号 ⟹ rc==0，否则 abort）为现行汇聚防线；PURPLE detach   */
/*       的 rc==0 断言随封存体保留（局部路线重启时生效）。              */
/* 局部账本机制的保留语境（入口在、登记面停用后账本不再新增候选）：     */
/*   - shutdown 终轮收干（rigi_gc_shutdown 就地 lgc_pass 全局兜底账本， */
/*     空账本跳过）；                                                   */
/*   - 协程终态收干（rigi_gc_local_ledger_teardown，cohandle.c 协程     */
/*     销毁调用——仍负责 lgc_pass 兜底与账本结构释放）；                 */
/*   - promote detach（rigi_gc_local_ledger_detach：翻位前摘局部条目，  */
/*     维持「局部账本在册 ⟹ local 会计」不变量）；                      */
/*   - 分配尾部债务检查 / 挂出点收集（债务不再增长，实际空转）；        */
/*   - 析构中间态 guard（只剩「收集触发延迟到析构外」防御语义）。       */
/* 历史不变量存档（局部路线重启前提，详勘误见 macrogc.c 3d-1 段）：     */
/*   - 局部账本在册 ⟹ local 会计（promote 翻位同步摘条目）；            */
/*   - 终态判定只认「减量后归零 / PURPLE detach 成功归还 +1 后归零」，  */
/*     任何「减后值==1 即终态」形态都是实证过的 UAF 根因（case 455）；  */
/*   - 局部收集与 macroGC pass 经 region/fence 互斥、两收集器图不相交、 */
/*     颜色位无跨写者；                                                 */
/*   - 重启前须重新论证「属主独占」对全部 local 会计对象成立（含跨协程  */
/*     机制对象与闭包捕获 cell），并过 case 455 三环境（默认 / GC_OFF / */
/*     RIGI_RT_GC_THRESHOLD=64）并发回归。                              */

/* release local 收口——atomicfix 定案后本入口自 mutator 并发面停用：
 * arc.c rigi_account_release_local 已统一转发 rigi_gc_release_shared，
 * 本函数不再被任何调用方触达，入口短路返回 after+1。历史契约（PURPLE
 * after==1 detach 终态快路径 / 非在册登记候选 + 债务阈值就地收集）
 * 随实现 #if 0 封存于 macrogc.c（重启前提见上段与该处函数头注释）。
 * 返回值约定不变：1 = 终态析构信号（停用后不可能返回）；否则减前值
 * after+1（≥2）——绝不能把减后值透传（减后值==1 的非终态对象会被调
 * 用方误判终态析构，case 455 UAF 根因）。 */
uint32_t rigi_gc_local_after_release(void *object, uint32_t after);

/* 分配热路径债务检查（rigi_alloc 尾部调用）：当前上下文账本债务超
 * 阈值时就地跑一轮属主收集（兜底「只有分配没有释放」的长循环）。
 * atomicfix：mutator 登记面停用后债务不再增长，本检查实际空转，入口
 * 保留待局部收集路线重启。 */
void rigi_gc_local_maybe_collect(void);

/* Phase 3d-2：挂出点顺手小回收（cohandle.c rigi_coroutine_resume 段尾
 * 调用，YIELDED/SUSPENDED 挂出时；DONE 走终态收干）。TLS 协程账本债务
 * 超全局阈值即收一轮（完整 lgc_pass，有界；判据与 3d-1 债务阈值同口径
 * ——激进 debt>0 判据的实施验证见 macrogc.c 注释）；账本未建（从未
 * 登记候选）零开销返回。park 滞留控制：bench10 形态（挂出前债务远超
 * 阈值）在挂出瞬间清账 ⟹ parked 期间无 local 债务滞留（与终止收干的
 * 组合替代「park 闹钟唤醒」，延迟上界 = 首个超阈挂出点）。
 * atomicfix：登记面停用后债务不再增长，本入口实际空转，保留待局部
 * 收集路线重启。 */
void rigi_gc_local_suspend_collect(void);

/* 析构中间态 guard（arc.c rigi_destruct 包围置位）：析构拆边到一半的
 * 图不满足收集扫描的快照一致性——guard 期间收集触发延迟到析构外的
 * 下一次 release/alloc 检查（atomicfix：登记面停用后「登记照做」的
 * 原语义已不存在，guard 保留为触发延迟的防御位）。 */
void rigi_gc_local_guard_enter(void);
void rigi_gc_local_guard_exit(void);

/* 协程终态收干（cohandle.c rigi_ch_destroy_payload 调用，在
 * rigi_shell_owner_teardown 之前——先收干纯 local 账本，再让 promote
 * 过户翻位；atomicfix 后两步 rc 访问均已原子化，该顺序保留为「局部
 * 账本在册 ⟹ local 会计」归属判别的结构性前提）。收干 = 强制跑一轮
 * 属主收集（终态语境：属主已死，local 对象无人竞争触碰）+ 释放账本
 * 结构。
 * memtrack 零泄漏口径的协程侧兜底。
 * Phase 3d-2 批发 teardown（免环检测）评估不落地：挂起帧/壳锚/
 * CoroutineLocal 绑定三类活锚 + 死环成员互指边使「免判活整堆丢弃」
 * 两个前提均被证伪（详证见 macrogc.c 本函数注释），保留三阶段。 */
void rigi_gc_local_ledger_teardown(void *handle);

/* per-协程账本槽位（cohandle.c 协作面，RigiCoHandle 私有字段；仿
 * rigi_ch_shell_registry_load/store 先例）。 */
void *rigi_ch_local_ledger_load(void *handle);
void rigi_ch_local_ledger_store(void *handle, void *ledger);

#endif /* RIGI_MACROGC_H */
