/*
 * Ryu d2s/f2s 核心移植：IEEE 754 → 最短十进制 mantissa * 10^exponent。
 * 出处：https://github.com/ulfjack/ryu（d2s.c / f2s.c），Apache-2.0。
 * 不导出 Ryu 原 to_chars 字符串；呈现层见 stringfmt.c。
 * 表驱动 generic 路径（d2s_full_table）；强制 64-bit 便携乘法，无 SSE。
 */
// Copyright 2018 Ulf Adams
//
// The contents of this file may be used under the terms of the Apache License,
// Version 2.0.
//
//    (See accompanying file LICENSE-Apache or copy at
//     http://www.apache.org/licenses/LICENSE-2.0)
//
// Alternatively, the contents of this file may be used under the terms of
// the Boost Software License, Version 1.0.
//    (See accompanying file LICENSE-Boost or copy at
//     https://www.boost.org/LICENSE_1_0.txt)
//
// Unless required by applicable law or agreed to in writing, this software
// is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
// KIND, either express or implied.

#include "ryu.h"

#include <stdbool.h>
#include <stdint.h>

#include "ryu_common.h"
#include "ryu_d2s_intrinsics.h"
#include "ryu_d2s_full_table.h"
#include "ryu_f2s_intrinsics.h"

#define DOUBLE_MANTISSA_BITS 52
#define DOUBLE_EXPONENT_BITS 11
#define DOUBLE_BIAS 1023

#define FLOAT_MANTISSA_BITS 23
#define FLOAT_EXPONENT_BITS 8
#define FLOAT_BIAS 127

typedef struct floating_decimal_64 {
  uint64_t mantissa;
  int32_t exponent;
} floating_decimal_64;

typedef struct floating_decimal_32 {
  uint32_t mantissa;
  int32_t exponent;
} floating_decimal_32;

static inline floating_decimal_64 d2d(const uint64_t ieeeMantissa, const uint32_t ieeeExponent) {
  int32_t e2;
  uint64_t m2;
  if (ieeeExponent == 0) {
    e2 = 1 - DOUBLE_BIAS - DOUBLE_MANTISSA_BITS - 2;
    m2 = ieeeMantissa;
  } else {
    e2 = (int32_t) ieeeExponent - DOUBLE_BIAS - DOUBLE_MANTISSA_BITS - 2;
    m2 = (1ull << DOUBLE_MANTISSA_BITS) | ieeeMantissa;
  }
  const bool even = (m2 & 1) == 0;
  const bool acceptBounds = even;

  const uint64_t mv = 4 * m2;
  const uint32_t mmShift = ieeeMantissa != 0 || ieeeExponent <= 1;

  uint64_t vr, vp, vm;
  int32_t e10;
  bool vmIsTrailingZeros = false;
  bool vrIsTrailingZeros = false;
  if (e2 >= 0) {
    const uint32_t q = log10Pow2(e2) - (e2 > 3);
    e10 = (int32_t) q;
    const int32_t k = DOUBLE_POW5_INV_BITCOUNT + pow5bits((int32_t) q) - 1;
    const int32_t i = -e2 + (int32_t) q + k;
    vr = mulShiftAll64(m2, DOUBLE_POW5_INV_SPLIT[q], i, &vp, &vm, mmShift);
    if (q <= 21) {
      const uint32_t mvMod5 = ((uint32_t) mv) - 5 * ((uint32_t) div5(mv));
      if (mvMod5 == 0) {
        vrIsTrailingZeros = multipleOfPowerOf5(mv, q);
      } else if (acceptBounds) {
        vmIsTrailingZeros = multipleOfPowerOf5(mv - 1 - mmShift, q);
      } else {
        vp -= multipleOfPowerOf5(mv + 2, q);
      }
    }
  } else {
    const uint32_t q = log10Pow5(-e2) - (-e2 > 1);
    e10 = (int32_t) q + e2;
    const int32_t i = -e2 - (int32_t) q;
    const int32_t k = pow5bits(i) - DOUBLE_POW5_BITCOUNT;
    const int32_t j = (int32_t) q - k;
    vr = mulShiftAll64(m2, DOUBLE_POW5_SPLIT[i], j, &vp, &vm, mmShift);
    if (q <= 1) {
      vrIsTrailingZeros = true;
      if (acceptBounds) {
        vmIsTrailingZeros = mmShift == 1;
      } else {
        --vp;
      }
    } else if (q < 63) {
      vrIsTrailingZeros = multipleOfPowerOf2(mv, q);
    }
  }

  int32_t removed = 0;
  uint8_t lastRemovedDigit = 0;
  uint64_t output;
  if (vmIsTrailingZeros || vrIsTrailingZeros) {
    for (;;) {
      const uint64_t vpDiv10 = div10(vp);
      const uint64_t vmDiv10 = div10(vm);
      if (vpDiv10 <= vmDiv10) {
        break;
      }
      const uint32_t vmMod10 = ((uint32_t) vm) - 10 * ((uint32_t) vmDiv10);
      const uint64_t vrDiv10 = div10(vr);
      const uint32_t vrMod10 = ((uint32_t) vr) - 10 * ((uint32_t) vrDiv10);
      vmIsTrailingZeros &= vmMod10 == 0;
      vrIsTrailingZeros &= lastRemovedDigit == 0;
      lastRemovedDigit = (uint8_t) vrMod10;
      vr = vrDiv10;
      vp = vpDiv10;
      vm = vmDiv10;
      ++removed;
    }
    if (vmIsTrailingZeros) {
      for (;;) {
        const uint64_t vmDiv10 = div10(vm);
        const uint32_t vmMod10 = ((uint32_t) vm) - 10 * ((uint32_t) vmDiv10);
        if (vmMod10 != 0) {
          break;
        }
        const uint64_t vpDiv10 = div10(vp);
        const uint64_t vrDiv10 = div10(vr);
        const uint32_t vrMod10 = ((uint32_t) vr) - 10 * ((uint32_t) vrDiv10);
        vrIsTrailingZeros &= lastRemovedDigit == 0;
        lastRemovedDigit = (uint8_t) vrMod10;
        vr = vrDiv10;
        vp = vpDiv10;
        vm = vmDiv10;
        ++removed;
      }
    }
    if (vrIsTrailingZeros && lastRemovedDigit == 5 && vr % 2 == 0) {
      lastRemovedDigit = 4;
    }
    output = vr + ((vr == vm && (!acceptBounds || !vmIsTrailingZeros)) || lastRemovedDigit >= 5);
  } else {
    bool roundUp = false;
    const uint64_t vpDiv100 = div100(vp);
    const uint64_t vmDiv100 = div100(vm);
    if (vpDiv100 > vmDiv100) {
      const uint64_t vrDiv100 = div100(vr);
      const uint32_t vrMod100 = ((uint32_t) vr) - 100 * ((uint32_t) vrDiv100);
      roundUp = vrMod100 >= 50;
      vr = vrDiv100;
      vp = vpDiv100;
      vm = vmDiv100;
      removed += 2;
    }
    for (;;) {
      const uint64_t vpDiv10 = div10(vp);
      const uint64_t vmDiv10 = div10(vm);
      if (vpDiv10 <= vmDiv10) {
        break;
      }
      const uint64_t vrDiv10 = div10(vr);
      const uint32_t vrMod10 = ((uint32_t) vr) - 10 * ((uint32_t) vrDiv10);
      roundUp = vrMod10 >= 5;
      vr = vrDiv10;
      vp = vpDiv10;
      vm = vmDiv10;
      ++removed;
    }
    output = vr + (vr == vm || roundUp);
  }

  floating_decimal_64 fd;
  fd.exponent = e10 + removed;
  fd.mantissa = output;
  return fd;
}

static inline bool d2d_small_int(const uint64_t ieeeMantissa, const uint32_t ieeeExponent,
  floating_decimal_64* const v) {
  const uint64_t m2 = (1ull << DOUBLE_MANTISSA_BITS) | ieeeMantissa;
  const int32_t e2 = (int32_t) ieeeExponent - DOUBLE_BIAS - DOUBLE_MANTISSA_BITS;

  if (e2 > 0) {
    return false;
  }
  if (e2 < -52) {
    return false;
  }

  const uint64_t mask = (1ull << -e2) - 1;
  const uint64_t fraction = m2 & mask;
  if (fraction != 0) {
    return false;
  }

  v->mantissa = m2 >> -e2;
  v->exponent = 0;
  return true;
}

static inline floating_decimal_32 f2d(const uint32_t ieeeMantissa, const uint32_t ieeeExponent) {
  int32_t e2;
  uint32_t m2;
  if (ieeeExponent == 0) {
    e2 = 1 - FLOAT_BIAS - FLOAT_MANTISSA_BITS - 2;
    m2 = ieeeMantissa;
  } else {
    e2 = (int32_t) ieeeExponent - FLOAT_BIAS - FLOAT_MANTISSA_BITS - 2;
    m2 = (1u << FLOAT_MANTISSA_BITS) | ieeeMantissa;
  }
  const bool even = (m2 & 1) == 0;
  const bool acceptBounds = even;

  const uint32_t mv = 4 * m2;
  const uint32_t mp = 4 * m2 + 2;
  const uint32_t mmShift = ieeeMantissa != 0 || ieeeExponent <= 1;
  const uint32_t mm = 4 * m2 - 1 - mmShift;

  uint32_t vr, vp, vm;
  int32_t e10;
  bool vmIsTrailingZeros = false;
  bool vrIsTrailingZeros = false;
  uint8_t lastRemovedDigit = 0;
  if (e2 >= 0) {
    const uint32_t q = log10Pow2(e2);
    e10 = (int32_t) q;
    const int32_t k = FLOAT_POW5_INV_BITCOUNT + pow5bits((int32_t) q) - 1;
    const int32_t i = -e2 + (int32_t) q + k;
    vr = mulPow5InvDivPow2(mv, q, i);
    vp = mulPow5InvDivPow2(mp, q, i);
    vm = mulPow5InvDivPow2(mm, q, i);
    if (q != 0 && (vp - 1) / 10 <= vm / 10) {
      const int32_t l = FLOAT_POW5_INV_BITCOUNT + pow5bits((int32_t) (q - 1)) - 1;
      lastRemovedDigit = (uint8_t) (mulPow5InvDivPow2(mv, q - 1, -e2 + (int32_t) q - 1 + l) % 10);
    }
    if (q <= 9) {
      if (mv % 5 == 0) {
        vrIsTrailingZeros = multipleOfPowerOf5_32(mv, q);
      } else if (acceptBounds) {
        vmIsTrailingZeros = multipleOfPowerOf5_32(mm, q);
      } else {
        vp -= multipleOfPowerOf5_32(mp, q);
      }
    }
  } else {
    const uint32_t q = log10Pow5(-e2);
    e10 = (int32_t) q + e2;
    const int32_t i = -e2 - (int32_t) q;
    const int32_t k = pow5bits(i) - FLOAT_POW5_BITCOUNT;
    int32_t j = (int32_t) q - k;
    vr = mulPow5divPow2(mv, (uint32_t) i, j);
    vp = mulPow5divPow2(mp, (uint32_t) i, j);
    vm = mulPow5divPow2(mm, (uint32_t) i, j);
    if (q != 0 && (vp - 1) / 10 <= vm / 10) {
      j = (int32_t) q - 1 - (pow5bits(i + 1) - FLOAT_POW5_BITCOUNT);
      lastRemovedDigit = (uint8_t) (mulPow5divPow2(mv, (uint32_t) (i + 1), j) % 10);
    }
    if (q <= 1) {
      vrIsTrailingZeros = true;
      if (acceptBounds) {
        vmIsTrailingZeros = mmShift == 1;
      } else {
        --vp;
      }
    } else if (q < 31) {
      vrIsTrailingZeros = multipleOfPowerOf2_32(mv, q - 1);
    }
  }

  int32_t removed = 0;
  uint32_t output;
  if (vmIsTrailingZeros || vrIsTrailingZeros) {
    while (vp / 10 > vm / 10) {
      vmIsTrailingZeros &= vm - (vm / 10) * 10 == 0;
      vrIsTrailingZeros &= lastRemovedDigit == 0;
      lastRemovedDigit = (uint8_t) (vr % 10);
      vr /= 10;
      vp /= 10;
      vm /= 10;
      ++removed;
    }
    if (vmIsTrailingZeros) {
      while (vm % 10 == 0) {
        vrIsTrailingZeros &= lastRemovedDigit == 0;
        lastRemovedDigit = (uint8_t) (vr % 10);
        vr /= 10;
        vp /= 10;
        vm /= 10;
        ++removed;
      }
    }
    if (vrIsTrailingZeros && lastRemovedDigit == 5 && vr % 2 == 0) {
      lastRemovedDigit = 4;
    }
    output = vr + ((vr == vm && (!acceptBounds || !vmIsTrailingZeros)) || lastRemovedDigit >= 5);
  } else {
    while (vp / 10 > vm / 10) {
      lastRemovedDigit = (uint8_t) (vr % 10);
      vr /= 10;
      vp /= 10;
      vm /= 10;
      ++removed;
    }
    output = vr + (vr == vm || lastRemovedDigit >= 5);
  }

  floating_decimal_32 fd;
  fd.exponent = e10 + removed;
  fd.mantissa = output;
  return fd;
}

void rigi_ryu_d2d(double value, rigi_ryu_dec *out)
{
    const uint64_t bits = double_to_bits(value);
    const int ieeeSign = (int) ((bits >> (DOUBLE_MANTISSA_BITS + DOUBLE_EXPONENT_BITS)) & 1);
    const uint64_t ieeeMantissa = bits & ((1ull << DOUBLE_MANTISSA_BITS) - 1);
    const uint32_t ieeeExponent = (uint32_t) ((bits >> DOUBLE_MANTISSA_BITS) & ((1u << DOUBLE_EXPONENT_BITS) - 1));
    out->negative = ieeeSign;
    if (ieeeExponent == ((1u << DOUBLE_EXPONENT_BITS) - 1u)) {
        out->kind = ieeeMantissa != 0 ? RIGI_RYU_NAN : RIGI_RYU_INF;
        out->mantissa = 0;
        out->exponent = 0;
        return;
    }
    if (ieeeExponent == 0 && ieeeMantissa == 0) {
        out->kind = RIGI_RYU_FINITE;
        out->mantissa = 0;
        out->exponent = 0;
        return;
    }
    floating_decimal_64 v;
    if (d2d_small_int(ieeeMantissa, ieeeExponent, &v)) {
        for (;;) {
            const uint64_t q = div10(v.mantissa);
            const uint32_t r = ((uint32_t) v.mantissa) - 10 * ((uint32_t) q);
            if (r != 0) {
                break;
            }
            v.mantissa = q;
            ++v.exponent;
        }
    } else {
        v = d2d(ieeeMantissa, ieeeExponent);
    }
    out->kind = RIGI_RYU_FINITE;
    out->mantissa = v.mantissa;
    out->exponent = v.exponent;
}

void rigi_ryu_f2d(float value, rigi_ryu_dec *out)
{
    const uint32_t bits = float_to_bits(value);
    const int ieeeSign = (int) ((bits >> (FLOAT_MANTISSA_BITS + FLOAT_EXPONENT_BITS)) & 1);
    const uint32_t ieeeMantissa = bits & ((1u << FLOAT_MANTISSA_BITS) - 1);
    const uint32_t ieeeExponent = (bits >> FLOAT_MANTISSA_BITS) & ((1u << FLOAT_EXPONENT_BITS) - 1);
    out->negative = ieeeSign;
    if (ieeeExponent == ((1u << FLOAT_EXPONENT_BITS) - 1u)) {
        out->kind = ieeeMantissa != 0 ? RIGI_RYU_NAN : RIGI_RYU_INF;
        out->mantissa = 0;
        out->exponent = 0;
        return;
    }
    if (ieeeExponent == 0 && ieeeMantissa == 0) {
        out->kind = RIGI_RYU_FINITE;
        out->mantissa = 0;
        out->exponent = 0;
        return;
    }
    const floating_decimal_32 v = f2d(ieeeMantissa, ieeeExponent);
    out->kind = RIGI_RYU_FINITE;
    out->mantissa = v.mantissa;
    out->exponent = v.exponent;
}
