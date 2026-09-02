/*
 * MessageQueue 传输层 native 面（MW11d-C）：只搬运/存储 Parcel 胖引用，
 * 不回调 Rigi 做序列化。句柄 id 进程内单调递增不复用。
 * C 符号 = rigi_ + @NativeSymbol（RuntimeFaces.MapNativeSymbol）。
 */
#ifndef RIGI_MESSAGE_H
#define RIGI_MESSAGE_H

#include <stdint.h>
#include "coroutine.h"

#ifdef __cplusplus
extern "C" {
#endif

int64_t rigi_mq_create(void);
int64_t rigi_mq_add(int64_t source_id, int64_t type_code);
int32_t rigi_mq_release(int64_t handle_id);
int32_t rigi_mq_post(int64_t handle_id, const RigiFatRef *parcel);
int32_t rigi_mq_try_next(int64_t handle_id);
void rigi_mq_take(RigiFatRef *out, int64_t handle_id);
int64_t rigi_mq_alarm(int64_t handle_id);
int32_t rigi_mq_next_enter(int64_t handle_id);
void rigi_mq_next_exit(int64_t handle_id);

#ifdef __cplusplus
}
#endif

#endif /* RIGI_MESSAGE_H */
