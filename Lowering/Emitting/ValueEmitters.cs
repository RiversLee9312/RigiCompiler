using LatteCompiler.Bil;

namespace LatteCompiler
{
    // 值发射（S6–S8a；BIL §10–§15）。自旧 EmitSession.EmitValue 各分支
    // 迁移，行为不变——表达式物化为变量操作数（§10.1），返回变量名。

    // 字面量：提取进 Resources（§4.2），经 load res(...) 引用（§13.1）
    internal sealed class LiteralEmitter : EmitVisitor<LiteralEmitter, string>
    {
        protected override string VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var literal = (LoweredLiteralExpression)node;
            var resource = EmittingFacility.RegisterResource(literal, env);
            var temp = EmittingFacility.NewTemp(literal.Type, ctx);
            target.Instructions.Add(new BilInstruction("load",
                BilOp.Res(resource), BilOp.Var(temp))
            { Origin = literal });
            return temp;
        }
    }

    // P4a 合成常量（S7b bool；S7f null——安全访问/空值
    // 回退脱糖产物）：与字面量同路进 Resources（同键去重）
    internal sealed class ConstantEmitter : EmitVisitor<ConstantEmitter, string>
    {
        protected override string VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var constant = (LoweredConstantExpression)node;
            var constantResource = constant.Value is bool boolValue
                ? EmittingFacility.RegisterScalarResource("bool", boolValue ? "true" : "false", env)
                : constant.Value is null
                    ? EmittingFacility.RegisterNullResource(constant.Type,
                        constant.Origin.Syntax.Span, env)
                    : throw new CompilerInternalException(
                        "P4a 合成常量类型未覆盖: " + constant.Value.GetType().Name);
            var constantTemp = EmittingFacility.NewTemp(constant.Type, ctx);
            target.Instructions.Add(new BilInstruction("load",
                BilOp.Res(constantResource), BilOp.Var(constantTemp))
            { Origin = constant });
            return constantTemp;
        }
    }

    // 值引用（局部/参数）：名字即操作数，零指令
    internal sealed class ValueReferenceEmitter : EmitVisitor<ValueReferenceEmitter, string>
    {
        protected override string VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var valueReference = (LoweredValueReferenceExpression)node;
            return valueReference.Symbol.Name;
        }
    }

    // 全局/static 字段读取（§13.4）
    internal sealed class FieldReferenceEmitter : EmitVisitor<FieldReferenceEmitter, string>
    {
        protected override string VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var fieldReference = (LoweredFieldReferenceExpression)node;
            var ownerRef = EmittingFacility.FieldOwnerRef(fieldReference.Field, env);
            if (ownerRef == null) return "<error>";    // 已诊断
            var fieldValue = EmittingFacility.NewTemp(fieldReference.Type, ctx);
            target.Instructions.Add(new BilInstruction("get.field.static",
                BilOp.Var(fieldValue), BilOp.Type(ownerRef),
                BilOp.Field(CanonicalSymbolPrinter.PrintField(fieldReference.Field)))
            { Origin = fieldReference });
            return fieldValue;
        }
    }

    // 二元 intrinsic 运算（§11，opcode 单点映射）
    internal sealed class BinaryEmitter : EmitVisitor<BinaryEmitter, string>
    {
        protected override string VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var binary = (LoweredBinaryExpression)node;
            var left = EmitValueDispatcher.Visit(binary.Left, target, ctx, env);
            var right = EmitValueDispatcher.Visit(binary.Right, target, ctx, env);
            var binaryResult = EmittingFacility.NewTemp(binary.Type, ctx);
            target.Instructions.Add(new BilInstruction(EmittingFacility.IntrinsicOpcode(binary.Op),
                BilOp.Var(left), BilOp.Var(right), BilOp.Var(binaryResult))
            { Origin = binary });
            return binaryResult;
        }
    }

    // 一元 intrinsic 运算（§11，opcode 单点映射）
    internal sealed class UnaryEmitter : EmitVisitor<UnaryEmitter, string>
    {
        protected override string VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var unary = (LoweredUnaryExpression)node;
            var operand = EmitValueDispatcher.Visit(unary.Operand, target, ctx, env);
            var unaryResult = EmittingFacility.NewTemp(unary.Type, ctx);
            target.Instructions.Add(new BilInstruction(EmittingFacility.IntrinsicOpcode(unary.Op),
                BilOp.Var(operand), BilOp.Var(unaryResult))
            { Origin = unary });
            return unaryResult;
        }
    }

    // 带返回值调用（§15.1）：实参从左到右物化（§10.2），再发 invoke
    internal sealed class CallExpressionEmitter : EmitVisitor<CallExpressionEmitter, string>
    {
        protected override string VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var callExpression = (LoweredCallExpression)node;
            var callArguments = new List<BilOperand>();
            foreach (var argument in callExpression.Arguments)
            {
                callArguments.Add(BilOp.Var(EmitValueDispatcher.Visit(argument, target, ctx, env)));
            }
            var callResult = EmittingFacility.NewTemp(callExpression.Type, ctx);
            target.Instructions.Add(new BilInstruction("invoke",
                BilOp.Fn(CanonicalSymbolPrinter.PrintMethod(callExpression.Method)),
                BilOp.Var(callResult), BilOp.List(callArguments.ToArray()))
            { Origin = callExpression });
            return callResult;
        }
    }

    // new 构造（§14.1：init 选择归 Middleware（按精确参数类型），
    // 发射不写 init 符号）
    internal sealed class NewExpressionEmitter : EmitVisitor<NewExpressionEmitter, string>
    {
        protected override string VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var newExpression = (LoweredNewExpression)node;
            var newArguments = new List<BilOperand>();
            foreach (var argument in newExpression.Arguments)
            {
                newArguments.Add(BilOp.Var(EmitValueDispatcher.Visit(argument, target, ctx, env)));
            }
            var newResult = EmittingFacility.NewTemp(newExpression.Type, ctx);
            target.Instructions.Add(new BilInstruction("new",
                BilOp.Type(CanonicalSymbolPrinter.PrintType(newExpression.Type)),
                BilOp.Var(newResult), BilOp.List(newArguments.ToArray()))
            { Origin = newExpression });
            return newResult;
        }
    }

    // this → $.this 变量操作数（§7.3，零指令——.this 在
    // .args 已声明，与参数同 $ 引用形式 §9.3）
    internal sealed class ThisEmitter : EmitVisitor<ThisEmitter, string>
    {
        protected override string VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            return ".this";
        }
    }

    // 实例调用（§7.3/§15.1）：receiver 求值作首实参；
    // 接口方法符号引用时分派归 Middleware（注释约定）
    internal sealed class InstanceCallEmitter : EmitVisitor<InstanceCallEmitter, string>
    {
        protected override string VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var instCall = (LoweredInstanceCallExpression)node;
            var instReceiver = EmitValueDispatcher.Visit(instCall.Receiver, target, ctx, env);
            var instArguments = new List<BilOperand> { BilOp.Var(instReceiver) };
            foreach (var argument in instCall.Arguments)
            {
                instArguments.Add(BilOp.Var(EmitValueDispatcher.Visit(argument, target, ctx, env)));
            }
            var instResult = EmittingFacility.NewTemp(instCall.Type, ctx);
            target.Instructions.Add(new BilInstruction("invoke",
                BilOp.Fn(CanonicalSymbolPrinter.PrintMethod(instCall.Method)),
                BilOp.Var(instResult), BilOp.List(instArguments.ToArray()))
            { Origin = instCall });
            return instResult;
        }
    }

    // 实例字段读取（§13.3：get.field OBJECT TARGET field(F)）
    internal sealed class FieldAccessEmitter : EmitVisitor<FieldAccessEmitter, string>
    {
        protected override string VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var fieldAccess = (LoweredFieldAccessExpression)node;
            var accessReceiver = EmitValueDispatcher.Visit(fieldAccess.Receiver, target, ctx, env);
            var accessResult = EmittingFacility.NewTemp(fieldAccess.Type, ctx);
            target.Instructions.Add(new BilInstruction("get.field",
                BilOp.Var(accessReceiver), BilOp.Var(accessResult),
                BilOp.Field(CanonicalSymbolPrinter.PrintField(fieldAccess.Field)))
            { Origin = fieldAccess });
            return accessResult;
        }
    }

    // cast（S7e，§12.1/§12.2）：SOURCE RESULT type(TARGET_TYPE)，
    // 结果物化 .t 临时变量
    internal sealed class CastEmitter : EmitVisitor<CastEmitter, string>
    {
        protected override string VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var cast = (LoweredCastExpression)node;
            var castSourceValue = EmitValueDispatcher.Visit(cast.Source, target, ctx, env);
            var castResult = EmittingFacility.NewTemp(cast.Type, ctx);
            target.Instructions.Add(new BilInstruction(
                cast.IsSafe ? "cast.safe" : "cast",
                BilOp.Var(castSourceValue), BilOp.Var(castResult),
                BilOp.Type(CanonicalSymbolPrinter.PrintType(cast.TargetType)))
            { Origin = cast });
            return castResult;
        }
    }

    // is/supers/with（S8a，§12.3）：
    // 静态 type.X VALUE type(TARGET_TYPE) RESULT；
    // 动态 type.X.indirect VALUE TYPEID_VAR RESULT
    internal sealed class TypeCheckEmitter : EmitVisitor<TypeCheckEmitter, string>
    {
        protected override string VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var typeCheck = (LoweredTypeCheckExpression)node;
            var checkValue = EmitValueDispatcher.Visit(typeCheck.Operand, target, ctx, env);
            var checkResult = EmittingFacility.NewTemp(typeCheck.Type, ctx);
            var checkOpcode = typeCheck.Kind switch
            {
                BoundTypeCheckKind.Is => "type.is",
                BoundTypeCheckKind.Supers => "type.supers",
                BoundTypeCheckKind.With => "type.with",
                _ => throw new CompilerInternalException(
                    "未知类型检查种类: " + typeCheck.Kind),
            };
            if (typeCheck.TargetValue != null)
            {
                var typeIdVar = EmitValueDispatcher.Visit(typeCheck.TargetValue, target, ctx, env);
                target.Instructions.Add(new BilInstruction(checkOpcode + ".indirect",
                    BilOp.Var(checkValue), BilOp.Var(typeIdVar),
                    BilOp.Var(checkResult))
                { Origin = typeCheck });
            }
            else
            {
                target.Instructions.Add(new BilInstruction(checkOpcode,
                    BilOp.Var(checkValue),
                    BilOp.Type(CanonicalSymbolPrinter.PrintType(typeCheck.TargetType!)),
                    BilOp.Var(checkResult))
                { Origin = typeCheck });
            }
            return checkResult;
        }
    }

    // typeOf（S8a，§12.5）：值形态 getid.var VALUE RESULT；
    // 类型形态 getid.type type(TYPE_SYMBOL) RESULT
    internal sealed class TypeOfEmitter : EmitVisitor<TypeOfEmitter, string>
    {
        protected override string VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var typeOf = (LoweredTypeOfExpression)node;
            var typeOfResult = EmittingFacility.NewTemp(typeOf.Type, ctx);
            if (typeOf.Operand != null)
            {
                var typeOfValue = EmitValueDispatcher.Visit(typeOf.Operand, target, ctx, env);
                target.Instructions.Add(new BilInstruction("getid.var",
                    BilOp.Var(typeOfValue), BilOp.Var(typeOfResult))
                { Origin = typeOf });
            }
            else
            {
                target.Instructions.Add(new BilInstruction("getid.type",
                    BilOp.Type(CanonicalSymbolPrinter.PrintType(typeOf.TargetType!)),
                    BilOp.Var(typeOfResult))
                { Origin = typeOf });
            }
            return typeOfResult;
        }
    }
}
