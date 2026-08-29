using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Passes
{
    /// <summary>
    /// singleton 运行时支持（MW10 刀5）：消费 SingletonPlanner 的收集。
    /// ① 为每个 singleton 类型 T 合成三态取实例 fn
    /// <c>T$.static.mw.singleton.get()@T</c>：状态与缓存落在合成静态槽（state
    /// i32：0=未构造/1=在途/2=就绪；cache 胖引用槽）——复用
    /// StaticFieldEmitter 既有静态存储，VM 的 _singletons/_initializing
    /// 双表在 native 收敛为「state + cache」两槽（state==2 ⇔ cache
    /// 非空）。就绪 → 直返缓存；未构造 → 置在途 → 真构造
    ///（MirNewObject：alloc + 隐藏 typeid + 零参 ..init.wrapper + 零参
    /// init，VM New 的 singleton 首构造同序——init.wrapper 先于 init
    /// 体由 EmitAllocAndInit 调用序保证）→ 登记缓存 → 置就绪 → 返回；
    /// 在途 = 构造环 → 抛 core::RuntimeException「singleton 初始化循环
    /// 依赖：T」（VM SingletonCycleException 同前缀；VM 消息带在途栈
    /// 全链「A → B → A」，native v1 静态槽形态没有在途链对象，只报
    /// 触发类型 T——已知分歧，以 VM 为准）。并发：v1 简单标志（MW11
    /// Worker 线程模型上线后复核原子性）。
    /// ② 把全部 MIR fn（get fn 自身除外）里 new type(单例) 的
    /// MirNewObject 改写为 MirCall(get fn)——运行期 new type(singleton)
    /// 返回同一份已初始化实例、不重跑 init（VM New 同语义）；实参按
    /// VM 口径丢弃（急切初始化保证用户代码执行时恒已构造；实参表达式
    /// 的求值副作用由先行指令承担，不随改写消失）。singleton 依法不得
    /// 声明类型参数（verifier），故无构造类型形态。
    /// 无零参 init 的 singleton（..globals.host 形态）合成空 init 凑齐
    /// 构造尾（VM「无 init 声明 + 零实参仍构造」同语义）；只有有参
    /// init 的 singleton 受控拒绝（VM 在 InitializeSingletons 期同样
    /// 必败，native 提前到编译期）。
    /// 读：Mir + Symbols；写：追加合成 fn、原地替换 new 指令、挂载
    /// MwContext.Singletons（Emit 消费）。小改写 pass，不上 CRTP。
    /// </summary>
    public sealed class SingletonLoweringPass : IMwStage
    {
        public string Name => "SingletonLowering";

        public void Run(MwContext context)
        {
            var mir = context.Mir
                ?? throw new CompilerInternalException("SingletonLowering 要求 Mir 已挂载");
            var singletons = SingletonPlanner.Collect(context.Symbols);
            if (singletons.Count == 0)
            {
                return;
            }
            var entries = new List<SingletonEntry>(singletons.Count);
            var getFnByType = new Dictionary<string, MwMemberSymbol>(
                System.StringComparer.Ordinal);
            foreach (var canonical in singletons)
            {
                var getFn = SynthesizeGetter(context, mir, canonical);
                getFnByType.Add(canonical, getFn.Symbol);
                entries.Add(new SingletonEntry(canonical, getFn.Symbol.Canonical,
                    SingletonPlanner.StateFieldOf(canonical),
                    SingletonPlanner.CacheFieldOf(canonical)));
            }
            context.Singletons = entries;
            RewriteNews(mir, singletons, getFnByType);
        }

        // ===== ① get fn 合成 =====

        private static MirFunction SynthesizeGetter(MwContext context, MirModule mir,
            string canonical)
        {
            var type = context.Symbols.FindType(canonical)
                ?? throw new CompilerInternalException("singleton 类型符号缺失: " + canonical);
            var typeRef = MirType.Of(canonical);
            var getCanonical = SingletonPlanner.GetFnCanonicalOf(canonical);
            var stateField = SingletonPlanner.StateFieldOf(canonical);
            var cacheField = SingletonPlanner.CacheFieldOf(canonical);
            var init = FindZeroArgInit(context, type);
            var initWrapper = MirBuilder.FindInitWrapper(context.Symbols, type, 0);
            var fnSymbol = ProxyBakeSupport.SyntheticMember(getCanonical, type);
            var fn = new MirFunction(fnSymbol, typeRef, new List<MirLocal>(),
                new List<MirLocal>(), new List<MirBlock>(), false);
            var i32 = ProxyWildcardAbi.I32Type;
            var boolean = ProxyWildcardAbi.BoolType;

            // entry：读三态 → 就绪支/继续判定
            var state = ProxyWildcardAbi.FreshLocal(fn, "$mw.sg.s.", i32);
            var two = ProxyWildcardAbi.FreshLocal(fn, "$mw.sg.c2.", i32);
            var isReady = ProxyWildcardAbi.FreshLocal(fn, "$mw.sg.rdy.", boolean);
            fn.AddBlock(new MirBlock("entry", new List<MirInst>
            {
                new MirGetStatic(stateField, state),
                new MirLoadResource(ProxyWildcardAbi.AddI32Resource(context, 2), two),
                new MirBinaryIntrinsic(BilBinaryOp.CmpEq, new MirLocalOperand(state),
                    new MirLocalOperand(two), i32, i32, boolean, isReady),
            }, new MirCondBranch(new MirLocalOperand(isReady), "mw.sg.ready", "mw.sg.check")));

            // check：在途 → 环异常支；否则进构造支
            var one = ProxyWildcardAbi.FreshLocal(fn, "$mw.sg.c1.", i32);
            var isInflight = ProxyWildcardAbi.FreshLocal(fn, "$mw.sg.fly.", boolean);
            fn.AddBlock(new MirBlock("mw.sg.check", new List<MirInst>
            {
                new MirLoadResource(ProxyWildcardAbi.AddI32Resource(context, 1), one),
                new MirBinaryIntrinsic(BilBinaryOp.CmpEq, new MirLocalOperand(state),
                    new MirLocalOperand(one), i32, i32, boolean, isInflight),
            }, new MirCondBranch(new MirLocalOperand(isInflight),
                "mw.sg.cycle", "mw.sg.construct")));

            // construct：置在途 → 真构造（带异常边：init 族内抛出 → 失败
            // 块置回未构造后传播，VM finally EndInitializing + 不登记同
            // 口径）→ 登记缓存 → 置就绪 → 返回
            var obj = ProxyWildcardAbi.FreshLocal(fn, "$mw.sg.obj.", typeRef);
            var twoAgain = ProxyWildcardAbi.FreshLocal(fn, "$mw.sg.c2.", i32);
            var failBlock = new MirBlock("mw.sg.fail", new List<MirInst>(),
                new MirRetThrow());
            fn.AddBlock(new MirBlock("mw.sg.construct", new List<MirInst>
            {
                new MirSetStatic(new MirLocalOperand(one), stateField),
                new MirNewObject(type, initWrapper, init,
                    new List<MirOperand>(), obj, excTarget: failBlock),
                new MirSetStatic(new MirLocalOperand(obj), cacheField),
                new MirLoadResource(ProxyWildcardAbi.AddI32Resource(context, 2), twoAgain),
                new MirSetStatic(new MirLocalOperand(twoAgain), stateField),
            }, new MirRet(new MirLocalOperand(obj))));

            // fail：构造中途抛出（pending 仍在 TLS）——置回未构造（VM
            // 构造失败不登记、在途出栈同口径；下回访问重试），终结符
            // MirRetThrow 由 RcInjection 解析进共享传播垫（垫按出口同
            // 口径 release 全部托管槽——obj 未落槽恒零，无误伤）
            var zeroAgain = ProxyWildcardAbi.FreshLocal(fn, "$mw.sg.c0.", i32);
            failBlock.InstructionList.Add(new MirLoadResource(
                ProxyWildcardAbi.AddI32Resource(context, 0), zeroAgain));
            failBlock.InstructionList.Add(new MirSetStatic(
                new MirLocalOperand(zeroAgain), stateField));
            fn.AddBlock(failBlock);

            // ready：直返缓存（VM GetSingleton 命中同语义）
            var cached = ProxyWildcardAbi.FreshLocal(fn, "$mw.sg.out.", typeRef);
            fn.AddBlock(new MirBlock("mw.sg.ready", new List<MirInst>
            {
                new MirGetStatic(cacheField, cached),
            }, new MirRet(new MirLocalOperand(cached))));

            // cycle：构造环 → 抛 RuntimeException（VM SingletonCycleException
            // 同前缀文案）；MirThrow 出厂块以 MirRetThrow 收尾（B 棒契约，
            // ExcTarget==null 的解析归 RcInjection 传播垫）
            var cycleInsts = new List<MirInst>();
            var msg = ProxyWildcardAbi.FreshLocal(fn, "$mw.sg.msg.", ProxyWildcardAbi.StringType);
            cycleInsts.Add(new MirLoadResource(
                ProxyWildcardAbi.AddStringResource(context, "singleton 初始化循环依赖：" + canonical),
                msg));
            var excType = context.Symbols.FindTypeByRef("core::RuntimeException")
                ?? throw new MwNotSupportedException(
                    "singleton 构造环异常要求 core::RuntimeException 在模块内（stdlib 缺失）");
            var excInit = FindTextInit(excType)
                ?? throw new CompilerInternalException(
                    "core::RuntimeException 缺 init(text: String)");
            var excInitWrapper = MirBuilder.FindInitWrapper(context.Symbols, excType, 0);
            var exc = ProxyWildcardAbi.FreshLocal(fn, "$mw.sg.exc.",
                MirType.Of(excType.Canonical));
            cycleInsts.Add(new MirNewObject(excType, excInitWrapper, excInit,
                new List<MirOperand> { new MirLocalOperand(msg) }, exc));
            cycleInsts.Add(new MirThrow(new MirLocalOperand(exc), null));
            fn.AddBlock(new MirBlock("mw.sg.cycle", cycleInsts, new MirRetThrow()));

            mir.AddFunction(fn);
            return fn;
        }

        // 零参 init 解析；类型无任何 init 声明时合成空 init（VM 无 init
        // 声明 + 零实参仍构造同语义）；只有有参 init 时受控拒绝（VM 急切
        // 初始化期必败，native 提前到编译期报出）
        private static MwMemberSymbol FindZeroArgInit(MwContext context, MwTypeSymbol type)
        {
            MwMemberSymbol? zeroArg = null;
            var hasOtherInit = false;
            foreach (var member in type.Members)
            {
                if (!member.HasKeyword(BilKeyword.Init))
                {
                    continue;
                }
                if (CanonicalSignature.Parse(member.Canonical).Parameters.Count == 0)
                {
                    zeroArg = member;
                }
                else
                {
                    hasOtherInit = true;
                }
            }
            if (zeroArg != null)
            {
                return zeroArg;
            }
            if (hasOtherInit)
            {
                throw new MwNotSupportedException(
                    "singleton 急切初始化要求零参 init（VM InitializeSingletons 同口径）: "
                    + type.Canonical);
            }
            return SynthesizeEmptyInit(context, type);
        }

        // 空 init 合成：体 = 裸 ret（字段零值由 alloc 清零承担，VM
        // AllocateObject 的 ZeroOf 同口径——native 对象头/字段槽由
        // rigi_alloc 零化）
        private static MwMemberSymbol SynthesizeEmptyInit(MwContext context, MwTypeSymbol type)
        {
            var mir = context.Mir!;
            var symbol = ProxyBakeSupport.SyntheticMember(type.Canonical + "$init()@.void", type);
            var thisParam = new MirLocal(".this", MirType.Of(type.Canonical));
            var fn = new MirFunction(symbol, MirType.Of(".void"),
                new List<MirLocal> { thisParam }, new List<MirLocal> { thisParam },
                new List<MirBlock>
                {
                    new MirBlock("entry", new List<MirInst>(), new MirRet(null)),
                }, false);
            mir.AddFunction(fn);
            return symbol;
        }

        // init(text: String) 解析（ProxyBakingPass.EmitThrowNoSuchMethod
        // 内 FindInit 同口径）
        private static MwMemberSymbol? FindTextInit(MwTypeSymbol type)
        {
            foreach (var member in type.Members)
            {
                if (!member.HasKeyword(BilKeyword.Init))
                {
                    continue;
                }
                var signature = CanonicalSignature.Parse(member.Canonical);
                if (signature.Parameters.Count == 1
                    && signature.Parameters[0].Name == "text")
                {
                    return member;
                }
            }
            return null;
        }

        // ===== ② new 改写 =====

        // 全部 MIR fn（get fn 与合成空 init 自身除外——get fn 体内的
        // MirNewObject 是真构造，改写会自递归）里 singleton 的
        // MirNewObject → MirCall(get fn)
        private static void RewriteNews(MirModule mir, IReadOnlyList<string> singletons,
            Dictionary<string, MwMemberSymbol> getFnByType)
        {
            var singletonSet = new HashSet<string>(singletons, System.StringComparer.Ordinal);
            var getFnNames = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var pair in getFnByType)
            {
                getFnNames.Add(pair.Value.Canonical);
            }
            foreach (var fn in mir.Functions)
            {
                if (getFnNames.Contains(fn.Symbol.Canonical))
                {
                    continue;
                }
                foreach (var block in fn.Blocks)
                {
                    var insts = block.InstructionList;
                    for (var i = 0; i < insts.Count; i++)
                    {
                        if (insts[i] is MirNewObject newObject
                            && singletonSet.Contains(newObject.Type.Canonical))
                        {
                            insts[i] = new MirCall(getFnByType[newObject.Type.Canonical],
                                new List<MirOperand>(), newObject.Target);
                        }
                    }
                }
            }
        }
    }
}
