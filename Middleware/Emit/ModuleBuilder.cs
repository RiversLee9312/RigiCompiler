using System.Collections.Generic;
using System.Text;
using LLVMSharp.Interop;
using RigiCompiler.Bil;

namespace RigiCompiler.Middleware
{
    /// <summary>
    /// LLVM 模块构建（MIDDLEWARE_ARCHITECTURE §3 MW6 层，MW1 最小落地）：
    /// MIR + 布局计划（TypeLayout）+ 实现绑定（ImplBinder）→ LLVM 模块。
    /// 具名局部落 alloca 槽，SSA 提升交 mem2reg；指令选择是 ImplBinding
    /// 到 LLVM builder 调用的机械映射。运行时面（rigi_rt）声明在此按需
    /// 登记，定义由 bitcode 合并（LlvmBitcode）带入。
    /// </summary>
    public static class ModuleBuilder
    {
        // entrypoint fn 的对外 C 符号（rigi_rt 的 main 调用它）
        public const string EntrySymbol = "rigi_entry";

        public static LLVMModuleRef Build(MwContext context, MirModule mir)
        {
            var module = LLVMModuleRef.CreateWithName(ModuleNameOf(context));
            try
            {
                module.Target = LlvmHost.HostTriple;
                new Session(module, mir).EmitAll();
                return module;
            }
            catch
            {
                module.Dispose();
                throw;
            }
        }

        // 模块名：Metadata 的 module 键（BIL §4.1，字面量原文含引号需去除），
        // 缺省回退 "rigi.module"
        internal static string ModuleNameOf(MwContext context)
        {
            foreach (var entry in context.Module.Metadata)
            {
                if (entry.Key == "module" && entry.Type == BilScalarType.String)
                {
                    return entry.LiteralText.Trim('"');
                }
            }
            return "rigi.module";
        }

        // 发射会话：一次模块构建的共享状态
        private sealed class Session
        {
            private readonly LLVMModuleRef _module;
            private readonly LLVMContextRef _context;
            private readonly Dictionary<string, EmittedFunction> _functions = new(System.StringComparer.Ordinal);
            private readonly Dictionary<string, (LLVMValueRef Fn, LLVMTypeRef Type)> _faces = new(System.StringComparer.Ordinal);

            internal Session(LLVMModuleRef module, MirModule mir)
            {
                _module = module;
                _context = module.Context;
                Mir = mir;
            }

            private MirModule Mir { get; }

            private sealed record EmittedFunction(LLVMValueRef Value, LLVMTypeRef Type, MirFunction Mir);

            internal void EmitAll()
            {
                // 两遍：先声明全部 fn（内部调用可前向引用），再逐一定义函数体
                foreach (var fn in Mir.Functions)
                {
                    DeclareFunction(fn);
                }
                var builder = _context.CreateBuilder();
                foreach (var fn in Mir.Functions)
                {
                    EmitBody(builder, _functions[fn.Symbol.Canonical]);
                }
            }

            private void DeclareFunction(MirFunction fn)
            {
                LLVMTypeRef type;
                string name;
                if (fn.IsEntrypoint)
                {
                    // 入口对外形态：i32 rigi_entry(void)（.void 返回由发射包装为 0）
                    if (fn.Parameters.Count != 0)
                    {
                        throw new MwNotSupportedException($"入口函数不得有参数: {fn.Symbol.Canonical}");
                    }
                    if (!fn.ReturnType.IsVoid && fn.ReturnType.Key != "i32")
                    {
                        throw new MwNotSupportedException(
                            $"MW1 入口函数返回类型仅支持 .i32/.void: {fn.Symbol.Canonical}");
                    }
                    type = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, System.Array.Empty<LLVMTypeRef>(), false);
                    name = EntrySymbol;
                }
                else
                {
                    var paramTypes = new LLVMTypeRef[fn.Parameters.Count];
                    for (var i = 0; i < fn.Parameters.Count; i++)
                    {
                        paramTypes[i] = TypeLayout.MapType(_context, fn.Parameters[i].Type);
                    }
                    type = LLVMTypeRef.CreateFunction(TypeLayout.MapType(_context, fn.ReturnType), paramTypes, false);
                    name = fn.Symbol.Canonical;
                }
                var value = _module.AddFunction(name, type);
                if (!fn.IsEntrypoint)
                {
                    // 模块内符号不出 .o（合并 rigi_rt 后由优化管线内联/裁减）
                    value.Linkage = LLVMLinkage.LLVMInternalLinkage;
                }
                _functions.Add(fn.Symbol.Canonical, new EmittedFunction(value, type, fn));
            }

            private void EmitBody(LLVMBuilderRef builder, EmittedFunction emitted)
            {
                var fn = emitted.Mir;
                var entry = emitted.Value.AppendBasicBlock(fn.Blocks[0].Id);
                builder.PositionAtEnd(entry);

                // 具名局部 → alloca 槽（含参数落槽）
                var slots = new Dictionary<string, (LLVMValueRef Slot, MirLocal Local)>(System.StringComparer.Ordinal);
                foreach (var local in fn.Locals)
                {
                    var slot = builder.BuildAlloca(TypeLayout.MapType(_context, local.Type), local.Name);
                    slots.Add(local.Name, (slot, local));
                }
                for (var i = 0; i < fn.Parameters.Count; i++)
                {
                    builder.BuildStore(emitted.Value.GetParam((uint)i), slots[fn.Parameters[i].Name].Slot);
                }

                foreach (var inst in fn.Blocks[0].Instructions)
                {
                    EmitInst(builder, slots, inst);
                }
                EmitTerminator(builder, slots, fn, fn.Blocks[0].Terminator);
            }

            private void EmitInst(LLVMBuilderRef builder,
                Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirInst inst)
            {
                switch (inst)
                {
                    case MirLoadResource load:
                        builder.BuildStore(BuildResourceValue(load.Resource, slots[load.Target].Local.Type),
                            slots[load.Target].Slot);
                        break;
                    case MirCopyLocal copy:
                        builder.BuildStore(LoadLocal(builder, slots, copy.Source), slots[copy.Target].Slot);
                        break;
                    case MirBinaryIntrinsic binary:
                        builder.BuildStore(EmitBinary(builder, slots, binary), slots[binary.Target].Slot);
                        break;
                    case MirUnaryIntrinsic unary:
                        builder.BuildStore(EmitUnary(builder, slots, unary), slots[unary.Target].Slot);
                        break;
                    case MirCall call:
                        EmitCall(builder, slots, call);
                        break;
                    default:
                        throw new CompilerInternalException($"未覆盖的 MIR 指令: {inst.GetType().Name}");
                }
            }

            private void EmitTerminator(LLVMBuilderRef builder,
                Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
                MirFunction fn, MirTerminator terminator)
            {
                switch (terminator)
                {
                    case MirRet ret:
                        if (fn.IsEntrypoint)
                        {
                            // 对外 i32 返回：.void 包装为 0
                            builder.BuildRet(ret.Value == null
                                ? LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false)
                                : LoadLocal(builder, slots, ret.Value));
                        }
                        else if (ret.Value == null)
                        {
                            builder.BuildRetVoid();
                        }
                        else
                        {
                            builder.BuildRet(LoadLocal(builder, slots, ret.Value));
                        }
                        break;
                    default:
                        throw new CompilerInternalException($"未覆盖的 MIR 终结符: {terminator.GetType().Name}");
                }
            }

            // ===== 指令子发射 =====

            private LLVMValueRef EmitBinary(LLVMBuilderRef builder,
                Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirBinaryIntrinsic inst)
            {
                var left = LoadLocal(builder, slots, inst.Left);
                var right = LoadLocal(builder, slots, inst.Right);
                switch (ImplBinder.BindBinary(inst.Op, inst.LeftType, inst.RightType, inst.ResultType))
                {
                    case PrimitiveOpBinding primitive:
                        return SelectPrimitive(builder, primitive.Kind, left, right);
                    case RuntimeFaceBinding face:
                        return EmitFaceCall(builder, face.FaceSymbol, new[] { left, right });
                    default:
                        throw new CompilerInternalException("二元运算的非预期绑定形态");
                }
            }

            private LLVMValueRef EmitUnary(LLVMBuilderRef builder,
                Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirUnaryIntrinsic inst)
            {
                var operand = LoadLocal(builder, slots, inst.Operand);
                if (ImplBinder.BindUnary(inst.Op, inst.OperandType, inst.ResultType) is not PrimitiveOpBinding primitive)
                {
                    throw new CompilerInternalException("一元运算的非预期绑定形态");
                }
                return primitive.Kind switch
                {
                    PrimitiveOpKind.IntNeg => builder.BuildNeg(operand, "neg"),
                    PrimitiveOpKind.FloatNeg => builder.BuildFNeg(operand, "fneg"),
                    PrimitiveOpKind.LogicNot => builder.BuildNot(operand, "not"),
                    PrimitiveOpKind.BitNot => builder.BuildNot(operand, "binnot"),
                    _ => throw new CompilerInternalException($"未覆盖的一元指令选择: {primitive.Kind}"),
                };
            }

            private void EmitCall(LLVMBuilderRef builder,
                Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirCall call)
            {
                switch (ImplBinder.BindCall(call.Target))
                {
                    case NativeDirectBinding native:
                    {
                        var signature = CanonicalSignature.Parse(call.Target.Canonical);
                        var cSymbol = RuntimeFaces.MapNativeSymbol(native.Library, native.Symbol);
                        var (fn, fnType) = DeclareNativeFace(cSymbol, signature);
                        var args = new LLVMValueRef[call.Args.Count];
                        for (var i = 0; i < call.Args.Count; i++)
                        {
                            var argValue = LoadLocal(builder, slots, call.Args[i]);
                            var paramType = MirType.Of(signature.Parameters[i].TypeRef);
                            // String 的 C 边界传递约定：rigi_string*（见 RuntimeFaces 注释）
                            args[i] = paramType.IsString ? StoreToTemp(builder, argValue) : argValue;
                        }
                        var result = builder.BuildCall2(fnType, fn, args, "");
                        if (call.Result != null)
                        {
                            builder.BuildStore(result, slots[call.Result].Slot);
                        }
                        break;
                    }
                    case DirectCallBinding direct:
                    {
                        var callee = _functions[direct.Target.Canonical];
                        var args = new LLVMValueRef[call.Args.Count];
                        for (var i = 0; i < call.Args.Count; i++)
                        {
                            args[i] = LoadLocal(builder, slots, call.Args[i]);
                        }
                        var result = builder.BuildCall2(callee.Type, callee.Value, args, "");
                        if (call.Result != null)
                        {
                            builder.BuildStore(result, slots[call.Result].Slot);
                        }
                        break;
                    }
                    default:
                        throw new CompilerInternalException("调用的非预期绑定形态");
                }
            }

            // 运行时面调用：StringIn 取下一个输入值存临时槽传指针，
            // StringOut 开出参槽、调用后读回为结果值（MW1 面恒 void 返回）
            private LLVMValueRef EmitFaceCall(LLVMBuilderRef builder, string faceSymbol,
                IReadOnlyList<LLVMValueRef> inputs)
            {
                var shape = RuntimeFaces.ShapeOf(faceSymbol);
                var (fn, fnType) = DeclareFace(faceSymbol, shape);
                var args = new List<LLVMValueRef>(shape.Count);
                var inputIndex = 0;
                LLVMValueRef? outSlot = null;
                foreach (var param in shape)
                {
                    if (param == RuntimeFaceParam.StringOut)
                    {
                        outSlot = builder.BuildAlloca(TypeLayout.StringType(_context), "face.out");
                        args.Add(outSlot.Value);
                    }
                    else
                    {
                        args.Add(StoreToTemp(builder, inputs[inputIndex++]));
                    }
                }
                builder.BuildCall2(fnType, fn, args.ToArray(), "");
                if (outSlot == null)
                {
                    throw new CompilerInternalException($"运行时面 {faceSymbol} 无出参却被求值");
                }
                return builder.BuildLoad2(TypeLayout.StringType(_context), outSlot.Value, "face.result");
            }

            // ===== 声明登记 =====

            private (LLVMValueRef Fn, LLVMTypeRef Type) DeclareFace(string faceSymbol,
                IReadOnlyList<RuntimeFaceParam> shape)
            {
                if (_faces.TryGetValue(faceSymbol, out var cached))
                {
                    return cached;
                }
                var paramTypes = new LLVMTypeRef[shape.Count];
                for (var i = 0; i < shape.Count; i++)
                {
                    paramTypes[i] = LLVMTypeRef.CreatePointer(TypeLayout.StringType(_context), 0);
                }
                var type = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, paramTypes, false);
                var fn = _module.AddFunction(faceSymbol, type);
                _faces.Add(faceSymbol, (fn, type));
                return (fn, type);
            }

            private (LLVMValueRef Fn, LLVMTypeRef Type) DeclareNativeFace(string cSymbol,
                CanonicalSignature signature)
            {
                if (_faces.TryGetValue(cSymbol, out var cached))
                {
                    return cached;
                }
                var paramTypes = new LLVMTypeRef[signature.Parameters.Count];
                for (var i = 0; i < signature.Parameters.Count; i++)
                {
                    var paramType = MirType.Of(signature.Parameters[i].TypeRef);
                    paramTypes[i] = paramType.IsString
                        ? LLVMTypeRef.CreatePointer(TypeLayout.StringType(_context), 0)
                        : TypeLayout.MapType(_context, paramType);
                }
                var returnType = MirType.Of(signature.ReturnTypeRef);
                if (returnType.IsString)
                {
                    throw new MwNotSupportedException($"MW1 不支持 native 返回 .string: {cSymbol}");
                }
                var type = LLVMTypeRef.CreateFunction(TypeLayout.MapType(_context, returnType), paramTypes, false);
                var fn = _module.AddFunction(cSymbol, type);
                _faces.Add(cSymbol, (fn, type));
                return (fn, type);
            }

            // ===== 值与资源 =====

            private LLVMValueRef LoadLocal(LLVMBuilderRef builder,
                Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirOperand operand)
            {
                if (operand is not MirLocalOperand local)
                {
                    throw new CompilerInternalException($"未覆盖的操作数形态: {operand.GetType().Name}");
                }
                var (slot, localInfo) = slots[local.Name];
                return builder.BuildLoad2(TypeLayout.MapType(_context, localInfo.Type), slot, local.Name);
            }

            private LLVMValueRef StoreToTemp(LLVMBuilderRef builder, LLVMValueRef value)
            {
                var slot = builder.BuildAlloca(value.TypeOf, "tmp");
                builder.BuildStore(value, slot);
                return slot;
            }

            private LLVMValueRef BuildResourceValue(BilResource resource, MirType targetType)
            {
                if (resource is not BilScalarResource scalar)
                {
                    throw new MwNotSupportedException($"MW1 仅支持标量资源: {resource.Name}");
                }
                var text = scalar.LiteralText;
                switch (scalar.Type)
                {
                    case BilScalarType.String:
                        return BuildStringConstant(BilScalarLiteral.DecodeString(text), resource.Name);
                    case BilScalarType.Bool:
                        return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, text == "true" ? 1u : 0u, false);
                    case BilScalarType.Char:
                        return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int16, BilScalarLiteral.DecodeChar(text), false);
                    case BilScalarType.I8 or BilScalarType.I16 or BilScalarType.I32 or BilScalarType.I64:
                        return LLVMValueRef.CreateConstInt(TypeLayout.MapType(_context, targetType),
                            unchecked((ulong)BilScalarLiteral.ParseSigned(text)), true);
                    case BilScalarType.U8 or BilScalarType.U16 or BilScalarType.U32 or BilScalarType.U64:
                        return LLVMValueRef.CreateConstInt(TypeLayout.MapType(_context, targetType),
                            BilScalarLiteral.ParseUnsigned(text), false);
                    case BilScalarType.F32:
                        return LLVMValueRef.CreateConstRealOfStringAndSize(LLVMTypeRef.Float, text, (uint)text.Length);
                    case BilScalarType.F64:
                        return LLVMValueRef.CreateConstRealOfStringAndSize(LLVMTypeRef.Double, text, (uint)text.Length);
                    default:
                        throw new MwNotSupportedException($"MW1 不支持资源类型 {scalar.Type}: {resource.Name}");
                }
            }

            // { i8* data, i64 len } 常量：字节入内部全局，指针经 ConstGEP 取
            private LLVMValueRef BuildStringConstant(string text, string resourceName)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                var bytePtrType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
                LLVMValueRef dataPointer;
                if (bytes.Length == 0)
                {
                    dataPointer = LLVMValueRef.CreateConstPointerNull(bytePtrType);
                }
                else
                {
                    var arrayType = LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)bytes.Length);
                    var elements = new LLVMValueRef[bytes.Length];
                    for (var i = 0; i < bytes.Length; i++)
                    {
                        elements[i] = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, bytes[i], false);
                    }
                    var global = _module.AddGlobal(arrayType, "str." + resourceName);
                    global.Linkage = LLVMLinkage.LLVMInternalLinkage;
                    global.IsGlobalConstant = true;
                    global.Initializer = LLVMValueRef.CreateConstArray(LLVMTypeRef.Int8, elements);
                    var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false);
                    dataPointer = LLVMValueRef.CreateConstInBoundsGEP2(arrayType, global, new[] { zero, zero });
                }
                var length = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)bytes.Length, false);
                return _context.GetConstStruct(new[] { dataPointer, length }, false);
            }

            private static LLVMValueRef SelectPrimitive(LLVMBuilderRef builder, PrimitiveOpKind kind,
                LLVMValueRef left, LLVMValueRef right)
            {
                return kind switch
                {
                    PrimitiveOpKind.IntAdd => builder.BuildAdd(left, right, "add"),
                    PrimitiveOpKind.IntSub => builder.BuildSub(left, right, "sub"),
                    PrimitiveOpKind.IntMul => builder.BuildMul(left, right, "mul"),
                    PrimitiveOpKind.IntSDiv => builder.BuildSDiv(left, right, "sdiv"),
                    PrimitiveOpKind.IntUDiv => builder.BuildUDiv(left, right, "udiv"),
                    PrimitiveOpKind.FloatAdd => builder.BuildFAdd(left, right, "fadd"),
                    PrimitiveOpKind.FloatSub => builder.BuildFSub(left, right, "fsub"),
                    PrimitiveOpKind.FloatMul => builder.BuildFMul(left, right, "fmul"),
                    PrimitiveOpKind.FloatDiv => builder.BuildFDiv(left, right, "fdiv"),
                    PrimitiveOpKind.LogicAnd => builder.BuildAnd(left, right, "and"),
                    PrimitiveOpKind.LogicOr => builder.BuildOr(left, right, "or"),
                    PrimitiveOpKind.BitAnd => builder.BuildAnd(left, right, "bitand"),
                    PrimitiveOpKind.BitOr => builder.BuildOr(left, right, "bitor"),
                    PrimitiveOpKind.BitXor => builder.BuildXor(left, right, "bitxor"),
                    PrimitiveOpKind.ShiftLeft => builder.BuildShl(left, right, "shl"),
                    PrimitiveOpKind.ShiftRightSigned => builder.BuildAShr(left, right, "ashr"),
                    PrimitiveOpKind.ShiftRightUnsigned => builder.BuildLShr(left, right, "lshr"),
                    PrimitiveOpKind.IntCmpEq => builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, right, "eq"),
                    PrimitiveOpKind.IntCmpNe => builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, left, right, "ne"),
                    PrimitiveOpKind.IntCmpSLt => builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, left, right, "slt"),
                    PrimitiveOpKind.IntCmpSLe => builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, left, right, "sle"),
                    PrimitiveOpKind.IntCmpSGt => builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, left, right, "sgt"),
                    PrimitiveOpKind.IntCmpSGe => builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, left, right, "sge"),
                    PrimitiveOpKind.IntCmpULt => builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, left, right, "ult"),
                    PrimitiveOpKind.IntCmpULe => builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, left, right, "ule"),
                    PrimitiveOpKind.IntCmpUGt => builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, left, right, "ugt"),
                    PrimitiveOpKind.IntCmpUGe => builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, left, right, "uge"),
                    PrimitiveOpKind.FloatCmpEq => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, left, right, "feq"),
                    PrimitiveOpKind.FloatCmpNe => builder.BuildFCmp(LLVMRealPredicate.LLVMRealONE, left, right, "fne"),
                    PrimitiveOpKind.FloatCmpLt => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLT, left, right, "flt"),
                    PrimitiveOpKind.FloatCmpLe => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLE, left, right, "fle"),
                    PrimitiveOpKind.FloatCmpGt => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGT, left, right, "fgt"),
                    PrimitiveOpKind.FloatCmpGe => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGE, left, right, "fge"),
                    _ => throw new CompilerInternalException($"未覆盖的二元指令选择: {kind}"),
                };
            }
        }
    }
}
