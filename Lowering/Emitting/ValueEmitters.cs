using System.Collections.Generic;
using LatteCompiler.Bil;

namespace LatteCompiler
{
    // 值发射（S6–S8c；BIL §10–§15）。自旧 EmitSession.EmitValue 各分支
    // 迁移，行为不变——表达式物化为变量操作数（§10.1）。
    // M57 起产物为 BilVariableOperand（物化契约类型化，不再传递变量名字符串）。

    // 字面量：提取进 Resources（§4.2），经 load res(...) 引用（§13.1）
    internal sealed class LiteralEmitter : EmitVisitor<LiteralEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var literal = (LoweredLiteralExpression)node;
            var resource = EmittingFacility.RegisterResource(literal, env);
            var temp = ctx.Temps.NewTemp(literal.Type);
            target.Instructions.Add(new LoadInstruction(resource, temp) { Origin = literal });
            return temp;
        }
    }

    // P4a 合成常量（S7b bool；S7f null——安全访问/空值
    // 回退脱糖产物）：与字面量同路进 Resources（同键去重）
    internal sealed class ConstantEmitter : EmitVisitor<ConstantEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var constant = (LoweredConstantExpression)node;
            var constantResource = constant.Value is bool boolValue
                ? EmittingFacility.RegisterScalarResource(BilScalarType.Bool,
                    boolValue ? "true" : "false", env)
                : constant.Value is null
                    ? EmittingFacility.RegisterNullResource(constant.Type,
                        constant.Origin.Syntax.Span, env)
                    : throw new CompilerInternalException(
                        "P4a 合成常量类型未覆盖: " + constant.Value.GetType().Name);
            var constantTemp = ctx.Temps.NewTemp(constant.Type);
            target.Instructions.Add(new LoadInstruction(constantResource, constantTemp)
            { Origin = constant });
            return constantTemp;
        }
    }

    // 值引用（局部/参数）：名字即操作数，零指令。
    // S9d：可变参数引用映射到隐藏包变量（.vargs.<名>/.kwargs.<名>，
    // §7.1——源码参数名是包变量，BIL 以保留名承载）；映射与写入侧
    // （AssignmentEmitter set.var）共用 EmittingFacility.ValueVariableName
    internal sealed class ValueReferenceEmitter : EmitVisitor<ValueReferenceEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var valueReference = (LoweredValueReferenceExpression)node;
            return BilOp.Var(EmittingFacility.ValueVariableName(valueReference.Symbol));
        }
    }

    // 全局/static 字段读取（§13.4）
    internal sealed class FieldReferenceEmitter : EmitVisitor<FieldReferenceEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var fieldReference = (LoweredFieldReferenceExpression)node;
            var ownerRef = EmittingFacility.FieldOwnerRef(fieldReference.Field, env);
            if (ownerRef == null) return BilOp.Var("<error>");    // 已诊断
            var fieldValue = ctx.Temps.NewTemp(fieldReference.Type);
            target.Instructions.Add(new GetFieldStaticInstruction(
                fieldValue, BilOp.Type(ownerRef),
                BilOp.Field(CanonicalSymbolPrinter.PrintField(fieldReference.Field)))
            { Origin = fieldReference });
            return fieldValue;
        }
    }

    // 二元 intrinsic 运算（§11，op 映射单点）
    internal sealed class BinaryEmitter : EmitVisitor<BinaryEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var binary = (LoweredBinaryExpression)node;
            var left = EmitValueDispatcher.Visit(binary.Left, target, ctx, env);
            var right = EmitValueDispatcher.Visit(binary.Right, target, ctx, env);
            var binaryResult = ctx.Temps.NewTemp(binary.Type);
            target.Instructions.Add(new BinaryIntrinsicInstruction(
                EmittingFacility.MapBinaryOp(binary.Op), left, right, binaryResult)
            { Origin = binary });
            return binaryResult;
        }
    }

    // 一元 intrinsic 运算（§11，op 映射单点）
    internal sealed class UnaryEmitter : EmitVisitor<UnaryEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var unary = (LoweredUnaryExpression)node;
            var operand = EmitValueDispatcher.Visit(unary.Operand, target, ctx, env);
            var unaryResult = ctx.Temps.NewTemp(unary.Type);
            target.Instructions.Add(new UnaryIntrinsicInstruction(
                EmittingFacility.MapUnaryOp(unary.Op), operand, unaryResult)
            { Origin = unary });
            return unaryResult;
        }
    }

    // 带返回值调用（§15.1）：实参从左到右物化（§10.2），再发 invoke。
    // S9e：显式泛型实参按 §7.2 调用序前置物化（.generic.T 隐藏实参——
    // 静态实参 getid.type、嵌套泛型调用转发 $.generic.T）
    // S9d-2：泛型可变包在固定泛型之后、普通实参之前打包物化（§7.2）
    internal sealed class CallExpressionEmitter : EmitVisitor<CallExpressionEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var callExpression = (LoweredCallExpression)node;
            var callArguments = new List<BilVariableOperand>();
            foreach (var typeArgument in callExpression.TypeArguments)
            {
                callArguments.Add(EmittingFacility.MaterializeTypeId(typeArgument, callExpression,
                    target, ctx, env));
            }
            if (callExpression.GenericPack != null)
            {
                callArguments.Add(GenericVarArgsEmitter.Visit(callExpression.GenericPack, target,
                    ctx, env));
            }
            foreach (var argument in callExpression.Arguments)
            {
                callArguments.Add(EmitValueDispatcher.Visit(argument, target, ctx, env));
            }
            var callResult = ctx.Temps.NewTemp(callExpression.Type);
            if (callExpression.IsIndirect)
            {
                // §15.3 间接调用：物化目标对象表达式后虚调用其 $$call
                var indirectTarget = EmitValueDispatcher.Visit(callExpression.IndirectTarget!,
                    target, ctx, env);
                target.Instructions.Add(new InvokeIndirectInstruction(indirectTarget, callResult,
                    callArguments) { Origin = callExpression });
                return callResult;
            }
            target.Instructions.Add(new InvokeInstruction(
                BilOp.Fn(CanonicalSymbolPrinter.PrintMethod(callExpression.Method)),
                callResult, callArguments)
            { Origin = callExpression });
            return callResult;
        }
    }

    // new 构造（§14.1：init 选择归 Middleware（按精确参数类型），
    // 发射不写 init 符号）
    internal sealed class NewExpressionEmitter : EmitVisitor<NewExpressionEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var newExpression = (LoweredNewExpression)node;
            var newArguments = new List<BilVariableOperand>();
            foreach (var argument in newExpression.Arguments)
            {
                newArguments.Add(EmitValueDispatcher.Visit(argument, target, ctx, env));
            }
            var newResult = ctx.Temps.NewTemp(newExpression.Type);
            target.Instructions.Add(new NewInstruction(
                BilOp.Type(CanonicalSymbolPrinter.PrintType(newExpression.Type)),
                newResult, newArguments)
            { Origin = newExpression });
            return newResult;
        }
    }

    // enum case 构造（S11，§14.3：new.case type(TYPE) case(CASE) TARGET
    // [ARGS]）：洞实参逐条物化（§10.2 从左到右；P4a 已按洞签名类型物化
    // cast，实参类型与 case 声明参数严格相等）；结果临时变量类型 = 宿主
    // enum 类型引用
    internal sealed class EnumCaseEmitter : EmitVisitor<EnumCaseEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var enumCase = (LoweredEnumCaseExpression)node;
            var caseArguments = new List<BilVariableOperand>();
            foreach (var argument in enumCase.Arguments)
            {
                caseArguments.Add(EmitValueDispatcher.Visit(argument, target, ctx, env));
            }
            var caseResult = ctx.Temps.NewTemp(enumCase.Type);
            target.Instructions.Add(new NewCaseInstruction(
                BilOp.Type(CanonicalSymbolPrinter.PrintType(enumCase.Case.Owner)),
                BilOp.Case(CanonicalSymbolPrinter.PrintCase(enumCase.Case)),
                caseResult, caseArguments)
            { Origin = enumCase });
            return caseResult;
        }
    }

    // this → $.this 变量操作数（§7.3，零指令——.this 在
    // .args 已声明，与参数同 $ 引用形式 §9.3）
    internal sealed class ThisEmitter : EmitVisitor<ThisEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            return BilOp.Var(".this");
        }
    }

    // 实例调用（§7.3/§15.1）：receiver 求值作首实参；S9e 泛型实参
    // 在 receiver 之后、普通实参之前（§7.2 调用序）；S9d-2 泛型可变包
    // 在固定泛型之后
    internal sealed class InstanceCallEmitter : EmitVisitor<InstanceCallEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var instCall = (LoweredInstanceCallExpression)node;
            var instReceiver = EmitValueDispatcher.Visit(instCall.Receiver, target, ctx, env);
            var instArguments = new List<BilVariableOperand> { instReceiver };
            foreach (var typeArgument in instCall.TypeArguments)
            {
                instArguments.Add(EmittingFacility.MaterializeTypeId(typeArgument, instCall,
                    target, ctx, env));
            }
            if (instCall.GenericPack != null)
            {
                instArguments.Add(GenericVarArgsEmitter.Visit(instCall.GenericPack, target,
                    ctx, env));
            }
            foreach (var argument in instCall.Arguments)
            {
                instArguments.Add(EmitValueDispatcher.Visit(argument, target, ctx, env));
            }
            var instResult = ctx.Temps.NewTemp(instCall.Type);
            target.Instructions.Add(new InvokeInstruction(
                BilOp.Fn(CanonicalSymbolPrinter.PrintMethod(instCall.Method)),
                instResult, instArguments)
            { Origin = instCall });
            return instResult;
        }
    }

    // 实例字段读取（§13.3：get.field OBJECT TARGET field(F)）
    internal sealed class FieldAccessEmitter : EmitVisitor<FieldAccessEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var fieldAccess = (LoweredFieldAccessExpression)node;
            var accessReceiver = EmitValueDispatcher.Visit(fieldAccess.Receiver, target, ctx, env);
            var accessResult = ctx.Temps.NewTemp(fieldAccess.Type);
            target.Instructions.Add(new GetFieldInstruction(
                accessReceiver, accessResult,
                BilOp.Field(CanonicalSymbolPrinter.PrintField(fieldAccess.Field)))
            { Origin = fieldAccess });
            return accessResult;
        }
    }

    // wrapper 值拷贝（S11c，§12.4：get.wrapper VALUE type(WRAPPER_TYPE) RESULT）——
    // wrapper place 作成员访问接收者的物化
    internal sealed class GetWrapperEmitter : EmitVisitor<GetWrapperEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var getWrapper = (LoweredGetWrapperExpression)node;
            var wrapperValue = EmitValueDispatcher.Visit(getWrapper.Source, target, ctx, env);
            var wrapperResult = ctx.Temps.NewTemp(getWrapper.Type);
            target.Instructions.Add(new GetWrapperInstruction(wrapperValue,
                BilOp.Type(CanonicalSymbolPrinter.PrintType(getWrapper.Wrapper)), wrapperResult)
            { Origin = getWrapper });
            return wrapperResult;
        }
    }

    // 字段-Value wrapper 值拷贝（M84，§12.4：
    // get.wrapper.field OBJECT field(HOST_FIELD) type(WRAPPER_TYPE) RESULT）
    internal sealed class GetFieldWrapperEmitter
        : EmitVisitor<GetFieldWrapperEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var getFieldWrapper = (LoweredGetFieldWrapperExpression)node;
            var objectValue = EmitValueDispatcher.Visit(getFieldWrapper.Object, target, ctx, env);
            var result = ctx.Temps.NewTemp(getFieldWrapper.Type);
            target.Instructions.Add(new GetWrapperFieldInstruction(objectValue,
                BilOp.Field(CanonicalSymbolPrinter.PrintField(getFieldWrapper.HostField)),
                BilOp.Type(CanonicalSymbolPrinter.PrintType(getFieldWrapper.Wrapper)), result)
            { Origin = getFieldWrapper });
            return result;
        }
    }

    // set.wrapper.field 写链设施（§13.3）：PlaceChain 最外层→最内层，
    // FieldSymbol→field(F)、TypeSymbol→wrapper(W)；字段应用 = 相邻
    // field+wrapper 对。读侧不经此设施（Materialize + 普通 get.field）。
    // 普通值中间反向写回仍走 set.field。
    internal static class WrapperFieldEmission
    {
        public static void EmitWrite(LoweredWrapperFieldExpression place,
            BilVariableOperand source, BilVariableOperand receiver,
            BilBlock target, EmitContext ctx, EmitEnvironment env)
        {
            var chain = ProjectPlaceChain(place.PlaceChain);
            target.Instructions.Add(new SetWrapperFieldInstruction(source, receiver, chain,
                BilOp.Field(CanonicalSymbolPrinter.PrintField(place.Field)))
            { Origin = place });
        }

        private static IReadOnlyList<BilOperand> ProjectPlaceChain(
            IReadOnlyList<SemanticSymbol> placeChain)
        {
            var chain = new BilOperand[placeChain.Count];
            for (var i = 0; i < placeChain.Count; i++)
            {
                chain[i] = placeChain[i] switch
                {
                    FieldSymbol field => BilOp.Field(CanonicalSymbolPrinter.PrintField(field)),
                    TypeSymbol type => BilOp.Wrapper(CanonicalSymbolPrinter.PrintType(type)),
                    _ => throw new CompilerInternalException(
                        "wrapper PlaceChain element must be FieldSymbol or TypeSymbol, got " +
                        (placeChain[i]?.GetType().Name ?? "null")),
                };
            }
            return chain;
        }
    }

    // proxy 体 self（M88，§12.5 get.self RESULT）
    internal sealed class GetSelfEmitter : EmitVisitor<GetSelfEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var getSelf = (LoweredGetSelfExpression)node;
            var result = ctx.Temps.NewTemp(getSelf.Type);
            target.Instructions.Add(new GetSelfInstruction(result) { Origin = getSelf });
            return result;
        }
    }

    // proxy 体 inner(...)（M88，§15.4；#27⑦ 泛型包显式前置）：
    // 操作数序 = ForwardedGenericPacks（$.generic.<Name>，声明序）+
    // 源码层显式值实参（含 .kwargs./.vargs.）；void / IsVoid → noret
    internal sealed class CallInnerEmitter : EmitVisitor<CallInnerEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var callInner = (LoweredCallInnerExpression)node;
            var args = new List<BilVariableOperand>(
                callInner.ForwardedGenericPacks.Count + callInner.Arguments.Count);
            // BIL §7.2 / §15.4：可变泛型包前置（复用 MaterializeTypeId——
            // GenericParameterSymbol → BilOp.Var(".generic." + Name)，
            // 不手写 $ 前缀；.args 已声明，零指令转发）
            foreach (var pack in callInner.ForwardedGenericPacks)
            {
                args.Add(EmittingFacility.MaterializeTypeId(pack, callInner, target, ctx, env));
            }
            foreach (var argument in callInner.Arguments)
            {
                args.Add(EmitValueDispatcher.Visit(argument, target, ctx, env));
            }
            if (callInner.IsVoid)
            {
                target.Instructions.Add(new InvokeNoResultInstruction(
                    BilOp.Fn(BilSpellings.InnerReservedFunction), args) { Origin = callInner });
                return BilOp.Var("<void>");
            }
            var result = ctx.Temps.NewTemp(callInner.Type);
            target.Instructions.Add(new InvokeInstruction(
                BilOp.Fn(BilSpellings.InnerReservedFunction), result, args) { Origin = callInner });
            return result;
        }
    }

    internal sealed class AwaitEmitter : EmitVisitor<AwaitEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var awaitExpression = (LoweredAwaitExpression)node;
            var task = EmitValueDispatcher.Visit(awaitExpression.Operand, target, ctx, env);
            if (!awaitExpression.HasResult)
            {
                target.Instructions.Add(new AwaitInstruction(task) { Origin = awaitExpression });
                return BilOp.Var("<void>");
            }
            var result = ctx.Temps.NewTemp(awaitExpression.ResultType!);
            target.Instructions.Add(new AwaitInstruction(task, result) { Origin = awaitExpression });
            return result;
        }
    }

    // cell 对象引用（SYNTAX §5.2 闭包模型）：cell 变量名即操作数，零指令
    // （Symbol 恒为 LocalSymbol——源码局部的 cell 变量即原名 var，被捕获
    // 参数的为 .c.<名> 合成局部）
    internal sealed class CellReferenceEmitter : EmitVisitor<CellReferenceEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var cellReference = (LoweredCellReferenceExpression)node;
            return BilOp.Var(cellReference.Symbol.Name);
        }
    }


    // super ABI = $.this + generic hidden args + normal args；BIL 不泄露 base canonical 名。
    internal sealed class SuperCallEmitter : EmitVisitor<SuperCallEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var super = (LoweredSuperCallExpression)node;
            var args = new List<BilVariableOperand> { BilOp.Var(".this") };
            foreach (var typeArgument in super.TypeArguments)
            {
                args.Add(EmittingFacility.MaterializeTypeId(typeArgument, super, target, ctx, env));
            }
            if (super.GenericPack != null)
            {
                args.Add(GenericVarArgsEmitter.Visit(super.GenericPack, target, ctx, env));
            }
            foreach (var argument in super.Arguments)
            {
                args.Add(EmitValueDispatcher.Visit(argument, target, ctx, env));
            }
            if (super.IsVoid)
            {
                target.Instructions.Add(new InvokeNoResultInstruction(
                    BilOp.Fn(BilSpellings.SuperReservedFunction), args) { Origin = super });
                return BilOp.Var("<void>");
            }
            var result = ctx.Temps.NewTemp(super.Type);
            target.Instructions.Add(new InvokeInstruction(
                BilOp.Fn(BilSpellings.SuperReservedFunction), result, args) { Origin = super });
            return result;
        }
    }

    // 索引读取（S8c，§13.6：get.array COLLECTION INDEX RESULT）——
    // collection/index 物化，结果物化 .t 临时变量；写入形态（赋值目标）
    // 见 AssignmentEmitter 的 set.array 分支
    internal sealed class IndexAccessEmitter : EmitVisitor<IndexAccessEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var indexAccess = (LoweredIndexExpression)node;
            var collection = EmitValueDispatcher.Visit(indexAccess.Receiver, target, ctx, env);
            var index = EmitValueDispatcher.Visit(indexAccess.Index, target, ctx, env);
            var accessResult = ctx.Temps.NewTemp(indexAccess.Type);
            target.Instructions.Add(new GetArrayInstruction(collection, index, accessResult)
            { Origin = indexAccess });
            return accessResult;
        }
    }

    // cast（S7e，§12.1/§12.2）：SOURCE RESULT type(TARGET_TYPE)，
    // 结果物化 .t 临时变量
    internal sealed class CastEmitter : EmitVisitor<CastEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var cast = (LoweredCastExpression)node;
            var castSourceValue = EmitValueDispatcher.Visit(cast.Source, target, ctx, env);
            var castResult = ctx.Temps.NewTemp(cast.Type);
            target.Instructions.Add(new CastInstruction(
                castSourceValue, castResult,
                BilOp.Type(CanonicalSymbolPrinter.PrintType(cast.TargetType)), cast.IsSafe)
            { Origin = cast });
            return castResult;
        }
    }

    // is/supers/with（S8a，§12.3）：
    // 静态 type.X VALUE type(TARGET_TYPE) RESULT；
    // 动态 type.X.indirect VALUE TYPEID_VAR RESULT。
    // S11：is .Case 判别匹配发独立的 type.is.case VALUE case(CASE) RESULT
    // （无 BilTypeCheckKind 映射——不属于 type.is/supers/with 家族，须先分流）
    internal sealed class TypeCheckEmitter : EmitVisitor<TypeCheckEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var typeCheck = (LoweredTypeCheckExpression)node;
            var checkValue = EmitValueDispatcher.Visit(typeCheck.Operand, target, ctx, env);
            var checkResult = ctx.Temps.NewTemp(typeCheck.Type);
            if (typeCheck.Kind == BoundTypeCheckKind.IsCase)
            {
                target.Instructions.Add(new IsCaseInstruction(checkValue,
                    BilOp.Case(CanonicalSymbolPrinter.PrintCase(typeCheck.Case!)), checkResult)
                { Origin = typeCheck });
                return checkResult;
            }
            var checkKind = EmittingFacility.MapTypeCheckKind(typeCheck.Kind);
            if (typeCheck.TargetValue != null)
            {
                var typeIdVar = EmitValueDispatcher.Visit(typeCheck.TargetValue, target, ctx, env);
                target.Instructions.Add(new IndirectTypeCheckInstruction(
                    checkKind, checkValue, typeIdVar, checkResult)
                { Origin = typeCheck });
            }
            else
            {
                target.Instructions.Add(new DirectTypeCheckInstruction(
                    checkKind, checkValue,
                    BilOp.Type(CanonicalSymbolPrinter.PrintType(typeCheck.TargetType!)),
                    checkResult)
                { Origin = typeCheck });
            }
            return checkResult;
        }
    }

    // typeOf（S8a，§12.5）：值形态 getid.var VALUE RESULT；
    // 类型形态 getid.type type(TYPE_SYMBOL) RESULT
    internal sealed class TypeOfEmitter : EmitVisitor<TypeOfEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var typeOf = (LoweredTypeOfExpression)node;
            var typeOfResult = ctx.Temps.NewTemp(typeOf.Type);
            if (typeOf.Operand != null)
            {
                var typeOfValue = EmitValueDispatcher.Visit(typeOf.Operand, target, ctx, env);
                target.Instructions.Add(new GetIdVarInstruction(typeOfValue, typeOfResult)
                { Origin = typeOf });
            }
            else
            {
                target.Instructions.Add(new GetIdTypeInstruction(
                    BilOp.Type(CanonicalSymbolPrinter.PrintType(typeOf.TargetType!)),
                    typeOfResult)
                { Origin = typeOf });
            }
            return typeOfResult;
        }
    }

    // 可变参数包打包（S9d，§7.1/§14）：调用点把归包实参构造为隐藏包值——
    // 位置包 new type(.array<.any>) [元素装箱 cast 到 .any...]；具名包
    // 每项先 new type(core::Pair<.string, .any>)（名字字符串资源 + 值装箱
    // cast）再包进 new type(.array<core::Pair<.string, .any>>)。
    // 结果类型与 fn .args 的 .vargs./.kwargs. 条目同元素类型（§7.1——
    // core::Pair 非内建，经 canonical 投影而非 .pair 构造头别名）
    internal sealed class VarArgsEmitter : EmitVisitor<VarArgsEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var pack = (LoweredVarArgsArgument)node;
            var anyType = env.Unit.Symbols.Bootstrap.Any;
            if (!pack.IsNamed)
            {
                var packed = new List<BilVariableOperand>();
                foreach (var value in pack.Values)
                {
                    packed.Add(BoxToAny(value, target, ctx, env));
                }
                var packResult = ctx.Temps.NewTemp(pack.Type);
                target.Instructions.Add(new NewInstruction(
                    BilOp.Type(CanonicalSymbolPrinter.PrintType(pack.Type)),
                    packResult, packed)
                { Origin = pack });
                return packResult;
            }
            // 具名包：pair 逐项构造后装入 array——结果类型 =
            // .array<.pair<.string, .any>>（与 fn .args 的 .kwargs.<名>
            // 契约一致，§7.1）；pairTemp 类型即容器元素类型，无需装箱
            var pairType = env.Unit.Symbols.GetConstructedType(
                BootstrapPairDefinition(env), env.Unit.Symbols.Bootstrap.String, anyType);
            var pairValues = EmitNamedPairs(pack, pack.NamedValues, pairType,
                value => BoxToAny(value, target, ctx, env), target, ctx, env);
            var namedPackType = env.Unit.Symbols.GetConstructedType(
                env.Unit.Symbols.Bootstrap.ArrayDefinition, pairType);
            var namedPackResult = ctx.Temps.NewTemp(namedPackType);
            target.Instructions.Add(new NewInstruction(
                BilOp.Type(CanonicalSymbolPrinter.PrintType(namedPackType)),
                namedPackResult, pairValues)
            { Origin = pack });
            return namedPackResult;
        }

        // 具名包逐项 pair 构造（值包/泛型包共用）：每项 = 名字字符串资源
        // load + new type(.pair<.string, ELEM>) [名, 元素操作数]；元素
        // 物化（值装箱到 Any / 类型 getid.type）由调用方回调承担
        internal static List<BilVariableOperand> EmitNamedPairs<T>(
            LoweredNode origin, IReadOnlyList<(string Name, T Item)> items, TypeSymbol pairType,
            Func<T, BilVariableOperand> materializeElement, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var pairValues = new List<BilVariableOperand>();
            foreach (var (name, item) in items)
            {
                var nameResource = EmittingFacility.RegisterScalarResource(BilScalarType.String,
                    "\"" + EmittingFacility.Escape(name) + "\"", env);
                var nameTemp = ctx.Temps.NewTemp(env.Unit.Symbols.Bootstrap.String);
                target.Instructions.Add(new LoadInstruction(nameResource, nameTemp)
                { Origin = origin });
                var pairTemp = ctx.Temps.NewTemp(pairType);
                target.Instructions.Add(new NewInstruction(
                    BilOp.Type(CanonicalSymbolPrinter.PrintType(pairType)),
                    pairTemp, new List<BilVariableOperand> { nameTemp, materializeElement(item) })
                { Origin = origin });
                pairValues.Add(pairTemp);
            }
            return pairValues;
        }

        // 值装箱到统一 Any 槽（RUNTIME §10；§12.1 引用视图转换——非 Any
        // 时包显式 cast，Any 直通）
        private static BilVariableOperand BoxToAny(LoweredExpression value, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var valueOperand = EmitValueDispatcher.Visit(value, target, ctx, env);
            var anyType = env.Unit.Symbols.Bootstrap.Any;
            if (value.Type is TypeSymbol valueType && ReferenceEquals(valueType, anyType))
            {
                return valueOperand;
            }
            var boxed = ctx.Temps.NewTemp(anyType);
            target.Instructions.Add(new CastInstruction(valueOperand, boxed,
                BilOp.Type(CanonicalSymbolPrinter.PrintType(anyType)), isSafe: false)
            { Origin = value });
            return boxed;
        }

        // 标准 Pair 定义（.pair<.string, .any>）：bootstrap 无 Pair——
        // stdlib .bootstrap.latte 自举（core::Pair）；查命名空间兜底
        internal static TypeSymbol BootstrapPairDefinition(EmitEnvironment env)
        {
            var core = env.Unit.Symbols.GlobalNamespace.ChildNamespaces
                .FirstOrDefault(n => n.Name == "core");
            return core?.Types.FirstOrDefault(t => t.Name == "Pair")
                ?? throw new CompilerInternalException(
                    "stdlib core::Pair 缺失（kwargs 打包依赖）");
        }
    }

    // 泛型可变参数包打包（S9d-2，§7.1/§14）：调用点把推导的类型实参构造
    // 为隐藏包值——位置包 new type(.array<.typeid<.any>>) [逐项 getid.type...]；
    // 具名包逐项 new type(core::Pair<.string, .typeid<.any>>)（名字字符串
    // 资源 + 类型 getid.type）再包进 new type(.map<.string, .typeid<.any>>)。
    // 类型实参物化复用 MaterializeTypeId（静态实参 getid.type / 嵌套泛型
    // 调用转发 $.generic.T），结果类型与 fn .args 的 .generic.* 包条目一致
    // （§7.1：.array<.typeid> / .map<.string, .typeid>，无边界的 .typeid
    // ≡ .typeid<.any>）
    internal sealed class GenericVarArgsEmitter
        : EmitVisitor<GenericVarArgsEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var pack = (LoweredGenericVarArgsArgument)node;
            var typeIdType = env.Unit.Symbols.GetConstructedType(
                env.Unit.Symbols.Bootstrap.TypeDefinition, env.Unit.Symbols.Bootstrap.Any);
            if (!pack.IsNamed)
            {
                var packed = new List<BilVariableOperand>();
                foreach (var typeArgument in pack.TypeArguments)
                {
                    packed.Add(EmittingFacility.MaterializeTypeId(typeArgument, pack, target,
                        ctx, env));
                }
                var packType = env.Unit.Symbols.GetConstructedType(
                    env.Unit.Symbols.Bootstrap.ArrayDefinition, typeIdType);
                var packResult = ctx.Temps.NewTemp(packType);
                target.Instructions.Add(new NewInstruction(
                    BilOp.Type(CanonicalSymbolPrinter.PrintType(packType)),
                    packResult, packed)
                { Origin = pack });
                return packResult;
            }
            // 具名包：pair 逐项构造（名 + 类型 typeid）后装入 map
            var pairType = env.Unit.Symbols.GetConstructedType(
                VarArgsEmitter.BootstrapPairDefinition(env),
                env.Unit.Symbols.Bootstrap.String, typeIdType);
            var pairValues = VarArgsEmitter.EmitNamedPairs(pack, pack.NamedTypes, pairType,
                typeArgument => EmittingFacility.MaterializeTypeId(typeArgument, pack, target,
                    ctx, env),
                target, ctx, env);
            var mapType = env.Unit.Symbols.GetConstructedType(
                env.Unit.Symbols.Bootstrap.MapDefinition,
                env.Unit.Symbols.Bootstrap.String, typeIdType);
            var namedPackResult = ctx.Temps.NewTemp(mapType);
            target.Instructions.Add(new NewInstruction(
                BilOp.Type(CanonicalSymbolPrinter.PrintType(mapType)),
                namedPackResult, pairValues)
            { Origin = pack });
            return namedPackResult;
        }
    }
}
