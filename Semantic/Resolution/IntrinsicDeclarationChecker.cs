namespace RigiCompiler
{
    // 声明可由源码扩展，物理表示不可扩展。这里仅核对固定 ABI，不登记能力。
    internal sealed class IntrinsicDeclarationChecker : ResolverVisitor<IntrinsicDeclarationChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var type in env.Unit.Symbols.Bootstrap.SourceTypes.Values.Where(t => t.IsBuiltin))
            {
                var syntax = env.Declarations.DeclarationOf(type);
                if (syntax == null) continue; // 无标准库的仅签名分析。
                var expectedKind = type.Name switch
                {
                    "Any" => TypeKind.Interface,
                    "Enum" => TypeKind.EnumStruct,
                    "Wrapper" => TypeKind.Wrapper,
                    "Object" or "Span" or "SharedSpan" or "Nullable" or "Box" or "Array" or "Map" => TypeKind.Class,
                    _ => TypeKind.Struct,
                };
                var arity = type.Name switch
                {
                    "Map" => 2,
                    "Type" or "Span" or "SharedSpan" or "Nullable" or "Box" or "Array" => 1,
                    _ => 0,
                };
                if (type.Kind != expectedKind || type.GenericParameters.Count != arity
                    || type.IsRich || type.IsShared != (type.Name == "SharedSpan")
                    || type.IsUnsafe || type.IsSingleton || type.Interfaces.Count != 0
                    || type.Cases.Count != 0 || type.NestedTypes.Count != 0)
                    env.Error(syntax.Span, $"内建类型 '{type.Name}' 的声明形状与固定 ABI 不一致");

                foreach (var field in type.Fields.Where(f => !f.IsStatic && f.ExtTargetPath == null))
                {
                    // 数组头中的只读长度是既有 ABI 投影，不是新增 Rigi 存储。
                    if (type.Name is "Span" or "SharedSpan" or "Array"
                        && field.Name == "length" && field.IsConst
                        && ReferenceEquals(field.FieldType, env.Unit.Symbols.Bootstrap.Int32)
                        && field.Getter == null && field.Setter == null
                        && env.Declarations.DeclarationOf(field) is VariableDeclarationASTNode { Initializer: null })
                        continue;
                    env.Error(syntax.Span, $"内建类型 '{type.Name}' 不允许新增实例存储：{field.Name}");
                }
                foreach (var app in type.AppliedWrappers)
                    if (!IsPureMarker(app.WrapperDefinition, env.Declarations))
                        env.Error(app.Syntax?.Span ?? syntax.Span,
                            $"固定 ABI 内建类型 '{type.Name}' 只能应用无存储、无代理且构造为空的纯标记 wrapper");
            }
        }

        internal static bool IsPureMarker(TypeSymbol wrapper, DeclarationCollection declarations)
        {
            if (wrapper.Kind != TypeKind.Wrapper || wrapper.IsRich
                || wrapper.Fields.Any(f => !f.IsStatic)
                || wrapper.AppliedWrappers.Count != 0
                || wrapper.Methods.Any(m => m.ProxyTemplate != null || m.AppliedWrappers.Count != 0)) return false;
            foreach (var init in wrapper.Methods.Where(m => m.Kind == MethodKind.Init))
                if (init.Parameters.Count != 0 || init.IsNative || init.IsAsync
                    || declarations.DeclarationOf(init) is not CallableDeclarationASTNode
                        { Body: null } and not CallableDeclarationASTNode { Body.Statements.Count: 0 })
                    return false;
            return true;
        }
    }
}
