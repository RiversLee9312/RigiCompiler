/*
 * 数组分配与越界/负长度 abort 面（MW4）：alloc_array 按元素 TypeSheet
 * 的 INLINE_VALUE 位选择步长（值类型 typeSize 内联，否则 16B 胖槽），
 * 对象头走传入的 Array TypeSheet；length 写在偏移 16。消息与 VM
 * VmException 原文对齐，退出码 1。
 */
#include "arc.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define RIGI_ARRAY_PREFIX 24u

void *rigi_malloc(int32_t size)
{
    void *block;
    if (size < 0)
    {
        fprintf(stderr, "rigi_rt: malloc size < 0\n");
        abort();
    }
    block = malloc((size_t)size);
    if (block == NULL)
    {
        fprintf(stderr, "rigi_rt: out of memory (malloc %d)\n", size);
        abort();
    }
    memset(block, 0, (size_t)size);
    return block;
}

_Noreturn void rigi_abort_array_negative_length(void)
{
    static const char message[] = "数组长度不能为负\n";
    fwrite(message, 1, sizeof(message) - 1, stderr);
    fflush(stderr);
    exit(1);
}

_Noreturn void rigi_abort_array_oob(int32_t index, int32_t length)
{
    fprintf(stderr, "数组下标越界：%d（长度 %d）\n", index, length);
    fflush(stderr);
    exit(1);
}

void *rigi_alloc_array(const RigiTypeSheet *arraySheet, const RigiTypeSheet *elemSheet,
    int32_t len)
{
    int32_t stride;
    size_t bytes;
    RigiObjectHeader *object;

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
    object = (RigiObjectHeader *)malloc(bytes);
    if (object == NULL)
    {
        fprintf(stderr, "rigi_rt: out of memory (array len=%d stride=%d)\n", len, stride);
        abort();
    }
    memset(object, 0, bytes);
    object->typeId = arraySheet;
    object->rc = 1;
    memcpy((char *)object + 16, &len, sizeof(len));
    return object;
}
