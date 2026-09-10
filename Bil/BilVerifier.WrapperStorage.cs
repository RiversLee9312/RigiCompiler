namespace RigiCompiler.Bil
{
    public static partial class BilVerifier
    {
        // wrapper 普通字段和隐藏内嵌槽遵守同一套 rich 规则。get.self 是独立
        // 指令，不是字段；绝不按字段名（如 self）给手写 BIL 提供豁免。
        private static void VerifyWrapperStorage(BilVerificationContext context,
            List<BilVerificationError> errors)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var declaration in context.TypeDeclarations.Values)
            {
                var reference = declaration.Symbol;
                if (declaration.GenericParameters.Count != 0)
                    reference += "<" + string.Join(",", declaration.GenericParameters.Select(
                        name => ".generic<$.generic." + name + ">")) + ">";
                Visit(reference, 0);
            }
            foreach (var function in context.Module.Functions)
            {
                foreach (var arg in function.Args) Visit(arg.TypeRef, 0);
                foreach (var variable in function.Vars) Visit(variable.TypeRef, 0);
                var hostReference = function.Args.FirstOrDefault(a => a.Name == ".this").TypeRef;
                foreach (var block in function.Blocks)
                foreach (var instruction in block.Instructions)
                {
                    var wrapperReference = instruction switch
                    {
                        NewWrapperEntityInstruction entity => entity.WrapperType.TypeRef,
                        NewWrapperFieldInstruction field => field.WrapperType.TypeRef,
                        NewWrapperMethodInstruction method => method.WrapperType.TypeRef,
                        _ => null,
                    };
                    if (wrapperReference == null) continue;
                    Visit(wrapperReference, 0);
                    if (hostReference != null) CheckHostApplication(hostReference, wrapperReference);
                }
            }

            void CheckHostApplication(string hostReference, string wrapperReference)
            {
                if (!context.TryGetTypeDeclaration(hostReference, out var host)
                    || !context.TryGetTypeDeclaration(wrapperReference, out var wrapper)) return;
                if (!HasKeyword(host.Modifiers, BilKeyword.Rich)
                    && host.Kind is BilTypeKind.Struct or BilTypeKind.EnumStruct or BilTypeKind.Wrapper
                    && HasKeyword(wrapper.Modifiers, BilKeyword.Rich))
                    errors.Add(new BilVerificationError("21.8", hostReference,
                        $"非 rich 宿主不能内嵌 rich wrapper：{wrapperReference}"));
                if (HasKeyword(host.Modifiers, BilKeyword.Shared)
                    && !HasKeyword(wrapper.Modifiers, BilKeyword.Shared))
                    errors.Add(new BilVerificationError("21.8", hostReference,
                        $"shared 宿主不能内嵌非 shared wrapper：{wrapperReference}"));
            }

            void Visit(string reference, int depth)
            {
                if (depth > 64 || !visited.Add(reference)) return;
                // 嵌套实参也要验证，不能用 Array<W<Object>> 隐藏非法构造。
                var angle = reference.IndexOf('<');
                if (angle >= 0 && reference.EndsWith('>') && !reference.StartsWith(".generic<"))
                    foreach (var arg in BilVerificationContext.SplitTopLevel(reference[(angle + 1)..^1]))
                        Visit(arg, depth + 1);
                if (!context.TryGetTypeDeclaration(reference, out var declaration)) return;
                var rich = HasKeyword(declaration.Modifiers, BilKeyword.Rich);
                var shared = HasKeyword(declaration.Modifiers, BilKeyword.Shared);
                foreach (var modifier in declaration.Modifiers.OfType<BilWrappedModifier>())
                    CheckApplication(modifier.WrapperTypeRef);
                foreach (var field in declaration.Members.OfType<BilSimpleMemberDeclaration>())
                {
                    if (field.Kind is not (BilMemberKind.Field or BilMemberKind.StaticField)) continue;
                    if (!BilVerificationContext.TryParseFieldSymbol(field.Symbol,
                        out _, out _, out var fieldType)) continue;
                    fieldType = SubstituteHostGenerics(fieldType, declaration, reference);
                    Visit(fieldType, depth + 1);
                    if (field.Kind == BilMemberKind.StaticField) continue;
                    if (declaration.Kind == BilTypeKind.Wrapper)
                    {
                        if (!rich && IsNonRichValue(fieldType) == false)
                            errors.Add(new BilVerificationError("21.8", reference,
                                $"非 rich wrapper 不能持有对象或 rich 值字段：{field.Symbol}"));
                        else if (shared && IsSharedValue(fieldType) == false)
                            errors.Add(new BilVerificationError("21.8", reference,
                                $"shared wrapper 不能持有 local 字段：{field.Symbol}"));
                    }
                    foreach (var modifier in field.Modifiers.OfType<BilWrappedModifier>())
                        CheckApplication(modifier.WrapperTypeRef);
                }

                void CheckApplication(string wrapperReference)
                {
                    wrapperReference = SubstituteHostGenerics(wrapperReference, declaration, reference);
                    Visit(wrapperReference, depth + 1);
                    CheckHostApplication(reference, wrapperReference);
                }
            }

            bool? IsNonRichValue(string reference)
            {
                if (reference.StartsWith(".generic<", StringComparison.Ordinal)) return null;
                var normalized = BilVerificationContext.NormalizeTypeRef(reference);
                if (normalized.StartsWith("core::Type<", StringComparison.Ordinal)) return true;
                if (context.TryGetTypeDeclaration(normalized, out var type))
                    return type.Kind is BilTypeKind.Struct or BilTypeKind.EnumStruct or BilTypeKind.Wrapper
                        && !HasKeyword(type.Modifiers, BilKeyword.Rich);
                // 无声明的内建值布局来自 BIL 基元定义，不根据用户类名授予能力。
                if (normalized is "core::i8" or "core::i16" or "core::i32" or "core::i64"
                    or "core::u8" or "core::u16" or "core::u32" or "core::u64"
                    or "core::float" or "core::double" or "core::bool" or "core::char"
                    or "core::String") return true;
                if (normalized is "core::Any" or "core::Object" or "core::ValueType"
                    || normalized.StartsWith("core::Nullable<")
                    || normalized.StartsWith("core::Array<")
                    || normalized.StartsWith("core::Box<")
                    || normalized.StartsWith("core::Span<")
                    || normalized.StartsWith("core::SharedSpan<")) return false;
                return null; // 不完整 external 声明仍由链接后的验证与布局检查处理。
            }

            bool? IsSharedValue(string reference)
            {
                var normalized = BilVerificationContext.NormalizeTypeRef(reference);
                if (normalized.StartsWith("core::Nullable<") && normalized.EndsWith('>'))
                    return IsSharedValue(normalized["core::Nullable<".Length..^1]);
                if (IsNonRichValue(reference) == true) return true;
                if (context.TryGetTypeDeclaration(normalized, out var type))
                    return HasKeyword(type.Modifiers, BilKeyword.Shared);
                if (normalized.StartsWith("core::SharedSpan<") || normalized.StartsWith("core::Array<"))
                    return IsSharedValue(normalized[(normalized.IndexOf('<') + 1)..^1]);
                return IsNonRichValue(reference) == false ? false : null;
            }
        }
    }
}
