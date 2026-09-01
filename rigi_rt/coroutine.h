/*
 * 协程 ABI 共享类型（MW11c 棒5a 瘦身，RUNTIME §17.4）：RigiFatRef /
 * RigiResumeCode。旧 C 调度面（rigi_spawn / wait / yield /
 * executor_run / make_sleep_alarm）已删除；调度在 Rigi Dispatcher，
 * native 原语见 worker.h / cohandle.h / failreg.c。
 * 纯 C11；与 shim.c 同纪律。
 */
#ifndef RIGI_COROUTINE_H
#define RIGI_COROUTINE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* 胖引用 16B 槽的 C 镜像（arc.h refMap 注释的 FATREF 槽布局：
 * [0..8) typeid（最高字节 tag，见 RIGI_TAG_*）+ [8..16) payload）。
 * 零值 {0,0} = tag0 null，acquire/release 均为无操作。 */
typedef struct RigiFatRef
{
    uint64_t type_id;
    uint64_t payload;
} RigiFatRef;

/* 恢复函数原型：编译器状态机 fn 的 C 投影，返回本次执行的归宿
 *（cohandle resume 与 Dispatcher.workerLoop 同口径） */
typedef enum RigiResumeCode
{
    RIGI_RESUME_SUSPENDED = 0, /* 已挂起（waiter 已登记或已重发布），交还执行权 */
    RIGI_RESUME_YIELDED   = 1, /* 裸 yield 直返：调用方负责重发布 */
    RIGI_RESUME_DONE      = 2, /* 到达终态（complete/fail 已在状态机内部调过） */
} RigiResumeCode;
typedef RigiResumeCode (*RigiResumeFn)(void *frame);

#ifdef __cplusplus
}
#endif

#endif /* RIGI_COROUTINE_H */
