namespace LatteCompiler
{
    // ===== import 可解析性校验（import/namespace 模块语义义务）=====
    //
    // 自旧 DeclarationResolver.ResolveSession.ValidateImports 迁移，行为不变。
    internal sealed class ImportValidator : ResolverVisitor<ImportValidator>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var file in env.Unit.SourceFiles)
            {
                var ctx = env.Declarations.FileContextOf(file);
                foreach (var item in ctx.Imports)
                {
                    var path = item.symbolNode.symbol;
                    // import 路径自身不带泛型实参；解析失败统一在此报一次
                    // （名字解析消费 import 时一律静默，避免二次噪音）
                    var resolved = env.Names.ResolveSymbolPath(path, ctx, declaringType: null,
                        declaringMethod: null, allowImports: false, reportErrors: false, span: null);
                    if (resolved is ErrorTypeSymbol)
                    {
                        env.Error(item.symbolNode.Span ?? file.Span,
                            $"Unresolved import: '{NameResolver.PathText(path)}'");
                    }
                }
            }
        }
    }
}
