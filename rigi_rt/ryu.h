/*
 * Ryu 最短往返十进制（d2s/f2s 核心）：mantissa * 10^exponent。
 * 出处：https://github.com/ulfjack/ryu（ulfjack/ryu，Apache-2.0）。
 * 呈现层（.NET ToString 默认形态）在 stringfmt.c，不在本头。
 */
#ifndef RIGI_RYU_H
#define RIGI_RYU_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

enum {
    RIGI_RYU_FINITE = 0,
    RIGI_RYU_NAN = 1,
    RIGI_RYU_INF = 2
};

typedef struct {
    int kind;
    int negative;
    uint64_t mantissa;
    int32_t exponent;
} rigi_ryu_dec;

void rigi_ryu_d2d(double value, rigi_ryu_dec *out);
void rigi_ryu_f2d(float value, rigi_ryu_dec *out);

#ifdef __cplusplus
}
#endif
#endif
