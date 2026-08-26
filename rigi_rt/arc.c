/*
 * rigi_rt ARC 面族实现（MW4 批 1 + MW7a）：alloc/acquire/release +
 * 值语义四面族 + region 协议 + 析构（数组 / kind 感知 refMap）。
 * 生成代码只见四面族与 string 面；析构级联复用嵌套 region 计数。
 */
#include "arc.h"

#include <stdatomic.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* MW12 前恒 IDLE；region 最外层见非 0 则 stub abort */
uint32_t rigi_gc_flag = 0;

static _Thread_local uint32_t rigi_region_depth = 0;
static _Thread_local uint32_t rigi_region_c_flag = 0;

void rigi_region_enter(void)
{
    if (rigi_region_depth++ == 0)
    {
        if (rigi_gc_flag != 0)
        {
            fprintf(stderr, "rigi_rt: gc fence 未就绪（MW12）\n");
            abort();
        }
        rigi_region_c_flag = 2; /* PROCESSING */
    }
}

void rigi_region_exit(void)
{
    if (--rigi_region_depth == 0)
    {
        rigi_region_c_flag = 0;
    }
    (void)rigi_region_c_flag;
}

void *rigi_alloc(const RigiTypeSheet *desc)
{
    RigiObjectHeader *object = (RigiObjectHeader *)rigi_track_malloc(desc->typeSize);
    memset(object, 0, desc->typeSize);
    object->typeId = desc;
    object->rc = 1;
    return object;
}

void rigi_acquire_local(void *object)
{
    rigi_region_enter();
    if (object != NULL)
    {
        ((RigiObjectHeader *)object)->rc += 1;
    }
    rigi_region_exit();
}

void rigi_acquire_shared(void *object)
{
    rigi_region_enter();
    if (object != NULL)
    {
        atomic_fetch_add_explicit(
            &(((_Atomic uint32_t *)&((RigiObjectHeader *)object)->rc)[0]),
            1, memory_order_relaxed);
    }
    rigi_region_exit();
}

/* §25 IDisposable 检查挂点（MW4 批 1 no-op）：typeFlags 含 DISPOSABLE
 * 时的 dispose 调用在此挂接，随资源合约批定稿 */
static void rigi_dispose_hook(void *object, const RigiTypeSheet *desc)
{
    (void)object;
    (void)desc;
}

static void rigi_value_walk(void *ptr, const RigiTypeSheet *sheet, bool is_acquire)
{
    const char *base;
    size_t cursor;
    uint32_t i;
    if (ptr == NULL || sheet == NULL || sheet->refMap == NULL)
    {
        return;
    }
    base = (const char *)ptr;
    cursor = 0;
    for (i = 0; i < sheet->refMapSize; i++)
    {
        uint16_t entry = sheet->refMap[i];
        uint16_t kind = (uint16_t)(entry >> RIGI_REFMAP_KIND_SHIFT);
        uint16_t hops = (uint16_t)(entry & RIGI_REFMAP_HOP_MASK);
        cursor += (size_t)hops * 16u;
        if (kind == RIGI_REFMAP_STRING)
        {
            char *data = *(char *const *)(base + cursor);
            if (is_acquire)
            {
                rigi_string_acquire(data);
            }
            else
            {
                rigi_string_release(data);
            }
        }
        else
        {
            uint64_t type_id = *(const uint64_t *)(base + cursor);
            uint64_t payload = *(const uint64_t *)(base + cursor + 8);
            if (is_acquire)
            {
                (void)rigi_ref_acquire(type_id, payload);
            }
            else
            {
                rigi_ref_release(type_id, payload);
            }
        }
        cursor += 16u;
    }
}

void rigi_value_acquire(void *ptr, const RigiTypeSheet *sheet)
{
    rigi_region_enter();
    rigi_value_walk(ptr, sheet, true);
    rigi_region_exit();
}

void rigi_value_release(void *ptr, const RigiTypeSheet *sheet)
{
    rigi_region_enter();
    rigi_value_walk(ptr, sheet, false);
    rigi_region_exit();
}

uint64_t rigi_ref_acquire(uint64_t type_id, uint64_t payload)
{
    uint64_t tag;
    const RigiTypeSheet *sheet;
    void *block;
    rigi_region_enter();
    tag = type_id >> RIGI_TAG_SHIFT;
    if (tag == RIGI_TAG_OBJECT)
    {
        if (payload == 0)
        {
            rigi_region_exit();
            return payload;
        }
        sheet = (const RigiTypeSheet *)(uintptr_t)(type_id & RIGI_SHEET_MASK);
        if (sheet != NULL && (sheet->typeFlags & RIGI_TYPE_SHARED) != 0)
        {
            rigi_acquire_shared((void *)(uintptr_t)payload);
        }
        else
        {
            rigi_acquire_local((void *)(uintptr_t)payload);
        }
        rigi_region_exit();
        return payload;
    }
    if (tag == RIGI_TAG_HEAP_VALUE)
    {
        /* 克隆语义：unique 裸块深拷贝 + 内部引用 acquire */
        sheet = (const RigiTypeSheet *)(uintptr_t)(type_id & RIGI_SHEET_MASK);
        if (sheet == NULL || payload == 0)
        {
            rigi_region_exit();
            return payload;
        }
        block = rigi_track_malloc(sheet->typeSize);
        memcpy(block, (void *)(uintptr_t)payload, sheet->typeSize);
        rigi_value_acquire(block, sheet);
        rigi_region_exit();
        return (uint64_t)(uintptr_t)block;
    }
    /* tag0：原样返回 */
    rigi_region_exit();
    return payload;
}

void rigi_ref_release(uint64_t type_id, uint64_t payload)
{
    uint64_t tag;
    const RigiTypeSheet *sheet;
    rigi_region_enter();
    tag = type_id >> RIGI_TAG_SHIFT;
    if (tag == RIGI_TAG_OBJECT)
    {
        if (payload != 0)
        {
            sheet = (const RigiTypeSheet *)(uintptr_t)(type_id & RIGI_SHEET_MASK);
            if (sheet != NULL && (sheet->typeFlags & RIGI_TYPE_SHARED) != 0)
            {
                rigi_release_shared((void *)(uintptr_t)payload);
            }
            else
            {
                rigi_release_local((void *)(uintptr_t)payload);
            }
        }
        rigi_region_exit();
        return;
    }
    if (tag == RIGI_TAG_HEAP_VALUE)
    {
        if (payload != 0)
        {
            sheet = (const RigiTypeSheet *)(uintptr_t)(type_id & RIGI_SHEET_MASK);
            rigi_value_release((void *)(uintptr_t)payload, sheet);
            rigi_track_free((void *)(uintptr_t)payload);
        }
        rigi_region_exit();
        return;
    }
    /* tag0：无操作 */
    rigi_region_exit();
}

/* release 归零的库内自动析构：数组走元素表；否则挂点 → kind 感知
 * refMap → track_free。嵌套走查经 region 计数天然安全。 */
static void rigi_destruct(void *object, const RigiTypeSheet *desc)
{
    const char *base = (const char *)object;
    size_t cursor;
    uint32_t i;

    if (desc != NULL && (desc->typeFlags & RIGI_TYPE_ARRAY) != 0)
    {
        const RigiTypeSheet *elemSheet =
            *(RigiTypeSheet *const *)((char *)object + 16);
        int32_t len = *(int32_t *)((char *)object + 24);
        const char *elems = (const char *)object + 32;
        int32_t stride;
        int32_t idx;
        if (elemSheet != NULL && (elemSheet->typeFlags & RIGI_TYPE_INLINE_VALUE) != 0
            && elemSheet->typeSize > 0)
        {
            stride = (int32_t)elemSheet->typeSize;
        }
        else
        {
            stride = 16;
        }
        for (idx = 0; idx < len; idx++)
        {
            void *elem = (void *)(elems + (size_t)idx * (size_t)stride);
            if (elemSheet == NULL)
            {
                continue;
            }
            if ((elemSheet->typeFlags & RIGI_TYPE_STRING) != 0)
            {
                rigi_string_release(*(char *const *)elem);
            }
            else if ((elemSheet->typeFlags & RIGI_TYPE_INLINE_VALUE) != 0)
            {
                if (elemSheet->refMapSize > 0
                    || (elemSheet->typeFlags & RIGI_TYPE_RICH) != 0)
                {
                    rigi_value_release(elem, elemSheet);
                }
            }
            else
            {
                uint64_t type_id = *(const uint64_t *)elem;
                uint64_t payload = *(const uint64_t *)((const char *)elem + 8);
                rigi_ref_release(type_id, payload);
            }
        }
        rigi_track_free(object);
        return;
    }

    cursor = sizeof(RigiObjectHeader);
    rigi_dispose_hook(object, desc);
    if (desc == NULL || desc->refMap == NULL)
    {
        rigi_track_free(object);
        return;
    }
    for (i = 0; i < desc->refMapSize; i++)
    {
        uint16_t entry = desc->refMap[i];
        uint16_t kind = (uint16_t)(entry >> RIGI_REFMAP_KIND_SHIFT);
        uint16_t hops = (uint16_t)(entry & RIGI_REFMAP_HOP_MASK);
        cursor += (size_t)hops * 16u;
        if (kind == RIGI_REFMAP_STRING)
        {
            rigi_string_release(*(char *const *)(base + cursor));
        }
        else
        {
            uint64_t type_id = *(const uint64_t *)(base + cursor);
            uint64_t payload = *(const uint64_t *)(base + cursor + 8);
            rigi_ref_release(type_id, payload);
        }
        cursor += 16u;
    }
    rigi_track_free(object);
}

void rigi_release_local(void *object)
{
    RigiObjectHeader *header = (RigiObjectHeader *)object;
    rigi_region_enter();
    if (header != NULL && --header->rc == 0)
    {
        rigi_destruct(object, header->typeId);
    }
    rigi_region_exit();
}

void rigi_release_shared(void *object)
{
    RigiObjectHeader *header = (RigiObjectHeader *)object;
    rigi_region_enter();
    if (header != NULL
        && atomic_fetch_sub_explicit(
               &(((_Atomic uint32_t *)&header->rc)[0]),
               1, memory_order_acq_rel) == 1)
    {
        rigi_destruct(object, header->typeId);
    }
    rigi_region_exit();
}
