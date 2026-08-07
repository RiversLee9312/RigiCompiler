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
                    // interface 不得声明字段（SYNTAX §11，同 ModifierChecker 对
                    // 成员字段的禁令）——ext 注入同样禁止（M80 收口：此前 ext
                    // 条目 DeclaringType 恒 null，绕过该检查）
                    if (targetType.Kind == TypeKind.Interface)
                    {
                        env.Error(entry.Node.Span,
                            $"Extension field '{field.Name}' cannot target interface '{targetType.Name}' (interfaces cannot declare fields)");
                        continue;
                    }
                    // 重复/遮蔽检测：与目标类型既有字段同名即诊断（P1 同名字段口径），
                    // 不注册——防止 P3 查找双候选静默遮蔽
                    if (targetType.Fields.Any(f => f.Name == field.Name))
                    {
                        env.Error(entry.Node.Span,
                            $"Extension field '{field.Name}' duplicates an existing member of type '{targetType.Name}'");
                        continue;
                    }
                    // ext 实例字段注册后是目标类型的实例字段，与声明在目标体内
                    // 同受 §3.1.1 闭包表约束（M80 收口：FieldClosureChecker 运行
                    // 在本阶段之前，此前 ext 字段永不被覆盖）；静态 ext 字段不
                    // 参与实例闭包（共享安全由 SharedSafetyGateChecker 闸门 1 覆盖）
                    if (!field.IsStatic && FieldClosureChecker.CheckExtensionField(
                        targetType, field, entry.Node.Span, env))
                    {
                        continue;
                    }
                    field.AttachToExtTarget(targetType);
                    targetType.Fields.Add(field);
                    // ext 字段的访问器随字段随迁宿主（S8e；仍不入容器方法表——
                    // P4b 声明发射由字段槽驱动）
                    field.Getter?.AttachToExtTarget(targetType);
                    field.Setter?.AttachToExtTarget(targetType);
                }
                else if (symbol is MethodSymbol method)
                {
                    // 重复/遮蔽检测：与目标类型既有方法同签名即诊断
                    // （P1 MethodKey 口径的符号级版本：名 + 泛型元数 + 参数名序列
                    // + 参数类型引用相等序列），不注册
                    if (targetType.Methods.Any(m => SameSignature(m, method)))
                    {
                        env.Error(entry.Node.Span,
                            $"Extension method '{method.Name}' duplicates an existing member of type '{targetType.Name}'");
                        continue;
                    }
                    method.AttachToExtTarget(targetType);
                    targetType.Methods.Add(method);
                }
            }
        }

        // 符号级同签名判定（对应 P1 DeclarationCollector.MethodKey 文本口径）：
        // 名 + 泛型元数 + 参数个数 + 参数名序列 + 参数类型引用相等序列。
        // 参数类型未解析（null）时只比名字结构，保守不判重
        private static bool SameSignature(MethodSymbol a, MethodSymbol b)
        {
            if (a.Name != b.Name
                || a.GenericParameters.Count != b.GenericParameters.Count
                || a.Parameters.Count != b.Parameters.Count)
            {
                return false;
            }
            for (int i = 0; i < a.Parameters.Count; i++)
            {
                if (a.Parameters[i].Name != b.Parameters[i].Name) return false;
                var ta = a.Parameters[i].Type;
                var tb = b.Parameters[i].Type;
                if (ta != null && tb != null && !ReferenceEquals(ta, tb)) return false;
            }
            return true;
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
                        span: annotation.Name.Span ?? annotation.Span ?? entry.Node.Span,
                        allowBareGenericDefinition: true);
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
                    // S11a：应用登记为 WrapperApplication 记录——Entity wrapper 恰一
                    // 泛型参数时 TTarget 代入宿主构造（泛型实参显形）；元数非法的
                    // Entity wrapper 由 ProxyShapeChecker 诊断，此处按定义原样登记
                    var appliedType = wrapperType;
                    if (wrapperType.WrapperTarget == WrapperTargetKind.Entity
                        && wrapperType.GenericParameters.Count == 1
                        && entry.Symbol is TypeSymbol applicationHost)
                    {
                        appliedType = env.Unit.Symbols.GetConstructedType(wrapperType, applicationHost);
                    }
                    ResolveEnvironment.AppliedWrappersOf(entry.Symbol)?.Add(
                        new WrapperApplication(appliedType, annotation));
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
                    foreach (var application in ifaceDef.AppliedWrappers)
                    {
                        var wrapper = application.WrapperDefinition;
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

    // ===== wrapper 继承闭包检查 =====
    //
    // wrapper 应用是声明列表语义，不是隐式传染：子声明必须把继承闭包中
    // 的应用按原相对顺序重新写出。这里只比较 WrapperDefinition 身份和
    // 注解实参结构；额外应用允许，重复路径按定义级身份去重。
    internal sealed class WrapperInheritanceChecker : ResolverVisitor<WrapperInheritanceChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.TypeEntries)
            {
                if (!entry.InGraph || entry.Symbol is not TypeSymbol type || type.IsBuiltin) continue;
                CheckList(entry, type.AppliedWrappers,
                    InheritedEntityWrappers(type, env), "Entity", env);
            }

            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph || entry.DeclaringType == null) continue;
                if (entry.Symbol is MethodSymbol { Kind: MethodKind.Regular, IsOverride: true } method)
                {
                    var inherited = OverrideChecker.InheritedMethodsForWrapper(
                        entry.DeclaringType, method, env)
                        .SelectMany(m => m.AppliedWrappers);
                    CheckList(entry, method.AppliedWrappers, inherited, "Method", env);
                }
                else if (entry.Symbol is FieldSymbol field
                    && entry.Node is VariableDeclarationASTNode)
                {
                    var inherited = new List<WrapperApplication>();
                    if (field.Getter?.IsOverride == true)
                    {
                        inherited.AddRange(OverrideChecker.InheritedFieldsForAccessor(
                            entry.DeclaringType, field.Getter, env)
                            .SelectMany(f => f.AppliedWrappers));
                    }
                    if (field.Setter?.IsOverride == true)
                    {
                        inherited.AddRange(OverrideChecker.InheritedFieldsForAccessor(
                            entry.DeclaringType, field.Setter, env)
                            .SelectMany(f => f.AppliedWrappers));
                    }
                    if (inherited.Count > 0)
                    {
                        // getter/setter wrapper 应用挂在同一个字段声明上；同一字段
                        // 的两个访问器只检查一次，避免同一缺失应用产生重复诊断。
                        CheckList(entry, field.AppliedWrappers, inherited, "Accessor", env);
                    }
                }
            }
        }

        private static void CheckList(DeclEntry entry, IReadOnlyList<WrapperApplication> actual,
            IEnumerable<WrapperApplication> inherited, string category, ResolveEnvironment env)
        {
            var required = inherited
                .GroupBy(a => a.WrapperDefinition, ReferenceEqualityComparer.Instance)
                .Select(group => group.First())
                .ToList();
            var positions = new List<int>();
            foreach (var expected in required)
            {
                var expectedDefinition = expected.WrapperDefinition;
                var matches = actual
                    .Select((application, index) => (application, index))
                    .Where(pair => ReferenceEquals(pair.application.WrapperDefinition, expectedDefinition))
                    .ToList();
                if (matches.Count == 0)
                {
                    env.Error(entry.Node.Span,
                        $"{category} wrapper '{expectedDefinition.Name}' inherited by '{entry.Symbol.Name}' " +
                        "must be explicitly redeclared");
                    continue;
                }
                var matchingParameters = matches.FirstOrDefault(pair =>
                    WrapperArgumentKey(pair.application.Syntax) == WrapperArgumentKey(expected.Syntax));
                if (matchingParameters.application == null)
                {
                    env.Error(entry.Node.Span,
                        $"{category} wrapper '{expectedDefinition.Name}' has conflicting arguments in redeclaration");
                }
                positions.Add(matchingParameters.application != null
                    ? matchingParameters.index
                    : matches[0].index);
            }
            if (positions.Zip(positions.Skip(1), (left, right) => left > right).Any(x => x))
            {
                env.Error(entry.Node.Span,
                    $"{category} wrapper redeclarations cannot reorder inherited wrappers");
            }
        }

        private static List<WrapperApplication> InheritedEntityWrappers(TypeSymbol host,
            ResolveEnvironment env)
        {
            var result = new List<WrapperApplication>();
            var visited = new HashSet<TypeSymbol>(ReferenceEqualityComparer.Instance);
            if (host.BaseType != null) CollectType(host.BaseType, host, result, visited, env);
            foreach (var iface in host.Interfaces)
                CollectType(iface, host, result, visited, env);
            return result;
        }

        private static void CollectType(TypeSymbol type, TypeSymbol target,
            List<WrapperApplication> result, HashSet<TypeSymbol> visited, ResolveEnvironment env)
        {
            var definition = type.ConstructedFrom ?? type;
            if (!visited.Add(definition)) return;
            if (type.BaseType != null) CollectType(type.BaseType, target, result, visited, env);
            foreach (var iface in type.Interfaces)
            {
                var next = ReferenceEquals(definition, type)
                    ? iface
                    : (TypeSymbol)(env.Substitute(iface, definition, type) ?? iface);
                CollectType(next, target, result, visited, env);
            }
            foreach (var application in definition.AppliedWrappers)
            {
                result.Add(NormalizeEntityApplication(application, target, env));
            }
        }

        private static WrapperApplication NormalizeEntityApplication(WrapperApplication application,
            TypeSymbol target, ResolveEnvironment env)
        {
            var definition = application.WrapperDefinition;
            if (definition.WrapperTarget == WrapperTargetKind.Entity
                && definition.GenericParameters.Count == 1)
            {
                return new WrapperApplication(
                    env.Unit.Symbols.GetConstructedType(definition, target), application.Syntax);
            }
            return application;
        }

        private static string WrapperArgumentKey(AnnotationASTNode? annotation)
        {
            if (annotation == null) return "<constraint>";
            return string.Join(";", annotation.Arguments.Select(a =>
                (a.Name ?? "_") + "=" + ExpressionKey(a.Value.Expression)));
        }

        private static string ExpressionKey(ExpressionASTNode expression)
        {
            return expression switch
            {
                LiteralExpressionASTNode literal => LiteralKey(literal.Literal),
                PathExpressionASTNode path => "path:" + path.Head.Name +
                    string.Join("", path.Segments.Select(s => "." + s.Name)),
                EnumCaseExpressionASTNode e => "case:" + e.CaseName,
                UnaryExpressionASTNode unary => "unary:" + unary.Operator + "(" +
                    ExpressionKey(unary.Operand.Expression) + ")",
                BinaryExpressionASTNode binary => "binary:" + binary.Operator + "(" +
                    ExpressionKey(binary.Left.Expression) + "," + ExpressionKey(binary.Right.Expression) + ")",
                _ => expression.GetType().FullName ?? expression.GetType().Name,
            };
        }

        private static string LiteralKey(LiteralASTNode literal) => literal switch
        {
            IntLiteralASTNode i => $"i:{i.IntType}:{i.Value}",
            FloatLiteralASTNode f => $"f:{f.IsFloat}:{f.Value:R}",
            StringLiteralASTNode s => "s:" + s.Value,
            CharLiteralASTNode c => "c:" + c.Value,
            BoolLiteralASTNode b => "b:" + b.Value,
            NullLiteralASTNode => "null",
            _ => literal.GetType().FullName ?? literal.GetType().Name,
        };
    }
}
