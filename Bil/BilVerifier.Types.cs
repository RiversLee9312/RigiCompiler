using System.Collections.Generic;

namespace LatteCompiler.Bil
{
    // BilVerifier 类型检查（§20.3）：变量类型环境 + 逐指令 switch。
    // 严格相等按 §6.4；含 .generic< 的 typeid 位置表达式与查不到声明的
    // 派生规则一律降级通过（防误报原则，见 BilVerifier.cs 文件头）。
    // S8c 增补：§13.6 get.array/set.array 非数组形态的严格三元组查询
    // （用户 getAtIndex/setAtIndex 索引运算符实现重查）。
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
                    errors.Add(new BilVerificationError("20.3", location,
                        $"验证器未覆盖指令类型 {instruction.GetType().Name}——请扩展 BilVerifier"));
                }
                // 变量可解析（§20.2）+ breakid 不得普通读写（§20.6）
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
                errors.Add(new BilVerificationError("20.2", location,
                    $"未声明的变量 \"${variable.Name}\""));
            }
            else if (context.BreakIdVariables.Contains(variable.Name))
            {
                errors.Add(new BilVerificationError("20.6", location,
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
                case GetWrapperInstruction getWrapper:
                    reads.Add(getWrapper.Value);
                    writes.Add(getWrapper.Target);
                    return true;
                case GetIdVarInstruction getIdVar:
                    reads.Add(getIdVar.Value);
                    writes.Add(getIdVar.Target);
                    return true;
                case GetIdTypeInstruction getIdType:
                    writes.Add(getIdType.Target);
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
                default:
                    return false;
            }
        }

        // ===== 逐指令类型规则（§20.3）=====
        private static void VerifyInstructionTypes(BilFunctionContext context,
            BilInstruction instruction, string location, List<BilVerificationError> errors)
        {
            switch (instruction)
            {
                case LoadInstruction load:
                    // §13.1：资源类型 ≡ TARGET；资源必须属于本模块（§20.2）
                    if (!context.Module.ResourceSet.Contains(load.Resource))
                    {
                        errors.Add(new BilVerificationError("20.2", location,
                            $"load 引用的资源 \"{load.Resource.Name}\" 不属于本模块"));
                    }
                    CheckType(context, VarType(context, load.Target),
                        BilVerificationContext.ResourceValueType(load.Resource), location,
                        "load 目标变量", errors);
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

                case GetWrapperInstruction getWrapper:
                    VerifyWrapperType(context, getWrapper.WrapperType.TypeRef, location, errors);
                    CheckType(context, VarType(context, getWrapper.Target),
                        getWrapper.WrapperType.TypeRef, location, "get.wrapper 结果", errors);
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
                    VerifyFieldWritable(context, setField.Field.Symbol, location, errors);
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
                    VerifyFieldWritable(context, setFieldStatic.Field.Symbol, location, errors);
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

                case InvokeInstruction invoke:
                    VerifyInvoke(context, invoke.Method.Symbol, invoke.Arguments,
                        invoke.Target, location, errors);
                    break;

                case InvokeNoResultInstruction invokeNoResult:
                    VerifyInvoke(context, invokeNoResult.Method.Symbol, invokeNoResult.Arguments,
                        null, location, errors);
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
                    // §16.6/§18.4：表必须是 switch-table 资源、属于本模块、
                    // selector 类型 ≡ 表元素类型、表项数与 block 表一致
                    if (!context.Module.ResourceSet.Contains(switchInstruction.Table))
                    {
                        errors.Add(new BilVerificationError("20.2", location,
                            $"switch 引用的资源 \"{switchInstruction.Table.Name}\" 不属于本模块"));
                    }
                    if (switchInstruction.Table is not BilSwitchTableResource switchTable)
                    {
                        errors.Add(new BilVerificationError("20.3", location,
                            $"switch 的表资源 \"{switchInstruction.Table.Name}\" 不是 switch-table"));
                    }
                    else
                    {
                        CheckType(context, VarType(context, switchInstruction.Selector),
                            switchTable.SelectorTypeRef, location, "switch selector", errors);
                        if (switchTable.Elements.Count != switchInstruction.ItemBlocks.Count)
                        {
                            errors.Add(new BilVerificationError("20.3", location,
                                $"switch-table 元素数 {switchTable.Elements.Count} 与 item block 数 " +
                                $"{switchInstruction.ItemBlocks.Count} 不一致"));
                        }
                    }
                    break;

                case TryInstruction tryInstruction:
                    // §16.7/§18.5：表必须是 catch-table 资源、属于本模块、
                    // 条目异常类型可解析且兼容 core::Exception
                    if (!context.Module.ResourceSet.Contains(tryInstruction.CatchTable))
                    {
                        errors.Add(new BilVerificationError("20.2", location,
                            $"try 引用的资源 \"{tryInstruction.CatchTable.Name}\" 不属于本模块"));
                    }
                    if (tryInstruction.CatchTable is not BilCatchTableResource catchTable)
                    {
                        errors.Add(new BilVerificationError("20.3", location,
                            $"try 的表资源 \"{tryInstruction.CatchTable.Name}\" 不是 catch-table"));
                    }
                    else
                    {
                        foreach (var entry in catchTable.Entries)
                        {
                            VerifyResolvableType(context, entry.ExceptionType.TypeRef, location, errors);
                            if (!IsExceptionCompatible(context, entry.ExceptionType.TypeRef))
                            {
                                errors.Add(new BilVerificationError("20.3", location,
                                    $"catch 类型 \"{entry.ExceptionType.TypeRef}\" 不兼容 core::Exception"));
                            }
                        }
                    }
                    break;

                case ThrowInstruction throwInstruction:
                    var exceptionType = VarType(context, throwInstruction.Exception);
                    if (exceptionType != null && !IsExceptionCompatible(context, exceptionType))
                    {
                        errors.Add(new BilVerificationError("20.3", location,
                            $"throw 值类型 \"{exceptionType}\" 不兼容 core::Exception"));
                    }
                    break;

                case BreakInstruction:
                case ContinueInstruction:
                case CallBlockInstruction:
                    break;   // 无类型规则（capability 检查在 Flow.cs）

                default:
                    errors.Add(new BilVerificationError("20.3", location,
                        $"验证器未覆盖指令类型 {instruction.GetType().Name}——请扩展 BilVerifier"));
                    break;
            }
        }

        // ===== 指令级辅助 =====

        private static void VerifyInvoke(BilFunctionContext context, string methodSymbol,
            IReadOnlyList<BilVariableOperand> arguments, BilVariableOperand? target,
            string location, List<BilVerificationError> errors)
        {
            // §20.2：方法符号可解析
            if (!context.Module.MethodSymbols.Contains(methodSymbol))
            {
                errors.Add(new BilVerificationError("20.2", location,
                    $"invoke 的方法符号不可解析 \"{methodSymbol}\""));
            }
            if (!BilVerificationContext.TryParseMethodSymbol(methodSymbol,
                    out var owner, out var isStatic, out var parameters, out var returnType))
            {
                errors.Add(new BilVerificationError("20.1", location,
                    $"invoke 的方法符号不符合 canonical 语法 \"{methodSymbol}\""));
                return;
            }

            // §15.1：返回形态匹配——有返回用 invoke，无返回用 invoke.noret
            if (target != null && returnType == ".void")
            {
                errors.Add(new BilVerificationError("20.3", location,
                    $"无返回方法 \"{methodSymbol}\" 必须使用 invoke.noret"));
            }
            if (target == null && returnType != ".void")
            {
                errors.Add(new BilVerificationError("20.3", location,
                    $"有返回方法 \"{methodSymbol}\" 不得使用 invoke.noret"));
            }
            if (target != null && returnType != ".void")
            {
                CheckType(context, VarType(context, target), returnType, location,
                    "invoke 结果", errors);
            }

            // §15.1：实参 ≡ 规范签名（.this 首参 + 普通参数逐项；hidden
            // 参数形态发射器尚未产出，符号中含 hidden 形态的参数跳过比对）
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
            var argumentIndex = 0;
            if (!isStatic && owner.Length > 0)
            {
                if (arguments.Count == 0)
                {
                    errors.Add(new BilVerificationError("20.3", location,
                        $"实例方法 \"{methodSymbol}\" 的 invoke 缺少 receiver 首实参"));
                    return;
                }
                CheckHostAssignable(context, VarType(context, arguments[0]), owner, location,
                    "invoke receiver(.this)", errors);
                argumentIndex = 1;
            }
            if (arguments.Count - argumentIndex != expected.Count)
            {
                errors.Add(new BilVerificationError("20.3", location,
                    $"invoke 实参个数 {arguments.Count - argumentIndex} 与方法 \"{methodSymbol}\" " +
                    $"签名参数个数 {expected.Count} 不一致"));
                return;
            }
            for (var i = 0; i < expected.Count; i++)
            {
                CheckType(context, VarType(context, arguments[argumentIndex + i]),
                    expected[i].TypeRef, location, $"invoke 实参 {i}", errors);
            }
        }

        private static void VerifyNew(BilFunctionContext context, NewInstruction newInstruction,
            string location, List<BilVerificationError> errors)
        {
            var typeRef = newInstruction.Type.TypeRef;
            VerifyResolvableType(context, typeRef, location, errors);
            CheckType(context, VarType(context, newInstruction.Target), typeRef, location,
                "new 结果", errors);
            if (!context.Module.TypeDeclarations.TryGetValue(
                    BilVerificationContext.StripTypeArguments(typeRef), out var declaration))
            {
                return;   // 查不到声明（external 不完整）降级
            }
            // §20.8：abstract 不被构造；enum-struct 不走普通 new
            if (HasKeyword(declaration.Modifiers, BilKeyword.Abstract))
            {
                errors.Add(new BilVerificationError("20.8", location,
                    $"abstract 类型 \"{typeRef}\" 不得被 new 构造"));
            }
            if (declaration.Kind == BilTypeKind.EnumStruct)
            {
                errors.Add(new BilVerificationError("20.8", location,
                    $"enum-struct \"{typeRef}\" 不得走普通 new（应使用 new.case）"));
            }
            // §14.1：参数必须严格匹配唯一 init（无 init 声明时只允许无参构造）
            var inits = new List<BilSimpleMemberDeclaration>();
            foreach (var member in declaration.Members)
            {
                if (member is BilSimpleMemberDeclaration simple && HasKeyword(simple, BilKeyword.Init))
                {
                    inits.Add(simple);
                }
            }
            if (inits.Count == 0)
            {
                if (newInstruction.Arguments.Count > 0)
                {
                    errors.Add(new BilVerificationError("20.3", location,
                        $"类型 \"{typeRef}\" 没有 init 声明，不得带参数构造"));
                }
                return;
            }
            foreach (var init in inits)
            {
                if (!BilVerificationContext.TryParseMethodSymbol(init.Symbol,
                        out _, out _, out var parameters, out _))
                {
                    continue;
                }
                if (SignatureMatches(context, parameters, newInstruction.Arguments))
                {
                    return;
                }
            }
            errors.Add(new BilVerificationError("20.3", location,
                $"new \"{typeRef}\" 的实参不匹配任何 init 签名"));
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
            if (!context.Module.TypeDeclarations.TryGetValue(
                    BilVerificationContext.StripTypeArguments(collectionType), out var declaration))
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
                if (!context.Module.TypeDeclarations.TryGetValue(
                        BilVerificationContext.StripTypeArguments(current.ExtendsType),
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
                errors.Add(new BilVerificationError("20.3", location,
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
                errors.Add(new BilVerificationError("20.3", location,
                    $"{opcode} 的操作数在 \"{collectionType}\" 的 {operatorName} 实现中无精确匹配" +
                    $"（禁止隐式转换）：索引 \"{indexType}\"" +
                    (isGet ? "" : $"，元素 \"{valueType}\"")));
                return;
            }
            if (exact.Count > 1)
            {
                errors.Add(new BilVerificationError("20.3", location,
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
        {var typeRef = newCase.Type.TypeRef;
            VerifyResolvableType(context, typeRef, location, errors);
            CheckType(context, VarType(context, newCase.Target), typeRef, location,
                "new.case 结果", errors);
            // §14.3：ENUM_TYPE 必须是 enum-struct 且 case owner 一致
            if (context.Module.TypeDeclarations.TryGetValue(
                    BilVerificationContext.StripTypeArguments(typeRef), out var declaration)
                && declaration.Kind != BilTypeKind.EnumStruct)
            {
                errors.Add(new BilVerificationError("20.3", location,
                    $"new.case 目标类型 \"{typeRef}\" 不是 enum-struct"));
            }
            if (!newCase.Case.QualifiedName.StartsWith(typeRef + "."))
            {
                errors.Add(new BilVerificationError("20.3", location,
                    $"new.case 的 case \"{newCase.Case.QualifiedName}\" 不属于类型 \"{typeRef}\""));
            }
            if (!context.Module.CaseDeclarations.TryGetValue(newCase.Case.QualifiedName, out var caseDeclaration))
            {
                errors.Add(new BilVerificationError("20.2", location,
                    $"new.case 的 case 符号不可解析 \"{newCase.Case.QualifiedName}\""));
                return;
            }
            // case 参数签名匹配
            if (caseDeclaration.Parameters.Count != newCase.Arguments.Count)
            {
                errors.Add(new BilVerificationError("20.3", location,
                    $"new.case 实参个数与 case 声明参数个数不一致"));
                return;
            }
            for (var i = 0; i < caseDeclaration.Parameters.Count; i++)
            {
                CheckType(context, VarType(context, newCase.Arguments[i]),
                    caseDeclaration.Parameters[i].TypeRef, location, $"new.case 实参 {i}", errors);
            }
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
                    && !BilVerificationContext.TypesCompatible(argumentType, parameters[i].TypeRef))
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
                errors.Add(new BilVerificationError("20.2", location,
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
                errors.Add(new BilVerificationError("20.3", location,
                    $"{what}类型不匹配：实际 \"{actualType}\"，期望可赋值到 \"{ownerRef}\""));
            }
        }

        private static void VerifyStaticField(BilFunctionContext context, string fieldSymbol,
            string ownerTypeRef, string location, List<BilVerificationError> errors)
        {
            if (!context.Module.FieldSymbols.Contains(fieldSymbol))
            {
                errors.Add(new BilVerificationError("20.2", location,
                    $"字段符号不可解析 \"{fieldSymbol}\""));
                return;
            }
            if (BilVerificationContext.TryParseFieldSymbol(fieldSymbol,
                    out var owner, out _, out _))
            {
                CheckType(context, ownerTypeRef, owner, location, "静态字段的宿主类型", errors);
            }
        }

        private static string? FieldTypeOf(BilFunctionContext context, string fieldSymbol)
        {
            return BilVerificationContext.TryParseFieldSymbol(fieldSymbol, out _, out _, out var fieldType)
                ? fieldType
                : null;
        }

        // §20.8：const 字段不被写入
        private static void VerifyFieldWritable(BilFunctionContext context, string fieldSymbol,
            string location, List<BilVerificationError> errors)
        {
            if (context.Module.FieldDeclarations.TryGetValue(fieldSymbol, out var declaration)
                && HasKeyword(declaration, BilKeyword.Const))
            {
                errors.Add(new BilVerificationError("20.8", location,
                    $"const 字段 \"{fieldSymbol}\" 不得被写入"));
            }
        }

        private static void VerifyWrapperType(BilFunctionContext context, string typeRef,
            string location, List<BilVerificationError> errors)
        {
            VerifyResolvableType(context, typeRef, location, errors);
            if (context.Module.TypeDeclarations.TryGetValue(
                    BilVerificationContext.StripTypeArguments(typeRef), out var declaration)
                && declaration.Kind != BilTypeKind.Wrapper)
            {
                errors.Add(new BilVerificationError("20.3", location,
                    $"\"{typeRef}\" 不是 wrapper 类型"));
            }
        }

        private static void VerifyResolvableType(BilFunctionContext context, string typeRef,
            string location, List<BilVerificationError> errors)
        {
            if (!context.Module.IsResolvableTypeRef(typeRef))
            {
                errors.Add(new BilVerificationError("20.2", location,
                    $"类型符号不可解析 \"{typeRef}\""));
            }
        }

        // 异常兼容（§16.7/§16.9）：沿 extends 链可达 core::Exception。
        // 查不到声明或链断（external 不完整）降级通过；只有声明齐全且确实
        // 不继承时才报错（防误报原则）
        private static bool IsExceptionCompatible(BilFunctionContext context, string typeRef)
        {
            const string exceptionRoot = "core::Exception";
            var current = BilVerificationContext.StripTypeArguments(typeRef);
            var visited = new HashSet<string>();
            while (true)
            {
                if (current == exceptionRoot)
                {
                    return true;
                }
                if (!visited.Add(current)
                    || !context.Module.TypeDeclarations.TryGetValue(current, out var declaration)
                    || declaration.ExtendsType == null)
                {
                    return true;   // 链断：降级
                }
                current = BilVerificationContext.StripTypeArguments(declaration.ExtendsType);
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
                : BilVerificationContext.TypesCompatible(actual, expected);
            if (!compatible)
            {
                errors.Add(new BilVerificationError("20.3", location,
                    $"{what}类型不匹配：实际 \"{actual}\"，期望 \"{expected}\""));
            }
        }
    }
}
