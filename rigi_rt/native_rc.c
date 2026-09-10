/* NativeRcHandle 强引用注册表；纯 C11，零平台锁依赖。 */
#include "native_rc.h"
#include "arc.h"

#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>

typedef struct RigiNativeRcEntry
{
    uint64_t token;
    uint64_t strong;
    void *payload;
    RigiNativeRcDestroy destroy;
    struct RigiNativeRcEntry *next;
} RigiNativeRcEntry;

static atomic_flag rigi_nrc_gate = ATOMIC_FLAG_INIT;
static _Atomic uint64_t rigi_nrc_next = 1;
static RigiNativeRcEntry *rigi_nrc_entries = NULL;

static void rigi_nrc_lock(void)
{
    while (atomic_flag_test_and_set_explicit(&rigi_nrc_gate,
        memory_order_acquire))
    {
    }
}

static void rigi_nrc_unlock(void)
{
    atomic_flag_clear_explicit(&rigi_nrc_gate, memory_order_release);
}

static RigiNativeRcEntry *rigi_nrc_find(uint64_t token)
{
    RigiNativeRcEntry *entry;
    for (entry = rigi_nrc_entries; entry != NULL; entry = entry->next)
    {
        if (entry->token == token) return entry;
    }
    return NULL;
}

int64_t rigi_native_rc_create(void *payload, RigiNativeRcDestroy destroy)
{
    RigiNativeRcEntry *entry;
    uint64_t token;
    if (payload == NULL || destroy == NULL)
    {
        fprintf(stderr, "rigi_rt: NativeRc create 收到空 payload/destroy（运行时 bug）\n");
        abort();
    }
    token = atomic_fetch_add_explicit(&rigi_nrc_next, 1, memory_order_relaxed);
    if (token == 0 || token > (uint64_t)INT64_MAX)
    {
        fprintf(stderr, "rigi_rt: NativeRc token 空间耗尽\n");
        abort();
    }
    entry = (RigiNativeRcEntry *)rigi_track_malloc(sizeof(RigiNativeRcEntry));
    entry->token = token;
    entry->strong = 1;
    entry->payload = payload;
    entry->destroy = destroy;
    rigi_nrc_lock();
    entry->next = rigi_nrc_entries;
    rigi_nrc_entries = entry;
    rigi_nrc_unlock();
    return (int64_t)token;
}

int32_t rigi_native_rc_retain(int64_t token)
{
    RigiNativeRcEntry *entry;
    if (token <= 0) return 0;
    rigi_nrc_lock();
    entry = rigi_nrc_find((uint64_t)token);
    if (entry == NULL)
    {
        rigi_nrc_unlock();
        return 0;
    }
    if (entry->strong == UINT64_MAX)
    {
        rigi_nrc_unlock();
        fprintf(stderr, "rigi_rt: NativeRc 强引用计数溢出\n");
        abort();
    }
    entry->strong++;
    rigi_nrc_unlock();
    return 1;
}

void rigi_native_rc_release(int64_t token)
{
    RigiNativeRcEntry **link;
    RigiNativeRcEntry *entry;
    RigiNativeRcDestroy destroy;
    void *payload;
    if (token <= 0)
    {
        fprintf(stderr, "rigi_rt: NativeRc release 收到空 token（运行时 bug）\n");
        abort();
    }
    rigi_nrc_lock();
    for (link = &rigi_nrc_entries; *link != NULL; link = &(*link)->next)
    {
        if ((*link)->token == (uint64_t)token) break;
    }
    entry = *link;
    if (entry == NULL || entry->strong == 0)
    {
        rigi_nrc_unlock();
        fprintf(stderr, "rigi_rt: NativeRc release 收到无效或已释放 token（运行时 bug）\n");
        abort();
    }
    entry->strong--;
    if (entry->strong != 0)
    {
        rigi_nrc_unlock();
        return;
    }
    *link = entry->next;
    destroy = entry->destroy;
    payload = entry->payload;
    rigi_nrc_unlock();

    /* 析构不在全局闸内执行；它可以递归释放其它 NativeRc 资源。 */
    destroy(payload);
    rigi_track_free(entry);
}

void *rigi_native_rc_payload_of(int64_t token, const char *face)
{
    RigiNativeRcEntry *entry;
    void *payload;
    if (token <= 0)
    {
        fprintf(stderr, "rigi_rt: %s 收到空 NativeRc token（运行时 bug）\n", face);
        abort();
    }
    rigi_nrc_lock();
    entry = rigi_nrc_find((uint64_t)token);
    if (entry == NULL || entry->strong == 0)
    {
        rigi_nrc_unlock();
        fprintf(stderr, "rigi_rt: %s 收到已释放 NativeRc token（运行时 bug）\n", face);
        abort();
    }
    payload = entry->payload;
    rigi_nrc_unlock();
    return payload;
}
