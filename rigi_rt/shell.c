/*
 * shell.c（GC Phase 3b-β，δ2 属主化改线）：Handle 壳注册表、归零
 * 转移与属主挂起栈消化实现。契约见 shell.h 头注释。
 *
 * 并发模型（δ2 起）：
 *   - 两张表（全局索引 + 属主分组）统一由全局自旋锁保护——壳操作均
 *     为低频边界操作，锁开销可忽略。
 *   - 清理权唯一且属主化：teardown 只做 promote + owner_reg 原子改指
 *     （不清壳）；壳的归零清理只发生在属主互斥执行槽——cohandle
 *     resume 门闸内的 rigi_shell_drain_pending（段前/段后）与 teardown
 *     的终局排空。发布者线程归零转移只压挂起栈（δ2 封口：β 的
 *     「publishNative 分支发布者线程同步清理」让步点已删除，anchor
 *     release 不再落在发布者线程——3d 非原子 local 会计前置条件）。
 *     「count ≥ 1 ⟹ 壳必活」+「count == 0 ⟹ 恰有一个清理者在途」
 *     由 fetch_sub 的原子旧值判定保证。
 *   - 压栈/弹栈时序与竞态分析（shell.h 头注释详述，此处索引）：
 *     竞态一 属主运行中投递——压栈 pin（retain）使 teardown 推迟，
 *     弹栈点在执行门闸内，release-CAS → acquire 读 happens-before；
 *     竞态二 属主终止中投递——retain 验活失败转兜底内核（补 promote），
 *     pin 释放晚于压栈 ⟹ teardown 必兜住已压项；竞态三 重复/迟到——
 *     归零转移 fetch_sub old==1 恰一次，弹栈侧索引查找幂等丢弃；
 *     竞态四 弹栈单消费者——resume 门闸 + seq using 强引用与 teardown
 *     互斥成立。
 *
 * 静态名一律 sh_ / rigi_shell_ 前缀（unity build 单编译单元防碰撞，
 * cohandle.c 先例）。纯 C11 + <stdatomic.h>；台账配对
 *(track_malloc/free）。
 */
#include "shell.h"
#include "arc.h"
#include "macrogc.h"
#include "cohandle.h"
#include "native_rc.h"

#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* shellID 分配器：进程内全局唯一、永不复用；0 = 无效保留 */
static _Atomic uint64_t sh_next_id = 1;

/* ---- 壳与注册表结构 ---- */

/* per-协程属主分组表：挂 RigiCoHandle 槽位，teardown 遍历用（β 起
 * 摘链/遍历均在全局锁内，不再是属主独占结构） */
typedef struct RigiShellRegistry RigiShellRegistry;

/* 壳实体：target 胖引用为锚引用（make 时恰好一次 acquire；target 的
 * rc 只被属主触碰——过户 promote 后按 shared 原子会计）。count =
 * 存活 capability 对象数。owner = 属主 NativeRc token（验活/诊断）；
 * owner_reg = 归属注册表（原子指针：过户 = teardown 锁内改指全局，
 * 裁定 #4「指针改指」形态，壳物理不动。注意 _Atomic 修饰指针本身：
 * `RigiShellRegistry *_Atomic`，指 callee 可原子 load/store 指针值） */
typedef struct RigiShell
{
    uint64_t shell_id;       /* shellID：全局唯一身份，永不复用 */
    uint64_t target_type;    /* target 胖引用 {type, payload}（锚引用） */
    uint64_t target_payload;
    _Atomic int64_t count;   /* 存活 capability 对象数（原子） */
    uint64_t owner;          /* 属主协程 NativeRc token；0 = 全局归属 */
    RigiShellRegistry *_Atomic owner_reg; /* 归属表（原子改指） */
    struct RigiShell *group_next; /* 属主分组链（teardown 遍历用） */
    struct RigiShell *index_next; /* 全局索引链（shellID 唯一真存储） */
} RigiShell;

struct RigiShellRegistry
{
    RigiShell *group_head;   /* 属主分组链（头插） */
    size_t group_count;      /* 分组壳数（诊断口径） */
};

/* 全局兜底归属表：无协程上下文创建的壳 / 属主终止过户的壳归属这里
 *（owner_reg 终值）；与全局索引共用一把锁 */
static RigiShellRegistry sh_global_registry;

/* 全局索引：shellID → 壳 唯一真存储（头插单链；handle_release 的
 * 查找键，免属主枚举） */
static RigiShell *sh_index = NULL;
static size_t sh_index_count = 0;

static atomic_flag sh_gate = ATOMIC_FLAG_INIT;

static void sh_global_lock(void)
{
    while (atomic_flag_test_and_set_explicit(&sh_gate, memory_order_acquire))
    {
    }
}

static void sh_global_unlock(void)
{
    atomic_flag_clear_explicit(&sh_gate, memory_order_release);
}

/* ---- 表内操作（调用方持全局锁） ---- */

/* 归零清理内核前向声明（release_via_ptr 死属主兜底分支使用，定义在
 * API 实现段）。 */
static void sh_release_by_id(uint64_t shell_id);

static RigiShell *sh_index_find(uint64_t shell_id)
{
    RigiShell *s;
    for (s = sh_index; s != NULL; s = s->index_next)
    {
        if (s->shell_id == shell_id) return s;
    }
    return NULL;
}

static void sh_index_remove(RigiShell *shell)
{
    RigiShell **link;
    for (link = &sh_index; *link != NULL; link = &(*link)->index_next)
    {
        if (*link == shell)
        {
            *link = shell->index_next;
            shell->index_next = NULL;
            sh_index_count--;
            return;
        }
    }
    fprintf(stderr, "rigi_rt: 壳索引摘除未命中（shellID=%llu，编译器 bug）\n",
        (unsigned long long)shell->shell_id);
    abort();
}

/* 分组链摘除：返回 1 = 摘除成功、0 = 未命中（未命中 = teardown 已把
 * 整组摘走——所有表操作锁内串行，读到旧 owner_reg 的窗口已消除，
 * 容忍分支仅作防御） */
static int sh_group_remove(RigiShellRegistry *reg, RigiShell *shell)
{
    RigiShell **link;
    for (link = &reg->group_head; *link != NULL; link = &(*link)->group_next)
    {
        if (*link == shell)
        {
            *link = shell->group_next;
            shell->group_next = NULL;
            reg->group_count--;
            return 1;
        }
    }
    return 0;
}

/* 属主分组表定位（懒建；调用方持全局锁——make 语境属主本线程写槽位
 * 字段，无竞争）。只读定位用 rigi_ch_shell_registry_load 直读。 */
static RigiShellRegistry *sh_registry_of_owner(uint64_t owner_token)
{
    void *co = rigi_native_rc_payload_of((int64_t)owner_token, "rigi_shell_make");
    RigiShellRegistry *reg = rigi_ch_shell_registry_load(co);
    if (reg == NULL)
    {
        reg = (RigiShellRegistry *)rigi_track_malloc(sizeof(RigiShellRegistry));
        reg->group_head = NULL;
        reg->group_count = 0;
        rigi_ch_shell_registry_store(co, reg);
    }
    return reg;
}

/* ---- 子图 shared 会计提升（teardown / 消息兜底用） ----
 * Phase 3c 起共用面移至 macrogc.c 的 rigi_gc_promote_subgraph（显式栈
 * + visited + 数组分支 + 对象 refMap 以对象头为基准的修正版 walker；
 * 本文件原 3b-α/β 本地实现两处缺陷随提取修正：off-by-16B 起点错位、
 * 数组元素子图漏翻——详见 macrogc.c 提取注记）。本文件调用点语境：
 * teardown 在全局壳锁内 promote（只翻位不取锁，无自锁风险）、兜底在
 * 死属主语境锁外 promote——均满足翻位前置协议。 */

/* ---- 壳终态：anchor release + free（清理权唯一收口点） ----
 * anchor release 按目标实例会计位走 rigi_ref_release 既有分流：
 * 未提升 = local 会计（原子实现，任意线程行为安全）；过户 promote 后
 * = shared 原子。δ2 起本函数只从属主互斥执行槽可达（挂起栈消化与
 * 全局壳就地清理均在属主槽/过户后语境，见文件头）。 */

static void sh_shell_destroy(RigiShell *shell)
{
    rigi_ref_release(shell->target_type, shell->target_payload);
    shell->target_type = 0;
    shell->target_payload = 0;
    rigi_track_free(shell);
}

/* ---- per-属主挂起栈（3b-δ2） ----
 * 节点 = {shellID, next} 侵入式单链；栈顶指针挂 RigiCoHandle 槽位
 *（rigi_ch_pending_slot）。MPSC：多发布者 CAS 压栈（release）；
 * 属主互斥单消费者 CAS 弹栈（acquire）——弹栈点 = resume 门闸内
 * drain 与 teardown 排空，两者经「resume 在途必有 seq using 强引用
 * ⟹ teardown 未启动」+「teardown 在途 ⟹ token 已死无人可 pin 压栈」
 * 互斥。单消费者 + 节点仅在 CAS 摘链成功后释放 ⟹ 压栈方 CAS 失败
 * 重读栈顶即可，无 ABA/UAF（压栈方从不解引用非自持节点）。 */

typedef struct RigiShellPendingNode
{
    uint64_t shell_id;
    struct RigiShellPendingNode *next;
} RigiShellPendingNode;

/* ---- API 实现 ---- */

void *rigi_shell_make(uint64_t target_type, uint64_t target_payload,
                      uint64_t *out_shell_id)
{
    RigiShell *shell = (RigiShell *)rigi_track_malloc(sizeof(RigiShell));
    /* 取当前协程 token：TLS 协程为 NULL（含主线程根协程）→ 0 →
     * 归属全局表（cohandle.h rigi_coroutine_current 口径） */
    uint64_t owner = rigi_coroutine_current();
    if (out_shell_id == NULL)
    {
        fprintf(stderr, "rigi_rt: rigi_shell_make 出参为空（编译器 bug）\n");
        abort();
    }
    shell->shell_id = atomic_fetch_add_explicit(&sh_next_id, 1,
        memory_order_relaxed);
    /* anchor acquire：capability 生命周期内唯一一次 target acquire
     *（裁定 #6；空 payload 自然无操作，rigi_ref_acquire 口径） */
    shell->target_type = target_type;
    shell->target_payload = rigi_ref_acquire(target_type, target_payload);
    atomic_init(&shell->count, 1);
    shell->owner = owner;
    shell->group_next = NULL;
    shell->index_next = NULL;
    sh_global_lock();
    if (owner != 0)
    {
        RigiShellRegistry *reg = sh_registry_of_owner(owner);
        atomic_store_explicit(&shell->owner_reg, reg, memory_order_relaxed);
        shell->group_next = reg->group_head;
        reg->group_head = shell;
        reg->group_count++;
    }
    else
    {
        atomic_store_explicit(&shell->owner_reg, &sh_global_registry,
            memory_order_relaxed);
    }
    shell->index_next = sh_index;
    sh_index = shell;
    sh_index_count++;
    sh_global_unlock();
    *out_shell_id = shell->shell_id;
    return shell;
}

void rigi_shell_increment_ptr(void *shell_ptr)
{
    RigiShell *shell = (RigiShell *)shell_ptr;
    /* 计数直接原子加；「count ≥ 1 ⟹ 壳必活」由调用方保证 */
    atomic_fetch_add_explicit(&shell->count, 1, memory_order_relaxed);
}

void rigi_shell_target_of(void *shell_ptr, uint64_t *out_type,
                          uint64_t *out_payload)
{
    RigiShell *shell = (RigiShell *)shell_ptr;
    if (out_type == NULL || out_payload == NULL)
    {
        fprintf(stderr, "rigi_rt: rigi_shell_target_of 出参为空（编译器 bug）\n");
        abort();
    }
    /* 借用读出（壳指针直达，count ≥ 1 ⟹ 壳必活）；acquire 与否由
     * 调用方决定（β：handle_target 保持 owned acquire） */
    *out_type = shell->target_type;
    *out_payload = shell->target_payload;
}

/* 归零清理（全局归属壳；调用方持锁进入）：只摘索引——全局壳无分组
 * 链。锁外 anchor release + free（析构可级联，不在锁内——native_rc.c
 * 先例）。 */
static void sh_zero_release_locked(RigiShell *shell)
{
    sh_index_remove(shell);
}

void rigi_shell_release_via_ptr(void *shell_ptr)
{
    RigiShell *shell = (RigiShell *)shell_ptr;
    int64_t old = atomic_fetch_sub_explicit(&shell->count, 1,
        memory_order_acq_rel);
    if (old > 1)
    {
        return; /* 还有存活 capability：无投递（裁定 #2 减量通道） */
    }
    /* old == 1：归零转移。归属判定（β 同窗口）：owner_reg acquire 读
     * = 全局 ⟹ 恒全局（teardown 只做属主表 → 全局单向改指），任意
     * 线程直接归零清理。 */
    if (atomic_load_explicit(&shell->owner_reg, memory_order_acquire)
        == &sh_global_registry)
    {
        sh_global_lock();
        sh_zero_release_locked(shell);
        sh_global_unlock();
        sh_shell_destroy(shell);
        return;
    }
    /* 属主表壳（δ2 属主化投递）：shellID 压入属主挂起栈，清理转交
     * 属主互斥执行槽（cohandle resume 段前/段后 drain / teardown
     * 排空）。owner 值锁内读取（与 teardown 的 owner=0 写串行——
     * owner_reg 读为旧值的窗口内 teardown 可能已在改写，锁内读保证
     * 取到一致值）。 */
    {
        int64_t owner;
        sh_global_lock();
        if (atomic_load_explicit(&shell->owner_reg, memory_order_relaxed)
            == &sh_global_registry)
        {
            /* 锁内复核：owner_reg 读后 teardown 恰好过户了本壳 →
             * 已 promote，按全局壳直接清理 */
            sh_zero_release_locked(shell);
            sh_global_unlock();
            sh_shell_destroy(shell);
            return;
        }
        owner = (int64_t)shell->owner;
        sh_global_unlock();
        /* 压栈前 native_rc_retain 验活 pin 住属主句柄：
         *   - pin 成功 ⟹ destroy_payload（teardown）未启动（native_rc
         *     归零 release 先锁内摘 entry 再回调 destroy）⟹ 压栈与
         *     teardown 互斥；pin 释放若恰为最后强引用，teardown 随后
         *     过户 + 排空本项（release-CAS → 弹栈 acquire 读，
         *     happens-before 成立，不丢项）。
         *   - pin 失败 = 属主已死：转兜底内核 sh_release_by_id——锁内
         *     重查归属，属主表分支验活必失败 → 补 promote 子图后清理
         *     （3b-β 裁定二逻辑保留；teardown 已过户的全局壳走直接
         *     分支）。
         * 发布者线程在此只做 count 减量、压栈与兜底清理（兜底仅发生
         * 在属主已死语境，非原子 local 会计的属主互斥前提仍成立）。 */
        if (rigi_native_rc_retain(owner) != 0)
        {
            void *h = rigi_native_rc_payload_of(owner,
                "rigi_shell_release_via_ptr");
            RigiShellPendingNode *node =
                (RigiShellPendingNode *)rigi_track_malloc(
                    sizeof(RigiShellPendingNode));
            RigiShellPendingNode *_Atomic *slot = rigi_ch_pending_slot(h);
            RigiShellPendingNode *head = atomic_load_explicit(slot,
                memory_order_relaxed);
            node->shell_id = shell->shell_id;
            do
            {
                node->next = head;
            } while (!atomic_compare_exchange_weak_explicit(slot, &head,
                node, memory_order_release, memory_order_relaxed));
            rigi_native_rc_release(owner); /* 交还 pin；见上时序注释 */
            return;
        }
    }
    sh_release_by_id(shell->shell_id);
}

/* 归零清理内核（3b-δ2：原 rigi_shell_handle_release 收编为静态——
 * 消息面删除后仅属主挂起栈消化与 release_via_ptr 死属主兜底调用）：
 * 全局索引查找 → 壳已不存在 = 重复/迟到幂等丢弃；壳未过户（owner_reg
 * 仍指属主表）时按属主验活分流——活 = 调用方处属主互斥语境直接清理
 * （anchor release 合法）；死 = 就地补 promote 子图后清理（裁定二）。
 * 过户壳（owner_reg 全局）= 直接清理。 */
static void sh_release_by_id(uint64_t shell_id)
{
    RigiShell *shell;
    RigiShellRegistry *reg;
    int promoted_needed = 0;
    sh_global_lock();
    shell = sh_index_find(shell_id);
    if (shell == NULL)
    {
        /* 重复/迟到投递：壳已清理（幂等丢弃） */
        sh_global_unlock();
        return;
    }
    /* 归属表指针锁内读（与 teardown 的改指/free 串行互斥）：owner_reg
     * != 全局 ⟹ 该分组表未被 teardown 处理（teardown 处理时同锁内
     * 已整组改指），指针必然有效。 */
    reg = (RigiShellRegistry *)atomic_load_explicit(&shell->owner_reg,
        memory_order_relaxed);
    if (reg != &sh_global_registry)
    {
        /* 壳未过户：属主终止与投递的竞态窗口（裁定二）。属主已死
         * → 就地补 promote 子图（属主已死无人碰子图，翻位前置协议
         * 满足）；属主仍活 → 调用方即属主互斥槽（挂起栈消化），
         * anchor release 在属主上下文完成。两种归属下都由本调用
         * 完成唯一清理。 */
        int alive = shell->owner != 0
            && rigi_native_rc_retain((int64_t)shell->owner) != 0;
        if (alive)
        {
            rigi_native_rc_release((int64_t)shell->owner);
        }
        promoted_needed = !alive;
        (void)sh_group_remove(reg, shell);
    }
    sh_index_remove(shell);
    sh_global_unlock();
    if (promoted_needed)
    {
        rigi_gc_promote_subgraph(shell->target_type, shell->target_payload);
    }
    sh_shell_destroy(shell);
}

/* 属主挂起栈消化（3b-δ2；属主互斥单消费者语境——cohandle resume
 * 门闸内段前/段后 + teardown 排空调用）：逐枚 CAS 弹栈 → 清理内核。
 * 消化期间新压栈的项（消化回调级联触发归零转移的防御分支）也会被
 * 后续弹空；消化不了的场景不存在——本函数返回时栈可为空（级联新项
 * 留待下一槽），但调用点序列（段前/段后/teardown）保证有限壳终被
 * 消化或随 teardown 排空。 */
void rigi_shell_drain_pending(void *handle)
{
    RigiShellPendingNode *_Atomic *slot;
    if (handle == NULL)
    {
        return;
    }
    slot = rigi_ch_pending_slot(handle);
    for (;;)
    {
        RigiShellPendingNode *head = atomic_load_explicit(slot,
            memory_order_acquire);
        if (head == NULL)
        {
            return;
        }
        if (atomic_compare_exchange_weak_explicit(slot, &head, head->next,
            memory_order_acquire, memory_order_acquire))
        {
            uint64_t shell_id = head->shell_id;
            rigi_track_free(head);
            sh_release_by_id(shell_id);
        }
    }
}

void rigi_shell_gc_release_capability(void *object,
                                      void (*raw_release)(uint64_t type_id,
                                                          uint64_t payload))
{
    RigiShell *shell;
    RigiShellRegistry *reg;
    int64_t old;
    memcpy(&shell, (const char *)object + 16, 8);
    old = atomic_fetch_sub_explicit(&shell->count, 1, memory_order_acq_rel);
    if (old > 1)
    {
        return; /* 壳仍有别的 capability 持有：壳不动 */
    }
    /* GC 冻结期归零：不投消息（fence 冻结期公共 release 面 /
     * Dispatcher 自锁）——就地原始清理：锁内摘双表，target 经
     * raw_release（macrogc gc_teardown_fat 包装：白色同胞边整条
     * 跳过 + rc 原始减，与白色批清理协议一致）拆解，壳即放 */
    sh_global_lock();
    sh_index_remove(shell);
    reg = (RigiShellRegistry *)atomic_load_explicit(&shell->owner_reg,
        memory_order_relaxed);
    if (reg != &sh_global_registry)
    {
        (void)sh_group_remove(reg, shell);
    }
    sh_global_unlock();
    raw_release(shell->target_type, shell->target_payload);
    shell->target_type = 0;
    shell->target_payload = 0;
    rigi_track_free(shell);
}

void rigi_shell_owner_teardown(void *handle)
{
    RigiShellRegistry *reg = rigi_ch_shell_registry_load(handle);
    RigiShell *chain;
    RigiShell *p;
    if (reg == NULL)
    {
        /* 属主从未创建壳：仍须排空挂起栈——压栈不以注册表存在为前提
         *(属主名下第一枚壳归零可能在 teardown 之前的窗口压栈） */
        rigi_shell_drain_pending(handle);
        return;
    }
    sh_global_lock();
    /* 整组摘下 + 逐壳过户：owner_reg 原子改指全局表（裁定 #4「指针
     * 改指」——壳物理不动，全局索引本就持有）+ 子图 promote。锁内
     * promote：与挂起栈消化的并发清理靠本锁互斥（清理权唯一归
     * drain/release_via_ptr，teardown 不在此清壳——本调用期间消化
     * 被挡在锁外，promote 完成改指后它们看到全局归属走直接路径）。 */
    chain = reg->group_head;
    reg->group_head = NULL;
    reg->group_count = 0;
    for (p = chain; p != NULL; p = p->group_next)
    {
        p->owner = 0;
        atomic_store_explicit(&p->owner_reg, &sh_global_registry,
            memory_order_release);
        rigi_gc_promote_subgraph(p->target_type, p->target_payload);
    }
    sh_global_unlock();
    /* 表本体释放与槽位清空在锁内完成：消化/兜底路径读到的 owner_reg
     * 指针在锁内恒有效（本表已整组改指，读方走全局分支不再解引用本
     * 指针） */
    rigi_ch_shell_registry_store(handle, NULL);
    rigi_track_free(reg);
    /* δ2 终局排空：过户后弹空挂起栈残留归零壳——已 promote，走全局
     * 分支就地清理；teardown 语境即属主互斥语境（token 已死：无 resume
     * 在途、无新压栈可发生），3d 语义合法。排空后本属主名下无未决壳，
     * 挂起栈随句柄终结。 */
    rigi_shell_drain_pending(handle);
}
