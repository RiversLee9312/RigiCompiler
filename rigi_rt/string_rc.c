/*
 * String ARC（MW7a）：堆块 { rc u32; reserved u32; char data[] }，
 * 对外指针为块基址 + 8。IMMORTAL 字面量跳过增减与释放。
 * acquire/release 自包含 region。
 */
#include "arc.h"

#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>

typedef struct
{
    uint32_t rc;
    uint32_t reserved;
} RigiStringBlock;

char *rigi_string_new(int64_t len)
{
    RigiStringBlock *block;
    if (len < 0)
    {
        fprintf(stderr, "rigi_rt: string_new len < 0\n");
        abort();
    }
    block = (RigiStringBlock *)rigi_track_malloc(
        sizeof(RigiStringBlock) + (size_t)len);
    block->rc = 1;
    block->reserved = 0;
    return (char *)(block + 1);
}

void rigi_string_acquire(const char *data)
{
    RigiStringBlock *block;
    rigi_region_enter();
    if (data == NULL)
    {
        rigi_region_exit();
        return;
    }
    block = (RigiStringBlock *)(void *)(data - sizeof(RigiStringBlock));
    if (block->rc != RIGI_STRING_IMMORTAL)
    {
        atomic_fetch_add_explicit((_Atomic uint32_t *)&block->rc, 1,
            memory_order_relaxed);
    }
    rigi_region_exit();
}

/* GC 已停世界时不能再次进入 mutator fence；共享同一字符串释放实现。 */
void rigi_string_release_unfenced(const char *data)
{
    RigiStringBlock *block;
    if (data == NULL)
    {
        return;
    }
    block = (RigiStringBlock *)(void *)(data - sizeof(RigiStringBlock));
    if (block->rc != RIGI_STRING_IMMORTAL
        && atomic_fetch_sub_explicit((_Atomic uint32_t *)&block->rc, 1,
               memory_order_acq_rel) == 1)
    {
        rigi_track_free(block);
    }
}

void rigi_string_release(const char *data)
{
    rigi_region_enter();
    rigi_string_release_unfenced(data);
    rigi_region_exit();
}
