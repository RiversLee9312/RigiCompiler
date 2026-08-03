namespace LatteCompiler
{
    // P3 绑定驱动器（VISITOR_REWRITE.md §4）：遍历编译单元的声明骨架，
    // 为每个函数体创建独立 BindContext（函数体互不嵌套；每体一个上下文
    // 实例——旧 BindSession 的「防御性清空」由此消失），经分派器启动绑定。
    internal sealed class BindingDriver
    {
        private readonly BindEnvironment env;
        private readonly List<BoundFunctionBody> bodies = new List<BoundFunctionBody>();

        public BindingDriver(BindEnvironment env)
        {
            this.env = env;
        }

        public IReadOnlyList<BoundFunctionBody> Run()
        {
            foreach (var file in env.Unit.SourceFiles)
            {
                var fileCtx = env.Declarations.FileContextOf(file);
                foreach (var decl in file.Declarations)
                {
                    WalkDeclaration(decl, fileCtx, declaringType: null);
                }
            }
            return bodies;
        }

        // 遍历声明骨架找函数体（不进函数体内部；全局字段初始化器 S5 跳过）
        private void WalkDeclaration(ASTNode node, FileContext fileCtx, TypeSymbol? declaringType)
        {
            switch (node)
            {
                case CallableDeclarationASTNode fn:
                    if (fn.Body == null) return;    // 抽象/接口方法无体
                    var symbol = env.Declarations.SymbolOf(fn) as MethodSymbol
                        ?? throw new CompilerInternalException("P1 未登记函数符号: " + fn.Name);
                    BindBody(fn, symbol, fileCtx, declaringType);
                    return;
                case ClassDeclarationASTNode or StructDeclarationASTNode or InterfaceDeclarationASTNode
                    or EnumStructDeclarationASTNode or WrapperDeclarationASTNode:
                    var nested = env.Declarations.SymbolOf(node) as TypeSymbol
                        ?? throw new CompilerInternalException("P1 未登记类型符号");
                    foreach (var member in MembersOf(node))
                    {
                        WalkDeclaration(member, fileCtx, nested);
                    }
                    return;
                default:
                    // 全局字段/namespace/import/enum case：S5 不分析
                    return;
            }
        }

        private static List<ASTNode> MembersOf(ASTNode node) => node switch
        {
            ClassDeclarationASTNode d => d.Members,
            StructDeclarationASTNode d => d.Members,
            InterfaceDeclarationASTNode d => d.Members,
            EnumStructDeclarationASTNode d => d.Members,
            WrapperDeclarationASTNode d => d.Members,
            _ => throw new CompilerInternalException("非类型声明节点: " + node.GetType().Name),
        };

        private void BindBody(CallableDeclarationASTNode fn, MethodSymbol symbol,
            FileContext fileCtx, TypeSymbol? owner)
        {
            var ctx = new BindContext(symbol, fileCtx, owner);
            var body = BlockDispatcher.Visit(fn.Body!, null, ctx, env);
            // 所有路径显式返回（SYNTAX §4.1 无隐式返回）
            if (symbol.ReturnType != null && !BoundAnalysis.GuaranteesReturn(body))
            {
                env.Error(fn.Span, $"Function '{symbol.Name}' must return a value on all code paths");
            }
            bodies.Add(new BoundFunctionBody(symbol, ctx.Locals.ToList(), body));
        }
    }
}
