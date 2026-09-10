/*
 * NativeRcHandle 的原生生命周期基建。
 *
 * Carrige 只携带不可复用的数值 token，不保存裸指针，也不拥有强引用。
 * 资源由一个或多个 local NativeRcHandle/运行时所有者持有强引用；最后一个
 * 强引用释放时从注册表摘除，再调用资源专属析构。过期 Carrige 的 retain
 * 只会查找失败，绝不会解引用已经释放的地址。
 */
#ifndef RIGI_NATIVE_RC_H
#define RIGI_NATIVE_RC_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef void (*RigiNativeRcDestroy)(void *payload);

/* 库内创建/取值面。create 产生一份初始强引用；token 永不为 0、进程内
 * 永不复用。payload_of 只允许强引用持有者调用。 */
int64_t rigi_native_rc_create(void *payload, RigiNativeRcDestroy destroy);
void *rigi_native_rc_payload_of(int64_t token, const char *face);

/* stdlib 可见的通用面：retain 成功返回 1，资源已释放/票据无效返回 0；
 * release 只用于成功持有的强引用。 */
int32_t rigi_native_rc_retain(int64_t token);
void rigi_native_rc_release(int64_t token);

#ifdef __cplusplus
}
#endif

#endif /* RIGI_NATIVE_RC_H */
