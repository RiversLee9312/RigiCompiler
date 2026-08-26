/*
 * rigi_rt ARC 面族（MW4 批 1 + MW7a）：TypeSheet 的 C 镜像（RUNTIME §6
 * 字段序原样）+ alloc/acquire/release + 值语义四面族 / String ARC /
 * region / 内存台账。生成代码（RcInjection，MW7）只见四面族与 string
 * 面，无 destroy 面——release 归零在库内自动析构。
 * 纯 C11 + <stdatomic.h>，与 shim.c 同纪律。
 */
#ifndef RIGI_ARC_H
#define RIGI_ARC_H

#include <stddef.h>
#include <stdint.h>
#include "rigi_string.h"

#ifdef __cplusplus
extern "C" {
#endif

/* typeFlags 位（RUNTIME §6；与 Middleware TypeLayoutPlan.Flag* 一致） */
#define RIGI_TYPE_RICH         0x1u
#define RIGI_TYPE_SHARED       0x2u
#define RIGI_TYPE_DISPOSABLE   0x4u
#define RIGI_TYPE_INLINE_VALUE 0x8u
#define RIGI_TYPE_ARRAY        0x10u
#define RIGI_TYPE_STRING       0x20u

/* 胖引用 typeid 最高字节 tag（RUNTIME §2）与 sheet 地址掩码 */
#define RIGI_TAG_INLINE      0u
#define RIGI_TAG_HEAP_VALUE  1u
#define RIGI_TAG_OBJECT      2u
#define RIGI_TAG_SHIFT       56
#define RIGI_SHEET_MASK      UINT64_C(0x00FFFFFFFFFFFFFF)

/*
 * refMap 编码（MW7a）：每个 u16 条目 = 高 2 位 kind | 低 14 位跳数。
 * 跳数单位仍为 16B 槽；kind 决定该槽的解释。
 *   FATREF(0)：胖引用槽 {u64 typeid, u64 payload}
 *   STRING(1)：String 槽，槽首 8 字节为 char *data
 * 旧条目（仅跳数、高 2 位为 0）自然兼容为 FATREF。
 */
#define RIGI_REFMAP_KIND_SHIFT 14
#define RIGI_REFMAP_HOP_MASK   0x3FFFu
#define RIGI_REFMAP_FATREF     0
#define RIGI_REFMAP_STRING     1

/*
 * String ARC 块布局：堆块 = { _Atomic uint32_t rc; uint32_t reserved; char data[] }。
 * 槽内 data 指针 = 块基址 + 8。RIGI_STRING_IMMORTAL 为字面量永生标记，
 * acquire/release 均跳过（不改 rc、不释放）。
 */
#define RIGI_STRING_IMMORTAL 0xFFFFFFFFu

/*
 * 数组前缀 32B：对象头[0..16) + elemSheet 指针[16..24) + length i32[24..28)
 * + pad[28..32)，元素基址 32。
 */

typedef struct RigiTypeInfo RigiTypeInfo;
typedef struct RigiTypeSheet RigiTypeSheet;
struct RigiTypeSheet
{
    const RigiTypeInfo *typeInfoId;  /* 64 bit：TypeInfo（诊断名 / wrapper 表） */
    const RigiTypeSheet *baseTypeId; /* 基类 TypeSheet；仅 Any 为 NULL */
    uint32_t typeSize;               /* 含对象头的对象尺寸（字节） */
    uint32_t typeFlags;              /* RICH/SHARED/DISPOSABLE/ARRAY/STRING 位 */
    uint32_t vTableSize;             /* vtable 元素数量 */
    void *const *vTable;             /* 方法入口指针数组 */
    uint32_t iMapSize;               /* iMap 键值对数量 */
    const void *iMap;                /* {iface TypeSheet*, u32 base offset} 对数组 */
    uint32_t refMapSize;             /* refMap 条目数量 */
    const uint16_t *refMap;          /* kind|跳数 编码，见上 */
};

/* TypeInfo：诊断名 + 回指 sheet + wrapper 清单 + 接口闭包。
 * ifaceClosure = 该类型传递 implements 的全部接口 sheet（含父接口）。 */
struct RigiTypeInfo
{
    rigi_string name;
    const RigiTypeSheet *sheet;
    const RigiTypeSheet *const *wrappers;
    int32_t wrapperCount;
    const RigiTypeSheet *const *ifaceClosure;
    int32_t ifaceClosureCount;
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

/* 连续缓冲区分配（数组 / Span 同构 32B 前缀）；负长度 abort。
 * 数组与 Span 面函数共用此体，避免双实现漂移。 */
void *rigi_alloc_contiguous(const RigiTypeSheet *sheet, const RigiTypeSheet *elemSheet,
    int32_t len);

/* Span / SharedSpan 分配：与数组同构，区别仅在 sheet 身份
 * （core::Span / core::SharedSpan） */
void *rigi_span_alloc(const RigiTypeSheet *spanSheet, const RigiTypeSheet *elemSheet,
    int32_t len);

/* local 域：非原子 RC（单线程所有权） */
void rigi_acquire_local(void *object);
void rigi_release_local(void *object);

/* shared 域：C11 原子 RC（跨线程共享） */
void rigi_acquire_shared(void *object);
void rigi_release_shared(void *object);

/* 值语义四面族（MW7a）：生成代码只见这四个面 + string 面 */
uint64_t rigi_ref_acquire(uint64_t type_id, uint64_t payload);
void rigi_ref_release(uint64_t type_id, uint64_t payload);
void rigi_value_acquire(void *ptr, const RigiTypeSheet *sheet);
void rigi_value_release(void *ptr, const RigiTypeSheet *sheet);

/* String ARC：data 为块基址 + 8；NULL / IMMORTAL 跳过 */
void rigi_string_acquire(const char *data);
void rigi_string_release(const char *data);
char *rigi_string_new(int64_t len);   /* 分配 rc=1 字符串块，返回 data 指针 */

/* region 协议（MW7a；MW12 前 gc_flag 恒 IDLE） */
void rigi_region_enter(void);
void rigi_region_exit(void);

/* 内存台账：16B 头 {size_t size; size_t pad;}，返回头后指针 */
void *rigi_track_malloc(size_t size);
void rigi_track_free(void *p);
void rigi_mem_report(void);

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

/* 类型检查 helper（MW5 c3，typecheck.c）：胖引用拆成 typeid/payload
 * 两枚 i64 传入，规避 16B struct 按值的 win-x64/SysV 分歧；返回 i32 0/1 */
int32_t rigi_type_is(uint64_t type_id, uint64_t payload, const RigiTypeSheet *target);
int32_t rigi_type_is_indirect(uint64_t type_id, uint64_t payload,
    const RigiTypeSheet *target);
int32_t rigi_type_supers(uint64_t type_id, uint64_t payload,
    const RigiTypeSheet *target);
int32_t rigi_type_supers_indirect(uint64_t type_id, uint64_t payload,
    const RigiTypeSheet *target);
int32_t rigi_type_with(uint64_t type_id, uint64_t payload,
    const RigiTypeSheet *wrapper);
int32_t rigi_type_with_indirect(uint64_t type_id, uint64_t payload,
    const RigiTypeSheet *wrapper);

#ifdef __cplusplus
}
#endif

#endif /* RIGI_ARC_H */
