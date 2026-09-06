/*
 * 异常传输面族实现（MW9a 第一刀）：线程局部 pending 槽三面。
 * acquire/release 分派复用 arc.c 既有的 local/shared 口径（按对象头
 * typeId 的 SHARED 位分派，与 rigi_ref_acquire 的 tag2 分支一致）；
 * 此处只有裸对象指针，sheet 取自对象头自身。
 * region 安全：面内自包含——增减计数经 arc.c 面各自带 region 进出，
 * 三面本身不分配/释放堆内存、不触碰任何可抛设施。
 */
#include "eh.h"

#include <stddef.h>
#include <stdlib.h>

#include "arc.h"

/* 线程局部 pending 槽（C11 _Thread_local，clang win/linux 双平台均支持）。
 * MW11 协程跨 Worker 迁移时需在 frame 保存/恢复 pending；此处按 OS
 * 线程隔离即正确。 */
static _Thread_local void *rigi_exc_pending_slot = NULL;

/* local/shared 分派与 arc.c rigi_ref_acquire 的 tag2 分支同口径；
 * 两路计数均原子化，支持同一失败 Task 的异常图由多个 waiter 持有。 */
static void rigi_exc_object_acquire(void *object)
{
    const RigiTypeSheet *sheet = ((const RigiObjectHeader *)object)->typeId;
    if (sheet != NULL && (sheet->typeFlags & (RIGI_TYPE_SHARED | RIGI_TYPE_ARRAY)) != 0)
    {
        rigi_acquire_shared(object);
    }
    else
    {
        rigi_acquire_local(object);
    }
}

static void rigi_exc_object_release(void *object)
{
    const RigiTypeSheet *sheet = ((const RigiObjectHeader *)object)->typeId;
    if (sheet != NULL && (sheet->typeFlags & (RIGI_TYPE_SHARED | RIGI_TYPE_ARRAY)) != 0)
    {
        rigi_release_shared(object);
    }
    else
    {
        rigi_release_local(object);
    }
}

void rigi_exc_raise(void *exception_object)
{
    if (exception_object == NULL)
    {
        return;
    }
    /* 防御：槽已非空属编译器 bug（上一异常未沿异常边取走又抛新异常），
     * 先 release 旧值再覆盖，避免泄漏 */
    if (rigi_exc_pending_slot != NULL)
    {
        rigi_exc_object_release(rigi_exc_pending_slot);
    }
    rigi_exc_object_acquire(exception_object);
    rigi_exc_pending_slot = exception_object;
}

void *rigi_exc_pending(void)
{
    /* 借用语义：只读不写，计数不变 */
    return rigi_exc_pending_slot;
}

void *rigi_exc_take(void)
{
    /* 移动语义：槽内 +1 所有权随返回值交给调用方，不做任何计数变更 */
    void *object = rigi_exc_pending_slot;
    rigi_exc_pending_slot = NULL;
    return object;
}

void rigi_type_name_of(void *obj, rigi_string *out)
{
    const RigiTypeSheet *sheet;
    const RigiTypeInfo *info;
    if (obj == NULL)
    {
        out->data = NULL;
        out->len = 0;
        return;
    }
    sheet = ((const RigiObjectHeader *)obj)->typeId;
    info = sheet != NULL ? sheet->typeInfoId : NULL;
    if (info == NULL)
    {
        /* 防御：无 TypeInfo 的 sheet 写空串（诊断名缺失不致崩） */
        out->data = NULL;
        out->len = 0;
        return;
    }
    /* 借用拷出：TypeInfo.name 自持存储（字面量永生），不另 acquire */
    *out = info->name;
}

void rigi_exc_halt(void)
{
    /* noreturn：未捕获异常进程出口，退出码对齐 VM 未捕获口径 */
    exit(1);
}
