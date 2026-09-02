/*
 * MessageQueue 传输层 native 实现（MW11d-C）：append-only 广播日志 +
 * 每 reader 独立 cursor + watermark 回收 + capability 矩阵 + sealed/EOS。
 * 只搬运/存储 Parcel 胖引用（深复制在 Rigi 层经 toParcel/fromParcel 完成）。
 * 可预见的违规（capability/生命周期）返回负错误码，Rigi 层翻译为
 * core.IllegalStateException（VM/Native 双端同文）；不可达的防御路径
 * 直接诊断 abort。句柄 id 进程内单调递增不复用（§22.8）。
 *
 * 并发模型：注册表与全部队列状态共用一把大闸（post 的多 Worker 线性化
 * 由此天然得到全局追加序，§18）；事件信号（rigi_event_signal →
 * rigi_dispatch_publish）在闸外执行，避免与 Dispatcher 闸互嵌。
 * 无 libuv 降级形态：无 Worker 即无并发，锁为空操作（alarm_wait 在
 * 降级形态本就 abort，同步路径语义完整）。
 */
#include "message.h"

#include "arc.h"
#include "worker.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* 错误码：与 stdlib core.messaging 的 mqError 文本逐条对应 */
#define RIGI_MQ_ERR_RELEASED        (-1)  /* 句柄已释放或不存在 */
#define RIGI_MQ_ERR_DOUBLE_RELEASE  (-2)  /* 句柄重复释放 */
#define RIGI_MQ_ERR_DERIVE_OWNER    (-3)  /* 不能派生 Owner */
#define RIGI_MQ_ERR_DERIVE_SENDER   (-4)  /* 不能从该句柄派生 Sender */
#define RIGI_MQ_ERR_DERIVE_READER   (-5)  /* 不能从该句柄派生 Reader */
#define RIGI_MQ_ERR_POST            (-6)  /* 该句柄不能 post */
#define RIGI_MQ_ERR_NEXT            (-7)  /* 该句柄不能 next */
#define RIGI_MQ_ERR_SEALED_POST     (-8)  /* 队列已 sealed，不能 post */
#define RIGI_MQ_ERR_SEALED_SENDER   (-9)  /* 队列已 sealed，不能派生 Sender */
#define RIGI_MQ_ERR_OUTSTANDING_NEXT (-10) /* 同一 Reader 并发 next */

/* QueueHandleType.code 对齐（stdlib core.messaging） */
#define RIGI_MQ_READER 0
#define RIGI_MQ_OWNER  1
#define RIGI_MQ_SENDER 2

typedef struct RigiMqReader
{
    int64_t handle_id;
    uint64_t cursor;          /* 下一条可见消息的全局序号 */
    int64_t event;            /* 「消息可得」手动 EventAlarm（rigi_event_*） */
    int in_next;              /* 单 outstanding next 守约（§24）：next
                               * 调用边界由 Rigi 层 enter/exit 标记（全局
                               * 可变状态须共享安全，Rigi 侧无共享集合） */
    struct RigiMqReader *next;
} RigiMqReader;

typedef struct RigiMq
{
    RigiFatRef *log;          /* append-only 日志；log[0] 的全局序号 = base_seq */
    size_t len;
    size_t cap;
    uint64_t base_seq;
    int owner_alive;
    int64_t sender_count;
    int sealed;
    RigiMqReader *readers;
    int64_t ref_count;        /* 存活句柄数（owner+sender+reader 合计） */
    struct RigiMq *next;
} RigiMq;

typedef struct RigiMqHandle
{
    int64_t id;
    int32_t type;
    int released;             /* 墓碑：重复释放诊断（随队列回收 purge） */
    RigiMq *queue;
    RigiMqReader *reader;     /* type == RIGI_MQ_READER 时的对应记录 */
    struct RigiMqHandle *next;
} RigiMqHandle;

#ifdef RIGI_HAS_LIBUV
static uv_mutex_t rigi_mq_gate;
static int rigi_mq_gate_ready = 0;
#define RIGI_MQ_LOCK()   uv_mutex_lock(&rigi_mq_gate)
#define RIGI_MQ_UNLOCK() uv_mutex_unlock(&rigi_mq_gate)
#else
#define RIGI_MQ_LOCK()   ((void)0)
#define RIGI_MQ_UNLOCK() ((void)0)
#endif

static RigiMq *rigi_mq_queues = NULL;
static RigiMqHandle *rigi_mq_handles = NULL;
static int64_t rigi_mq_next_id = 1;
static int rigi_mq_gate_ensured = 0;

/* 首次使用懒建大闸（与 rigi_worker_registry_ensure 同款懒初始化；
 * 降级形态为空操作） */
static void rigi_mq_ensure(void)
{
    if (rigi_mq_gate_ensured)
    {
        return;
    }
    rigi_mq_gate_ensured = 1;
#ifdef RIGI_HAS_LIBUV
    if (!rigi_mq_gate_ready)
    {
        if (uv_mutex_init(&rigi_mq_gate) != 0)
        {
            fprintf(stderr, "rigi_rt: uv_mutex_init 失败（环境耗尽）\n");
            abort();
        }
        rigi_mq_gate_ready = 1;
    }
#endif
}

static int64_t rigi_mq_alloc_id(void)
{
    return rigi_mq_next_id++;
}

/* 队尾全局序号（下一条 post 的落点） */
static uint64_t rigi_mq_tail(const RigiMq *q)
{
    return q->base_seq + (uint64_t)q->len;
}

/* watermark 回收：序号 < 全部存活 reader cursor 最小值 的消息不再
 * 有读者，释放 Parcel 引用并前移 base_seq；无存活 reader 时全部
 * 回收（新 reader 从队尾起，存量对它不可见）。调用时须持大闸。 */
static void rigi_mq_reclaim(RigiMq *q)
{
    uint64_t mark;
    uint64_t drop;
    const RigiMqReader *r;
    if (q->len == 0)
    {
        return;
    }
    if (q->readers == NULL)
    {
        mark = rigi_mq_tail(q);
    }
    else
    {
        mark = q->readers->cursor;
        for (r = q->readers->next; r != NULL; r = r->next)
        {
            if (r->cursor < mark)
            {
                mark = r->cursor;
            }
        }
    }
    if (mark <= q->base_seq)
    {
        return;
    }
    drop = mark - q->base_seq;
    if (drop > (uint64_t)q->len)
    {
        drop = (uint64_t)q->len;
    }
    for (uint64_t i = 0; i < drop; i++)
    {
        rigi_ref_release(q->log[i].type_id, q->log[i].payload);
    }
    memmove(q->log, q->log + drop, (q->len - (size_t)drop) * sizeof(RigiFatRef));
    q->len -= (size_t)drop;
    q->base_seq += drop;
}

/* sealed 判定（Owner 已释放 ∧ Sender 计数归零，§9.2）；进入 sealed
 * 时收集全部 reader 事件句柄供闸外唤醒（pending next 观察到 EOS）。
 * 返回的事件数组由调用方释放；*count 为个数。调用时须持大闸。 */
static int64_t *rigi_mq_collect_reader_events(RigiMq *q, int *count)
{
    int64_t *events;
    int n = 0;
    RigiMqReader *r;
    for (r = q->readers; r != NULL; r = r->next)
    {
        n++;
    }
    *count = n;
    if (n == 0)
    {
        return NULL;
    }
    events = (int64_t *)rigi_track_malloc(sizeof(int64_t) * (size_t)n);
    n = 0;
    for (r = q->readers; r != NULL; r = r->next)
    {
        events[n++] = r->event;
    }
    return events;
}

static void rigi_mq_signal_events(int64_t *events, int count)
{
    for (int i = 0; i < count; i++)
    {
        rigi_event_signal(events[i]);
    }
    if (events != NULL)
    {
        rigi_track_free(events);
    }
}

/* 队列析构（ref_count 归零）：残留消息全释放（正常路径 watermark
 * 已清空，此处为防御）、摘除注册表、释放日志块。调用时须持大闸。 */
static void rigi_mq_free_queue(RigiMq *q)
{
    RigiMq **link;
    RigiMqHandle **hlink;
    RigiMqReader *r;
    for (size_t i = 0; i < q->len; i++)
    {
        rigi_ref_release(q->log[i].type_id, q->log[i].payload);
    }
    rigi_track_free(q->log);
    while (q->readers != NULL)
    {
        r = q->readers;
        q->readers = r->next;
        rigi_event_destroy(r->event);
        rigi_track_free(r);
    }
    /* 墓碑句柄随队列 purge（队列存活期间墓碑保留以诊断重复释放） */
    hlink = &rigi_mq_handles;
    while (*hlink != NULL)
    {
        if ((*hlink)->queue == q)
        {
            RigiMqHandle *dead = *hlink;
            *hlink = dead->next;
            rigi_track_free(dead);
        }
        else
        {
            hlink = &(*hlink)->next;
        }
    }
    for (link = &rigi_mq_queues; *link != NULL; link = &(*link)->next)
    {
        if (*link == q)
        {
            *link = q->next;
            break;
        }
    }
    rigi_track_free(q);
}

static RigiMqHandle *rigi_mq_find_handle(int64_t id)
{
    RigiMqHandle *h;
    for (h = rigi_mq_handles; h != NULL; h = h->next)
    {
        if (h->id == id)
        {
            return h;
        }
    }
    return NULL;
}

static RigiMqHandle *rigi_mq_new_handle(int32_t type, RigiMq *q,
    RigiMqReader *reader)
{
    RigiMqHandle *h = (RigiMqHandle *)rigi_track_malloc(sizeof(RigiMqHandle));
    h->id = rigi_mq_alloc_id();
    h->type = type;
    h->released = 0;
    h->queue = q;
    h->reader = reader;
    h->next = rigi_mq_handles;
    rigi_mq_handles = h;
    q->ref_count++;
    return h;
}

int64_t rigi_mq_create(void)
{
    RigiMq *q;
    RigiMqHandle *owner;
    int64_t id;
    rigi_mq_ensure();
    RIGI_MQ_LOCK();
    q = (RigiMq *)rigi_track_malloc(sizeof(RigiMq));
    memset(q, 0, sizeof(*q));
    q->owner_alive = 1;
    q->next = rigi_mq_queues;
    rigi_mq_queues = q;
    owner = rigi_mq_new_handle(RIGI_MQ_OWNER, q, NULL);
    id = owner->id;
    RIGI_MQ_UNLOCK();
    return id;
}

int64_t rigi_mq_add(int64_t source_id, int64_t type_code)
{
    RigiMqHandle *source;
    RigiMqHandle *derived = NULL;
    RigiMqReader *reader = NULL;
    int64_t result;
    rigi_mq_ensure();
    RIGI_MQ_LOCK();
    source = rigi_mq_find_handle(source_id);
    if (source == NULL || source->released)
    {
        result = RIGI_MQ_ERR_RELEASED;
        goto out;
    }
    if (type_code == RIGI_MQ_OWNER)
    {
        result = RIGI_MQ_ERR_DERIVE_OWNER;
        goto out;
    }
    if (type_code == RIGI_MQ_SENDER)
    {
        if (source->type == RIGI_MQ_READER)
        {
            result = RIGI_MQ_ERR_DERIVE_SENDER;
            goto out;
        }
        if (source->queue->sealed)
        {
            result = RIGI_MQ_ERR_SEALED_SENDER;
            goto out;
        }
        source->queue->sender_count++;
        derived = rigi_mq_new_handle(RIGI_MQ_SENDER, source->queue, NULL);
    }
    else if (type_code == RIGI_MQ_READER)
    {
        if (source->type == RIGI_MQ_SENDER)
        {
            result = RIGI_MQ_ERR_DERIVE_READER;
            goto out;
        }
        reader = (RigiMqReader *)rigi_track_malloc(sizeof(RigiMqReader));
        /* 新 reader cursor 从创建时队尾起（新订阅语义，§12.1）；
         * 事件句柄闸内创建（rigi_event_create 不触队列状态，闸内安全），
         * 避免 post 收集到半成品 event==0 */
        reader->cursor = rigi_mq_tail(source->queue);
        reader->event = rigi_event_create();
        reader->in_next = 0;
        reader->next = source->queue->readers;
        source->queue->readers = reader;
        derived = rigi_mq_new_handle(RIGI_MQ_READER, source->queue, reader);
        reader->handle_id = derived->id;
    }
    else
    {
        result = RIGI_MQ_ERR_RELEASED;
        goto out;
    }
    result = derived->id;
out:
    RIGI_MQ_UNLOCK();
    return result;
}

int32_t rigi_mq_release(int64_t handle_id)
{
    RigiMqHandle *h;
    RigiMq *q;
    RigiMqReader **rlink;
    int64_t *wake = NULL;
    int wake_count = 0;
    int64_t dead_event = 0;
    int freed = 0;
    int32_t result = 0;
    rigi_mq_ensure();
    RIGI_MQ_LOCK();
    h = rigi_mq_find_handle(handle_id);
    if (h == NULL)
    {
        result = RIGI_MQ_ERR_RELEASED;
        goto out;
    }
    if (h->released)
    {
        result = RIGI_MQ_ERR_DOUBLE_RELEASE;
        goto out;
    }
    h->released = 1;
    q = h->queue;
    q->ref_count--;
    if (h->type == RIGI_MQ_OWNER)
    {
        q->owner_alive = 0;
    }
    else if (h->type == RIGI_MQ_SENDER)
    {
        q->sender_count--;
    }
    else if (h->reader != NULL)
    {
        /* 摘 reader 链 + 事件句柄留存闸外销毁（先信号唤醒 pending
         * next——它将以「句柄已释放」错误收场，再 destroy） */
        for (rlink = &q->readers; *rlink != NULL; rlink = &(*rlink)->next)
        {
            if (*rlink == h->reader)
            {
                *rlink = h->reader->next;
                break;
            }
        }
        dead_event = h->reader->event;
        rigi_track_free(h->reader);
        h->reader = NULL;
    }
    if (!q->sealed && !q->owner_alive && q->sender_count == 0)
    {
        q->sealed = 1;
        wake = rigi_mq_collect_reader_events(q, &wake_count);
    }
    if (q->ref_count == 0)
    {
        rigi_mq_free_queue(q);
        freed = 1;
    }
    else
    {
        rigi_mq_reclaim(q);
    }
out:
    RIGI_MQ_UNLOCK();
    if (dead_event != 0)
    {
        rigi_event_signal(dead_event);
        rigi_event_destroy(dead_event);
    }
    if (wake != NULL)
    {
        rigi_mq_signal_events(wake, wake_count);
    }
    (void)freed;
    return result;
}

int32_t rigi_mq_post(int64_t handle_id, const RigiFatRef *parcel)
{
    RigiMqHandle *h;
    RigiMq *q;
    int64_t *wake = NULL;
    int wake_count = 0;
    int32_t result = 0;
    if (parcel == NULL)
    {
        fprintf(stderr, "rigi_rt: rigi_mq_post 收到空 Parcel 槽（编译器 bug）\n");
        abort();
    }
    rigi_mq_ensure();
    RIGI_MQ_LOCK();
    h = rigi_mq_find_handle(handle_id);
    if (h == NULL || h->released)
    {
        result = RIGI_MQ_ERR_RELEASED;
        goto out;
    }
    if (h->type != RIGI_MQ_SENDER)
    {
        result = RIGI_MQ_ERR_POST;
        goto out;
    }
    q = h->queue;
    if (q->sealed)
    {
        result = RIGI_MQ_ERR_SEALED_POST;
        goto out;
    }
    if (q->len == q->cap)
    {
        size_t ncap = q->cap == 0 ? 8 : q->cap * 2;
        RigiFatRef *nlog = (RigiFatRef *)rigi_track_malloc(ncap * sizeof(RigiFatRef));
        memcpy(nlog, q->log, q->len * sizeof(RigiFatRef));
        rigi_track_free(q->log);
        q->log = nlog;
        q->cap = ncap;
    }
    /* 队列自持一份 +1（watermark/析构对称 release）。
     * 注意：rigi_ref_acquire 的返回是持有侧 payload（tag1 克隆出新块，
     * tag2 原样），type_id 不变——不得把返回值当 type_id 存 */
    q->log[q->len].type_id = parcel->type_id;
    q->log[q->len].payload = rigi_ref_acquire(parcel->type_id, parcel->payload);
    q->len++;
    /* 追加即唤醒（事件信号闸外执行）；并发 post 由大闸串行化 =
     * 全局追加序（§18） */
    wake = rigi_mq_collect_reader_events(q, &wake_count);
    rigi_mq_reclaim(q);
out:
    RIGI_MQ_UNLOCK();
    if (wake != NULL)
    {
        rigi_mq_signal_events(wake, wake_count);
    }
    return result;
}

int32_t rigi_mq_try_next(int64_t handle_id)
{
    RigiMqHandle *h;
    RigiMq *q;
    int32_t result;
    rigi_mq_ensure();
    RIGI_MQ_LOCK();
    h = rigi_mq_find_handle(handle_id);
    if (h == NULL || h->released)
    {
        result = RIGI_MQ_ERR_RELEASED;
        goto out;
    }
    if (h->type != RIGI_MQ_READER || h->reader == NULL)
    {
        result = RIGI_MQ_ERR_NEXT;
        goto out;
    }
    q = h->queue;
    if (h->reader->cursor < rigi_mq_tail(q))
    {
        result = 1;                    /* 有消息：随后 rigi_mq_take 取 */
    }
    else if (q->sealed)
    {
        result = 2;                    /* EOS（§25：sealed ∧ cursor 到队尾） */
    }
    else
    {
        result = 0;                    /* 空：调用方 yield 队列 alarm 重试 */
    }
out:
    RIGI_MQ_UNLOCK();
    return result;
}

void rigi_mq_take(RigiFatRef *out, int64_t handle_id)
{
    RigiMqHandle *h;
    RigiMq *q;
    uint64_t seq;
    rigi_mq_ensure();
    if (out == NULL)
    {
        fprintf(stderr, "rigi_rt: rigi_mq_take 收到空 out 槽（编译器 bug）\n");
        abort();
    }
    out->type_id = 0;
    out->payload = 0;
    RIGI_MQ_LOCK();
    h = rigi_mq_find_handle(handle_id);
    if (h == NULL || h->released || h->type != RIGI_MQ_READER
        || h->reader == NULL)
    {
        fprintf(stderr, "rigi_rt: rigi_mq_take 句柄非法（须先经 try_next 校验）\n");
        RIGI_MQ_UNLOCK();
        abort();
    }
    q = h->queue;
    seq = h->reader->cursor;
    if (seq >= rigi_mq_tail(q))
    {
        fprintf(stderr, "rigi_rt: rigi_mq_take 无消息可取（try_next/take "
            "未配对，编译器 bug）\n");
        RIGI_MQ_UNLOCK();
        abort();
    }
    /* 回调方自持一份 +1（日志仍持其份，watermark 经过时对称 release）；
     * acquire 返回持有侧 payload（tag1 克隆出新块），type_id 不变 */
    out->type_id = q->log[seq - q->base_seq].type_id;
    out->payload = rigi_ref_acquire(q->log[seq - q->base_seq].type_id,
        q->log[seq - q->base_seq].payload);
    h->reader->cursor = seq + 1;
    rigi_mq_reclaim(q);
    RIGI_MQ_UNLOCK();
}

int64_t rigi_mq_alarm(int64_t handle_id)
{
    RigiMqHandle *h;
    int64_t event;
    rigi_mq_ensure();
    RIGI_MQ_LOCK();
    h = rigi_mq_find_handle(handle_id);
    if (h == NULL || h->released || h->type != RIGI_MQ_READER
        || h->reader == NULL)
    {
        event = 0;
    }
    else
    {
        event = h->reader->event;
    }
    RIGI_MQ_UNLOCK();
    return event;
}

int32_t rigi_mq_next_enter(int64_t handle_id)
{
    RigiMqHandle *h;
    int32_t result = 0;
    rigi_mq_ensure();
    RIGI_MQ_LOCK();
    h = rigi_mq_find_handle(handle_id);
    if (h == NULL || h->released)
    {
        result = RIGI_MQ_ERR_RELEASED;
        goto out;
    }
    if (h->type != RIGI_MQ_READER || h->reader == NULL)
    {
        result = RIGI_MQ_ERR_NEXT;
        goto out;
    }
    if (h->reader->in_next)
    {
        result = RIGI_MQ_ERR_OUTSTANDING_NEXT;
        goto out;
    }
    h->reader->in_next = 1;
out:
    RIGI_MQ_UNLOCK();
    return result;
}

void rigi_mq_next_exit(int64_t handle_id)
{
    RigiMqHandle *h;
    rigi_mq_ensure();
    RIGI_MQ_LOCK();
    h = rigi_mq_find_handle(handle_id);
    /* 幂等：finally 路径句柄可能已释放/从未 enter 成功，静默返回 */
    if (h != NULL && h->type == RIGI_MQ_READER && h->reader != NULL)
    {
        h->reader->in_next = 0;
    }
    RIGI_MQ_UNLOCK();
}
