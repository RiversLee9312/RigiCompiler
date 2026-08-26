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
//
// 出处：https://github.com/ulfjack/ryu/blob/master/ryu/f2s_intrinsics.h
#ifndef RYU_F2S_INTRINSICS_H
#define RYU_F2S_INTRINSICS_H

#include <assert.h>
#include <stdint.h>

#include "ryu_common.h"
#include "ryu_d2s_full_table.h"

#define FLOAT_POW5_INV_BITCOUNT (DOUBLE_POW5_INV_BITCOUNT - 64)
#define FLOAT_POW5_BITCOUNT (DOUBLE_POW5_BITCOUNT - 64)

static inline uint32_t pow5factor_32(uint32_t value) {
  uint32_t count = 0;
  for (;;) {
    assert(value != 0);
    const uint32_t q = value / 5;
    const uint32_t r = value % 5;
    if (r != 0) {
      break;
    }
    value = q;
    ++count;
  }
  return count;
}

static inline bool multipleOfPowerOf5_32(const uint32_t value, const uint32_t p) {
  return pow5factor_32(value) >= p;
}

static inline bool multipleOfPowerOf2_32(const uint32_t value, const uint32_t p) {
  return (value & ((1u << p) - 1)) == 0;
}

static inline uint32_t mulShift32(const uint32_t m, const uint64_t factor, const int32_t shift) {
  assert(shift > 32);

  const uint32_t factorLo = (uint32_t)(factor);
  const uint32_t factorHi = (uint32_t)(factor >> 32);
  const uint64_t bits0 = (uint64_t)m * factorLo;
  const uint64_t bits1 = (uint64_t)m * factorHi;

#if defined(RYU_32_BIT_PLATFORM)
  const uint32_t bits0Hi = (uint32_t)(bits0 >> 32);
  uint32_t bits1Lo = (uint32_t)(bits1);
  uint32_t bits1Hi = (uint32_t)(bits1 >> 32);
  bits1Lo += bits0Hi;
  bits1Hi += (bits1Lo < bits0Hi);
  if (shift >= 64) {
    return (uint32_t)(bits1Hi >> (shift - 64));
  } else {
    const int32_t s = shift - 32;
    return (bits1Hi << (32 - s)) | (bits1Lo >> s);
  }
#else
  const uint64_t sum = (bits0 >> 32) + bits1;
  const uint64_t shiftedSum = sum >> (shift - 32);
  assert(shiftedSum <= UINT32_MAX);
  return (uint32_t) shiftedSum;
#endif
}

static inline uint32_t mulPow5InvDivPow2(const uint32_t m, const uint32_t q, const int32_t j) {
  return mulShift32(m, DOUBLE_POW5_INV_SPLIT[q][1] + 1, j);
}

static inline uint32_t mulPow5divPow2(const uint32_t m, const uint32_t i, const int32_t j) {
  return mulShift32(m, DOUBLE_POW5_SPLIT[i][1], j);
}

#endif
