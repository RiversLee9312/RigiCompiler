/*
 * rigi_rt 系统随机原语（施工块 6-5，STDLIB §4.11.4 / D7）——stdlib
 * core/math.rg 的 priv native 原语 rigi_sys_random_u64（@NativeSymbol
 * 短名 "sys_random_u64"）的 C 侧实现：为 Random 无参构造取得完整
 * 8 字节系统随机材料，按小端解释为种子（字节序组装在 Rigi 包装层，
 * 双宿主同路径）。
 *
 * 语义契约（§4.11.4）：
 *   - 只要求材料质量，不要求 VM/native 同种子（无参构造本来就无
 *     可复现性承诺）；材料必须来自系统随机源——Windows 用
 *     BCryptGenRandom（BCRYPT_USE_SYSTEM_PREFERRED_RNG），Linux 用
 *     getrandom（glibc ≥2.25，libc 内，无需额外链接库），getrandom
 *     不可用时回退 /dev/urandom 全量读取；
 *   - 获取失败返回 -1（Rigi 包装层换抛 core.IOException，不静默回退
 *     为时间戳/零/固定种子）。一切失败路径（含缓冲区形态不符）都
 *     不得伪造材料；
 *   - 此内部能力不新增 core.system、不对外提供密码学随机 API（D7）。
 *
 * C 边界 ABI：Span<u8> → 16B 胖引用指针（coroutine.h 的 RigiFatRef；
 * payload = 缓冲区对象基址，与数组同构 32B 前缀，length i32 在
 * [24..28)，元素基址 32）。
 */
#include "coroutine.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#include <windows.h>
#include <bcrypt.h>
#else
#include <errno.h>
#include <fcntl.h>
#include <sys/random.h>
#include <unistd.h>
#endif

/* 填充 8 字节系统随机材料：0 成功 / -1 失败。 */
static int rigi_sys_random_fill(uint8_t *out)
{
#if defined(_WIN32)
    /* BCryptGenRandom 一次取足 8 字节（系统优选 RNG；不维护算法
     * 句柄，NULL provider 形态）。NTSTATUS 非成功即失败，不重试、
     * 不回退到弱源。 */
    NTSTATUS status = BCryptGenRandom(NULL, out, 8,
        BCRYPT_USE_SYSTEM_PREFERRED_RNG);
    return (status >= 0) ? 0 : -1;
#else
    /* getrandom 自 2.25 起在 libc：EINTR 重试，部分读循环补齐；
     * 8 字节远低于 getrandom 的熵节流上限（4096），部分读只可能
     * 来自信号中断。 */
    size_t done = 0;
    while (done < 8)
    {
        ssize_t n = getrandom(out + done, 8 - done, 0);
        if (n > 0)
        {
            done += (size_t)n;
            continue;
        }
        if (n < 0 && errno == EINTR)
        {
            continue;
        }
        /* ENOSYS（老内核/非 glibc）等一切失败：回退 /dev/urandom */
        break;
    }
    if (done == 8)
    {
        return 0;
    }
    {
        int fd = open("/dev/urandom", O_RDONLY);
        if (fd < 0)
        {
            return -1;
        }
        size_t got = 0;
        while (got < 8)
        {
            ssize_t n = read(fd, out + got, 8 - got);
            if (n > 0)
            {
                got += (size_t)n;
                continue;
            }
            if (n < 0 && errno == EINTR)
            {
                continue;
            }
            break;
        }
        close(fd);
        return (got == 8) ? 0 : -1;
    }
#endif
}

/* sys_random_u64 原生面：把 8 字节系统随机材料按小端顺序写入
 * buffer[0..8)，返回 0；获取失败返回 -1（包装层抛 core.IOException）。
 * 空胖引用/缓冲区不足 8 字节属编译器 bug（包装层恒传 spanOf<u8>(8)），
 * 诊断 abort（span_u8_echo 同纪律），绝不伪造材料。 */
int32_t rigi_sys_random_u64(const RigiFatRef *buffer)
{
    uint8_t *base;
    int32_t length;
    uint8_t bytes[8];

    if (buffer == NULL || buffer->payload == 0)
    {
        fprintf(stderr, "rigi_rt: sys_random_u64 收到空缓冲区（编译器 bug）\n");
        abort();
    }
    base = (uint8_t *)(uintptr_t)buffer->payload;
    memcpy(&length, base + 24, sizeof(int32_t));
    if (length < 8)
    {
        fprintf(stderr, "rigi_rt: sys_random_u64 缓冲区不足 8 字节（编译器 bug）\n");
        abort();
    }
    if (rigi_sys_random_fill(bytes) != 0)
    {
        return -1;
    }
    memcpy(base + 32, bytes, 8);
    return 0;
}
