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
            var temp = EmittingFacility.NewTemp(literal.Type, ctx);
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
            var constantTemp = EmittingFacility.NewTemp(constant.Type, ctx);
            target.Instructions.Add(new LoadInstruction(constantResource, constantTemp)
            { Origin = constant });
            return constantTemp;
        }
    }

    // 值引用（局部/参数）：名字即操作数，零指令
    internal sealed class ValueReferenceEmitter : EmitVisitor<ValueReferenceEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var valueReference = (LoweredValueReferenceExpression)node;
            return BilOp.Var(valueReference.Symbol.Name);
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
            var fieldValue = EmittingFacility.NewTemp(fieldReference.Type, ctx);
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
            var binaryResult = EmittingFacility.NewTemp(binary.Type, ctx);
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
            var unaryResult = EmittingFacility.NewTemp(unary.Type, ctx);
            target.Instructions.Add(new UnaryIntrinsicInstruction(
                EmittingFacility.MapUnaryOp(unary.Op), operand, unaryResult)
            { Origin = unary });
            return unaryResult;
        }
    }

    // 带返回值调用（§15.1）：实参从左到右物化（§10.2），再发 invoke
    internal sealed class CallExpressionEmitter : EmitVisitor<CallExpressionEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var callExpression = (LoweredCallExpression)node;
            var callArguments = new List<BilVariableOperand>();
            foreach (var argument in callExpression.Arguments)
            {
                callArguments.Add(EmitValueDispatcher.Visit(argument, target, ctx, env));
            }
            var callResult = EmittingFacility.NewTemp(callExpression.Type, ctx);
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
            var newResult = EmittingFacility.NewTemp(newExpression.Type, ctx);
            target.Instructions.Add(new NewInstruction(
                BilOp.Type(CanonicalSymbolPrinter.PrintType(newExpression.Type)),
                newResult, newArguments)
            { Origin = newExpression });
            return newResult;
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

    // 实例调用（§7.3/§15.1）：receiver 求值作首实参；
    // 接口方法符号引用时分派归 Middleware（注释约定）
    internal sealed class InstanceCallEmitter : EmitVisitor<InstanceCallEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var instCall = (LoweredInstanceCallExpression)node;
            var instReceiver = EmitValueDispatcher.Visit(instCall.Receiver, target, ctx, env);
            var instArguments = new List<BilVariableOperand> { instReceiver };
            foreach (var argument in instCall.Arguments)
            {
                instArguments.Add(EmitValueDispatcher.Visit(argument, target, ctx, env));
            }
            var instResult = EmittingFacility.NewTemp(instCall.Type, ctx);
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
            var accessResult = EmittingFacility.NewTemp(fieldAccess.Type, ctx);
            target.Instructions.Add(new GetFieldInstruction(
                accessReceiver, accessResult,
                BilOp.Field(CanonicalSymbolPrinter.PrintField(fieldAccess.Field)))
            { Origin = fieldAccess });
            return accessResult;
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
            var accessResult = EmittingFacility.NewTemp(indexAccess.Type, ctx);
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
            var castResult = EmittingFacility.NewTemp(cast.Type, ctx);
            target.Instructions.Add(new CastInstruction(
                castSourceValue, castResult,
                BilOp.Type(CanonicalSymbolPrinter.PrintType(cast.TargetType)), cast.IsSafe)
            { Origin = cast });
            return castResult;
        }
    }

    // is/supers/with（S8a，§12.3）：
    // 静态 type.X VALUE type(TARGET_TYPE) RESULT；
    // 动态 type.X.indirect VALUE TYPEID_VAR RESULT
    internal sealed class TypeCheckEmitter : EmitVisitor<TypeCheckEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var typeCheck = (LoweredTypeCheckExpression)node;
            var checkValue = EmitValueDispatcher.Visit(typeCheck.Operand, target, ctx, env);
            var checkResult = EmittingFacility.NewTemp(typeCheck.Type, ctx);
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
            var typeOfResult = EmittingFacility.NewTemp(typeOf.Type, ctx);
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
}
