/*
 * core.text 字节原语（施工块 3-2，STDLIB §4.3.1）：String 的 UTF-8 字节
 * 访问面最小两个 priv native（stdlib core/text/text.rg 声明）。
 *
 *   rigi_text_copy_out —— 把 String 的 UTF-8 字节段拷入 Span<u8>（范围
 *     校验失败按约定返回负值错误码，Rigi 侧包装层换抛
 *     core.OutOfBoundException；与 VM hook TextCopyOut 双宿主同语义）。
 *   rigi_text_from_bytes —— 从 Span<u8> 字节段重建 String（严格 UTF-8
 *     校验；这是切片结果的重建通道，切片保证边界合法，正常路径零失败）。
 *     C 宿主无法抛语言级异常：范围/编码失败属「Rigi 包装层先行校验后
 *     不可能到达」的编译器 bug 路径，诊断 abort（span_u8_echo 同纪律）；
 *     VM 侧同规则抛可捕获的 core::OutOfBoundException（行为参考宿主）。
 *
 * C 边界 ABI（NativeCallEmitter / StringAbi）：String → rigi_string*
 * （返回走 out 首参）；Span → 16B 胖引用指针（payload = 缓冲区对象基址，
 * 与数组同构 32B 前缀，元素基址 32，length i32 在 [24..28)）。
 */
#include "arc.h"
#include "coroutine.h"
#include "rigi_string.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/*
 * 严格 UTF-8（WFF）校验：结构完整 + 拒绝过长编码 + 拒绝代理区
 * （U+D800–DFFF）+ 拒绝 > U+10FFFF。返回 1 合法 / 0 非法。
 * VM 侧 TextFromBytes hook 同规则——双宿主对「合法 UTF-8」的判定必须
 * 一致；3-4 编解码器块复用同一口径。
 */
int rigi_utf8_validate(const uint8_t *data, int32_t count)
{
    int32_t i = 0;
    while (i < count)
    {
        uint8_t b0 = data[i];
        if (b0 < 0x80u)
        {
            i++;
            continue;
        }
        if (b0 < 0xC2u)
        {
            return 0; /* 续字节作首字节；C0/C1 为过长编码 */
        }
        if (b0 < 0xE0u)
        { /* 2 字节序列 */
            if (i + 1 >= count || (data[i + 1] & UINT8_C(0xC0)) != UINT8_C(0x80))
            {
                return 0;
            }
            i += 2;
            continue;
        }
        if (b0 < 0xF0u)
        { /* 3 字节序列：E0 后须 A0..BF（防过长）；ED 后须 80..9F（防代理） */
            uint8_t b1;
            if (i + 2 >= count)
            {
                return 0;
            }
            b1 = data[i + 1];
            if ((b1 & UINT8_C(0xC0)) != UINT8_C(0x80))
            {
                return 0;
            }
            if (b0 == 0xE0u && b1 < 0xA0u)
            {
                return 0;
            }
            if (b0 == 0xEDu && b1 > 0x9Fu)
            {
                return 0;
            }
            if ((data[i + 2] & UINT8_C(0xC0)) != UINT8_C(0x80))
            {
                return 0;
            }
            i += 3;
            continue;
        }
        if (b0 < 0xF5u)
        { /* 4 字节序列：F0 后须 90..BF；F4 后须 80..8F（封顶 U+10FFFF） */
            uint8_t b1;
            if (i + 3 >= count)
            {
                return 0;
            }
            b1 = data[i + 1];
            if ((b1 & UINT8_C(0xC0)) != UINT8_C(0x80))
            {
                return 0;
            }
            if (b0 == 0xF0u && b1 < 0x90u)
            {
                return 0;
            }
            if (b0 == 0xF4u && b1 > 0x8Fu)
            {
                return 0;
            }
            if ((data[i + 2] & UINT8_C(0xC0)) != UINT8_C(0x80)
                || (data[i + 3] & UINT8_C(0xC0)) != UINT8_C(0x80))
            {
                return 0;
            }
            i += 4;
            continue;
        }
        return 0; /* F5..FF 非法首字节 */
    }
    return 1;
}

/*
 * 把 src 的 UTF-8 字节段 [srcOffset, srcOffset + count) 拷入
 * dest[destOffset .. destOffset + count)。成功返回 count（0 合法）；
 * 源范围非法返回 -1、目标范围非法返回 -2（Rigi 包装层换抛
 * core.OutOfBoundException）。空缓冲区对象属编译器 bug，诊断 abort。
 */
int32_t rigi_text_copy_out(const rigi_string *src, int64_t src_offset,
    const RigiFatRef *dest, int32_t dest_offset, int32_t count)
{
    const uint8_t *base;
    int32_t dest_len;
    if (src == NULL || dest == NULL || dest->payload == 0)
    {
        fprintf(stderr, "rigi_rt: rigi_text_copy_out 收到空源串/缓冲区（编译器 bug）\n");
        abort();
    }
    if (src_offset < 0 || count < 0 || src_offset > src->len - (int64_t)count)
    {
        return -1; /* 源字节段越界 */
    }
    base = (const uint8_t *)(uintptr_t)dest->payload;
    memcpy(&dest_len, base + 24, sizeof(int32_t));
    if ((int64_t)dest_offset > (int64_t)dest_len - (int64_t)count)
    {
        return -2; /* 目标 Span 段越界 */
    }
    if (count > 0)
    {
        memcpy((void *)(base + 32 + dest_offset), src->data + src_offset,
            (size_t)count);
    }
    return count;
}

/*
 * 从 bytes[offset .. offset + count) 严格校验 UTF-8 后重建 String
 *（out 首参出参，rigi_string_new 分配独立 rc=1 块——切片结果的重建
 * 通道）。范围/编码失败属编译器 bug 路径（Rigi 包装层先行校验；VM
 * 参考宿主抛 core::OutOfBoundException），诊断 abort。
 */
void rigi_text_from_bytes(rigi_string *out, const RigiFatRef *bytes,
    int32_t offset, int32_t count)
{
    const uint8_t *base;
    int32_t len;
    char *data;
    if (out == NULL || bytes == NULL || bytes->payload == 0)
    {
        fprintf(stderr, "rigi_rt: rigi_text_from_bytes 收到空出参/缓冲区（编译器 bug）\n");
        abort();
    }
    base = (const uint8_t *)(uintptr_t)bytes->payload;
    memcpy(&len, base + 24, sizeof(int32_t));
    if (offset < 0 || count < 0 || (int64_t)offset > (int64_t)len - (int64_t)count)
    {
        fprintf(stderr, "rigi_rt: rigi_text_from_bytes 段越界：offset=%d count=%d "
            "length=%d（编译器 bug）\n", offset, count, len);
        abort();
    }
    if (!rigi_utf8_validate(base + 32 + offset, count))
    {
        fprintf(stderr, "rigi_rt: rigi_text_from_bytes 非法 UTF-8 序列："
            "offset=%d count=%d（编译器 bug）\n", offset, count);
        abort();
    }
    data = rigi_string_new(count);
    if (count > 0)
    {
        memcpy(data, base + 32 + offset, (size_t)count);
    }
    out->data = data;
    out->len = count;
}
