/*
 * rigi_rt ARC 面族实现（MW4 批 1，真实实现非占位）：alloc/acquire/
 * release 四变体 + release 归零的库内自动析构（§25 IDisposable 挂点
 * 本批 no-op → 沿 refMap 释放子引用 → free）。本批尚无生成代码调用
 *（RcInjection 属 MW7），正确性由批 2 端到端兜底。
 */
#include "arc.h"

#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

void *rigi_alloc(const RigiTypeSheet *desc)
{
    RigiObjectHeader *object = (RigiObjectHeader *)malloc(desc->typeSize);
    if (object == NULL)
    {
        /* 内存耗尽无语言异常可抛：响亮终止 */
        fprintf(stderr, "rigi_rt: out of memory (typeSize=%u)\n", desc->typeSize);
        abort();
    }
    memset(object, 0, desc->typeSize);
    object->typeId = desc;
    object->rc = 1;
    return object;
}

void rigi_acquire_local(void *object)
{
    if (object != NULL)
    {
        ((RigiObjectHeader *)object)->rc += 1;
    }
}

void rigi_acquire_shared(void *object)
{
    if (object != NULL)
    {
        atomic_fetch_add_explicit(
            &(((_Atomic uint32_t *)&((RigiObjectHeader *)object)->rc)[0]),
            1, memory_order_relaxed);
    }
}

/* §25 IDisposable 检查挂点（MW4 批 1 no-op）：typeFlags 含 DISPOSABLE
 * 时的 dispose 调用在此挂接，随资源合约批定稿 */
static void rigi_dispose_hook(void *object, const RigiTypeSheet *desc)
{
    (void)object;
    (void)desc;
}

/* release 归零的库内自动析构：挂点 → 沿 refMap 跳数释放子引用 → free。
 * 子引用的 local/shared 由子胖引用 typeid 的 TypeSheet.typeFlags 判定 */
static void rigi_destruct(void *object, const RigiTypeSheet *desc)
{
    const char *base = (const char *)object;
    size_t cursor = sizeof(RigiObjectHeader);   /* 扫描起点 = 对象头末 */
    uint32_t i;
    rigi_dispose_hook(object, desc);
    for (i = 0; i < desc->refMapSize; i++)
    {
        const RigiTypeSheet *childType;
        void *child;
        cursor += (size_t)desc->refMap[i] * 16u;
        /* 子引用 = 胖引用槽 {i64 typeid（高字节 tag）, i64 payload} */
        childType = *(RigiTypeSheet *const *)(base + cursor);
        child = *(void **)(base + cursor + 8);
        /* typeid 高字节是 tag（RUNTIME §2），取 sheet 地址须掩除 */
        childType = (RigiTypeSheet *)((uintptr_t)childType & 0x00FFFFFFFFFFFFFFULL);
        if (child != NULL && childType != NULL)
        {
            if ((childType->typeFlags & RIGI_TYPE_SHARED) != 0)
            {
                rigi_release_shared(child);
            }
            else
            {
                rigi_release_local(child);
            }
        }
        cursor += 16u;
    }
    free(object);
}

void rigi_release_local(void *object)
{
    RigiObjectHeader *header = (RigiObjectHeader *)object;
    if (header != NULL && --header->rc == 0)
    {
        rigi_destruct(object, header->typeId);
    }
}

void rigi_release_shared(void *object)
{
    RigiObjectHeader *header = (RigiObjectHeader *)object;
    if (header != NULL
        && atomic_fetch_sub_explicit(
               &(((_Atomic uint32_t *)&header->rc)[0]),
               1, memory_order_acq_rel) == 1)
    {
        rigi_destruct(object, header->typeId);
    }
}
