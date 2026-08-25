/*
 * rigi_rt ARC 面族（MW4 批 1）：TypeSheet 的 C 镜像（RUNTIME §6 字段序
 * 原样）+ alloc/acquire/release 面声明。生成代码（RcInjection，MW7）只
 * 见这四个面，无 destroy 面——release 归零在库内自动析构。
 * 纯 C11 + <stdatomic.h>，与 shim.c 同纪律。
 */
#ifndef RIGI_ARC_H
#define RIGI_ARC_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* typeFlags 位（RUNTIME §6；与 Middleware TypeLayoutPlan.Flag* 一致） */
#define RIGI_TYPE_RICH         0x1u
#define RIGI_TYPE_SHARED       0x2u
#define RIGI_TYPE_DISPOSABLE   0x4u
#define RIGI_TYPE_INLINE_VALUE 0x8u

typedef struct RigiTypeSheet RigiTypeSheet;
struct RigiTypeSheet
{
    void *typeInfoId;                /* 64 bit：TypeInfo 对象（MW8 前恒 NULL） */
    const RigiTypeSheet *baseTypeId; /* 基类 TypeSheet；仅 Any 为 NULL */
    uint32_t typeSize;               /* 含对象头的对象尺寸（字节） */
    uint32_t typeFlags;              /* RICH/SHARED/DISPOSABLE 位 */
    uint32_t vTableSize;             /* vtable 元素数量 */
    void *const *vTable;             /* 方法入口指针数组 */
    uint32_t iMapSize;               /* iMap 键值对数量 */
    const void *iMap;                /* {iface TypeSheet*, u32 base offset} 对数组 */
    uint32_t refMapSize;             /* refMap 条目数量 */
    const uint16_t *refMap;          /* 128-bit 槽粒度跳数序列 */
};

/* 对象头 16B：[0..8) TypeSheet* + [8..12) RC u32 + [12..16) 位打包域
 *（颜色 2bit + 候选索引/标志位，MW12 前恒 0）；对象 16B 对齐 */
typedef struct
{
    const RigiTypeSheet *typeId;
    uint32_t rc;
    uint32_t packedFlags;
} RigiObjectHeader;

/* 分配：头部 RC=1、打包域 0、payload 零初始化（对齐 VM ZeroOf 语义）；
 * malloc 失败属环境耗尽，abort */
void *rigi_alloc(const RigiTypeSheet *desc);

/* local 域：非原子 RC（单线程所有权） */
void rigi_acquire_local(void *object);
void rigi_release_local(void *object);

/* shared 域：C11 原子 RC（跨线程共享） */
void rigi_acquire_shared(void *object);
void rigi_release_shared(void *object);

/* iMap 键值对（RUNTIME §8）：接口 TypeSheet 地址 + 本类 vtable 段基址
 *（C 侧自然对齐与 LLVM {ptr, i32} 同构：8 + 4（+4 padding）= 16B） */
typedef struct
{
    const RigiTypeSheet *iface;
    uint32_t base;
} RigiImapPair;

/* 派发 helper 面（MW4 批 2，dispatch.c）：对象头 [0] 即实际类型的
 * TypeSheet 裸指针（无 tag）；bitcode 合并后由优化管线内联 */
void *rigi_vtable_entry(void *object, uint32_t slot);
void *rigi_imap_entry(void *object, const RigiTypeSheet *iface, uint32_t slot);

#ifdef __cplusplus
}
#endif

#endif /* RIGI_ARC_H */
