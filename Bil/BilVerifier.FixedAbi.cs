namespace RigiCompiler.Bil
{
    public static partial class BilVerifier
    {
        // 内建类型的 ExternalSymbols 描述仅承载能力和签名；绝不能重定义布局。
        private static void VerifyFixedAbiDescriptions(BilVerificationContext context,
            List<BilVerificationError> errors)
        {
            foreach (var type in context.Module.ExternalSymbols.OfType<BilTypeDeclaration>())
            {
                var canonical = BilVerificationContext.NormalizeTypeRef(type.Symbol);
                var scalar = canonical is "core::i8" or "core::i16" or "core::i32" or "core::i64"
                    or "core::u8" or "core::u16" or "core::u32" or "core::u64"
                    or "core::float" or "core::double" or "core::bool" or "core::char" or "core::String";
                if (!scalar && canonical != "core::Array") continue;
                if (type.Kind != (scalar ? BilTypeKind.Struct : BilTypeKind.Class)
                    || type.GenericParameters.Count != (scalar ? 0 : 1)
                    || type.GenericVariances.Any(v => v != BilGenericVariance.None)
                    || type.ExtendsType != null
                    || type.Modifiers.OfType<BilKeywordModifier>().Any()
                    || type.Members.Any(m => m is not BilSimpleMemberDeclaration
                        { Kind: BilMemberKind.Method or BilMemberKind.StaticMethod }))
                    errors.Add(new BilVerificationError("21.8", type.Symbol,
                        "固定 ABI 外部描述不能改变类型形状、基类或实例存储"));
                foreach (var app in type.Modifiers.OfType<BilWrappedModifier>())
                    if (!IsFixedAbiMarker(app.WrapperTypeRef))
                        errors.Add(new BilVerificationError("21.8", type.Symbol,
                            "固定 ABI 类型只能应用构造为空且不含存储或代理的纯标记 wrapper"));
            }

            bool IsFixedAbiMarker(string reference)
            {
                if (!context.TryGetTypeDeclaration(reference, out var wrapper)
                    || wrapper.Kind != BilTypeKind.Wrapper
                    || HasKeyword(wrapper.Modifiers, BilKeyword.Rich)
                    || wrapper.Modifiers.OfType<BilWrappedModifier>().Any()) return false;
                foreach (var member in wrapper.Members)
                {
                    if (member is not BilSimpleMemberDeclaration method
                        || method.Kind == BilMemberKind.Field
                        || method.Modifiers.OfType<BilWrapperProxyModifier>().Any()
                        || method.Modifiers.OfType<BilWrappedModifier>().Any()) return false;
                    if (!HasKeyword(method.Modifiers, BilKeyword.Init)) continue;
                    var body = context.Module.Functions.FirstOrDefault(f => f.Symbol == method.Symbol);
                    if (body == null || body.Args.Any(a => a.Name is not (".this" or ".return"))
                        || body.Blocks.Count != 1 || body.Blocks[0].Instructions.Count != 1
                        || body.Blocks[0].Instructions[0] is not RetInstruction { Value: null }) return false;
                }
                return true;
            }
        }
    }
}
