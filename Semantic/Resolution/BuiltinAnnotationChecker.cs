namespace RigiCompiler
{
    // ===== @Terminal / @Internal 内建注解（MW11d，同 @EntryPoint 族）=====
    //
    // 按末段名硬编码识别，不属于 wrapper 体系。本阶段只做目标校验与
    // 标志位落定；组合终点检查与 @Internal 应用限制见 WrapperApplicationChecker。
    internal sealed class BuiltinAnnotationChecker : ResolverVisitor<BuiltinAnnotationChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph || entry.Node is not IWrapperAttachable attachable) continue;
                foreach (var annotation in attachable.Annotations)
                {
                    if (ResolveEnvironment.IsTerminalAnnotation(annotation))
                        CheckMarker(entry, annotation, env, terminal: true);
                    else if (ResolveEnvironment.IsInternalAnnotation(annotation))
                        CheckMarker(entry, annotation, env, terminal: false);
                }
            }
        }

        private static void CheckMarker(DeclEntry entry, AnnotationASTNode annotation,
            ResolveEnvironment env, bool terminal)
        {
            var name = terminal ? "@Terminal" : "@Internal";
            var span = annotation.Span ?? entry.Node.Span;
            if (annotation.HasArguments)
            {
                env.Error(span, $"{name} does not take arguments");
                return;
            }
            if (entry.Symbol is not TypeSymbol { Kind: TypeKind.Wrapper } wrapper)
            {
                env.Error(span, $"{name} can only be applied to wrapper declarations");
                return;
            }
            if (terminal) wrapper.IsTerminal = true;
            else wrapper.IsInternal = true;
        }
    }
}
