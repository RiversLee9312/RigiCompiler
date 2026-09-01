/*
 * 未观察失败注册表（MW11c 棒5a，native 半场；RUNTIME §18.2/§18.3）：
 * Task 失败异常本体的 native 承载。shared class 不得持 local
 * Exception 字段（P2 检查），故异常对象由本注册表 +1 持有，Task 只存
 * 节点 id（i64 字段 failureNodeId）。通道：
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

#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>

typedef struct RigiFailureNode
{
    int64_t id;
    RigiFatRef failure; /* +1 持有（tag2 对象；零值防御 tolerated） */
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
        rigi_release_shared((void *)(uintptr_t)fat->payload);
        fat->payload = 0;
        fat->type_id = 0;
    }
}

/* atexit 兜底：残余节点统一释放（未观察失败已随进程退出终结） */
static void rigi_failreg_cleanup(void)
{
    rigi_failreg_lock();
    while (rigi_failreg_head != NULL)
    {
        RigiFailureNode *node = rigi_failreg_head;
        rigi_failreg_head = node->next;
        rigi_failreg_release_ref(&node->failure);
        rigi_track_free(node);
    }
    rigi_failreg_unlock();
}

/* 登记（DONE 垫尾）：+1 拷贝持有异常胖引用，返回节点 id（>0） */
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
        rigi_acquire_shared((void *)(uintptr_t)node->failure.payload);
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
 * 已观察节点由汇总跳过、随进程退出经 atexit 链释放 */
void rigi_failure_drop(int64_t id)
{
    RigiFailureNode *node;
    if (id == 0)
    {
        return;
    }
    rigi_failreg_lock();
    for (node = rigi_failreg_head; node != NULL; node = node->next)
    {
        if (node->id == id)
        {
            node->observed = 1;
            break;
        }
    }
    rigi_failreg_unlock();
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
    rigi_failreg_lock();
    for (node = rigi_failreg_head; node != NULL; node = node->next)
    {
        if (node->id == id)
        {
            *out = node->failure;
            if (out->payload != 0)
            {
                rigi_acquire_shared((void *)(uintptr_t)out->payload);
            }
            rigi_failreg_unlock();
            return 1;
        }
    }
    rigi_failreg_unlock();
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
