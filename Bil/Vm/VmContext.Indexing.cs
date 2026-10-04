using System.Globalization;
using System.IO;
using System.Text;

namespace RigiCompiler.Bil.Vm
{
    public sealed partial class VmContext
    {
        // Indexing 职责；与主文件共享同一类型、字段及生命周期。

        public static int RequireIndex(VmValue index)
        {
            return index switch
            {
                VmI8 v => v.Value,
                VmI16 v => v.Value,
                VmI32 v => v.Value,
                VmI64 v => checked((int)v.Value),
                VmU8 v => v.Value,
                VmU16 v => v.Value,
                VmU32 v => checked((int)v.Value),
                VmU64 v => checked((int)v.Value),
                _ => throw new VmException("数组下标不是整数：" + index.TypeRef),
            };
        }

        private void IndexMembers(IEnumerable<BilSymbolSectionEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (entry is BilSimpleMemberDeclaration member)
                {
                    IndexSimple(member);
                }
                else if (entry is BilTypeDeclaration type)
                {
                    // MW11c 棒4a：同名不同元数类型（SYNTAX §15.3，首例 =
                    // stdlib Task / Task<TReturn>）的 declaration.Symbol 相同
                    // ——裸符号键必须归 arity-0 声明（FindType 裸 typeRef 的
                    // 语义即非泛型）；构造 typeRef（带 <>）走 _typesByKey
                    // 的 arity 键不受影响。旧实现后者覆盖前者，导致非泛型
                    // Task 的 sheet/派发全部错绑到 Task<TReturn>（字段键
                    // 错位：waiter 排空读空、唤醒丢失挂死）
                    if (!_types.TryGetValue(type.Symbol, out var existing)
                        || existing.GenericParameters.Count > type.GenericParameters.Count)
                    {
                        _types[type.Symbol] = type;
                    }
                    var key = type.Symbol + "`" + type.GenericParameters.Count;
                    if (!_typesByKey.ContainsKey(key))
                    {
                        _typesByKey[key] = type;
                    }
                    foreach (var nested in type.Members)
                    {
                        if (nested is BilSimpleMemberDeclaration nestedMember)
                        {
                            IndexSimple(nestedMember);
                        }
                        else if (nested is BilCaseDeclaration caseDecl)
                        {
                            _cases[caseDecl.QualifiedName] = caseDecl;
                        }
                    }
                }
                else if (entry is BilCaseDeclaration topCase)
                {
                    _cases[topCase.QualifiedName] = topCase;
                }
            }
        }

        private void IndexSimple(BilSimpleMemberDeclaration member)
        {
            _members[member.Symbol] = member;
            if (member.Kind is BilMemberKind.Field or BilMemberKind.StaticField)
            {
                _fields[member.Symbol] = member;
            }
            foreach (var modifier in member.Modifiers)
            {
                if (modifier is not BilAccessorModifier accessor)
                {
                    continue;
                }
                if (accessor.Kind == BilAccessorKind.Getter)
                {
                    _getters[accessor.FieldSymbol] = member.Symbol;
                }
                else
                {
                    _setters[accessor.FieldSymbol] = member.Symbol;
                }
                _accessorFields[member.Symbol] = accessor.FieldSymbol;
            }
        }

        private bool ParametersMatch(List<(string Name, string TypeRef)> parameters,
            IReadOnlyList<VmValue> arguments, IReadOnlyList<string> argumentStaticTypes)
        {
            if (parameters.Count != arguments.Count
                || argumentStaticTypes.Count != arguments.Count)
            {
                return false;
            }
            for (var i = 0; i < parameters.Count; i++)
            {
                // §14.1 / RUNTIME §11：对编译期已解析入口的验证——cast 后
                // 静态类型与形参 TypesEqual（不是运行期 typeid 可赋值性）
                if (!TypesEqual(argumentStaticTypes[i], parameters[i].TypeRef))
                {
                    // 泛型元数和实参均参与身份；裸名不是构造类型的 ABI 视图。
                    return false;
                }
            }
            return true;
        }

        // BIL 变量声明类型（.args / .vars）；查不到时回落运行期 TypeRef。
        // init / super 匹配用静态类型，不用对象头 typeid。
        internal static string LookupStaticType(BilFunction function, string name,
            string fallback)
        {
            foreach (var arg in function.Args)
            {
                if (arg.Name == name)
                {
                    return arg.TypeRef;
                }
            }
            foreach (var variable in function.Vars)
            {
                if (variable.Name == name)
                {
                    return variable.TypeRef;
                }
            }
            return fallback;
        }

        internal static string[] ArgumentStaticTypes(BilFunction function,
            IReadOnlyList<BilVariableOperand> operands, IReadOnlyList<VmValue> values)
        {
            var types = new string[operands.Count];
            for (var i = 0; i < operands.Count; i++)
            {
                types[i] = LookupStaticType(function, operands[i].Name, values[i].TypeRef);
            }
            return types;
        }

        // 任一侧含 .generic<（函数泛型占位，无法静态判定）降级通过
        // ——与 BilVerificationContext.TypesCompatible 一致
        internal static bool TypesEqual(string left, string right)
        {
            if (left == right)
            {
                return true;
            }
            if (left.Contains(".generic<", StringComparison.Ordinal)
                || right.Contains(".generic<", StringComparison.Ordinal))
            {
                return true;
            }
            // 同一类型的两种合法 BIL 拼写（§6.1 非 compact 形 ".string, .i64"
            // 与 §5.2 符号内嵌 compact 形 ".string,.i64"）必须判等——双泛型
            // 实参的构造类型在此踩过空白差异（review-20260910 #06），归一化
            // 走验证器同一轮子（别名 + 构造头 + 空白全归一）
            return BilVerificationContext.NormalizeTypeRef(left)
                == BilVerificationContext.NormalizeTypeRef(right);
        }

        private static bool IsBuiltinScalar(string typeRef)
        {
            return typeRef is ".i8" or ".i16" or ".i32" or ".i64"
                or ".u8" or ".u16" or ".u32" or ".u64"
                or ".f32" or ".f64" or ".bool" or ".char"
                or "core::i8" or "core::i16" or "core::i32" or "core::i64"
                or "core::u8" or "core::u16" or "core::u32" or "core::u64"
                or "core::float" or "core::double" or "core::bool" or "core::char";
        }

        private static string DeclarationKeyOf(string typeRef)
        {
            var angle = typeRef.IndexOf('<');
            if (angle < 0)
            {
                return typeRef + "`0";
            }
            var inner = typeRef.Substring(angle + 1, typeRef.Length - angle - 2);
            return typeRef.Substring(0, angle) + "`" + CountTopLevel(inner);
        }

        private static string StripTypeArguments(string typeRef)
        {
            var angle = typeRef.IndexOf('<');
            return angle < 0 ? typeRef : typeRef.Substring(0, angle);
        }

        private static bool TryStripConstructor(string typeRef, string head, out string inner)
        {
            inner = "";
            var prefix = head + "<";
            if (!typeRef.StartsWith(prefix, StringComparison.Ordinal) || !typeRef.EndsWith(">"))
            {
                return false;
            }
            inner = typeRef.Substring(prefix.Length, typeRef.Length - prefix.Length - 1);
            return true;
        }

        private static int CountTopLevel(string text)
        {
            if (text.Length == 0)
            {
                return 0;
            }
            var count = 1;
            var depth = 0;
            foreach (var ch in text)
            {
                if (ch == '<') depth++;
                else if (ch == '>') depth--;
                else if (ch == ',' && depth == 0) count++;
            }
            return count;
        }

        internal static string MethodNameOf(string symbol) => BilLogicalName.Method(symbol);

        internal static bool HasKeyword(BilSimpleMemberDeclaration member, BilKeyword keyword)
        {
            foreach (var modifier in member.Modifiers)
            {
                if (modifier is BilKeywordModifier keywordModifier
                    && keywordModifier.Keyword == keyword)
                {
                    return true;
                }
            }
            return false;
        }

        private static VmValue LoadScalar(BilScalarResource resource)
        {
            // 字面量词法解码唯一归口 BilScalarLiteral（§19.1），此处只包装异常
            var text = resource.LiteralText;
            try
            {
                switch (resource.Type)
                {
                    case BilScalarType.String:
                        return new VmString(BilScalarLiteral.DecodeString(text));
                    case BilScalarType.Bool:
                        return new VmBool(text == "true");
                    case BilScalarType.Char:
                        return new VmChar(BilScalarLiteral.DecodeChar(text));
                    case BilScalarType.I8:
                        return new VmI8(sbyte.Parse(text, CultureInfo.InvariantCulture));
                    case BilScalarType.I16:
                        return new VmI16(short.Parse(text, CultureInfo.InvariantCulture));
                    case BilScalarType.I32:
                        return new VmI32(int.Parse(text, CultureInfo.InvariantCulture));
                    case BilScalarType.I64:
                        return new VmI64(long.Parse(text, CultureInfo.InvariantCulture));
                    case BilScalarType.U8:
                        return new VmU8(byte.Parse(text, CultureInfo.InvariantCulture));
                    case BilScalarType.U16:
                        return new VmU16(ushort.Parse(text, CultureInfo.InvariantCulture));
                    case BilScalarType.U32:
                        return new VmU32(uint.Parse(text, CultureInfo.InvariantCulture));
                    case BilScalarType.U64:
                        return new VmU64(BilScalarLiteral.ParseUnsigned(text));
                    case BilScalarType.F32:
                        return new VmF32(float.Parse(text, CultureInfo.InvariantCulture));
                    case BilScalarType.F64:
                        return new VmF64(double.Parse(text, CultureInfo.InvariantCulture));
                    default:
                        throw new VmException("不支持的标量资源类型：" + resource.Type);
                }
            }
            catch (FormatException ex)
            {
                throw new VmException(ex.Message);
            }
        }

    }
}
