/*
 * 数组分配与负长度 abort 面（MW4 / MW7a / MW7b）：alloc_contiguous
 * 为数组与 Span 共用的同构分配体（INLINE_VALUE 选步长、32B 前缀），
 * alloc_array 只是面函数。消息与 VM VmException 原文对齐，退出码 1。
 * MW9b-G：写越界 abort 面 rigi_abort_array_oob 退场——越界写改抛可被
 * try/catch 捕获的 core.OutOfBoundException（守卫由 Middleware 发射）。
 */
#include "arc.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define RIGI_ARRAY_PREFIX 32u

void *rigi_malloc(int32_t size)
{
    void *block;
    if (size < 0)
    {
        fprintf(stderr, "rigi_rt: malloc size < 0\n");
        abort();
    }
    block = rigi_track_malloc((size_t)size);
    memset(block, 0, (size_t)size);
    return block;
}

_Noreturn void rigi_abort_array_negative_length(void)
{
    static const char message[] = "数组长度不能为负\n";
    fwrite(message, 1, sizeof(message) - 1, stderr);
    fflush(stderr);
    /* 可在 worker 线程命中：exit 经 atexit 链会与主线程互锁挂死
     *（arc.c rigi_alloc 同口径），_Exit 跳过 atexit 直接终止 */
    _Exit(1);
}

void *rigi_alloc_contiguous(const RigiTypeSheet *sheet, const RigiTypeSheet *elemSheet,
    int32_t len)
{
    int32_t stride;
    size_t bytes;
    RigiObjectHeader *object;

    /* 类型具化失败必须在分配前拒绝，禁止创建身份被擦除的数组。 */
    if (sheet == NULL || elemSheet == NULL)
    {
        fprintf(stderr, "rigi_rt: contiguous type identity unavailable\n");
        /* 与 arc.c rigi_alloc 同口径：worker 命中时 exit 经 atexit 挂死 */
        fflush(NULL);
        _Exit(1);
    }
    if (len < 0)
    {
        rigi_abort_array_negative_length();
    }
    if (elemSheet != NULL && (elemSheet->typeFlags & RIGI_TYPE_INLINE_VALUE) != 0
        && elemSheet->typeSize > 0)
    {
        stride = (int32_t)elemSheet->typeSize;
    }
    else
    {
        stride = 16;
    }
    bytes = (size_t)RIGI_ARRAY_PREFIX + (size_t)len * (size_t)stride;
    object = (RigiObjectHeader *)rigi_track_malloc(bytes);
    memset(object, 0, bytes);
    object->typeId = sheet;
    object->rc = 1;
    *(const RigiTypeSheet **)((char *)object + 16) = elemSheet;
    *(int32_t *)((char *)object + 24) = len;
    return object;
}

void *rigi_alloc_array(const RigiTypeSheet *arraySheet, const RigiTypeSheet *elemSheet,
    int32_t len)
{
    return rigi_alloc_contiguous(arraySheet, elemSheet, len);
}
