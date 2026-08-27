/*
 * rigi_rt 异常传输面族（MW9a 第一刀）：checked-flag 便携模型的线程局部
 * pending 槽三面。throw = acquire 后写槽；每个可抛调用返回后由生成代码
 * 查 pending 并沿 MIR 异常边传播；不使用平台原生 EH。
 * 纯 C11（_Thread_local），与 shim.c 同纪律；面内不分配/释放堆内存。
 */
#ifndef RIGI_EH_H
#define RIGI_EH_H

#include "rigi_string.h"

#ifdef __cplusplus
extern "C" {
#endif

/* 抛掷：异常对象（恒 tag2 class 对象，16B 头见 arc.h）acquire +1 后
 * 存入本线程 pending 槽 */
void rigi_exc_raise(void *exception_object);

/* 查询：返回槽当前值（借用语义，不动计数）；无异常返回 NULL */
void *rigi_exc_pending(void);

/* 取走：返回槽当前值并清空槽；+1 所有权随返回值移动给调用方 */
void *rigi_exc_take(void);

/* 诊断名取回（MW9a 第 C 棒，顶层未捕获 reporter）：obj→对象头
 * typeId（TypeSheet*）→ typeInfoId（TypeInfo*）→ name 拷出（借用语义，
 * 字面量/TypeInfo 自持存储，不另 acquire）；obj/typeInfoId 为 NULL
 * 防御写空串 */
void rigi_type_name_of(void *obj, rigi_string *out);

/* 未捕获异常出口（reporter 打印收尾）：exit(1)。noreturn——调用方在
 * 调用后补 unreachable 表达 */
void rigi_exc_halt(void);

#ifdef __cplusplus
}
#endif

#endif /* RIGI_EH_H */
