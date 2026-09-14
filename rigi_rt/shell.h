/*
 * shell.h（GC Phase 3b-β，δ2 属主化改线）：Handle 壳（capability
 * shell）注册表、归零转移与属主挂起栈消化面。
 *
 * 职责边界（GC_OPTIMIZATION_PLAN Phase 3b，属主计数壳模型）：
 *   - 壳以 shellID 为身份，锚定一枚 target 胖引用（anchor 引用，make
 *     时恰好一次 acquire）；count = 存活 capability 对象数（原子 i64）。
 *     capability 对象双持有 {壳指针, shellID}（48B 固定 ABI，refMap=0
 *     ——锚引用由壳持有，capability 不再持受管引用），resolve/increment
 *     经壳指针直达（count ≥ 1 ⟹ 壳必活，无注册表查找）；shellID 仅作
 *     挂起栈载荷与注册表查找键。
 *   - 减量通道（裁定 #2）：非属主线程 capability 析构 → 经壳指针
 *     fetch_sub；old > 1 直接完事（无投递）；old == 1（归零转移）才
 *     委托释放——δ2 起投递面为 per-属主挂起栈（MPSC 无锁侵入式栈，
 *     挂 RigiCoHandle 槽位），清理收敛到属主执行槽。
 *   - 注册表（裁定 #4 形态）：全局兜底注册表升格「全员索引」（shellID
 *     → 壳唯一真存储）+ per-协程属主分组表（挂 RigiCoHandle，teardown
 *     遍历用）；两张表统一由全局自旋锁保护。锁内读分组表指针一律经壳
 *     的 owner_reg，与 teardown 的整组改指、注册表本体释放串行。
 *   - 会计位兼容：壳的 anchor release 与 target 的
 *     RIGI_PF_SHARED_ACCOUNTING 位（Phase 3a）分流兼容——未提升时
 *     local 会计（原子实现，行为安全），过户 promote 后 shared 原子。
 *
 * Phase 3b-δ2 = 壳释放属主化（封口 β 的「模型纯度让步点」——原释放
 * 消息经 rigi_dispatch_publish 投递、publishNative 分支在发布者线程
 * 同步归零清理；anchor release 落在发布者线程，3d 非原子 local 会计
 * 上线后即为竞态源。δ2 起归零转移改为：shellID 压入属主挂起栈 →
 * 属主互斥执行槽内 rigi_shell_drain_pending 消化；发布者线程对壳
 * 只做 count 减量与压栈，绝不再触碰壳清理）：
 *   - 压栈方（任意线程）：native_rc_retain 验活 pin 住属主句柄 →
 *     CAS 压栈（release）；验活失败 = 属主已死 → 转兜底内核
 *     （sh_release_by_id：未过户属主死 = 补 promote + 清理，β 裁定二
 *     逻辑保留）。pin 成功 ⟹ teardown 未启动（native_rc 归零 release
 *     先锁内摘 entry 再回调 destroy_payload），压栈与 teardown 互斥。
 *   - 消化方（属主互斥单消费者）：cohandle resume 门闸内段前/段后
 *     drain + teardown drain。逐枚弹栈 shellID → 查全局索引：壳已
 *     不存在 = 幂等丢弃；owner_reg 全局 = 过户壳直接清理；属主表且
 *     属主活 = 属主上下文清理（anchor release 合法触达 local rc）；
 *     属主死 = 补 promote 后清理。弹栈点互斥性：resume 持执行门闸 +
 *     Dispatcher 泵的 seq using 强引用（⟹ teardown 无法并发）；
 *     teardown 语境 token 已死（⟹ 无 resume 在途、无新压栈）。
 *   - 不设发布者唤醒：Dispatcher 的 carriage 与协程恢复严格条件门控
 *     （裸 yield/轮询自重排；await/EventAlarm 恢复被 Task 终态/信号
 *     门控，恢复块重回 wait 块经非幂等 registerWaiter——多余 carriage
 *     会造成 waiter 重复登记/续体双执行），任何「以属主 token 发额外
 *     唤醒」都破坏恢复语义。拾取延迟因此为「属主下一个执行槽或
 *     teardown」：段内释放当段尾即清；跨线程释放最迟属主下一次恢复
 *     （被门控唤醒自带 happens-before）或 teardown；count == 0 ⟹ 无
 *     capability 可再触达壳/目标，延迟只延长锚引用寿命，语义不可观测。
 *
 * 纯 C11 + <stdatomic.h>；台账配对（track_malloc/free）。
 */
#ifndef RIGI_SHELL_H
#define RIGI_SHELL_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ---- cohandle.c 协作面（3b 内部胶水，非生成代码面） ----
 * per-协程属主分组表槽位：物理字段在 RigiCoHandle（cohandle.c），
 * 本文件提供读写通道。teardown 后槽位清空、本体由 teardown 释放。 */
void *rigi_ch_shell_registry_load(void *handle);
void rigi_ch_shell_registry_store(void *handle, void *registry);

/* ---- Phase 3b-β API 面 ---- */

/* 创建壳：锚定 target 胖引用（anchor acquire 一枚 target 引用——
 * capability 生命周期内唯一一次 target acquire，裁定 #6；调用方语境 =
 * 属主线程，属主触碰 local rc 合法），count = 1，双注册（属主分组表 +
 * 全局索引表；TLS 协程为 NULL → 归属全局表）。返回壳指针（capability
 * 双持有写入用），out_shell_id 出参回 shellID（进程内全局唯一，永不
 * 复用，0 = 无效保留）。 */
void *rigi_shell_make(uint64_t target_type, uint64_t target_payload,
                      uint64_t *out_shell_id);

/* 共享计数加（asMutable 经源 capability 壳指针直达，裁定 #7：直接
 * 原子加，不新建壳、不二次 acquire）。不变量「count ≥ 1 ⟹ 壳必活」
 * 由调用方保证。 */
void rigi_shell_increment_ptr(void *shell);

/* 壳锚目标读取（handle_target 用；借用读出，调用方自行决定 acquire
 * ——β 阶段 handle_target 保持 owned acquire，3b-δ 改借用）。 */
void rigi_shell_target_of(void *shell, uint64_t *out_type,
                          uint64_t *out_payload);

/* capability 析构减量（普通期；arc.c rigi_destruct 挂点经
 * rigi_handle_capability_release 间接调用）：经壳指针 fetch_sub；
 * old > 1 直接完事；old == 1（归零转移）按归属分流——全局表壳任意
 * 线程就地清理；属主表壳压入属主挂起栈（native_rc_retain 验活 pin，
 * 验活失败 = 属主已死转兜底内核补 promote + 清理），清理在属主互斥
 * 执行槽内完成（3b-δ2 封口发布者线程清理）。 */
void rigi_shell_release_via_ptr(void *shell);

/* capability 析构减量（GC 冻结期专用；macrogc.c gc_teardown_ex 挂点
 * 调用）：不投消息（fence 冻结期公共 release 面/Dispatcher 会自锁）
 * ——就地原始清理：锁内摘双表，target 经 raw_release（macrogc 的
 * gc_teardown_fat 包装：白色同胞边整条跳过 + rc 原始减，与白色批
 * 清理协议一致）拆解，壳即放。 */
void rigi_shell_gc_release_capability(void *object,
                                      void (*raw_release)(uint64_t type_id,
                                                          uint64_t payload));

/* 3b-δ2：属主挂起栈消化（cohandle resume 段前/段后 + teardown 调用；
 * 属主互斥单消费者语境）：逐枚 CAS 弹栈 shellID → 全局索引查找 →
 * 壳已不存在 = 重复/迟到幂等丢弃；owner_reg 全局（过户壳）= 就地
 * 清理；属主表壳 = 按属主验活分流（活 = 属主上下文清理；死 = 补
 * promote 后清理）。handle 为属主 RigiCoHandle 指针（调用方保证
 * 存活：resume 门闸语境 / teardown 终态语境）。 */
void rigi_shell_drain_pending(void *handle);

/* 属主终止过户 + 挂起栈终局排空（cohandle.c rigi_ch_destroy_payload
 * 钩子调用，token 失效前——本回调语境句柄本体尚存活）：锁内摘属主
 * 分组链 + 逐壳 owner_reg 原子改指全局表 + 子图 promote（shared 会计
 * 提升，此后壳的 anchor 引用可被任意线程触碰）+ 释放注册表本体；随后
 * rigi_shell_drain_pending 排空挂起栈残留归零壳（已过户 → 全局分支
 * 就地清理——teardown 语境即属主互斥语境，3d 语义合法）。过户 + 排空
 * 后本属主名下不再有任何未决壳。 */
void rigi_shell_owner_teardown(void *handle);

#ifdef __cplusplus
}
#endif

#endif /* RIGI_SHELL_H */
