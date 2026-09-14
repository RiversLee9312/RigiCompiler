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
/* 3b-β：asMutable 经壳指针共享计数（不新建壳、不二次 acquire）派生
 * 新 capability（mutable 置位、kind 继承源）；sheet 经源对象头读取 */
void rigi_handle_as_mutable(void *out, const void *value);
int32_t rigi_handle_kind(const void *value);
/* 3b-β 壳析构识别与挂点：sheet 名 = ".handle" 判定（len 快筛 +
 * memcmp）；rigi_destruct（普通期）/ gc_teardown_ex（GC 冻结期）
 * 两入口共享 */
int32_t rigi_handle_is_capability(const void *object, const RigiTypeSheet *sheet);
void rigi_handle_capability_release(void *object);
/* 3b-β：capability 双持有槽读取（+16 壳指针）——macrogc 壳锚代理边用 */
void *rigi_shell_of_capability(const void *object);

#endif
