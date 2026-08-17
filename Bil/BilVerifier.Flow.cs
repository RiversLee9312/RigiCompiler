using System.Collections.Generic;

namespace RigiCompiler.Bil
{
    // BilVerifier 控制流检查（§21.4 definite assignment + §21.5 控制流
    // + §21.6 .breakid capability）。§21.8 enum struct 实例字段 init
    // 全路径写入由本文件 VerifyEnumStructInstanceFields 在 DA 之后执行。
    //
    // DA 为保守流分析（零误报优先，漏报可接受）：参数入口已赋值；线性
    // 读前赋值；if 分支独立分析、合并取交集；loop/switch/try 子块入口
    // 取进入态、出口保守取进入态；call blk 出口 = 块分析出口（块内赋值
    // 对调用点可见，§16.1）。loop.rev 的 condition 首次读取在 body 之后，
    // 不查进入时已赋值（§16.4）。
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
        // reported 按 (位置, 变量) 去重 DA 错误
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
                errors.Add(new BilVerificationError("21.5", context.Function.Symbol,
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
                            var thenExit = AnalyzeBlock(context, ifInstruction.ThenBlock,
                                new HashSet<string>(assigned), ifTokens, stack, errors, reported);
                            var elseExit = ifInstruction.ElseBlock != null
                                ? AnalyzeBlock(context, ifInstruction.ElseBlock,
                                    new HashSet<string>(assigned), ifTokens, stack, errors, reported)
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
                            // §16.1：块落尾返回续 call 的下一条——块内赋值对调用点可见；
                            // call 的 breakid token 在被调块内活跃（§16.5 推广）
                            var callTokens = new List<(string, bool)>(tokens)
                                { (call.BreakId.Name, false) };
                            assigned = AnalyzeBlock(context, call.Block, assigned, callTokens,
                                stack, errors, reported);
                            break;
                        case TryInstruction tryInstruction:
                            // try 的 breakid token 在 body/各 handler/finally
                            // 内活跃（§16.5 推广；finally 内允许 break tryId）
                            var tryTokens = new List<(string, bool)>(tokens)
                                { (tryInstruction.BreakId.Name, false) };
                            var tryExit = AnalyzeBlock(context, tryInstruction.Body,
                                new HashSet<string>(assigned), tryTokens, stack, errors, reported);
                            if (tryInstruction.CatchTable is BilCatchTableResource catchTable)
                            {
                                var catchExit = new HashSet<string>(tryExit);
                                foreach (var entry in catchTable.Entries)
                                {
                                    // 异常槽在 handler 内视为已赋值（§16.7）
                                    var handlerAssigned = new HashSet<string>(assigned)
                                        { tryInstruction.ExceptionSlot.Name };
                                    var handlerExit = AnalyzeBlock(context, entry.Handler, handlerAssigned, tryTokens,
                                        stack, errors, reported);
                                    catchExit.IntersectWith(handlerExit);
                                }
                                assigned = catchExit;
                            }
                            else
                            {
                                // 无 catch 时正常路径必经 body，异常路径不回到
                                // try 后续；finally 可继续更新该正常出口的 DA。
                                assigned = tryExit;
                            }
                            if (tryInstruction.FinallyBlock != null)
                            {
                                // §16.7：try 指令在进 finally 前恒写 EXCEPTION_VAR
                                // （正常路径写 null / 异常路径写当前逃逸异常）——
                                // finally(e) cell 化后体头读 slot 构造 cell 合法
                                var finallyAssigned = new HashSet<string>(assigned)
                                    { tryInstruction.ExceptionSlot.Name };
                                assigned = AnalyzeBlock(context, tryInstruction.FinallyBlock,
                                    finallyAssigned, tryTokens, stack, errors, reported);
                            }
                            break;
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
                var bodyExit = AnalyzeBlock(context, loop.Body, new HashSet<string>(assigned),
                    loopTokens, stack, errors, reported);
                var judgeEntry = bodyExit;
                if (loop.EnumBlock != null)
                {
                    judgeEntry = AnalyzeBlock(context, loop.EnumBlock,
                        new HashSet<string>(bodyExit), loopTokens, stack, errors, reported);
                }
                AnalyzeBlock(context, loop.Judge, new HashSet<string>(judgeEntry),
                    loopTokens, stack, errors, reported);
                assigned.Clear();
                assigned.UnionWith(bodyExit);
                return;
            }
            // §16.3 正向 loop：body 可能零次执行——三块都用进入态副本分析，
            // 出口保守保持进入态
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
