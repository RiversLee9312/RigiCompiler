namespace LatteCompiler
{
    // ===== 子任务 7a：wrapper 声明的 @WrapperTarget 解析 =====
    //
    // 自旧 DeclarationResolver.ResolveSession.ResolveWrapperTargets 迁移，行为不变。
    internal sealed class WrapperTargetResolver : ResolverVisitor<WrapperTargetResolver>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.TypeEntries)
            {
                if (!entry.InGraph) continue;
                var type = (TypeSymbol)entry.Symbol;
                if (type.Kind != TypeKind.Wrapper || type.IsBuiltin) continue;
                var annotations = ((WrapperDeclarationASTNode)entry.Node).Annotations;
                var targetAnnotation = annotations.FirstOrDefault(ResolveEnvironment.IsWrapperTargetAnnotation);
                if (targetAnnotation == null)
                {
                    // §14 三分类是 wrapper 适用性的必备信息，规范示例均显式标注
                    // （推断规则：缺失即诊断，见 PROGRESS_REPORT 技术债）
                    env.Error(entry.Node.Span,
                        $"Wrapper '{type.Name}' requires @WrapperTarget(.Entity/.Value/.Method)");
                    continue;
                }
                var kind = ResolveEnvironment.WrapperTargetKindOf(targetAnnotation);
                if (kind == null)
                {
                    env.Error(targetAnnotation.Span ?? entry.Node.Span,
                        "@WrapperTarget expects .Entity, .Value or .Method");
                    continue;
                }
                type.WrapperTarget = kind;
            }
        }
    }

    // ===== 子任务 7b：ext 成员注册到目标类型 =====
    //
    // 自旧 DeclarationResolver.ResolveSession.RegisterExtensions 迁移，行为不变。
    internal sealed class ExtensionRegistrar : ResolverVisitor<ExtensionRegistrar>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var symbol in env.Declarations.PendingExtMembers)
            {
                var entry = env.EntryOfSymbol[symbol];
                var extTargetPath = symbol switch
                {
                    FieldSymbol f => f.ExtTargetPath,
                    MethodSymbol m => m.ExtTargetPath,
                    _ => null,
                };
                // 成员位置的 ext（§4.4 只允许全局声明）：诊断已在 CheckMemberModifiers
                // 报过（DeclaringType != null），此处不再注册
                if (entry.DeclaringType != null) continue;
                var target = env.Names.ResolveDottedPath(extTargetPath!.Split('.'), entry.Context,
                    allowImports: true, reportErrors: true, span: entry.Node.Span);
                if (target is ErrorTypeSymbol) continue;    // 毒化静默
                if (target is not TypeSymbol targetType)
                {
                    env.Error(entry.Node.Span, $"Extension target '{extTargetPath}' must be a type");
                    continue;
                }
                if (symbol is FieldSymbol field)
                {
                    field.AttachToExtTarget(targetType);
                    targetType.Fields.Add(field);
                }
                else if (symbol is MethodSymbol method)
                {
                    method.AttachToExtTarget(targetType);
                    targetType.Methods.Add(method);
                }
            }
        }
    }

    // ===== 子任务 7c：wrapper 应用检查（类别匹配 + 目标矩阵 + interface 传染）=====
    //
    // 自旧 DeclarationResolver.ResolveSession.CheckWrapperApplications/
    // CheckWrapperCategoryMatch/CheckValueMethodTarget/CheckInterfaceWrapperContagion
    // 迁移，行为不变。
    internal sealed class WrapperApplicationChecker : ResolverVisitor<WrapperApplicationChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            CheckWrapperApplications(env);
            CheckInterfaceWrapperContagion(env);
        }

        private static void CheckWrapperApplications(ResolveEnvironment env)
        {
            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph || entry.Node is not IWrapperAttachable attachable) continue;
                foreach (var annotation in attachable.Annotations)
                {
                    if (ResolveEnvironment.IsWrapperTargetAnnotation(annotation))
                    {
                        // wrapper 声明上的已在 ResolveWrapperTargets 处理；挂别处即非法
                        if (entry.Symbol is not TypeSymbol { Kind: TypeKind.Wrapper })
                        {
                            env.Error(annotation.Span ?? entry.Node.Span,
                                "@WrapperTarget can only be applied to wrapper declarations");
                        }
                        continue;
                    }
                    // @NativeLibrary/@NativeSymbol 是编译器内建注解（§4.6），不属于
                    // wrapper 体系；合法性已在 CheckNativeDeclarations 处理
                    if (ResolveEnvironment.NativeAnnotationNameOf(annotation) != null) continue;
                    var resolved = env.Names.ResolveSymbolPath(annotation.Name.symbol, entry.Context,
                        entry.DeclaringType, declaringMethod: null,
                        allowImports: true, reportErrors: true,
                        span: annotation.Name.Span ?? annotation.Span ?? entry.Node.Span);
                    if (resolved is ErrorTypeSymbol) continue;    // 毒化静默
                    if (resolved is not TypeSymbol { Kind: TypeKind.Wrapper } wrapperType)
                    {
                        env.Error(annotation.Span ?? entry.Node.Span,
                            $"'{NameResolver.PathText(annotation.Name.symbol)}' is not a wrapper type");
                        continue;
                    }
                    // wrapper 自身的 @WrapperTarget 缺失/非法已在声明处报过，此处静默
                    if (wrapperType.WrapperTarget is { } targetKind)
                    {
                        CheckWrapperCategoryMatch(entry, wrapperType, targetKind, annotation, env);
                    }
                    ResolveEnvironment.AppliedWrappersOf(entry.Symbol)?.Add(wrapperType);
                }
            }
        }

        // §14.9：@WrapperTarget 类别 × 目标声明分类 + 宿主可内嵌性 + shared 目标矩阵
        private static void CheckWrapperCategoryMatch(DeclEntry entry, TypeSymbol wrapperType,
            WrapperTargetKind targetKind, AnnotationASTNode annotation, ResolveEnvironment env)
        {
            var span = annotation.Span ?? entry.Node.Span;
            switch (targetKind)
            {
                case WrapperTargetKind.Entity:
                    if (entry.Symbol is not TypeSymbol targetType)
                    {
                        env.Error(span, $"Entity wrapper '{wrapperType.Name}' can only be applied to type declarations");
                        return;
                    }
                    // 宿主可内嵌性：非 rich struct/enum struct 不能被任何 wrapper 修饰
                    if ((targetType.Kind == TypeKind.Struct || targetType.Kind == TypeKind.EnumStruct)
                        && !targetType.IsRich)
                    {
                        env.Error(span, $"Non-rich struct '{targetType.Name}' cannot be wrapped (§14.9)");
                    }
                    // shared 矩阵 D：非 shared wrapper 仅修饰非 shared 类型
                    if (!wrapperType.IsShared && targetType.IsShared)
                    {
                        env.Error(span, $"Non-shared wrapper '{wrapperType.Name}' cannot wrap shared type '{targetType.Name}'");
                    }
                    return;
                case WrapperTargetKind.Value:
                    if (entry.Symbol is not FieldSymbol field)
                    {
                        env.Error(span, $"Value wrapper '{wrapperType.Name}' can only be applied to fields and variables");
                        return;
                    }
                    CheckValueMethodTarget(wrapperType, span, host: field.Owner,
                        isGlobalOrStatic: field.Owner == null || field.IsStatic,
                        targetDescription: $"field '{field.Name}'", env);
                    return;
                case WrapperTargetKind.Method:
                    if (entry.Symbol is not MethodSymbol method)
                    {
                        env.Error(span, $"Method wrapper '{wrapperType.Name}' can only be applied to methods");
                        return;
                    }
                    CheckValueMethodTarget(wrapperType, span, host: method.Owner,
                        isGlobalOrStatic: method.Owner == null || method.IsStatic,
                        targetDescription: $"method '{method.Name}'", env);
                    return;
            }
        }

        // Value/Method wrapper 的共享判定（矩阵 A/B 同构）：
        // 全局/静态目标只允许 shared wrapper；非 shared wrapper 还要求宿主类型非 shared；
        // 非 rich struct 的实例字段/方法不能挂 wrapper（宿主须能内嵌 rich struct）。
        // （栈上变量属矩阵 C 恒合法，但栈上声明不进 P1/P2，归 P3。）
        private static void CheckValueMethodTarget(TypeSymbol wrapperType, CharRange? span,
            TypeSymbol? host, bool isGlobalOrStatic, string targetDescription, ResolveEnvironment env)
        {
            if (isGlobalOrStatic)
            {
                if (!wrapperType.IsShared)
                {
                    env.Error(span, $"Non-shared wrapper '{wrapperType.Name}' cannot wrap global or static {targetDescription}");
                }
                return;
            }
            if (host != null &&
                (host.Kind == TypeKind.Struct || host.Kind == TypeKind.EnumStruct) && !host.IsRich)
            {
                env.Error(span, $"Instance {targetDescription} of non-rich struct '{host.Name}' cannot be wrapped (§14.9)");
            }
            if (!wrapperType.IsShared && host is { IsShared: true })
            {
                env.Error(span, $"Non-shared wrapper '{wrapperType.Name}' cannot wrap {targetDescription} of shared type '{host.Name}'");
            }
        }

        // §14.9 interface 实现者传染（在实现者声明处检查）：
        // 被非 shared wrapper 修饰的 interface 不得被 shared 类型实现
        private static void CheckInterfaceWrapperContagion(ResolveEnvironment env)
        {
            foreach (var entry in env.TypeEntries)
            {
                if (!entry.InGraph) continue;
                var type = (TypeSymbol)entry.Symbol;
                foreach (var iface in type.Interfaces)
                {
                    var ifaceDef = iface.ConstructedFrom ?? iface;
                    foreach (var wrapper in ifaceDef.AppliedWrappers)
                    {
                        if (!wrapper.IsShared && type.IsShared)
                        {
                            env.Error(entry.Node.Span,
                                $"Shared type '{type.Name}' cannot implement interface '{ifaceDef.Name}' wrapped by non-shared wrapper '{wrapper.Name}' (§14.9)");
                        }
                    }
                }
            }
        }
    }
}
