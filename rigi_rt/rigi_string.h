/*
 * String 过渡表示（MIDDLEWARE MW1：{data,len} UTF-8；MW7 胖值化时迁移）。
 * C 边界一律经 rigi_string* 传递（StringOut 出参置首参）。
 */
#ifndef RIGI_STRING_H
#define RIGI_STRING_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct { const char *data; int64_t len; } rigi_string;

#ifdef __cplusplus
}
#endif
#endif
