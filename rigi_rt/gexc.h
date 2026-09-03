/*
 * gexc.h（MW12b）：全局异常通道 —— RUNTIME §25.2「IDisposable 销毁时强制
 * 检查」的事件队列 + core.GlobalExceptionHandler 的 native 底座。
 *
 * 职责边界：
 *   - rigi_gexc_report_undisposed：销毁检查（rigi_dispose_check，三销毁
 *     入口共用）发现 DISPOSABLE 对象 disposed 位未置位时入队一条事件。
 *     只写队列，不碰引用图 —— 析构/收集期调用安全（含 GC 线程 teardown
 *     与 globals_cleanup 的 atexit 末批）；payload 是违规对象实际类型的
 *     TypeSheet*（sheet 静态全局永生，存指针即可）；
 *   - rigi_gexc_take：entry stub（生成代码）在 main/drain 之后、失败汇总
 *     之前统一 drain：弹一条并把 sheet→TypeInfo.name 拷出（借用语义——
 *     TypeInfo.name 是 IMMORTAL 字面量块，随全局 sheet 永生；不做
 *     acquire，调用方也不 release）；
 *   - rigi_gexc_flush_default：晚到事件（globals_cleanup 与 GC 终轮收集
 *     阶段入队）不经用户处理器，由本面按默认文本打印到 stderr（atexit
 *     注册序卡在 gc_shutdown 之后、mem_report 之前，LIFO 保证末批候选
 *     终轮收集产生的事件也能落到本面）。
 * 并发：release 路径（含 shared 域）可多线程入队 → 队列访问走
 * atomic 自旋闸（macrogc.c 候选账本同形态）。
 */
#ifndef RIGI_GEXC_H
#define RIGI_GEXC_H

#include "arc.h"
#include "coroutine.h"

#ifdef __cplusplus
extern "C" {
#endif

/* undisposed-resource 事件入队（dispose 检查未置位时调用） */
void rigi_gexc_report_undisposed(const RigiTypeSheet *sheet);

/* 队列弹一条：把违规类型的 TypeInfo.name 借用拷出到 out（StringOut 首参
 * 惯例）。返 1 取出 / 0 队列空 */
int32_t rigi_gexc_take(rigi_string *out);

/* 队列剩余全部按默认文本打印到 stderr 并释放队列缓冲（atexit 末段） */
void rigi_gexc_flush_default(void);

/* ===== 处理器注册表（core.GlobalExceptionHandler 的 native 承载）=====
 * SYNTAX §3.1.1 共享安全闸门禁止全局/静态字段持 local class（Action 非
 * shared），注册表条目本体由 native +1 持有（failreg.c 未观察失败注册表
 * 同先例）；Rigi 侧只经三面访问，注册序 = 下标序。 */
/* 登记：+1 持有处理器胖引用，返登记下标（≥0） */
int64_t rigi_gexc_register_handler(const RigiFatRef *handler);
/* 注册表大小 */
int64_t rigi_gexc_handler_count(void);
/* 按下标读取（Any 返回 = out 首参惯例）：+1 随 out 移交；越界属编译器
 * bug，诊断 abort */
void rigi_gexc_handler_at(RigiFatRef *out, int64_t index);

#ifdef __cplusplus
}
#endif

#endif /* RIGI_GEXC_H */
