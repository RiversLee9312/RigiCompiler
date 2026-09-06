namespace RigiCompiler.Bil
{
    public static partial class BilVerifier
    {
        // block 是可共享的结构化区域。按 (块, 调用时权限) 遍历，不能因为
        // 同一块曾从 unsafe 路径到达，就替随后到达的 safe 路径授予权限。
        private static void VerifyUnsafeContexts(BilFunctionContext context,
            List<BilVerificationError> errors)
        {
            var function = context.Function;
            var methodUnsafe = context.Module.MethodDeclarations.TryGetValue(function.Symbol,
                out var declaration) && HasKeyword(declaration, BilKeyword.Unsafe);
            var visited = new HashSet<(BilBlock, bool)>();
            var pending = new Stack<(BilBlock Block, bool Unsafe)>();
            foreach (var block in function.Blocks.Where(b => b.Modifiers.Contains(BilBlockModifier.Entrypoint)))
                pending.Push((block, methodUnsafe));
            while (pending.TryPop(out var item))
            {
                var isUnsafe = item.Unsafe || item.Block.Modifiers.Contains(BilBlockModifier.Unsafe);
                if (!visited.Add((item.Block, isUnsafe))) continue;
                foreach (var instruction in item.Block.Instructions)
                {
                    if (!isUnsafe)
                    {
                        var target = instruction switch
                        {
                            InvokeInstruction call => call.Method.Symbol,
                            InvokeNoResultInstruction call => call.Method.Symbol,
                            _ => null,
                        };
                        if (target != null && IsUnsafeCall(context, target))
                        {
                            errors.Add(new BilVerificationError("21.8", function.Symbol + "/" + item.Block.Id,
                                $"unsafe 方法调用需要 unsafe 上下文：{target}"));
                        }
                        if (instruction is NewInstruction create)
                            VerifyUnsafeConstruction(context, create.Type.TypeRef, create.Arguments,
                                item.Block.Id, errors);
                        // typeid 只有具体静态界时可检查元数据；泛型占位沿用既有透传。
                        if (instruction is NewIndirectInstruction indirect
                            && TryTypeIdBound(VarType(context, indirect.TypeId), out var bound))
                            VerifyUnsafeConstruction(context, bound, indirect.Arguments,
                                item.Block.Id, errors);
                        if (instruction is InvokeIndirectInstruction indirectCall)
                            VerifyUnsafeIndirectCall(context, indirectCall.CallTarget, indirectCall.Arguments,
                                item.Block.Id, errors);
                        if (instruction is InvokeIndirectNoResultInstruction indirectNoResult)
                            VerifyUnsafeIndirectCall(context, indirectNoResult.CallTarget, indirectNoResult.Arguments,
                                item.Block.Id, errors);
                        if (instruction is NewWrappedInstruction wrapped)
                            VerifyUnsafeConstruction(context, wrapped.Type.TypeRef, wrapped.InitArguments,
                                item.Block.Id, errors);
                    }
                    foreach (var child in ReferencedBlocks(instruction)) pending.Push((child, isUnsafe));
                }
            }
        }

        private static bool IsUnsafeCall(BilFunctionContext context, string target)
        {
            if (context.Module.MethodDeclarations.TryGetValue(target, out var method)
                && HasKeyword(method, BilKeyword.Unsafe)) return true;
            return BilVerificationContext.TryParseMethodSymbol(target, out var owner, out _, out _, out _)
                && context.Module.TryGetTypeDeclaration(owner, out var type)
                && HasKeyword(type.Modifiers, BilKeyword.Unsafe);
        }

        private static void VerifyUnsafeIndirectCall(BilFunctionContext context,
            BilVariableOperand target, IReadOnlyList<BilVariableOperand> arguments,
            string blockId, List<BilVerificationError> errors)
        {
            var type = VarType(context, target);
            // 复用间接调用的精确签名查询，未知外部/泛型目标不凭空增加限制。
            // 形状诊断由类型验证负责，这里只消费已确定的调用成员。
            if (type != null && TryFindCallOperator(context, type, arguments, blockId,
                    new List<BilVerificationError>(), out _, out _, out _, out var method)
                && method != null && IsUnsafeCall(context, method.Symbol))
                errors.Add(new BilVerificationError("21.8", context.Function.Symbol + "/" + blockId,
                    $"unsafe 方法调用需要 unsafe 上下文：{method.Symbol}"));
        }
        private static void VerifyUnsafeConstruction(BilFunctionContext context, string typeRef,
            IReadOnlyList<BilVariableOperand> arguments, string blockId, List<BilVerificationError> errors)
        {
            if (!context.Module.TryGetTypeDeclaration(typeRef, out var type)) return;
            var unsafeConstructor = HasKeyword(type.Modifiers, BilKeyword.Unsafe);
            foreach (var init in CollectInits(type))
            {
                if (!HasKeyword(init, BilKeyword.Unsafe)
                    || !BilVerificationContext.TryParseMethodSymbol(init.Symbol, out _, out _,
                        out var parameters, out _)) continue;
                for (var i = 0; i < parameters.Count; i++)
                    parameters[i] = (parameters[i].Name,
                        SubstituteHostGenerics(parameters[i].TypeRef, type, typeRef));
                unsafeConstructor |= SignatureMatches(context, parameters, arguments);
            }
            if (unsafeConstructor)
                errors.Add(new BilVerificationError("21.8", context.Function.Symbol + "/" + blockId,
                    $"unsafe 构造需要 unsafe 上下文：{typeRef}"));
        }
    }
}
