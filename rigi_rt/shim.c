/*
 * rigi_rt shim 库 MW1 最小面（MIDDLEWARE_ARCHITECTURE §4.8）——纯 C11，寄生 libc，
 * 跨 win-x64/linux-x64 仅用标准 CRT（stdio.h/stdlib.h/string.h/stdint.h），
 * 不依赖任何 Windows 专用 API。本库经 EmbeddedResource 内嵌进编译器程序集，
 * 由 RigiRtBuilder 现场用 clang 编成 LLVM bitcode（unity build）参与 lld 链接。
 * 调用约定：默认 C 约定。
 */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "arc.h"
#include "rigi_string.h"

/* 写 stdout + fflush：fwrite 直接按 len 输出，不依赖 NUL 结尾 */
void rigi_print(const rigi_string *text)
{
    if (text->len > 0)
    {
        fwrite(text->data, 1, (size_t)text->len, stdout);
    }
    fflush(stdout);
}

/* 写 stderr + fflush */
void rigi_print_err(const rigi_string *text)
{
    if (text->len > 0)
    {
        fwrite(text->data, 1, (size_t)text->len, stderr);
    }
    fflush(stderr);
}

/* 经 rigi_string_new 分配 rc=1 字符串块并拼接 */
void rigi_string_concat(rigi_string *out, const rigi_string *a, const rigi_string *b)
{
    int64_t len = a->len + b->len;
    char *data = rigi_string_new(len);
    if (len > 0)
    {
        if (a->len > 0)
        {
            memcpy(data, a->data, (size_t)a->len);
        }
        if (b->len > 0)
        {
            memcpy(data + a->len, b->data, (size_t)b->len);
        }
    }
    out->data = data;
    out->len = len;
}

/* 三态字符串比较：UTF-8 字节序字典序（memcmp 按无符号字节）。eq/ne 即内容
 * 相等——UTF-8 编码唯一，字节序列相等当且仅当串相等；BMP 内字典序与 BIL VM
 * 基准（string.CompareOrdinal，UTF-16 码元序）一致；astral 平面（代理对）
 * 的码元序与码点序分歧随 MW7 胖值化定稿 */
int32_t rigi_string_compare(const rigi_string *a, const rigi_string *b)
{
    int64_t shared = a->len < b->len ? a->len : b->len;
    int cmp = shared > 0 ? memcmp(a->data, b->data, (size_t)shared) : 0;
    if (cmp != 0)
    {
        return cmp < 0 ? -1 : 1;
    }
    if (a->len < b->len)
    {
        return -1;
    }
    if (a->len > b->len)
    {
        return 1;
    }
    return 0;
}

/* MW2 占位检查面（BIL §11.2 的 DividedByZeroException 在 MW9 异常机制
 * 落地前的占位语义）：stderr 文本与 BIL VM 的未捕获异常消息逐字节一致，
 * 退出码对齐 vm 命令的未捕获异常出口（1）。MW9 换真异常时由 Emit 的
 * 标量检查策略注入点单点替换，本面随之退役 */
_Noreturn void rigi_abort_divided_by_zero(void)
{
    static const char message[] = "整数除以零\n";
    fwrite(message, 1, sizeof(message) - 1, stderr);
    fflush(stderr);
    exit(1);
}

/* i64 MIN/-1 的 VM 基准行为是基础设施溢出失败（.NET OverflowException
 * 经 VM 包装后的消息原文），i8/i16/i32 则回绕（不走本面） */
_Noreturn void rigi_abort_arithmetic_overflow(void)
{
    static const char message[] = "Arithmetic operation resulted in an overflow.\n";
    fwrite(message, 1, sizeof(message) - 1, stderr);
    fflush(stderr);
    exit(1);
}

/* 拆箱类型不符（MW5 Box）：VM 抛 CastException「无法将 .any 转换为 T」，
 * 本面在 MW9 真异常落地前占位——前缀对齐 VM 口径，目标名取 TypeInfo.name */
_Noreturn void rigi_abort_invalid_cast(const RigiTypeSheet *target)
{
    static const char prefix[] = "无法将 .any 转换为 ";
    static const char fallback[] = "目标值类型";
    const RigiTypeInfo *info;
    fwrite(prefix, 1, sizeof(prefix) - 1, stderr);
    info = target != NULL ? target->typeInfoId : NULL;
    if (info != NULL && info->name.data != NULL && info->name.len > 0)
    {
        fwrite(info->name.data, 1, (size_t)info->name.len, stderr);
    }
    else
    {
        fwrite(fallback, 1, sizeof(fallback) - 1, stderr);
    }
    fwrite("\n", 1, 1, stderr);
    fflush(stderr);
    exit(1);
}

/* 由编译器发射（BIL entrypoint fn） */
extern int32_t rigi_entry(void);
extern void rigi_globals_cleanup(void);

int main(void)
{
    atexit(rigi_mem_report);       /* 先注册后执行：报告最后跑 */
    atexit(rigi_globals_cleanup);  /* LIFO：cleanup 先于 report 执行 */
    return rigi_entry();
}
