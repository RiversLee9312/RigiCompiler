using System.Collections.Generic;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Bil
{
    // §11 运算指令与 §12 转换/运行时类型指令（M57 强类型化）。
    // 操作数全为变量（§10.1）；结果变量一律为指令最后一个操作数（§10.3）。

    // §11 二元运算维度（拼写见 BilSpellings；and/or 为不短路形态——
    // 内建 bool 短路已在 P4a 展开）
    public enum BilBinaryOp
    {
        Add, Sub, Mul, Div,             // §11.2 算术
        And, Or,                        // §11.3 逻辑（不短路）
        BinAnd, BinOr, BinXor,          // §11.4 位运算
        ShiftLeft, ShiftRight, ShiftRightUnsigned,
        CmpEq, CmpNe, CmpLt, CmpLe, CmpGt, CmpGe,   // §11.5 比较
    }

    // §11 一元运算维度
    public enum BilUnaryOp
    {
        Opposite,                       // §11.2 取负
        Not,                            // §11.3 逻辑非
        BinNot,                         // §11.4 位非
    }

    // §11 二元 intrinsic：op LEFT RIGHT RESULT
    public sealed class BinaryIntrinsicInstruction : BilInstruction
    {
        public BilBinaryOp Op { get; }
        public BilVariableOperand Left { get; }
        public BilVariableOperand Right { get; }
        public BilVariableOperand Target { get; }

        public BinaryIntrinsicInstruction(BilBinaryOp op, BilVariableOperand left,
            BilVariableOperand right, BilVariableOperand target)
        {
            Op = op;
            Left = left;
            Right = right;
            Target = target;
        }

        internal override string Opcode => BilSpellings.Of(Op);
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Left, Right, Target };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilComputeExecution.ExecuteBinary(this, context, coroutine);
        }
    }

    // §11 一元 intrinsic：op OPERAND RESULT
    public sealed class UnaryIntrinsicInstruction : BilInstruction
    {
        public BilUnaryOp Op { get; }
        public BilVariableOperand Operand { get; }
        public BilVariableOperand Target { get; }

        public UnaryIntrinsicInstruction(BilUnaryOp op, BilVariableOperand operand,
            BilVariableOperand target)
        {
            Op = op;
            Operand = operand;
            Target = target;
        }

        internal override string Opcode => BilSpellings.Of(Op);
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Operand, Target };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilComputeExecution.ExecuteUnary(this, context, coroutine);
        }
    }

    // §12.1/§12.2 转换：cast|cast.safe SOURCE RESULT type(TARGET_TYPE)
    public sealed class CastInstruction : BilInstruction
    {
        public BilVariableOperand Source { get; }
        public BilVariableOperand Target { get; }
        public BilTypeOperand TargetType { get; }
        // false = §12.1 强制转换（失败抛 CastException）；
        // true = §12.2 安全转换（失败产 null）
        public bool IsSafe { get; }

        public CastInstruction(BilVariableOperand source, BilVariableOperand target,
            BilTypeOperand targetType, bool isSafe)
        {
            Source = source;
            Target = target;
            TargetType = targetType;
            IsSafe = isSafe;
        }

        internal override string Opcode => IsSafe ? "cast.safe" : "cast";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Source, Target, TargetType };

        // §12.1/§12.2 完整语义：数值转换、引用上下转、.any 装拆箱、.nullable。
        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            var source = coroutine.ReadVar(Source.Name);
            var converted = IsSafe
                ? VmTypeOps.CastSafe(context, coroutine, source, TargetType.TypeRef)
                : VmTypeOps.CastOrThrow(context, coroutine, source, TargetType.TypeRef);
            coroutine.WriteVar(Target.Name, converted);
        }
    }

    // §12.1/§12.2 动态转换：cast.indirect|cast.safe.indirect SOURCE RESULT TYPEID_VAR
    public sealed class CastIndirectInstruction : BilInstruction
    {
        public BilVariableOperand Source { get; }
        public BilVariableOperand Target { get; }
        public BilVariableOperand TypeId { get; }
        public bool IsSafe { get; }

        public CastIndirectInstruction(BilVariableOperand source, BilVariableOperand target,
            BilVariableOperand typeId, bool isSafe)
        {
            Source = source;
            Target = target;
            TypeId = typeId;
            IsSafe = isSafe;
        }

        internal override string Opcode => IsSafe ? "cast.safe.indirect" : "cast.indirect";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Source, Target, TypeId };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            var source = coroutine.ReadVar(Source.Name);
            var targetType = VmTypeOps.RequireTypeId(coroutine.ReadVar(TypeId.Name));
            var converted = IsSafe
                ? VmTypeOps.CastSafe(context, coroutine, source, targetType)
                : VmTypeOps.CastOrThrow(context, coroutine, source, targetType);
            coroutine.WriteVar(Target.Name, converted);
        }
    }

    // §12.3 类型检查种类
    public enum BilTypeCheckKind
    {
        Is,         // type.is：运行时类型是目标类型或其子类型
        Supers,     // type.supers：运行时类型是目标类型的超类型
        With,       // type.with：运行时类型附着的 wrapper 链含目标 wrapper
    }

    // §12.3 类型检查基类：type.X VALUE ... RESULT（双形态见子类）
    public abstract class TypeCheckInstruction : BilInstruction
    {
        public BilTypeCheckKind Kind { get; }
        public BilVariableOperand Value { get; }
        public BilVariableOperand Target { get; }

        protected TypeCheckInstruction(BilTypeCheckKind kind, BilVariableOperand value,
            BilVariableOperand target)
        {
            Kind = kind;
            Value = value;
            Target = target;
        }

        // 动态形态追加 .indirect 后缀
        internal abstract bool IsIndirect { get; }
        internal override string Opcode =>
            BilSpellings.Of(Kind) + (IsIndirect ? ".indirect" : "");

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            var value = coroutine.ReadVar(Value.Name);
            var targetType = ResolveTargetType(coroutine);
            var matched = Kind switch
            {
                BilTypeCheckKind.Is => VmTypeOps.Is(context, coroutine, value, targetType),
                BilTypeCheckKind.Supers => VmTypeOps.Supers(context, coroutine, value, targetType),
                BilTypeCheckKind.With => VmTypeOps.With(context, coroutine, value, targetType),
                _ => throw new VmException("未知类型检查 " + Kind),
            };
            coroutine.WriteVar(Target.Name, new VmBool(matched));
        }

        private string ResolveTargetType(VmCoroutine coroutine)
        {
            if (this is DirectTypeCheckInstruction direct)
            {
                return direct.TargetType.TypeRef;
            }
            if (this is IndirectTypeCheckInstruction indirect)
            {
                return VmTypeOps.RequireTypeId(coroutine.ReadVar(indirect.TypeId.Name));
            }
            throw new VmException("未知 type.check 形态");
        }
    }

    // §12.3 静态形态：type.X VALUE type(TARGET_TYPE) RESULT
    public sealed class DirectTypeCheckInstruction : TypeCheckInstruction
    {
        public BilTypeOperand TargetType { get; }

        public DirectTypeCheckInstruction(BilTypeCheckKind kind, BilVariableOperand value,
            BilTypeOperand targetType, BilVariableOperand target)
            : base(kind, value, target)
        {
            TargetType = targetType;
        }

        internal override bool IsIndirect => false;
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Value, TargetType, Target };
    }

    // §12.3 动态形态：type.X.indirect VALUE TYPEID_VAR RESULT
    public sealed class IndirectTypeCheckInstruction : TypeCheckInstruction
    {
        public BilVariableOperand TypeId { get; }

        public IndirectTypeCheckInstruction(BilTypeCheckKind kind, BilVariableOperand value,
            BilVariableOperand typeId, BilVariableOperand target)
            : base(kind, value, target)
        {
            TypeId = typeId;
        }

        internal override bool IsIndirect => true;
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Value, TypeId, Target };
    }
    // §12.3 enum case 判别检查（S11，语义由 RUNTIME §16.3 定义）：
    // type.is.case VALUE case(ENUM_TYPE.CaseName) RESULT——隐藏判别字段与
    // case 编译期判别常量的整数比较；非子类型检查、不比较 payload、
    // 不改变 VALUE 静态类型；判别宽度 u16/u32 是布局内部细节
    public sealed class IsCaseInstruction : BilInstruction
    {
        public BilVariableOperand Value { get; }
        public BilCaseOperand Case { get; }
        public BilVariableOperand Target { get; }

        public IsCaseInstruction(BilVariableOperand value, BilCaseOperand caseOperand,
            BilVariableOperand target)
        {
            Value = value;
            Case = caseOperand;
            Target = target;
        }

        internal override string Opcode => "type.is.case";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Value, Case, Target };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            var value = coroutine.ReadVar(Value.Name);
            var matched = value is VmEnum enumValue && enumValue.CaseSymbol == Case.QualifiedName;
            coroutine.WriteVar(Target.Name, new VmBool(matched));
        }
    }

    // §12.4 取得 wrapper 值：get.wrapper VALUE type(WRAPPER_TYPE) RESULT
    public sealed class GetWrapperInstruction : BilInstruction
    {
        public BilVariableOperand Value { get; }
        public BilTypeOperand WrapperType { get; }
        public BilVariableOperand Target { get; }

        public GetWrapperInstruction(BilVariableOperand value, BilTypeOperand wrapperType,
            BilVariableOperand target)
        {
            Value = value;
            WrapperType = wrapperType;
            Target = target;
        }

        internal override string Opcode => "get.wrapper";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Value, WrapperType, Target };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            coroutine.WriteVar(Target.Name,
                VmTypeOps.GetWrapper(context, coroutine.ReadVar(Value.Name), WrapperType.TypeRef));
        }
    }

    // §12.4 动态形态：get.wrapper.indirect VALUE WRAPPER_TYPEID_VAR RESULT
    public sealed class GetWrapperIndirectInstruction : BilInstruction
    {
        public BilVariableOperand Value { get; }
        public BilVariableOperand WrapperTypeId { get; }
        public BilVariableOperand Target { get; }

        public GetWrapperIndirectInstruction(BilVariableOperand value,
            BilVariableOperand wrapperTypeId, BilVariableOperand target)
        {
            Value = value;
            WrapperTypeId = wrapperTypeId;
            Target = target;
        }

        internal override string Opcode => "get.wrapper.indirect";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Value, WrapperTypeId, Target };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            var wrapperType = VmTypeOps.RequireTypeId(coroutine.ReadVar(WrapperTypeId.Name));
            coroutine.WriteVar(Target.Name,
                VmTypeOps.GetWrapper(context, coroutine.ReadVar(Value.Name), wrapperType));
        }
    }

    // §12.4 字段-Value 应用：从属主对象的特定字段应用取得 wrapper 值拷贝
    // get.wrapper.field OBJECT field(HOST_FIELD) type(WRAPPER_TYPE) RESULT
    public sealed class GetWrapperFieldInstruction : BilInstruction
    {
        public BilVariableOperand Object { get; }
        public BilFieldOperand HostField { get; }
        public BilTypeOperand WrapperType { get; }
        public BilVariableOperand Target { get; }

        public GetWrapperFieldInstruction(BilVariableOperand objectValue, BilFieldOperand hostField,
            BilTypeOperand wrapperType, BilVariableOperand target)
        {
            Object = objectValue;
            HostField = hostField;
            WrapperType = wrapperType;
            Target = target;
        }

        internal override string Opcode => "get.wrapper.field";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Object, HostField, WrapperType, Target };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            coroutine.WriteVar(Target.Name,
                VmTypeOps.GetWrapperField(context, coroutine.ReadVar(Object.Name),
                    HostField.Symbol, WrapperType.TypeRef));
        }
    }

    // §12.5 取得宿主实例（proxy 模板）：get.self RESULT
    // 仅 wrapper-proxy 标记的 fn 体内合法；RESULT = 模板所属 wrapper 的
    // TTarget（Entity 恰一泛型参数时的代入结果）
    public sealed class GetSelfInstruction : BilInstruction
    {
        public BilVariableOperand Target { get; }

        public GetSelfInstruction(BilVariableOperand target)
        {
            Target = target;
        }

        internal override string Opcode => "get.self";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Target };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            var host = coroutine.CurrentFrame.WrapperSelfArgument;
            if (host == null)
            {
                throw new VmException("get.self 要求当前调用携带独立的 wrapper 宿主参数");
            }
            coroutine.WriteVar(Target.Name, host);
        }
    }

    // §12.6 取得值的 typeid：getid.var VALUE RESULT
    public sealed class GetIdVarInstruction : BilInstruction
    {
        public BilVariableOperand Value { get; }
        public BilVariableOperand Target { get; }

        public GetIdVarInstruction(BilVariableOperand value, BilVariableOperand target)
        {
            Value = value;
            Target = target;
        }

        internal override string Opcode => "getid.var";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Value, Target };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            coroutine.WriteVar(Target.Name,
                new VmTypeId(VmTypeOps.ActualType(coroutine.ReadVar(Value.Name))));
        }
    }

    // §12.6 取得类型的 typeid：getid.type type(TYPE_SYMBOL) RESULT
    public sealed class GetIdTypeInstruction : BilInstruction
    {
        public BilTypeOperand TargetType { get; }
        public BilVariableOperand Target { get; }

        public GetIdTypeInstruction(BilTypeOperand targetType, BilVariableOperand target)
        {
            TargetType = targetType;
            Target = target;
        }

        internal override string Opcode => "getid.type";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { TargetType, Target };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            var resolved = VmTypeOps.ResolveTypeRef(context, coroutine, TargetType.TypeRef);
            coroutine.WriteVar(Target.Name, new VmTypeId(resolved));
        }
    }

    // §12.6 取得字段 fieldid：getid.field field(FIELD_SYMBOL) TARGET_FIELDID
    public sealed class GetIdFieldInstruction : BilInstruction
    {
        public BilFieldOperand Field { get; }
        public BilVariableOperand Target { get; }

        public GetIdFieldInstruction(BilFieldOperand field, BilVariableOperand target)
        {
            Field = field;
            Target = target;
        }

        internal override string Opcode => "getid.field";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Field, Target };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            coroutine.WriteVar(Target.Name, new VmFieldId(Field.Symbol));
        }
    }

    // §11 内建运算分派（BIL_VM_DESIGN §3.3 / §5 / BIL_STANDARD §11 / §22.3）：
    // 查询键 = opcode + 精确操作数类型 + 精确结果类型；内建标量走 primitive，
    // 用户类型按精确类型解析 operator fn 以普通调用语义执行。
    internal static class BilComputeExecution
    {
        internal static void ExecuteBinary(BinaryIntrinsicInstruction instruction,
            VmContext context, VmCoroutine coroutine)
        {
            var left = coroutine.ReadVar(instruction.Left.Name);
            var right = coroutine.ReadVar(instruction.Right.Name);
            if (instruction.Op is BilBinaryOp.CmpEq or BilBinaryOp.CmpNe
                && (IsNullLike(left) || IsNullLike(right)))
            {
                var equal = IsNullLike(left) && IsNullLike(right);
                coroutine.WriteVar(instruction.Target.Name,
                    new VmBool(instruction.Op == BilBinaryOp.CmpEq ? equal : !equal));
                return;
            }
            // Span/SharedSpan 是 class：相等为引用恒等（RUNTIME §5）
            if (instruction.Op is BilBinaryOp.CmpEq or BilBinaryOp.CmpNe
                && (left is VmSpan || right is VmSpan))
            {
                var equal = ReferenceEquals(left, right);
                coroutine.WriteVar(instruction.Target.Name,
                    new VmBool(instruction.Op == BilBinaryOp.CmpEq ? equal : !equal));
                return;
            }
            if (VmTypeOps.IsPrimitiveOperand(left) && VmTypeOps.IsPrimitiveOperand(right))
            {
                coroutine.WriteVar(instruction.Target.Name,
                    EvalBinary(instruction.Op, left, right, context, coroutine));
                return;
            }
            DispatchUserBinary(instruction, context, coroutine, left, right);
        }

        private static bool IsNullLike(VmValue value)
        {
            return value is VmNull || value is VmNullable { HasValue: false };
        }

        internal static void ExecuteUnary(UnaryIntrinsicInstruction instruction,
            VmContext context, VmCoroutine coroutine)
        {
            var operand = coroutine.ReadVar(instruction.Operand.Name);
            if (VmTypeOps.IsPrimitiveOperand(operand))
            {
                coroutine.WriteVar(instruction.Target.Name, EvalUnary(instruction.Op, operand));
                return;
            }
            var name = VmTypeOps.UnaryOperatorName(instruction.Op);
            var symbol = context.FindOperator(operand.TypeRef, name, Array.Empty<VmValue>());
            if (symbol == null)
            {
                throw new VmException("没有用户 operator " + name + "：" + operand.TypeRef);
            }
            if (VmWrapperDispatch.TryStartOperatorChain(context, coroutine, symbol, operand,
                    Array.Empty<VmValue>(), instruction.Target.Name))
            {
                return;
            }
            BilInvokeExecution.InvokeValues(context, coroutine, symbol,
                context.InjectOperatorTypeIds(symbol, new[] { operand }),
                instruction.Target.Name);
        }

        private static void DispatchUserBinary(BinaryIntrinsicInstruction instruction,
            VmContext context, VmCoroutine coroutine, VmValue left, VmValue right)
        {
            var name = VmTypeOps.BinaryOperatorName(instruction.Op);
            var symbol = context.FindOperator(left.TypeRef, name, new[] { right });
            if (symbol == null && instruction.Op is BilBinaryOp.CmpEq or BilBinaryOp.CmpNe
                && context.FindFunction(BilSpellings.AnyEqualsCanonical) != null)
            {
                // Any 默认 equals 臂（==/!= 判等，SYNTAX §13.2，用户裁定）：
                // 左操作数沿派生链没有声明 equals 时回退 Any 承诺的默认体
                //（双虚调 hash 比较，equals-or-hash 判等链，绝不涉 toString）。
                // 合成 fn 只有 fn 定义、无符号段声明（内建宿主不进
                // LocalSymbols，同 toString/hash 先例），FindOperator 的声明
                // needle 扫描看不到它——函数表直查。类型自声明的 equals 已在
                // 上面按最派生命中，不经过此臂；fn 缺席（无 stdlib 夹具）保持原样
                symbol = BilSpellings.AnyEqualsCanonical;
            }
            if (symbol == null)
            {
                throw new VmException("没有用户 operator " + name + "：" + left.TypeRef);
            }
            if (instruction.Op == BilBinaryOp.CmpNe
                || VmTypeOps.IsOrderCompare(instruction.Op))
            {
                // != 由 equals 取反；< > <= >= 由 compareTo 的 ComparisonResult
                // 推导（SYNTAX §13.2）。内部调用必须经过 wrapper operator 链。
                var depth = coroutine.CallStack.Count;
                if (VmWrapperDispatch.TryStartOperatorChain(context, coroutine, symbol, left,
                        new[] { right }, instruction.Target.Name))
                {
                    StepToDepth(context, coroutine, depth);
                }
                else
                {
                    InvokeSync(context, coroutine, symbol,
                        context.InjectOperatorTypeIds(symbol, new[] { left, right }),
                        instruction.Target.Name);
                }
                if (coroutine.HasAbruptCompletion
                    || coroutine.State != VmCoroutineState.Running)
                {
                    return;
                }
                var raw = coroutine.ReadVar(instruction.Target.Name);
                if (instruction.Op == BilBinaryOp.CmpNe)
                {
                    if (raw is VmBool flag)
                    {
                        coroutine.WriteVar(instruction.Target.Name, new VmBool(!flag.Value));
                    }
                    return;
                }
                coroutine.WriteVar(instruction.Target.Name,
                    new VmBool(OrderCompare(instruction.Op, raw)));
                return;
            }
            // Entity operator 派发（§14.2）：命中带 wrapped 宿主时走 .proxy.opr.*
            if (VmWrapperDispatch.TryStartOperatorChain(context, coroutine, symbol, left,
                    new[] { right }, instruction.Target.Name))
            {
                return;
            }
            BilInvokeExecution.InvokeValues(context, coroutine, symbol,
                context.InjectOperatorTypeIds(symbol, new[] { left, right }),
                instruction.Target.Name);
        }

        // compareTo 返回 core.ComparisonResult（.LesserThanAnother / .Equal /
        // .GreaterThanAnother）；按 case 名后缀映射为 bool。
        private static bool OrderCompare(BilBinaryOp op, VmValue raw)
        {
            if (raw is not VmEnum enumValue)
            {
                throw new VmException("compareTo 必须返回 enum（core.ComparisonResult），得到 "
                    + raw.TypeRef);
            }
            var symbol = enumValue.CaseSymbol;
            var dot = symbol.LastIndexOf('.');
            var caseName = dot >= 0 ? symbol[(dot + 1)..] : symbol;
            var lesser = caseName == "LesserThanAnother";
            var equal = caseName == "Equal";
            var greater = caseName == "GreaterThanAnother";
            if (!lesser && !equal && !greater)
            {
                throw new VmException("compareTo 返回未知 ComparisonResult case：" + symbol);
            }
            return op switch
            {
                BilBinaryOp.CmpLt => lesser,
                BilBinaryOp.CmpLe => lesser || equal,
                BilBinaryOp.CmpGt => greater,
                BilBinaryOp.CmpGe => greater || equal,
                _ => throw new VmException("非排序比较：" + op),
            };
        }

        private static void InvokeSync(VmContext context, VmCoroutine coroutine, string symbol,
            IReadOnlyList<VmValue> arguments, string resultSlot)
        {
            var depth = coroutine.CallStack.Count;
            BilInvokeExecution.InvokeValues(context, coroutine, symbol, arguments, resultSlot);
            StepToDepth(context, coroutine, depth);
        }

        // 同步推进已发起的调用/派发链直至回落到给定栈深（proxy 链逐环同步嵌套）
        private static void StepToDepth(VmContext context, VmCoroutine coroutine, int depth)
        {
            while (coroutine.CallStack.Count > depth
                && coroutine.State == VmCoroutineState.Running
                && !coroutine.HasAbruptCompletion)
            {
                coroutine.Step(context);
            }
        }

        internal static VmValue EvalBinary(BilBinaryOp op, VmValue left, VmValue right,
            VmContext context, VmCoroutine coroutine)
        {
            if (left.TypeRef != right.TypeRef)
            {
                throw new VmException("操作数精确类型不一致：" + left.TypeRef + " vs " + right.TypeRef);
            }
            if (TryAsInt(left, out var leftInt) && TryAsInt(right, out var rightInt))
            {
                return EvalIntBinary(op, leftInt, rightInt, context, coroutine);
            }
            if (left is VmF32 leftF32 && right is VmF32 rightF32)
            {
                return EvalFloatBinary(op, leftF32.Value, rightF32.Value, isF32: true);
            }
            if (left is VmF64 leftF64 && right is VmF64 rightF64)
            {
                return EvalFloatBinary(op, leftF64.Value, rightF64.Value, isF32: false);
            }
            if (left is VmString leftString && right is VmString rightString)
            {
                return EvalStringBinary(op, leftString.Value, rightString.Value);
            }
            if (left is VmBool leftBool && right is VmBool rightBool)
            {
                return EvalBoolBinary(op, leftBool.Value, rightBool.Value);
            }
            if (left is VmChar leftChar && right is VmChar rightChar)
            {
                return EvalCharBinary(op, leftChar.Value, rightChar.Value);
            }
            throw new VmException("未实现的内建运算 " + op + "：" + left.TypeRef);
        }

        internal static VmValue EvalUnary(BilUnaryOp op, VmValue operand)
        {
            if (TryAsInt(operand, out var integer))
            {
                return op switch
                {
                    BilUnaryOp.Opposite => BoxInt(unchecked(0UL - integer.Bits), integer),
                    BilUnaryOp.BinNot => BoxInt(~integer.Bits, integer),
                    _ => throw new VmException("未实现的内建一元运算 " + op + "：" + operand.TypeRef),
                };
            }
            if (operand is VmF32 f32 && op == BilUnaryOp.Opposite)
            {
                return new VmF32(-f32.Value);
            }
            if (operand is VmF64 f64 && op == BilUnaryOp.Opposite)
            {
                return new VmF64(-f64.Value);
            }
            if (operand is VmBool flag && op == BilUnaryOp.Not)
            {
                return new VmBool(!flag.Value);
            }
            throw new VmException("未实现的内建一元运算 " + op + "：" + operand.TypeRef);
        }

        private static VmValue EvalIntBinary(BilBinaryOp op, IntBits left, IntBits right,
            VmContext context, VmCoroutine coroutine)
        {
            var width = left.Width;
            var shift = ShiftAmount(right, width);
            switch (op)
            {
                case BilBinaryOp.Add:
                    return BoxInt(unchecked(left.Bits + right.Bits), left);
                case BilBinaryOp.Sub:
                    return BoxInt(unchecked(left.Bits - right.Bits), left);
                case BilBinaryOp.Mul:
                    return BoxInt(unchecked(left.Bits * right.Bits), left);
                case BilBinaryOp.Div:
                    return BoxInt(DivInt(left, right, context, coroutine), left);
                case BilBinaryOp.BinAnd:
                    return BoxInt(left.Bits & right.Bits, left);
                case BilBinaryOp.BinOr:
                    return BoxInt(left.Bits | right.Bits, left);
                case BilBinaryOp.BinXor:
                    return BoxInt(left.Bits ^ right.Bits, left);
                case BilBinaryOp.ShiftLeft:
                    return BoxInt(left.Bits << shift, left);
                case BilBinaryOp.ShiftRight:
                    return left.Signed
                        ? BoxInt((ulong)(Signed(left) >> shift), left)
                        : BoxInt(left.Bits >> shift, left);
                case BilBinaryOp.ShiftRightUnsigned:
                    return BoxInt(left.Bits >> shift, left);
                case BilBinaryOp.CmpEq:
                    return new VmBool(left.Bits == right.Bits);
                case BilBinaryOp.CmpNe:
                    return new VmBool(left.Bits != right.Bits);
                case BilBinaryOp.CmpLt:
                    return new VmBool(left.Signed ? Signed(left) < Signed(right) : left.Bits < right.Bits);
                case BilBinaryOp.CmpLe:
                    return new VmBool(left.Signed ? Signed(left) <= Signed(right) : left.Bits <= right.Bits);
                case BilBinaryOp.CmpGt:
                    return new VmBool(left.Signed ? Signed(left) > Signed(right) : left.Bits > right.Bits);
                case BilBinaryOp.CmpGe:
                    return new VmBool(left.Signed ? Signed(left) >= Signed(right) : left.Bits >= right.Bits);
                default:
                    throw new VmException("未实现的内建运算 " + op + "：" + left.TypeRef);
            }
        }

        private static VmValue EvalFloatBinary(BilBinaryOp op, double left, double right, bool isF32)
        {
            switch (op)
            {
                case BilBinaryOp.Add:
                    return BoxFloat(left + right, isF32);
                case BilBinaryOp.Sub:
                    return BoxFloat(left - right, isF32);
                case BilBinaryOp.Mul:
                    return BoxFloat(left * right, isF32);
                case BilBinaryOp.Div:
                    return BoxFloat(left / right, isF32);
                case BilBinaryOp.CmpEq:
                    return new VmBool(left == right);
                case BilBinaryOp.CmpNe:
                    return new VmBool(left != right);
                case BilBinaryOp.CmpLt:
                    return new VmBool(left < right);
                case BilBinaryOp.CmpLe:
                    return new VmBool(left <= right);
                case BilBinaryOp.CmpGt:
                    return new VmBool(left > right);
                case BilBinaryOp.CmpGe:
                    return new VmBool(left >= right);
                default:
                    throw new VmException("未实现的内建运算 " + op + "：" + (isF32 ? ".f32" : ".f64"));
            }
        }

        private static VmValue EvalStringBinary(BilBinaryOp op, string left, string right)
        {
            return op switch
            {
                BilBinaryOp.Add => new VmString(left + right),
                BilBinaryOp.CmpEq => new VmBool(left == right),
                BilBinaryOp.CmpNe => new VmBool(left != right),
                BilBinaryOp.CmpLt => new VmBool(string.CompareOrdinal(left, right) < 0),
                BilBinaryOp.CmpLe => new VmBool(string.CompareOrdinal(left, right) <= 0),
                BilBinaryOp.CmpGt => new VmBool(string.CompareOrdinal(left, right) > 0),
                BilBinaryOp.CmpGe => new VmBool(string.CompareOrdinal(left, right) >= 0),
                _ => throw new VmException("未实现的内建运算 " + op + "：.string"),
            };
        }

        private static VmValue EvalBoolBinary(BilBinaryOp op, bool left, bool right)
        {
            return op switch
            {
                BilBinaryOp.And => new VmBool(left & right),
                BilBinaryOp.Or => new VmBool(left | right),
                BilBinaryOp.CmpEq => new VmBool(left == right),
                BilBinaryOp.CmpNe => new VmBool(left != right),
                _ => throw new VmException("未实现的内建运算 " + op + "：.bool"),
            };
        }

        private static VmValue EvalCharBinary(BilBinaryOp op, char left, char right)
        {
            return op switch
            {
                BilBinaryOp.CmpEq => new VmBool(left == right),
                BilBinaryOp.CmpNe => new VmBool(left != right),
                BilBinaryOp.CmpLt => new VmBool(left < right),
                BilBinaryOp.CmpLe => new VmBool(left <= right),
                BilBinaryOp.CmpGt => new VmBool(left > right),
                BilBinaryOp.CmpGe => new VmBool(left >= right),
                _ => throw new VmException("未实现的内建运算 " + op + "：.char"),
            };
        }

        private readonly struct IntBits
        {
            public ulong Bits { get; }
            public int Width { get; }
            public bool Signed { get; }
            public string TypeRef { get; }

            public IntBits(ulong bits, int width, bool signed, string typeRef)
            {
                Bits = bits;
                Width = width;
                Signed = signed;
                TypeRef = typeRef;
            }
        }

        private static bool TryAsInt(VmValue value, out IntBits bits)
        {
            switch (value)
            {
                case VmI8 n:
                    bits = new IntBits((byte)n.Value, 8, true, ".i8");
                    return true;
                case VmI16 n:
                    bits = new IntBits((ushort)n.Value, 16, true, ".i16");
                    return true;
                case VmI32 n:
                    bits = new IntBits((uint)n.Value, 32, true, ".i32");
                    return true;
                case VmI64 n:
                    bits = new IntBits((ulong)n.Value, 64, true, ".i64");
                    return true;
                case VmU8 n:
                    bits = new IntBits(n.Value, 8, false, ".u8");
                    return true;
                case VmU16 n:
                    bits = new IntBits(n.Value, 16, false, ".u16");
                    return true;
                case VmU32 n:
                    bits = new IntBits(n.Value, 32, false, ".u32");
                    return true;
                case VmU64 n:
                    bits = new IntBits(n.Value, 64, false, ".u64");
                    return true;
                default:
                    bits = default;
                    return false;
            }
        }

        private static long Signed(IntBits value)
        {
            return value.Width switch
            {
                8 => (sbyte)value.Bits,
                16 => (short)value.Bits,
                32 => (int)value.Bits,
                _ => (long)value.Bits,
            };
        }

        private static ulong Mask(ulong value, int width)
        {
            return width == 64 ? value : value & ((1UL << width) - 1);
        }

        private static VmValue BoxInt(ulong bits, IntBits proto)
        {
            var masked = Mask(bits, proto.Width);
            return (proto.Signed, proto.Width) switch
            {
                (true, 8) => new VmI8((sbyte)masked),
                (true, 16) => new VmI16((short)masked),
                (true, 32) => new VmI32((int)masked),
                (true, 64) => new VmI64((long)masked),
                (false, 8) => new VmU8((byte)masked),
                (false, 16) => new VmU16((ushort)masked),
                (false, 32) => new VmU32((uint)masked),
                (false, 64) => new VmU64(masked),
                _ => throw new VmException("内部整数宽度错误"),
            };
        }

        private static VmValue BoxFloat(double value, bool isF32)
        {
            return isF32 ? new VmF32((float)value) : new VmF64(value);
        }

        private static int ShiftAmount(IntBits bits, int width)
        {
            var count = bits.Signed ? (int)Signed(bits) : (int)bits.Bits;
            return count & (width - 1);
        }

        // 整数除零是语言级异常（§11.2 / SYNTAX §8.1）：抛
        // core::DividedByZeroException 对象，可被用户 try/catch 捕获；
        // 浮点除零不走此路径（IEEE 754 产 inf/NaN，见 EvalFloatBinary）
        private static ulong DivInt(IntBits left, IntBits right, VmContext context,
            VmCoroutine coroutine)
        {
            if (right.Bits == 0)
            {
                throw context.DividedByZero(coroutine);
            }
            if (left.Signed)
            {
                return Mask((ulong)(Signed(left) / Signed(right)), left.Width);
            }
            return left.Bits / Mask(right.Bits, right.Width);
        }
    }
}
