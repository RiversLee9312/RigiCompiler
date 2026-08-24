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

/* 字符串过渡表示（MIDDLEWARE MW1：{data,len} UTF-8；MW7 胖值化时迁移） */
typedef struct { const char *data; int64_t len; } rigi_string;

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

/* malloc 新缓冲区拼接；len=0 的边界按 (size_t)0 处理，malloc(0) 返回的指针
 * 直接透传给调用方（生命周期归调用方 free） */
void rigi_string_concat(rigi_string *out, const rigi_string *a, const rigi_string *b)
{
    int64_t len = a->len + b->len;
    char *data = (char *)malloc((size_t)len);
    if (len > 0 && data != NULL)
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
    out->len = data != NULL ? len : 0;
}

/* 由编译器发射（BIL entrypoint fn） */
extern int32_t rigi_entry(void);

int main(void)
{
    return rigi_entry();
}
