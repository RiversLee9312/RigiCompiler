namespace RigiCompiler.Bil.Vm
{
    // 运行时类型运算（BIL_VM_DESIGN §9 切片 3 / BIL_STANDARD §12 / §22.3）：
    // cast 全家、type.is/supers/with、wrapper 值拷贝、operator / $$call 查找。
    // 类型兼容复用 BilVerificationContext.TypesAssignable（与验证器同一套图）。

    internal static class VmTypeOps
    {
        internal static string ActualType(VmValue value)
        {
            switch (value)
            {
                case VmNull:
                    return ".null";
                case VmAny any:
                    return any.TypeId.TypeSymbol;
                case VmNullable nullable:
                    return nullable.HasValue ? ActualType(nullable.Value!) : ".null";
                default:
                    return value.TypeRef;
            }
        }

        internal static string RequireTypeId(VmValue value)
        {
            if (value is VmTypeId typeId)
            {
                return typeId.TypeSymbol;
            }
            throw new VmException("操作数不是 typeid：" + value.TypeRef);
        }

        internal static string RequireFieldId(VmValue value)
        {
            if (value is VmFieldId fieldId)
            {
                return fieldId.FieldSymbol;
            }
            throw new VmException("操作数不是 fieldid：" + value.TypeRef);
        }

        // .generic< 是 typeid 占位（§14.3 / SYNTAX §14.8），执行期类型操作必须
        // 先按当前调用帧的泛型绑定（hidden .generic.X typeid 实参）解析成具体
        // typeid 再继续。顶层裸 .generic<T> 未绑定抛 VmException（绝不恒等
        // 放行）；构造类型内嵌占位（.array<.generic<T>>）未绑定时原样保留，
        // 由 TypesCompatible / TypesEqual 降级——调用点帧通常未绑 callee 的槽。
        internal static string ResolveTypeRef(VmContext context, VmCoroutine coroutine,
            string typeRef)
        {
            if (!typeRef.Contains(".generic<", StringComparison.Ordinal))
            {
                return typeRef;
            }
            return ResolveTypeRefImpl(context, coroutine, typeRef, requireBound: true);
        }

        private static string ResolveTypeRefImpl(VmContext context, VmCoroutine coroutine,
            string typeRef, bool requireBound)
        {
            var angle = typeRef.IndexOf('<');
            if (angle > 0 && typeRef.EndsWith(">"))
            {
                var head = typeRef.Substring(0, angle);
                var inner = typeRef.Substring(angle + 1, typeRef.Length - angle - 2);
                if (head == ".generic")
                {
                    if (TryResolveGenericPlaceholder(coroutine, inner, out var bound))
                    {
                        return bound;
                    }
                    if (requireBound)
                    {
                        throw new VmException("无法解析泛型占位 .generic<" + inner
                            + ">：当前调用帧未绑定该 typeid");
                    }
                    return typeRef;
                }
                var arguments = BilVerificationContext.SplitTopLevel(inner);
                var resolved = new List<string>(arguments.Count);
                foreach (var argument in arguments)
                {
                    resolved.Add(ResolveTypeRefImpl(context, coroutine, argument,
                        requireBound: false));
                }
                return head + "<" + string.Join(", ", resolved) + ">";
            }
            return typeRef;
        }

        // 占位内文形如 $.generic.NAME（$ 为引用标记），映射到当前帧同名 hidden
        // typeid 实参。
        private static bool TryResolveGenericPlaceholder(VmCoroutine coroutine, string inner,
            out string bound)
        {
            var name = inner.StartsWith("$", StringComparison.Ordinal) ? inner.Substring(1) : inner;
            var frame = coroutine.CurrentFrame;
            if (frame.Slots.TryGetValue(name, out var value) && value is VmTypeId typeId)
            {
                bound = typeId.TypeSymbol;
                return true;
            }
            bound = "";
            return false;
        }

        // 按泛型参数名解析当前帧 hidden typeid 绑定（嵌套类外层 GP 捕获用，
        // review-20260910 #02）；未绑定返回 false（降级由调用方决定）
        internal static bool TryResolveFrameGeneric(VmCoroutine coroutine,
            string genericParameterName, out string bound)
        {
            return TryResolveGenericPlaceholder(coroutine,
                "$.generic." + genericParameterName, out bound);
        }

        internal static bool Is(VmContext context, VmCoroutine coroutine, VmValue value,
            string targetType)
        {
            if (value is VmNull)
            {
                return false;
            }
            var resolved = ResolveTypeRef(context, coroutine, targetType);
            if (BilVerificationContext.NormalizeTypeRef(resolved)
                .StartsWith("core::Type<", StringComparison.Ordinal))
                return TryCast(context, value, resolved, out _);
            if (BilVerificationContext.NormalizeTypeRef(resolved) == "core::ValueType")
                return context.IsValueType(ActualType(value));
            return context.Types.TypesAssignable(ActualType(value), resolved);
        }

        internal static bool Supers(VmContext context, VmCoroutine coroutine, VmValue value,
            string targetType)
        {
            if (value is VmNull)
            {
                return false;
            }
            var resolved = ResolveTypeRef(context, coroutine, targetType);
            return context.Types.TypesAssignable(resolved, ActualType(value));
        }

        internal static bool With(VmContext context, VmCoroutine coroutine, VmValue value,
            string wrapperType)
        {
            if (value is VmNull)
            {
                return false;
            }
            var resolved = ResolveTypeRef(context, coroutine, wrapperType);
            return TypeHasWrapper(context, ActualType(value), resolved,
                new HashSet<string>(StringComparer.Ordinal));
        }

        internal static VmValue GetWrapper(VmContext context, VmValue host, string wrapperType)
        {
            if (!VmObject.TryAsHost(host, out var fieldHost))
            {
                throw new VmException("get.wrapper 目标不是对象：" + host.TypeRef);
            }
            if (!fieldHost.TryReadHidden(VmContext.HiddenEntityKey(wrapperType), out var wrapper))
            {
                throw new VmException("宿主没有 wrapper：" + wrapperType);
            }
            return new VmWrapperReceiver(wrapper.Copy(), host);
        }

        internal static VmValue GetWrapperField(VmContext context, VmValue host,
            string fieldSymbol, string wrapperType)
        {
            if (!VmObject.TryAsHost(host, out var fieldHost))
            {
                throw new VmException("get.wrapper.field 目标不是对象：" + host.TypeRef);
            }
            if (!fieldHost.TryReadHidden(
                    VmContext.HiddenFieldKey(fieldSymbol, wrapperType), out var wrapper))
            {
                throw new VmException("字段没有 wrapper：" + wrapperType);
            }
            return new VmWrapperReceiver(wrapper.Copy(), host);
        }

        internal static bool TryCast(VmContext context, VmValue source, string targetType,
            out VmValue result)
        {
            result = VmNull.Instance;
            var requested = targetType;
            var normalizedTarget = BilVerificationContext.NormalizeTypeRef(targetType);

            if (source is VmNull)
            {
                if (IsNullable(normalizedTarget) || IsReferenceLike(context, normalizedTarget))
                {
                    result = VmNull.Instance;
                    return true;
                }
                return false;
            }

            if (source is VmAny any)
            {
                if (IsAny(normalizedTarget))
                {
                    result = any;
                    return true;
                }
                return TryCast(context, any.Payload, targetType, out result);
            }

            if (source is VmNullable nullable)
            {
                if (!nullable.HasValue)
                {
                    if (IsNullable(normalizedTarget))
                    {
                        result = VmNull.Instance;
                        return true;
                    }
                    return false;
                }
                if (IsNullable(normalizedTarget, out var nullableInner))
                {
                    if (!TryCast(context, nullable.Value!, nullableInner, out var inner))
                    {
                        return false;
                    }
                    result = WrapNullable(inner, nullableInner);
                    return true;
                }
                return TryCast(context, nullable.Value!, targetType, out result);
            }

            if (VmContext.TypesEqual(source.TypeRef, normalizedTarget)
                || BilVerificationContext.NormalizeTypeRef(source.TypeRef) == normalizedTarget)
            {
                result = source.Copy();
                return true;
            }

            if (IsNullable(normalizedTarget, out var innerType))
            {
                if (!TryCast(context, source, innerType, out var converted))
                {
                    return false;
                }
                result = WrapNullable(converted, innerType);
                return true;
            }

            if (IsAny(normalizedTarget))
            {
                result = new VmAny(source);
                return true;
            }

            // Type<TBound> 的 T 是合法的类型边界，不是裸模板擦除。
            // 只有 VmTypeId 才能进入本分支，普通标量不能伪造类型句柄。
            if (source is VmTypeId typeId && normalizedTarget.StartsWith("core::Type<", StringComparison.Ordinal))
            {
                var bounds = BilVerificationContext.SplitTopLevel(normalizedTarget["core::Type<".Length..^1]);
                if (bounds is { Count: 1 } && (IsAny(bounds[0])
                    || context.Types.TypesAssignable(typeId.TypeSymbol, bounds[0])))
                {
                    result = source.Copy();
                    return true;
                }
                return false;
            }

            if (TryNumericCast(source, normalizedTarget, out var numeric))
            {
                result = numeric;
                return true;
            }

            if (IsObject(normalizedTarget)
                || (IsValueTypeRoot(normalizedTarget) && context.IsValueType(ActualType(source))))
            {
                result = source.Copy();
                return true;
            }

            var actualType = ActualType(source);
            if (context.Types.TypesAssignable(actualType, requested)
                || context.Types.TypesAssignable(actualType, normalizedTarget))
            {
                result = source.Copy();
                return true;
            }

            return false;
        }

        internal static VmValue CastOrThrow(VmContext context, VmCoroutine coroutine,
            VmValue source, string targetType)
        {
            var resolved = ResolveTypeRef(context, coroutine, targetType);
            if (TryCast(context, source, resolved, out var result))
            {
                return result;
            }
            throw context.CastFailed(coroutine, source.TypeRef, resolved);
        }

        internal static VmValue CastSafe(VmContext context, VmCoroutine coroutine,
            VmValue source, string targetType)
        {
            var resolved = ResolveTypeRef(context, coroutine, targetType);
            return TryCast(context, source, resolved, out var result)
                ? result
                : VmNull.Instance;
        }

        // ===== §12.1 用户自定义转换分派（review-20260910 #03）=====
        // 语义层早已把 castTo/castFrom 解析进 BoundCastExpression，但
        // Lowering 只发普通 cast（设计意图：运行时按 §12.1 自行分派）——
        // 本入口补上欠缺的半环。castTo 优先（源类型零参泛型 operator，
        // TTarget 无从推断、显式装目标 typeid），castFrom 兜底（目标类型
        // 实例 operator；转换完成前无目标实例，receiver 用目标零值——
        // 惯常体不读 this，TSource 由 source 实参统一推断）。均不命中
        // 返回 false，调用方继续内建 TryCast 兜底（§12.1 第 3 条）。
        // 只能在带结果槽的指令执行入口调用（命中即压帧、结果异步入槽）；
        // CastOrThrow/CastSafe 的同步调用方维持内建通道。
        internal static bool TryStartUserConversion(VmContext context, VmCoroutine coroutine,
            VmValue source, string targetType, string resultSlot)
        {
            // 名义可赋值（含恒等/视图改写）不走转换 operator——否则
            // Dur→Dur 这类恒等 cast 会递归调进 castFrom（#03 实证）
            if (context.Types.TypesAssignable(ActualType(source), targetType))
            {
                return false;
            }
            // 胖壳解包后的实际类型是 castTo 的宿主（与 operator 分派同口径）
            var castTo = context.FindOperator(ActualType(source), "castTo",
                Array.Empty<VmValue>());
            if (castTo != null
                && context.FindFunction(castTo) is { } castToFn)
            {
                var castToArgs = new List<VmValue>();
                var shaped = true;
                foreach (var arg in castToFn.Args)
                {
                    if (arg.Name == ".return")
                    {
                        continue;
                    }
                    if (arg.Name == ".this")
                    {
                        castToArgs.Add(source);
                        continue;
                    }
                    if (arg.Name.StartsWith(".generic.", StringComparison.Ordinal))
                    {
                        castToArgs.Add(new VmTypeId(targetType));
                        continue;
                    }
                    // §12.1 castTo 形态是零值参泛型 operator；其它形态
                    // 不作转换候选（防误命中同名 operator）
                    shaped = false;
                    break;
                }
                if (shaped)
                {
                    BilInvokeExecution.InvokeValues(context, coroutine, castTo,
                        castToArgs, resultSlot);
                    return true;
                }
            }
            var castFrom = context.FindOperator(targetType, "castFrom", new[] { source });
            if (castFrom != null && context.FindFunction(castFrom) != null)
            {
                BilInvokeExecution.InvokeValues(context, coroutine, castFrom,
                    context.InjectOperatorTypeIds(castFrom,
                        new[] { context.ZeroOf(targetType), source }),
                    resultSlot);
                return true;
            }
            return false;
        }

        // T → Nullable\<T\> 包装（BIL §13.6 get.array 内建数组读取与
        // cast 到可空目标共用）：值类型/枚举包 VmNullable 存在位；引用类型
        // 与 .any 沿用 VmNull 表示直接透传；VmNull 原样
        internal static VmValue WrapNullable(VmValue inner, string innerType)
        {
            if (inner is VmNull)
            {
                return inner;
            }
            if (IsBuiltinScalar(inner.TypeRef) || inner.TypeRef == ".string"
                || inner.TypeRef == "core::String" || inner is VmObject { IsValueType: true }
                || inner is VmEnum)
            {
                return new VmNullable(innerType, inner);
            }
            return inner;
        }

        private static bool TryNumericCast(VmValue source, string target, out VmValue result)
        {
            result = VmNull.Instance;
            target = BilVerificationContext.NormalizeTypeRef(target);
            if (!IsNumericTarget(target))
            {
                return false;
            }
            switch (source)
            {
                case VmI8 n:
                    result = FromSigned(n.Value, target);
                    return result != null!;
                case VmI16 n:
                    result = FromSigned(n.Value, target);
                    return result != null!;
                case VmI32 n:
                    result = FromSigned(n.Value, target);
                    return result != null!;
                case VmI64 n:
                    result = FromSigned(n.Value, target);
                    return result != null!;
                case VmU8 n:
                    result = FromUnsigned(n.Value, target);
                    return result != null!;
                case VmU16 n:
                    result = FromUnsigned(n.Value, target);
                    return result != null!;
                case VmU32 n:
                    result = FromUnsigned(n.Value, target);
                    return result != null!;
                case VmU64 n:
                    result = FromUnsigned(n.Value, target);
                    return result != null!;
                case VmF32 n:
                    result = FromFloat(n.Value, target);
                    return result != null!;
                case VmF64 n:
                    result = FromFloat(n.Value, target);
                    return result != null!;
                case VmChar n:
                    result = FromUnsigned(n.Value, target);
                    return result != null!;
                default:
                    return false;
            }
        }

        private static VmValue FromSigned(long value, string target)
        {
            return target switch
            {
                "core::i8" => new VmI8(unchecked((sbyte)value)),
                "core::i16" => new VmI16(unchecked((short)value)),
                "core::i32" => new VmI32(unchecked((int)value)),
                "core::i64" => new VmI64(value),
                "core::u8" => new VmU8(unchecked((byte)value)),
                "core::u16" => new VmU16(unchecked((ushort)value)),
                "core::u32" => new VmU32(unchecked((uint)value)),
                "core::u64" => new VmU64(unchecked((ulong)value)),
                "core::float" => new VmF32(value),
                "core::double" => new VmF64(value),
                "core::char" => new VmChar(unchecked((char)value)),
                _ => null!,
            };
        }

        private static VmValue FromUnsigned(ulong value, string target)
        {
            return target switch
            {
                "core::i8" => new VmI8(unchecked((sbyte)value)),
                "core::i16" => new VmI16(unchecked((short)value)),
                "core::i32" => new VmI32(unchecked((int)value)),
                "core::i64" => new VmI64(unchecked((long)value)),
                "core::u8" => new VmU8(unchecked((byte)value)),
                "core::u16" => new VmU16(unchecked((ushort)value)),
                "core::u32" => new VmU32(unchecked((uint)value)),
                "core::u64" => new VmU64(value),
                "core::float" => new VmF32(value),
                "core::double" => new VmF64(value),
                "core::char" => new VmChar(unchecked((char)value)),
                _ => null!,
            };
        }

        private static VmValue FromFloat(double value, string target)
        {
            return target switch
            {
                "core::i8" => new VmI8(unchecked((sbyte)value)),
                "core::i16" => new VmI16(unchecked((short)value)),
                "core::i32" => new VmI32(unchecked((int)value)),
                "core::i64" => new VmI64(unchecked((long)value)),
                "core::u8" => new VmU8(unchecked((byte)value)),
                "core::u16" => new VmU16(unchecked((ushort)value)),
                "core::u32" => new VmU32(unchecked((uint)value)),
                "core::u64" => new VmU64(unchecked((ulong)value)),
                "core::float" => new VmF32((float)value),
                "core::double" => new VmF64(value),
                "core::char" => new VmChar(unchecked((char)(long)value)),
                _ => null!,
            };
        }

        private static bool IsNumericTarget(string normalized)
        {
            return normalized is "core::i8" or "core::i16" or "core::i32" or "core::i64"
                or "core::u8" or "core::u16" or "core::u32" or "core::u64"
                or "core::float" or "core::double" or "core::char";
        }

        private static bool IsBuiltinScalar(string typeRef)
        {
            var normalized = BilVerificationContext.NormalizeTypeRef(typeRef);
            return normalized is "core::i8" or "core::i16" or "core::i32" or "core::i64"
                or "core::u8" or "core::u16" or "core::u32" or "core::u64"
                or "core::float" or "core::double" or "core::bool" or "core::char";
        }

        internal static bool IsPrimitiveOperand(VmValue value)
        {
            return value is VmI8 or VmI16 or VmI32 or VmI64
                or VmU8 or VmU16 or VmU32 or VmU64
                or VmF32 or VmF64 or VmBool or VmChar or VmString;
        }

        private static bool IsAny(string normalized) => normalized == "core::Any";

        private static bool IsObject(string normalized) => normalized == "core::Object";

        private static bool IsValueTypeRoot(string normalized) => normalized == "core::ValueType";

        private static bool IsNullable(string normalized) =>
            normalized.StartsWith("core::Nullable<", StringComparison.Ordinal);

        private static bool IsNullable(string normalized, out string inner)
        {
            const string head = "core::Nullable<";
            if (normalized.StartsWith(head, StringComparison.Ordinal) && normalized.EndsWith(">"))
            {
                inner = normalized.Substring(head.Length, normalized.Length - head.Length - 1);
                return true;
            }
            inner = "";
            return false;
        }

        private static bool IsReferenceLike(VmContext context, string normalized)
        {
            if (IsAny(normalized) || IsObject(normalized)
                || normalized.StartsWith("core::Array<", StringComparison.Ordinal)
                || normalized.StartsWith("core::Span<", StringComparison.Ordinal)
                || normalized.StartsWith("core::SharedSpan<", StringComparison.Ordinal)
                || normalized.StartsWith("core::Cell<", StringComparison.Ordinal)
                || normalized.StartsWith("core::ReadonlyCell<", StringComparison.Ordinal))
            {
                return true;
            }
            if (IsNullable(normalized) || IsBuiltinScalar(normalized)
                || normalized == "core::String" || normalized == "core::void")
            {
                return false;
            }
            var declaration = context.FindType(normalized);
            if (declaration == null)
            {
                return !context.IsValueType(normalized);
            }
            return declaration.Kind is BilTypeKind.Class or BilTypeKind.Interface;
        }

        private static bool TypeHasWrapper(VmContext context, string typeRef, string wrapperType,
            HashSet<string> visited)
        {
            var key = BilVerificationContext.DeclarationKeyOf(typeRef);
            if (!visited.Add(key))
            {
                return false;
            }
            var declaration = context.FindType(typeRef);
            if (declaration == null)
            {
                return false;
            }
            foreach (var modifier in declaration.Modifiers)
            {
                if (modifier is BilWrappedModifier wrapped
                    && VmContext.TypesEqual(wrapped.WrapperTypeRef, wrapperType))
                {
                    return true;
                }
            }
            if (declaration.ExtendsType != null
                && TypeHasWrapper(context, declaration.ExtendsType, wrapperType, visited))
            {
                return true;
            }
            foreach (var iface in declaration.ImplementsTypes)
            {
                if (TypeHasWrapper(context, iface, wrapperType, visited))
                {
                    return true;
                }
            }
            return false;
        }

        internal static string BinaryOperatorName(BilBinaryOp op)
        {
            return op switch
            {
                BilBinaryOp.Add => "plus",
                BilBinaryOp.Sub => "minus",
                BilBinaryOp.Mul => "times",
                BilBinaryOp.Div => "div",
                BilBinaryOp.Mod => "mod",
                BilBinaryOp.And => "and",
                BilBinaryOp.Or => "or",
                BilBinaryOp.BinAnd => "bitwiseAnd",
                BilBinaryOp.BinOr => "bitwiseOr",
                BilBinaryOp.BinXor => "bitwiseXor",
                BilBinaryOp.ShiftLeft => "leftShift",
                BilBinaryOp.ShiftRight => "rightShift",
                BilBinaryOp.ShiftRightUnsigned => "unsignedRightShift",
                BilBinaryOp.CmpEq => "equals",
                BilBinaryOp.CmpNe => "equals",
                BilBinaryOp.CmpLt => "compareTo",
                BilBinaryOp.CmpLe => "compareTo",
                BilBinaryOp.CmpGt => "compareTo",
                BilBinaryOp.CmpGe => "compareTo",
                _ => throw new VmException("未知二元运算 " + op),
            };
        }

        internal static string UnaryOperatorName(BilUnaryOp op)
        {
            return op switch
            {
                BilUnaryOp.Opposite => "opposite",
                BilUnaryOp.Not => "not",
                BilUnaryOp.BinNot => "bitwiseNot",
                _ => throw new VmException("未知一元运算 " + op),
            };
        }

        internal static bool IsOrderCompare(BilBinaryOp op)
        {
            return op is BilBinaryOp.CmpLt or BilBinaryOp.CmpLe
                or BilBinaryOp.CmpGt or BilBinaryOp.CmpGe;
        }
    }
}
