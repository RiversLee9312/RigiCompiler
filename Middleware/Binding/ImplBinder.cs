using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Binding
{
    /// <summary>
    /// 实现绑定查询（MIDDLEWARE_ARCHITECTURE §3 MW2）：操作类别 + 操作数严格
    /// 类型 + 结果严格类型 + 已解析符号身份 → 唯一实现形态。不含隐式转换、
    /// 候选排序或最佳匹配（source-level overload ranking 已在 frontend 完成，
    /// BIL §3.3）。MW1 覆盖：标量/string 内建运算 + native/直接调用分流。
    /// 查询键为 BIL 类型引用（任意别名形态，内部经 MwTypeKey 归一），
    /// 本层不依赖 MIR——MIR 保留类型身份（MirType），调用方以其 Canonical
    /// 或原始 typeRef 入查均可。
    /// </summary>
    public static class ImplBinder
    {
        public static ImplBinding BindBinary(BilBinaryOp op, string leftType, string rightType, string resultType)
        {
            var left = MwTypeKey.Normalize(leftType);
            var right = MwTypeKey.Normalize(rightType);
            // 内建字符串拼接（BIL §11.2：op 级内建，非 invoke 路径）
            if (MwTypeKey.IsString(left) && MwTypeKey.IsString(right) && op == BilBinaryOp.Add)
            {
                return new RuntimeFaceBinding(RuntimeFaces.StringConcat);
            }
            // string 比较（§11.5：eq/ne 内容相等，排序字典序三态）→ 比较面
            if (MwTypeKey.IsString(left) && MwTypeKey.IsString(right)
                && op is >= BilBinaryOp.CmpEq and <= BilBinaryOp.CmpGe)
            {
                return new StringCompareBinding();
            }

            var key = MwTypeKey.Of(left);
            // .nullable<T> 相等检查（§3.4 nullable 检查标准形态：与 null
            // 资源 cmp.eq/ne）：胖引用恒等（typeid+payload 双段全等；
            // null 双段零天然成立）。排序比较与算术不适用
            if (key.StartsWith("core::Nullable<", System.StringComparison.Ordinal))
            {
                return op switch
                {
                    BilBinaryOp.CmpEq => new PrimitiveOpBinding(PrimitiveOpKind.RefCmpEq),
                    BilBinaryOp.CmpNe => new PrimitiveOpBinding(PrimitiveOpKind.RefCmpNe),
                    _ => throw new MwNotSupportedException(
                        $"MW2 不支持二元运算 {op} 作用于 {leftType}"),
                };
            }
            var isFloat = key is "float" or "double";
            var isBool = key == "bool";
            // char 只有比较（VM 同口径：无 char 算术/位/移位）；比较按
            // 32 位 Unicode 标量值的无符号数值序（VM 的 char 比较同此序）
            var isChar = key == "char";
            var isUnsigned = key is "u8" or "u16" or "u32" or "u64";
            var isSignedInt = key is "i8" or "i16" or "i32" or "i64";

            var kind = op switch
            {
                BilBinaryOp.Add when isSignedInt || isUnsigned => PrimitiveOpKind.IntAdd,
                BilBinaryOp.Sub when isSignedInt || isUnsigned => PrimitiveOpKind.IntSub,
                BilBinaryOp.Mul when isSignedInt || isUnsigned => PrimitiveOpKind.IntMul,
                BilBinaryOp.Div when isSignedInt => PrimitiveOpKind.IntSDiv,
                BilBinaryOp.Div when isUnsigned => PrimitiveOpKind.IntUDiv,
                BilBinaryOp.Mod when isSignedInt => PrimitiveOpKind.IntSMod,
                BilBinaryOp.Mod when isUnsigned => PrimitiveOpKind.IntUMod,
                BilBinaryOp.Add when isFloat => PrimitiveOpKind.FloatAdd,
                BilBinaryOp.Sub when isFloat => PrimitiveOpKind.FloatSub,
                BilBinaryOp.Mul when isFloat => PrimitiveOpKind.FloatMul,
                BilBinaryOp.Div when isFloat => PrimitiveOpKind.FloatDiv,
                BilBinaryOp.Mod when isFloat => PrimitiveOpKind.FloatMod,
                BilBinaryOp.And when isBool => PrimitiveOpKind.LogicAnd,
                BilBinaryOp.Or when isBool => PrimitiveOpKind.LogicOr,
                // §11.4：内建位运算仅整数族（bool/char/float 不落绑定；
                // 此类 BIL 已过不了 Gate，此处为纵深防御）
                BilBinaryOp.BinAnd when isSignedInt || isUnsigned => PrimitiveOpKind.BitAnd,
                BilBinaryOp.BinOr when isSignedInt || isUnsigned => PrimitiveOpKind.BitOr,
                BilBinaryOp.BinXor when isSignedInt || isUnsigned => PrimitiveOpKind.BitXor,
                BilBinaryOp.ShiftLeft when isSignedInt || isUnsigned => PrimitiveOpKind.ShiftLeft,
                BilBinaryOp.ShiftRight when isSignedInt => PrimitiveOpKind.ShiftRightSigned,
                BilBinaryOp.ShiftRight when isUnsigned => PrimitiveOpKind.ShiftRightUnsigned,
                BilBinaryOp.ShiftRightUnsigned when isSignedInt || isUnsigned => PrimitiveOpKind.ShiftRightUnsigned,
                BilBinaryOp.CmpEq when isSignedInt || isUnsigned || isBool || isChar => PrimitiveOpKind.IntCmpEq,
                BilBinaryOp.CmpNe when isSignedInt || isUnsigned || isBool || isChar => PrimitiveOpKind.IntCmpNe,
                BilBinaryOp.CmpLt when isSignedInt => PrimitiveOpKind.IntCmpSLt,
                BilBinaryOp.CmpLe when isSignedInt => PrimitiveOpKind.IntCmpSLe,
                BilBinaryOp.CmpGt when isSignedInt => PrimitiveOpKind.IntCmpSGt,
                BilBinaryOp.CmpGe when isSignedInt => PrimitiveOpKind.IntCmpSGe,
                BilBinaryOp.CmpLt when isUnsigned || isChar => PrimitiveOpKind.IntCmpULt,
                BilBinaryOp.CmpLe when isUnsigned || isChar => PrimitiveOpKind.IntCmpULe,
                BilBinaryOp.CmpGt when isUnsigned || isChar => PrimitiveOpKind.IntCmpUGt,
                BilBinaryOp.CmpGe when isUnsigned || isChar => PrimitiveOpKind.IntCmpUGe,
                BilBinaryOp.CmpEq when isFloat => PrimitiveOpKind.FloatCmpEq,
                BilBinaryOp.CmpNe when isFloat => PrimitiveOpKind.FloatCmpNe,
                BilBinaryOp.CmpLt when isFloat => PrimitiveOpKind.FloatCmpLt,
                BilBinaryOp.CmpLe when isFloat => PrimitiveOpKind.FloatCmpLe,
                BilBinaryOp.CmpGt when isFloat => PrimitiveOpKind.FloatCmpGt,
                BilBinaryOp.CmpGe when isFloat => PrimitiveOpKind.FloatCmpGe,
                _ => throw new MwNotSupportedException(
                    $"MW1 不支持二元运算 {op} 作用于 {leftType} × {rightType}"),
            };
            return new PrimitiveOpBinding(kind);
        }

        public static ImplBinding BindUnary(BilUnaryOp op, string operandType, string resultType)
        {
            var key = MwTypeKey.Of(MwTypeKey.Normalize(operandType));
            var isFloat = key is "float" or "double";
            var isBool = key == "bool";
            // char 无一元运算（VM 同口径）
            var isInt = key is "i8" or "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "u64";

            var kind = op switch
            {
                BilUnaryOp.Opposite when isInt => PrimitiveOpKind.IntNeg,
                BilUnaryOp.Opposite when isFloat => PrimitiveOpKind.FloatNeg,
                BilUnaryOp.Not when isBool => PrimitiveOpKind.LogicNot,
                BilUnaryOp.BinNot when isInt => PrimitiveOpKind.BitNot,
                _ => throw new MwNotSupportedException(
                    $"MW1 不支持一元运算 {op} 作用于 {operandType}"),
            };
            return new PrimitiveOpBinding(kind);
        }

        // native 声明 → NativeDirectBinding；其余 → 模块内直接调用
        public static ImplBinding BindCall(MwMemberSymbol target)
        {
            string? library = null;
            string? nativeSymbol = null;
            var isNative = false;
            foreach (var modifier in target.Declaration.Modifiers)
            {
                switch (modifier)
                {
                    case BilKeywordModifier { Keyword: BilKeyword.Native }:
                        isNative = true;
                        break;
                    case BilNativeLibraryModifier lib:
                        library = lib.Library;
                        break;
                    case BilNativeSymbolModifier sym:
                        nativeSymbol = sym.Symbol;
                        break;
                }
            }
            if (isNative)
            {
                // verifier §21 已强制 symbol(...)/lib(...) 与 native 同现且恰一次；防御
                if (library == null || nativeSymbol == null)
                {
                    throw new CompilerInternalException($"native 声明缺 symbol/lib 修饰符: {target.Canonical}");
                }
                return new NativeDirectBinding(library, nativeSymbol);
            }
            // 显式 invoke 运算符（除 $$call）保持静态直调：VM 的
            // ResolveDispatchSymbol 对不在 VM vtable 的 operator 原样返回
            // 调用点符号（直调），本特判与之对齐；intrinsic 运算符位置的
            // 实际类型派发走 BindOperatorCall，不经本路径
            if (target.IsOperatorMember
                && !target.Canonical.Contains("$$call(", System.StringComparison.Ordinal))
            {
                return new DirectCallBinding(target);
            }
            // 实例方法按 VM 同口径细分（MW4）：class → vtable 虚调用；
            // interface → iMap 派发；init/ext/static/struct/enum 方法与全局
            // fn 直调（struct/enum 的 .this 形态随批 3）
            if (target.Owner != null && target.IsVirtualMember)
            {
                return target.Owner.Declaration.Kind switch
                {
                    BilTypeKind.Class => new VirtualCallBinding(target),
                    BilTypeKind.Interface => new InterfaceCallBinding(target),
                    _ => new DirectCallBinding(target),
                };
            }
            return new DirectCallBinding(target);
        }

        // §15.3 callable 协议：沿 extends 链解析唯一匹配的 $$call
        //（宿主泛型代入 + §6.4 全等比对，参照 BilVerifier.TryFindCallOperator）。
        // 查不到属 Gate 漏检。functions 用于读取泛型 $$call 的 .generic.* / 值包隐藏前缀。
        public static IndirectCallBinding BindIndirectCall(
            MwSymbolTable symbols, string objectStaticType,
            IReadOnlyList<string> argTypes, string? resultType,
            IReadOnlyList<BilFunction>? functions = null)
        {
            if (BilVerificationContext.IsBuiltinType(objectStaticType))
            {
                throw new CompilerInternalException(
                    $"invoke.indirect 目标是内建类型: {objectStaticType}");
            }
            var current = MwTypeKey.Normalize(objectStaticType);
            var visited = new HashSet<string>(System.StringComparer.Ordinal);
            while (visited.Add(current))
            {
                var type = symbols.FindTypeByRef(current);
                if (type == null)
                {
                    throw new CompilerInternalException(
                        $"invoke.indirect 静态类型不可解析: {current}");
                }
                foreach (var member in type.Members)
                {
                    if (!IsCallOperator(member))
                    {
                        continue;
                    }
                    if (!TryMatchCallOperator(member, type.Declaration, current, argTypes,
                            resultType, functions, out _))
                    {
                        continue;
                    }
                    // async $$call（AsyncAction/AsyncFunc）与同步同一绑定：
                    // 调用点结果已是 Task/Task<T>（TryMatchCallOperator 按
                    // §15.2 包装），发射走虚派发；CoroutineSplit 把目标
                    // $$call 改成 spawn stub，await 是后续 MirAwait
                    return new IndirectCallBinding(member);
                }
                if (type.Declaration.ExtendsType == null)
                {
                    break;
                }
                current = MwTypeKey.Normalize(type.Declaration.ExtendsType);
            }
            throw new CompilerInternalException(
                $"invoke.indirect 无匹配 $$call: {objectStaticType}");
        }

        private static bool IsCallOperator(MwMemberSymbol member)
        {
            if (member.Declaration.Kind != BilMemberKind.Method)
            {
                return false;
            }
            foreach (var modifier in member.Declaration.Modifiers)
            {
                if (modifier is BilOperatorModifier { Name: "call" })
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryMatchCallOperator(MwMemberSymbol member,
            BilTypeDeclaration declaration, string hostTypeRef,
            IReadOnlyList<string> argTypes, string? resultType,
            IReadOnlyList<BilFunction>? functions, out bool isAsync)
        {
            isAsync = member.HasKeyword(BilKeyword.Async);
            if (!BilVerificationContext.TryParseMethodSymbol(member.Canonical,
                    out _, out _, out var parameters, out var candidateReturn))
            {
                return false;
            }
            var ordinary = new List<(string Name, string TypeRef)>();
            foreach (var parameter in parameters)
            {
                if (parameter.Name.StartsWith(".generic.", System.StringComparison.Ordinal)
                    || parameter.Name.StartsWith(".vargs.", System.StringComparison.Ordinal)
                    || parameter.Name.StartsWith(".kwargs.", System.StringComparison.Ordinal))
                {
                    continue;
                }
                ordinary.Add(parameter);
            }
            var genericHidden = new List<BilArgDeclaration>();
            var packArguments = new List<BilArgDeclaration>();
            if (functions != null)
            {
                foreach (var fn in functions)
                {
                    if (fn.Symbol != member.Canonical)
                    {
                        continue;
                    }
                    foreach (var arg in fn.Args)
                    {
                        if (arg.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                        {
                            genericHidden.Add(arg);
                        }
                        else if (arg.Name.StartsWith(".vargs.", System.StringComparison.Ordinal)
                            || arg.Name.StartsWith(".kwargs.", System.StringComparison.Ordinal))
                        {
                            packArguments.Add(arg);
                        }
                    }
                    break;
                }
            }
            var expectedCount = genericHidden.Count + ordinary.Count + packArguments.Count;
            if (argTypes.Count != expectedCount)
            {
                return false;
            }
            for (var i = 0; i < genericHidden.Count; i++)
            {
                if (!BilVerificationContext.TypesCompatible(argTypes[i], genericHidden[i].TypeRef))
                {
                    return false;
                }
            }
            var valueStart = genericHidden.Count;
            for (var i = 0; i < ordinary.Count; i++)
            {
                var expected = SubstituteHostGenerics(ordinary[i].TypeRef, declaration, hostTypeRef);
                if (!BilVerificationContext.TypesCompatible(argTypes[valueStart + i], expected))
                {
                    return false;
                }
            }
            for (var i = 0; i < packArguments.Count; i++)
            {
                var packIndex = valueStart + ordinary.Count + i;
                if (!BilVerificationContext.TypesCompatible(argTypes[packIndex],
                        packArguments[i].TypeRef))
                {
                    return false;
                }
            }
            var returnType = SubstituteHostGenerics(candidateReturn, declaration, hostTypeRef);
            if (isAsync)
            {
                returnType = returnType == ".void" || MwTypeKey.IsVoid(MwTypeKey.Normalize(returnType))
                    ? "core.coroutine::Task"
                    : $"core.coroutine::Task<{returnType}>";
            }
            if (resultType == null)
            {
                return (returnType == ".void" || MwTypeKey.IsVoid(MwTypeKey.Normalize(returnType)))
                    && !isAsync;
            }
            return BilVerificationContext.TypesCompatible(resultType, returnType);
        }

        // 宿主泛型代入（与 BilVerifier.SubstituteHostGenerics 同口径）
        internal static string SubstituteHostGenerics(string typeRef,
            BilTypeDeclaration declaration, string hostTypeRef)
        {
            if (declaration.GenericParameters.Count == 0
                || !typeRef.Contains(".generic<", System.StringComparison.Ordinal))
            {
                return typeRef;
            }
            var angle = hostTypeRef.IndexOf('<');
            if (angle < 0 || !hostTypeRef.EndsWith(">", System.StringComparison.Ordinal))
            {
                return typeRef;
            }
            var arguments = BilVerificationContext.SplitTopLevel(hostTypeRef.Substring(
                angle + 1, hostTypeRef.Length - angle - 2));
            if (arguments.Count != declaration.GenericParameters.Count)
            {
                return typeRef;
            }
            var result = typeRef;
            for (var i = 0; i < arguments.Count; i++)
            {
                result = result.Replace(
                    ".generic<$.generic." + declaration.GenericParameters[i] + ">",
                    arguments[i], System.StringComparison.Ordinal);
            }
            return result;
        }

        // ===== 用户运算符查询（遗1：native 运算符分派） =====
        // VM 口径（BilComputeInstructions.DispatchUserBinary / ExecuteUnary
        // + VmContext.FindOperator）：intrinsic 指令按左操作数实际类型沿
        // extends 链找 operator fn，右操作数仅参与形参可赋匹配。native
        // 编译期按左操作数静态类型解析声明符号；运行期的实际类型派发由
        // vtable 槽完成（运算符已入 native vtable，MwSymbol.IsVirtualMember），
        // 与 VM 的实际类型解析殊途同归。运算符名映射与
        // VmTypeOps.BinaryOperatorName/UnaryOperatorName 同表（VM 层设施
        // 不被本层引用，两张表靠 MiddlewareTests 锚定防漂移）。

        // 内建二元操作数族（BindBinary 已覆盖的左操作数类型）：标量族 /
        // string / Nullable 比较。左操作数在此族内不查用户 operator
        public static bool IsBuiltinBinaryOperand(string leftType)
        {
            var key = MwTypeKey.Of(MwTypeKey.Normalize(leftType));
            return key is "i8" or "i16" or "i32" or "i64"
                or "u8" or "u16" or "u32" or "u64"
                or "float" or "double" or "bool" or "char" or "String"
                || key.StartsWith("core::Nullable<", System.StringComparison.Ordinal);
        }

        // 内建一元操作数族（BindUnary 已覆盖；string 无一元运算但同样
        // 不落用户派发——VM 对 string 一元走内建求值后响亮失败同口径）
        public static bool IsBuiltinUnaryOperand(string operandType)
        {
            var key = MwTypeKey.Of(MwTypeKey.Normalize(operandType));
            return key is "i8" or "i16" or "i32" or "i64"
                or "u8" or "u16" or "u32" or "u64"
                or "float" or "double" or "bool" or "char" or "String";
        }

        // Any 默认 equals 合成 fn canonical（==/!= 判等，用户裁定；
        // 唯一定义在 BilSpellings.AnyEqualsCanonical——LocalSymbolEmitters.
        // EmitSynthesizedEqualsDefaultBody 同形投影，VM fallback 共用）
        public const string AnyEqualsCanonical = BilSpellings.AnyEqualsCanonical;

        public static string UserBinaryOperatorName(BilBinaryOp op)
        {            return op switch
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
                _ => throw new CompilerInternalException($"未知二元运算: {op}"),
            };
        }

        public static string UserUnaryOperatorName(BilUnaryOp op)
        {
            return op switch
            {
                BilUnaryOp.Opposite => "opposite",
                BilUnaryOp.Not => "not",
                BilUnaryOp.BinNot => "bitwiseNot",
                _ => throw new CompilerInternalException($"未知一元运算: {op}"),
            };
        }

        public static bool IsOrderCompare(BilBinaryOp op)
        {
            return op is BilBinaryOp.CmpLt or BilBinaryOp.CmpLe
                or BilBinaryOp.CmpGt or BilBinaryOp.CmpGe;
        }

        // 二元用户运算符解析：按左操作数静态类型沿 extends 链（VM
        // FindOperator 同口径；含成员表兜底扫描——预定义宿主不进符号段
        // 类型声明时的防御）；右操作数静态类型参与形参可赋匹配。找不到
        // 返回 null（调用方按受控拒绝处理，消息对齐 VM 运行期异常文本）
        public static MwMemberSymbol? FindUserBinaryOperator(MwSymbolTable symbols,
            BilBinaryOp op, string leftType, string rightType)
        {
            return FindUserOperator(symbols, UserBinaryOperatorName(op), leftType,
                new[] { rightType });
        }

        public static MwMemberSymbol? FindUserUnaryOperator(MwSymbolTable symbols,
            BilUnaryOp op, string operandType)
        {
            return FindUserOperator(symbols, UserUnaryOperatorName(op), operandType,
                System.Array.Empty<string>());
        }

        // §12.1 用户转换运算符查询（review-20260910 #03 native 侧编译期
        // 重写用）：castTo/castFrom 与内建运算符共用同一沿 extends 链的
        // 成员查找（VM VmContext.FindOperator 同口径；ext operator 经
        // MwSymbolTable 归户后同样在宿主 Members 内）
        public static MwMemberSymbol? FindUserConversionOperator(MwSymbolTable symbols,
            string operatorName, string ownerType, IReadOnlyList<string> argTypes)
        {
            return FindUserOperator(symbols, operatorName, ownerType, argTypes);
        }

        private static MwMemberSymbol? FindUserOperator(MwSymbolTable symbols,
            string operatorName, string ownerType, IReadOnlyList<string> argTypes)
        {
            var current = MwTypeKey.Normalize(ownerType);
            // 泛型占位左操作数（T extends Bound 内的运算）：静态无法解析
            // 到唯一声明（VM 运行期按实际 typeid 派发）——遗1 不支持，
            // 由调用方受控拒绝
            if (Layout.GenericAbi.TryPlaceholderName(current, out _))
            {
                return null;
            }
            var visited = new HashSet<string>(System.StringComparer.Ordinal);
            while (visited.Add(BilVerificationContext.StripTypeArguments(current)))
            {
                var type = symbols.FindTypeByRef(current);
                if (type == null)
                {
                    break;
                }
                foreach (var member in type.Members)
                {
                    if (IsOperatorNamed(member, operatorName)
                        && OperatorParamsMatchStatic(symbols, member, type.Declaration,
                            current, argTypes))
                    {
                        return member;
                    }
                }
                if (type.Declaration.ExtendsType == null)
                {
                    break;
                }
                current = MwTypeKey.Normalize(type.Declaration.ExtendsType);
            }
            return null;
        }

        private static bool IsOperatorNamed(MwMemberSymbol member, string operatorName)
        {
            if (member.Declaration.Kind != BilMemberKind.Method)
            {
                return false;
            }
            foreach (var modifier in member.Declaration.Modifiers)
            {
                if (modifier is BilOperatorModifier op && op.Name == operatorName)
                {
                    return true;
                }
            }
            return member.Canonical.Contains("$" + operatorName + "(",
                System.StringComparison.Ordinal);
        }

        // G4 占位运算的派发候选集（VM FindOperator 的静态投影）：模块内
        // 全部同名 operator 方法（非 External；接口声明的 operator 不在
        // 此列——VM FindOperatorMember 的宿主集只含 extends 链，接口
        // 默认实现亦不会被命中，排除即 VM 口径）。按宿主派生深度降序
        // 排列（最深优先 = VM 沿实际类型派生链先命中最具体实现）。
        // MirReachability 可达边与 GenericOpEmitter 派发臂共用同一集合。
        public static List<MwMemberSymbol> CollectOperatorCandidates(MwSymbolTable symbols,
            string operatorName)
        {
            var list = new List<MwMemberSymbol>();
            foreach (var member in symbols.Members)
            {
                if (member.IsExternal
                    || member.Owner?.Declaration.Kind == BilTypeKind.Interface)
                {
                    continue;
                }
                if (IsOperatorNamed(member, operatorName))
                {
                    list.Add(member);
                }
            }
            list.Sort((a, b) =>
            {
                var depth = DerivationDepth(symbols, b).CompareTo(DerivationDepth(symbols, a));
                return depth != 0
                    ? depth
                    : string.CompareOrdinal(a.Canonical, b.Canonical);
            });
            return list;
        }

        private static int DerivationDepth(MwSymbolTable symbols, MwMemberSymbol member)
        {
            var depth = 0;
            for (var type = member.Owner; type != null;)
            {
                var extends = type.Declaration.ExtendsType;
                type = extends == null ? null : symbols.FindTypeByRef(extends);
                depth++;
                if (depth > 64)
                {
                    break;   // 防御：环状 extends（verifier 已拒）
                }
            }
            return depth;
        }

        // 形参匹配（静态口径）：剥 .generic./.vargs./.kwargs. 隐藏形参；
        // 宿主泛型代入（SubstituteHostGenerics 同 BindIndirectCall 口径）
        // 后逐个可赋（实参静态类型 → 形参类型，沿 extends/implements 闭包）
        private static bool OperatorParamsMatchStatic(MwSymbolTable symbols,
            MwMemberSymbol member,
            BilTypeDeclaration declaration, string hostTypeRef,
            IReadOnlyList<string> argTypes)
        {
            if (!BilVerificationContext.TryParseMethodSymbol(member.Canonical,
                    out _, out _, out var parameters, out _))
            {
                return false;
            }
            var ordinary = new List<(string Name, string TypeRef)>();
            foreach (var parameter in parameters)
            {
                if (parameter.Name.StartsWith(".generic.", System.StringComparison.Ordinal)
                    || parameter.Name.StartsWith(".vargs.", System.StringComparison.Ordinal)
                    || parameter.Name.StartsWith(".kwargs.", System.StringComparison.Ordinal))
                {
                    continue;
                }
                ordinary.Add(parameter);
            }
            if (ordinary.Count != argTypes.Count)
            {
                return false;
            }
            for (var i = 0; i < ordinary.Count; i++)
            {
                var expected = SubstituteHostGenerics(ordinary[i].TypeRef, declaration,
                    hostTypeRef);
                if (!TypeAssignableStatic(symbols, argTypes[i], expected))
                {
                    return false;
                }
            }
            return true;
        }

        // 静态可赋（VM TypeAssignable 的编译期投影）：canonical 全等（含
        // .generic 降级）或 from 的 extends/implements 闭包命中 to
        private static bool TypeAssignableStatic(MwSymbolTable symbols, string from, string to)
        {
            if (BilVerificationContext.TypesCompatible(from, to))
            {
                return true;
            }
            var normalizedTo = MwTypeKey.Normalize(to);
            if (MwTypeKey.IsAny(normalizedTo))
            {
                return true;
            }
            var visited = new HashSet<string>(System.StringComparer.Ordinal);
            var stack = new Stack<string>();
            stack.Push(MwTypeKey.Normalize(from));
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!visited.Add(BilVerificationContext.StripTypeArguments(current)))
                {
                    continue;
                }
                var type = symbols.FindTypeByRef(current);
                if (type == null)
                {
                    continue;
                }
                if (type.Declaration.ExtendsType is { } baseRef)
                {
                    var normalizedBase = MwTypeKey.Normalize(baseRef);
                    if (BilVerificationContext.TypesCompatible(normalizedBase, normalizedTo))
                    {
                        return true;
                    }
                    stack.Push(normalizedBase);
                }
                foreach (var iface in type.Declaration.ImplementsTypes)
                {
                    var normalizedIface = MwTypeKey.Normalize(iface);
                    if (BilVerificationContext.TypesCompatible(normalizedIface, normalizedTo))
                    {
                        return true;
                    }
                    stack.Push(normalizedIface);
                }
            }
            return false;
        }

        // intrinsic 运算符调用的派发绑定：class → vtable 虚调用（实际类型
        // 派发，VM FindOperator 口径）；interface → iMap 派发；struct/enum
        // → 直调（值类型无继承，静态即实际）
        public static ImplBinding BindOperatorCall(MwMemberSymbol target)
        {
            if (target.Owner != null && target.IsVirtualMember)
            {
                return target.Owner.Declaration.Kind switch
                {
                    BilTypeKind.Class => new VirtualCallBinding(target),
                    BilTypeKind.Interface => new InterfaceCallBinding(target),
                    _ => new DirectCallBinding(target),
                };
            }
            return new DirectCallBinding(target);
        }

        // ===== 成员查询（访问器 / 索引运算符；不依赖 MIR 类型对象） =====

        // 字段访问器查找（沿宿主基类链；excludingFn = 当前 fn，访问器
        // 体内不递归自调——VM TryFindAccessor 同口径）。全局字段
        //（§8.4.1）宿主段是命名空间而非类型，FindType 落空：访问器是
        // owner==null 的顶层方法，改扫全局成员表（VM _getters/_setters
        // 扁平字典同效；excludingFn 自身排除同口径）
        public static MwMemberSymbol? FindAccessor(MwSymbolTable symbols, string fieldSymbol,
            BilAccessorKind kind, string excludingFn)
        {
            if (symbols.FindType(FieldOwnerOf(fieldSymbol)) == null)
            {
                foreach (var global in symbols.GlobalMembers)
                {
                    if (global.Canonical == excludingFn)
                    {
                        continue;
                    }
                    foreach (var modifier in global.Declaration.Modifiers)
                    {
                        if (modifier is BilAccessorModifier globalAccessor
                            && globalAccessor.Kind == kind
                            && globalAccessor.FieldSymbol == fieldSymbol)
                        {
                            return global;
                        }
                    }
                }
                return null;
            }
            var visited = new HashSet<string>(System.StringComparer.Ordinal);
            for (var type = symbols.FindType(FieldOwnerOf(fieldSymbol));
                type != null && visited.Add(BilVerificationContext.DeclarationKeyOf(type.Canonical));
                type = BaseOf(symbols, type))
            {
                foreach (var member in type.Members)
                {
                    if (member.Canonical == excludingFn)
                    {
                        continue;
                    }
                    foreach (var modifier in member.Declaration.Modifiers)
                    {
                        if (modifier is BilAccessorModifier accessor
                            && accessor.Kind == kind && accessor.FieldSymbol == fieldSymbol)
                        {
                            return member;
                        }
                    }
                }
            }
            return null;
        }

        // 用户索引运算符（VM FindIndexOperator 同口径）：$$getAtIndex /
        // $$setAtIndex，宿主按剥泛型后的类型名匹配
        public static MwMemberSymbol? FindIndexOperator(MwSymbolTable symbols,
            string collectionTypeCanonical, bool isGet)
        {
            var needle = isGet ? "$$getAtIndex(" : "$$setAtIndex(";
            var hosts = new HashSet<string>(System.StringComparer.Ordinal)
            {
                collectionTypeCanonical,
                BilVerificationContext.StripTypeArguments(collectionTypeCanonical),
            };
            var visited = new HashSet<string>(System.StringComparer.Ordinal);
            for (var type = symbols.FindType(collectionTypeCanonical)
                    ?? symbols.FindType(
                        BilVerificationContext.StripTypeArguments(collectionTypeCanonical));
                type != null && visited.Add(BilVerificationContext.DeclarationKeyOf(type.Canonical));
                type = BaseOf(symbols, type))
            {
                hosts.Add(type.Canonical);
                if (type.Declaration.ExtendsType is { } baseRef)
                {
                    hosts.Add(BilVerificationContext.StripTypeArguments(baseRef));
                }
            }
            foreach (var member in symbols.Members)
            {
                if (!member.Canonical.Contains(needle, System.StringComparison.Ordinal))
                {
                    continue;
                }
                var owner = member.Owner?.Canonical;
                if (owner != null && (hosts.Contains(owner)
                    || hosts.Contains(BilVerificationContext.StripTypeArguments(owner))))
                {
                    return member;
                }
            }
            return null;
        }

        // 字段符号宿主段：Counter#count@.i32 → Counter
        public static string FieldOwnerOf(string fieldSymbol)
        {
            var hash = fieldSymbol.IndexOf('#');
            return hash < 0
                ? throw new CompilerInternalException($"字段符号缺宿主段: {fieldSymbol}")
                : fieldSymbol.Substring(0, hash);
        }

        private static MwTypeSymbol? BaseOf(MwSymbolTable symbols, MwTypeSymbol type) =>
            type.Declaration.ExtendsType is { } baseRef ? symbols.FindTypeByRef(baseRef) : null;
    }
}
