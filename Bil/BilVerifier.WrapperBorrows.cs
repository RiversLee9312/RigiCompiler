namespace RigiCompiler.Bil
{
    public static partial class BilVerifier
    {
        // wrapper 临时只借用宿主存储。局部转发保留类型，不能返回、装箱、
        // 写入普通存储或作为实参传递；接收者位置不等于普通实参位置。
        private static void VerifyWrapperBorrows(BilFunctionContext context,
            List<BilVerificationError> errors)
        {
            foreach (var (block, instruction) in context.AllInstructions())
            {
                var values = new List<BilVariableOperand>();
                var writes = new List<BilVariableOperand>();
                ClassifyVariables(instruction, values, writes);
                switch (instruction)
                {
                    case GetVarInstruction move when IsLocalBorrow(move.Target):
                        values.Remove(move.Source);
                        break;
                    case SetVarInstruction move when IsLocalBorrow(move.Target):
                        values.Remove(move.Source);
                        break;
                    case GetFieldInstruction field: values.Remove(field.Object); break;
                    case GetArrayInstruction index: values.Remove(index.Array); break;
                    case SetArrayInstruction index: values.Remove(index.Collection); break;
                    case SetFieldInstruction field: values.Remove(field.Object); break;
                    case GetFieldIndirectInstruction field: values.Remove(field.Object); break;
                    case SetFieldIndirectInstruction field: values.Remove(field.Object); break;
                    case SetWrapperFieldInstruction field: values.Remove(field.Object); break;
                    case GetWrapperFieldInstruction field: values.Remove(field.Object); break;
                    case GetWrapperInstruction wrapper: values.Remove(wrapper.Value); break;
                    case GetWrapperIndirectInstruction wrapper: values.Remove(wrapper.Value); break;
                    case GetIdVarInstruction id: values.Remove(id.Value); break;
                    case InvokeInstruction call: AllowReceiver(call.Method.Symbol, call.Arguments); break;
                    case InvokeNoResultInstruction call: AllowReceiver(call.Method.Symbol, call.Arguments); break;
                    case InvokeIndirectInstruction call: values.Remove(call.CallTarget); break;
                    case InvokeIndirectNoResultInstruction call: values.Remove(call.CallTarget); break;
                }
                if (values.Any(IsWrapperVariable)
                    || writes.Any(value => IsWrapperVariable(value)
                        && (value.Name == ".return" || instruction is CastInstruction
                            or CastIndirectInstruction or NewInstruction or NewIndirectInstruction
                            or NewWrappedInstruction or NewCaseInstruction or NewWrappedCaseInstruction)))
                    errors.Add(new BilVerificationError("21.3", context.Function.Symbol + " / " + block.Id,
                        "wrapper 借用不能整体取值、构造或逃逸（只能作为成员接收者）"));

                void AllowReceiver(string method, IReadOnlyList<BilVariableOperand> arguments)
                {
                    if (arguments.Count != 0
                        && BilVerificationContext.TryParseMethodSymbol(method, out var owner,
                            out var isStatic, out _, out _)
                        && !isStatic && (IsWrapperType(owner)
                            || (IsWrapperVariable(arguments[0])
                                && context.VariableTypes.TryGetValue(arguments[0].Name,
                                    out var receiverType)
                                && BilVerificationContext.StripTypeArguments(receiverType)
                                    == BilVerificationContext.StripTypeArguments(owner))))
                    {
                        // 泛型 owner 在方法符号中只有定义名，按零元反查会漏掉
                        // wrapper；由接收者的完整类型确认同一声明，仍只豁免首参。
                        values.Remove(arguments[0]);
                    }
                }
            }

            bool IsLocalBorrow(BilVariableOperand value) => value.Name != ".return"
                && context.Function.Vars.Any(v => v.Name == value.Name) && IsWrapperVariable(value);
            bool IsWrapperVariable(BilVariableOperand value) =>
                context.VariableTypes.TryGetValue(value.Name, out var type) && IsWrapperType(type);
            bool IsWrapperType(string type) => context.Module.TryGetTypeDeclaration(type,
                out var declaration) && declaration.Kind == BilTypeKind.Wrapper;
        }
    }
}
