namespace RigiCompiler
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
                    // 导入目标是定义本身（可含未构造泛型定义）；实参在使用处书写
                    var resolved = env.Names.ResolveSymbolPath(path, ctx, declaringType: null,
                        declaringMethod: null, allowImports: false, reportErrors: false, span: null,
                        allowBareGenericDefinition: true);
                    if (resolved is ErrorTypeSymbol)
                    {
                        env.Error(item.symbolNode.Span ?? file.Span,
                            $"Unresolved import: '{NameResolver.PathText(path)}'");
                    }
                    // §15.2 三种形态之外的裸命名空间导入（`import core.collections`）：
                    // 具名导入（{} 已由 Parser 展开为多条）的目标是类型、命名空间
                    // 顶层函数（同名重载随名字整体导入）或全局字段/const（S4）；
                    // 通配导入的容器保留命名空间/类型双合法
                    else if (!item.importAll && resolved is not (TypeSymbol or MethodSymbol
                        or FieldSymbol))
                    {
                        env.Error(item.symbolNode.Span ?? file.Span,
                            $"Import target '{NameResolver.PathText(path)}' is not a type, " +
                            "function or field (§15.2)");
                    }
                }
            }
        }
    }
}
