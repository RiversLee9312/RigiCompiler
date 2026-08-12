using System.Collections.Generic;

namespace LatteCompiler.Bil
{
    // BilVerifier 类型检查（§21.3）：变量类型环境 + 逐指令 switch。
    // 严格相等按 §6.4；含 .generic< 的 typeid 位置表达式与查不到声明的
    // 派生规则一律降级通过（防误报原则，见 BilVerifier.cs 文件头）。
    // S8c 增补：§13.6 get.array/set.array 非数组形态的严格三元组查询
    // （用户 getAtIndex/setAtIndex 索引运算符实现重查）。
    // M64 增补：§18 hint 指令（资源归属本模块 §21.2 + string 标量限定 §21.3）。
    //
    // ClassifyVariables 是指令读/写变量位置的唯一分类表（DA 与 breakid
    // 检查共用）；绑定/特殊位（loop/switch 的 breakid、break/continue 的
    // token、try 的异常槽）不进普通读/写，由 Flow.cs 按 capability 规则处理。
    // 两个 switch 的 default 都报「验证器未覆盖指令类型」——Bil 新增指令
    // 子类时必须同步扩展验证器（防腐化，惯例同 ASTIntegrityValidator）。

    public static partial class BilVerifier
    {
        private static void VerifyFunctionTypes(BilFunctionContext context,
            List<BilVerificationError> errors)
        {
            var reads = new List<BilVariableOperand>();
            var writes = new List<BilVariableOperand>();
            foreach (var (block, instruction) in context.AllInstructions())
            {
                var location = context.Function.Symbol + " / " + block.Id;
                reads.Clear();
                writes.Clear();
                if (!ClassifyVariables(instruction, reads, writes))
                {
                    errors.Add(new BilVerificationError("21.3", location,
                        $"验证器未覆盖指令类型 {instruction.GetType().Name}——请扩展 BilVerifier"));
                }
                // 变量可解析（§21.2）+ breakid 不得普通读写（§21.6）
                foreach (var variable in reads)
                {
                    VerifyVariableUse(context, variable, location, errors);
                }
                foreach (var variable in writes)
                {
                    VerifyVariableUse(context, variable, location, errors);
                }
                VerifyInstructionTypes(context, instruction, location, errors);
            }
        }

        private static void VerifyVariableUse(BilFunctionContext context,
            BilVariableOperand variable, string location, List<BilVerificationError> errors)
        {
            if (!context.VariableTypes.ContainsKey(variable.Name))
            {
                errors.Add(new BilVerificationError("21.2", location,
                    $"未声明的变量 \"${variable.Name}\""));
            }
            else if (context.BreakIdVariables.Contains(variable.Name))
            {
                errors.Add(new BilVerificationError("21.6", location,
                    $".breakid 变量 \"${variable.Name}\" 不得被普通读写（只允许 " +
                    "loop/loop.rev/switch 绑定与 break/continue 引用）"));
            }
        }

        // ===== 指令读/写变量位置分类（唯一分类表）=====
        // 返回 false = 未覆盖的指令子类
        internal static bool ClassifyVariables(BilInstruction instruction,
            List<BilVariableOperand> reads, List<BilVariableOperand> writes)
        {
            switch (instruction)
            {
                case LoadInstruction load:
                    writes.Add(load.Target);
                    return true;
                case GetVarInstruction getVar:
                    reads.Add(getVar.Source);
                    writes.Add(getVar.Target);
                    return true;
                case SetVarInstruction setVar:
                    reads.Add(setVar.Source);
                    writes.Add(setVar.Target);
                    return true;
                case GetFieldInstruction getField:
                    reads.Add(getField.Object);
                    writes.Add(getField.Target);
                    return true;
                case SetFieldInstruction setField:
                    reads.Add(setField.Source);
                    reads.Add(setField.Object);
                    return true;
                case GetFieldStaticInstruction getFieldStatic:
                    writes.Add(getFieldStatic.Target);
                    return true;
                case SetFieldStaticInstruction setFieldStatic:
                    reads.Add(setFieldStatic.Source);
                    return true;
                case GetArrayInstruction getArray:
                    reads.Add(getArray.Array);
                    reads.Add(getArray.Index);
                    writes.Add(getArray.Target);
                    return true;
                case SetArrayInstruction setArray:
                    reads.Add(setArray.Collection);
                    reads.Add(setArray.Index);
                    reads.Add(setArray.Element);
                    return true;
                case NewInstruction newInstruction:
                    reads.AddRange(newInstruction.Arguments);
                    writes.Add(newInstruction.Target);
                    return true;
                case NewCaseInstruction newCase:
                    reads.AddRange(newCase.Arguments);
                    writes.Add(newCase.Target);
                    return true;
                case NewWrappedInstruction newWrapped:
                    reads.AddRange(newWrapped.WrapperArguments);
                    reads.AddRange(newWrapped.InitArguments);
                    writes.Add(newWrapped.Target);
                    return true;
                case NewWrappedCaseInstruction newWrappedCase:
                    reads.AddRange(newWrappedCase.WrapperArguments);
                    reads.AddRange(newWrappedCase.CaseArguments);
                    writes.Add(newWrappedCase.Target);
                    return true;
                case NewWrapperFieldInstruction newWrapperField:
                    reads.AddRange(newWrapperField.Arguments);
                    return true;
                case NewWrapperMethodInstruction newWrapperMethod:
                    reads.AddRange(newWrapperMethod.Arguments);
                    return true;
                case NewWrapperEntityInstruction newWrapperEntity:
                    reads.AddRange(newWrapperEntity.Arguments);
                    return true;
                case InvokeInstruction invoke:
                    reads.AddRange(invoke.Arguments);
                    writes.Add(invoke.Target);
                    return true;
                case InvokeNoResultInstruction invokeNoResult:
                    reads.AddRange(invokeNoResult.Arguments);
                    return true;
                case BinaryIntrinsicInstruction binary:
                    reads.Add(binary.Left);
                    reads.Add(binary.Right);
                    writes.Add(binary.Target);
                    return true;
                case UnaryIntrinsicInstruction unary:
                    reads.Add(unary.Operand);
                    writes.Add(unary.Target);
                    return true;
                case AwaitInstruction awaitInstruction:
                    reads.Add(awaitInstruction.Task);
                    if (awaitInstruction.Result != null) writes.Add(awaitInstruction.Result);
                    return true;
                case YieldInstruction yieldInstruction:
                    if (yieldInstruction.Alarm != null) reads.Add(yieldInstruction.Alarm);
                    return true;
                case CastInstruction cast:
                    reads.Add(cast.Source);
                    writes.Add(cast.Target);
                    return true;
                case DirectTypeCheckInstruction directTypeCheck:
                    reads.Add(directTypeCheck.Value);
                    writes.Add(directTypeCheck.Target);
                    return true;
                case IndirectTypeCheckInstruction indirectTypeCheck:
                    reads.Add(indirectTypeCheck.Value);
                    reads.Add(indirectTypeCheck.TypeId);
                    writes.Add(indirectTypeCheck.Target);
                    return true;
                case IsCaseInstruction isCase:
                    reads.Add(isCase.Value);
                    writes.Add(isCase.Target);
                    return true;
                case GetWrapperInstruction getWrapper:
                    reads.Add(getWrapper.Value);
                    writes.Add(getWrapper.Target);
                    return true;
                case GetWrapperFieldInstruction getWrapperField:
                    reads.Add(getWrapperField.Object);
                    writes.Add(getWrapperField.Target);
                    return true;
                case GetSelfInstruction getSelf:
                    writes.Add(getSelf.Target);
                    return true;
                case SetWrapperFieldInstruction setWrapperField:
                    reads.Add(setWrapperField.Source);
                    reads.Add(setWrapperField.Object);
                    return true;
                case GetIdVarInstruction getIdVar:
                    reads.Add(getIdVar.Value);
                    writes.Add(getIdVar.Target);
                    return true;
                case GetIdTypeInstruction getIdType:
                    writes.Add(getIdType.Target);
                    return true;
                case InvokeIndirectInstruction invokeIndirect:
                    reads.Add(invokeIndirect.CallTarget);
                    reads.AddRange(invokeIndirect.Arguments);
                    writes.Add(invokeIndirect.Target);
                    return true;
                case InvokeIndirectNoResultInstruction invokeIndirectNoResult:
                    reads.Add(invokeIndirectNoResult.CallTarget);
                    reads.AddRange(invokeIndirectNoResult.Arguments);
                    return true;
                case RetInstruction ret:
                    if (ret.Value != null)
                    {
                        reads.Add(ret.Value);
                    }
                    return true;
                case IfInstruction ifInstruction:
                    reads.Add(ifInstruction.Condition);
                    return true;
                // loop 的 Condition 不进普通读位：loop.rev 首次读取在 body 之后
                // （进入时可能尚未赋值），DA 由 Flow.cs 按 loop/loop.rev 分别处理
                case LoopInstruction:
                    return true;
                case SwitchInstruction switchInstruction:
                    reads.Add(switchInstruction.Selector);
                    return true;
                case ThrowInstruction throwInstruction:
                    reads.Add(throwInstruction.Exception);
                    return true;
                // break/continue 的 token、loop/switch 的 breakid 绑定位、try 的
                // 异常槽：capability/特殊位，不进普通读写（Flow.cs 处理）
                case BreakInstruction:
                case ContinueInstruction:
                case CallBlockInstruction:
                case TryInstruction:
                    return true;
                // hint（§18）：不读写任何变量，纯位置标记，不参与 DA
                case HintInstruction:
                    return true;
                default:
                    return false;
            }
        }

        // ===== 逐指令类型规则（§21.3）=====
        private static void VerifyInstructionTypes(BilFunctionContext context,
            BilInstruction instruction, string location, List<BilVerificationError> errors)
        {
            switch (instruction)
            {
                case LoadInstruction load:
                    // §13.1：资源类型 ≡ TARGET；资源必须属于本模块（§21.2）
                    if (!context.Module.ResourceSet.Contains(load.Resource))
                    {
                        errors.Add(new BilVerificationError("21.2", location,
                            $"load 引用的资源 \"{load.Resource.Name}\" 不属于本模块"));
                    }
                    CheckType(context, VarType(context, load.Target),
                        BilVerificationContext.ResourceValueType(load.Resource), location,
                        "load 目标变量", errors);
                    break;

                case HintInstruction hint:
                    // §18：资源必须属于本模块（§21.2）且为 string 标量资源
                    if (!context.Module.ResourceSet.Contains(hint.Resource))
                    {
                        errors.Add(new BilVerificationError("21.2", location,
                            $"hint 引用的资源 \"{hint.Resource.Name}\" 不属于本模块"));
                    }
                    if (hint.Resource is not BilScalarResource { Type: BilScalarType.String })
                    {
                        errors.Add(new BilVerificationError("21.3", location,
                            $"hint 引用的资源 \"{hint.Resource.Name}\" 必须是 string 资源"));
                    }
                    break;

                case GetVarInstruction getVar:
                    CheckType(context, VarType(context, getVar.Target),
                        VarType(context, getVar.Source), location, "get.var 两端", errors);
                    break;

                case SetVarInstruction setVar:
                    CheckType(context, VarType(context, setVar.Target),
                        VarType(context, setVar.Source), location, "set.var 两端", errors);
                    break;

                case BinaryIntrinsicInstruction binary:
                    // §11.1：两操作数严格相同；§11.5：cmp 结果为 .bool
                    CheckType(context, VarType(context, binary.Right),
                        VarType(context, binary.Left), location, "二元运算两操作数", errors);
                    if (binary.Op is >= BilBinaryOp.CmpEq and <= BilBinaryOp.CmpGe)
                    {
                        CheckType(context, VarType(context, binary.Target), ".bool", location,
                            "cmp 结果", errors);
                    }
                    else
                    {
                        CheckType(context, VarType(context, binary.Target),
                            VarType(context, binary.Left), location, "二元运算结果", errors);
                    }
                    break;

                case UnaryIntrinsicInstruction unary:
                    CheckType(context, VarType(context, unary.Target),
                        VarType(context, unary.Operand), location, "一元运算结果", errors);
                    break;

                case YieldInstruction yieldInstruction:
                    VerifyYield(context, yieldInstruction, location, errors);
                    break;

                case AwaitInstruction awaitInstruction:
                    VerifyAwait(context, awaitInstruction, location, errors);
                    break;

                case CastInstruction cast:
                    // §12.1：RESULT ≡ TARGET_TYPE；§12.2：cast.safe 结果为 nullable 形式
                    VerifyResolvableType(context, cast.TargetType.TypeRef, location, errors);
                    CheckType(context, VarType(context, cast.Target),
                        cast.IsSafe ? ".nullable<" + cast.TargetType.TypeRef + ">"
                            : cast.TargetType.TypeRef,
                        location, "cast 结果", errors);
                    break;

                case DirectTypeCheckInstruction directTypeCheck:
                    VerifyResolvableType(context, directTypeCheck.TargetType.TypeRef, location, errors);
                    CheckType(context, VarType(context, directTypeCheck.Target), ".bool", location,
                        "type." + directTypeCheck.Kind + " 结果", errors);
                    if (directTypeCheck.Kind == BilTypeCheckKind.With)
                    {
                        VerifyWrapperType(context, directTypeCheck.TargetType.TypeRef, location, errors);
                    }
                    break;

                case IndirectTypeCheckInstruction indirectTypeCheck:
                    CheckType(context, VarType(context, indirectTypeCheck.TypeId), ".typeid",
                        location, "typeid 变量", errors, prefixMatch: true);
                    CheckType(context, VarType(context, indirectTypeCheck.Target), ".bool", location,
                        "type." + indirectTypeCheck.Kind + " 结果", errors);
                    break;

                case IsCaseInstruction isCase:
                    VerifyCaseCheck(context, isCase, location, errors);
                    CheckType(context, VarType(context, isCase.Target), ".bool", location,
                        "type.is.case 结果", errors);
                    break;

                case GetWrapperInstruction getWrapper:
                    VerifyWrapperType(context, getWrapper.WrapperType.TypeRef, location, errors);
                    CheckType(context, VarType(context, getWrapper.Target),
                        getWrapper.WrapperType.TypeRef, location, "get.wrapper 结果", errors);
                    break;

                case GetWrapperFieldInstruction getWrapperField:
                    VerifyGetWrapperField(context, getWrapperField, location, errors);
                    break;

                case GetSelfInstruction getSelf:
                    VerifyProxyTemplateInstruction(context, location, "get.self", errors);
                    VerifyGetSelfResult(context, getSelf, location, errors);
                    break;

                case SetWrapperFieldInstruction setWrapperField:
                    // wrapper 隐藏存储写后门：链结构校验，且链必须含 wrapper
                    // 元素（非纯 field 普通嵌套写——后者走 set.field）
                    if (!ChainHasWrapperElement(setWrapperField.Chain))
                    {
                        errors.Add(new BilVerificationError("21.3", location,
                            "set.wrapper.field 链必须含至少一个 wrapper(...) 元素" +
                            "（wrapper 隐藏存储后门，非普通字段写入；§13.3）"));
                        break;
                    }
                    VerifyWrapperFieldChain(context, setWrapperField.Chain,
                        setWrapperField.InnerField.Symbol,
                        VarType(context, setWrapperField.Object), location,
                        "set.wrapper.field", errors);
                    CheckType(context, VarType(context, setWrapperField.Source),
                        FieldTypeOf(context, setWrapperField.InnerField.Symbol), location,
                        "set.wrapper.field 源变量", errors);
                    VerifyFieldWritable(context, setWrapperField.InnerField.Symbol,
                        isInstanceWrite: true, location, errors);
                    break;

                case GetIdVarInstruction getIdVar:
                    CheckType(context, VarType(context, getIdVar.Target), ".typeid", location,
                        "getid.var 结果", errors, prefixMatch: true);
                    break;

                case GetIdTypeInstruction getIdType:
                    VerifyResolvableType(context, getIdType.TargetType.TypeRef, location, errors);
                    CheckType(context, VarType(context, getIdType.Target), ".typeid", location,
                        "getid.type 结果", errors, prefixMatch: true);
                    break;

                case GetFieldInstruction getField:
                    VerifyInstanceField(context, getField.Field.Symbol, VarType(context, getField.Object),
                        location, errors);
                    CheckType(context, VarType(context, getField.Target),
                        FieldTypeOf(context, getField.Field.Symbol), location,
                        "get.field 目标变量", errors);
                    break;

                case SetFieldInstruction setField:
                    VerifyInstanceField(context, setField.Field.Symbol, VarType(context, setField.Object),
                        location, errors);
                    CheckType(context, VarType(context, setField.Source),
                        FieldTypeOf(context, setField.Field.Symbol), location,
                        "set.field 源变量", errors);
                    VerifyFieldWritable(context, setField.Field.Symbol,
                        isInstanceWrite: true, location, errors);
                    break;

                case GetFieldStaticInstruction getFieldStatic:
                    VerifyStaticField(context, getFieldStatic.Field.Symbol,
                        getFieldStatic.OwnerType.TypeRef, location, errors);
                    CheckType(context, VarType(context, getFieldStatic.Target),
                        FieldTypeOf(context, getFieldStatic.Field.Symbol), location,
                        "get.field.static 目标变量", errors);
                    break;

                case SetFieldStaticInstruction setFieldStatic:
                    VerifyStaticField(context, setFieldStatic.Field.Symbol,
                        setFieldStatic.OwnerType.TypeRef, location, errors);
                    CheckType(context, VarType(context, setFieldStatic.Source),
                        FieldTypeOf(context, setFieldStatic.Field.Symbol), location,
                        "set.field.static 源变量", errors);
                    VerifyFieldWritable(context, setFieldStatic.Field.Symbol,
                        isInstanceWrite: false, location, errors);
                    break;

                case GetArrayInstruction getArray:
                    // §13.6：.array<T> 内建形态 TARGET ≡ 元素类型 T；
                    // 非数组（用户索引运算符）走严格三元组查询
                    var arrayType = VarType(context, getArray.Array);
                    if (arrayType != null && arrayType.StartsWith(".array<"))
                    {
                        var elementType = arrayType.Substring(".array<".Length,
                            arrayType.Length - ".array<".Length - 1);
                        CheckType(context, VarType(context, getArray.Target), elementType, location,
                            "get.array 目标变量", errors);
                    }
                    else if (arrayType != null)
                    {
                        VerifyIndexOperator(context, arrayType, VarType(context, getArray.Index),
                            VarType(context, getArray.Target), isGet: true, location, errors);
                    }
                    break;

                case SetArrayInstruction setArray:
                    // §13.6：.array<T> 内建形态 ELEMENT ≡ 元素类型 T
                    // （INDEX 规则同 get——内建形态不查索引类型）；
                    // 非数组（用户索引运算符）走严格三元组查询
                    var collectionType = VarType(context, setArray.Collection);
                    if (collectionType != null && collectionType.StartsWith(".array<"))
                    {
                        var setElementType = collectionType.Substring(".array<".Length,
                            collectionType.Length - ".array<".Length - 1);
                        CheckType(context, VarType(context, setArray.Element), setElementType,
                            location, "set.array 元素变量", errors);
                    }
                    else if (collectionType != null)
                    {
                        VerifyIndexOperator(context, collectionType,
                            VarType(context, setArray.Index), VarType(context, setArray.Element),
                            isGet: false, location, errors);
                    }
                    break;

                case NewInstruction newInstruction:
                    VerifyNew(context, newInstruction, location, errors);
                    break;

                case NewCaseInstruction newCase:
                    VerifyNewCase(context, newCase, location, errors);
                    break;

                case NewWrappedInstruction newWrapped:
                    VerifyNewWrapped(context, newWrapped, location, errors);
                    break;

                case NewWrappedCaseInstruction newWrappedCase:
                    VerifyNewWrappedCase(context, newWrappedCase, location, errors);
                    break;

                case NewWrapperFieldInstruction newWrapperField:
                    VerifyNewWrapperField(context, newWrapperField, location, errors);
                    break;

                case NewWrapperMethodInstruction newWrapperMethod:
                    VerifyNewWrapperMethod(context, newWrapperMethod, location, errors);
                    break;

                case NewWrapperEntityInstruction newWrapperEntity:
                    VerifyNewWrapperEntity(context, newWrapperEntity, location, errors);
                    break;

                case InvokeInstruction invoke:
                    VerifyInvoke(context, invoke.Method.Symbol, invoke.Arguments,
                        invoke.Target, location, errors);
                    break;

                case InvokeNoResultInstruction invokeNoResult:
                    VerifyInvoke(context, invokeNoResult.Method.Symbol, invokeNoResult.Arguments,
                        null, location, errors);
                    break;

                case InvokeIndirectInstruction invokeIndirect:
                    VerifyIndirectInvoke(context, invokeIndirect.CallTarget,
                        invokeIndirect.Arguments, invokeIndirect.Target, location, errors);
                    break;

                case InvokeIndirectNoResultInstruction invokeIndirectNoResult:
                    VerifyIndirectInvoke(context, invokeIndirectNoResult.CallTarget,
                        invokeIndirectNoResult.Arguments, null, location, errors);
                    break;

                case RetInstruction ret:
                    // §16.8：返回值类型 ≡ .return（形态检查在 Flow.cs）
                    if (ret.Value != null)
                    {
                        CheckType(context, VarType(context, ret.Value), context.ReturnType, location,
                            "ret 返回值", errors);
                    }
                    break;

                case IfInstruction ifInstruction:
                    CheckType(context, VarType(context, ifInstruction.Condition), ".bool", location,
                        "if 条件", errors);
                    break;

                case LoopInstruction loop:
                    CheckType(context, VarType(context, loop.Condition), ".bool", location,
                        "loop 条件", errors);
                    break;

                case SwitchInstruction switchInstruction:
                    // §16.6/§19.4：表必须是 switch-table 资源、属于本模块、
                    // selector 类型 ≡ 表元素类型、表项数与 block 表一致
                    if (!context.Module.ResourceSet.Contains(switchInstruction.Table))
                    {
                        errors.Add(new BilVerificationError("21.2", location,
                            $"switch 引用的资源 \"{switchInstruction.Table.Name}\" 不属于本模块"));
                    }
                    if (switchInstruction.Table is not BilSwitchTableResource switchTable)
                    {
                        errors.Add(new BilVerificationError("21.3", location,
                            $"switch 的表资源 \"{switchInstruction.Table.Name}\" 不是 switch-table"));
                    }
                    else
                    {
                        CheckType(context, VarType(context, switchInstruction.Selector),
                            switchTable.SelectorTypeRef, location, "switch selector", errors);
                        if (switchTable.Elements.Count != switchInstruction.ItemBlocks.Count)
                        {
                            errors.Add(new BilVerificationError("21.3", location,
                                $"switch-table 元素数 {switchTable.Elements.Count} 与 item block 数 " +
                                $"{switchInstruction.ItemBlocks.Count} 不一致"));
                        }
                    }
                    break;

                case TryInstruction tryInstruction:
                    // §16.7/§19.5：表必须是 catch-table 资源、属于本模块、
                    // 条目异常类型可解析且兼容 core::Exception
                    if (!context.Module.ResourceSet.Contains(tryInstruction.CatchTable))
                    {
                        errors.Add(new BilVerificationError("21.2", location,
                            $"try 引用的资源 \"{tryInstruction.CatchTable.Name}\" 不属于本模块"));
                    }
                    if (tryInstruction.CatchTable is not BilCatchTableResource catchTable)
                    {
                        errors.Add(new BilVerificationError("21.3", location,
                            $"try 的表资源 \"{tryInstruction.CatchTable.Name}\" 不是 catch-table"));
                    }
                    else
                    {
                        foreach (var entry in catchTable.Entries)
                        {
                            VerifyResolvableType(context, entry.ExceptionType.TypeRef, location, errors);
                            if (!IsExceptionCompatible(context, entry.ExceptionType.TypeRef))
                            {
                                errors.Add(new BilVerificationError("21.3", location,
                                    $"catch 类型 \"{entry.ExceptionType.TypeRef}\" 不兼容 core::Exception"));
                            }
                        }
                    }
                    break;

                case ThrowInstruction throwInstruction:
                    var exceptionType = VarType(context, throwInstruction.Exception);
                    if (exceptionType != null && !IsExceptionCompatible(context, exceptionType))
                    {
                        errors.Add(new BilVerificationError("21.3", location,
                            $"throw 值类型 \"{exceptionType}\" 不兼容 core::Exception"));
                    }
                    break;

                case BreakInstruction:
                case ContinueInstruction:
                case CallBlockInstruction:
                    break;   // 无类型规则（capability 检查在 Flow.cs）

                default:
                    errors.Add(new BilVerificationError("21.3", location,
                        $"验证器未覆盖指令类型 {instruction.GetType().Name}——请扩展 BilVerifier"));
                    break;
            }
        }

        // ===== 指令级辅助 =====

        private static void VerifyAwait(BilFunctionContext context,
            AwaitInstruction instruction, string location, List<BilVerificationError> errors)
        {
            var taskType = VarType(context, instruction.Task);
            if (taskType == null) return;
            var normalizedTask = BilVerificationContext.NormalizeTypeRef(taskType);
            const string taskHead = "core.coroutine::Task";
            if (normalizedTask == taskHead)
            {
                if (instruction.Result != null)
                {
                    errors.Add(new BilVerificationError("21.3", location,
                        "await core.coroutine::Task 不得带 RESULT"));
                }
                return;
            }
            if (!normalizedTask.StartsWith(taskHead + "<", StringComparison.Ordinal)
                || !normalizedTask.EndsWith(">"))
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"await TASK 类型必须是精确 core.coroutine::Task 或 Task<T>，实际 \"{taskType}\""));
                return;
            }
            var resultArguments = BilVerificationContext.SplitTopLevel(
                normalizedTask.Substring(taskHead.Length + 1,
                    normalizedTask.Length - taskHead.Length - 2));
            if (resultArguments.Count != 1 || instruction.Result == null)
            {
                if (instruction.Result == null)
                {
                    errors.Add(new BilVerificationError("21.3", location,
                        "await Task<T> 必须带 RESULT"));
                }
                return;
            }
            var actualResultType = VarType(context, instruction.Result);
            if (actualResultType != null
                && !BilVerificationContext.TypesCompatible(actualResultType, resultArguments[0]))
            {
                    errors.Add(new BilVerificationError("21.3", location,
                        $"await RESULT 类型必须严格等于 Task<T> 的 T：实际 \"{actualResultType}\"，" +
                        $"期望 \"{resultArguments[0]}\""));
            }
        }

        private static void VerifyYield(BilFunctionContext context,
            YieldInstruction instruction, string location, List<BilVerificationError> errors)
        {
            if (instruction.Alarm == null) return;
            var alarmType = VarType(context, instruction.Alarm);
            if (alarmType == null) return;
            if (!IsAlarmCompatible(context, alarmType, "core.coroutine::PollingAlarm")
                && !IsAlarmCompatible(context, alarmType, "core.coroutine::EventAlarm"))
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"yield ALARM 类型必须可赋值到 PollingAlarm 或 EventAlarm，实际 \"{alarmType}\""));
            }
        }

        private static bool IsAlarmCompatible(BilFunctionContext context, string actual,
            string expected)
        {
            // TypesAssignable supplies the nominal/variance check. If either side's
            // declaration is absent, the inheritance graph is incomplete and this
            // check must degrade without manufacturing a verifier error.
            if (context.Module.TypesAssignable(actual, expected)) return true;
            var actualKnown = context.Module.IsResolvableTypeRef(actual)
                || context.Module.TryGetTypeDeclaration(actual, out _);
            if (!actualKnown
                || !context.Module.TryGetTypeDeclaration(expected, out _)) return true;
            return false;
        }

        // §15.3 间接调用（callable 协议）：CALL_TARGET 的静态类型（沿 extends
        // 链）必须声明与实参列表严格匹配的 $$call（operator(call) 成员，
        // canonical 名 $$call）；宿主泛型实参按 §6.4 严格口径代入候选签名。
        // M108：泛型 $$call 的 typeid/包前缀与 direct invoke 同构（§7.2 序）
        private static void VerifyIndirectInvoke(BilFunctionContext context,
            BilVariableOperand callTarget, IReadOnlyList<BilVariableOperand> arguments,
            BilVariableOperand? target, string location, List<BilVerificationError> errors)
        {
            var type = VarType(context, callTarget);
            if (type == null) return;
            if (!TryFindCallOperator(context, type, arguments, location, errors,
                    out var returnType, out var isAsync, out var found))
            {
                if (!found)
                {
                    errors.Add(new BilVerificationError("21.3", location,
                        $"invoke.indirect 目标类型 \"{type}\" 没有与实参列表匹配的 " +
                        "operator call 实现"));
                }
                return;
            }
            // 返回形态（§15.1/§15.2 同口径）：async call 恒有 Task 结果
            if (target == null)
            {
                if (returnType != ".void" || isAsync)
                {
                    errors.Add(new BilVerificationError("21.3", location,
                        "有返回（或 async）的 operator call 不得使用 invoke.indirect.noret"));
                }
                return;
            }
            var expectedResult = isAsync
                ? (returnType == ".void"
                    ? "core.coroutine::Task"
                    : $"core.coroutine::Task<{returnType}>")
                : returnType;
            if (returnType == ".void" && !isAsync)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    "无返回 operator call 必须使用 invoke.indirect.noret"));
                return;
            }
            CheckType(context, VarType(context, target), expectedResult, location,
                "invoke.indirect 结果", errors);
        }

        // 沿 extends 链查找与实参列表严格匹配的 $$call 成员（宿主泛型代入后
        // 逐实参 §6.4 全等比对；M108：泛型隐藏条目 typeid/包前缀同 §15.1
        // invoke 口径）。查不到声明（external 不完整）降级为通过。
        // 返回 false 且 found=false = 无匹配候选；found=true 且 false =
        // 已命中候选但前缀形态错误（诊断已落袋）
        private static bool TryFindCallOperator(BilFunctionContext context, string objectTypeRef,
            IReadOnlyList<BilVariableOperand> arguments, string location,
            List<BilVerificationError> errors, out string returnType, out bool isAsync,
            out bool found)
        {
            returnType = ".void";
            isAsync = false;
            found = false;
            // 内建类型（.i32/.string 等别名形态）无声明表条目且确定无
            // operator call——直接判负（不走「查不到声明降级」通道）
            if (BilVerificationContext.IsBuiltinType(objectTypeRef)) return false;
            var current = BilVerificationContext.NormalizeTypeRef(objectTypeRef);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (visited.Add(current))
            {
                if (!context.Module.TryGetTypeDeclaration(current, out var declaration))
                {
                    found = true;
                    return true;   // 链断/查不到声明：降级（同 §13.6 口径）
                }
                foreach (var member in declaration.Members)
                {
                    if (member is not BilSimpleMemberDeclaration simple
                        || !simple.Modifiers.Any(m => m is BilOperatorModifier
                        {
                            Name: "call"
                        }))
                    {
                        continue;
                    }
                    if (!BilVerificationContext.TryParseMethodSymbol(simple.Symbol,
                            out _, out _, out var parameters, out var candidateReturn))
                    {
                        continue;
                    }
                    // 普通参数（符号段无 hidden）+ fn 定义侧 .generic.*/值包
                    // （同 §15.1 VerifyInvoke：无 fn 定义时 hidden=0）
                    var ordinary = new List<(string Name, string TypeRef)>();
                    foreach (var parameter in parameters)
                    {
                        if (parameter.Name.StartsWith(".generic.")
                            || parameter.Name.StartsWith(".vargs.")
                            || parameter.Name.StartsWith(".kwargs."))
                        {
                            continue;
                        }
                        ordinary.Add(parameter);
                    }
                    var genericHidden = new List<BilArgDeclaration>();
                    var packArguments = new List<BilArgDeclaration>();
                    var calleeDefinition = context.Module.Module.Functions
                        .FirstOrDefault(f => f.Symbol == simple.Symbol);
                    if (calleeDefinition != null)
                    {
                        foreach (var arg in calleeDefinition.Args)
                        {
                            if (arg.Name.StartsWith(".generic.")) genericHidden.Add(arg);
                            else if (arg.Name.StartsWith(".vargs.")
                                || arg.Name.StartsWith(".kwargs."))
                            {
                                packArguments.Add(arg);
                            }
                        }
                    }
                    var expectedCount = genericHidden.Count + ordinary.Count + packArguments.Count;
                    if (arguments.Count != expectedCount) continue;
                    // 前缀：固定 .generic.T = .typeid / 包 = .array|.map 形态
                    for (var i = 0; i < genericHidden.Count; i++)
                    {
                        var actualType = VarType(context, arguments[i]) ?? "";
                        if (!BilVerificationContext.TypesCompatible(actualType,
                                genericHidden[i].TypeRef))
                        {
                            errors.Add(new BilVerificationError("21.3", location,
                                $"invoke.indirect 泛型隐藏实参 {i} 类型 \"{actualType}\" " +
                                $"与 \"{genericHidden[i].Name}: {genericHidden[i].TypeRef}\" 不匹配"));
                            found = true;
                            return false;
                        }
                    }
                    var valueStart = genericHidden.Count;
                    var matches = true;
                    for (var i = 0; i < ordinary.Count; i++)
                    {
                        var expected = SubstituteHostGenerics(ordinary[i].TypeRef,
                            declaration, current);
                        if (!BilVerificationContext.TypesCompatible(
                                VarType(context, arguments[valueStart + i]) ?? "", expected))
                        {
                            matches = false;
                            break;
                        }
                    }
                    if (!matches) continue;
                    for (var i = 0; i < packArguments.Count; i++)
                    {
                        var packIndex = valueStart + ordinary.Count + i;
                        if (!BilVerificationContext.TypesCompatible(
                                VarType(context, arguments[packIndex]) ?? "",
                                packArguments[i].TypeRef))
                        {
                            matches = false;
                            break;
                        }
                    }
                    if (!matches) continue;
                    returnType = SubstituteHostGenerics(candidateReturn, declaration, current);
                    isAsync = simple.Modifiers.Any(m =>
                        m is BilKeywordModifier { Keyword: BilKeyword.Async });
                    found = true;
                    return true;
                }
                if (declaration.ExtendsType == null) return false;
                current = BilVerificationContext.NormalizeTypeRef(declaration.ExtendsType);
            }
            return false;
        }

        // 宿主代入辅助：receiver 在 owner 定义处的构造形态解析 +
        // 声明查表（一次性出两个产物）；查不到声明/链断返回 false（降级）
        private static bool TryGetHostSubstitution(BilFunctionContext context,
            string? receiverTypeRef, string ownerRef, out BilTypeDeclaration declaration,
            out string hostForm)
        {
            declaration = null!;
            hostForm = "";
            if (receiverTypeRef == null) return false;
            var resolved = context.Module.ResolveConstructedHostForm(receiverTypeRef, ownerRef);
            if (resolved == null) return false;
            if (!context.Module.TryGetTypeDeclaration(resolved, out declaration)) return false;
            hostForm = resolved;
            return true;
        }

        // 宿主泛型代入：成员签名中的 .generic<$.generic.<名>> 按宿主构造实参
        // 替换（声明的 GenericParameters 名序 ↔ 宿主类型实参序；无实参的
        // 非泛型宿主恒等返回）
        private static string SubstituteHostGenerics(string typeRef,
            BilTypeDeclaration declaration, string hostTypeRef)
        {
            if (declaration.GenericParameters.Count == 0
                || !typeRef.Contains(".generic<", StringComparison.Ordinal))
            {
                return typeRef;
            }
            var angle = hostTypeRef.IndexOf('<');
            if (angle < 0 || !hostTypeRef.EndsWith(">", StringComparison.Ordinal))
            {
                return typeRef;
            }
            var arguments = BilVerificationContext.SplitTopLevel(hostTypeRef.Substring(
                angle + 1, hostTypeRef.Length - angle - 2));
            if (arguments.Count != declaration.GenericParameters.Count) return typeRef;
            var result = typeRef;
            for (var i = 0; i < arguments.Count; i++)
            {
                result = result.Replace(
                    ".generic<$.generic." + declaration.GenericParameters[i] + ">",
                    arguments[i], StringComparison.Ordinal);
            }
            return result;
        }


        private static void VerifyInvoke(BilFunctionContext context, string methodSymbol,
            IReadOnlyList<BilVariableOperand> arguments, BilVariableOperand? target,
            string location, List<BilVerificationError> errors)
        {
            // §15.4：保留目标不是 canonical 方法，不得进入方法符号/receiver 校验。
            if (methodSymbol == BilSpellings.InnerReservedFunction)
            {
                VerifyInnerInvoke(context, arguments, target, location, errors);
                return;
            }
            if (methodSymbol == BilSpellings.SuperReservedFunction)
            {
                VerifySuperInvoke(context, arguments, target, location, errors);
                return;
            }
            // §21.2：方法符号可解析
            if (!context.Module.MethodSymbols.Contains(methodSymbol))
            {
                errors.Add(new BilVerificationError("21.2", location,
                    $"invoke 的方法符号不可解析 \"{methodSymbol}\""));
            }
            if (!BilVerificationContext.TryParseMethodSymbol(methodSymbol,
                    out var owner, out var isStatic, out var parameters, out var returnType))
            {
                errors.Add(new BilVerificationError("21.1", location,
                    $"invoke 的方法符号不符合 canonical 语法 \"{methodSymbol}\""));
                return;
            }
            // 泛型宿主成员签名代入（§6.3 构造头类型的成员调用——
            // core::Cell\<T\>.getValue/setValue 等）：在任何返回形态/实参
            // 比对之前，按 receiver 首实参在 owner 定义处的构造形态把签名
            // 中的 .generic<$.generic.*> 替换为构造实参；非泛型宿主代入恒等
            if (!isStatic && owner.Length > 0 && !owner.EndsWith("::")
                && arguments.Count > 0
                && TryGetHostSubstitution(context, VarType(context, arguments[0]), owner,
                    out var hostDeclaration, out var hostForm))
            {
                returnType = SubstituteHostGenerics(returnType, hostDeclaration, hostForm);
                for (var i = 0; i < parameters.Count; i++)
                {
                    parameters[i] = (parameters[i].Name,
                        SubstituteHostGenerics(parameters[i].TypeRef, hostDeclaration, hostForm));
                }
            }

            // §15.1：返回形态匹配——有返回用 invoke，无返回用 invoke.noret。
            // S10（§15.2）：async 方法调用恒有 Task 结果（无结果 async 也是
            // core.coroutine.Task）——两种检查均豁免 async
            var isAsync = context.Module.MethodDeclarations.TryGetValue(methodSymbol,
                out var calleeDeclaration)
                && calleeDeclaration.Modifiers.Any(m =>
                    m is BilKeywordModifier { Keyword: BilKeyword.Async });
            if (target != null && returnType == ".void" && !isAsync)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"无返回方法 \"{methodSymbol}\" 必须使用 invoke.noret"));
            }
            if (target == null && returnType != ".void")
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"有返回方法 \"{methodSymbol}\" 不得使用 invoke.noret"));
            }
            if (target != null && returnType != ".void")
            {
                // §15.2：async 有结果方法调用结果 = core.coroutine.Task\<TResult\>
                var expectedResultType = isAsync
                    ? $"core.coroutine::Task<{returnType}>" : returnType;
                CheckType(context, VarType(context, target), expectedResultType, location,
                    "invoke 结果", errors);
            }
            else if (target != null && isAsync)
            {
                // §15.2：async 无结果方法调用结果 = core.coroutine.Task
                CheckType(context, VarType(context, target), "core.coroutine::Task", location,
                    "invoke 结果", errors);
            }

            // §15.1：实参 ≡ 规范签名（§7.2 调用序：.this → .generic.*（固定
            // 泛型 + 泛型包）→ 普通参数逐项 → .vargs 包 → .kwargs 包；符号中
            // hidden 形态的参数跳过比对）。
            // S9e：泛型隐藏参数（.generic.*）已产出——取被调 fn 定义的 .args
            // 隐藏条目数，receiver 后前导跳过相同个数；S9d：值包（.vargs/
            // .kwargs）在普通参数之后逐条比对（无 fn 定义的 native/external
            // 无隐藏参数，降级 0）
            var expected = new List<(string Name, string TypeRef)>();
            foreach (var parameter in parameters)
            {
                if (parameter.Name.StartsWith(".generic.") || parameter.Name.StartsWith(".vargs.")
                    || parameter.Name.StartsWith(".kwargs."))
                {
                    continue;
                }
                expected.Add(parameter);
            }
            var genericHiddenCount = 0;
            var packArguments = new List<BilArgDeclaration>();
            var calleeDefinition = context.Module.Module.Functions
                .FirstOrDefault(f => f.Symbol == methodSymbol);
            if (calleeDefinition != null)
            {
                genericHiddenCount = calleeDefinition.Args.Count(a => a.Name.StartsWith(".generic."));
                foreach (var arg in calleeDefinition.Args)
                {
                    if (arg.Name.StartsWith(".vargs.") || arg.Name.StartsWith(".kwargs."))
                    {
                        packArguments.Add(arg);
                    }
                }
            }
            var argumentIndex = 0;
            // owner 段以 "::" 结尾的是命名空间前缀（全局函数），无 receiver
            // （与 §21.7 fn 定义侧判定一致）
            if (!isStatic && owner.Length > 0 && !owner.EndsWith("::"))
            {
                if (arguments.Count == 0)
                {
                    errors.Add(new BilVerificationError("21.3", location,
                        $"实例方法 \"{methodSymbol}\" 的 invoke 缺少 receiver 首实参"));
                    return;
                }
                CheckHostAssignable(context, VarType(context, arguments[0]), owner, location,
                    "invoke receiver(.this)", errors);
                argumentIndex = 1;
            }
            if (arguments.Count - argumentIndex - genericHiddenCount
                != expected.Count + packArguments.Count)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"invoke 实参个数 {arguments.Count - argumentIndex} 与方法 \"{methodSymbol}\" " +
                    $"签名参数个数 {expected.Count}（含 {genericHiddenCount} 个泛型隐藏参数与 " +
                    $"{packArguments.Count} 个值包）不一致"));
                return;
            }
            for (var i = 0; i < expected.Count; i++)
            {
                CheckType(context, VarType(context, arguments[argumentIndex + genericHiddenCount + i]),
                    expected[i].TypeRef, location, $"invoke 实参 {i}", errors);
            }
            // 末尾值包逐条比对（§7.1 类型形态以 fn 定义声明为准：
            // .vargs = .array<.any>、.kwargs = .array<.pair<.string, .any>>）
            for (var i = 0; i < packArguments.Count; i++)
            {
                CheckType(context,
                    VarType(context,
                        arguments[argumentIndex + genericHiddenCount + expected.Count + i]),
                    packArguments[i].TypeRef, location,
                    $"invoke 值包 \"{packArguments[i].Name}\"", errors);
            }
        }

        private static void VerifyNew(BilFunctionContext context, NewInstruction newInstruction,
            string location, List<BilVerificationError> errors)
        {
            var typeRef = newInstruction.Type.TypeRef;
            VerifyResolvableType(context, typeRef, location, errors);
            CheckType(context, VarType(context, newInstruction.Target), typeRef, location,
                "new 结果", errors);
            if (!context.Module.TryGetTypeDeclaration(typeRef, out var declaration))
            {
                return;   // 查不到声明（external 不完整）降级
            }
            // §21.8：abstract 不被构造；enum-struct 不走普通 new
            if (HasKeyword(declaration.Modifiers, BilKeyword.Abstract))
            {
                errors.Add(new BilVerificationError("21.8", location,
                    $"abstract 类型 \"{typeRef}\" 不得被 new 构造"));
            }
            if (declaration.Kind == BilTypeKind.EnumStruct)
            {
                errors.Add(new BilVerificationError("21.8", location,
                    $"enum-struct \"{typeRef}\" 不得走普通 new（应使用 new.case）"));
            }
            // §14.4：有参 ..init.wrapper 的类型必须用 new.wrapped
            if (TryGetInitWrapperParameters(declaration, typeRef, out var wrapperParams)
                && wrapperParams.Count > 0)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"类型 \"{typeRef}\" 的 ..init.wrapper 有参数，必须使用 new.wrapped（§14.4）"));
                return;
            }
            // §14.1：参数必须严格匹配唯一 init（无 init 声明时只允许无参构造）
            if (!MatchAnyInit(context, declaration, typeRef, newInstruction.Arguments))
            {
                if (CollectInits(declaration).Count == 0)
                {
                    if (newInstruction.Arguments.Count > 0)
                    {
                        errors.Add(new BilVerificationError("21.3", location,
                            $"类型 \"{typeRef}\" 没有 init 声明，不得带参数构造"));
                    }
                }
                else
                {
                    errors.Add(new BilVerificationError("21.3", location,
                        $"new \"{typeRef}\" 的实参不匹配任何 init 签名"));
                }
            }
        }

        // §14.4.1 new.wrapped type(T) RESULT [WRAPPER_ARGS] [INIT_ARGS]
        private static void VerifyNewWrapped(BilFunctionContext context,
            NewWrappedInstruction instruction, string location, List<BilVerificationError> errors)
        {
            var typeRef = instruction.Type.TypeRef;
            VerifyResolvableType(context, typeRef, location, errors);
            CheckType(context, VarType(context, instruction.Target), typeRef, location,
                "new.wrapped 结果", errors);
            if (!context.Module.TryGetTypeDeclaration(typeRef, out var declaration))
            {
                return;
            }
            if (HasKeyword(declaration.Modifiers, BilKeyword.Abstract))
            {
                errors.Add(new BilVerificationError("21.8", location,
                    $"abstract 类型 \"{typeRef}\" 不得被 new.wrapped 构造"));
            }
            if (declaration.Kind == BilTypeKind.EnumStruct)
            {
                errors.Add(new BilVerificationError("21.8", location,
                    $"enum-struct \"{typeRef}\" 不得走 new.wrapped（应使用 new.wrapped.case）"));
            }
            if (!TryGetInitWrapperParameters(declaration, typeRef, out var wrapperParams)
                || wrapperParams.Count == 0)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"类型 \"{typeRef}\" 无有参 ..init.wrapper，禁止 new.wrapped（§14.4）"));
                return;
            }
            if (!SignatureMatches(context, wrapperParams, instruction.WrapperArguments))
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"new.wrapped \"{typeRef}\" 的 wrapper 前缀实参不匹配 ..init.wrapper 签名"));
            }
            if (!MatchAnyInit(context, declaration, typeRef, instruction.InitArguments))
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"new.wrapped \"{typeRef}\" 的 init 实参不匹配任何 init 签名"));
            }
        }

        // §14.4.2 new.wrapped.case
        private static void VerifyNewWrappedCase(BilFunctionContext context,
            NewWrappedCaseInstruction instruction, string location,
            List<BilVerificationError> errors)
        {
            var typeRef = instruction.Type.TypeRef;
            VerifyResolvableType(context, typeRef, location, errors);
            CheckType(context, VarType(context, instruction.Target), typeRef, location,
                "new.wrapped.case 结果", errors);
            if (!context.Module.TryGetTypeDeclaration(typeRef, out var declaration))
            {
                return;
            }
            if (declaration.Kind != BilTypeKind.EnumStruct)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"new.wrapped.case 的目标 \"{typeRef}\" 不是 enum-struct"));
            }
            if (!TryGetInitWrapperParameters(declaration, typeRef, out var wrapperParams)
                || wrapperParams.Count == 0)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"类型 \"{typeRef}\" 无有参 ..init.wrapper，禁止 new.wrapped.case（§14.4）"));
                return;
            }
            if (!SignatureMatches(context, wrapperParams, instruction.WrapperArguments))
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"new.wrapped.case \"{typeRef}\" 的 wrapper 前缀实参不匹配 ..init.wrapper 签名"));
            }
            // case 实参：复用 new.case 校验路径的结构（case 可解析 + 洞签名）
            VerifyNewCaseArguments(context, instruction.Case.QualifiedName, typeRef,
                instruction.CaseArguments, location, "new.wrapped.case", errors);
        }

        private static void VerifyNewWrapperField(BilFunctionContext context,
            NewWrapperFieldInstruction instruction, string location,
            List<BilVerificationError> errors)
        {
            if (!RequireInitWrapperBody(context, location, "new.wrapper.field", errors))
            {
                return;
            }
            VerifyWrapperType(context, instruction.WrapperType.TypeRef, location, errors);
            VerifyWrapperInitArguments(context, instruction.WrapperType.TypeRef,
                instruction.Arguments, location, "new.wrapper.field", errors);
            var fieldSymbol = instruction.Field.Symbol;
            if (!context.Module.FieldSymbols.Contains(fieldSymbol))
            {
                errors.Add(new BilVerificationError("21.2", location,
                    $"new.wrapper.field 的字段符号不可解析 \"{fieldSymbol}\""));
                return;
            }
            if (!BilVerificationContext.TryParseFieldSymbol(fieldSymbol,
                    out _, out var isStatic, out _))
            {
                return;
            }
            if (isStatic)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"new.wrapper.field 的字段 \"{fieldSymbol}\" 必须是实例字段"));
            }
            if (!context.Module.FieldDeclarations.TryGetValue(fieldSymbol, out var fieldDecl)
                || !MemberHasWrapped(fieldDecl, instruction.WrapperType.TypeRef))
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"new.wrapper.field 的字段 \"{fieldSymbol}\" 必须带 wrapped(" +
                    $"{instruction.WrapperType.TypeRef})（§14.5）"));
            }
        }

        private static void VerifyNewWrapperMethod(BilFunctionContext context,
            NewWrapperMethodInstruction instruction, string location,
            List<BilVerificationError> errors)
        {
            if (!RequireInitWrapperBody(context, location, "new.wrapper.method", errors))
            {
                return;
            }
            VerifyWrapperType(context, instruction.WrapperType.TypeRef, location, errors);
            VerifyWrapperInitArguments(context, instruction.WrapperType.TypeRef,
                instruction.Arguments, location, "new.wrapper.method", errors);
            var methodSymbol = instruction.Method.Symbol;
            if (!context.Module.MethodSymbols.Contains(methodSymbol)
                && methodSymbol != BilSpellings.InnerReservedFunction
                && methodSymbol != BilSpellings.SuperReservedFunction)
            {
                errors.Add(new BilVerificationError("21.2", location,
                    $"new.wrapper.method 的方法符号不可解析 \"{methodSymbol}\""));
            }
        }

        private static void VerifyNewWrapperEntity(BilFunctionContext context,
            NewWrapperEntityInstruction instruction, string location,
            List<BilVerificationError> errors)
        {
            if (!RequireInitWrapperBody(context, location, "new.wrapper.entity", errors))
            {
                return;
            }
            VerifyWrapperType(context, instruction.WrapperType.TypeRef, location, errors);
            VerifyWrapperInitArguments(context, instruction.WrapperType.TypeRef,
                instruction.Arguments, location, "new.wrapper.entity", errors);
            // 宿主类型声明须带 wrapped(W)
            if (BilVerificationContext.TryParseMethodSymbol(context.Function.Symbol,
                    out var owner, out _, out _, out _)
                && context.Module.TryGetTypeDeclaration(owner, out var hostDecl)
                && !DeclarationHasWrapped(hostDecl, instruction.WrapperType.TypeRef))
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"new.wrapper.entity 要求宿主类型 \"{owner}\" 带 wrapped(" +
                    $"{instruction.WrapperType.TypeRef})（§14.5）"));
            }
        }

        private static bool RequireInitWrapperBody(BilFunctionContext context, string location,
            string opcode, List<BilVerificationError> errors)
        {
            var name = MethodNameSegment(context.Function.Symbol);
            if (name == BilSpellings.InitWrapperMethodName)
            {
                return true;
            }
            errors.Add(new BilVerificationError("21.3", location,
                $"{opcode} 仅允许在 ..init.wrapper fn 体内（§14.5）"));
            return false;
        }

        private static void VerifyWrapperInitArguments(BilFunctionContext context,
            string wrapperTypeRef, IReadOnlyList<BilVariableOperand> arguments,
            string location, string opcode, List<BilVerificationError> errors)
        {
            if (!context.Module.TryGetTypeDeclaration(wrapperTypeRef, out var declaration))
            {
                return;
            }
            if (!MatchAnyInit(context, declaration, wrapperTypeRef, arguments))
            {
                if (CollectInits(declaration).Count == 0)
                {
                    if (arguments.Count > 0)
                    {
                        errors.Add(new BilVerificationError("21.3", location,
                            $"{opcode}：wrapper \"{wrapperTypeRef}\" 无 init，不得带参数"));
                    }
                }
                else
                {
                    errors.Add(new BilVerificationError("21.3", location,
                        $"{opcode} 的实参不匹配 wrapper \"{wrapperTypeRef}\" 的任何 init 签名"));
                }
            }
        }

        private static List<BilSimpleMemberDeclaration> CollectInits(BilTypeDeclaration declaration)
        {
            var inits = new List<BilSimpleMemberDeclaration>();
            foreach (var member in declaration.Members)
            {
                if (member is BilSimpleMemberDeclaration simple && HasKeyword(simple, BilKeyword.Init))
                {
                    inits.Add(simple);
                }
            }
            return inits;
        }

        private static bool MatchAnyInit(BilFunctionContext context, BilTypeDeclaration declaration,
            string typeRef, IReadOnlyList<BilVariableOperand> arguments)
        {
            var inits = CollectInits(declaration);
            if (inits.Count == 0)
            {
                return arguments.Count == 0;
            }
            foreach (var init in inits)
            {
                if (!BilVerificationContext.TryParseMethodSymbol(init.Symbol,
                        out _, out _, out var parameters, out _))
                {
                    continue;
                }
                for (var i = 0; i < parameters.Count; i++)
                {
                    parameters[i] = (parameters[i].Name,
                        SubstituteHostGenerics(parameters[i].TypeRef, declaration, typeRef));
                }
                if (SignatureMatches(context, parameters, arguments))
                {
                    return true;
                }
            }
            return false;
        }

        // 查找类型上的 ..init.wrapper 并解析参数表（代入宿主泛型）
        private static bool TryGetInitWrapperParameters(BilTypeDeclaration declaration,
            string typeRef, out List<(string Name, string TypeRef)> parameters)
        {
            parameters = new List<(string, string)>();
            BilSimpleMemberDeclaration? found = null;
            foreach (var member in declaration.Members)
            {
                if (member is not BilSimpleMemberDeclaration simple) continue;
                if (MethodNameSegment(simple.Symbol) != BilSpellings.InitWrapperMethodName)
                {
                    continue;
                }
                found = simple;
                break;
            }
            if (found == null)
            {
                return false;
            }
            if (!BilVerificationContext.TryParseMethodSymbol(found.Symbol,
                    out _, out _, out parameters, out _))
            {
                return false;
            }
            for (var i = 0; i < parameters.Count; i++)
            {
                parameters[i] = (parameters[i].Name,
                    SubstituteHostGenerics(parameters[i].TypeRef, declaration, typeRef));
            }
            return true;
        }

        private static bool MemberHasWrapped(BilSimpleMemberDeclaration declaration,
            string wrapperTypeRef)
        {
            foreach (var modifier in declaration.Modifiers)
            {
                if (modifier is BilWrappedModifier wrapped
                    && BilVerificationContext.TypesCompatible(wrapped.WrapperTypeRef,
                        wrapperTypeRef))
                {
                    return true;
                }
            }
            return false;
        }

        private static void VerifyNewCaseArguments(BilFunctionContext context,
            string caseQualifiedName, string enumTypeRef,
            IReadOnlyList<BilVariableOperand> arguments, string location, string opcode,
            List<BilVerificationError> errors)
        {
            if (!context.Module.CaseDeclarations.TryGetValue(caseQualifiedName, out var caseDecl))
            {
                // 宽松：有的模块用完整符号索引
                foreach (var kv in context.Module.CaseDeclarations)
                {
                    if (kv.Key == caseQualifiedName || kv.Value.QualifiedName == caseQualifiedName)
                    {
                        caseDecl = kv.Value;
                        break;
                    }
                }
            }
            if (caseDecl == null)
            {
                errors.Add(new BilVerificationError("21.2", location,
                    $"{opcode} 的 case 符号不可解析 \"{caseQualifiedName}\""));
                return;
            }
            var expected = new List<(string Name, string TypeRef)>();
            foreach (var p in caseDecl.Parameters)
            {
                expected.Add((p.Name, p.TypeRef));
            }
            if (!SignatureMatches(context, expected, arguments))
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"{opcode} 的 case 实参不匹配 case 洞签名"));
            }
        }

        // §13.6 非数组索引：严格三元组查询（collection + index + result/
        // element）——在 collection 类型上（含 extends 链宿主回退，与
        // invoke 的宿主判定同机制；§8.4.1 ext 裸条目经 canonical 符号
        // 解析归位宿主）查 $$getAtIndex（恰 1 参数）/ $$setAtIndex
        // （恰 2 参数）实现：无候选 → 报错（§13.6 必须存在唯一精确
        // 实现）；有候选但索引（与元素）类型无 §6.4 严格相等匹配 →
        // 报错（禁止隐式转换）；多个精确命中 → 报不唯一；get 唯一命中
        // 后再验 RESULT ≡ 返回类型。
        // collection 类型查不到声明或 extends 链断（内建别名投影、
        // external 不完整）→ 降级通过（防误报原则）
        private static void VerifyIndexOperator(BilFunctionContext context, string collectionType,
            string? indexType, string? valueType, bool isGet, string location,
            List<BilVerificationError> errors)
        {
            if (indexType == null || valueType == null)
            {
                return;   // 变量未声明等上游已报错，跳过
            }
            var opcode = isGet ? "get.array" : "set.array";
            var operatorName = isGet ? "getAtIndex" : "setAtIndex";
            var arity = isGet ? 1 : 2;
            if (!context.Module.TryGetTypeDeclaration(collectionType, out var declaration))
            {
                return;   // 查不到声明（内建别名投影/external 不完整）降级
            }
            // extends 链（含自身）：链断则宿主回退不可信，降级；继承环防
            // 死循环（环本身由声明侧检查另报）
            var chain = new HashSet<string>();
            var current = declaration;
            while (true)
            {
                chain.Add(current.Symbol);
                if (current.ExtendsType == null)
                {
                    break;
                }
                if (!context.Module.TryGetTypeDeclaration(current.ExtendsType,
                        out var baseDeclaration))
                {
                    return;   // 链断（external 不完整）降级
                }
                if (chain.Contains(baseDeclaration.Symbol))
                {
                    break;
                }
                current = baseDeclaration;
            }
            // 候选：宿主 ∈ extends 链、名匹配（$$名 运算符 canonical）、
            // 参数个数恰为 arity（§13.2 签名固定单 TIndex）
            var candidates = new List<(List<(string Name, string TypeRef)> Parameters,
                string ReturnType)>();
            foreach (var (ownerType, member, _) in context.Module.MemberEntries)
            {
                if (member.Kind is not (BilMemberKind.Method or BilMemberKind.StaticMethod)
                    || !member.Symbol.Contains("$$" + operatorName + "("))
                {
                    continue;
                }
                if (!BilVerificationContext.TryParseMethodSymbol(member.Symbol,
                        out var parsedOwner, out _, out var parameters, out var returnType))
                {
                    continue;   // malformed 由符号检查另报，此处跳过
                }
                var host = ownerType ?? BilVerificationContext.StripTypeArguments(parsedOwner);
                var hostInChain = false;
                foreach (var chainType in chain)
                {
                    if (BilVerificationContext.TypesCompatible(chainType, host))
                    {
                        hostInChain = true;
                        break;
                    }
                }
                if (hostInChain && parameters.Count == arity)
                {
                    candidates.Add((parameters, returnType));
                }
            }
            if (candidates.Count == 0)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"类型 \"{collectionType}\" 没有 {operatorName} 索引运算符实现" +
                    $"（{opcode} 要求唯一精确实现）"));
                return;
            }
            // 精确匹配（§6.4 严格相等经 TypesCompatible 判定——内建别名
            // 投影与 .generic< 位置的降级已含其中）：索引 ≡ param[0]，
            // set 再要求元素 ≡ param[1]
            var exact = new List<(List<(string Name, string TypeRef)> Parameters,
                string ReturnType)>();
            foreach (var candidate in candidates)
            {
                if (!BilVerificationContext.TypesCompatible(indexType,
                        candidate.Parameters[0].TypeRef))
                {
                    continue;
                }
                if (!isGet && !BilVerificationContext.TypesCompatible(valueType,
                        candidate.Parameters[1].TypeRef))
                {
                    continue;
                }
                exact.Add(candidate);
            }
            if (exact.Count == 0)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"{opcode} 的操作数在 \"{collectionType}\" 的 {operatorName} 实现中无精确匹配" +
                    $"（禁止隐式转换）：索引 \"{indexType}\"" +
                    (isGet ? "" : $"，元素 \"{valueType}\"")));
                return;
            }
            if (exact.Count > 1)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"{opcode} 在 \"{collectionType}\" 上命中多个精确 {operatorName} 实现" +
                    "（必须唯一）"));
                return;
            }
            if (isGet)
            {
                CheckType(context, valueType, exact[0].ReturnType, location,
                    "get.array 目标变量", errors);
            }
        }

        private static void VerifyNewCase(BilFunctionContext context, NewCaseInstruction newCase,
            string location, List<BilVerificationError> errors)
        {
            var typeRef = newCase.Type.TypeRef;
            VerifyResolvableType(context, typeRef, location, errors);
            CheckType(context, VarType(context, newCase.Target), typeRef, location,
                "new.case 结果", errors);
            // §14.3：ENUM_TYPE 必须是 enum-struct 且 case owner 一致
            if (context.Module.TryGetTypeDeclaration(typeRef, out var declaration))
            {
                if (declaration.Kind != BilTypeKind.EnumStruct)
                {
                    errors.Add(new BilVerificationError("21.3", location,
                        $"new.case 目标类型 \"{typeRef}\" 不是 enum-struct"));
                }
                // §14.4：有参 ..init.wrapper 必须用 new.wrapped.case
                if (TryGetInitWrapperParameters(declaration, typeRef, out var wrapperParams)
                    && wrapperParams.Count > 0)
                {
                    errors.Add(new BilVerificationError("21.3", location,
                        $"类型 \"{typeRef}\" 的 ..init.wrapper 有参数，必须使用 new.wrapped.case（§14.4）"));
                    return;
                }
            }
            if (!newCase.Case.QualifiedName.StartsWith(typeRef + "."))
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"new.case 的 case \"{newCase.Case.QualifiedName}\" 不属于类型 \"{typeRef}\""));
            }
            if (!context.Module.CaseDeclarations.TryGetValue(newCase.Case.QualifiedName, out var caseDeclaration))
            {
                errors.Add(new BilVerificationError("21.2", location,
                    $"new.case 的 case 符号不可解析 \"{newCase.Case.QualifiedName}\""));
                return;
            }
            // case 参数签名匹配
            if (caseDeclaration.Parameters.Count != newCase.Arguments.Count)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"new.case 实参个数与 case 声明参数个数不一致"));
                return;
            }
            for (var i = 0; i < caseDeclaration.Parameters.Count; i++)
            {
                CheckType(context, VarType(context, newCase.Arguments[i]),
                    caseDeclaration.Parameters[i].TypeRef, location, $"new.case 实参 {i}", errors);
            }
        }

        // §21.3：type.is.case 校验（§12.3，S11）——case 符号可解析且以
        // enum-struct 为 owner；VALUE 静态类型严格等于 case 的 enum 类型
        // （目标 .bool 由调用方 CheckType 断言）
        private static void VerifyCaseCheck(BilFunctionContext context,
            IsCaseInstruction instruction, string location, List<BilVerificationError> errors)
        {
            if (!context.Module.CaseDeclarations.TryGetValue(instruction.Case.QualifiedName,
                    out _))
            {
                errors.Add(new BilVerificationError("21.2", location,
                    $"type.is.case 的 case 符号不可解析 \"{instruction.Case.QualifiedName}\""));
                return;
            }
            // case 全名最后一段是 CaseName，前缀即 enum 类型（canonical 中
            // 命名空间/嵌套类型统一以 ::/. 分段，从最后一个 . 切分）
            var lastDot = instruction.Case.QualifiedName.LastIndexOf('.');
            if (lastDot <= 0)
            {
                errors.Add(new BilVerificationError("21.2", location,
                    $"type.is.case 的 case 符号缺少 enum 类型前缀 \"{instruction.Case.QualifiedName}\""));
                return;
            }
            var enumType = instruction.Case.QualifiedName.Substring(0, lastDot);
            VerifyResolvableType(context, enumType, location, errors);
            if (context.Module.TryGetTypeDeclaration(enumType, out var declaration)
                && declaration.Kind != BilTypeKind.EnumStruct)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"type.is.case 的目标类型 \"{enumType}\" 不是 enum-struct"));
            }
            CheckType(context, VarType(context, instruction.Value), enumType, location,
                "type.is.case 的操作数", errors);
        }

        // §21.3：get.self / invoke fn(..inner) 仅在带 wrapper-proxy 的 fn 体内合法
        private static void VerifyProxyTemplateInstruction(BilFunctionContext context,
            string location, string opcode, List<BilVerificationError> errors)
        {
            if (!context.Module.MethodDeclarations.TryGetValue(context.Function.Symbol,
                    out var declaration))
            {
                // 查不到声明（builtin 宿主等）降级通过
                return;
            }
            foreach (var modifier in declaration.Modifiers)
            {
                if (modifier is BilWrapperProxyModifier) return;
            }
            errors.Add(new BilVerificationError("21.3", location,
                $"{opcode} 仅允许在带 wrapper-proxy 修饰符的 fn 体内（§12.5/§15.4）"));
        }

        // §15.4/#27⑦：invoke fn(..inner) 的泛型包操作数必须完整、按当前 fn .args
        // 声明序前置；固定 .generic.T（.typeid）不得混入。值实参从包前缀后开始。
        private static void VerifyInnerPackArguments(BilFunctionContext context,
            IReadOnlyList<BilVariableOperand> arguments, string location,
            List<BilVerificationError> errors)
        {
            var expectedPacks = context.Function.Args
                .Where(a => a.Name.StartsWith(".generic.") && IsGenericPackType(a.TypeRef))
                .ToList();
            for (var i = 0; i < expectedPacks.Count; i++)
            {
                if (i >= arguments.Count || arguments[i].Name != expectedPacks[i].Name)
                {
                    var actual = i < arguments.Count ? "$" + arguments[i].Name : "<missing>";
                    errors.Add(new BilVerificationError("21.3", location,
                        $"invoke fn(..inner) 泛型包操作数 {i} 应为 \"${expectedPacks[i].Name}\"" +
                        $"（按 .args 声明序前置），实际为 \"{actual}\""));
                    continue;
                }
                CheckType(context, VarType(context, arguments[i]), expectedPacks[i].TypeRef,
                    location, "invoke fn(..inner) 泛型包操作数", errors);
            }
            for (var i = expectedPacks.Count; i < arguments.Count; i++)
            {
                if (!arguments[i].Name.StartsWith(".generic.")) continue;
                errors.Add(new BilVerificationError("21.3", location,
                    $"invoke fn(..inner) 的泛型包操作数 \"${arguments[i].Name}\" 必须按 " +
                    ".args 声明序前置，且固定泛型参数不得出现在参数列表中"));
            }
        }

        // §15.4：inner ABI 无 receiver；泛型包与值实参保持模板态原序，
        // Middleware 据此链接下一环。
        private static void VerifyInnerInvoke(BilFunctionContext context,
            IReadOnlyList<BilVariableOperand> arguments, BilVariableOperand? target,
            string location, List<BilVerificationError> errors)
        {
            VerifyProxyTemplateInstruction(context, location, "invoke fn(..inner)", errors);
            if (arguments.Any(argument => argument.Name == ".this"))
            {
                errors.Add(new BilVerificationError("21.3", location,
                    "invoke fn(..inner) 不接受 receiver（$.this）"));
            }
            VerifyInnerPackArguments(context, arguments, location, errors);
            if (context.ReturnType == ".void")
            {
                if (target != null)
                {
                    errors.Add(new BilVerificationError("21.3", location,
                        "void proxy 模板必须使用 invoke.noret fn(..inner)"));
                }
                return;
            }
            if (target == null)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    "有返回 proxy 模板必须使用 invoke fn(..inner)"));
                return;
            }
            CheckType(context, VarType(context, target), context.ReturnType, location,
                "invoke fn(..inner) 结果", errors);
        }

        // ..super 是 Middleware 的直接基类原始实现入口，不是可声明/可解析的
        // canonical 方法。调用者必须是 override 或 init，receiver 固定为 $.this。
        private static void VerifySuperInvoke(BilFunctionContext context,
            IReadOnlyList<BilVariableOperand> arguments, BilVariableOperand? target,
            string location, List<BilVerificationError> errors)
        {
            if (!context.Module.MethodDeclarations.TryGetValue(context.Function.Symbol,
                    out var declaration)
                || (!HasKeyword(declaration, BilKeyword.Override)
                    && !HasKeyword(declaration, BilKeyword.Init)))
            {
                errors.Add(new BilVerificationError("21.3", location,
                    "invoke fn(..super) 仅允许在 override 或 init fn 体内"));
            }
            if (arguments.Count == 0 || arguments[0].Name != ".this")
            {
                errors.Add(new BilVerificationError("21.3", location,
                    "invoke fn(..super) 的首实参必须精确为 $.this"));
            }
            var expectedHidden = context.Function.Args
                .Where(argument => argument.Name.StartsWith(".generic."))
                .ToList();
            for (var i = 0; i < expectedHidden.Count; i++)
            {
                var position = i + 1;
                if (position >= arguments.Count || arguments[position].Name != expectedHidden[i].Name)
                {
                    errors.Add(new BilVerificationError("21.3", location,
                        $"invoke fn(..super) 泛型隐藏实参 {i} 应为 " +
                        $"\"${expectedHidden[i].Name}\"（紧随 $.this 且按 .args 声明序）"));
                }
            }
            var isInit = declaration != null && HasKeyword(declaration, BilKeyword.Init);
            if (isInit && target != null)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    "init 体内的 invoke fn(..super) 必须使用 invoke.noret"));
            }
            if (!isInit)
            {
                if (context.ReturnType == ".void" && target != null)
                {
                    errors.Add(new BilVerificationError("21.3", location,
                        "void override 必须使用 invoke.noret fn(..super)"));
                }
                if (context.ReturnType != ".void" && target == null)
                {
                    errors.Add(new BilVerificationError("21.3", location,
                        "有返回 override 必须使用 invoke fn(..super)"));
                }
                if (target != null)
                {
                    CheckType(context, VarType(context, target), context.ReturnType, location,
                        "invoke fn(..super) 结果", errors);
                }
            }
        }

        private static bool IsGenericPackType(string typeRef)
        {
            var normalized = typeRef.Replace(" ", "");
            return normalized == ".array<.typeid<.any>>"
                || normalized == ".map<.string,.typeid<.any>>";
        }

        // §12.5：RESULT 类型 = 模板所属 wrapper 的 TTarget（generic 参数投影
        // `.generic<$.generic.NAME>`）；查不到定义级泛型参数信息时降级通过
        private static void VerifyGetSelfResult(BilFunctionContext context,
            GetSelfInstruction instruction, string location, List<BilVerificationError> errors)
        {
            if (!BilVerificationContext.TryParseMethodSymbol(context.Function.Symbol,
                    out var owner, out _, out _, out _))
            {
                return;
            }
            if (!context.Module.TryGetTypeDeclaration(owner, out var ownerDecl))
            {
                return;   // 查不到降级
            }
            if (ownerDecl.Kind != BilTypeKind.Wrapper)
            {
                return;   // 非 wrapper 宿主已由声明侧 §21.2 报
            }
            if (ownerDecl.GenericParameters.Count == 0)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    "零泛型参数的 wrapper 模板内不得出现 get.self（§12.5）"));
                return;
            }
            // Entity 恰一 TTarget 时 RESULT = `.generic<$.generic.TTarget>`
            var expected = ".generic<$.generic." + ownerDecl.GenericParameters[0] + ">";
            CheckType(context, VarType(context, instruction.Target), expected, location,
                "get.self 结果", errors);
        }

        // §21.3：set.wrapper.field 链校验（§13.3）——链至少一元素，元素两态
        // field(F)/wrapper(W)；按序跟踪当前位置类型：
        //   field(F),wrapper(W) 且 F 声明带 wrapped(W) → 字段应用（位置转 W）；
        //   普通 field(F) → 按下钻字段类型；
        //   wrapper(W) → 类型应用（当前 type 声明须带 wrapped(W)，查不到降级）；
        // INNER_FIELD 实例且 owner 可赋到最终当前位置。每步至多一条诊断。
        // opcodeLabel 用于空链等错误文本（set.wrapper.field）。
        private static void VerifyWrapperFieldChain(BilFunctionContext context,
            IReadOnlyList<BilOperand> chain, string innerFieldSymbol, string? objectType,
            string location, string opcodeLabel, List<BilVerificationError> errors)
        {
            if (chain.Count == 0)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    opcodeLabel + " 链至少含一个链元素（§13.3）"));
                return;
            }

            string? currentType = objectType;
            var i = 0;
            while (i < chain.Count)
            {
                switch (chain[i])
                {
                    case BilFieldOperand fieldElement:
                        if (!TryValidateChainField(context, fieldElement.Symbol, location, errors,
                                out var fieldOwner, out var fieldType))
                        {
                            return;
                        }
                        // 相邻 field(F)+wrapper(W)：F 带 wrapped(W) 则字段应用对
                        if (i + 1 < chain.Count && chain[i + 1] is BilWrapperOperand pairWrapper)
                        {
                            var pairKind = ClassifyFieldWrapperPair(context, fieldElement.Symbol,
                                pairWrapper.TypeRef);
                            if (pairKind == FieldWrapperPairKind.FieldApplication)
                            {
                                if (!VerifyWrapperElementType(context, pairWrapper.TypeRef,
                                        location, errors))
                                {
                                    return;
                                }
                                CheckHostAssignable(context, currentType, fieldOwner, location,
                                    "嵌套字段访问的字段应用宿主", errors);
                                currentType = pairWrapper.TypeRef;
                                i += 2;
                                break;
                            }
                            // 不匹配时先按普通字段下钻；下一轮把 W 验证为该字段
                            // 类型上的 Entity 应用。这样 `field(F),wrapper(W)` 同时
                            // 可表达字段应用与普通字段后的类型应用，匹配标记消歧。
                        }
                        CheckHostAssignable(context, currentType, fieldOwner, location,
                            "嵌套字段访问的链字段宿主", errors);
                        currentType = fieldType;
                        i += 1;
                        break;

                    case BilWrapperOperand wrapperElement:
                        if (!VerifyWrapperElementType(context, wrapperElement.TypeRef,
                                location, errors))
                        {
                            return;
                        }
                        // 类型应用：当前 type 声明须带 wrapped(W)；查不到/外部降级
                        if (currentType != null
                            && context.Module.TryGetTypeDeclaration(currentType, out var hostDecl)
                            && !DeclarationHasWrapped(hostDecl, wrapperElement.TypeRef))
                        {
                            errors.Add(new BilVerificationError("21.3", location,
                                $"嵌套字段访问的类型应用 wrapper(\"{wrapperElement.TypeRef}\") " +
                                $"要求当前位置类型 \"{currentType}\" 带 " +
                                $"wrapped({wrapperElement.TypeRef}) 应用标记（§8.3.1/§13.3）"));
                            return;
                        }
                        if (currentType != null
                            && BilVerificationContext.IsBuiltinType(currentType))
                        {
                            errors.Add(new BilVerificationError("21.3", location,
                                $"嵌套字段访问的类型应用 wrapper(\"{wrapperElement.TypeRef}\") " +
                                $"要求当前位置类型 \"{currentType}\" 带 " +
                                $"wrapped({wrapperElement.TypeRef}) 应用标记（§8.3.1/§13.3）"));
                            return;
                        }
                        currentType = wrapperElement.TypeRef;
                        i += 1;
                        break;

                    default:
                        errors.Add(new BilVerificationError("21.3", location,
                            "嵌套字段访问的链元素必须是 field(...) 或 wrapper(...)（§13.3）"));
                        return;
                }
            }

            if (!context.Module.FieldSymbols.Contains(innerFieldSymbol))
            {
                errors.Add(new BilVerificationError("21.2", location,
                    $"嵌套字段访问的内层字段符号不可解析 \"{innerFieldSymbol}\""));
                return;
            }
            if (!BilVerificationContext.TryParseFieldSymbol(innerFieldSymbol,
                    out var innerOwner, out var innerStatic, out _))
            {
                return;
            }
            if (innerStatic)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"嵌套字段访问的内层字段 \"{innerFieldSymbol}\" 必须是实例字段"));
                return;
            }
            CheckHostAssignable(context, currentType, innerOwner, location,
                "嵌套字段访问的内层字段宿主", errors);
        }

        private static bool ChainHasWrapperElement(IReadOnlyList<BilOperand> chain)
        {
            for (var i = 0; i < chain.Count; i++)
            {
                if (chain[i] is BilWrapperOperand) return true;
            }
            return false;
        }

        private enum FieldWrapperPairKind
        {
            None,
            FieldApplication,
        }

        private static FieldWrapperPairKind ClassifyFieldWrapperPair(
            BilFunctionContext context, string fieldSymbol, string wrapperTypeRef)
        {
            if (!context.Module.FieldDeclarations.TryGetValue(fieldSymbol, out var fieldDecl))
            {
                // 字段符号已在 TryValidateChainField 确认可解析；声明缺席时
                // 无法核 wrapped → 不按字段应用对处理（降级为普通 field）
                return FieldWrapperPairKind.None;
            }
            foreach (var modifier in fieldDecl.Modifiers)
            {
                if (modifier is not BilWrappedModifier wrapped) continue;
                if (BilVerificationContext.TypesCompatible(wrapped.WrapperTypeRef, wrapperTypeRef))
                {
                    return FieldWrapperPairKind.FieldApplication;
                }
            }
            return FieldWrapperPairKind.None;
        }

        private static bool DeclarationHasWrapped(BilTypeDeclaration declaration,
            string wrapperTypeRef)
        {
            foreach (var modifier in declaration.Modifiers)
            {
                if (modifier is BilWrappedModifier wrapped
                    && BilVerificationContext.TypesCompatible(wrapped.WrapperTypeRef,
                        wrapperTypeRef))
                {
                    return true;
                }
            }
            return false;
        }

        // 链字段可解析 + 实例；失败已落诊断并返回 false
        private static bool TryValidateChainField(BilFunctionContext context, string fieldSymbol,
            string location, List<BilVerificationError> errors,
            out string fieldOwner, out string fieldType)
        {
            fieldOwner = "";
            fieldType = "";
            if (!context.Module.FieldSymbols.Contains(fieldSymbol))
            {
                errors.Add(new BilVerificationError("21.2", location,
                    $"嵌套字段访问的链字段符号不可解析 \"{fieldSymbol}\""));
                return false;
            }
            if (!BilVerificationContext.TryParseFieldSymbol(fieldSymbol,
                    out fieldOwner, out var isStatic, out fieldType))
            {
                return false;
            }
            if (isStatic)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"嵌套字段访问的链字段 \"{fieldSymbol}\" 必须是实例字段"));
                return false;
            }
            return true;
        }

        // wrapper 元素必须是 wrapper 类型（查不到类型引用降级通过）
        private static bool VerifyWrapperElementType(BilFunctionContext context, string typeRef,
            string location, List<BilVerificationError> errors)
        {
            if (!context.Module.IsResolvableTypeRef(typeRef))
            {
                return true;
            }
            if (context.Module.TryGetTypeDeclaration(typeRef, out var wrapperDecl)
                && wrapperDecl.Kind != BilTypeKind.Wrapper)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"嵌套字段访问的 wrapper 元素 \"{typeRef}\" 不是 wrapper 类型（§13.3）"));
                return false;
            }
            return true;
        }

        private static bool SignatureMatches(BilFunctionContext context,
            List<(string Name, string TypeRef)> parameters,
            IReadOnlyList<BilVariableOperand> arguments)
        {
            if (parameters.Count != arguments.Count)
            {
                return false;
            }
            for (var i = 0; i < parameters.Count; i++)
            {
                var argumentType = VarType(context, arguments[i]);
                if (argumentType != null
                    && !context.Module.TypesAssignable(argumentType, parameters[i].TypeRef))
                {
                    return false;
                }
            }
            return true;
        }

        private static void VerifyInstanceField(BilFunctionContext context, string fieldSymbol,
            string? objectType, string location, List<BilVerificationError> errors)
        {
            if (!context.Module.FieldSymbols.Contains(fieldSymbol))
            {
                errors.Add(new BilVerificationError("21.2", location,
                    $"字段符号不可解析 \"{fieldSymbol}\""));
                return;
            }
            if (BilVerificationContext.TryParseFieldSymbol(fieldSymbol,
                    out var owner, out _, out _))
            {
                CheckHostAssignable(context, objectType, owner, location, "字段访问的宿主对象", errors);
            }
        }

        // 宿主对象类型必须可赋值到 owner（继承字段/方法直接命中基类符号）；
        // 查不到声明的降级场景由 IsAssignableTo 内部处理
        private static void CheckHostAssignable(BilFunctionContext context, string? actualType,
            string ownerRef, string location, string what, List<BilVerificationError> errors)
        {
            if (actualType == null)
            {
                return;
            }
            if (!context.Module.IsAssignableTo(actualType, ownerRef))
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"{what}类型不匹配：实际 \"{actualType}\"，期望可赋值到 \"{ownerRef}\""));
            }
        }

        private static void VerifyStaticField(BilFunctionContext context, string fieldSymbol,
            string ownerTypeRef, string location, List<BilVerificationError> errors)
        {
            if (!context.Module.FieldSymbols.Contains(fieldSymbol))
            {
                errors.Add(new BilVerificationError("21.2", location,
                    $"字段符号不可解析 \"{fieldSymbol}\""));
                return;
            }
            if (BilVerificationContext.TryParseFieldSymbol(fieldSymbol,
                    out var owner, out _, out _))
            {
                // 命名空间全局字段（§13.4 未规定宿主形态）：符号 owner 段是
                // 带 "::" 的命名空间前缀，指令宿主操作数投影命名空间全名
                // （EmittingFacility.FieldOwnerRef 约定）——归一后比对
                if (owner.EndsWith("::"))
                {
                    owner = owner.Substring(0, owner.Length - 2);
                }
                CheckType(context, ownerTypeRef, owner, location, "静态字段的宿主类型", errors);
            }
        }

        private static string? FieldTypeOf(BilFunctionContext context, string fieldSymbol)
        {
            return BilVerificationContext.TryParseFieldSymbol(fieldSymbol, out _, out _, out var fieldType)
                ? fieldType
                : null;
        }

        // §21.8：const 字段不被写入——init 豁免（对齐 P3 ConstFieldRules，
        // SYNTAX §9.3 构造期一次性赋值）：init 方法（§8.4 以 init 修饰符
        // 标识）体内写实例 const 字段放行；P3 豁免不限字段宿主与函数宿主
        // 一致（init 体内写任意实例 const 字段均放行，含继承的基类字段），
        // BIL 侧对齐；静态写入（set.field.static）不在豁免内——isInstanceWrite
        // 由指令形态给出（set.wrapper.field 内层字段必为实例，§21.3 已拦截静态）。
        // fn 声明查不到时模块已有 §21.2 错误（fn 无对应本地声明），不豁免
        private static void VerifyFieldWritable(BilFunctionContext context, string fieldSymbol,
            bool isInstanceWrite, string location, List<BilVerificationError> errors)
        {
            if (!context.Module.FieldDeclarations.TryGetValue(fieldSymbol, out var declaration)
                || !HasKeyword(declaration, BilKeyword.Const))
            {
                return;
            }
            if (isInstanceWrite && IsInitFunction(context))
            {
                return;
            }
            errors.Add(new BilVerificationError("21.8", location,
                $"const 字段 \"{fieldSymbol}\" 不得被写入"));
        }

        // 当前 fn 是否 init 方法：§8.4 以声明的 init 修饰符标识（init 的
        // canonical 是普通 $init(...)@.void 形态，不作判定依据）；fn 定义
        // 必对应 LocalSymbols 声明（§21.2），查不到声明即非 init
        private static bool IsInitFunction(BilFunctionContext context)
        {
            return context.Module.MethodDeclarations.TryGetValue(context.Function.Symbol,
                    out var fnDeclaration)
                && HasKeyword(fnDeclaration, BilKeyword.Init);
        }

        private static void VerifyWrapperType(BilFunctionContext context, string typeRef,
            string location, List<BilVerificationError> errors)
        {
            VerifyResolvableType(context, typeRef, location, errors);
            if (context.Module.TryGetTypeDeclaration(typeRef, out var declaration)
                && declaration.Kind != BilTypeKind.Wrapper)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"\"{typeRef}\" 不是 wrapper 类型"));
            }
        }

        // §12.4 get.wrapper.field：OBJECT 可赋值到 HOST_FIELD owner；
        // HOST_FIELD 实例字段且带 wrapped(WRAPPER_TYPE)；RESULT = W
        private static void VerifyGetWrapperField(BilFunctionContext context,
            GetWrapperFieldInstruction instruction, string location,
            List<BilVerificationError> errors)
        {
            VerifyWrapperType(context, instruction.WrapperType.TypeRef, location, errors);
            CheckType(context, VarType(context, instruction.Target),
                instruction.WrapperType.TypeRef, location, "get.wrapper.field 结果", errors);
            var fieldSymbol = instruction.HostField.Symbol;
            if (!context.Module.FieldSymbols.Contains(fieldSymbol))
            {
                errors.Add(new BilVerificationError("21.2", location,
                    $"get.wrapper.field 的宿主字段符号不可解析 \"{fieldSymbol}\""));
                return;
            }
            if (BilVerificationContext.TryParseFieldSymbol(fieldSymbol,
                    out var owner, out var isStatic, out _))
            {
                if (isStatic)
                {
                    errors.Add(new BilVerificationError("21.3", location,
                        $"get.wrapper.field 的宿主字段 \"{fieldSymbol}\" 必须是实例字段"));
                }
                CheckHostAssignable(context, VarType(context, instruction.Object), owner,
                    location, "get.wrapper.field 的宿主对象", errors);
            }
            if (!context.Module.FieldDeclarations.TryGetValue(fieldSymbol, out var fieldDecl))
            {
                return;
            }
            var hasWrapped = false;
            foreach (var modifier in fieldDecl.Modifiers)
            {
                if (modifier is BilWrappedModifier wrapped
                    && BilVerificationContext.TypesCompatible(wrapped.WrapperTypeRef,
                        instruction.WrapperType.TypeRef))
                {
                    hasWrapped = true;
                    break;
                }
            }
            if (!hasWrapped)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"get.wrapper.field 的宿主字段 \"{fieldSymbol}\" 必须带 " +
                    $"wrapped({instruction.WrapperType.TypeRef}) 应用标记（§8.3.1/§12.4）"));
            }
        }

        private static void VerifyResolvableType(BilFunctionContext context, string typeRef,
            string location, List<BilVerificationError> errors)
        {
            if (!context.Module.IsResolvableTypeRef(typeRef))
            {
                errors.Add(new BilVerificationError("21.2", location,
                    $"类型符号不可解析 \"{typeRef}\""));
            }
        }

        // 异常兼容（§16.7/§16.9）：沿 extends 链可达 core::Exception。
        // 查不到声明或链断（external 不完整）降级通过；只有声明齐全且确实
        // 不继承时才报错（防误报原则）
        private static bool IsExceptionCompatible(BilFunctionContext context, string typeRef)
        {
            const string exceptionRoot = "core::Exception";
            var current = typeRef;
            var visited = new HashSet<string>();
            while (true)
            {
                if (BilVerificationContext.StripTypeArguments(current) == exceptionRoot)
                {
                    return true;
                }
                if (!visited.Add(BilVerificationContext.DeclarationKeyOf(current))
                    || !context.Module.TryGetTypeDeclaration(current, out var declaration)
                    || declaration.ExtendsType == null)
                {
                    return true;   // 链断：降级
                }
                current = declaration.ExtendsType;
            }
        }

        // ===== 类型比较原语 =====

        private static string? VarType(BilFunctionContext context, BilVariableOperand variable)
        {
            return context.VariableTypes.TryGetValue(variable.Name, out var type) ? type : null;
        }

        // expected 为 null 表示上游已报错（符号 malformed/变量未声明），跳过；
        // prefixMatch 用于 .typeid / .typeid<TBound> 家族
        private static void CheckType(BilFunctionContext context, string? actual, string? expected,
            string location, string what, List<BilVerificationError> errors,
            bool prefixMatch = false)
        {
            if (actual == null || expected == null)
            {
                return;
            }
            var compatible = prefixMatch
                ? (actual == expected || actual.StartsWith(expected + "<"))
                : context.Module.TypesAssignable(actual, expected);
            if (!compatible)
            {
                errors.Add(new BilVerificationError("21.3", location,
                    $"{what}类型不匹配：实际 \"{actual}\"，期望 \"{expected}\""));
            }
        }
    }
}
