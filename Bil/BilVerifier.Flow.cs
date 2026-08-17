using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RigiCompiler.Bil
{
    // BilVerifier 控制流检查（§21.4 definite assignment + §21.5 控制流
    // + §21.6 .breakid capability）。§21.8 enum struct 实例字段 init
    // 全路径写入由本文件 VerifyEnumStructInstanceFields 在 DA 之后执行。
    //
    // DA 为保守流分析（零误报优先，漏报可接受）：参数入口已赋值；线性
    // 读前赋值；if 分支独立分析、合并取交集；switch 全分支（含 default）
    // 独立分析、出口取全分支交集（恒执行且仅执行一个分支）；loop/try
    // 子块入口取进入态、出口保守取进入态；call blk 出口 = 块分析出口
    //（块内赋值对调用点可见，§16.1）。loop.rev 的 condition 首次读取在
    // body 之后，不查进入时已赋值（§16.4）。
    //
    // §18.1 rigi.seq-route hint（§21.4 route dispatcher 分组）：region
    // 指令（if/switch/call blk）后紧跟经 V0–V4 结构校验的 hint 时，DA
    // 为每条汇聚前驱边维护独立出口态并按 route 写入常量标注组号，尾链
    // 逐条剥离（本组边合并态精确送入 relay 目标块），块尾落尾仅由 0 组
    // 边流入（0 组为空则落尾静态不可达，不给续点送状态）。校验失败或
    // 无 hint 的模块退回保守全合并——与旧版行为逐位一致。
    //
    // §21.8 enum struct 实例字段（§14.3「enum 无零值」）：宿主每个 init 的
    // 全部完成路径须对该字段发 set.field（OBJECT 为 $.this）；get.field
    // 不得先于 set。路径合并：if 双分支交、正向 loop 出口=进入态（body
    // 可能零次）、loop.rev 出口=body 出口（至少一次）、switch 全分支交
    // （与 DA 的 switch 保进入态不同——本规则要证明全路径已写）、
    // try/catch 交再叠 finally（与 DA 同）。有 finally 时体内 ret/throw
    // 延后到 finally 之后才记离体。set.field.indirect 仅当 fieldid 能静态
    // 解到该字段才算已写，否则保守未设置。跨函数把 $.this 传出、以及
    // $.this 别名不追踪（frontend 保证不变量，本规则是安全网）。静态字段
    // / .vars / 数组元素不在本规则。泛型 enum 构造形态经 DeclarationKeyOf
    // 反查；字段类型含 .generic< 则跳过。
    //
    // 结构环（含 call blk 直接结构递归）一律拒绝——§21.5 允许实现拒绝
    // 无法证明有界的直接结构递归。

    public static partial class BilVerifier
    {
        private static void VerifyFunctionFlow(BilFunctionContext context,
            List<BilVerificationError> errors)
        {
            var function = context.Function;

            // ===== §21.5 结构：恰一个 entrypoint block；entry 必须终止性收尾 =====
            var entryBlocks = new List<BilBlock>();
            foreach (var block in function.Blocks)
            {
                foreach (var modifier in block.Modifiers)
                {
                    if (modifier == BilBlockModifier.Entrypoint)
                    {
                        entryBlocks.Add(block);
                    }
                }
            }
            BilBlock? entryBlock = null;
            if (entryBlocks.Count != 1)
            {
                errors.Add(new BilVerificationError("21.5", function.Symbol,
                    $"函数必须恰有一个 entrypoint block（实际 {entryBlocks.Count}）"));
            }
            else
            {
                entryBlock = entryBlocks[0];
                // §9.4：entrypoint block 不得正常落到末尾——结构化终止判定：
                // 末指令是 ret/throw，或是全分支都终止的结构指令（全分支
                // return 的 switch/if/try 之后发射器不再补 ret）
                if (!BlockTerminates(context, entryBlock, new HashSet<BilBlock>(
                    ReferenceEqualityComparer.Instance)))
                {
                    errors.Add(new BilVerificationError("21.5", function.Symbol,
                        $"entrypoint block \"{entryBlock.Id}\" 不得以落尾结束（必须显式 ret 或 throw）"));
                }
            }

            // ===== §21.5 块成员资格 + §21.6 breakid 绑定唯一 =====
            var breakIdBindings = new Dictionary<string, string>();
            foreach (var (block, instruction) in InstructionsInFunction(context))
            {
                var location = function.Symbol + " / " + block.Id;
                foreach (var referenced in ReferencedBlocks(instruction))
                {
                    if (!context.BlockSet.Contains(referenced))
                    {
                        errors.Add(new BilVerificationError("21.5", location,
                            $"block 引用越权：\"{referenced.Id}\" 不属于当前函数"));
                    }
                }
                // 结构化 region 指令的 breakid 绑定位（§16.5 推广：
                // loop/loop.rev/switch/if/call/try）：必须声明为
                // .breakid 变量，且全 fn 唯一绑定一次（§9.3/§21.6）
                string? boundBreakId = instruction switch
                {
                    LoopInstruction loop => loop.BreakId.Name,
                    SwitchInstruction switchInstruction => switchInstruction.BreakId.Name,
                    IfInstruction ifInstruction => ifInstruction.BreakId.Name,
                    CallBlockInstruction call => call.BreakId.Name,
                    TryInstruction tryInstruction => tryInstruction.BreakId.Name,
                    _ => null,
                };
                if (boundBreakId != null)
                {
                    if (!context.VariableTypes.ContainsKey(boundBreakId))
                    {
                        errors.Add(new BilVerificationError("21.2", location,
                            $"breakid 变量 \"${boundBreakId}\" 未声明"));
                    }
                    else if (!context.BreakIdVariables.Contains(boundBreakId))
                    {
                        errors.Add(new BilVerificationError("21.6", location,
                            $"结构化 region 指令只能绑定 .breakid 类型变量，\"${boundBreakId}\" " +
                            $"声明类型为 \"{context.VariableTypes[boundBreakId]}\""));
                    }
                    if (breakIdBindings.TryGetValue(boundBreakId, out var firstLocation))
                    {
                        errors.Add(new BilVerificationError("21.6", location,
                            $".breakid 变量 \"${boundBreakId}\" 被二次绑定（首次于 {firstLocation}）"));
                    }
                    else
                    {
                        breakIdBindings.Add(boundBreakId, location);
                    }
                }
            }

            // ===== §21.4 DA + token 作用域（从 entrypoint 出发的可达分析）=====
            if (entryBlock == null)
            {
                return;
            }
            var assigned = new HashSet<string>();
            foreach (var arg in function.Args)
            {
                if (arg.Name != ".return")
                {
                    assigned.Add(arg.Name);
                }
            }
            AnalyzeBlock(context, entryBlock, assigned,
                new List<(string Name, bool IsLoop)>(),
                new HashSet<BilBlock>(ReferenceEqualityComparer.Instance),
                errors, new HashSet<(string Location, string Name)>());
            VerifyEnumStructInstanceFields(context, entryBlock, errors);
        }

        // 结构化终止判定（§9.4）：块末指令是否保证不以落尾结束。
        // ret/throw 终止；switch 要求 item 与 default 全终止；if 要求双分支
        // 都在且全终止；try 要求 body 与全部 catch handler 终止；loop 可能
        // 零次执行、call blk 落尾返回——均不算终止。环与越权块保守为否
        private static bool BlockTerminates(BilFunctionContext context, BilBlock block,
            HashSet<BilBlock> visited)
        {
            if (!visited.Add(block) || block.Instructions.Count == 0)
            {
                return false;
            }
            var last = block.Instructions[block.Instructions.Count - 1];
            switch (last)
            {
                case RetInstruction:
                case ThrowInstruction:
                    return true;
                case SwitchInstruction switchInstruction:
                    foreach (var itemBlock in switchInstruction.ItemBlocks)
                    {
                        if (!BlockTerminates(context, itemBlock, visited))
                        {
                            return false;
                        }
                    }
                    return BlockTerminates(context, switchInstruction.DefaultBlock, visited);
                case IfInstruction ifInstruction:
                    return ifInstruction.ElseBlock != null
                        && BlockTerminates(context, ifInstruction.ThenBlock, visited)
                        && BlockTerminates(context, ifInstruction.ElseBlock, visited);
                case TryInstruction tryInstruction:
                    // try-finally 无 catch 时 catch-table 为空——无 handler
                    // 要查，body 终止即判终止（finally 是通道，不改变终止性）
                    if (tryInstruction.CatchTable is not BilCatchTableResource catchTable
                        || !BlockTerminates(context, tryInstruction.Body, visited))
                    {
                        return false;
                    }
                    foreach (var entry in catchTable.Entries)
                    {
                        if (!BlockTerminates(context, entry.Handler, visited))
                        {
                            return false;
                        }
                    }
                    return true;
                default:
                    return false;
            }
        }

        // 块分析：沿结构化指令递归。assigned 为进入态（就地演进），返回出口态；
        // tokens 为活跃 breakid 结构栈（来源指令种类区分 loop 与非 loop——
        // continue 仅允许 loop token）；stack 为分析路径块栈（环检测）；
        // reported 按 (位置, 变量) 去重 DA 错误。
        // 返回 null = 本块落尾静态不可达（仅 §18.1 hint 消费产生：0 组为空
        // 或汇聚边全部被收集；无 hint 模块恒非 null，行为与旧版逐位一致）。
        // collectors/flow 是 hint 消费的逐边状态（region 收集器栈 + 路径
        // 常量追踪），非 hint 模式恒 null；flow 按就地演进纪律使用（分叉
        // 传克隆、合并写回），出口时与返回的 assigned 同属一条路径
        private static HashSet<string>? AnalyzeBlock(BilFunctionContext context, BilBlock block,
            HashSet<string> assigned, List<(string Name, bool IsLoop)> tokens,
            HashSet<BilBlock> stack, List<BilVerificationError> errors,
            HashSet<(string Location, string Name)> reported,
            List<SeqRouteCollector>? collectors = null, SeqRouteFlow? flow = null)
        {
            if (!context.BlockSet.Contains(block))
            {
                return assigned;   // 跨函数引用已由结构检查报错
            }
            if (!stack.Add(block))
            {
                errors.Add(new BilVerificationError("21.5", context.Function.Symbol,
                    $"结构块引用成环（含 call blk 直接结构递归）：\"{block.Id}\""));
                return new HashSet<string>(assigned);
            }
            try
            {
                return AnalyzeBlockInstructions(context, block, 0, assigned, tokens, stack,
                    errors, reported, collectors, flow);
            }
            finally
            {
                stack.Remove(block);
            }
        }

        // 块指令序列主循环（AnalyzeBlock 自 0 起；hint 消费的链尾续点自
        // 链节 if 之后起）
        private static HashSet<string>? AnalyzeBlockInstructions(BilFunctionContext context,
            BilBlock block, int startIndex, HashSet<string> assigned,
            List<(string Name, bool IsLoop)> tokens, HashSet<BilBlock> stack,
            List<BilVerificationError> errors,
            HashSet<(string Location, string Name)> reported,
            List<SeqRouteCollector>? collectors, SeqRouteFlow? flow)
        {
            var reads = new List<BilVariableOperand>();
            var writes = new List<BilVariableOperand>();
            var instructions = block.Instructions;
            for (var i = startIndex; i < instructions.Count; i++)
            {
                var instruction = instructions[i];
                var location = context.Function.Symbol + " / " + block.Id;
                reads.Clear();
                writes.Clear();
                ClassifyVariables(instruction, reads, writes);

                // §21.4：读前已赋值
                foreach (var variable in reads)
                {
                    if (context.VariableTypes.ContainsKey(variable.Name)
                        && !assigned.Contains(variable.Name)
                        && reported.Add((location, variable.Name)))
                    {
                        errors.Add(new BilVerificationError("21.4", location,
                            $"变量 \"${variable.Name}\" 在赋值前被读取"));
                    }
                }

                // §18.1/§21.4：region 指令（if/switch/call blk）后紧跟
                // rigi.seq-route hint → route dispatcher 分组消费（逐边
                // 状态 + 尾链剥离）；V0–V4 任一校验失败静默忽略（退回保守
                // 全合并，行为同无 hint）
                if (instruction is IfInstruction or SwitchInstruction or CallBlockInstruction
                    && i + 1 < instructions.Count
                    && instructions[i + 1] is HintInstruction hint
                    && TryConsumeSeqRouteHint(context, block, i, instruction, hint, assigned,
                        tokens, stack, errors, reported, collectors, flow,
                        out var resumed, out var resumedFlow, out var resumeIndex))
                {
                    if (resumed == null)
                    {
                        return null;   // 0 组为空：落尾边静态不可达，不给续点送状态
                    }
                    assigned = resumed;
                    flow = resumedFlow;
                    i = resumeIndex;   // 链节已随剥离消费，for 的 i++ 落到续点
                    continue;
                }

                switch (instruction)
                {
                    case BreakInstruction breakInstruction:
                        VerifyBreakToken(context, breakInstruction.BreakId, tokens,
                            isContinue: false, location, errors);
                        // hint 收集器活跃：break 命中收集的 region token →
                        // 记录一条汇聚前驱边，本路径不再落尾
                        if (TryRecordSeqRouteEdge(collectors, breakInstruction.BreakId.Name,
                                assigned, flow))
                        {
                            return null;
                        }
                        break;
                    case ContinueInstruction continueInstruction:
                        VerifyBreakToken(context, continueInstruction.BreakId, tokens,
                            isContinue: true, location, errors);
                        break;
                    case RetInstruction ret:
                        // §16.8：ret 形态与 .return 匹配
                        if (context.ReturnType != null)
                        {
                            if (ret.Value == null && context.ReturnType != ".void")
                            {
                                errors.Add(new BilVerificationError("21.5", location,
                                    $"非 void 函数（.return = {context.ReturnType}）不得裸 ret"));
                            }
                            if (ret.Value != null && context.ReturnType == ".void")
                            {
                                errors.Add(new BilVerificationError("21.5", location,
                                    "void 函数 ret 不得带值"));
                            }
                        }
                        break;
                    case IfInstruction ifInstruction:
                    {
                        // if 的 breakid token 在双分支内活跃（§16.5 推广）
                        var ifTokens = new List<(string, bool)>(tokens)
                            { (ifInstruction.BreakId.Name, false) };
                        var thenFlow = flow?.Clone();
                        var thenExit = AnalyzeBlock(context, ifInstruction.ThenBlock,
                            new HashSet<string>(assigned), ifTokens, stack, errors, reported,
                            collectors, thenFlow);
                        var elseFlow = flow?.Clone();
                        var elseExit = ifInstruction.ElseBlock != null
                            ? AnalyzeBlock(context, ifInstruction.ElseBlock,
                                new HashSet<string>(assigned), ifTokens, stack, errors, reported,
                                collectors, elseFlow)
                            : new HashSet<string>(assigned);
                        // 单臂落尾不可达（hint 消费）：续点只由可达臂出口流入；
                        // 双臂均不可达则续点不可达。无 hint 时两臂恒可达，
                        // 合并与旧版逐位一致
                        if (thenExit == null && elseExit == null)
                        {
                            return null;
                        }
                        if (thenExit == null)
                        {
                            assigned = elseExit!;
                            CopySeqRouteFlow(flow, elseFlow);
                            break;
                        }
                        if (elseExit == null)
                        {
                            assigned = thenExit;
                            CopySeqRouteFlow(flow, thenFlow);
                            break;
                        }
                        // 结构化路径合并：两分支都赋值的变量才算已赋值
                        thenExit.IntersectWith(elseExit);
                        thenFlow?.IntersectWith(elseFlow!);
                        assigned = thenExit;
                        CopySeqRouteFlow(flow, thenFlow);
                        break;
                    }
                    case LoopInstruction loop:
                    {
                        var loopExit = VerifyLoop(context, loop, assigned, tokens, stack,
                            errors, reported, location, collectors, flow);
                        if (loopExit == null)
                        {
                            return null;
                        }
                        assigned = loopExit;
                        break;
                    }
                    case SwitchInstruction switchInstruction:
                    {
                        var caseTokens = new List<(string, bool)>(tokens)
                            { (switchInstruction.BreakId.Name, false) };
                        // 出口合并：switch 恒执行且仅执行一个分支
                        //（default 恒在）——可达分支出口交集即出口态
                        //（与 if 双分支合并同规则、与 §21.8「switch
                        // 全分支交」一致），分支臂内的写入对 switch
                        // 之后的读取可见；落尾不可达的分支（hint 消费）
                        // 不参与交集
                        HashSet<string>? merged = null;
                        SeqRouteFlow? mergedFlow = null;
                        foreach (var itemBlock in switchInstruction.ItemBlocks)
                        {
                            var itemFlow = flow?.Clone();
                            var itemExit = AnalyzeBlock(context, itemBlock,
                                new HashSet<string>(assigned), caseTokens, stack, errors,
                                reported, collectors, itemFlow);
                            if (itemExit == null)
                            {
                                continue;
                            }
                            if (merged == null)
                            {
                                merged = itemExit;
                                mergedFlow = itemFlow;
                            }
                            else
                            {
                                merged.IntersectWith(itemExit);
                                mergedFlow?.IntersectWith(itemFlow!);
                            }
                        }
                        var defaultFlow = flow?.Clone();
                        var defaultExit = AnalyzeBlock(context,
                            switchInstruction.DefaultBlock, new HashSet<string>(assigned),
                            caseTokens, stack, errors, reported, collectors, defaultFlow);
                        if (defaultExit != null)
                        {
                            if (merged == null)
                            {
                                merged = defaultExit;
                                mergedFlow = defaultFlow;
                            }
                            else
                            {
                                merged.IntersectWith(defaultExit);
                                mergedFlow?.IntersectWith(defaultFlow!);
                            }
                        }
                        if (merged == null)
                        {
                            return null;
                        }
                        assigned = merged;
                        CopySeqRouteFlow(flow, mergedFlow);
                        break;
                    }
                    case CallBlockInstruction call:
                    {
                        // §16.1：块落尾返回续 call 的下一条——块内赋值对调用点可见；
                        // call 的 breakid token 在被调块内活跃（§16.5 推广）
                        var callTokens = new List<(string, bool)>(tokens)
                            { (call.BreakId.Name, false) };
                        var callExit = AnalyzeBlock(context, call.Block, assigned, callTokens,
                            stack, errors, reported, collectors, flow);
                        if (callExit == null)
                        {
                            return null;
                        }
                        assigned = callExit;
                        break;
                    }
                    case TryInstruction tryInstruction:
                    {
                        // try 的 breakid token 在 body/各 handler/finally
                        // 内活跃（§16.5 推广；finally 内允许 break tryId）
                        var tryTokens = new List<(string, bool)>(tokens)
                            { (tryInstruction.BreakId.Name, false) };
                        var bodyFlow = flow?.Clone();
                        var bodyExit = AnalyzeBlock(context, tryInstruction.Body,
                            new HashSet<string>(assigned), tryTokens, stack, errors, reported,
                            collectors, bodyFlow);
                        // 正常/捕获路径合并态（body 落尾不可达时不含正常路径）
                        HashSet<string>? merged = bodyExit;
                        SeqRouteFlow? mergedFlow = bodyFlow;
                        if (tryInstruction.CatchTable is BilCatchTableResource catchTable)
                        {
                            foreach (var entry in catchTable.Entries)
                            {
                                // 异常槽在 handler 内视为已赋值（§16.7）
                                var handlerFlow = flow?.Clone();
                                var handlerAssigned = new HashSet<string>(assigned)
                                    { tryInstruction.ExceptionSlot.Name };
                                var handlerExit = AnalyzeBlock(context, entry.Handler,
                                    handlerAssigned, tryTokens, stack, errors, reported,
                                    collectors, handlerFlow);
                                if (handlerExit == null)
                                {
                                    continue;
                                }
                                if (merged == null)
                                {
                                    merged = handlerExit;
                                    mergedFlow = handlerFlow;
                                }
                                else
                                {
                                    merged.IntersectWith(handlerExit);
                                    mergedFlow?.IntersectWith(handlerFlow!);
                                }
                            }
                        }
                        if (tryInstruction.FinallyBlock != null)
                        {
                            // §16.7：try 指令在进 finally 前恒写 EXCEPTION_VAR
                            // （正常路径写 null / 异常路径写当前逃逸异常）——
                            // finally(e) cell 化后体头读 slot 构造 cell 合法。
                            // finally 在正常/异常/逃逸全路径上执行：无正常/捕获
                            // 路径（merged == null）时以 try 进入态分析其内部
                            // 读取（保守——逃逸路径的精确态不沿本合并传播），
                            // 且 finally 出口不改变续点不可达的判定（逃逸
                            // completion 经 finally 后继续向外）
                            var finallyFlow = merged != null ? mergedFlow : flow?.Clone();
                            var finallyAssigned = new HashSet<string>(merged ?? assigned)
                                { tryInstruction.ExceptionSlot.Name };
                            var finallyExit = AnalyzeBlock(context, tryInstruction.FinallyBlock,
                                finallyAssigned, tryTokens, stack, errors, reported, collectors,
                                finallyFlow);
                            if (finallyExit == null || merged == null)
                            {
                                return null;
                            }
                            merged = finallyExit;
                            mergedFlow = finallyFlow;
                        }
                        if (merged == null)
                        {
                            return null;
                        }
                        assigned = merged;
                        CopySeqRouteFlow(flow, mergedFlow);
                        break;
                    }
                }

                // hint 模式的路径常量追踪（route 组号来源，V4）
                if (flow != null)
                {
                    UpdateSeqRouteFlow(instruction, writes, flow, collectors);
                }
                foreach (var variable in writes)
                {
                    assigned.Add(variable.Name);
                }
            }
            return assigned;
        }

        private static HashSet<string>? VerifyLoop(BilFunctionContext context, LoopInstruction loop,
            HashSet<string> assigned, List<(string Name, bool IsLoop)> tokens,
            HashSet<BilBlock> stack, List<BilVerificationError> errors,
            HashSet<(string Location, string Name)> reported, string location,
            List<SeqRouteCollector>? collectors, SeqRouteFlow? flow)
        {
            // condition 变量必须已声明。§21.4：loop condition 在每次读取前
            // 由 judge block 赋值——进入循环时不要求已赋值（judge 在首次
            // 读取前执行，loop/loop.rev 同规则），只要求 judge 块写入它
            if (!context.VariableTypes.ContainsKey(loop.Condition.Name))
            {
                errors.Add(new BilVerificationError("21.2", location,
                    $"loop 条件变量 \"${loop.Condition.Name}\" 未声明"));
            }

            // §21.4：loop condition 每次读取前由 judge block 赋值——judge 块
            // （含嵌套结构）内必须存在对 condition 的写入
            if (!BlockWritesTo(context, loop.Judge, loop.Condition.Name))
            {
                errors.Add(new BilVerificationError("21.4", location,
                    $"loop 的 judge block \"{loop.Judge.Id}\" 未对条件变量 " +
                    $"\"${loop.Condition.Name}\" 赋值"));
            }

            var loopTokens = new List<(string, bool)>(tokens) { (loop.BreakId.Name, true) };
            if (loop.IsRev)
            {
                // §16.4 loop.rev：执行序 body → enum → judge → condition，
                // body 保证至少执行一次——后续块以前块出口态分析（judge 读取
                // body/enum 内赋值不算未赋值）；循环出口取 body 出口态
                // （body 至少一次，其落尾赋值对循环后可见）
                var bodyFlow = flow?.Clone();
                var bodyExit = AnalyzeBlock(context, loop.Body, new HashSet<string>(assigned),
                    loopTokens, stack, errors, reported, collectors, bodyFlow);
                // body 落尾不可达（hint 消费）：body 保证至少执行一次却永不
                // 完成——enum/judge 与循环续点均静态不可达
                if (bodyExit == null)
                {
                    return null;
                }
                var judgeEntry = bodyExit;
                if (loop.EnumBlock != null)
                {
                    var enumExit = AnalyzeBlock(context, loop.EnumBlock,
                        new HashSet<string>(bodyExit), loopTokens, stack, errors, reported,
                        collectors, bodyFlow?.Clone());
                    if (enumExit == null)
                    {
                        return null;
                    }
                    judgeEntry = enumExit;
                }
                AnalyzeBlock(context, loop.Judge, new HashSet<string>(judgeEntry),
                    loopTokens, stack, errors, reported, collectors, bodyFlow?.Clone());
                assigned.Clear();
                assigned.UnionWith(bodyExit);
                CopySeqRouteFlow(flow, bodyFlow);
                return assigned;
            }
            // §16.3 正向 loop：body 可能零次执行——三块都用进入态副本分析，
            // 出口保守保持进入态（子块落尾不可达不改变零次路径的可达性）
            if (loop.EnumBlock != null)
            {
                AnalyzeBlock(context, loop.EnumBlock, new HashSet<string>(assigned),
                    loopTokens, stack, errors, reported, collectors, flow?.Clone());
            }
            AnalyzeBlock(context, loop.Body, new HashSet<string>(assigned),
                loopTokens, stack, errors, reported, collectors, flow?.Clone());
            AnalyzeBlock(context, loop.Judge, new HashSet<string>(assigned),
                loopTokens, stack, errors, reported, collectors, flow?.Clone());
            return assigned;
        }

        // ===== §18.1 rigi.seq-route hint 消费（§21.4 route dispatcher 分组）=====

        // 路径常量追踪（hint 消费模式的路径伴随态）：RouteGroups = 活跃
        // route 局部 → 最后一次常量写入值（前驱边组号来源，V4）；
        // LoadedConsts = 变量 → 最近一次 load 的整数常量值（发射器
        // load → set.var 紧邻形态的轻量前向追踪，非常量/未知即移除条目）
        private sealed class SeqRouteFlow
        {
            public Dictionary<string, int> RouteGroups { get; } =
                new Dictionary<string, int>();
            public Dictionary<string, int> LoadedConsts { get; } =
                new Dictionary<string, int>();

            public SeqRouteFlow Clone()
            {
                var clone = new SeqRouteFlow();
                foreach (var pair in RouteGroups)
                {
                    clone.RouteGroups.Add(pair.Key, pair.Value);
                }
                foreach (var pair in LoadedConsts)
                {
                    clone.LoadedConsts.Add(pair.Key, pair.Value);
                }
                return clone;
            }

            // 多路径合并（与 assigned 同口径取交集）：两侧同键同值的条目
            // 保留，其余删除
            public void IntersectWith(SeqRouteFlow other)
            {
                IntersectEntries(RouteGroups, other.RouteGroups);
                IntersectEntries(LoadedConsts, other.LoadedConsts);
            }

            public void CopyFrom(SeqRouteFlow other)
            {
                RouteGroups.Clear();
                foreach (var pair in other.RouteGroups)
                {
                    RouteGroups.Add(pair.Key, pair.Value);
                }
                LoadedConsts.Clear();
                foreach (var pair in other.LoadedConsts)
                {
                    LoadedConsts.Add(pair.Key, pair.Value);
                }
            }

            private static void IntersectEntries(Dictionary<string, int> entries,
                Dictionary<string, int> other)
            {
                var keys = new List<string>(entries.Keys);
                foreach (var key in keys)
                {
                    if (!other.TryGetValue(key, out var value) || value != entries[key])
                    {
                        entries.Remove(key);
                    }
                }
            }
        }

        // 汇聚前驱边：组号（该边 break 前对 route 的最后一次常量写入值，
        // 未写 = 0 组）+ 该边的独立出口态（assigned 与常量追踪快照）
        private sealed class SeqRouteEdge
        {
            public SeqRouteEdge(int group, HashSet<string> assigned, SeqRouteFlow flow)
            {
                Group = group;
                Assigned = assigned;
                Flow = flow;
            }

            public int Group { get; }
            public HashSet<string> Assigned { get; }
            public SeqRouteFlow Flow { get; }
        }

        // region 收集器：合法 hint 激活；Token = region 指令绑定的 breakid
        // 变量名，Route = hint 声明的 route 局部名；Edges 累积 break 命中
        // Token 的前驱边。收集器栈支持嵌套 region——内层尾链 relay 的
        // break 落进外层收集器（外层 route 的组号随路径追踪自然正确）
        private sealed class SeqRouteCollector
        {
            public SeqRouteCollector(string token, string route)
            {
                Token = token;
                Route = route;
            }

            public string Token { get; }
            public string Route { get; }
            public List<SeqRouteEdge> Edges { get; } = new List<SeqRouteEdge>();
        }

        // hint 消费中途发现不可精确分类的形态（活跃 route 的写入解析不出
        // 常量等）——放弃全部活跃 hint 消费，由最外层消费帧捕获后退回
        // 保守重分析（诊断经 reported 去重，行为同无 hint）
        private sealed class SeqRouteBailException : Exception
        {
        }

        // 尾链链节（V3 结构校验产物）：load 整数常量 → cmp.eq route 常量 →
        // if（条件 = cmp 结果，then = relay 目标块，else = 链续块|none）
        private sealed class SeqRouteLink
        {
            public int Constant { get; set; }
            public string LoadTarget { get; set; } = "";
            public string CmpTarget { get; set; } = "";
            public IfInstruction If { get; set; } = null!;
            public BilBlock Relay { get; set; } = null!;
            public BilBlock Container { get; set; } = null!;
            public int IfIndex { get; set; }
        }

        // flow 就地替换为 source 的内容（assigned 重指向时的伴随迁移；
        // 非 hint 模式两侧恒 null）
        private static void CopySeqRouteFlow(SeqRouteFlow? flow, SeqRouteFlow? source)
        {
            if (flow != null && source != null)
            {
                flow.CopyFrom(source);
            }
        }

        // break 命中活跃收集器的 region token → 记录汇聚前驱边并返回 true
        //（组号 = 该路径对 route 的最后一次常量写入值，未写 = 0 组，V4）
        private static bool TryRecordSeqRouteEdge(List<SeqRouteCollector>? collectors,
            string token, HashSet<string> assigned, SeqRouteFlow? flow)
        {
            if (collectors == null)
            {
                return false;
            }
            foreach (var collector in collectors)
            {
                if (collector.Token == token)
                {
                    var group = flow != null
                        && flow.RouteGroups.TryGetValue(collector.Route, out var value)
                        ? value : 0;
                    collector.Edges.Add(new SeqRouteEdge(group, new HashSet<string>(assigned),
                        flow?.Clone() ?? new SeqRouteFlow()));
                    return true;
                }
            }
            return false;
        }

        // 路径常量追踪的线性更新（主循环在写入生效前调用；仅 hint 模式）。
        // 活跃 route 局部的写入必须能解析出常量（V1 已保证「load 整数常量
        // → set.var」紧邻形态；追踪丢失 = 不可精确分类 → 放弃 hint 消费）
        private static void UpdateSeqRouteFlow(BilInstruction instruction,
            List<BilVariableOperand> writes, SeqRouteFlow flow,
            List<SeqRouteCollector>? collectors)
        {
            if (instruction is LoadInstruction load)
            {
                if (TryGetIntConstant(load.Resource, out var constant))
                {
                    flow.LoadedConsts[load.Target.Name] = constant;
                }
                else
                {
                    flow.LoadedConsts.Remove(load.Target.Name);
                }
                return;
            }
            if (instruction is SetVarInstruction setVar)
            {
                if (IsActiveSeqRoute(setVar.Target.Name, collectors))
                {
                    if (!flow.LoadedConsts.TryGetValue(setVar.Source.Name, out var tag))
                    {
                        throw new SeqRouteBailException();
                    }
                    flow.RouteGroups[setVar.Target.Name] = tag;
                }
                if (flow.LoadedConsts.TryGetValue(setVar.Source.Name, out var propagated))
                {
                    flow.LoadedConsts[setVar.Target.Name] = propagated;
                }
                else
                {
                    flow.LoadedConsts.Remove(setVar.Target.Name);
                }
                return;
            }
            var routeWritten = false;
            foreach (var write in writes)
            {
                flow.LoadedConsts.Remove(write.Name);
                routeWritten |= IsActiveSeqRoute(write.Name, collectors);
            }
            if (routeWritten)
            {
                throw new SeqRouteBailException();
            }
        }

        private static bool IsActiveSeqRoute(string variable,
            List<SeqRouteCollector>? collectors)
        {
            if (collectors == null)
            {
                return false;
            }
            foreach (var collector in collectors)
            {
                if (collector.Route == variable)
                {
                    return true;
                }
            }
            return false;
        }

        // 整数标量资源 → int 常量值（route tag 与链节比较常量共用；i32
        // 之外的超宽形态与不可解析文本保守返回 false——hint 忽略口径）
        private static bool TryGetIntConstant(BilResource resource, out int value)
        {
            value = 0;
            return resource is BilScalarResource scalar
                && scalar.Type is BilScalarType.I8 or BilScalarType.I16 or BilScalarType.I32
                    or BilScalarType.U8 or BilScalarType.U16
                && int.TryParse(scalar.LiteralText, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value);
        }

        // V0：JSON 合法；kind/version 匹配；route 是本 fn 已声明 .i32 局部
        private static bool TryParseSeqRouteHint(BilFunctionContext context,
            HintInstruction hint, out string route)
        {
            route = "";
            if (hint.Resource is not BilScalarResource scalar
                || scalar.Type != BilScalarType.String
                || !TryDecodeSeqRouteString(scalar.LiteralText, out var json))
            {
                return false;
            }
            string? routeName;
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("kind", out var kind)
                    || kind.ValueKind != JsonValueKind.String
                    || kind.GetString() != "rigi.seq-route"
                    || !root.TryGetProperty("version", out var version)
                    || version.ValueKind != JsonValueKind.Number
                    || !version.TryGetInt32(out var versionNumber)
                    || versionNumber != 1
                    || !root.TryGetProperty("route", out var routeProperty)
                    || routeProperty.ValueKind != JsonValueKind.String)
                {
                    return false;
                }
                routeName = routeProperty.GetString();
            }
            catch (JsonException)
            {
                return false;
            }
            if (routeName == null || !routeName.StartsWith("$"))
            {
                return false;
            }
            routeName = routeName.Substring(1);
            if (!context.VariableTypes.TryGetValue(routeName, out var type)
                || type != ".i32")
            {
                return false;
            }
            route = routeName;
            return true;
        }

        // BIL 字符串字面量原文（含引号）解码——转义表与发射端 Escape 同集
        // 的逆映射；畸形（缺引号/截断/未知转义）返回 false（hint 忽略口径）
        private static bool TryDecodeSeqRouteString(string literalText, out string value)
        {
            value = "";
            if (literalText.Length < 2 || literalText[0] != '"'
                || literalText[literalText.Length - 1] != '"')
            {
                return false;
            }
            var sb = new StringBuilder();
            for (var i = 1; i < literalText.Length - 1; i++)
            {
                var c = literalText[i];
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (i + 1 >= literalText.Length - 1)
                {
                    return false;
                }
                i++;
                char decoded;
                switch (literalText[i])
                {
                    case '\\': decoded = '\\'; break;
                    case '"': decoded = '"'; break;
                    case '\'': decoded = '\''; break;
                    case '$': decoded = '$'; break;
                    case 'a': decoded = '\a'; break;
                    case 'b': decoded = '\b'; break;
                    case 't': decoded = '\t'; break;
                    case 'n': decoded = '\n'; break;
                    case 'v': decoded = '\v'; break;
                    case 'f': decoded = '\f'; break;
                    case 'r': decoded = '\r'; break;
                    default: return false;
                }
                sb.Append(decoded);
            }
            value = sb.ToString();
            return true;
        }

        // V1：fn 内对 route 的每次写入都是常量写入——发射器形态为
        //「load res(整数常量) $t → set.var $t $route」同块紧邻；据此
        // 收集全部写入常量。其他任何写 route 的指令形态 → false
        private static bool TryCollectRouteWriteConstants(BilFunctionContext context,
            string route, out HashSet<int> constants)
        {
            constants = new HashSet<int>();
            var reads = new List<BilVariableOperand>();
            var writes = new List<BilVariableOperand>();
            foreach (var fnBlock in context.Function.Blocks)
            {
                var instructions = fnBlock.Instructions;
                for (var i = 0; i < instructions.Count; i++)
                {
                    var instruction = instructions[i];
                    if (instruction is SetVarInstruction setVar && setVar.Target.Name == route)
                    {
                        if (i == 0
                            || instructions[i - 1] is not LoadInstruction load
                            || load.Target.Name != setVar.Source.Name
                            || !TryGetIntConstant(load.Resource, out var constant))
                        {
                            return false;
                        }
                        constants.Add(constant);
                        continue;
                    }
                    reads.Clear();
                    writes.Clear();
                    ClassifyVariables(instruction, reads, writes);
                    foreach (var write in writes)
                    {
                        if (write.Name == route)
                        {
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        // V3：hint 之后是「load 常量 → cmp.eq route → if 跳转」链——非末
        // 链节的 if 必须是所在块末指令、链续在 else 块内（自 else 块首
        // 指令起递归同形）；末链节 else 为 none，其后的块内续指令即
        // 0 组落尾续点。链上每个比较常量与 V1 收集的非零写入常量一一对应
        private static bool TryParseSeqRouteChain(BilFunctionContext context,
            BilBlock startBlock, int startIndex, string route, HashSet<int> writeConstants,
            out List<SeqRouteLink> links)
        {
            links = new List<SeqRouteLink>();
            var compared = new HashSet<int>();
            var visited = new HashSet<BilBlock>(ReferenceEqualityComparer.Instance)
                { startBlock };
            var container = startBlock;
            var index = startIndex;
            while (true)
            {
                var instructions = container.Instructions;
                if (index + 2 >= instructions.Count
                    || instructions[index] is not LoadInstruction load
                    || !TryGetIntConstant(load.Resource, out var constant)
                    || instructions[index + 1] is not BinaryIntrinsicInstruction compare
                    || compare.Op != BilBinaryOp.CmpEq
                    || compare.Left.Name != route
                    || compare.Right.Name != load.Target.Name
                    || instructions[index + 2] is not IfInstruction linkIf
                    || linkIf.Condition.Name != compare.Target.Name
                    || !context.BlockSet.Contains(linkIf.ThenBlock))
                {
                    break;   // 链节模式结束（零链节亦合法——见末尾对应检查）
                }
                if (!compared.Add(constant))
                {
                    return false;   // 重复比较常量：与写入值无一一对应
                }
                links.Add(new SeqRouteLink
                {
                    Constant = constant,
                    LoadTarget = load.Target.Name,
                    CmpTarget = compare.Target.Name,
                    If = linkIf,
                    Relay = linkIf.ThenBlock,
                    Container = container,
                    IfIndex = index + 2,
                });
                if (linkIf.ElseBlock == null)
                {
                    break;   // 末链节
                }
                // 链续：本链节 if 必须是块末；else 块属于本 fn、未成环、
                // 且自下一条链节起
                if (index + 2 != instructions.Count - 1
                    || !context.BlockSet.Contains(linkIf.ElseBlock)
                    || !visited.Add(linkIf.ElseBlock))
                {
                    return false;
                }
                container = linkIf.ElseBlock;
                index = 0;
            }
            var nonzeroWrites = new HashSet<int>(writeConstants);
            nonzeroWrites.Remove(0);
            return compared.SetEquals(nonzeroWrites);
        }

        // hint 校验与消费（§18.1 V0–V4 + §21.4 分组）。返回 true = hint
        // 合法且已消费：resumed/resumedFlow/resumeIndex 给出续点状态与
        // 位置（resumed = null 表示 0 组为空、落尾边静态不可达）；任一
        // 校验失败或消费中发现不可精确分类的形态 → false（忽略 hint，
        // 调用点按保守全合并重走 region 指令，行为同无 hint）
        private static bool TryConsumeSeqRouteHint(BilFunctionContext context, BilBlock block,
            int regionIndex, BilInstruction regionInstruction, HintInstruction hint,
            HashSet<string> assigned, List<(string Name, bool IsLoop)> tokens,
            HashSet<BilBlock> stack, List<BilVerificationError> errors,
            HashSet<(string Location, string Name)> reported,
            List<SeqRouteCollector>? collectors, SeqRouteFlow? flow,
            out HashSet<string>? resumed, out SeqRouteFlow? resumedFlow, out int resumeIndex)
        {
            resumed = null;
            resumedFlow = null;
            resumeIndex = 0;
            // V0：JSON 合法；kind/version 匹配；route 是本 fn 已声明 .i32 局部
            if (!TryParseSeqRouteHint(context, hint, out var route))
            {
                return false;
            }
            // V1：fn 内对 route 的每次写入都是常量写入；据此收集写入常量
            if (!TryCollectRouteWriteConstants(context, route, out var writeConstants))
            {
                return false;
            }
            // V3：尾链结构 + 比较常量 ↔ V1 非零写入常量一一对应
            if (!TryParseSeqRouteChain(context, block, regionIndex + 2, route,
                    writeConstants, out var links))
            {
                return false;
            }
            // V2 由调用点的前置模式匹配承载（hint 前一条 = if/switch/call
            // blk，其 break token 的着陆点即 hint 位置——结构化语义保证）
            var token = regionInstruction switch
            {
                IfInstruction ifInstruction => ifInstruction.BreakId.Name,
                SwitchInstruction switchInstruction => switchInstruction.BreakId.Name,
                CallBlockInstruction call => call.BreakId.Name,
                _ => "",
            };
            var collector = new SeqRouteCollector(token, route);
            var subCollectors = collectors == null
                ? new List<SeqRouteCollector> { collector }
                : new List<SeqRouteCollector>(collectors) { collector };
            var entryFlow = flow?.Clone() ?? new SeqRouteFlow();
            try
            {
                // V4：逐边分析 region 子结构——每条前驱边标注组号（边
                // break 前对 route 的最后一次常量写入值，未写 = 0）
                CollectSeqRouteEdges(context, regionInstruction, assigned, tokens, stack,
                    errors, reported, subCollectors, entryFlow, collector, route);

                // 尾链逐条剥离：每条链节把本组边从流中剥离、合并态（交集）
                // 送入 relay 目标块（精确喂养——逃逸臂上写入的外层结果局部
                // 沿 relay 链传播；relay 出口是逃逸边，不回续点）；本组为
                // 空则 relay 静态不可达，不送状态
                var rest = new List<SeqRouteEdge>(collector.Edges);
                foreach (var link in links)
                {
                    var groupEdges = new List<SeqRouteEdge>();
                    var remaining = new List<SeqRouteEdge>(rest.Count);
                    foreach (var edge in rest)
                    {
                        if (edge.Group == link.Constant)
                        {
                            groupEdges.Add(edge);
                        }
                        else
                        {
                            remaining.Add(edge);
                        }
                    }
                    rest = remaining;
                    if (groupEdges.Count == 0)
                    {
                        continue;
                    }
                    var relayTokens = new List<(string, bool)>(tokens)
                        { (link.If.BreakId.Name, false) };
                    AnalyzeBlock(context, link.Relay, MergeSeqRouteAssigned(groupEdges),
                        relayTokens, stack, errors, reported, collectors,
                        MergeSeqRouteFlows(groupEdges));
                }

                // 块尾落尾（region 正常结束、回 call blk 续点）仅由 0 组边
                // 的合并态流入
                var zeroEdges = new List<SeqRouteEdge>(rest.Count);
                foreach (var edge in rest)
                {
                    if (edge.Group != 0)
                    {
                        return false;   // 非常量组无链节承接（V3 已保证，防御）
                    }
                    zeroEdges.Add(edge);
                }
                if (zeroEdges.Count == 0)
                {
                    return true;   // resumed = null：0 组为空，落尾边静态不可达
                }
                var zeroAssigned = MergeSeqRouteAssigned(zeroEdges);
                var zeroFlow = MergeSeqRouteFlows(zeroEdges);
                // 链节写入的 load/cmp 临时量在 0 路径上均已执行（链节随
                // 剥离跳过主循环，此处补齐其写入的 DA 可见性）
                foreach (var link in links)
                {
                    zeroAssigned.Add(link.LoadTarget);
                    zeroAssigned.Add(link.CmpTarget);
                }
                if (links.Count == 0)
                {
                    // 零链节（V3：无非常量写入）：hint 是纯位置标记——
                    // 全边合并即续点（与保守合并等价），仅跳过 hint 自身
                    resumed = zeroAssigned;
                    resumedFlow = zeroFlow;
                    resumeIndex = regionIndex + 1;
                    return true;
                }
                var lastLink = links[links.Count - 1];
                if (links.Count == 1)
                {
                    // 单链节链：续点就在本块链节 if 之后
                    resumed = zeroAssigned;
                    resumedFlow = zeroFlow;
                    resumeIndex = lastLink.IfIndex;
                    return true;
                }
                // 多链节链：本块链节 if 必为块末（V3），落尾续点在末链节
                // 所在块——其出口经嵌套 else 即为本块出口
                resumed = AnalyzeSeqRouteContinuation(context, lastLink.Container,
                    lastLink.IfIndex + 1, zeroAssigned, tokens, stack, errors, reported,
                    collectors, zeroFlow);
                resumedFlow = zeroFlow;
                resumeIndex = block.Instructions.Count - 1;
                return true;
            }
            catch (SeqRouteBailException) when (collectors == null || collectors.Count == 0)
            {
                // 最外层消费帧兜底：不可精确分类 → 忽略 hint 退回保守
                return false;
            }
        }

        // region 子结构逐边分析（V4 边分类）：每个子块以进入态副本独立
        // 分析，非 null 落尾出口 = 一条落尾前驱边（组号 = 该路径对 route
        // 的最后一次常量写入，未写 = 0）；break region token 的边由主
        // 循环在收集器活跃时自动记录进 collector
        private static void CollectSeqRouteEdges(BilFunctionContext context,
            BilInstruction regionInstruction, HashSet<string> assigned,
            List<(string Name, bool IsLoop)> tokens, HashSet<BilBlock> stack,
            List<BilVerificationError> errors,
            HashSet<(string Location, string Name)> reported,
            List<SeqRouteCollector> subCollectors, SeqRouteFlow entryFlow,
            SeqRouteCollector collector, string route)
        {
            switch (regionInstruction)
            {
                case IfInstruction ifInstruction:
                {
                    var ifTokens = new List<(string, bool)>(tokens)
                        { (ifInstruction.BreakId.Name, false) };
                    var thenFlow = entryFlow.Clone();
                    var thenExit = AnalyzeBlock(context, ifInstruction.ThenBlock,
                        new HashSet<string>(assigned), ifTokens, stack, errors, reported,
                        subCollectors, thenFlow);
                    if (thenExit != null)
                    {
                        collector.Edges.Add(new SeqRouteEdge(
                            SeqRouteGroupOf(thenFlow, route), thenExit, thenFlow));
                    }
                    var elseFlow = entryFlow.Clone();
                    var elseExit = ifInstruction.ElseBlock != null
                        ? AnalyzeBlock(context, ifInstruction.ElseBlock,
                            new HashSet<string>(assigned), ifTokens, stack, errors, reported,
                            subCollectors, elseFlow)
                        : new HashSet<string>(assigned);
                    if (elseExit != null)
                    {
                        collector.Edges.Add(new SeqRouteEdge(
                            SeqRouteGroupOf(elseFlow, route), elseExit, elseFlow));
                    }
                    break;
                }
                case SwitchInstruction switchInstruction:
                {
                    var caseTokens = new List<(string, bool)>(tokens)
                        { (switchInstruction.BreakId.Name, false) };
                    foreach (var itemBlock in switchInstruction.ItemBlocks)
                    {
                        var itemFlow = entryFlow.Clone();
                        var itemExit = AnalyzeBlock(context, itemBlock,
                            new HashSet<string>(assigned), caseTokens, stack, errors, reported,
                            subCollectors, itemFlow);
                        if (itemExit != null)
                        {
                            collector.Edges.Add(new SeqRouteEdge(
                                SeqRouteGroupOf(itemFlow, route), itemExit, itemFlow));
                        }
                    }
                    var defaultFlow = entryFlow.Clone();
                    var defaultExit = AnalyzeBlock(context, switchInstruction.DefaultBlock,
                        new HashSet<string>(assigned), caseTokens, stack, errors, reported,
                        subCollectors, defaultFlow);
                    if (defaultExit != null)
                    {
                        collector.Edges.Add(new SeqRouteEdge(
                            SeqRouteGroupOf(defaultFlow, route), defaultExit, defaultFlow));
                    }
                    break;
                }
                case CallBlockInstruction call:
                {
                    var callTokens = new List<(string, bool)>(tokens)
                        { (call.BreakId.Name, false) };
                    // 单一路径：进入态就地演进即可
                    var exit = AnalyzeBlock(context, call.Block, assigned, callTokens, stack,
                        errors, reported, subCollectors, entryFlow);
                    if (exit != null)
                    {
                        collector.Edges.Add(new SeqRouteEdge(
                            SeqRouteGroupOf(entryFlow, route), exit, entryFlow));
                    }
                    break;
                }
            }
        }

        // 路径对 route 的最后一次常量写入值（未写 = 0 组）
        private static int SeqRouteGroupOf(SeqRouteFlow flow, string route)
        {
            return flow.RouteGroups.TryGetValue(route, out var group) ? group : 0;
        }

        // 同组边的合并态（交集口径，与 if/switch 分支合并同规则）
        private static HashSet<string> MergeSeqRouteAssigned(List<SeqRouteEdge> edges)
        {
            var merged = new HashSet<string>(edges[0].Assigned);
            for (var i = 1; i < edges.Count; i++)
            {
                merged.IntersectWith(edges[i].Assigned);
            }
            return merged;
        }

        private static SeqRouteFlow MergeSeqRouteFlows(List<SeqRouteEdge> edges)
        {
            var merged = edges[0].Flow.Clone();
            for (var i = 1; i < edges.Count; i++)
            {
                merged.IntersectWith(edges[i].Flow);
            }
            return merged;
        }

        // hint 消费的链尾续点分析（末链节所在块自链节 if 之后继续；该块
        // 随尾链剥离首次进入分析，环守卫与 AnalyzeBlock 同口径）
        private static HashSet<string>? AnalyzeSeqRouteContinuation(BilFunctionContext context,
            BilBlock block, int startIndex, HashSet<string> assigned,
            List<(string Name, bool IsLoop)> tokens, HashSet<BilBlock> stack,
            List<BilVerificationError> errors,
            HashSet<(string Location, string Name)> reported,
            List<SeqRouteCollector>? collectors, SeqRouteFlow? flow)
        {
            if (!stack.Add(block))
            {
                errors.Add(new BilVerificationError("21.5", context.Function.Symbol,
                    $"结构块引用成环（含 call blk 直接结构递归）：\"{block.Id}\""));
                return new HashSet<string>(assigned);
            }
            try
            {
                return AnalyzeBlockInstructions(context, block, startIndex, assigned, tokens,
                    stack, errors, reported, collectors, flow);
            }
            finally
            {
                stack.Remove(block);
            }
        }

        // break/continue 的 token：必须声明为 .breakid 变量，且在活跃结构
        // 作用域内（§21.5）；continue 不得引用非 loop token（§16.5——
        // switch/if/call/try 的 breakid 均 AllowsContinue=false）
        private static void VerifyBreakToken(BilFunctionContext context,
            BilVariableOperand token, List<(string Name, bool IsLoop)> tokens,
            bool isContinue, string location, List<BilVerificationError> errors)
        {
            if (!context.VariableTypes.ContainsKey(token.Name))
            {
                errors.Add(new BilVerificationError("21.2", location,
                    $"break/continue 的 token \"${token.Name}\" 未声明"));
                return;
            }
            if (!context.BreakIdVariables.Contains(token.Name))
            {
                errors.Add(new BilVerificationError("21.6", location,
                    $"break/continue 的 token \"${token.Name}\" 必须是 .breakid 类型变量" +
                    $"（声明类型为 \"{context.VariableTypes[token.Name]}\"）"));
                return;
            }
            foreach (var (name, isLoop) in tokens)
            {
                if (name == token.Name)
                {
                    if (isContinue && !isLoop)
                    {
                        errors.Add(new BilVerificationError("21.5", location,
                            $"continue 不得引用非 loop 的 breakid \"${token.Name}\""));
                    }
                    return;
                }
            }
            errors.Add(new BilVerificationError("21.5", location,
                $"{(isContinue ? "continue" : "break")} 的 token \"${token.Name}\" " +
                "不在当前活跃结构作用域内"));
        }

        // 块（含嵌套结构，防环）内是否存在对指定变量的写入
        private static bool BlockWritesTo(BilFunctionContext context, BilBlock start, string variableName)
        {
            var visited = new HashSet<BilBlock>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<BilBlock>();
            var reads = new List<BilVariableOperand>();
            var writes = new List<BilVariableOperand>();
            pending.Push(start);
            while (pending.Count > 0)
            {
                var block = pending.Pop();
                if (!visited.Add(block) || !context.BlockSet.Contains(block))
                {
                    continue;
                }
                foreach (var instruction in block.Instructions)
                {
                    reads.Clear();
                    writes.Clear();
                    ClassifyVariables(instruction, reads, writes);
                    foreach (var variable in writes)
                    {
                        if (variable.Name == variableName)
                        {
                            return true;
                        }
                    }
                    foreach (var referenced in ReferencedBlocks(instruction))
                    {
                        pending.Push(referenced);
                    }
                }
            }
            return false;
        }

        // fn 内全部指令枚举（成员资格过滤下钻：越权块不展开，其内部错误
        // 不级联；环只展开一次）
        private static IEnumerable<(BilBlock, BilInstruction)> InstructionsInFunction(
            BilFunctionContext context)
        {
            var visited = new HashSet<BilBlock>(ReferenceEqualityComparer.Instance);
            foreach (var block in context.Function.Blocks)
            {
                foreach (var item in EnumerateOwnBlocks(context, block, visited))
                {
                    yield return item;
                }
            }
        }

        private static IEnumerable<(BilBlock, BilInstruction)> EnumerateOwnBlocks(
            BilFunctionContext context, BilBlock block, HashSet<BilBlock> visited)
        {
            if (!visited.Add(block))
            {
                yield break;
            }
            foreach (var instruction in block.Instructions)
            {
                yield return (block, instruction);
                foreach (var referenced in ReferencedBlocks(instruction))
                {
                    if (context.BlockSet.Contains(referenced))
                    {
                        foreach (var item in EnumerateOwnBlocks(context, referenced, visited))
                        {
                            yield return item;
                        }
                    }
                }
            }
        }

        // ===== §21.8 enum struct 实例字段：init 全路径 set.field（§14.3 无零值）=====
        // 检查对象：宿主类型（含 extends 链）的实例字段，类型为 enum struct
        // （精确名或构造形态 EnumType\<...\>，经 DeclarationKeyOf 元数反查）。
        // 含 .generic< 的 typeid 位置表达式无法静态判定，降级跳过（防误报）。
        // 只认对 $.this 的 set.field / 同路径 getid.field 可解的 set.field.indirect；
        // 别名与 invoke 把 .this 传出后的间接读不追踪（frontend 保证不变量，
        // 本规则是安全网）。静态字段 / .vars / 数组元素不在本规则。
        // 路径合并：if 双分支交集；正向 loop body 可能零次（体内 set 不计入
        // 出口）；loop.rev 取 body 出口（至少一次）；switch 全分支（含 default）
        // 交集——与 DA「switch 出口保持进入态」不同，本规则要证明全路径已写；
        // try/catch 交再叠 finally（与 DA 同）。有 finally 时体内 ret/throw
        // 延后到 finally 之后才记离体（finallyDepth）；Exited 阻止「体内 ret
        // 后仍把 try 后续 set 算进该路径」。
        // ===== §21.8 enum struct 实例字段：init 全路径 set.field（§14.3 无零值）=====
        // 检查对象：宿主类型（含 extends 链）的实例字段，类型为 enum struct
        // （精确名或构造形态 EnumType<...>，经 DeclarationKeyOf 元数反查）。
        // 含 .generic< 的 typeid 位置表达式无法静态判定，降级跳过（防误报）。
        // 只认对 $.this 的 set.field / 可解析的 set.field.indirect；别名与
        // invoke 把 .this 传出后的间接读不追踪（frontend 保证不变量，本规则
        // 是安全网）。静态字段 / .vars / 数组元素不在本规则。
        // 路径合并：if 双分支交集；正向 loop body 可能零次（体内 set 不计入
        // 出口）；loop.rev 取 body 出口（至少一次）；switch 全分支（含 default）
        // 交集——与 DA「switch 出口保持进入态」不同，本规则要证明全路径已写；
        // try/catch/finally 按 DA 的 completion 合并；ret/throw 在无待执行
        // finally 时记为完成路径；体内 ret 仍经 finally（finallyDepth）。
        // ..init.wrapper 不是构造期字段写入点。
        private static void VerifyEnumStructInstanceFields(BilFunctionContext context,
            BilBlock entryBlock, List<BilVerificationError> errors)
        {
            if (!IsInitFunction(context)
                || MethodNameSegment(context.Function.Symbol) == BilSpellings.InitWrapperMethodName)
            {
                return;
            }
            if (!BilVerificationContext.TryParseMethodSymbol(context.Function.Symbol,
                    out var owner, out var isStatic, out _, out _)
                || isStatic || owner.Length == 0 || owner.EndsWith("::"))
            {
                return;
            }
            var fieldSymbols = context.Module.CollectEnumStructInstanceFields(owner);
            if (fieldSymbols.Count == 0)
            {
                return;
            }
            var tracked = new HashSet<string>(fieldSymbols);
            var state = AnalyzeEnumFieldBlock(context, entryBlock, new EnumFieldInitState(),
                tracked, new HashSet<BilBlock>(ReferenceEqualityComparer.Instance), 0);
            var initSymbol = context.Function.Symbol;
            foreach (var fieldSymbol in fieldSymbols)
            {
                var fieldName = InstanceFieldName(fieldSymbol);
                if (state.EarlyRead.Contains(fieldSymbol))
                {
                    errors.Add(new BilVerificationError("21.8", initSymbol,
                        $"§21.8：类型 \"{owner}\" 的 enum struct 实例字段 \"{fieldName}\" " +
                        $"在 init \"{initSymbol}\" 中于 set.field 之前被 get.field 读取" +
                        "（§14.3「enum 无零值」）"));
                }
                if (state.MissingOnExit.Contains(fieldSymbol)
                    || !state.Assigned.Contains(fieldSymbol))
                {
                    errors.Add(new BilVerificationError("21.8", initSymbol,
                        $"§21.8：类型 \"{owner}\" 的 enum struct 实例字段 \"{fieldName}\" " +
                        $"未在 init \"{initSymbol}\" 的全部执行路径上 set.field" +
                        "（§14.3「enum 无零值」）"));
                }
            }
        }

        private static string InstanceFieldName(string fieldSymbol)
        {
            var hash = fieldSymbol.IndexOf('#');
            var at = fieldSymbol.LastIndexOf('@');
            if (hash < 0 || at <= hash)
            {
                return fieldSymbol;
            }
            var name = fieldSymbol.Substring(hash + 1, at - hash - 1);
            return name.StartsWith(".static.") ? name.Substring(".static.".Length) : name;
        }

        private sealed class EnumFieldInitState
        {
            public HashSet<string> Assigned { get; } = new HashSet<string>();
            public HashSet<string> EarlyRead { get; } = new HashSet<string>();
            public HashSet<string> MissingOnExit { get; } = new HashSet<string>();
            public Dictionary<string, string> FieldIdOf { get; } = new Dictionary<string, string>();

            public EnumFieldInitState Clone()
            {
                var copy = new EnumFieldInitState();
                copy.Assigned.UnionWith(Assigned);
                copy.EarlyRead.UnionWith(EarlyRead);
                copy.MissingOnExit.UnionWith(MissingOnExit);
                foreach (var pair in FieldIdOf)
                {
                    copy.FieldIdOf[pair.Key] = pair.Value;
                }
                return copy;
            }

            public void CopyFrom(EnumFieldInitState other)
            {
                if (ReferenceEquals(this, other))
                {
                    return;
                }
                Assigned.Clear();
                Assigned.UnionWith(other.Assigned);
                EarlyRead.Clear();
                EarlyRead.UnionWith(other.EarlyRead);
                MissingOnExit.Clear();
                MissingOnExit.UnionWith(other.MissingOnExit);
                FieldIdOf.Clear();
                foreach (var pair in other.FieldIdOf)
                {
                    FieldIdOf[pair.Key] = pair.Value;
                }
            }

            public void IntersectAssignWith(EnumFieldInitState other)
            {
                Assigned.IntersectWith(other.Assigned);
                EarlyRead.UnionWith(other.EarlyRead);
                MissingOnExit.UnionWith(other.MissingOnExit);
                var keys = new List<string>(FieldIdOf.Keys);
                foreach (var key in keys)
                {
                    if (!other.FieldIdOf.TryGetValue(key, out var bound) || bound != FieldIdOf[key])
                    {
                        FieldIdOf.Remove(key);
                    }
                }
            }
        }

        private static EnumFieldInitState AnalyzeEnumFieldBlock(BilFunctionContext context,
            BilBlock block, EnumFieldInitState state, HashSet<string> tracked,
            HashSet<BilBlock> stack, int finallyDepth)
        {
            if (!context.BlockSet.Contains(block) || !stack.Add(block))
            {
                return state;
            }
            try
            {
                var reads = new List<BilVariableOperand>();
                var writes = new List<BilVariableOperand>();
                foreach (var instruction in block.Instructions)
                {
                    NoteEnumFieldAccess(instruction, state, tracked);
                    var terminates = instruction is RetInstruction or ThrowInstruction;
                    if (terminates && finallyDepth == 0)
                    {
                        foreach (var field in tracked)
                        {
                            if (!state.Assigned.Contains(field))
                            {
                                state.MissingOnExit.Add(field);
                            }
                        }
                    }
                    switch (instruction)
                    {
                        case IfInstruction ifInstruction:
                        {
                            var thenExit = AnalyzeEnumFieldBlock(context, ifInstruction.ThenBlock,
                                state.Clone(), tracked, stack, finallyDepth);
                            var elseExit = ifInstruction.ElseBlock != null
                                ? AnalyzeEnumFieldBlock(context, ifInstruction.ElseBlock,
                                    state.Clone(), tracked, stack, finallyDepth)
                                : state.Clone();
                            thenExit.IntersectAssignWith(elseExit);
                            state.CopyFrom(thenExit);
                            break;
                        }
                        case LoopInstruction loop:
                            AnalyzeEnumFieldLoop(context, loop, state, tracked, stack, finallyDepth);
                            break;
                        case SwitchInstruction switchInstruction:
                        {
                            EnumFieldInitState? merged = null;
                            foreach (var itemBlock in switchInstruction.ItemBlocks)
                            {
                                var itemExit = AnalyzeEnumFieldBlock(context, itemBlock,
                                    state.Clone(), tracked, stack, finallyDepth);
                                if (merged == null)
                                {
                                    merged = itemExit;
                                }
                                else
                                {
                                    merged.IntersectAssignWith(itemExit);
                                }
                            }
                            var defaultExit = AnalyzeEnumFieldBlock(context,
                                switchInstruction.DefaultBlock, state.Clone(), tracked, stack,
                                finallyDepth);
                            if (merged == null)
                            {
                                merged = defaultExit;
                            }
                            else
                            {
                                merged.IntersectAssignWith(defaultExit);
                            }
                            state.CopyFrom(merged);
                            break;
                        }
                        case CallBlockInstruction call:
                            AnalyzeEnumFieldBlock(context, call.Block, state, tracked, stack,
                                finallyDepth);
                            break;
                        case TryInstruction tryInstruction:
                            AnalyzeEnumFieldTry(context, tryInstruction, state, tracked, stack,
                                finallyDepth);
                            break;
                    }
                    if (terminates)
                    {
                        break;
                    }
                    reads.Clear();
                    writes.Clear();
                    ClassifyVariables(instruction, reads, writes);
                    string? copiedFieldId = null;
                    string? copyTarget = null;
                    if (instruction is SetVarInstruction setVar)
                    {
                        copyTarget = setVar.Target.Name;
                        state.FieldIdOf.TryGetValue(setVar.Source.Name, out copiedFieldId);
                    }
                    else if (instruction is GetVarInstruction getVar)
                    {
                        copyTarget = getVar.Target.Name;
                        state.FieldIdOf.TryGetValue(getVar.Source.Name, out copiedFieldId);
                    }
                    foreach (var write in writes)
                    {
                        state.FieldIdOf.Remove(write.Name);
                    }
                    if (instruction is GetIdFieldInstruction getIdField)
                    {
                        state.FieldIdOf[getIdField.Target.Name] = getIdField.Field.Symbol;
                    }
                    else if (copyTarget != null && copiedFieldId != null)
                    {
                        state.FieldIdOf[copyTarget] = copiedFieldId;
                    }
                }
                return state;
            }
            finally
            {
                stack.Remove(block);
            }
        }

        private static void AnalyzeEnumFieldLoop(BilFunctionContext context, LoopInstruction loop,
            EnumFieldInitState state, HashSet<string> tracked, HashSet<BilBlock> stack,
            int finallyDepth)
        {
            if (loop.IsRev)
            {
                var bodyExit = AnalyzeEnumFieldBlock(context, loop.Body, state.Clone(),
                    tracked, stack, finallyDepth);
                var afterBody = bodyExit;
                if (loop.EnumBlock != null)
                {
                    afterBody = AnalyzeEnumFieldBlock(context, loop.EnumBlock, bodyExit.Clone(),
                        tracked, stack, finallyDepth);
                    bodyExit.EarlyRead.UnionWith(afterBody.EarlyRead);
                    bodyExit.MissingOnExit.UnionWith(afterBody.MissingOnExit);
                }
                var judgeExit = AnalyzeEnumFieldBlock(context, loop.Judge, afterBody.Clone(),
                    tracked, stack, finallyDepth);
                bodyExit.EarlyRead.UnionWith(judgeExit.EarlyRead);
                bodyExit.MissingOnExit.UnionWith(judgeExit.MissingOnExit);
                state.CopyFrom(bodyExit);
                return;
            }
            if (loop.EnumBlock != null)
            {
                var enumExit = AnalyzeEnumFieldBlock(context, loop.EnumBlock, state.Clone(),
                    tracked, stack, finallyDepth);
                state.EarlyRead.UnionWith(enumExit.EarlyRead);
                state.MissingOnExit.UnionWith(enumExit.MissingOnExit);
            }
            var bodyExitFwd = AnalyzeEnumFieldBlock(context, loop.Body, state.Clone(),
                tracked, stack, finallyDepth);
            state.EarlyRead.UnionWith(bodyExitFwd.EarlyRead);
            state.MissingOnExit.UnionWith(bodyExitFwd.MissingOnExit);
            var judgeExitFwd = AnalyzeEnumFieldBlock(context, loop.Judge, state.Clone(),
                tracked, stack, finallyDepth);
            state.EarlyRead.UnionWith(judgeExitFwd.EarlyRead);
            state.MissingOnExit.UnionWith(judgeExitFwd.MissingOnExit);
        }

        private static void AnalyzeEnumFieldTry(BilFunctionContext context,
            TryInstruction tryInstruction, EnumFieldInitState state, HashSet<string> tracked,
            HashSet<BilBlock> stack, int finallyDepth)
        {
            var innerDepth = tryInstruction.FinallyBlock != null ? finallyDepth + 1 : finallyDepth;
            var tryExit = AnalyzeEnumFieldBlock(context, tryInstruction.Body, state.Clone(),
                tracked, stack, innerDepth);
            if (tryInstruction.CatchTable is BilCatchTableResource catchTable)
            {
                var catchExit = tryExit.Clone();
                foreach (var entry in catchTable.Entries)
                {
                    var handlerExit = AnalyzeEnumFieldBlock(context, entry.Handler, state.Clone(),
                        tracked, stack, innerDepth);
                    catchExit.IntersectAssignWith(handlerExit);
                }
                tryExit = catchExit;
            }
            if (tryInstruction.FinallyBlock != null)
            {
                tryExit = AnalyzeEnumFieldBlock(context, tryInstruction.FinallyBlock, tryExit,
                    tracked, stack, finallyDepth);
            }
            state.CopyFrom(tryExit);
        }

        private static void NoteEnumFieldAccess(BilInstruction instruction,
            EnumFieldInitState state, HashSet<string> tracked)
        {
            switch (instruction)
            {
                case GetFieldInstruction getField:
                    NoteEnumFieldRead(state, tracked, getField.Object.Name, getField.Field.Symbol);
                    break;
                case SetFieldInstruction setField:
                    NoteEnumFieldWrite(state, tracked, setField.Object.Name, setField.Field.Symbol);
                    break;
                case GetFieldIndirectInstruction getIndirect:
                    if (state.FieldIdOf.TryGetValue(getIndirect.FieldId.Name, out var getSymbol))
                    {
                        NoteEnumFieldRead(state, tracked, getIndirect.Object.Name, getSymbol);
                    }
                    break;
                case SetFieldIndirectInstruction setIndirect:
                    if (state.FieldIdOf.TryGetValue(setIndirect.FieldId.Name, out var setSymbol))
                    {
                        NoteEnumFieldWrite(state, tracked, setIndirect.Object.Name, setSymbol);
                    }
                    break;
            }
        }

        private static void NoteEnumFieldRead(EnumFieldInitState state, HashSet<string> tracked,
            string objectName, string fieldSymbol)
        {
            if (objectName != ".this" || !tracked.Contains(fieldSymbol)
                || state.Assigned.Contains(fieldSymbol))
            {
                return;
            }
            state.EarlyRead.Add(fieldSymbol);
        }

        private static void NoteEnumFieldWrite(EnumFieldInitState state, HashSet<string> tracked,
            string objectName, string fieldSymbol)
        {
            if (objectName == ".this" && tracked.Contains(fieldSymbol))
            {
                state.Assigned.Add(fieldSymbol);
            }
        }
    }
}
