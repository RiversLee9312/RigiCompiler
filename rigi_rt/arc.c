/*
 * rigi_rt ARC 面族实现（MW4 批 1 + MW7a）：alloc/acquire/release +
 * 值语义四面族 + region 协议 + 析构（数组 / kind 感知 refMap）。
 * 生成代码只见四面族与 string 面；析构级联复用嵌套 region 计数。
 * Phase 3a（split-heap §2.1）：分配点按 typeFlags 置会计模式位，
 * acquire/release 按实例位分流。Phase 3d-1（split-heap 收益兑现）：
 * local 会计路替换为非原子 rc（属主独占触碰论证见
 * rigi_account_acquire_local）+ per-协程候选账本登记与属主协作收集
 * 触发（macrogc.c 3d-1 段）；shared 会计路保持 pin-before-sub 原子
 * 协议不变。
 */
#include "arc.h"
#include "gexc.h"
#include "macrogc.h"
#include "stringfmt.h"

#include <stdatomic.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* region 嵌套计数（TLS）；最外层 enter/exit 的 fence 双重检查与
 * cFlag 协议在 macrogc.c（MW12 上线：gc_flag 非 IDLE 时 ENTERING
 * 阻塞等 GCAlarm，恢复后完整重检，codegen 零变化） */
static _Thread_local uint32_t rigi_region_depth = 0;

void rigi_region_enter(void)
{
    if (rigi_region_depth++ == 0)
    {
        rigi_gc_region_fence_enter();
    }
}

void rigi_region_exit(void)
{
    if (--rigi_region_depth == 0)
    {
        rigi_gc_region_fence_exit();
    }
}

void *rigi_alloc(const RigiTypeSheet *desc)
{
    /* 具化身份缺失属于编译/运行时协议错误，不允许解引用空 sheet，
     * 也不能分配不足以容纳对象头的伪对象。 */
    if (desc == NULL || desc->typeSize < sizeof(RigiObjectHeader))
    {
        fprintf(stderr, "rigi_rt: object type identity unavailable or invalid\n");
        /* 本检查可在 worker 线程命中：exit(3) 会经 atexit 链
         *（globals_cleanup → gc_shutdown → gexc_flush → mem_report）
         * 与主线程/worker 等待互锁挂死；_Exit 跳过 atexit 直接终止。 */
        fflush(NULL);
        _Exit(1);
    }
    RigiObjectHeader *object = (RigiObjectHeader *)rigi_track_malloc(desc->typeSize);
    memset(object, 0, desc->typeSize);
    object->typeId = desc;
    object->rc = 1;
    /* Phase 3a：会计模式位按类型静态判定（split-heap §2.1）。shared 类型
     * 实例恒置位（shared 会计）；local 类型实例不置位（local 会计，3a
     * 仍按原子路径执行）。分配期单线程写、与 memset 的 0 值同序，无并发。 */
    object->packedFlags = rigi_pf_accounting_init(desc->typeFlags);
    /* atomicfix：local 会计的实现自本修复起同为原子 RMW（见
     * rigi_account_acquire_local/release_local），原「分配即翻位名单」
     * 无需引入——跨协程共享对象（CoroutineHandle、闭包捕获 cell 等编译
     * 期不可判定集合）统一由原子会计保内存安全，收集归属仍按实例位
     * 分流。 */
    /* Phase 3d-1：分配热路径债务检查——兜底「只有分配没有释放」的
     * 长循环（局部债务只在 release 登记时增长，alloc 检查让已积压
     * 的账本在无 release 的分配流中也能被收）。检查本身一次 TLS 读
     * + 债务比较，超阈值才进收集（macrogc.c 3d-1 段）；新对象此刻
     * 无人引用、不在账本，不参与扫描图。 */
    rigi_gc_local_maybe_collect();
    return object;
}

/* Phase 3a 分流内核（split-heap §2.1）：会计路径由「实例位」决定——
 * 一次 packedFlags relaxed load + 分支（位在分配时定格，冷路径 promote
 * 单调置位 ⟹ 分支方向对预测器近乎恒定）。Phase 3d-1 起两条会计路分
 * 道：local 路非原子 rc + per-协程候选账本 + 属主协作收集（「任何时刻
 * 内存安全」由 3b Handle 封口 + 3c 异常图 promotion 前置保证——非属主
 * 触碰前位必已翻 shared）；shared 路保持原子协议。入口
 * rigi_acquire/release_local|shared 的 local/shared 命名自 3a 起只是
 * 历史遗留：任一入口对同一实例最终走同一条会计路。 */

/* 会计模式位读取（relaxed 一次 load；位只被冷路径单调置位） */
static uint32_t rigi_pf_accounting(const RigiObjectHeader *h)
{
    return atomic_load_explicit(
        (const _Atomic uint32_t *)&h->packedFlags, memory_order_relaxed)
        & RIGI_PF_SHARED_ACCOUNTING;
}

/* local 会计 acquire。atomicfix 修复记录：3d-1 曾将本操作 plain 化
 * （++rc），论证前提是「local 实例只被属主协程触碰」——该前提被运行时
 * 调度面系统性打破（CoroutineHandle 经 Task/Dispatcher 完成链、闭包捕获
 * cell 经 Task 闭包均被 main/worker 两端并发触碰；后者为编译器动态命名
 * 类型，无法静态识别），plain 读改写的丢失更新导致 rc 收支失衡 → 提前
 * 终态析构 → UAF（NativeE2E case 455 实证）。自本修复起 local 会计与
 * shared 会计同样使用原子 RMW（relaxed 序与 acquire_shared 同口径——
 * 引用交接的 happens-before 由队列/门闸边供给，不靠 rc 操作定序），
 * 「任何时刻 rc == 真实引用数」恢复为无竞态不变量。 */
static void rigi_account_acquire_local(RigiObjectHeader *h)
{
    atomic_fetch_add_explicit((_Atomic uint32_t *)&h->rc, 1,
        memory_order_relaxed);
}

/* shared 会计 acquire（原子计数，跨线程共享） */
static void rigi_account_acquire_shared(RigiObjectHeader *h)
{
    atomic_fetch_add_explicit((_Atomic uint32_t *)&h->rc, 1,
        memory_order_relaxed);
}

/* 按实例位分流的 acquire 体（两入口共用；region 语义与调用方约定
 * 保持 MW7a 原状——每入口自带一对进出） */
static void rigi_acquire_by_accounting(void *object)
{
    RigiObjectHeader *h = (RigiObjectHeader *)object;
    if (h != NULL)
    {
        if (rigi_pf_accounting(h) != 0)
        {
            rigi_account_acquire_shared(h);
        }
        else
        {
            rigi_account_acquire_local(h);
        }
    }
}

void rigi_acquire_local(void *object)
{
    /* local 限制是语言可达性规则；实际计数口径由实例会计位决定（见
     * rigi_acquire_by_accounting）：local 会计实例的引用恒在属主协程
     * 闭包内（静态划分 + 3b 壳 + 3c promotion 封口，Phase 3d-1 非原子
     * 计数），shared 会计实例跨线程原子计数（同一失败 Task 的异常图
     * 已在发布点翻位，可被多 waiter 持有）。 */
    rigi_region_enter();
    rigi_acquire_by_accounting(object);
    rigi_region_exit();
}

void rigi_acquire_shared(void *object)
{
    rigi_region_enter();
    rigi_acquire_by_accounting(object);
    rigi_region_exit();
}

/* §25.2 IDisposable 销毁时强制检查（MW12b 真检查）：typeFlags 含
 * DISPOSABLE 且 disposed 位未置位 → undisposed-resource 全局异常事件
 * 入队（gexc.c；派发时机与晚到规则见 gexc.h）。绝不代跑 dispose、
 * 不延迟释放、不复活。三销毁入口共用：microGC/microSGC 经
 * rigi_destruct，macroGC 清理步经 macrogc.c gc_teardown。
 * MW11c 棒5a：旧 C 侧 Task/sleep EventAlarm 簿记已随调度面删除——
 * Task 是 Rigi 对象（refMap 扫描字段），时钟底座由 Worker 定时器
 * 原语 + Rigi SleepAlarm/Timer 持有句柄，不再经本钩子拆除。 */
void rigi_dispose_check(void *object, const RigiTypeSheet *desc)
{
    RigiObjectHeader *hdr = (RigiObjectHeader *)object;
    uint32_t pf;
    if (hdr == NULL || desc == NULL
        || (desc->typeFlags & RIGI_TYPE_DISPOSABLE) == 0)
    {
        return;
    }
    pf = atomic_load_explicit(
        (const _Atomic uint32_t *)&hdr->packedFlags, memory_order_relaxed);
    if ((pf & RIGI_PF_DISPOSED) == 0)
    {
        rigi_gexc_report_undisposed(desc);
    }
}

void rigi_mark_disposed(void *object)
{
    if (object != NULL)
    {
        atomic_fetch_or_explicit(
            (_Atomic uint32_t *)&((RigiObjectHeader *)object)->packedFlags,
            RIGI_PF_DISPOSED, memory_order_relaxed);
    }
}

static void rigi_value_walk(void *ptr, const RigiTypeSheet *sheet, bool is_acquire)
{
    const char *base;
    size_t cursor;
    uint32_t i;
    if (ptr == NULL || sheet == NULL || sheet->refMap == NULL)
    {
        return;
    }
    base = (const char *)ptr;
    cursor = 0;
    for (i = 0; i < sheet->refMapSize; i++)
    {
        uint16_t entry = sheet->refMap[i];
        uint16_t kind = (uint16_t)(entry >> RIGI_REFMAP_KIND_SHIFT);
        uint16_t hops = (uint16_t)(entry & RIGI_REFMAP_HOP_MASK);
        cursor += (size_t)hops * 16u;
        if (kind == RIGI_REFMAP_STRING)
        {
            char *data = *(char *const *)(base + cursor);
            if (is_acquire)
            {
                rigi_string_acquire(data);
            }
            else
            {
                rigi_string_release(data);
            }
        }
        else
        {
            uint64_t type_id = *(const uint64_t *)(base + cursor);
            uint64_t payload = *(const uint64_t *)(base + cursor + 8);
            if (is_acquire)
            {
                uint64_t new_payload = rigi_ref_acquire(type_id, payload);
                /* tag1 克隆语义（unique 裸块深拷贝）产生新块指针——嵌入槽
                 * 必须回写新 payload，否则 memcpy 副本与原值共持同一块，
                 * 双侧 release 即双释放/UAF（tag0/tag2 返回值不变，回写
                 * 无害）。release 走查只读，无回写。 */
                *(uint64_t *)(void *)(base + cursor + 8) = new_payload;
            }
            else
            {
                rigi_ref_release(type_id, payload);
                /* 防御（richretfix）：tag1 盒槽是唯一「release 即 free」
                 * 的引用槽种类，release 后清零槽位——若上层交付协议破约
                 * （如对已交付/已死槽再 release），悬垂指针退化为 null
                 * 槽（tag0，后续 release/acquire 走 no-op 分支，确定性），
                 * 而非随机 UAF。STRING/tag2 槽 release 不 free 对象，保
                 * 持原样；按值语义 release 后槽位已死，合法发射序列均在
                 * release 后 memset 或 memcpy 覆盖，清零不可见。 */
                if ((type_id >> RIGI_TAG_SHIFT) == RIGI_TAG_HEAP_VALUE)
                {
                    *(uint64_t *)(void *)(base + cursor) = 0;
                    *(uint64_t *)(void *)(base + cursor + 8) = 0;
                }
            }
        }
        cursor += 16u;
    }
}

void rigi_value_acquire(void *ptr, const RigiTypeSheet *sheet)
{
    rigi_region_enter();
    rigi_value_walk(ptr, sheet, true);
    rigi_region_exit();
}

void rigi_value_release(void *ptr, const RigiTypeSheet *sheet)
{
    rigi_region_enter();
    rigi_value_walk(ptr, sheet, false);
    rigi_region_exit();
}

/* 泛型胖引用形态守卫（数组元素 ABI 归一防御）。形态合法性口径（与
 * docs/RUNTIME/03-type-metadata.md「元素槽布局铁律」同步）：
 *   - null{0,0} / tag1 盒 / tag2 对象引用：一切 16B 引用槽的合法形态；
 *   - tag0（RIGI_TAG_INLINE）且 payload 非零 = 装箱标量的泛型形态：
 *     typeid 低 56 位（RIGI_SHEET_MASK）是标量的 TypeSheet 指针
 *     （发射层 TypeLayout 为全部标量置 RIGI_TYPE_INLINE_VALUE），
 *     payload 是标量位形。引用擦除容器槽（elemSheet 为 core::Any /
 *     Nullable 族的 .array<.any> 载荷等）由调用方静态路径写入装箱
 *     标量属合法形态，泛型共享体读回时经此处放行（box-span 对拍、
 *     array_boxed_scalar 语料定点）；
 *   - 其余 tag0（payload 非零且 tid 低 56 位解引用后不具备
 *     RIGI_TYPE_INLINE_VALUE，如 String 特化槽 {data,len} 被误当胖
 *     引用解释——data 指向的字符串块无 sheet 布局）说明发射层 ABI
 *     错配——立即 abort 定位，不放行到 typecheck 深处段错误。 */
void rigi_check_fat_ref(uint64_t type_id, uint64_t payload)
{
    if ((type_id >> RIGI_TAG_SHIFT) != RIGI_TAG_INLINE || payload == 0)
    {
        return;
    }
    const RigiTypeSheet *scalarSheet =
        (const RigiTypeSheet *)(uintptr_t)(type_id & RIGI_SHEET_MASK);
    if (scalarSheet != NULL
        && (scalarSheet->typeFlags & RIGI_TYPE_INLINE_VALUE) != 0)
    {
        return;
    }
    fprintf(stderr,
        "rigi_rt: 泛型胖引用形态非法（tag0 且 payload 非零、tid 非装箱标量 "
        "sheet）——数组元素 ABI 错配\n");
    abort();
}

/* 数组共用擦除元素类型的 sheet：生命周期保守使用原子 RC，覆盖嵌套数组。
 * 这不改变元素共享安全判定，也不提供数组元素访问的并发同步。
 * 对象必须读取头内实际 sheet；胖引用可能只是 Object/接口视图，
 * 不能用视图的非 shared 标志把共享对象误送进非原子 RC 路径。 */
uint64_t rigi_ref_acquire(uint64_t type_id, uint64_t payload)
{
    uint64_t tag;
    const RigiTypeSheet *sheet;
    void *block;
    rigi_region_enter();
    tag = type_id >> RIGI_TAG_SHIFT;
    if (tag == RIGI_TAG_OBJECT)
    {
        sheet = (const RigiTypeSheet *)(uintptr_t)(type_id & RIGI_SHEET_MASK);
        if (sheet != NULL && (sheet->typeFlags & RIGI_TYPE_INLINE_VALUE) != 0)
        {
            fprintf(stderr, "rigi_rt: 值类型借用地址错误进入对象 ARC（运行时 bug）\n");
            abort();
        }
        if (payload == 0)
        {
            rigi_region_exit();
            return payload;
        }
        sheet = ((const RigiObjectHeader *)(uintptr_t)payload)->typeId;
        if (sheet != NULL && (sheet->typeFlags & (RIGI_TYPE_SHARED | RIGI_TYPE_ARRAY)) != 0)
        {
            rigi_acquire_shared((void *)(uintptr_t)payload);
        }
        else
        {
            rigi_acquire_local((void *)(uintptr_t)payload);
        }
        rigi_region_exit();
        return payload;
    }
    if (tag == RIGI_TAG_HEAP_VALUE)
    {
        /* 克隆语义：unique 裸块深拷贝 + 内部引用 acquire */
        sheet = (const RigiTypeSheet *)(uintptr_t)(type_id & RIGI_SHEET_MASK);
        if (sheet == NULL || payload == 0)
        {
            rigi_region_exit();
            return payload;
        }
        block = rigi_track_malloc(sheet->typeSize);
        memcpy(block, (void *)(uintptr_t)payload, sheet->typeSize);
        rigi_value_acquire(block, sheet);
        rigi_region_exit();
        return (uint64_t)(uintptr_t)block;
    }
    /* tag0：原样返回 */
    rigi_region_exit();
    return payload;
}

void rigi_ref_release(uint64_t type_id, uint64_t payload)
{
    uint64_t tag;
    const RigiTypeSheet *sheet;
    rigi_region_enter();
    tag = type_id >> RIGI_TAG_SHIFT;
    if (tag == RIGI_TAG_OBJECT)
    {
        if (payload != 0)
        {
            sheet = ((const RigiObjectHeader *)(uintptr_t)payload)->typeId;
            if (sheet != NULL && (sheet->typeFlags & (RIGI_TYPE_SHARED | RIGI_TYPE_ARRAY)) != 0)
            {
                rigi_release_shared((void *)(uintptr_t)payload);
            }
            else
            {
                rigi_release_local((void *)(uintptr_t)payload);
            }
        }
        rigi_region_exit();
        return;
    }
    if (tag == RIGI_TAG_HEAP_VALUE)
    {
        if (payload != 0)
        {
            sheet = (const RigiTypeSheet *)(uintptr_t)(type_id & RIGI_SHEET_MASK);
            rigi_value_release((void *)(uintptr_t)payload, sheet);
            rigi_track_free((void *)(uintptr_t)payload);
        }
        rigi_region_exit();
        return;
    }
    /* tag0：无操作 */
    rigi_region_exit();
}

/* release 归零的库内自动析构：数组走元素表；否则 §25 挂点 → kind 感知
 * refMap → track_free。嵌套走查经 region 计数天然安全。
 * Phase 1.3：头部的无条件候选摘除移除——账本持 +1（PURPLE 在册 ⟹
 * rc = U + 1）下，非在册析构占绝对多数，forget 对它们是 provably
 * no-op，白取一遍账本锁；在册对象的最后一次用户 release 由
 * rigi_gc_release_shared 的 PURPLE 快路径 old==2 分支进锁摘除候选并
 * 归还账本引用后才返回 1，确定性释放语义（§22.2）保持不变。 */
static void rigi_destruct(void *object, const RigiTypeSheet *desc)
{
    const char *base = (const char *)object;
    size_t cursor;
    uint32_t i;

    if (desc != NULL && (desc->typeFlags & RIGI_TYPE_ARRAY) != 0)
    {
        const RigiTypeSheet *elemSheet =
            *(RigiTypeSheet *const *)((char *)object + 16);
        int32_t len = *(int32_t *)((char *)object + 24);
        const char *elems = (const char *)object + 32;
        int32_t stride;
        int32_t idx;
        if (elemSheet != NULL && (elemSheet->typeFlags & RIGI_TYPE_INLINE_VALUE) != 0
            && elemSheet->typeSize > 0)
        {
            stride = (int32_t)elemSheet->typeSize;
        }
        else
        {
            stride = 16;
        }
        for (idx = 0; idx < len; idx++)
        {
            void *elem = (void *)(elems + (size_t)idx * (size_t)stride);
            if (elemSheet == NULL)
            {
                /* new List<K> 开放构造未写入隐藏 T 时 elemSheet 为空，
                 * 元素仍按 16B 胖槽存储（TryStoreViaTypeId → Any/AssignFat）。 */
                uint64_t type_id = *(const uint64_t *)elem;
                uint64_t payload = *(const uint64_t *)((const char *)elem + 8);
                rigi_ref_release(type_id, payload);
                continue;
            }
            if ((elemSheet->typeFlags & RIGI_TYPE_STRING) != 0)
            {
                /* 泛型 List\<T\> 对 String 仍按胖引用写入（tag1 盒）；
                 * 仅当槽是真 String ABI 才 string_release。 */
                uint64_t elem_tid = *(const uint64_t *)elem;
                uint64_t elem_tag = elem_tid >> RIGI_TAG_SHIFT;
                if (elem_tag == RIGI_TAG_HEAP_VALUE || elem_tag == RIGI_TAG_OBJECT)
                {
                    uint64_t elem_pl = *(const uint64_t *)((const char *)elem + 8);
                    rigi_ref_release(elem_tid, elem_pl);
                }
                else
                {
                    rigi_string_release(*(char *const *)elem);
                }
            }
            else if ((elemSheet->typeFlags & RIGI_TYPE_INLINE_VALUE) != 0)
            {
                if (elemSheet->refMapSize > 0
                    || (elemSheet->typeFlags & RIGI_TYPE_RICH) != 0)
                {
                    rigi_value_release(elem, elemSheet);
                }
            }
            else
            {
                uint64_t type_id = *(const uint64_t *)elem;
                uint64_t payload = *(const uint64_t *)((const char *)elem + 8);
                rigi_ref_release(type_id, payload);
            }
        }
        rigi_track_free(object);
        return;
    }

    cursor = sizeof(RigiObjectHeader);
    rigi_dispose_check(object, desc);
    rigi_native_resources_destroy(object, desc);
    if (rigi_handle_is_capability(object, desc))
    {
        /* 3b-β：capability 壳析构（唯一被设计的第二 release 路径，
         * 裁定 #8）——壳计数减一 + 归零转移（普通期：全局归属直接
         * 清理；属主归属投递释放消息），本体即放 */
        rigi_handle_capability_release(object);
        return;
    }
    if (desc == NULL || desc->refMap == NULL)
    {
        rigi_track_free(object);
        return;
    }
    for (i = 0; i < desc->refMapSize; i++)
    {
        uint16_t entry = desc->refMap[i];
        uint16_t kind = (uint16_t)(entry >> RIGI_REFMAP_KIND_SHIFT);
        uint16_t hops = (uint16_t)(entry & RIGI_REFMAP_HOP_MASK);
        cursor += (size_t)hops * 16u;
        if (kind == RIGI_REFMAP_STRING)
        {
            rigi_string_release(*(char *const *)(base + cursor));
        }
        else
        {
            uint64_t type_id = *(const uint64_t *)(base + cursor);
            uint64_t payload = *(const uint64_t *)(base + cursor + 8);
            rigi_ref_release(type_id, payload);
        }
        cursor += 16u;
    }
    rigi_track_free(object);
}

/* 析构中间态 guard 包围（Phase 3d-1）：rigi_destruct 拆边到一半的图
 * 不满足局部收集扫描的快照一致性（macrogc.c 3d-1 段），guard 期间
 * 登记照做、触发延迟到析构外的下一次 release/alloc 检查。唯一调用点
 * 为 release 终态路径（rigi_release_by_accounting 的 old==1 分支）；
 * 析构级联（子 release → 子终态 → 递归）经计数天然嵌套。 */
static void rigi_destruct_guarded(void *object, const RigiTypeSheet *desc)
{
    rigi_gc_local_guard_enter();
    rigi_destruct(object, desc);
    rigi_gc_local_guard_exit();
}

/* 基类字段偏移保持不变；派生对象也承担继承来的原生资源所有权。 */
void rigi_native_resources_destroy(void *object, const RigiTypeSheet *desc)
{
    for (; desc != NULL; desc = desc->baseTypeId)
    {
        if (desc->typeInfoId != NULL && desc->typeInfoId->destroyNative != NULL)
        {
            desc->typeInfoId->destroyNative(object);
        }
    }
}

/* local 会计 release——atomicfix 定案：与 shared 会计统一收口
 * rigi_gc_release_shared（pin-before-sub 原子协议 + macroGC 全局候选
 * 账本）。演化记录：3d-1 曾把本路拆为「plain 减量 + per-协程局部账本
 * 登记/PURPLE 快路径」，论证前提「local 实例只被属主协程触碰」被运行时
 * 调度面系统性打破（CoroutineHandle 经 Task/Dispatcher 完成链、闭包捕获
 * cell 经 Task 闭包均被 main/worker 两端并发触碰；后者为编译器动态命名
 * 类型无法静态识别），plain rc 丢失更新 → rc 收支失衡 → 提前终态析构 →
 * 幸存引用 UAF（NativeE2E case 455 实证，GC_OFF 20/20、默认 25%、
 * GC_THRESHOLD=64 达 90%）；其间亦实证「仅原子化」不足——局部收集
 * markGray 的临时减量使 rc 中间态暴露给并发 mutator，「rc==0 即死」判定
 * 仍失守。本函数遂回归 3d-1 之前的已验证形态（a20fff8^ 沿革：local
 * 计数原子、release 与 shared 同协议同账本——彼注释明言「不保证运行时
 * 引用只在一个线程计数」）。local 账本的 mutator 登记面随之停用
 * （rigi_gc_local_after_release 入口短路），候选环检测统一交 macroGC
 * 全局 pass（fence 保护）。
 * 返回值约定同 rigi_gc_release_shared：1 = 终态析构信号（析构时刻 rc
 * 必已归 0，调用方有 rc==0 前置断言）；否则减前值 ≥2 非终态。 */
static uint32_t rigi_account_release_local(void *object)
{
    return rigi_gc_release_shared(object);
}

/* 按实例位分流的 release 体（两入口共用）：返回 1 = 终态，析构在
 * region 内照旧执行 */
static void rigi_release_by_accounting(void *object)
{
    RigiObjectHeader *header = (RigiObjectHeader *)object;
    uint32_t old;
    if (header != NULL)
    {
        old = rigi_pf_accounting(header) != 0
            ? rigi_gc_release_shared(object)       /* shared 会计 */
            : rigi_account_release_local(object);  /* local 会计 */
        if (old == 1)
        {
            /* atomicfix 终态析构前置不变量断言：返回 1（终态析构信号）
             * 的合法来源只有两个——local/shared 统一经
             * rigi_gc_release_shared 原子减后 rc==0（叶类型/pin 协议
             * 路径），或 PURPLE 在册 detach 成功归还账本 +1 后 rc 归 0。
             * 三者析构时刻 rc 必为 0；任何「减后值==1 即判终态」的误判
             * （历史缺陷：rigi_gc_local_after_release !alive 分支，
             * case 455 UAF 根因）都会在此以 rc!=0 响亮失败，调试期
             * 炸在析构源头而非悬垂 release 的 0xDD 解引用现场。 */
            if (header->rc != 0u)
            {
                fprintf(stderr,
                    "rigi_rt: 终态析构信号但 rc=%u 非零（终态判定破坏，"
                    "atomicfix 断言）\n", header->rc);
                fflush(NULL);
                abort();
            }
            rigi_destruct_guarded(object, header->typeId);
        }
    }
}

void rigi_release_local(void *object)
{
    rigi_region_enter();
    rigi_release_by_accounting(object);
    rigi_region_exit();
}

void rigi_release_shared(void *object)
{
    rigi_region_enter();
    rigi_release_by_accounting(object);
    rigi_region_exit();
}
