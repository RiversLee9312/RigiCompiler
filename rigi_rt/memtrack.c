/*
 * 内存台账（MW7a）：每分配 16B 头 {size_t size; size_t pad;}，
 * live 块数与字节数用 C11 _Atomic long 计数。默认关闭报告；
 * RIGI_RT_MEMTRACK 置位且 live != 0 时向 stderr 打印并 exit(1)。
 */
#ifdef _WIN32
#define _CRT_SECURE_NO_WARNINGS
#endif
#include "arc.h"

#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct
{
    size_t size;
    size_t pad;
} RigiMemHdr;

static _Atomic long rigi_live_blocks = 0;
static _Atomic long rigi_live_bytes = 0;

void *rigi_track_malloc(size_t size)
{
    RigiMemHdr *hdr = (RigiMemHdr *)malloc(sizeof(RigiMemHdr) + size);
    if (hdr == NULL)
    {
        fprintf(stderr, "rigi_rt: out of memory (track_malloc %zu)\n", size);
        abort();
    }
    hdr->size = size;
    hdr->pad = 0;
    atomic_fetch_add_explicit(&rigi_live_blocks, 1, memory_order_relaxed);
    atomic_fetch_add_explicit(&rigi_live_bytes, (long)size, memory_order_relaxed);
    return (void *)(hdr + 1);
}

void rigi_track_free(void *p)
{
    RigiMemHdr *hdr;
    size_t size;
    if (p == NULL)
    {
        return;
    }
    hdr = ((RigiMemHdr *)p) - 1;
    size = hdr->size;
    memset(p, 0xDD, size);
    atomic_fetch_sub_explicit(&rigi_live_blocks, 1, memory_order_relaxed);
    atomic_fetch_sub_explicit(&rigi_live_bytes, (long)size, memory_order_relaxed);
    free(hdr);
}

void rigi_mem_report(void)
{
    long blocks;
    long bytes;
    if (getenv("RIGI_RT_MEMTRACK") == NULL)
    {
        return;
    }
    blocks = atomic_load_explicit(&rigi_live_blocks, memory_order_relaxed);
    bytes = atomic_load_explicit(&rigi_live_bytes, memory_order_relaxed);
    if (blocks != 0)
    {
        fprintf(stderr, "rigi_rt: memory leak: %ld blocks, %ld bytes\n",
            blocks, bytes);
        exit(1);
    }
}
