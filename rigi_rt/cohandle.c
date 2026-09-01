/*
 * 协程句柄原语实现（MW11c 棒3）：契约见 cohandle.h 头注释。
 * 双编译形态真实现（纯 C11 + 台账，零 uv 依赖）；静态名一律
 * rigi_ch_ 前缀（unity build 单编译单元防碰撞）。
 * MW11c 棒5a：lane 槽 + PollingAlarm 轮询状态（退避定时器经
 * worker.c 定时器原语，回调只经导出符号 rigi_dispatch_publish
 * 重发布进 Rigi Dispatcher）。
 */
#include "cohandle.h"
#include "arc.h"

#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>

/* 生成代码导出符号（ModuleBuilder 恒发射；shim.c rigi_entry 先例）：
 * 重发布一个协程句柄进 Rigi Dispatcher（lane 由句柄槽读取） */
extern void rigi_dispatch_publish(int64_t handle);

/* 句柄实体：resume fn + frame（借用）+ lane +
 * 轮询状态（armed 标记/退避毫秒/退避定时器）+ CoroutineLocal 绑定栈 */
typedef struct RigiLocalBind
{
    RigiFatRef key;
    RigiFatRef value;
    struct RigiLocalBind *prev; /* 更旧的绑定；顶 = 句柄.locals */
} RigiLocalBind;

typedef struct RigiCoHandle
{
    RigiResumeFn resume;
    void *frame;
    _Atomic int64_t lane;
    int poll_armed;          /* 仅属主线程（执行段内）读写 */
    uint32_t poll_backoff_ms;
    int64_t poll_timer;      /* rigi_timer_* 句柄；0 = 无 */
    RigiLocalBind *locals;   /* 绑定栈顶；NULL = 空 */
} RigiCoHandle;

static void rigi_ch_locals_clear(RigiCoHandle *h);

static RigiCoHandle *rigi_ch_of(int64_t handle, const char *face)
{
    if (handle == 0)
    {
        fprintf(stderr, "rigi_rt: %s 收到空句柄（编译器 bug）\n", face);
        abort();
    }
    return (RigiCoHandle *)(uintptr_t)handle;
}

int64_t rigi_coroutine_create(int64_t resume_fn, int64_t frame)
{
    RigiCoHandle *h;
    if (resume_fn == 0)
    {
        fprintf(stderr,
            "rigi_rt: rigi_coroutine_create 的 resume fn 为 0（编译器 bug）\n");
        abort();
    }
    h = (RigiCoHandle *)rigi_track_malloc(sizeof(RigiCoHandle));
    h->resume = (RigiResumeFn)(uintptr_t)resume_fn;
    h->frame = (void *)(uintptr_t)frame;
    atomic_init(&h->lane, 0);
    h->poll_armed = 0;
    h->poll_backoff_ms = 1;
    h->poll_timer = 0;
    h->locals = NULL;
    rigi_stat_note_create();
    return (int64_t)(uintptr_t)h;
}

int64_t rigi_coroutine_resume(int64_t handle)
{
    RigiCoHandle *h = rigi_ch_of(handle, "rigi_coroutine_resume");
    void *previous = rigi_tls_get_coroutine();
    RigiResumeCode code;
    /* TLS 当前协程槽 set/clear 包围执行段（嵌套恢复保存/还原外层，
     * 沿用 cohandle resume 包围的 TLS 当前协程先例） */
    rigi_tls_set_coroutine(h);
    rigi_stat_note_resume_begin();
    code = h->resume(h->frame);
    rigi_stat_note_resume_end();
    rigi_tls_set_coroutine(previous);
    return (int64_t)code;
}

void rigi_coroutine_destroy(int64_t handle)
{
    RigiCoHandle *h = rigi_ch_of(handle, "rigi_coroutine_destroy");
    /* 轮询定时器随终态清理（VM DisposePollTimer 同口径：终态 choke
     * point 统一释放）；frame 所有权在生成代码 */
    if (h->poll_timer != 0)
    {
        rigi_poll_clear(handle);
    }
    rigi_ch_locals_clear(h);
    rigi_track_free(h);
    rigi_stat_note_destroy();
}

/* ---- lane 槽 ---- */

void rigi_coroutine_set_lane(int64_t handle, int32_t lane)
{
    atomic_store_explicit(&rigi_ch_of(handle, "rigi_coroutine_set_lane")->lane,
        (int64_t)lane, memory_order_release);
}

int32_t rigi_coroutine_get_lane(int64_t handle)
{
    return (int32_t)atomic_load_explicit(
        &rigi_ch_of(handle, "rigi_coroutine_get_lane")->lane,
        memory_order_acquire);
}

int64_t rigi_coroutine_current(void)
{
    return (int64_t)(uintptr_t)rigi_tls_get_coroutine();
}

/* ---- PollingAlarm 轮询状态 ---- */

/* 退避定时器回调（属主 Worker loop 线程）：只重发布进 Rigi
 * Dispatcher——isReady 探测恒由恢复块的 $mw.poll_probe 执行
 *（callback 不执行用户代码，对齐 VM SchedulePoll 口径）。一次性
 * 定时器块由 rigi_poll_clear/终态清理回收（armed 标记在重发布前
 * 保持置位，恢复块 pending 查询据此进入探测分支） */
static void rigi_ch_poll_fired(void *ctx)
{
    rigi_dispatch_publish((int64_t)(uintptr_t)ctx);
}

void rigi_poll_arm(int64_t handle)
{
    RigiCoHandle *h = rigi_ch_of(handle, "rigi_poll_arm");
    h->poll_armed = 1;
    h->poll_backoff_ms = 1;
}

int32_t rigi_poll_pending(int64_t handle)
{
    return rigi_ch_of(handle, "rigi_poll_pending")->poll_armed ? 1 : 0;
}

void rigi_poll_schedule(int64_t handle)
{
    RigiCoHandle *h = rigi_ch_of(handle, "rigi_poll_schedule");
    int64_t owner = rigi_tls_current_context();
    uint32_t delay = h->poll_backoff_ms;
    if (h->poll_timer != 0)
    {
        rigi_timer_destroy(h->poll_timer);
        h->poll_timer = 0;
    }
    /* 退避 1→32ms 指数（VM TakePollDelay 同口径）；callback_fn =
     * rigi_ch_poll_fired（库内 C 回调，仅调导出符号重发布） */
    h->poll_timer = rigi_timer_create(owner, (int64_t)delay, 0,
        (int64_t)(uintptr_t)&rigi_ch_poll_fired, handle);
    /* 下一次退避翻倍，封顶 32ms */
    h->poll_backoff_ms = delay >= 32 ? 32 : delay * 2;
}

void rigi_poll_clear(int64_t handle)
{
    RigiCoHandle *h = rigi_ch_of(handle, "rigi_poll_clear");
    h->poll_armed = 0;
    h->poll_backoff_ms = 1;
    if (h->poll_timer != 0)
    {
        int64_t timer = h->poll_timer;
        h->poll_timer = 0;
        rigi_timer_destroy(timer);
    }
}

/* ---- CoroutineLocal 绑定栈（§20.2）---- */

static void rigi_ch_fat_dup(RigiFatRef *dst, const RigiFatRef *src)
{
    dst->type_id = src->type_id;
    dst->payload = rigi_ref_acquire(src->type_id, src->payload);
}

static void rigi_ch_fat_release(RigiFatRef *fat)
{
    rigi_ref_release(fat->type_id, fat->payload);
    fat->type_id = 0;
    fat->payload = 0;
}

static void rigi_ch_locals_clear(RigiCoHandle *h)
{
    while (h->locals != NULL)
    {
        RigiLocalBind *n = h->locals;
        h->locals = n->prev;
        rigi_ch_fat_release(&n->key);
        rigi_ch_fat_release(&n->value);
        rigi_track_free(n);
    }
}

static int rigi_ch_locals_has(const RigiCoHandle *h, uint64_t key_payload)
{
    const RigiLocalBind *n;
    for (n = h->locals; n != NULL; n = n->prev)
    {
        if (n->key.payload == key_payload)
        {
            return 1;
        }
    }
    return 0;
}

void rigi_coro_local_push(const RigiFatRef *key, const RigiFatRef *value)
{
    RigiCoHandle *h = (RigiCoHandle *)rigi_tls_get_coroutine();
    RigiLocalBind *node;
    if (h == NULL || key == NULL || value == NULL)
    {
        fprintf(stderr,
            "rigi_rt: rigi_coro_local_push 无当前协程或参数为空（编译器 bug）\n");
        abort();
    }
    node = (RigiLocalBind *)rigi_track_malloc(sizeof(RigiLocalBind));
    rigi_ch_fat_dup(&node->key, key);
    rigi_ch_fat_dup(&node->value, value);
    node->prev = h->locals;
    h->locals = node;
}

void rigi_coro_local_pop(const RigiFatRef *key)
{
    RigiCoHandle *h = (RigiCoHandle *)rigi_tls_get_coroutine();
    RigiLocalBind **link;
    if (h == NULL || key == NULL)
    {
        fprintf(stderr,
            "rigi_rt: rigi_coro_local_pop 无当前协程或参数为空（编译器 bug）\n");
        abort();
    }
    for (link = &h->locals; *link != NULL; link = &(*link)->prev)
    {
        RigiLocalBind *n = *link;
        if (n->key.payload == key->payload)
        {
            *link = n->prev;
            rigi_ch_fat_release(&n->key);
            rigi_ch_fat_release(&n->value);
            rigi_track_free(n);
            return;
        }
    }
    fprintf(stderr, "rigi_rt: rigi_coro_local_pop 栈上无对应键（编译器 bug）\n");
    abort();
}

void rigi_coro_local_get(RigiFatRef *out, const RigiFatRef *key)
{
    RigiCoHandle *h;
    const RigiLocalBind *n;
    if (out == NULL || key == NULL)
    {
        fprintf(stderr, "rigi_rt: rigi_coro_local_get 参数为空（编译器 bug）\n");
        abort();
    }
    out->type_id = 0;
    out->payload = 0;
    h = (RigiCoHandle *)rigi_tls_get_coroutine();
    if (h == NULL)
    {
        return;
    }
    for (n = h->locals; n != NULL; n = n->prev)
    {
        if (n->key.payload == key->payload)
        {
            rigi_ch_fat_dup(out, &n->value);
            return;
        }
    }
}

void rigi_coro_local_inherit(int64_t child)
{
    RigiCoHandle *src = (RigiCoHandle *)rigi_tls_get_coroutine();
    RigiCoHandle *dst;
    const RigiLocalBind *n;
    if (src == NULL)
    {
        return;
    }
    dst = rigi_ch_of(child, "rigi_coro_local_inherit");
    /* 自顶向下：每个键只拷第一次（当前有效顶），不是整段父栈 */
    for (n = src->locals; n != NULL; n = n->prev)
    {
        RigiLocalBind *copy;
        if (rigi_ch_locals_has(dst, n->key.payload))
        {
            continue;
        }
        copy = (RigiLocalBind *)rigi_track_malloc(sizeof(RigiLocalBind));
        rigi_ch_fat_dup(&copy->key, &n->key);
        rigi_ch_fat_dup(&copy->value, &n->value);
        copy->prev = dst->locals;
        dst->locals = copy;
    }
}
