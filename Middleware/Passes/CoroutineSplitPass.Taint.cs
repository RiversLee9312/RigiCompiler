using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Passes
{
    public sealed partial class CoroutineSplitPass : IMwStage
    {
        // Taint 职责；与主文件共享同一类型、字段及生命周期。
        // ===== B-1 taint 分析 =====
        // 种子 = 含挂起点（await/yield/Mutex.enter）的非 async fn；沿
        // 调用边反向传染调用方至不动点（链根恒为 main 或 async fn）。
        // B-2 传染边全集：直调 / super（恒直调）/ 值类型宿主运算符
        //（直调形态）/ 虚·interface·class 运算符派发（闭包内任一实
        // 现 tainted 则整点升级——运行期目标静态不可钉死）；new init
        // 经构造点协议收编。R2-b：invoke.indirect（$$call 闭包）同
        // 虚派发口径传染；R2-c：new.indirect 按「tainted class init
        // × 静态实参形精确匹配」传染（值类型 init 一律受控拒绝）。
        private HashSet<string> TaintAnalysis(MwContext context, MirModule mir)
        {
            var tainted = new HashSet<string>(System.StringComparer.Ordinal);
            var byCanonical = new Dictionary<string, MirFunction>(
                System.StringComparer.Ordinal);
            foreach (var fn in mir.Functions)
            {
                byCanonical[fn.Symbol.Canonical] = fn;
            }
            var queue = new Queue<string>();
            // R2-c：new.indirect 保守边（运行期目标由 typeid 决定）
            // 不再一刀切拒绝——class init 由调用点臂协议收编
            //（EmitNewIndirectSplit）；值类型 init 无挂起协议（frame
            // .this 借用形态与 sret/原地构造路径不兼容），凡 tainted
            // 即拒（此前静态 new 形态静默语义错位——native exit 5
            // 无输出 vs VM 正常——补闸）
            foreach (var fn in mir.Functions)
            {
                if (!fn.IsAsync
                    && fn.Blocks.SelectMany(b => b.Instructions).Any(inst =>
                        inst is MirAwait or MirYieldBare or MirYieldAlarm
                        || IsMutexEnter(context, inst))
                    && tainted.Add(fn.Symbol.Canonical))
                {
                    queue.Enqueue(fn.Symbol.Canonical);
                }
            }
            while (queue.Count > 0)
            {
                var calleeCanonical = queue.Dequeue();
                var calleeFn = byCanonical[calleeCanonical];
                RejectUnsupportedTaintedShape(calleeFn);
                if (calleeFn.Symbol.HasKeyword(BilKeyword.Init)
                    && calleeFn.Symbol.Owner?.Declaration.Kind != BilTypeKind.Class)
                {
                    // R2-c：值类型 init 无挂起协议（见上注）
                    throw new MwNotSupportedException(
                        "B-2 暂不支持含挂起点的值类型 init（值类型构造路径无挂起协议）: "
                        + calleeCanonical);
                }
                foreach (var caller in mir.Functions)
                {
                    // async 调用方本身即状态机化（调用点在 split 时改写），
                    // 无需传染；tainted 集合只收需要新状态机化的普通 fn

                    if (caller.IsAsync || tainted.Contains(caller.Symbol.Canonical))
                    {
                        continue;
                    }
                    var infect = false;
                    foreach (var block in caller.Blocks)
                    {
                        foreach (var inst in block.Instructions)
                        {
                            switch (inst)
                            {
                                case MirCall call
                                    when call.Target.Canonical == calleeCanonical:
                                    // 直调形态（显式 invoke 直调 / 值
                                    // 类型宿主运算符）静态唯一目标→
                                    // 传染；虚/interface 派发形态由
                                    // 下方闭包规则统一覆盖
                                    var direct = call.OperatorDispatch
                                        ? Binding.ImplBinder.BindOperatorCall(call.Target)
                                        : Binding.ImplBinder.BindCall(call.Target);
                                    if (direct is Binding.DirectCallBinding)
                                    {
                                        infect = true;
                                    }
                                    break;
                                case MirSuperCall superCall
                                    when superCall.Target.Canonical == calleeCanonical:
                                    // B-2：super 已解析为唯一基类实现
                                    // （恒直调），同直调协议传染
                                    infect = true;
                                    break;
                                case MirNewObject newObject
                                    when newObject.Init?.Canonical == calleeCanonical:
                                    // B-2：含挂起点的 init 经构造点协
                                    // 议收编（EmitInitSplit——分配与
                                    // init 下钻分离）
                                    infect = true;
                                    break;
                            }
                            // 虚/interface/class 运算符派发点：目标集合
                            // 静态不唯一——闭包内任一实现 tainted 则整
                            // 点升级（调用方传染；站点协议化见
                            // EmitVirtualCallSplit）
                            if (!infect && inst is MirCall anyCall
                                && IsVirtualDispatchSite(anyCall)
                                && ClosurePairsOf(context, anyCall.Target,
                                    anyCall.OperatorDispatch)
                                    .Any(p => tainted.Contains(p.ImplCanonical)))
                            {
                                infect = true;
                            }
                            // R2-b：invoke.indirect（callable 协议
                            // $$call 虚调用）同虚派发口径——$$call 闭
                            // 包内任一实现 tainted 则调用方传染（站
                            // 点协议化复用 EmitVirtualCallSplit 臂）
                            if (!infect && inst is MirInvokeIndirect invoke
                                && ClosurePairsOf(context,
                                    IndirectCallOperatorOf(context, caller, invoke),
                                    operatorDispatch: false)
                                    .Any(p => tainted.Contains(p.ImplCanonical)))
                            {
                                infect = true;
                            }
                            // R2-c：new.indirect——模块内 tainted
                            // class init 重载与站点静态实参形精确
                            // 匹配（argc + 逐物化 sheet 代入的
                            // canonical 恒等）则调用方传染（站点
                            // 协议化见 EmitNewIndirectSplit）
                            if (!infect && inst is MirNewIndirect newIndirect
                                && IndirectInitRelevant(context, caller,
                                    newIndirect, tainted))
                            {
                                infect = true;
                            }
                        }
                    }
                    if (infect && tainted.Add(caller.Symbol.Canonical))
                    {
                        queue.Enqueue(caller.Symbol.Canonical);
                    }
                }
            }
            RejectSyntheticSpineTainted(context, mir, tainted);
            // Phase 2.6（§19.2 语义纠偏）：PollingAlarm.isReady 允许含挂起
            // 点——撤销原 #08 止血拒绝（RejectTaintedPollingIsReady，76e304c）。
            // tainted 探测不再走 $mw.poll_probe 同步虚派发（vtable 槽指向
            // ReplaceWithTrap 陷阱的旧风险），改经恢复块站点协议臂下钻
            //（PreparePollProbeSites / EmitPollGate 双路径）
            return tainted;
        }

        // R3：合成入口脊柱直调站点（非 MIR、不可协议化）的 taint 补闸。
        // rigi_entry stub 同步直调 ..globals.init（main 前）、singleton
        // 急切 get fn（同前）与 gexc drain 的 GlobalExceptionHandler.
        // dispatch（Dispatcher 关停后）；未捕获异常 reporter 同步虚调
        // getMessage（同在后段）。这些站点运行在调度器启动前/关停后，
        // 挂起点无泵可恢复——tainted 即 ReplaceWithTrap 后脊柱照调
        // 必崩（实证：trap 0x80000003 / AV），受控拒绝
        private static void RejectSyntheticSpineTainted(MwContext context, MirModule mir,
            HashSet<string> tainted)
        {
            // 先扫 getMessage 根因：脊柱 fn（如 dispatch 空注册表分支
            // printErr(exc.getMessage())）常因虚调 tainted override 被
            // 传染，报根因比报脊柱更准
            foreach (var fn in mir.Functions)
            {
                if (tainted.Contains(fn.Symbol.Canonical) && IsExceptionGetMessage(context, fn))
                {
                    throw new MwNotSupportedException(
                        "R3 暂不支持含挂起点的 core::Exception.getMessage override："
                        + "native 未捕获异常 reporter 同步虚调 getMessage: "
                        + fn.Symbol.Canonical);
                }
            }
            foreach (var fn in mir.Functions)
            {
                if (!tainted.Contains(fn.Symbol.Canonical))
                {
                    continue;
                }
                var canonical = fn.Symbol.Canonical;
                if (BilLogicalName.IsGlobalInitializer(canonical))
                {
                    throw new MwNotSupportedException(
                        "R3 暂不支持含挂起点（含经 lambda/indirect 传染）的全局初始值设定项："
                        + "native 入口脊柱在调度器启动前同步直调 ..globals.init: " + canonical);
                }
                if (canonical.StartsWith("core::GlobalExceptionHandler$.static.dispatch(",
                        System.StringComparison.Ordinal))
                {
                    throw new MwNotSupportedException(
                        "R3 暂不支持含挂起点的全局异常处理器：native gexc drain 在 "
                        + "Dispatcher 关停后同步直调 dispatch: " + canonical);
                }
                if (canonical.Contains(".mw.singleton.get(", System.StringComparison.Ordinal))
                {
                    throw new MwNotSupportedException(
                        "R3 暂不支持含挂起点的 singleton init：native 入口脊柱在调度器"
                        + "启动前急切同步构造 singleton: " + canonical);
                }
            }
        }

    }
}
