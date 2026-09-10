#ifndef RIGI_STRINGFMT_H
#define RIGI_STRINGFMT_H

#include <stdint.h>
#include "arc.h"

void rigi_i64_to_string(rigi_string *out, int64_t value);
void rigi_u64_to_string(rigi_string *out, uint64_t value);
void rigi_f64_to_string(rigi_string *out, double value);
void rigi_f32_to_string(rigi_string *out, float value);
void rigi_bool_to_string(rigi_string *out, int8_t value);
void rigi_char_to_string(rigi_string *out, int16_t value);
int64_t rigi_string_character_count(const rigi_string *value);
void rigi_any_to_string(rigi_string *out, const void *anySlot);
int64_t rigi_any_hash(const void *anySlot);
void rigi_handle_make(void *out, const RigiTypeSheet *sheet, const void *target,
    int32_t kind, uint8_t mutable_value);
void rigi_handle_target(void *out, const void *value);
int32_t rigi_handle_kind(const void *value);

#endif
