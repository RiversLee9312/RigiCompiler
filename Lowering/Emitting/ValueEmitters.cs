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
    // §7.1——源码参数名是包变量，BIL 以保留名承载）
    internal sealed class ValueReferenceEmitter : EmitVisitor<ValueReferenceEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var valueReference = (LoweredValueReferenceExpression)node;
            // named 参数 IsVariadic 与 IsNamedVariadic 同时为 true——具名先判
            var name = valueReference.Symbol switch
            {
                ParameterSymbol { IsNamedVariadic: true } parameter => ".kwargs." + parameter.Name,
                ParameterSymbol { IsVariadic: true } parameter => ".vargs." + parameter.Name,
                _ => valueReference.Symbol.Name,
            };
            return BilOp.Var(name);
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
    // 动态 type.X.indirect VALUE TYPEID_VAR RESULT
    internal sealed class TypeCheckEmitter : EmitVisitor<TypeCheckEmitter, BilVariableOperand>
    {
        protected override BilVariableOperand VisitCore(LoweredNode node, BilBlock target,
            EmitContext ctx, EmitEnvironment env)
        {
            var typeCheck = (LoweredTypeCheckExpression)node;
            var checkValue = EmitValueDispatcher.Visit(typeCheck.Operand, target, ctx, env);
            var checkResult = ctx.Temps.NewTemp(typeCheck.Type);
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
    // 每项先 new type(.pair<.string, .any>)（名字字符串资源 + 值装箱
    // cast）再包进 .array<.pair<.string, .any>>。结果类型 = .array<.any>
    // / .array<.pair<.string, .any>>（与 fn .args 的 .vargs./.kwargs. 类型一致）
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
            // 具名包：pair 逐项构造后装入 array
            var pairType = env.Unit.Symbols.GetConstructedType(
                BootstrapPairDefinition(env), env.Unit.Symbols.Bootstrap.String, anyType);
            var pairValues = new List<BilVariableOperand>();
            foreach (var (name, value) in pack.NamedValues)
            {
                var nameResource = EmittingFacility.RegisterScalarResource(BilScalarType.String,
                    "\"" + EmittingFacility.Escape(name) + "\"", env);
                var nameTemp = ctx.Temps.NewTemp(env.Unit.Symbols.Bootstrap.String);
                target.Instructions.Add(new LoadInstruction(nameResource, nameTemp)
                { Origin = pack });
                var pairTemp = ctx.Temps.NewTemp(pairType);
                target.Instructions.Add(new NewInstruction(
                    BilOp.Type(CanonicalSymbolPrinter.PrintType(pairType)),
                    pairTemp, new List<BilVariableOperand> { nameTemp, BoxToAny(value, target, ctx, env) })
                { Origin = pack });
                pairValues.Add(pairTemp);
            }
            var namedPackResult = ctx.Temps.NewTemp(pack.Type);
            target.Instructions.Add(new NewInstruction(
                BilOp.Type(CanonicalSymbolPrinter.PrintType(pack.Type)),
                namedPackResult, pairValues)
            { Origin = pack });
            return namedPackResult;
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
            var pairValues = new List<BilVariableOperand>();
            foreach (var (name, typeArgument) in pack.NamedTypes)
            {
                var nameResource = EmittingFacility.RegisterScalarResource(BilScalarType.String,
                    "\"" + EmittingFacility.Escape(name) + "\"", env);
                var nameTemp = ctx.Temps.NewTemp(env.Unit.Symbols.Bootstrap.String);
                target.Instructions.Add(new LoadInstruction(nameResource, nameTemp)
                { Origin = pack });
                var pairTemp = ctx.Temps.NewTemp(pairType);
                target.Instructions.Add(new NewInstruction(
                    BilOp.Type(CanonicalSymbolPrinter.PrintType(pairType)),
                    pairTemp, new List<BilVariableOperand>
                    {
                        nameTemp,
                        EmittingFacility.MaterializeTypeId(typeArgument, pack, target, ctx, env),
                    })
                { Origin = pack });
                pairValues.Add(pairTemp);
            }
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
