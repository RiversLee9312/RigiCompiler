/*
 * Span / SharedSpan 分配面（MW7b）：与数组同构，区别仅在 sheet 身份
 * （core::Span / core::SharedSpan）。析构复用 RIGI_TYPE_ARRAY 走查，
 * 不另开析构路径。
 */
#include "arc.h"
#include "coroutine.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

void *rigi_span_alloc(const RigiTypeSheet *spanSheet, const RigiTypeSheet *elemSheet,
    int32_t len)
{
    /* 与数组同构，区别仅在 sheet 身份（core::Span / core::SharedSpan） */
    return rigi_alloc_contiguous(spanSheet, elemSheet, len);
}

/*
 * B2-4a：Span<u8> native ABI 验证原语（stdlib core.native 的
 * rigi_span_u8_echo / VM hook "span_u8_echo" 双宿主同语义）。
 *
 * buffer = 16B 胖引用槽指针（D6：C 边界胖值一律指针）；payload = 缓冲区
 * 对象基址，与数组同构 32B 前缀（arc.h：对象头[0..16) + elemSheet[16..24)
 * + length i32[24..28) + pad，元素基址 32）。
 * 语义：data[offset .. offset+count) 逐字节 XOR 0xFF 写回原位置，返回
 * 处理字节数——同时验证「读 + 写 + 返回」三面的回声原语（后续标准流
 * read/write 原语消费同一 ABI）。区间越界属编译器 bug（Rigi 侧包装层
 * 先行校验；VM 侧同形态抛 VmException），诊断 abort。
 */
int32_t rigi_span_u8_echo(const RigiFatRef *buffer, int32_t offset, int32_t count)
{
    if (buffer == NULL || buffer->payload == 0)
    {
        fprintf(stderr, "rigi_rt: rigi_span_u8_echo 收到空缓冲区（编译器 bug）\n");
        abort();
    }
    uint8_t *base = (uint8_t *)(uintptr_t)buffer->payload;
    int32_t length;
    memcpy(&length, base + 24, sizeof(int32_t));
    if (offset < 0 || count < 0 || offset > length - count)
    {
        fprintf(stderr, "rigi_rt: rigi_span_u8_echo 区间越界：offset=%d count=%d "
            "length=%d（编译器 bug）\n", offset, count, length);
        abort();
    }
    uint8_t *data = base + 32;
    for (int32_t i = 0; i < count; i++)
    {
        data[offset + i] ^= 0xFFu;
    }
    return count;
}
