/*
 * 协程句柄原语（MW11c 棒3，RUNTIME §17.4）：薄封装 resume fn + frame，
 * 供 Rigi 世界的 Dispatcher 以「create/resume/destroy」驱动编译器状态机
 * fn。归属 Task 在 Rigi 世界；跨协程只传 NativeRc token/Carriage，
 * 不传裸 RigiCoHandle 指针。MW11a 的
 * RigiCoroutine 七态实体已随棒5a 拆除：
 * 本面只管「跑一个执行段」，状态机与调度语义全在 Rigi 世界。
 * 纯 C11，双编译形态真实现（零 uv 依赖）；台账配对（track_malloc/free）。
 */
#ifndef RIGI_COHANDLE_H
#define RIGI_COHANDLE_H

#include "worker.h"

#ifdef __cplusplus
extern "C" {
#endif

/* 创建句柄：resume_fn 为编译器状态机 fn（RigiResumeFn 原型，i64 承载），
 * frame 借用（所有权留在生成代码，frame 经 rigi_alloc 分配、RcInjection
 * move 进续体）。resume_fn == 0 属编译器 bug，诊断 abort */
int64_t rigi_coroutine_create(int64_t resume_fn, int64_t frame);

/* 在当前线程跑一个执行段：TLS 当前协程槽 set/clear 包围（沿用既有
 * _Thread_local 先例，嵌套恢复保存/还原外层），返回 RigiResumeCode
 * 语义（0=SUSPENDED / 1=YIELDED / 2=DONE，coroutine.h）。handle == 0
 * 诊断 abort */
int64_t rigi_coroutine_resume(int64_t handle);

/* 终态后释放运行时持有的初始强引用（frame 不在此释放——所有权纪律
 * 同上）；若仍有 local CoroutineHandle，实体延迟到最后一份强引用
 * dispose 后销毁。handle == 0 诊断 abort。 */
void rigi_coroutine_destroy(int64_t handle);

/* ---- MW11c 棒5a：Executor lane 与 PollingAlarm 轮询状态 ---- */

/* lane 槽（0=Main / 1=Compute / 2=IO，Dispatcher 三 lane 口径）：
 * 创建时 0；eager spawn stub 置「继承调用方」、spawn-into 置「预设 ??
 * 当前」、Task.executor 换绑 setter 在下一恢复点前更新；publish/唤醒
 * 路径经 get_lane 读取。原子读写（换绑写与发布读可能跨线程）。 */
void rigi_coroutine_set_lane(int64_t handle, int32_t lane);
int32_t rigi_coroutine_get_lane(int64_t handle);

/* 当前线程正在执行的协程句柄（cohandle resume 包围维护的 TLS 槽）；
 * 无当前协程返 0（主线程根协程不经 cohandle，调用方不得依赖 0 语义） */
int64_t rigi_coroutine_current(void);

/* PollingAlarm 轮询状态（§19.2；与 VM SchedulePoll 退避同口径）：
 * arm 在 yield PollingAlarm 点调用（退避复位 1ms）；pending 查询供
 * 恢复块判「先探测再续行」；schedule 在探测未就绪时排程退避重发布
 * （定时器回调只重发布，isReady 恒由恢复块的 $mw.poll_probe 执行）；
 * clear 在探测就绪/协程终态时解除（定时器取消+销毁，状态清零）。
 * 定时器属主 = 调用线程附着的 Worker；跨线程清理（换绑后终态于新
 * lane Worker）属未支持形态，诊断 abort。 */
void rigi_poll_arm(int64_t handle);
int32_t rigi_poll_pending(int64_t handle);
void rigi_poll_schedule(int64_t handle);
void rigi_poll_clear(int64_t handle);

/* CoroutineLocal 绑定栈（RUNTIME §20.2）：挂在协程句柄上，跟随
 * 句柄跨 Worker 迁移，不是 OS ThreadLocal。键身份 = key 胖引用
 * payload（CoroutineLocal 对象地址）。get 出参首槽（Any 返回）。
 * inherit 从 TLS 当前协程拷有效顶到 child；无当前协程为空操作。 */
void rigi_coro_local_push(const RigiFatRef *key, const RigiFatRef *value);
void rigi_coro_local_pop(const RigiFatRef *key);
void rigi_coro_local_get(RigiFatRef *out, const RigiFatRef *key);
void rigi_coro_local_inherit(int64_t child);

#ifdef __cplusplus
}
#endif

#endif /* RIGI_COHANDLE_H */
