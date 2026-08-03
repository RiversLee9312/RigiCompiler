using System.Collections.Generic;

namespace LatteCompiler.Bil
{
    // BilVerifier 控制流检查（§20.4 definite assignment + §20.5 控制流
    // + §20.6 .breakid capability）。
    //
    // DA 为保守流分析（零误报优先，漏报可接受）：参数入口已赋值；线性
    // 读前赋值；if 分支独立分析、合并取交集；loop/switch/try 子块入口
    // 取进入态、出口保守取进入态；call blk 出口 = 块分析出口（块内赋值
    // 对调用点可见，§16.1）。loop.rev 的 condition 首次读取在 body 之后，
    // 不查进入时已赋值（§16.4）。
    //
    // 结构环（含 call blk 直接结构递归）一律拒绝——§20.5 允许实现拒绝
    // 无法证明有界的直接结构递归。

    public static partial class BilVerifier
    {
        private static void VerifyFunctionFlow(BilFunctionContext context,
            List<BilVerificationError> errors)
        {
            var function = context.Function;

            // ===== §20.5 结构：恰一个 entrypoint block；entry 必须终止性收尾 =====
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
                errors.Add(new BilVerificationError("20.5", function.Symbol,
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
                    errors.Add(new BilVerificationError("20.5", function.Symbol,
                        $"entrypoint block \"{entryBlock.Id}\" 不得以落尾结束（必须显式 ret 或 throw）"));
                }
            }

            // ===== §20.5 块成员资格 + §20.6 breakid 绑定唯一 =====
            var breakIdBindings = new Dictionary<string, string>();
            foreach (var (block, instruction) in InstructionsInFunction(context))
            {
                var location = function.Symbol + " / " + block.Id;
                foreach (var referenced in ReferencedBlocks(instruction))
                {
                    if (!context.BlockSet.Contains(referenced))
                    {
                        errors.Add(new BilVerificationError("20.5", location,
                            $"block 引用越权：\"{referenced.Id}\" 不属于当前函数"));
                    }
                }
                // loop/switch 的 breakid 绑定位：必须声明为 .breakid 变量，
                // 且全 fn 唯一绑定一次（§9.3/§20.6）
                string? boundBreakId = instruction switch
                {
                    LoopInstruction loop => loop.BreakId.Name,
                    SwitchInstruction switchInstruction => switchInstruction.BreakId.Name,
                    _ => null,
                };
                if (boundBreakId != null)
                {
                    if (!context.VariableTypes.ContainsKey(boundBreakId))
                    {
                        errors.Add(new BilVerificationError("20.2", location,
                            $"breakid 变量 \"${boundBreakId}\" 未声明"));
                    }
                    else if (!context.BreakIdVariables.Contains(boundBreakId))
                    {
                        errors.Add(new BilVerificationError("20.6", location,
                            $"loop/switch 只能绑定 .breakid 类型变量，\"${boundBreakId}\" " +
                            $"声明类型为 \"{context.VariableTypes[boundBreakId]}\""));
                    }
                    if (breakIdBindings.TryGetValue(boundBreakId, out var firstLocation))
                    {
                        errors.Add(new BilVerificationError("20.6", location,
                            $".breakid 变量 \"${boundBreakId}\" 被二次绑定（首次于 {firstLocation}）"));
                    }
                    else
                    {
                        breakIdBindings.Add(boundBreakId, location);
                    }
                }
            }

            // ===== §20.4 DA + token 作用域（从 entrypoint 出发的可达分析）=====
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
                    if (tryInstruction.CatchTable is not BilCatchTableResource catchTable
                        || catchTable.Entries.Count == 0
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
        // tokens 为活跃 breakid 结构栈（来源指令种类区分 loop/switch）；
        // stack 为分析路径块栈（环检测）；reported 按 (位置, 变量) 去重 DA 错误
        private static HashSet<string> AnalyzeBlock(BilFunctionContext context, BilBlock block,
            HashSet<string> assigned, List<(string Name, bool IsLoop)> tokens,
            HashSet<BilBlock> stack, List<BilVerificationError> errors,
            HashSet<(string Location, string Name)> reported)
        {
            if (!context.BlockSet.Contains(block))
            {
                return assigned;   // 跨函数引用已由结构检查报错
            }
            if (!stack.Add(block))
            {
                errors.Add(new BilVerificationError("20.5", context.Function.Symbol,
                    $"结构块引用成环（含 call blk 直接结构递归）：\"{block.Id}\""));
                return new HashSet<string>(assigned);
            }
            try
            {
                var reads = new List<BilVariableOperand>();
                var writes = new List<BilVariableOperand>();
                foreach (var instruction in block.Instructions)
                {
                    var location = context.Function.Symbol + " / " + block.Id;
                    reads.Clear();
                    writes.Clear();
                    ClassifyVariables(instruction, reads, writes);

                    // §20.4：读前已赋值
                    foreach (var variable in reads)
                    {
                        if (context.VariableTypes.ContainsKey(variable.Name)
                            && !assigned.Contains(variable.Name)
                            && reported.Add((location, variable.Name)))
                        {
                            errors.Add(new BilVerificationError("20.4", location,
                                $"变量 \"${variable.Name}\" 在赋值前被读取"));
                        }
                    }

                    switch (instruction)
                    {
                        case BreakInstruction breakInstruction:
                            VerifyBreakToken(context, breakInstruction.BreakId, tokens,
                                isContinue: false, location, errors);
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
                                    errors.Add(new BilVerificationError("20.5", location,
                                        $"非 void 函数（.return = {context.ReturnType}）不得裸 ret"));
                                }
                                if (ret.Value != null && context.ReturnType == ".void")
                                {
                                    errors.Add(new BilVerificationError("20.5", location,
                                        "void 函数 ret 不得带值"));
                                }
                            }
                            break;
                        case IfInstruction ifInstruction:
                        {
                            var thenExit = AnalyzeBlock(context, ifInstruction.ThenBlock,
                                new HashSet<string>(assigned), tokens, stack, errors, reported);
                            var elseExit = ifInstruction.ElseBlock != null
                                ? AnalyzeBlock(context, ifInstruction.ElseBlock,
                                    new HashSet<string>(assigned), tokens, stack, errors, reported)
                                : new HashSet<string>(assigned);
                            // 结构化路径合并：两分支都赋值的变量才算已赋值
                            thenExit.IntersectWith(elseExit);
                            assigned = thenExit;
                            break;
                        }
                        case LoopInstruction loop:
                            VerifyLoop(context, loop, assigned, tokens, stack, errors, reported,
                                location);
                            break;
                        case SwitchInstruction switchInstruction:
                        {
                            var caseTokens = new List<(string, bool)>(tokens)
                                { (switchInstruction.BreakId.Name, false) };
                            foreach (var itemBlock in switchInstruction.ItemBlocks)
                            {
                                AnalyzeBlock(context, itemBlock, new HashSet<string>(assigned),
                                    caseTokens, stack, errors, reported);
                            }
                            AnalyzeBlock(context, switchInstruction.DefaultBlock,
                                new HashSet<string>(assigned), caseTokens, stack, errors, reported);
                            break;   // 出口保守：保持进入态
                        }
                        case CallBlockInstruction call:
                            // §16.1：块落尾返回续 call 的下一条——块内赋值对调用点可见
                            assigned = AnalyzeBlock(context, call.Block, assigned, tokens,
                                stack, errors, reported);
                            break;
                        case TryInstruction tryInstruction:
                            AnalyzeBlock(context, tryInstruction.Body,
                                new HashSet<string>(assigned), tokens, stack, errors, reported);
                            if (tryInstruction.CatchTable is BilCatchTableResource catchTable)
                            {
                                foreach (var entry in catchTable.Entries)
                                {
                                    // 异常槽在 handler 内视为已赋值（§16.7）
                                    var handlerAssigned = new HashSet<string>(assigned)
                                        { tryInstruction.ExceptionSlot.Name };
                                    AnalyzeBlock(context, entry.Handler, handlerAssigned, tokens,
                                        stack, errors, reported);
                                }
                            }
                            if (tryInstruction.FinallyBlock != null)
                            {
                                AnalyzeBlock(context, tryInstruction.FinallyBlock,
                                    new HashSet<string>(assigned), tokens, stack, errors, reported);
                            }
                            break;   // 出口保守：保持进入态
                    }

                    foreach (var variable in writes)
                    {
                        assigned.Add(variable.Name);
                    }
                }
                return assigned;
            }
            finally
            {
                stack.Remove(block);
            }
        }

        private static void VerifyLoop(BilFunctionContext context, LoopInstruction loop,
            HashSet<string> assigned, List<(string Name, bool IsLoop)> tokens,
            HashSet<BilBlock> stack, List<BilVerificationError> errors,
            HashSet<(string Location, string Name)> reported, string location)
        {
            // condition 变量必须已声明。§20.4：loop condition 在每次读取前
            // 由 judge block 赋值——进入循环时不要求已赋值（judge 在首次
            // 读取前执行，loop/loop.rev 同规则），只要求 judge 块写入它
            if (!context.VariableTypes.ContainsKey(loop.Condition.Name))
            {
                errors.Add(new BilVerificationError("20.2", location,
                    $"loop 条件变量 \"${loop.Condition.Name}\" 未声明"));
            }

            // §20.4：loop condition 每次读取前由 judge block 赋值——judge 块
            // （含嵌套结构）内必须存在对 condition 的写入
            if (!BlockWritesTo(context, loop.Judge, loop.Condition.Name))
            {
                errors.Add(new BilVerificationError("20.4", location,
                    $"loop 的 judge block \"{loop.Judge.Id}\" 未对条件变量 " +
                    $"\"${loop.Condition.Name}\" 赋值"));
            }

            var loopTokens = new List<(string, bool)>(tokens) { (loop.BreakId.Name, true) };
            if (loop.EnumBlock != null)
            {
                AnalyzeBlock(context, loop.EnumBlock, new HashSet<string>(assigned),
                    loopTokens, stack, errors, reported);
            }
            AnalyzeBlock(context, loop.Body, new HashSet<string>(assigned),
                loopTokens, stack, errors, reported);
            AnalyzeBlock(context, loop.Judge, new HashSet<string>(assigned),
                loopTokens, stack, errors, reported);
            // 出口保守：保持进入态（body 可能零次执行）
        }

        // break/continue 的 token：必须声明为 .breakid 变量，且在活跃结构
        // 作用域内（§20.5）；continue 不得引用 switch token（§16.5）
        private static void VerifyBreakToken(BilFunctionContext context,
            BilVariableOperand token, List<(string Name, bool IsLoop)> tokens,
            bool isContinue, string location, List<BilVerificationError> errors)
        {
            if (!context.VariableTypes.ContainsKey(token.Name))
            {
                errors.Add(new BilVerificationError("20.2", location,
                    $"break/continue 的 token \"${token.Name}\" 未声明"));
                return;
            }
            if (!context.BreakIdVariables.Contains(token.Name))
            {
                errors.Add(new BilVerificationError("20.6", location,
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
                        errors.Add(new BilVerificationError("20.5", location,
                            $"continue 不得引用 switch 的 breakid \"${token.Name}\""));
                    }
                    return;
                }
            }
            errors.Add(new BilVerificationError("20.5", location,
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
    }
}
