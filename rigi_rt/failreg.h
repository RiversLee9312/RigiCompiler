#ifndef RIGI_FAILREG_H
#define RIGI_FAILREG_H
#include <stdint.h>
#include "coroutine.h"

/* Task 内部析构仅摘册，不进入 ARC 或执行用户代码。 */
void rigi_failure_release_task(int64_t id);
/* 把异常绑定到 Task 的隐藏 refMap 槽，保留真实 GC 拥有边。 */
void rigi_failure_bind(int64_t id, RigiFatRef *slot);
#endif
