namespace RigiCompiler
{
    // ===== @EntryPoint 内建注解检查（SYNTAX §17 程序入口）=====
    //
    // @EntryPoint 是编译器内建注解（同 §4.6 @NativeLibrary/@NativeSymbol
    // 先例——按末段名识别，不属于 wrapper 体系，wrapper 应用检查经
    // IsEntryPointAnnotation 豁免）。修饰一个静态方法（全局函数天然
    // 静态）即登记为程序入口，P4 投影 BIL entrypoint 修饰符；任意命名
    // 空间的静态方法均可——解决裸 main 命名约定只认全局命名空间的问题。
    // 多入口不在这里诊断：BIL 允许多个 entrypoint 并存，运行前由
    // vm --entry-point <符号> 显式指定（缺省恰一个才自动选中）。
    internal sealed class EntryPointChecker : ResolverVisitor<EntryPointChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph || entry.Node is not IWrapperAttachable attachable) continue;
                foreach (var annotation in attachable.Annotations)
                {
                    if (!ResolveEnvironment.IsEntryPointAnnotation(annotation)) continue;
                    CheckEntryPoint(entry, annotation, env);
                }
            }
        }

        private static void CheckEntryPoint(DeclEntry entry, AnnotationASTNode annotation,
            ResolveEnvironment env)
        {
            var span = annotation.Span ?? entry.Node.Span;
            if (annotation.HasArguments)
            {
                env.Error(span, "@EntryPoint does not take arguments");
                return;
            }
            if (entry.Node is not CallableDeclarationASTNode fn
                || entry.Symbol is not MethodSymbol method)
            {
                env.Error(span, "@EntryPoint can only be applied to functions");
                return;
            }
            if (fn.Kind != CallableKind.Func)
            {
                env.Error(span, "@EntryPoint can only be applied to functions (not init/operator)");
                return;
            }
            if (method.IsNative)
            {
                env.Error(span, "@EntryPoint cannot be applied to native functions");
                return;
            }
            // 类型成员必须 static（全局函数无此要求——天然静态）。
            // ext 成员视同类型成员（ExtTargetPath 非空；本阶段在
            // ExtensionRegistrar 之前，Owner 尚未改写）
            if ((entry.DeclaringType != null || method.ExtTargetPath != null)
                && !method.IsStatic)
            {
                env.Error(span, $"@EntryPoint member function '{method.Name}' must be 'static'");
                return;
            }
            method.IsEntryPoint = true;
        }
    }
}
