/*
 * Worker 原语族实现（MW11c 棒3）：面契约与纪律见 worker.h 头注释。
 * 静态名一律 rigi_worker_/rigi_sem_/rigi_smutex_ 前缀（unity build
 * 单编译单元防碰撞）。
 */
#ifdef _WIN32
#ifndef _CRT_SECURE_NO_WARNINGS
#define _CRT_SECURE_NO_WARNINGS
#endif
#endif
#include "worker.h"
/* 内存台账声明必须显式可见，不能依赖 unity 中 arc.c 的排列位置。 */
#include "arc.h"

#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#else
#include <time.h>
#endif

/* ================================================================== */
/* TLS 当前上下文（双形态真实现，零 uv 依赖）                          */
/* ================================================================== */

/* 当前 Worker（Worker 线程体入口前附着、出口后摘除；主线程恒 NULL） */
static _Thread_local RigiWorker *rigi_tls_worker = NULL;
/* 当前协程句柄（cohandle.c resume 包围 set/clear） */
static _Thread_local void *rigi_tls_coroutine = NULL;
/* 当前 Task 胖引用槽（零值 = 无；棒4 接线） */
static _Thread_local RigiFatRef rigi_tls_task = { 0, 0 };

int64_t rigi_tls_current_context(void)
{
    return (int64_t)(uintptr_t)rigi_tls_worker;
}

void rigi_tls_set_coroutine(void *handle)
{
    rigi_tls_coroutine = handle;
}

void *rigi_tls_get_coroutine(void)
{
    return rigi_tls_coroutine;
}

void rigi_tls_set_task(const RigiFatRef *task)
{
    rigi_tls_task = task != NULL ? *task : (RigiFatRef){ 0, 0 };
}

void rigi_tls_get_task(RigiFatRef *out)
{
    if (out == NULL)
    {
        fprintf(stderr, "rigi_rt: rigi_tls_get_task 参数为 NULL（编译器 bug）\n");
        abort();
    }
    *out = rigi_tls_task;
}

/* ================================================================== */
/* 时钟底座（双形态真实现，零 uv 依赖）                                */
/* ================================================================== */

/* 选型：uv_now 是 loop 相对单调钟、非 epoch 且需活 loop，弃用；
 * win 用 GetSystemTimePreciseAsFileTime（亚毫秒精度、无 loop 依赖、
 * 双编译形态可用），其余平台 clock_gettime(CLOCK_REALTIME)。 */
int64_t rigi_time_now(void)
{
#ifdef _WIN32
    FILETIME ft;
    uint64_t ticks;
    GetSystemTimePreciseAsFileTime(&ft);
    ticks = ((uint64_t)ft.dwHighDateTime << 32) | (uint64_t)ft.dwLowDateTime;
    /* FILETIME 1601 纪元 → Unix 1970 纪元的 100ns 差值 */
    return (int64_t)((ticks - UINT64_C(116444736000000000)) / UINT64_C(10000));
#else
    struct timespec ts;
    if (clock_gettime(CLOCK_REALTIME, &ts) != 0)
    {
        fprintf(stderr, "rigi_rt: clock_gettime 失败（环境异常）\n");
        abort();
    }
    return (int64_t)ts.tv_sec * INT64_C(1000)
        + (int64_t)ts.tv_nsec / INT64_C(1000000);
#endif
}

/* DateTime.now 专用墙钟：一次系统采样写 12 字节 Span<u8>，不经旧
 * i64 毫秒 time_now 拼两次采样。Span<u8> ABI 同 fs.c：payload +24
 * 为 i32 长度、+32 为元素基址；越界属编译器/包装层 bug。 */
void rigi_time_now_parts(const RigiFatRef *out)
{
    if (out == NULL || out->payload == 0)
    {
        fprintf(stderr, "rigi_rt: time_now_parts 收到空 Span（编译器 bug）\n");
        abort();
    }
    uint8_t *payload = (uint8_t *)(uintptr_t)out->payload;
    int32_t length;
    memcpy(&length, payload + 24, sizeof(length));
    if (length < 12)
    {
        fprintf(stderr, "rigi_rt: time_now_parts Span 小于 12（编译器 bug）\n");
        abort();
    }
    int64_t ms;
    int32_t ns;
#ifdef _WIN32
    FILETIME ft;
    GetSystemTimePreciseAsFileTime(&ft);
    uint64_t ticks = ((uint64_t)ft.dwHighDateTime << 32)
        | (uint64_t)ft.dwLowDateTime;
    /* 1601→1970 偏置有符号运算；负时刻用 floor 商与非负余数。
     * FILETIME 100ns 粒度，乘 100 得真实纳秒余量，不声称 1ns 精度。 */
    int64_t unix100 = (int64_t)ticks - INT64_C(116444736000000000);
    ms = unix100 / INT64_C(10000);
    int64_t rem100 = unix100 % INT64_C(10000);
    if (rem100 < 0)
    {
        ms -= 1;
        rem100 += INT64_C(10000);
    }
    ns = (int32_t)(rem100 * INT64_C(100));
#else
    struct timespec ts;
    if (clock_gettime(CLOCK_REALTIME, &ts) != 0)
    {
        fprintf(stderr, "rigi_rt: time_now_parts clock_gettime 失败\n");
        abort();
    }
    if (ts.tv_nsec < 0 || ts.tv_nsec >= INT64_C(1000000000)
        || (int64_t)ts.tv_sec < INT64_MIN / INT64_C(1000) + 1
        || (int64_t)ts.tv_sec > INT64_MAX / INT64_C(1000) - 1)
    {
        fprintf(stderr, "rigi_rt: time_now_parts 宿主时间范围异常\n");
        abort();
    }
    ms = (int64_t)ts.tv_sec * INT64_C(1000)
        + (int64_t)ts.tv_nsec / INT64_C(1000000);
    ns = (int32_t)((int64_t)ts.tv_nsec % INT64_C(1000000));
#endif
    uint8_t *dst = payload + 32;
    for (int i = 0; i < 8; i++)
    {
        dst[i] = (uint8_t)((uint64_t)ms >> (i * 8));
    }
    for (int i = 0; i < 4; i++)
    {
        dst[8 + i] = (uint8_t)((uint32_t)ns >> (i * 8));
    }
}

/* ================================================================== */
/* 单调时钟底座（施工块 6-3，§4.9.4；VM VmDispatch.MonotonicNow 同语义） */
/* ================================================================== */

#ifdef _WIN32
/* QueryUnbiasedInterruptTimePrecise 实际驻留 kernelbase.dll（kernel32
 * 不导出 Precise 变体）；经 GetProcAddress 缓存解析。解析失败退回
 * QueryUnbiasedInterruptTime（kernel32，Windows 7+，0.5ms 更新批处理，
 * 单调性、100ns 单位与排除睡眠语义不变）。 */
static int64_t rigi_win_monotonic_now_ns(void)
{
    typedef void (WINAPI *RigiQubitPreciseFn)(ULONGLONG *);
    static RigiQubitPreciseFn precise_fn = NULL;
    static int lookup_done = 0;
    ULONGLONG unbiased_time = 0;
    if (!lookup_done)
    {
        HMODULE kernelbase = GetModuleHandleW(L"kernelbase.dll");
        if (kernelbase != NULL)
        {
            precise_fn = (RigiQubitPreciseFn)(void *)GetProcAddress(
                kernelbase, "QueryUnbiasedInterruptTimePrecise");
        }
        lookup_done = 1;
    }
    if (precise_fn != NULL)
    {
        precise_fn(&unbiased_time);
    }
    else
    {
        QueryUnbiasedInterruptTime(&unbiased_time);
    }
    return (int64_t)(unbiased_time * UINT64_C(100));
}
#endif

/* 单调读数（纳秒）：计入协程等待与进程未调度的时间，排除整机睡眠/休眠
 * （§4.9.4）。与 rigi_time_now（墙上 UTC 毫秒，可因校时跳变）语义不
 * 同源，两者不可互替。
 *   - Windows：QueryUnbiasedInterruptTimePrecise——自系统启动的
 *     「无偏」中断时间，100ns 单位、排除睡眠（休眠期间不推进；Precise
 *     变体绕开 0.5ms 更新批处理，读真实当前值；驻留 kernelbase.dll，
 *     运行时经 GetProcAddress 解析，缺失退回 QueryUnbiasedInterrupt
 *     Time——语义不变仅分辨率批处理）。要求 Windows 10 1607 /
 *     Server 2016+（回退路径 Windows 7+）。100ns → ns 乘 100：i64
 *     纳秒可表 ~292 年自启动时长，实际不可能触达，不设额外溢出分支。
 *   - Linux：clock_gettime(CLOCK_MONOTONIC)。man7 clock_gettime 原文
 *     「This clock does not count time that the system is
 *     suspended.」——即排除整机挂起/睡眠，恰与契约语义匹配
 *     （CLOCK_BOOTTIME 才计入挂起，不选用；挂起时间是否另计不是
 *     CLOCK_MONOTONIC 的语义）。秒 ×1e9 + 纳秒，同 ~292 年量级。
 * 实际分辨率平台相关（Windows 无偏中断时间非逐纳秒步进），契约不
 * 要求每次读取增加一纳秒（§4.9.4）。 */
int64_t rigi_monotonic_now_ns(void)
{
#ifdef _WIN32
    return rigi_win_monotonic_now_ns();
#else
    struct timespec ts;
    if (clock_gettime(CLOCK_MONOTONIC, &ts) != 0)
    {
        fprintf(stderr, "rigi_rt: clock_gettime(CLOCK_MONOTONIC) 失败（环境异常）\n");
        abort();
    }
    return (int64_t)ts.tv_sec * INT64_C(1000000000)
        + (int64_t)ts.tv_nsec;
#endif
}

/* ================================================================== */
/* 宿主平台判定原语（施工块 7-1，STDLIB §4.5.2/§4.5.9 私有原语）        */
/* ================================================================== */

/* 宿主平台判定：非 0 = Windows。core.fs Path 的平台路径词法校验内部
 * 使用（编译目标平台 == 宿主平台：native 经 _WIN32 编译期判定；VM 同
 * 进程宿主判定，VmHooks host_is_windows 镜像，两形态同语义）。
 * 公共平台信息/环境查询 API 按 D5 继续后置——本原语不构成对外平台
 * 查询入口，只是 Path 语法规则的内部实施依赖。 */
uint8_t rigi_host_is_windows(void)
{
#ifdef _WIN32
    return 1;
#else
    return 0;
#endif
}

/* ================================================================== */
/* 调度统计计数器（棒5a 死锁看门狗依据；双形态真实现，零 uv 依赖）      */
/* ================================================================== */
/* 口径（与 VM VmDispatch.IsDeadlocked 的「无 Running/Runnable 协程、
 * 无在途唤醒源」对齐）：live = 已建未销毁协程句柄数（create/destroy
 * 配对）；running = 正在执行段的协程数（resume 包围）；armed = 存活
 * 闹钟/轮询定时器数（看门狗自身除外）。三者为零且主 Worker 无任务可
 * 取 = 无可推进源。看门狗要求状态稳定持续（见 watchdog 注释），
 * 「已出队未 resume」的瞬态间隙由稳定期吸收。 */
static _Atomic int64_t rigi_stat_live = 0;
static _Atomic int64_t rigi_stat_running = 0;
static _Atomic int64_t rigi_stat_armed = 0;
static _Atomic int64_t rigi_stat_workers = 0;
static _Atomic int64_t rigi_stat_timer_bytes = 0;

/* 独立计量定时器底座的保留量，资源压力可区分它与 MQ 对象。 */
int64_t rigi_timer_live_bytes(void)
{
    return atomic_load_explicit(&rigi_stat_timer_bytes, memory_order_relaxed);
}

/* 压力入口观测：返回真实的辅助 Worker 数，不把主线程/GC 算作 Compute。 */
int64_t rigi_worker_live_count(void)
{
    return atomic_load_explicit(&rigi_stat_workers, memory_order_relaxed);
}

int64_t rigi_worker_running_count(void)
{
    return atomic_load_explicit(&rigi_stat_running, memory_order_relaxed);
}

/* cohandle.c 钩子（库内面，worker.h 声明） */
void rigi_stat_note_create(void)
{
    atomic_fetch_add_explicit(&rigi_stat_live, 1, memory_order_relaxed);
}

void rigi_stat_note_destroy(void)
{
    atomic_fetch_sub_explicit(&rigi_stat_live, 1, memory_order_relaxed);
}

void rigi_stat_note_resume_begin(void)
{
    atomic_fetch_add_explicit(&rigi_stat_running, 1, memory_order_relaxed);
}

void rigi_stat_note_resume_end(void)
{
    atomic_fetch_sub_explicit(&rigi_stat_running, 1, memory_order_relaxed);
}

/* ================================================================== */
/* 以下 Worker/信号量/同步锁面仅 libuv 形态实现；无 libuv 形态整面     */
/* 降级诊断 abort（对应 stdlib 占位「无 alarm 能力」同纪律，alarm.c    */
/* 先例）                                                              */
/* ================================================================== */
#ifdef RIGI_HAS_LIBUV

/* 默认使用全部可用处理器；显式覆盖严格限制在 1..254，非法值回退默认。 */
int32_t rigi_worker_parallelism(void)
{
    unsigned int count = uv_available_parallelism();
    const char *setting = getenv("RIGI_COMPUTE_WORKERS");
    if (setting != NULL && *setting >= '0' && *setting <= '9')
    {
        char *end;
        long parsed = strtol(setting, &end, 10);
        if (*end == '\0' && parsed >= 1 && parsed <= 254) return (int32_t)parsed;
    }
    return (int32_t)(count > 254 ? 254 : (count > 0 ? count : 1));
}

/* 任务交接 FIFO 节点（gate 锁内进出；台账配对） */
typedef struct RigiWorkNode
{
    int64_t token;
    struct RigiWorkNode *next;
} RigiWorkNode;

struct RigiWorker
{
    uv_thread_t thread;
    RigiWorkerEntry entry;        /* 合成 Rigi 入口 fn（Dispatcher 循环） */
    _Atomic int stop_requested;   /* destroy 置位，入口循环查询 */
    uv_sem_t sem;                 /* 计数信号量 = 队列长度 + 唤醒事件 */
    uv_mutex_t gate;              /* 交接队列闸 */
    RigiWorkNode *head;
    RigiWorkNode *tail;
    /* loop 懒建于 Worker 线程（首个 timer/唤醒需求时）；loop 字段指针
     * 以原子发布（release），enqueue 的跨线程 uv_async_send 侧以
     * acquire 读——句柄本体只允许属主线程触碰（uv_async_send 除外） */
    uv_loop_t loop_storage;       /* 内嵌静态存储（alarm.c 先例不经台账） */
    _Atomic(uv_loop_t *) loop;    /* NULL = 未建 / 已拆 */
    _Atomic int async_ready;      /* wake 句柄已 uv_async_init 且未 close */
    uv_async_t wake;              /* 跨线程唤醒句柄（仅属主线程初始化） */
    RigiTimer *live_timers;       /* 本 Worker 存活定时器链（仅属主线程） */
    RigiTimer *closing_timers;    /* 登记册闸保护；析构交接给属主 */
    int alarms_closing;           /* 登记册闸保护；shutdown 封闭交接 */
    struct RigiWorker *next_live; /* 全局 live 链（atexit 兜底依据） */
};

/* EventAlarm waiter 节点（响铃形态定时器的闸内链；台账配对） */
typedef struct RigiAlarmWaitNode
{
    int64_t waiter;                     /* 协程句柄（cohandle） */
    struct RigiAlarmWaitNode *next;
} RigiAlarmWaitNode;

/* 定时器实体：uv_timer 内嵌于台账块（alarm.c RigiEventAlarmRt 先例）；
 * 句柄字段仅属主 Worker 线程触碰。cb==NULL 的响铃形态持有 waiter
 * 链/signaled/剩余响铃次数（gate 护——登记与响铃的 §19.3 原子握手） */
struct RigiTimer
{
    uv_timer_t handle;
    RigiWorker *owner;
    RigiTimerCallback cb;         /* NULL = EventAlarm 响铃形态 */
    void *ctx;                    /* 响铃形态 = 重复配置（0 单/-1 无限/n） */
    int closed;                   /* destroy 幂等标记 */
    uv_mutex_t gate;              /* waiter 链/signaled/rings 闸 */
    RigiAlarmWaitNode *waiters;
    int signaled;                 /* 粘滞已触发（耗尽/单次响铃后） */
    int64_t rings_remaining;      /* -1 = 无限 */
    int armed_counted;            /* 已计入 rigi_stat_armed（看门狗） */
    int manual;                   /* 1 = 无 uv_timer 的手动 EventAlarm */
    struct RigiTimer *next_live;
    struct RigiTimer *next_registered;
    struct RigiTimer *next_close;
    int64_t identity;             /* 单调身份，不复用已失效句柄 */
};

struct RigiSem
{
    uv_sem_t sem;
    struct RigiSem *next_live;
};

typedef struct RigiSyncMutexRt
{
    uv_mutex_t mutex;
    struct RigiSyncMutexRt *next_live;
} RigiSyncMutexRt;

/* 全局 live 登记表（atexit 兜底清扫依据）：一把 uv_mutex 护四条链。
 * 初始化经 uv_once（棒5a：多 Worker 并发首建 sync mutex/Worker 时
 * 无双检竞态） */
static uv_mutex_t rigi_worker_registry_gate;
static RigiWorker *rigi_worker_live = NULL;
static RigiSem *rigi_sem_live = NULL;
static RigiSyncMutexRt *rigi_smutex_live = NULL;
static _Atomic int64_t rigi_smutex_count = 0;
/* 第四条链：全部 timer/手动事件的身份登记册。对象析构或属主
 * shutdown 摘除；next_registered 与属主 live 链独立，禁止裸指针
 * 句柄在退出后地址复用时误认新底座。 */
static RigiTimer *rigi_event_live = NULL;
static int64_t rigi_alarm_identity = 0;
static void rigi_worker_close_alarms(RigiWorker *worker);

static void rigi_worker_cleanup(void);

/* 生成代码导出符号（ModuleBuilder 恒发射；shim.c rigi_entry 先例）：
 * rigi_dispatcher_entry = Worker 线程体入口（Dispatcher workerLoop
 * 包装）；rigi_dispatch_publish = 重发布协程句柄进 Rigi Dispatcher
 *（定时器响铃/轮询退避回调用，lane 由句柄槽读取） */
extern void rigi_dispatcher_entry(void);
extern void rigi_dispatch_publish(int64_t handle);

/* 主 Worker（句柄 0）内建实例：无线程，TLS 当前上下文恒 0（主线程
 * 未附着）；loop/看门狗随首个定时器/park 懒建 */
static RigiWorker rigi_main_worker_storage;
static _Atomic int rigi_main_worker_ready = 0;

static void rigi_registry_once(void)
{
    if (uv_mutex_init(&rigi_worker_registry_gate) != 0)
    {
        fprintf(stderr, "rigi_rt: uv_mutex_init 失败（环境耗尽）\n");
        abort();
    }
    atexit(rigi_worker_cleanup);
}

/* 登记册初始化 + atexit 注册（LIFO 先于 rigi_mem_report 执行） */
static uv_once_t rigi_registry_once_ctrl = UV_ONCE_INIT;
static void rigi_worker_registry_ensure(void)
{
    uv_once(&rigi_registry_once_ctrl, rigi_registry_once);
}

/* 主 Worker 实例懒建（双检 + 登记册闸；无主初始化竞态） */
static RigiWorker *rigi_main_worker_ensure(void)
{
    if (!atomic_load_explicit(&rigi_main_worker_ready, memory_order_acquire))
    {
        rigi_worker_registry_ensure();
        uv_mutex_lock(&rigi_worker_registry_gate);
        if (!atomic_load_explicit(&rigi_main_worker_ready, memory_order_acquire))
        {
            memset(&rigi_main_worker_storage, 0,
                sizeof(rigi_main_worker_storage));
            if (uv_sem_init(&rigi_main_worker_storage.sem, 0) != 0
                || uv_mutex_init(&rigi_main_worker_storage.gate) != 0)
            {
                fprintf(stderr, "rigi_rt: 主 Worker sem/mutex_init 失败"
                    "（环境耗尽）\n");
                abort();
            }
            atomic_store_explicit(&rigi_main_worker_ready, 1, memory_order_release);
        }
        uv_mutex_unlock(&rigi_worker_registry_gate);
    }
    return &rigi_main_worker_storage;
}

/* ---- 计数信号量（库内面） ---- */

RigiSem *rigi_sem_create(uint32_t initial)
{
    RigiSem *sem;
    rigi_worker_registry_ensure();
    sem = (RigiSem *)rigi_track_malloc(sizeof(RigiSem));
    if (uv_sem_init(&sem->sem, initial) != 0)
    {
        fprintf(stderr, "rigi_rt: uv_sem_init 失败（环境耗尽）\n");
        abort();
    }
    uv_mutex_lock(&rigi_worker_registry_gate);
    sem->next_live = rigi_sem_live;
    rigi_sem_live = sem;
    uv_mutex_unlock(&rigi_worker_registry_gate);
    return sem;
}

void rigi_sem_post(RigiSem *sem)
{
    uv_sem_post(&sem->sem);
}

void rigi_sem_wait(RigiSem *sem)
{
    uv_sem_wait(&sem->sem);
}

void rigi_sem_destroy(RigiSem *sem)
{
    RigiSem **link;
    if (sem == NULL)
    {
        return;
    }
    uv_mutex_lock(&rigi_worker_registry_gate);
    for (link = &rigi_sem_live; *link != NULL; link = &(*link)->next_live)
    {
        if (*link == sem)
        {
            *link = sem->next_live;
            break;
        }
    }
    uv_mutex_unlock(&rigi_worker_registry_gate);
    uv_sem_destroy(&sem->sem);
    rigi_track_free(sem);
}

/* ---- 同步 Mutex（stdlib 面 + 库内 destroy） ---- */

int64_t rigi_sync_mutex_create(void)
{
    RigiSyncMutexRt *rt;
    rigi_worker_registry_ensure();
    rt = (RigiSyncMutexRt *)rigi_track_malloc(sizeof(RigiSyncMutexRt));
    if (uv_mutex_init(&rt->mutex) != 0)
    {
        fprintf(stderr, "rigi_rt: uv_mutex_init 失败（环境耗尽）\n");
        abort();
    }
    uv_mutex_lock(&rigi_worker_registry_gate);
    rt->next_live = rigi_smutex_live;
    rigi_smutex_live = rt;
    atomic_fetch_add_explicit(&rigi_smutex_count, 1, memory_order_relaxed);
    uv_mutex_unlock(&rigi_worker_registry_gate);
    return (int64_t)(uintptr_t)rt;
}

static RigiSyncMutexRt *rigi_smutex_of(int64_t mutex, const char *face)
{
    if (mutex == 0)
    {
        fprintf(stderr, "rigi_rt: %s 收到空句柄（编译器 bug）\n", face);
        abort();
    }
    return (RigiSyncMutexRt *)(uintptr_t)mutex;
}

void rigi_sync_mutex_acquire(int64_t mutex)
{
    uv_mutex_lock(&rigi_smutex_of(mutex, "rigi_sync_mutex_acquire")->mutex);
}

void rigi_sync_mutex_release(int64_t mutex)
{
    uv_mutex_unlock(&rigi_smutex_of(mutex, "rigi_sync_mutex_release")->mutex);
}

void rigi_sync_mutex_destroy(int64_t mutex)
{
    RigiSyncMutexRt *rt = (RigiSyncMutexRt *)(uintptr_t)mutex;
    RigiSyncMutexRt **link;
    int found = 0;
    if (rt == NULL) { return; }
    uv_mutex_lock(&rigi_worker_registry_gate);
    for (link = &rigi_smutex_live; *link != NULL; link = &(*link)->next_live)
    {
        if (*link == rt)
        {
            *link = rt->next_live;
            found = 1;
            break;
        }
    }
    uv_mutex_unlock(&rigi_worker_registry_gate);
    /* shutdown 可能先清扫登记册；迟到的对象析构不得再次解引用旧地址。 */
    if (!found) { return; }
    atomic_fetch_sub_explicit(&rigi_smutex_count, 1, memory_order_relaxed);
    uv_mutex_destroy(&rt->mutex);
    rigi_track_free(rt);
}

/* ---- Worker ---- */
int64_t rigi_sync_mutex_live_count(void)
{ return atomic_load_explicit(&rigi_smutex_count, memory_order_relaxed); }

int rigi_worker_stop_requested(RigiWorker *worker)
{
    return worker != NULL
        && atomic_load_explicit(&worker->stop_requested,
            memory_order_acquire) != 0;
}

uv_loop_t *rigi_worker_loop(RigiWorker *worker)
{
    return worker != NULL
        ? atomic_load_explicit(&worker->loop, memory_order_acquire) : NULL;
}

/* 跨线程唤醒回调在属主线程接收内部关闭请求，不执行用户代码。 */
static void rigi_worker_wake_cb(uv_async_t *handle)
{
    rigi_worker_close_alarms((RigiWorker *)handle->data);
}

/* 懒建 loop + wake 句柄（仅属主线程；首个 timer/唤醒需求时调用）。
 * 发布序：uv_async_init 成功 → async_ready 置位 → loop 指针 release
 * 发布；enqueue 侧先 acquire 读 loop 再 async_ready，双确认后才
 * uv_async_send（send 于未初始化句柄是 UB） */
uv_loop_t *rigi_worker_loop_ensure(RigiWorker *worker)
{
    uv_loop_t *loop = rigi_worker_loop(worker);
    if (loop != NULL)
    {
        return loop;
    }
    if (uv_loop_init(&worker->loop_storage) != 0)
    {
        fprintf(stderr, "rigi_rt: uv_loop_init 失败（环境耗尽）\n");
        abort();
    }
    if (uv_async_init(&worker->loop_storage, &worker->wake,
            rigi_worker_wake_cb) != 0)
    {
        fprintf(stderr, "rigi_rt: uv_async_init 失败（环境耗尽）\n");
        abort();
    }
    worker->wake.data = worker;
    atomic_store_explicit(&worker->async_ready, 1, memory_order_release);
    atomic_store_explicit(&worker->loop, &worker->loop_storage,
        memory_order_release);
    return &worker->loop_storage;
}

/* Worker 线程体收尾（属主线程，入口 fn 返回后）：残余 timer
 * stop/close/flush（timer.c 棒3 阶段2 挂接 rigi_worker_timer_sweep）、
 * async close、UV_RUN_DEFAULT flush closing、uv_loop_close、原子摘除。
 * 此刻 join 前的最后动作——此后 enqueue 侧不得再 async_send（destroy
 * 先 stop+post 再 join，join 返回即线程体全毕） */
static void rigi_worker_loop_teardown(RigiWorker *worker)
{
    uv_mutex_lock(&rigi_worker_registry_gate);
    worker->alarms_closing = 1;
    uv_mutex_unlock(&rigi_worker_registry_gate);
    rigi_worker_close_alarms(worker);
    if (rigi_worker_loop(worker) == NULL)
    {
        return;
    }
    /* 与 enqueue 的 async_send 同闸，禁止检查 ready 后撞上 uv_close。 */
    uv_mutex_lock(&worker->gate);
    atomic_store_explicit(&worker->loop, NULL, memory_order_release);
    int close_wake = atomic_exchange_explicit(&worker->async_ready, 0, memory_order_acq_rel);
    if (close_wake != 0) uv_close((uv_handle_t *)&worker->wake, NULL);
    uv_mutex_unlock(&worker->gate);
    rigi_worker_timer_sweep(worker); /* timer.c 内部钩子（无残余为空转） */
    /* timer/async 全闭，loop 无活跃句柄，flush 单迭代即返、绝不阻塞 */
    uv_run(&worker->loop_storage, UV_RUN_DEFAULT);
    uv_loop_close(&worker->loop_storage);
}

int rigi_worker_loop_pump(RigiWorker *worker, int block)
{
    uv_loop_t *loop;
    if (worker == NULL || rigi_tls_worker != worker)
    {
        fprintf(stderr, "rigi_rt: rigi_worker_loop_pump 必须在属主 Worker"
            "线程调用（跨线程操作 uv 句柄属 bug）\n");
        abort();
    }
    loop = rigi_worker_loop(worker);
    if (loop == NULL)
    {
        return 0;
    }
    /* 无活跃句柄时 UV_RUN_ONCE 立即返回会造成空转，先查（alarm.c
     * drain 同款纪律） */
    if (block != 0 && uv_loop_alive(loop) == 0)
    {
        return 0;
    }
    return uv_run(loop, block != 0 ? UV_RUN_ONCE : UV_RUN_NOWAIT);
}

/* ---- 定时器原语（仅属主线程；uv_timer 内嵌台账块） ---- */

/* 属主解析：owner 句柄 0 = 主 Worker 内建实例（主线程 TLS 未附着 = NULL
 * ⇔ 主 Worker） */
static RigiWorker *rigi_worker_resolve(int64_t worker)
{
    return worker == 0
        ? rigi_main_worker_ensure()
        : (RigiWorker *)(uintptr_t)worker;
}

/* 当前线程附着的 Worker（主线程 = 主 Worker 实例） */
static RigiWorker *rigi_tls_worker_or_main(void)
{
    return rigi_tls_worker != NULL
        ? rigi_tls_worker
        : rigi_main_worker_ensure();
}

/* 属主线程校验：timer 面一切 uv 操作必须在属主 Worker 的 loop 线程 */
static RigiWorker *rigi_timer_owner_check(RigiWorker *owner,
    const char *face)
{
    if (owner == NULL)
    {
        fprintf(stderr, "rigi_rt: %s 收到空 owner（编译器 bug）\n", face);
        abort();
    }
    if (rigi_tls_worker_or_main() != owner)
    {
        fprintf(stderr, "rigi_rt: %s 必须在属主 Worker 线程调用（跨线程"
            "操作 uv 句柄属 bug）\n", face);
        abort();
    }
    return owner;
}

/* 响铃（cb==NULL 的 EventAlarm 形态，属主 loop 线程）：闸内排空
 * waiter 链 + 剩余次数递减；耗尽（含单次）置 signaled 粘滞并停后续
 * 触发（uv_timer_stop 在回调内合法）；waiter 在闸外经导出符号
 * rigi_dispatch_publish 逐个重发布（VM RingTimer 同口径：先排空再
 * 发布）。定时器块不随响铃回收（迟到 yield 读 signaled 粘滞位），
 * 由属主收尾清扫统一释放。 */
static void rigi_timer_alarm_ring(RigiTimer *timer)
{
    RigiAlarmWaitNode *waiters;
    uv_mutex_lock(&timer->gate);
    waiters = timer->waiters;
    timer->waiters = NULL;
    if (timer->rings_remaining > 0)
    {
        timer->rings_remaining--;
    }
    if (timer->rings_remaining == 0)
    {
        timer->signaled = 1;
        uv_timer_stop(&timer->handle);
        if (timer->armed_counted)
        {
            timer->armed_counted = 0;
            atomic_fetch_sub_explicit(&rigi_stat_armed, 1,
                memory_order_relaxed);
        }
    }
    uv_mutex_unlock(&timer->gate);
    while (waiters != NULL)
    {
        RigiAlarmWaitNode *next = waiters->next;
        rigi_dispatch_publish(waiters->waiter);
        rigi_track_free(waiters);
        waiters = next;
    }
}

/* 到期回调（属主 loop 线程）：纯回调形态直接调库内 C 回调；响铃形态
 * 走 rigi_timer_alarm_ring */
static void rigi_timer_fire_cb(uv_timer_t *handle)
{
    RigiTimer *timer = (RigiTimer *)handle->data;
    if (timer->cb != NULL)
    {
        timer->cb(timer->ctx);
    }
    else
    {
        rigi_timer_alarm_ring(timer);
    }
}

int64_t rigi_timer_create(int64_t owner, int64_t delay_ms,
    int64_t repeat_ms, int64_t callback_fn, int64_t ctx)
{
    RigiWorker *w = rigi_timer_owner_check(
        rigi_worker_resolve(owner), "rigi_timer_create");
    uv_loop_t *loop = rigi_worker_loop_ensure(w);
    RigiTimer *timer =
        (RigiTimer *)rigi_track_malloc(sizeof(RigiTimer));
    atomic_fetch_add_explicit(&rigi_stat_timer_bytes, sizeof(RigiTimer), memory_order_relaxed);
    memset(timer, 0, sizeof(*timer));
    timer->owner = w;
    timer->cb = (RigiTimerCallback)(uintptr_t)callback_fn;
    timer->ctx = (void *)(uintptr_t)ctx;
    timer->closed = 0;
    timer->waiters = NULL;
    timer->signaled = 0;
    timer->manual = 0;
    /* 响铃形态：ctx = 重复配置（0 单次 / -1 无限 / n 有限次数） */
    timer->rings_remaining = callback_fn == 0
        ? (ctx == 0 ? 1 : ctx) : 0;
    if (uv_mutex_init(&timer->gate) != 0)
    {
        fprintf(stderr, "rigi_rt: uv_mutex_init 失败（环境耗尽）\n");
        abort();
    }
    timer->armed_counted = 1;
    atomic_fetch_add_explicit(&rigi_stat_armed, 1, memory_order_relaxed);
    if (uv_timer_init(loop, &timer->handle) != 0)
    {
        fprintf(stderr, "rigi_rt: uv_timer_init 失败（环境耗尽）\n");
        abort();
    }
    timer->handle.data = timer;
    /* 头插属主 live 链（destroy/sweep 摘除依据） */
    timer->next_live = w->live_timers;
    w->live_timers = timer;
    /* delay<=0 → 0（下一 loop 迭代即触发，沿用 VM Arm(<=0) 口径）；
     * repeat<0 钳 0；repeat>0 用 libuv 原生 repeat */
    uv_timer_start(&timer->handle, rigi_timer_fire_cb,
        (uint64_t)(delay_ms > 0 ? delay_ms : 0),
        (uint64_t)(repeat_ms > 0 ? repeat_ms : 0));
    uv_mutex_lock(&rigi_worker_registry_gate);
    if (rigi_alarm_identity == INT64_MAX) abort();
    timer->identity = ++rigi_alarm_identity;
    timer->next_registered = rigi_event_live;
    rigi_event_live = timer;
    uv_mutex_unlock(&rigi_worker_registry_gate);
    return timer->identity;
}

/* 查找/摘除只比较不复用的身份，不解引用调用方可能过期的句柄。
 * 调用方持登记册闸；摘除后底座的释放权唯一归接收者。 */
static RigiTimer *rigi_alarm_find(int64_t identity, int remove)
{
    RigiTimer **link;
    for (link = &rigi_event_live; *link != NULL;
        link = &(*link)->next_registered)
    {
        if ((*link)->identity == identity)
        {
            RigiTimer *timer = *link;
            if (remove) *link = timer->next_registered;
            return timer;
        }
    }
    return NULL;
}

static RigiTimer *rigi_timer_of(int64_t timer, const char *face)
{
    RigiTimer *t;
    if (timer == 0)
    {
        fprintf(stderr, "rigi_rt: %s 收到空句柄（编译器 bug）\n", face);
        abort();
    }
    uv_mutex_lock(&rigi_worker_registry_gate);
    t = rigi_alarm_find(timer, 0);
    /* 属主验证必须在登记册闸内读取，不能先解锁再读取可能已销毁的 t。
     * 验证成功后仅当前属主线程能销毁计时器，返回借用指针才是安全的。 */
    if (t != NULL) rigi_timer_owner_check(t->owner, face);
    uv_mutex_unlock(&rigi_worker_registry_gate);
    if (t == NULL) return NULL;
    return t;
}

void rigi_timer_cancel(int64_t timer)
{
    RigiTimer *t = rigi_timer_of(timer, "rigi_timer_cancel");
    if (t != NULL && !t->closed)
    {
        uv_timer_stop(&t->handle);
    }
}

static void rigi_timer_closed_cb(uv_handle_t *handle)
{
    RigiTimer *t = (RigiTimer *)handle->data;
    /* 残余 waiter 节点清扫（正常路径响铃已排空；防御） */
    while (t->waiters != NULL)
    {
        RigiAlarmWaitNode *next = t->waiters->next;
        rigi_track_free(t->waiters);
        t->waiters = next;
    }
    uv_mutex_destroy(&t->gate);
    rigi_track_free(t);
    atomic_fetch_sub_explicit(&rigi_stat_timer_bytes, sizeof(RigiTimer), memory_order_relaxed);
}

static void rigi_timer_close(RigiTimer *t)
{
    RigiTimer **link;
    if (t->closed)
    {
        return; /* 幂等 */
    }
    t->closed = 1;
    /* 摘属主 live 链 */
    for (link = &t->owner->live_timers; *link != NULL;
        link = &(*link)->next_live)
    {
        if (*link == t)
        {
            *link = t->next_live;
            break;
        }
    }
    if (t->armed_counted)
    {
        t->armed_counted = 0;
        atomic_fetch_sub_explicit(&rigi_stat_armed, 1,
            memory_order_relaxed);
    }
    /* close 回调负责释放，允许从 timer 回调内销毁；禁止递归 uv_run。 */
    uv_timer_stop(&t->handle);
    uv_close((uv_handle_t *)&t->handle, rigi_timer_closed_cb);
}

void rigi_timer_destroy(int64_t timer)
{
    RigiTimer *t = rigi_timer_of(timer, "rigi_timer_destroy");
    if (t == NULL) return;
    uv_mutex_lock(&rigi_worker_registry_gate);
    rigi_alarm_find(timer, 1);
    uv_mutex_unlock(&rigi_worker_registry_gate);
    rigi_timer_close(t);
}

static void rigi_worker_close_alarms(RigiWorker *worker)
{
    RigiTimer *timer;
    uv_mutex_lock(&rigi_worker_registry_gate);
    timer = worker->closing_timers;
    worker->closing_timers = NULL;
    uv_mutex_unlock(&rigi_worker_registry_gate);
    while (timer != NULL)
    {
        RigiTimer *next = timer->next_close;
        rigi_timer_close(timer);
        timer = next;
    }
}

/* EventAlarm waiter 登记（契约见 worker.h）：闸内「查 signaled +
 * 登记」原子完成（§19.3）；定时器句柄有效性由生成代码侧的 Alarm
 * 对象持有保证（挂起帧保活；响铃后仍保留到对象析构） */
int32_t rigi_alarm_wait(int64_t timer, int64_t waiter)
{
    RigiTimer *t;
    int32_t registered;
    if (timer == 0)
    {
        fprintf(stderr, "rigi_rt: rigi_alarm_wait 收到空句柄（编译器 bug："
            "yield EventAlarm 分流应先经 EventAlarm.ensureHandle 懒建底座）\n");
        abort();
    }
    uv_mutex_lock(&rigi_worker_registry_gate);
    t = rigi_alarm_find(timer, 0);
    if (t == NULL) { uv_mutex_unlock(&rigi_worker_registry_gate); return 0; }
    uv_mutex_lock(&t->gate);
    uv_mutex_unlock(&rigi_worker_registry_gate);
    registered = 0;
    if (t->signaled)
    {
        /* 已触发事件是粘滞终态。 */
    }
    else
    {
        RigiAlarmWaitNode *node =
            (RigiAlarmWaitNode *)rigi_track_malloc(sizeof(RigiAlarmWaitNode));
        node->waiter = waiter;
        node->next = t->waiters;
        t->waiters = node;
        registered = 1;
    }
    uv_mutex_unlock(&t->gate);
    return registered;
}

/* 用户粘滞事件复用定时器等待登记，signal后保持ready。 */
static RigiTimer *rigi_event_create_impl(void)
{
    RigiTimer *ev = (RigiTimer *)rigi_track_malloc(sizeof(RigiTimer));
    atomic_fetch_add_explicit(&rigi_stat_timer_bytes, sizeof(RigiTimer), memory_order_relaxed);
    memset(ev, 0, sizeof(*ev));
    ev->manual = 1;
    if (uv_mutex_init(&ev->gate) != 0)
    {
        fprintf(stderr, "rigi_rt: uv_mutex_init 失败（环境耗尽）\n");
        abort();
    }
    ev->armed_counted = 1;
    atomic_fetch_add_explicit(&rigi_stat_armed, 1, memory_order_relaxed);
    return ev;
}

/* 用户 EventAlarm 子类默认底座（L8，§19.3）：手动事件粘滞形态——
 * signal 恒置已触发（迟到 wait 立即消费且不清 signaled），重复
 * signal 幂等；stdlib EventAlarm.ensureHandle 懒建，内部析构摘册
 * 释放；atexit 仅兜底仍存活的底座。 */
int64_t rigi_event_create_sticky(void)
{
    RigiTimer *e = rigi_event_create_impl();
    rigi_worker_registry_ensure();
    uv_mutex_lock(&rigi_worker_registry_gate);
    if (rigi_alarm_identity == INT64_MAX) abort();
    e->identity = ++rigi_alarm_identity;
    e->next_registered = rigi_event_live;
    rigi_event_live = e;
    uv_mutex_unlock(&rigi_worker_registry_gate);
    return e->identity;
}

void rigi_event_signal(int64_t ev)
{
    RigiTimer *e;
    RigiAlarmWaitNode *waiters;
    if (ev == 0)
    {
        fprintf(stderr, "rigi_rt: rigi_event_signal 收到空句柄（编译器 bug）\n");
        abort();
    }
    uv_mutex_lock(&rigi_worker_registry_gate);
    e = rigi_alarm_find(ev, 0);
    if (e == NULL) { uv_mutex_unlock(&rigi_worker_registry_gate); return; }
    uv_mutex_lock(&e->gate);
    uv_mutex_unlock(&rigi_worker_registry_gate);
    waiters = e->waiters;
    e->waiters = NULL;
    {
        /* 粘滞形态（L8 用户 EventAlarm 底座，§19.3）：signal 即终态
         * ——恒置 signaled（有 waiter 也置，重复 signal 幂等）；此后
         * 新 wait 恒立即重发布、不再需要外部唤醒源，唤醒债务归还
         *（armed_counted 防双归还，对齐响铃耗尽口径） */
        e->signaled = 1;
        if (e->armed_counted)
        {
            e->armed_counted = 0;
            atomic_fetch_sub_explicit(&rigi_stat_armed, 1,
                memory_order_relaxed);
        }
    }
    uv_mutex_unlock(&e->gate);
    while (waiters != NULL)
    {
        RigiAlarmWaitNode *next = waiters->next;
        rigi_dispatch_publish(waiters->waiter);
        rigi_track_free(waiters);
        waiters = next;
    }
}

static void rigi_event_free(RigiTimer *e)
{
    uv_mutex_lock(&e->gate);
    if (e->closed)
    {
        uv_mutex_unlock(&e->gate);
        return; /* 幂等 */
    }
    e->closed = 1;
    if (e->armed_counted)
    {
        e->armed_counted = 0;
        atomic_fetch_sub_explicit(&rigi_stat_armed, 1,
            memory_order_relaxed);
    }
    /* 残余 waiter 防御清扫（正常路径队列回收先 signal 唤醒再 destroy） */
    while (e->waiters != NULL)
    {
        RigiAlarmWaitNode *next = e->waiters->next;
        rigi_track_free(e->waiters);
        e->waiters = next;
    }
    uv_mutex_unlock(&e->gate);
    uv_mutex_destroy(&e->gate);
    rigi_track_free(e);
    atomic_fetch_sub_explicit(&rigi_stat_timer_bytes, sizeof(RigiTimer), memory_order_relaxed);
}

/* 内部对象析构：不进入 ARC/GC fence，不执行用户代码。
 * 非属主线程仅交接关闭请求；shutdown 封闭后由 sweep 接管。
 * 登记册闸先于 worker 闸，退出与入队因此不会留下悬挂指针。 */
void rigi_alarm_release(int64_t identity)
{
    RigiTimer *timer;
    if (identity == 0) return;
    rigi_worker_registry_ensure();
    uv_mutex_lock(&rigi_worker_registry_gate);
    timer = rigi_alarm_find(identity, 1);
    if (timer != NULL && !timer->manual)
    {
        RigiWorker *owner = timer->owner;
        if (!owner->alarms_closing)
        {
            timer->next_close = owner->closing_timers;
            owner->closing_timers = timer;
            uv_mutex_lock(&owner->gate);
            uv_sem_post(&owner->sem);
            if (rigi_worker_loop(owner) != NULL
                && atomic_load_explicit(&owner->async_ready, memory_order_acquire))
                uv_async_send(&owner->wake);
            uv_mutex_unlock(&owner->gate);
        }
        timer = NULL;
    }
    uv_mutex_unlock(&rigi_worker_registry_gate);
    if (timer != NULL) rigi_event_free(timer);
}

void rigi_event_destroy(int64_t ev)
{
    rigi_alarm_release(ev);
}

/* 属主已封闭析构交接并排空队列：直接以 live 链中的记录关闭。
 * shutdown 可以先于静态槽/终轮 GC 析构，摘除身份后晚到释放查空。
 * close 回调统一释放，外层 teardown 冲刷，不递归 uv_run。 */
void rigi_worker_timer_sweep(RigiWorker *worker)
{
    RigiTimer *timer = worker != NULL ? worker->live_timers : NULL;
    if (timer == NULL)
    {
        return;
    }
    while (timer != NULL)
    {
        RigiTimer *next = timer->next_live;
        uv_mutex_lock(&rigi_worker_registry_gate);
        rigi_alarm_find(timer->identity, 1);
        uv_mutex_unlock(&rigi_worker_registry_gate);
        rigi_timer_close(timer);
        timer = next;
    }
}

/* 线程体：TLS 附着 → 入口 fn（Rigi Dispatcher 循环，park 取任务驱动）
 * → 入口返回后自拆 loop → TLS 摘除。ctx 不经参数——Worker 句柄经
 * TLS 当前上下文面自取（worker.h 头注释） */
static void rigi_worker_thread_main(void *arg)
{
    RigiWorker *worker = (RigiWorker *)arg;
    rigi_tls_worker = worker;
    worker->entry();
    rigi_worker_loop_teardown(worker);
    rigi_tls_worker = NULL;
}

int64_t rigi_worker_create(int64_t entry_fn)
{
    RigiWorker *worker;
    /* entry_fn == 0 = 约定入口：导出符号 rigi_dispatcher_entry（Rigi
     * Dispatcher workerLoop 包装；stdlib Dispatcher 懒起 Worker 传 0） */
    RigiWorkerEntry entry = entry_fn == 0
        ? &rigi_dispatcher_entry
        : (RigiWorkerEntry)(uintptr_t)entry_fn;
    rigi_worker_registry_ensure();
    worker = (RigiWorker *)rigi_track_malloc(sizeof(RigiWorker));
    memset(worker, 0, sizeof(*worker));
    worker->entry = entry;
    if (uv_sem_init(&worker->sem, 0) != 0
        || uv_mutex_init(&worker->gate) != 0)
    {
        fprintf(stderr, "rigi_rt: uv_sem/mutex_init 失败（环境耗尽）\n");
        abort();
    }
    if (uv_thread_create(&worker->thread, rigi_worker_thread_main,
            worker) != 0)
    {
        fprintf(stderr, "rigi_rt: uv_thread_create 失败（环境耗尽）\n");
        abort();
    }
    uv_mutex_lock(&rigi_worker_registry_gate);
    worker->next_live = rigi_worker_live;
    rigi_worker_live = worker;
    atomic_fetch_add_explicit(&rigi_stat_workers, 1, memory_order_relaxed);
    uv_mutex_unlock(&rigi_worker_registry_gate);
    return (int64_t)(uintptr_t)worker;
}

static RigiWorker *rigi_worker_of(int64_t worker, const char *face)
{
    (void)face;
    if (worker == 0)
    {
        /* 主 Worker 内建实例（主线程，无线程可 join） */
        return rigi_main_worker_ensure();
    }
    return (RigiWorker *)(uintptr_t)worker;
}

void rigi_worker_enqueue(int64_t worker, int64_t task)
{
    RigiWorker *w = rigi_worker_of(worker, "rigi_worker_enqueue");
    RigiWorkNode *node =
        (RigiWorkNode *)rigi_track_malloc(sizeof(RigiWorkNode));
    node->token = task;
    node->next = NULL;
    /* 交接协议：gate 锁内尾插 → sem_post（sem 计数恒 = 队列长度 +
     * 在途唤醒）→ 目标已建 loop 则 async_send 唤醒 uv_run */
    uv_mutex_lock(&w->gate);
    if (w->tail != NULL)
    {
        w->tail->next = node;
    }
    else
    {
        w->head = node;
    }
    w->tail = node;
    uv_sem_post(&w->sem);
    if (rigi_worker_loop(w) != NULL
        && atomic_load_explicit(&w->async_ready, memory_order_acquire) != 0)
    {
        uv_async_send(&w->wake);
    }
    uv_mutex_unlock(&w->gate);
}

/* 死锁看门狗（仅主 Worker，棒5a；VM WorkerPark 的 IsDeadlocked 显败
 * 同口径——双端判定内容一致：live>0 且无 runnable 且无在途唤醒源时
 * 无限 park 只会冻死进程，改为清晰诊断。VM 侧抛 VmException 经顶层
 * 退出码 1；native 侧 stderr 诊断 + abort（沿用 MW11a drain 死锁
 * 显败口径），退出形态不同属两宿主既有各自约定）。
 * 形态：主 loop 上 50ms 重复内部定时器（不进 armed 计数）；判定计数
 * 稳定持续 10 拍（500ms）才显败——「已出队未 resume」「响铃排空
 * 与发布之间」等瞬态间隙由稳定期吸收，不误判。 */
#define RIGI_WATCHDOG_INTERVAL_MS 50
#define RIGI_WATCHDOG_STABLE_TICKS 10

static uv_timer_t rigi_main_watchdog;
static int rigi_main_watchdog_ready = 0;
static int rigi_main_watchdog_stable = 0;

static void rigi_main_watchdog_cb(uv_timer_t *handle)
{
    (void)handle;
    if (atomic_load_explicit(&rigi_stat_live, memory_order_relaxed) > 0
        && atomic_load_explicit(&rigi_stat_running, memory_order_relaxed) == 0
        && atomic_load_explicit(&rigi_stat_armed, memory_order_relaxed) == 0)
    {
        rigi_main_watchdog_stable++;
        if (rigi_main_watchdog_stable >= RIGI_WATCHDOG_STABLE_TICKS)
        {
            fprintf(stderr, "rigi_rt: 调度死锁：存在未终态协程，但无"
                " runnable 任务且无在途唤醒源（Alarm/轮询定时器），"
                "调度器无法继续推进\n");
            fflush(stderr);
            abort();
        }
    }
    else
    {
        rigi_main_watchdog_stable = 0;
    }
}

/* 主 Worker 首次 park：懒建 loop + 看门狗（仅属主=主线程调用） */
static void rigi_main_park_setup(RigiWorker *worker)
{
    uv_loop_t *loop = rigi_worker_loop_ensure(worker);
    if (!rigi_main_watchdog_ready)
    {
        rigi_main_watchdog_ready = 1;
        if (uv_timer_init(loop, &rigi_main_watchdog) != 0)
        {
            fprintf(stderr, "rigi_rt: 看门狗 uv_timer_init 失败"
                "（环境耗尽）\n");
            abort();
        }
        uv_timer_start(&rigi_main_watchdog, rigi_main_watchdog_cb,
            RIGI_WATCHDOG_INTERVAL_MS, RIGI_WATCHDOG_INTERVAL_MS);
    }
}

int64_t rigi_worker_park(int64_t worker)
{
    RigiWorker *w = rigi_worker_of(worker, "rigi_worker_park");
    RigiWorkNode *node;
    int64_t token = 0;
    /* 有连续 runnable 时仍泵一次既有 loop，避免定时器被 token 饿死。 */
    uv_loop_t *ready_loop = rigi_worker_loop(w);
    if (ready_loop != NULL) uv_run(ready_loop, UV_RUN_NOWAIT);
    for (;;)
    {
        uv_loop_t *loop;
        /* 有任务令牌立取（sem 计数恒 = 队列长度 + 在途唤醒） */
        if (uv_sem_trywait(&w->sem) == 0)
        {
            break;
        }
        loop = rigi_worker_loop(w);
        if (loop == NULL)
        {
            if (worker == 0)
            {
                /* 主 Worker：懒建 loop + 看门狗后走泵循环 */
                rigi_main_park_setup(w);
                continue;
            }
            /* 无 loop 即无定时器：纯 sem 阻塞等待 */
            uv_sem_wait(&w->sem);
            break;
        }
        /* 泵属主 loop 一轮（阻塞至最近事件：定时器触发或 enqueue 的
         * async_send 唤醒），随后回环 trywait 取令牌 */
        uv_run(loop, UV_RUN_ONCE);
    }
    uv_mutex_lock(&w->gate);
    node = w->head;
    if (node != NULL)
    {
        w->head = node->next;
        if (w->head == NULL)
        {
            w->tail = NULL;
        }
    }
    uv_mutex_unlock(&w->gate);
    if (node != NULL)
    {
        token = node->token;
        rigi_track_free(node);
    }
    /* node == NULL：destroy/通知的唤醒 post（退出检查点），令牌 0 */
    return token;
}

/* 交接队列排空（destroy / 主 Worker shutdown 共用）：残余唤醒令牌
 * 台账配对。gate 锁内走完，tail 一并清零 */
static void rigi_worker_drain_work(RigiWorker *w)
{
    uv_mutex_lock(&w->gate);
    for (;;)
    {
        RigiWorkNode *node = w->head;
        if (node == NULL)
        {
            break;
        }
        w->head = node->next;
        rigi_track_free(node);
    }
    w->tail = NULL;
    uv_mutex_unlock(&w->gate);
}

void rigi_worker_destroy(int64_t worker)
{
    RigiWorker *w;
    RigiWorker **link;
    if (worker == 0)
    {
        fprintf(stderr, "rigi_rt: rigi_worker_destroy 收到主 Worker 句柄 0"
            "（主 Worker 无线程，不可 destroy）\n");
        abort();
    }
    w = rigi_worker_of(worker, "rigi_worker_destroy");
    /* 优雅退出：stop 置位 → 双通道唤醒（park 的 sem_wait 与 uv_run 各
     * 一路）→ join。入口 fn 返回后线程体已自拆 loop，join 后无线程
     * 再触碰任何 uv 句柄 */
    uv_mutex_lock(&w->gate);
    atomic_store_explicit(&w->stop_requested, 1, memory_order_release);
    uv_sem_post(&w->sem);
    if (rigi_worker_loop(w) != NULL
        && atomic_load_explicit(&w->async_ready, memory_order_acquire) != 0)
    {
        uv_async_send(&w->wake);
    }
    uv_mutex_unlock(&w->gate);
    uv_thread_join(&w->thread);
    /* 摘全局 live 链 */
    uv_mutex_lock(&rigi_worker_registry_gate);
    for (link = &rigi_worker_live; *link != NULL; link = &(*link)->next_live)
    {
        if (*link == w)
        {
            *link = w->next_live;
            atomic_fetch_sub_explicit(&rigi_stat_workers, 1, memory_order_relaxed);
            break;
        }
    }
    uv_mutex_unlock(&rigi_worker_registry_gate);
    /* 防御：正常路径 park 已排空队列；残余节点直接清扫保台账配对 */
    rigi_worker_drain_work(w);
    uv_sem_destroy(&w->sem);
    uv_mutex_destroy(&w->gate);
    rigi_track_free(w);
}

/* 主 Worker 收尾（rigi_entry 在 Dispatcher workerLoop 返回后调用）：
 * 交接队列残余令牌排空（quiescence 直返时 noteTerminal 末次唤醒
 * 可能未 park 消耗；主 Worker 不经 destroy）→ 看门狗拆除 →
 * 残余定时器清扫 → async close → flush → loop close
 *（rigi_worker_loop_teardown 同序）。此后主 Worker 不可再 park */
void rigi_main_worker_shutdown(void)
{
    RigiWorker *w;
    if (!atomic_load_explicit(&rigi_main_worker_ready, memory_order_acquire))
    {
        return;
    }
    w = &rigi_main_worker_storage;
    rigi_worker_drain_work(w);
    if (rigi_worker_loop(w) == NULL)
    {
        return;
    }
    if (rigi_main_watchdog_ready)
    {
        rigi_main_watchdog_ready = 0;
        uv_timer_stop(&rigi_main_watchdog);
        uv_close((uv_handle_t *)&rigi_main_watchdog, NULL);
    }
    rigi_worker_loop_teardown(w);
}

/* atexit 兜底清扫：未显式 destroy 的 Worker/信号量/同步锁与用户
 * EventAlarm 粘滞底座（L8 第四条链）统一释放。Worker 线程此刻应已
 * 全部随入口 fn 返回（Rigi Dispatcher 退出协议），残余走 destroy
 * 同路径；登记册互斥自身不记账（静态存储，随进程消亡） */
static void rigi_worker_cleanup(void)
{
    while (rigi_worker_live != NULL)
    {
        rigi_worker_destroy((int64_t)(uintptr_t)rigi_worker_live);
    }
    while (rigi_sem_live != NULL)
    {
        rigi_sem_destroy(rigi_sem_live);
    }
    while (rigi_smutex_live != NULL)
    {
        rigi_sync_mutex_destroy((int64_t)(uintptr_t)rigi_smutex_live);
    }
    for (;;)
    {
        int64_t identity;
        /* 终轮 GC 可能仍在摘册；只在闸内读取身份，闸外幂等释放。 */
        uv_mutex_lock(&rigi_worker_registry_gate);
        identity = rigi_event_live != NULL ? rigi_event_live->identity : 0;
        uv_mutex_unlock(&rigi_worker_registry_gate);
        if (identity == 0) break;
        /* 同 rigi_event_destroy 路径（幂等 + 残余 waiter 防御清扫 +
         * armed 归还 + 台账配对）；粘滞底座不得再被引用（进程退出中） */
        rigi_alarm_release(identity);
    }
    /* 静态登记册闸保留到进程结束，供随后 atexit 对象析构安全查空。 */
}

#else /* !RIGI_HAS_LIBUV：原语面降级诊断 abort（alarm.c 同纪律） */

RigiSem *rigi_sem_create(uint32_t initial)
{
    (void)initial;
    fprintf(stderr, "rigi_rt: 信号量原语需要 libuv，但 rigi_rt 按无 libuv"
        "能力编译（RIGI_HAS_LIBUV 未定义）\n");
    abort();
}

void rigi_sem_post(RigiSem *sem)
{
    (void)sem;
    fprintf(stderr, "rigi_rt: 信号量原语需要 libuv（RIGI_HAS_LIBUV 未定义）\n");
    abort();
}

void rigi_sem_wait(RigiSem *sem)
{
    (void)sem;
    fprintf(stderr, "rigi_rt: 信号量原语需要 libuv（RIGI_HAS_LIBUV 未定义）\n");
    abort();
}

void rigi_sem_destroy(RigiSem *sem)
{
    (void)sem;
}

int64_t rigi_sync_mutex_create(void)
{
    fprintf(stderr, "rigi_rt: 同步 Mutex 原语需要 libuv，但 rigi_rt 按无"
        "libuv 能力编译（RIGI_HAS_LIBUV 未定义）\n");
    abort();
}

void rigi_sync_mutex_acquire(int64_t mutex)
{
    (void)mutex;
    fprintf(stderr, "rigi_rt: 同步 Mutex 原语需要 libuv（RIGI_HAS_LIBUV 未定义）\n");
    abort();
}

void rigi_sync_mutex_release(int64_t mutex)
{
    (void)mutex;
    fprintf(stderr, "rigi_rt: 同步 Mutex 原语需要 libuv（RIGI_HAS_LIBUV 未定义）\n");
    abort();
}

void rigi_sync_mutex_destroy(int64_t mutex)
{
    (void)mutex;
}
int64_t rigi_sync_mutex_live_count(void) { return 0; }

int rigi_worker_stop_requested(RigiWorker *worker)
{
    (void)worker;
    return 1;
}

int32_t rigi_worker_parallelism(void) { return 1; }

int64_t rigi_worker_create(int64_t entry_fn)
{
    (void)entry_fn;
    fprintf(stderr, "rigi_rt: Worker 原语需要 libuv，但 rigi_rt 按无 libuv"
        "能力编译（RIGI_HAS_LIBUV 未定义）\n");
    abort();
}

void rigi_worker_destroy(int64_t worker)
{
    (void)worker;
}

void rigi_worker_enqueue(int64_t worker, int64_t task)
{
    (void)worker;
    (void)task;
    fprintf(stderr, "rigi_rt: Worker 原语需要 libuv（RIGI_HAS_LIBUV 未定义）\n");
    abort();
}

int64_t rigi_worker_park(int64_t worker)
{
    (void)worker;
    fprintf(stderr, "rigi_rt: Worker 原语需要 libuv（RIGI_HAS_LIBUV 未定义）\n");
    abort();
}

void rigi_worker_timer_sweep(RigiWorker *worker)
{
    (void)worker;
}

int64_t rigi_timer_create(int64_t owner, int64_t delay_ms,
    int64_t repeat_ms, int64_t callback_fn, int64_t ctx)
{
    (void)owner;
    (void)delay_ms;
    (void)repeat_ms;
    (void)callback_fn;
    (void)ctx;
    fprintf(stderr, "rigi_rt: 定时器原语需要 libuv，但 rigi_rt 按无 libuv"
        "能力编译（RIGI_HAS_LIBUV 未定义）\n");
    abort();
}

void rigi_timer_cancel(int64_t timer)
{
    (void)timer;
    fprintf(stderr, "rigi_rt: 定时器原语需要 libuv（RIGI_HAS_LIBUV 未定义）\n");
    abort();
}

void rigi_timer_destroy(int64_t timer)
{
    (void)timer;
}

int32_t rigi_alarm_wait(int64_t timer, int64_t waiter)
{
    (void)timer;
    (void)waiter;
    fprintf(stderr, "rigi_rt: EventAlarm waiter 登记需要 libuv"
        "（RIGI_HAS_LIBUV 未定义）\n");
    abort();
}

/* 手动 EventAlarm 降级形态：无协程原语即无 waiter 场景（Worker/
 * alarm_wait 均 abort），只保粘滞 signaled 单线程语义供同步路径 */
int64_t rigi_event_create_sticky(void)
{
    int *flag = (int *)rigi_track_malloc(sizeof(int));
    *flag = 0;
    return (int64_t)(uintptr_t)flag;
}

void rigi_event_signal(int64_t ev)
{
    if (ev != 0)
    {
        *(int *)(uintptr_t)ev = 1;
    }
}

void rigi_event_destroy(int64_t ev)
{
    if (ev != 0)
    {
        rigi_track_free((void *)(uintptr_t)ev);
    }
}

void rigi_main_worker_shutdown(void)
{
}

void rigi_alarm_release(int64_t identity)
{
    rigi_event_destroy(identity);
}

#endif /* RIGI_HAS_LIBUV */
