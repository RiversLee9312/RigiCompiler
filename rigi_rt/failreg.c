/*
 * 未观察失败注册表（MW11c 棒5a，native 半场；RUNTIME §18.2/§18.3）：
 * Task 失败异常本体的 native 承载。shared class 不得持 local
 * Exception 字段（P2 检查），故 Task 通过内部 refMap 槽 +1 持有异常。
 * 本表未观察节点额外 +1 负责顶层报告，已观察节点只借用 Task 槽。
 *   登记 = 生成代码 DONE 垫尾（MirTaskFail 发射 rigi_failure_record）；
 *   摘除 = await 观察（Task.registerWaiter → rigi_failure_drop，幂等）；
 *   汇总 = rigi_entry 失败汇总（rigi_failure_take_unobserved，main
 *          失败 > 未观察失败，MW9 reporter 出口）。
 * VM 半场由 VmDispatch._failed 承载（本注册表不参与）。
 * 双编译形态真实现（纯 C11 + atomic_flag 自旋闸，零 uv 依赖）；节点
 * 走 rigi_track_malloc 台账，残余节点随进程退出经 atexit 链释放
 *（LIFO 先于 rigi_mem_report——注册时机在首次登记，晚于 shim.c 的
 * report 注册）。
 */
#include "coroutine.h"
#include "arc.h"
#include "failreg.h"
#include "macrogc.h" /* Phase 3c：失败发布点整图 promotion */

#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>

typedef struct RigiFailureNode
{
    int64_t id;
    RigiFatRef failure; /* 未观察时 +1；观察后借用 Task 隐藏拥有槽 */
    int observed;       /* await 观察置位（节点保留——重读重抛需要） */
    struct RigiFailureNode *next;
} RigiFailureNode;

static atomic_flag rigi_failreg_gate = ATOMIC_FLAG_INIT;
static RigiFailureNode *rigi_failreg_head = NULL;
static int64_t rigi_failreg_next_id = 0;
static int rigi_failreg_cleanup_registered = 0;

static void rigi_failreg_lock(void)
{
    while (atomic_flag_test_and_set_explicit(&rigi_failreg_gate,
               memory_order_acquire))
    {
    }
}

static void rigi_failreg_unlock(void)
{
    atomic_flag_clear_explicit(&rigi_failreg_gate, memory_order_release);
}

static void rigi_failreg_release_ref(RigiFatRef *fat)
{
    if (fat->payload != 0)
    {
        /* tag2 对象：payload 即对象地址，头内 rc/sheet 自足 */
        rigi_ref_release(fat->type_id, fat->payload);
        fat->payload = 0;
        fat->type_id = 0;
    }
}

/* atexit 兜底：残余节点统一释放（未观察失败已随进程退出终结） */
static void rigi_failreg_cleanup(void)
{
    RigiFailureNode *nodes;
    rigi_region_enter();
    rigi_failreg_lock();
    nodes = rigi_failreg_head;
    rigi_failreg_head = NULL;
    rigi_failreg_unlock();
    while (nodes != NULL)
    {
        RigiFailureNode *next = nodes->next;
        if (!nodes->observed) rigi_failreg_release_ref(&nodes->failure);
        rigi_track_free(nodes);
        nodes = next;
    }
    rigi_region_exit();
}

/* Task 已死：已观察节点可回收；未观察节点保留顶层报告的所有权。
 * 普通 ARC 与 macroGC 均可调用，不读取借用异常、不进入 ARC fence。
 * 异常拥有边由 Task refMap 统一释放，包括同批白色对象边。 */
void rigi_failure_release_task(int64_t id)
{
    RigiFailureNode **link;
    if (id == 0) return;
    rigi_failreg_lock();
    for (link = &rigi_failreg_head; *link != NULL; link = &(*link)->next)
    {
        RigiFailureNode *node = *link;
        if (node->id != id) continue;
        if (node->observed)
        {
            *link = node->next;
            rigi_track_free(node);
        }
        break;
    }
    rigi_failreg_unlock();
}

/* 终态发布前建立 Task→异常的实际拥有边；槽在分配时清零，只绑定一次。
 * fence 先于册闸；内部槽没有 MIR 自动 ARC，+1 明确由此移交。 */
void rigi_failure_bind(int64_t id, RigiFatRef *slot)
{
    RigiFailureNode *node;
    if (id == 0) return;
    rigi_region_enter();
    rigi_failreg_lock();
    for (node = rigi_failreg_head; node != NULL; node = node->next)
    {
        if (node->id == id)
        {
            if (slot == NULL || slot->payload != 0) abort();
            *slot = node->failure;
            slot->payload = rigi_ref_acquire(slot->type_id, slot->payload);
            rigi_failreg_unlock();
            rigi_region_exit();
            return;
        }
    }
    rigi_failreg_unlock();
    rigi_region_exit();
    fprintf(stderr, "rigi_rt: failure_bind 节点不存在\n");
    abort();
}

/* 登记（DONE 垫尾）：+1 拷贝持有异常胖引用，返回节点 id（>0）。
 * Phase 3c（GC_OPTIMIZATION_PLAN §2.3）：失败发布点一次性整图
 * promotion——失败 Task 的属主协程正在死亡，壳模型无人可委托，异常图
 * 却可被多 waiter 跨线程持有（arc.h rigi_acquire_local 注释口径），
 * 在此把异常为根的 refMap 可达图逐实例翻位（local→shared 单调），此后
 * 按 shared 规则走。本函数是 failreg 唯一登记入口（EmitFailTerminal
 * DONE 垫尾/探测失败尾共用），翻位发生在节点入链之前——id 尚未发布、
 * Task 隐藏槽未 bind、fail() 未迁移终态、publishAll 未唤醒任何 waiter，
 * 「翻位完成先于任何非属主触碰」由本调用序保证（冷路径，按图大小
 * 付费）。VM 侧失败由 VmDispatch._failed 承载，不经本表（行为基准
 * 不变）。 */
int64_t rigi_failure_record(const RigiFatRef *exc)
{
    RigiFailureNode *node;
    if (exc == NULL)
    {
        fprintf(stderr, "rigi_rt: rigi_failure_record 参数为 NULL（编译器 bug）\n");
        abort();
    }
    node = (RigiFailureNode *)rigi_track_malloc(sizeof(RigiFailureNode));
    node->failure = *exc;
    node->observed = 0;
    node->next = NULL;
    if (node->failure.payload != 0)
    {
        node->failure.payload = rigi_ref_acquire(
            node->failure.type_id, node->failure.payload);
        /* 此刻异常图 rc 仅被属主线程触碰（本协程 DONE 垫尾），满足
         * promotion 翻位前置协议（macrogc.h rigi_gc_promote_subgraph） */
        rigi_gc_promote_subgraph(node->failure.type_id,
            node->failure.payload);
    }
    rigi_failreg_lock();
    if (!rigi_failreg_cleanup_registered)
    {
        rigi_failreg_cleanup_registered = 1;
        atexit(rigi_failreg_cleanup);
    }
    node->id = ++rigi_failreg_next_id;
    node->next = rigi_failreg_head;
    rigi_failreg_head = node;
    {
        int64_t id = node->id;
        rigi_failreg_unlock();
        return id;
    }
}

/* 观察标记（await 观察失败 Task 时由 registerWaiter 调用；幂等）。
 * 节点不摘除：await 快路径重抛需重读异常（rigi_failure_get）——
 * 已观察节点由汇总跳过，直到 Task 死亡才交接回收 */
void rigi_failure_drop(int64_t id)
{
    RigiFailureNode *node;
    RigiFatRef release = {0, 0};
    if (id == 0)
    {
        return;
    }
    rigi_region_enter();
    rigi_failreg_lock();
    for (node = rigi_failreg_head; node != NULL; node = node->next)
    {
        if (node->id == id)
        {
            if (!node->observed)
            {
                node->observed = 1;
                release = node->failure;
            }
            break;
        }
    }
    rigi_failreg_unlock();
    /* 放报告 root，不放 Task 槽；闸外释放允许普通析构级联。 */
    rigi_failreg_release_ref(&release);
    rigi_region_exit();
}

/* 读取（await 失败快路径）：节点 id → 异常拷贝（每次读取独立
 * acquire，+1 随 out 移交；可重复读）。id 未知/0 属运行时 bug，
 * 诊断 abort */
int32_t rigi_failure_get(int64_t id, RigiFatRef *out)
{
    RigiFailureNode *node;
    if (out == NULL || id == 0)
    {
        fprintf(stderr, "rigi_rt: rigi_failure_get 参数非法（编译器 bug）\n");
        abort();
    }
    /* fence 必须先于册闸，避免持闸 reader 等 GC、GC 析构又等册闸。 */
    rigi_region_enter();
    rigi_failreg_lock();
    for (node = rigi_failreg_head; node != NULL; node = node->next)
    {
        if (node->id == id)
        {
            *out = node->failure;
            if (out->payload != 0)
            {
                out->payload = rigi_ref_acquire(out->type_id, out->payload);
            }
            rigi_failreg_unlock();
            rigi_region_exit();
            return 1;
        }
    }
    rigi_failreg_unlock();
    rigi_region_exit();
    fprintf(stderr, "rigi_rt: rigi_failure_get 节点 %lld 不存在（运行时 bug）\n",
        (long long)id);
    abort();
}

/* 汇总（rigi_entry）：首个未观察失败 = 1 且异常 +1 随 out 移交
 *（出链），无 = 0。已观察节点跳过（保留供重读），重复调用不重复
 * 返回同一失败 */
int32_t rigi_failure_take_unobserved(RigiFatRef *out)
{
    RigiFailureNode **link;
    RigiFailureNode *found = NULL;
    if (out == NULL)
    {
        fprintf(stderr,
            "rigi_rt: rigi_failure_take_unobserved 参数为 NULL（编译器 bug）\n");
        abort();
    }
    rigi_failreg_lock();
    for (link = &rigi_failreg_head; *link != NULL; link = &(*link)->next)
    {
        if (!(*link)->observed)
        {
            found = *link;
            *link = found->next;
            break;
        }
    }
    rigi_failreg_unlock();
    if (found == NULL)
    {
        out->type_id = 0;
        out->payload = 0;
        return 0;
    }
    *out = found->failure; /* 所有权随 out 移交，不再 release */
    rigi_track_free(found);
    return 1;
}
