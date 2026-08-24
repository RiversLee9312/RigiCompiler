using System.Collections.Generic;
using RigiCompiler.Bil;

namespace RigiCompiler.Middleware
{
    /// <summary>
    /// MirBuilder（MW3 层，MW1 最小落地）：BIL 结构化块 → MIR CFG 的确定性
    /// 直译。输入已过 BilVerifier 门禁，故形状不变量（恰一 entrypoint block、
    /// 局部先声明后引用等）直接依赖，不复查。MW1 支持面：单 block 函数，
    /// load/set.var/get.var/一元二元内建/invoke 族/ret；控制流指令（结构化
    /// 拍平为多 block + Br）随控制流里程碑扩展，本骨架不变。
    /// </summary>
    public static class MirBuilder
    {
        public static MirModule Build(MwContext context)
        {
            // 可达性构建：从入口出发沿 invoke 边收闭包。模块中不可达的 fn
            // （如 bootstrap 预定义符号的编译器合成体 toString——它们不进
            // 符号段，verifier 以硬编码环境闭合）不建 MIR 也不进发射；
            // 这同时是模块级死代码消除。MW1 可达边只有 invoke 族
            var bySymbol = new Dictionary<string, BilFunction>(System.StringComparer.Ordinal);
            foreach (var bilFn in context.Module.Functions)
            {
                bySymbol.Add(bilFn.Symbol, bilFn);
            }

            var built = new Dictionary<string, MirFunction>(System.StringComparer.Ordinal);
            var order = new List<MirFunction>();
            var queue = new Queue<string>();
            foreach (var bilFn in context.Module.Functions)
            {
                // 无符号段声明的 fn 是预定义符号的合成体（§9.1 verifier 以
                // 硬编码环境豁免），永不为入口；不可达则不建 MIR
                var member = context.Symbols.FindMember(bilFn.Symbol);
                if (member != null && HasKeyword(member.Declaration, BilKeyword.Entrypoint))
                {
                    queue.Enqueue(bilFn.Symbol);
                }
            }
            while (queue.Count > 0)
            {
                var symbol = queue.Dequeue();
                if (built.ContainsKey(symbol))
                {
                    continue;
                }
                var fn = BuildFunction(context, bySymbol[symbol]);
                built.Add(symbol, fn);
                order.Add(fn);
                foreach (var block in fn.Blocks)
                {
                    foreach (var inst in block.Instructions)
                    {
                        if (inst is MirCall call
                            && ImplBinder.BindCall(call.Target) is DirectCallBinding
                            && bySymbol.ContainsKey(call.Target.Canonical))
                        {
                            queue.Enqueue(call.Target.Canonical);
                        }
                    }
                }
            }
            var module = new MirModule(order);
            context.Mir = module;
            return module;
        }

        private static MirFunction BuildFunction(MwContext context, BilFunction bilFn)
        {
            var symbol = context.Symbols.FindMember(bilFn.Symbol)
                ?? throw new MwNotSupportedException(
                    $"MW1 不支持无符号段声明的 fn（预定义合成体）: {bilFn.Symbol}");

            // .args：.return 在前，其后普通参数；MW1 拒绝隐藏参数形态
            MirType? returnType = null;
            var parameters = new List<MirLocal>();
            var locals = new List<MirLocal>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var arg in bilFn.Args)
            {
                if (arg.Name == ".return")
                {
                    returnType = MirType.Of(arg.TypeRef);
                    continue;
                }
                if (arg.Name.StartsWith('.'))
                {
                    throw new MwNotSupportedException(
                        $"MW1 不支持隐藏参数 {arg.Name}（fn {bilFn.Symbol}）");
                }
                AddLocal(parameters, locals, seen, arg.Name, arg.TypeRef, bilFn.Symbol);
            }
            if (returnType == null)
            {
                throw new CompilerInternalException($"fn 缺 .return 条目: {bilFn.Symbol}");
            }

            foreach (var varDecl in bilFn.Vars)
            {
                AddLocal(null, locals, seen, varDecl.Name, varDecl.TypeRef, bilFn.Symbol);
            }

            // MW1：单 block（多 block 只能由控制流指令引用产生，随控制流里程碑放行）
            if (bilFn.Blocks.Count != 1)
            {
                throw new MwNotSupportedException(
                    $"MW1 仅支持单 block 函数（fn {bilFn.Symbol} 有 {bilFn.Blocks.Count} 个 block）");
            }

            var localMap = new Dictionary<string, MirLocal>(System.StringComparer.Ordinal);
            foreach (var local in locals)
            {
                localMap.Add(local.Name, local);
            }
            var blocks = new List<MirBlock> { BuildBlock(context, bilFn, bilFn.Blocks[0], localMap) };

            var isEntrypoint = HasKeyword(symbol.Declaration, BilKeyword.Entrypoint);
            return new MirFunction(symbol, returnType, parameters, locals, blocks, isEntrypoint);
        }

        private static void AddLocal(List<MirLocal>? parameters, List<MirLocal> locals,
            HashSet<string> seen, string name, string typeRef, string fnSymbol)
        {
            // .vars 与参数共名属生成方违约（verifier 已查）；此处防御
            if (!seen.Add(name))
            {
                throw new CompilerInternalException($"fn {fnSymbol} 局部重名: {name}");
            }
            var local = new MirLocal(name, MirType.Of(typeRef));
            locals.Add(local);
            parameters?.Add(local);
        }

        private static MirBlock BuildBlock(MwContext context, BilFunction bilFn, BilBlock bilBlock,
            IReadOnlyDictionary<string, MirLocal> localMap)
        {
            var instructions = new List<MirInst>();
            MirTerminator? terminator = null;
            foreach (var inst in bilBlock.Instructions)
            {
                if (terminator != null)
                {
                    throw new CompilerInternalException(
                        $"fn {bilFn.Symbol} block {bilBlock.Id} 的 ret 之后仍有指令");
                }
                switch (inst)
                {
                    case LoadInstruction load:
                        instructions.Add(new MirLoadResource(load.Resource, load.Target.Name));
                        break;
                    case SetVarInstruction setVar:
                        instructions.Add(new MirCopyLocal(Local(setVar.Source), setVar.Target.Name));
                        break;
                    case GetVarInstruction getVar:
                        instructions.Add(new MirCopyLocal(Local(getVar.Source), getVar.Target.Name));
                        break;
                    case BinaryIntrinsicInstruction binary:
                        instructions.Add(new MirBinaryIntrinsic(binary.Op,
                            Local(binary.Left), Local(binary.Right),
                            TypeOf(binary.Left.Name), TypeOf(binary.Right.Name),
                            TypeOf(binary.Target.Name), binary.Target.Name));
                        break;
                    case UnaryIntrinsicInstruction unary:
                        instructions.Add(new MirUnaryIntrinsic(unary.Op, Local(unary.Operand),
                            TypeOf(unary.Operand.Name), TypeOf(unary.Target.Name), unary.Target.Name));
                        break;
                    case InvokeInstruction invoke:
                        instructions.Add(new MirCall(ResolveTarget(invoke.Method.Symbol),
                            Locals(invoke.Arguments), invoke.Target.Name));
                        break;
                    case InvokeNoResultInstruction invokeNoResult:
                        instructions.Add(new MirCall(ResolveTarget(invokeNoResult.Method.Symbol),
                            Locals(invokeNoResult.Arguments), null));
                        break;
                    case RetInstruction ret:
                        terminator = new MirRet(ret.Value == null ? null : Local(ret.Value));
                        break;
                    default:
                        throw new MwNotSupportedException(
                            $"MW1 不支持指令 {inst.Opcode}（fn {bilFn.Symbol}）");
                }
            }
            // verifier 保证函数有返回路径；防御
            terminator ??= new MirRet(null);
            return new MirBlock(bilBlock.Id, instructions, terminator);

            MirLocalOperand Local(BilVariableOperand operand) => new(operand.Name);
            MirType TypeOf(string name) => localMap[name].Type;

            MwMemberSymbol ResolveTarget(string symbol)
            {
                return context.Symbols.FindMember(symbol)
                    ?? throw new MwNotSupportedException(
                        $"MW1 不支持调用无符号段声明的预定义符号: {symbol}（fn {bilFn.Symbol}）");
            }

            static List<MirOperand> Locals(IReadOnlyList<BilVariableOperand> operands)
            {
                var list = new List<MirOperand>(operands.Count);
                foreach (var operand in operands)
                {
                    list.Add(new MirLocalOperand(operand.Name));
                }
                return list;
            }
        }

        private static bool HasKeyword(BilSimpleMemberDeclaration declaration, BilKeyword keyword)
        {
            foreach (var modifier in declaration.Modifiers)
            {
                if (modifier is BilKeywordModifier keywordModifier && keywordModifier.Keyword == keyword)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
