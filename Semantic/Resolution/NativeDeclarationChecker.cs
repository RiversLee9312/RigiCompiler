namespace RigiCompiler
{
    // ===== 子任务 3b：native 函数声明检查（SYNTAX §4.6）=====
    //
    // 自旧 DeclarationResolver.ResolveSession.CheckNativeDeclarations/
    // CheckNativeFunction 迁移。V2.5 放行 generic+native（alloc_array
    // 经 .generic.T 物化 typeid，RUNTIME §26 / BIL §22.5）；其余规则不变。
    // 白名单集合的构建自旧方法体开头上移到 Enter。
    internal sealed class NativeDeclarationChecker : ResolverVisitor<NativeDeclarationChecker>
    {
        // 参数/返回类型白名单（§4.6：§3.2 基本类型中的整数/浮点/bool/char/String）
        private HashSet<SemanticSymbol> compatibleTypes = null!;

        protected override void Enter(ResolveEnvironment env)
        {
            var b = env.Unit.Symbols.Bootstrap;
            compatibleTypes = new HashSet<SemanticSymbol>
            {
                b.Int8, b.Int16, b.Int32, b.Int64,
                b.UInt8, b.UInt16, b.UInt32, b.UInt64,
                b.Float, b.Double, b.Bool, b.Char, b.String,
            };
        }

        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph) continue;
                if (entry.Node is CallableDeclarationASTNode fn &&
                    fn.Modifiers.Contains(Keywords.NATIVE))
                {
                    CheckNativeFunction(entry, fn, (MethodSymbol)entry.Symbol, env);
                    continue;
                }
                // native 仅适用于函数（§4.6；挂类型/字段直接拒绝）
                if (ResolveEnvironment.ModifiersOf(entry.Node).Contains(Keywords.NATIVE))
                {
                    env.Error(entry.Node.Span, "'native' can only be applied to functions");
                }
                // 内建注解只允许出现在 native 函数声明上（§4.6；不属于 wrapper 体系，
                // wrapper 应用检查经 NativeAnnotationNameOf 豁免——见 CheckWrapperApplications）
                if (entry.Node is IWrapperAttachable attachable)
                {
                    foreach (var annotation in attachable.Annotations)
                    {
                        if (ResolveEnvironment.NativeAnnotationNameOf(annotation) is { } annotationName)
                        {
                            env.Error(annotation.Span ?? entry.Node.Span,
                                $"@{annotationName} can only be applied to native functions");
                        }
                    }
                }
            }
        }

        private void CheckNativeFunction(DeclEntry entry, CallableDeclarationASTNode fn,
            MethodSymbol method, ResolveEnvironment env)
        {
            // native 仅适用于函数；init/operator 直接拒绝（后续形态检查无意义）
            if (fn.Kind != CallableKind.Func)
            {
                env.Error(entry.Node.Span, "'native' can only be applied to functions");
                return;
            }
            if (fn.Body != null)
            {
                env.Error(entry.Node.Span, $"Native function '{method.Name}' must not have a body");
            }
            // 类型成员必须同时是 static（全局函数无此要求）。ext 成员视同类型
            // 成员：注册在更后的 ExtensionRegistrar（本阶段 DeclaringType 恒 null、
            // Owner 未改写），按 ExtTargetPath 非空判定
            if ((entry.DeclaringType != null || method.ExtTargetPath != null) && !method.IsStatic)
            {
                env.Error(entry.Node.Span, $"Native member function '{method.Name}' must be 'static'");
            }
            if (fn.Modifiers.Contains(Keywords.ASYNC))
            {
                env.Error(entry.Node.Span, $"Native function '{method.Name}' cannot be 'async'");
            }
            // 同容器内不得与同名函数构成重载（容器表不含 P1 重复声明，此处比的是合法重载）；
            // ext 成员尚未注册进目标容器（ExtensionRegistrar 在本阶段之后）——按目标
            // 类型既有成员表 + 同目标其余 pending ext 比对（自身计入一次，> 1 即重载）；
            // 目标解析失败毒化静默（注册阶段统一报）
            if (method.ExtTargetPath != null)
            {
                var target = env.Names.ResolveDottedPath(method.ExtTargetPath.Split('.'),
                    entry.Context, allowImports: true, reportErrors: false, span: null);
                if (target is TypeSymbol targetType)
                {
                    var sameName = targetType.Methods.Count(m => m.Name == method.Name)
                        + env.Declarations.PendingExtMembers.OfType<MethodSymbol>().Count(m =>
                            m.ExtTargetPath == method.ExtTargetPath && m.Name == method.Name);
                    if (sameName > 1)
                    {
                        env.Error(entry.Node.Span,
                            $"Native function '{method.Name}' cannot be overloaded");
                    }
                }
            }
            else
            {
                var siblings = method.Owner?.Methods ?? method.Namespace?.Methods;
                if (siblings != null && siblings.Count(m => m.Name == method.Name) > 1)
                {
                    env.Error(entry.Node.Span, $"Native function '{method.Name}' cannot be overloaded");
                }
            }
            // 参数类型白名单（ErrorType 毒化静默）——参数仍限基本类型（§4.6）；
            // 例外：Any 胖值槽（VM 统一 ABI 直传任意 VmValue——.bootstrap.rg
            // 的 any_to_string(value: Any) 落地形态，§3.8 toString 机制）
            foreach (var parameter in method.Parameters)
            {
                if (parameter.Type is null or ErrorTypeSymbol) continue;    // 毒化静默
                if (ReferenceEquals(parameter.Type, env.Unit.Symbols.Bootstrap.Any)) continue;
                if (!compatibleTypes.Contains(parameter.Type))
                {
                    env.Error(entry.Node.Span,
                        $"Parameter '{parameter.Name}' of native function '{method.Name}' must be a primitive type (integer, float, bool, char or String)");
                }
            }
            // 返回类型白名单（§4.6，S10 放宽）：基本类型，或用户声明的引用类型
            // （class/interface，含构造类型——运行时原生方法面可返回其句柄，
            // 如 core.coroutine.sleep 返回 EventAlarm；值类型、泛型参数与可变参数
            // 仍不允许；FFI 参数/返回值 ABI 细节归 Middleware，编译器只做形状校验）
            if (method.ReturnType is not null and not ErrorTypeSymbol &&
                !compatibleTypes.Contains(method.ReturnType))
            {
                var isUserRefType = method.ReturnType is TypeSymbol returnType &&
                    (returnType.Kind == TypeKind.Class || returnType.Kind == TypeKind.Interface);
                if (!isUserRefType)
                {
                    env.Error(entry.Node.Span,
                        $"Return type of native function '{method.Name}' must be a primitive type or a user-declared reference type (class/interface)");
                }
            }
            // 内建注解：@NativeLibrary 必填；@NativeSymbol 可省，缺省取函数名
            var libraryAnnotation = fn.Annotations.FirstOrDefault(
                a => ResolveEnvironment.NativeAnnotationNameOf(a) == "NativeLibrary");
            var symbolAnnotation = fn.Annotations.FirstOrDefault(
                a => ResolveEnvironment.NativeAnnotationNameOf(a) == "NativeSymbol");
            if (libraryAnnotation == null)
            {
                env.Error(entry.Node.Span, $"Native function '{method.Name}' requires @NativeLibrary(\"...\")");
            }
            else if (env.NativeAnnotationStringArgument(libraryAnnotation, entry) is { } library)
            {
                method.NativeLibrary = library;
            }
            method.NativeSymbol = symbolAnnotation == null
                ? method.Name
                : env.NativeAnnotationStringArgument(symbolAnnotation, entry);
            // capability 机制只由真正内嵌标准库触达，伪造库路径/符号名不能取得特权。
            if (!entry.Context.File.IsCompilerLibrary && method.NativeLibrary == "rigi_rt"
                && (method.NativeSymbol?.StartsWith("handle_", StringComparison.Ordinal) == true
                    || method.NativeSymbol == "place_same_target"))
                env.Error(entry.Node.Span, "Handle/Place runtime symbols are compiler-private");
        }
    }
}
