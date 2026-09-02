/*
 * Worker 原语族（MW11c 棒3，RUNTIME §17.4）：OS 线程抽象 + 任务交接
 * （计数信号量 + 同步互斥 + 跨线程唤醒）+ 同步 Mutex + TLS 当前上下文
 * + 时钟底座。调度策略/队列归属全在 Rigi 世界（core.coroutine 的
 * Dispatcher/Worker），native 只保留不能再降的原语。MW11c 棒5a 已拆除
 * MW11a/b 的 C 调度面（rigi_spawn / rigi_executor_run / alarm waiter）。
 * C ABI 与 stdlib core/coroutine.rg 的 priv native 声明逐一对齐（句柄 /
 * 帧 / fn 指针统一 i64 承载，§4.6）；库内面（rigi_sem_* /
 * rigi_worker_stop_requested / TLS setter）不进 stdlib，供棒4/5 接线与
 * C 冒烟使用。
 * 线程选型：uv_thread_t（libuv 形态）。理由——rigi_rt 已按
 * RIGI_HAS_LIBUV 双形态组织，uv_thread 跨 win/linux 同构且与同 loop 的
 * uv_sem/uv_mutex/uv_async/uv_timer 同源；不引 C11 threads.h 第二套线程
 * 库（clang 对 thrd_t 的支持度参差）。无 libuv 形态整面诊断 abort
 * （TLS/时钟除外——两者零 uv 依赖，双形态真实现）。
 * 交接协议（防丢唤醒）：入队 = gate 锁内尾插 → uv_sem_post → 目标 Worker
 * 若建了 loop 再 uv_async_send（唤醒可能阻塞在 uv_run 的 loop）；取任务 =
 * rigi_worker_park = uv_sem_wait → gate 锁内头出 → 返回令牌。令牌 0 保留
 * 作「唤醒但无任务」（destroy 的退出检查点），Rigi 侧不得入队 0。
 * 退出纪律：rigi_worker_destroy = stop 置位 + post/async 唤醒 + join +
 * 释放；Worker 线程体在入口 fn 返回后自行拆除 loop（残余 timer
 * stop/close/flush、async close、uv_loop_close）——loop 的一切操作恒在
 * 属主线程，跨线程只许 uv_async_send（libuv 唯一线程安全面）。
 * 台账纪律：Worker/队列节点/信号量/同步锁全走 rigi_track_malloc 台账；
 * uv_loop/uv_async/uv_timer 内嵌于台账块（libuv 内部自管理，不单独
 * 记账）。未 destroy 的 Worker/锁由 atexit 清理链
 * 兜底（首次创建时注册，LIFO 先于 rigi_mem_report 执行）。
 */
#ifndef RIGI_WORKER_H
#define RIGI_WORKER_H

#if !defined(_WIN32) && !defined(_POSIX_C_SOURCE)
#define _POSIX_C_SOURCE 200809L
#endif

#include <stdint.h>
#include "coroutine.h"

#ifdef RIGI_HAS_LIBUV
#include <uv.h>
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct RigiWorker RigiWorker;
typedef struct RigiSem RigiSem;
typedef struct RigiTimer RigiTimer;

/* Worker 入口 fn 原型：指向编译器合成的 Rigi 入口（沿用 probe/resume 的
 * fn 指针先例）；无线程 ctx——Worker 句柄经 TLS 当前上下文面自取 */
typedef void (*RigiWorkerEntry)(void);

/* 定时器回调原型：指向 Rigi 侧响铃处理入口（合成 fn，沿用同一 fn 指针
 * 先例）；ctx 为创建时登记的 i64 透传槽；在属主 Worker 的 loop 线程触发 */
typedef void (*RigiTimerCallback)(void *ctx);

/* ---- stdlib 对齐的 native 面（core/coroutine.rg priv native） ---- */

/* 起 OS 线程并立即进入入口 fn；返回 Worker 句柄。entry_fn == 0 =
 * 约定入口：生成代码导出的 rigi_dispatcher_entry（Dispatcher
 * workerLoop 包装，shim.c rigi_entry 先例的固定导出符号） */
int64_t rigi_worker_create(int64_t entry_fn);

/* 优雅退出：stop 置位 + 双通道唤醒（sem_post + uv_async_send）+ join +
 * 释放全部资源；句柄随后失效。worker == 0 诊断 abort（主 Worker 无线程，
 * 不可 destroy） */
void rigi_worker_destroy(int64_t worker);

/* 入队任务令牌并跨线程唤醒（gate 锁内尾插 → sem_post → 建了 loop 再
 * uv_async_send）；worker == 0 = 主 Worker（内建实例） */
void rigi_worker_enqueue(int64_t worker, int64_t task);

/* park = 阻塞取任务；返回任务令牌，0 = 唤醒无任务（退出检查点，见头
 * 注释交接协议）。worker == 0 = 主 Worker。棒5a 起 park 同时泵属主
 * loop（定时器底座）：已建 loop 时「sem_trywait + UV_RUN_ONCE」循环
 *（enqueue 的 async_send 唤醒 uv_run），未建 loop 时纯 sem_wait
 *（无 loop 即无定时器）。主 Worker 首次 park 懒建 loop + 死锁看门狗
 *（棒5a：见 .c 注释） */
int64_t rigi_worker_park(int64_t worker);

/* 同步 Mutex 原语：仅供 Dispatcher 内部队列一致性，任何路径不得跨挂起
 * 点持有，与语言级异步 Mutex（§19.6）严格区分；非重入。create 返回
 * 句柄；acquire/release 传 0 诊断 abort */
int64_t rigi_sync_mutex_create(void);
void rigi_sync_mutex_acquire(int64_t mutex);
void rigi_sync_mutex_release(int64_t mutex);

/* TLS 当前上下文：返回当前线程附着的 Worker 句柄（Worker 线程体入口
 * 前附着、出口后摘除）；主线程/未附着线程返回 0。当前 Task/协程句柄
 * 槽为库内面（下），棒4 接线 */
int64_t rigi_tls_current_context(void);

/* 时钟底座（§19.7 DateTime.now）：UTC epoch 毫秒。双形态真实现 */
int64_t rigi_time_now(void);

/* ---- 定时器原语（stdlib 对齐；§19.4/§19.5 时钟底座） ---- */

/* 创建即排程（对齐 stdlib Timer「构造即排程」）：挂到 owner Worker 的
 * uv loop（懒建），delay_ms<=0 立即触发（下一 loop 迭代，沿用 VM
 * Arm(<=0) 口径），repeat_ms>0 用 libuv 原生 repeat。
 * callback_fn != 0：纯回调形态（轮询退避等库内用途），ctx 透传。
 * callback_fn == 0：EventAlarm 响铃形态（棒5a，§19.3/§19.5）——
 * ctx 承载重复配置（0=NoRepeat 单次，-1=InfiniteRepeat 无限，
 * n=Repeat 有限次数）；响铃 = 闸内排空 waiter 链 + 经导出符号
 * rigi_dispatch_publish 逐个重发布进 Rigi Dispatcher；未耗尽的
 * 重复闹钟清 signaled 自动重排，耗尽/单次后置 signaled 粘滞
 * （VM VmDispatch.RingTimer 同口径）。owner == 0 = 主 Worker。
 * 纪律：仅允许在属主 Worker 线程调用（主 Worker = 主线程；TLS
 * 校验，跨线程属 bug 诊断 abort）——libuv 句柄操作恒在属主线程，
 * 跨线程只许 uv_async_send。 */
int64_t rigi_timer_create(int64_t owner, int64_t delay_ms,
    int64_t repeat_ms, int64_t callback_fn, int64_t ctx);

/* 停止后续触发（一次性与重复通用；句柄仍有效，可 destroy）。仅属主
 * 线程可调 */
void rigi_timer_cancel(int64_t timer);

/* 停止 + uv_close + NOWAIT flush + 释放（alarm.c 析构先例：close 回调
 * 在 flush 的 closing 阶段同步跑完后内存才释放）；幂等。仅属主线程
 * 可调 */
void rigi_timer_destroy(int64_t timer);

/* EventAlarm waiter 登记（棒5a，§19.3 原子握手；生成代码 yield
 * EventAlarm 点调用）：未触发 → 闸内 waiter 尾插登记，返 1（调用方
 * 随后结束执行段 ret SUSPENDED）；已触发（粘滞）→ 不登记，返 0
 *（调用方自行重发布自己，执行段仍结束）。闹钟定时器块在响铃耗尽后
 * 不立即回收（waiter 可能持句柄迟到登记——读 signaled 粘滞位），
 * 统一由属主 Worker 收尾清扫/主 Worker shutdown 回收。 */
int32_t rigi_alarm_wait(int64_t timer, int64_t waiter);

/* 手动 EventAlarm（MW11d-C 消息可得）：无 uv_timer，自动复位握手。
 * create 计入 armed（死锁看门狗）；signal = 有 waiter 则排空发布，
 * 否则置 signaled；wait 经 rigi_alarm_wait 消费 signaled。
 * destroy 不走 live_timers 清扫，由队列回收显式调用。 */
int64_t rigi_event_create(void);
void rigi_event_signal(int64_t ev);
void rigi_event_destroy(int64_t ev);

/* 主 Worker 收尾（rigi_entry 在 Dispatcher workerLoop 返回后调用）：
 * 交接队列残余令牌排空（quiescence 直返时 noteTerminal 末次唤醒
 * 可能未 park 消耗；主 Worker 不经 destroy）+ 残余定时器
 * stop/close/flush/释放 + 看门狗拆除 + loop close。
 * 两种编译形态均提供（无 libuv 为空操作） */
void rigi_main_worker_shutdown(void);

/* ---- 库内面（不进 stdlib；冒烟与棒4/5 接线用） ---- */

/* 计数信号量（交接原语独立形态，供未来 Rigi Dispatcher 直接组合；
 * uv_sem_t 薄封装，选型同 Worker 线程） */
RigiSem *rigi_sem_create(uint32_t initial);
void rigi_sem_post(RigiSem *sem);
void rigi_sem_wait(RigiSem *sem);
void rigi_sem_destroy(RigiSem *sem);

/* 同步锁显式析构（冒烟用；真实程序未显式析构的锁由 atexit 兜底） */
void rigi_sync_mutex_destroy(int64_t mutex);

/* 退出检查点查询：Worker 入口循环每轮（含 park 返 0 时）应查；
 * 置位后入口 fn 应尽快返回，线程体随后自拆 loop */
int rigi_worker_stop_requested(RigiWorker *worker);

/* TLS 库内槽：当前协程句柄（cohandle.c resume 包围 set/clear）与
 * 当前 Task 胖引用槽（棒4 接线；set/get 配对，借用语义不动计数） */
void rigi_tls_set_coroutine(void *handle);
void *rigi_tls_get_coroutine(void);
void rigi_tls_set_task(const RigiFatRef *task);
void rigi_tls_get_task(RigiFatRef *out);

/* 库内：Worker 线程内懒建/取本 Worker 的 uv loop（timer 创建面用；
 * 仅属主线程可调，跨线程调用属 bug）——仅 libuv 形态 */
#ifdef RIGI_HAS_LIBUV
uv_loop_t *rigi_worker_loop_ensure(RigiWorker *worker);
uv_loop_t *rigi_worker_loop(RigiWorker *worker);

/* 泵属主 loop 一轮（block!=0 为 UV_RUN_ONCE 阻塞至最近事件，否则
 * UV_RUN_NOWAIT；loop 未建 / 阻塞泵但无活跃句柄时直返 0 防空转）。
 * 供 Rigi Dispatcher 循环与 C 冒烟驱动；仅属主线程，跨线程诊断 abort */
int rigi_worker_loop_pump(RigiWorker *worker, int block);
#endif

/* 库内钩子（timer 原语 → worker.c 线程体收尾）：残余定时器
 * stop/close/flush（alarm.c 残余 timer 清扫先例）；仅属主线程调用。
 * 两种编译形态均提供（无 libuv 为空操作） */
void rigi_worker_timer_sweep(RigiWorker *worker);

/* 库内统计钩子（棒5a 死锁看门狗依据；cohandle.c 的 create/destroy/
 * resume 包围调用；双形态真实现） */
void rigi_stat_note_create(void);
void rigi_stat_note_destroy(void);
void rigi_stat_note_resume_begin(void);
void rigi_stat_note_resume_end(void);

#ifdef __cplusplus
}
#endif

#endif /* RIGI_WORKER_H */
