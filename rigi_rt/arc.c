/*
 * rigi_rt ARC 面族实现（MW4 批 1 + MW7a）：alloc/acquire/release +
 * 值语义四面族 + region 协议 + 析构（数组 / kind 感知 refMap）。
 * 生成代码只见四面族与 string 面；析构级联复用嵌套 region 计数。
 */
#include "arc.h"
#include "gexc.h"
#include "macrogc.h"

#include <stdatomic.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* region 嵌套计数（TLS）；最外层 enter/exit 的 fence 双重检查与
 * cFlag 协议在 macrogc.c（MW12 上线：gc_flag 非 IDLE 时 ENTERING
 * 阻塞等 GCAlarm，恢复后完整重检，codegen 零变化） */
static _Thread_local uint32_t rigi_region_depth = 0;

void rigi_region_enter(void)
{
    if (rigi_region_depth++ == 0)
    {
        rigi_gc_region_fence_enter();
    }
}

void rigi_region_exit(void)
{
    if (--rigi_region_depth == 0)
    {
        rigi_gc_region_fence_exit();
    }
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

/* §25.2 IDisposable 销毁时强制检查（MW12b 真检查）：typeFlags 含
 * DISPOSABLE 且 disposed 位未置位 → undisposed-resource 全局异常事件
 * 入队（gexc.c；派发时机与晚到规则见 gexc.h）。绝不代跑 dispose、
 * 不延迟释放、不复活。三销毁入口共用：microGC/microSGC 经
 * rigi_destruct，macroGC 清理步经 macrogc.c gc_teardown。
 * MW11c 棒5a：旧 C 侧 Task/sleep EventAlarm 簿记已随调度面删除——
 * Task 是 Rigi 对象（refMap 扫描字段），时钟底座由 Worker 定时器
 * 原语 + Rigi SleepAlarm/Timer 持有句柄，不再经本钩子拆除。 */
void rigi_dispose_check(void *object, const RigiTypeSheet *desc)
{
    RigiObjectHeader *hdr = (RigiObjectHeader *)object;
    uint32_t pf;
    if (hdr == NULL || desc == NULL
        || (desc->typeFlags & RIGI_TYPE_DISPOSABLE) == 0)
    {
        return;
    }
    pf = atomic_load_explicit(
        (const _Atomic uint32_t *)&hdr->packedFlags, memory_order_relaxed);
    if ((pf & RIGI_PF_DISPOSED) == 0)
    {
        rigi_gexc_report_undisposed(desc);
    }
}

void rigi_mark_disposed(void *object)
{
    if (object != NULL)
    {
        atomic_fetch_or_explicit(
            (_Atomic uint32_t *)&((RigiObjectHeader *)object)->packedFlags,
            RIGI_PF_DISPOSED, memory_order_relaxed);
    }
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
                uint64_t new_payload = rigi_ref_acquire(type_id, payload);
                /* tag1 克隆语义（unique 裸块深拷贝）产生新块指针——嵌入槽
                 * 必须回写新 payload，否则 memcpy 副本与原值共持同一块，
                 * 双侧 release 即双释放/UAF（tag0/tag2 返回值不变，回写
                 * 无害）。release 走查只读，无回写。 */
                *(uint64_t *)(void *)(base + cursor + 8) = new_payload;
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

/* release 归零的库内自动析构：数组走元素表；否则 §25 挂点 → kind 感知
 * refMap → track_free。嵌套走查经 region 计数天然安全。
 * MW12：头部先摘除在册候选（swap-remove；非候选零开销）。 */
static void rigi_destruct(void *object, const RigiTypeSheet *desc)
{
    const char *base = (const char *)object;
    size_t cursor;
    uint32_t i;

    rigi_gc_forget_candidate(object);

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
                /* new List<K> 开放构造未写入隐藏 T 时 elemSheet 为空，
                 * 元素仍按 16B 胖槽存储（TryStoreViaTypeId → Any/AssignFat）。 */
                uint64_t type_id = *(const uint64_t *)elem;
                uint64_t payload = *(const uint64_t *)((const char *)elem + 8);
                rigi_ref_release(type_id, payload);
                continue;
            }
            if ((elemSheet->typeFlags & RIGI_TYPE_STRING) != 0)
            {
                /* 泛型 List\<T\> 对 String 仍按胖引用写入（tag1 盒）；
                 * 仅当槽是真 String ABI 才 string_release。 */
                uint64_t elem_tid = *(const uint64_t *)elem;
                uint64_t elem_tag = elem_tid >> RIGI_TAG_SHIFT;
                if (elem_tag == RIGI_TAG_HEAP_VALUE || elem_tag == RIGI_TAG_OBJECT)
                {
                    uint64_t elem_pl = *(const uint64_t *)((const char *)elem + 8);
                    rigi_ref_release(elem_tid, elem_pl);
                }
                else
                {
                    rigi_string_release(*(char *const *)elem);
                }
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
    rigi_dispose_check(object, desc);
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
    if (header != NULL)
    {
        if (--header->rc == 0)
        {
            rigi_destruct(object, header->typeId);
        }
        else
        {
            /* MW12：减至非零 → 候选登记 + 债务累计 + 阈值触发（同 region） */
            rigi_gc_note_release(object, header->typeId);
        }
    }
    rigi_region_exit();
}

void rigi_release_shared(void *object)
{
    RigiObjectHeader *header = (RigiObjectHeader *)object;
    uint32_t old;
    rigi_region_enter();
    if (header != NULL)
    {
        old = atomic_fetch_sub_explicit(
            &(((_Atomic uint32_t *)&header->rc)[0]), 1, memory_order_acq_rel);
        if (old == 1)
        {
            rigi_destruct(object, header->typeId);
        }
        else
        {
            /* MW12：减至非零 → 候选登记 + 债务累计 + 阈值触发（同 region） */
            rigi_gc_note_release(object, header->typeId);
        }
    }
    rigi_region_exit();
}
