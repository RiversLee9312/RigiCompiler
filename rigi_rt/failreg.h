#ifndef RIGI_FAILREG_H
#define RIGI_FAILREG_H
#include <stdint.h>
#include "coroutine.h"

/* Task 内部析构仅摘册，不进入 ARC 或执行用户代码。 */
void rigi_failure_release_task(int64_t id);
/* 把异常绑定到 Task 的隐藏 refMap 槽，保留真实 GC 拥有边。 */
void rigi_failure_bind(int64_t id, RigiFatRef *slot);
int64_t rigi_failure_record(const RigiFatRef *exc);
void rigi_failure_drop(int64_t id);
int32_t rigi_failure_get(int64_t id, RigiFatRef *out);
int32_t rigi_failure_take_unobserved(RigiFatRef *out);
#endif
