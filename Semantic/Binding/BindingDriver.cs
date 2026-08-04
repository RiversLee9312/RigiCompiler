namespace LatteCompiler
{
    // P3 绑定驱动器（VISITOR_REWRITE.md §4）：遍历编译单元的声明骨架，
    // 为每个函数体创建独立 BindContext（函数体互不嵌套；每体一个上下文
    // 实例——旧 BindSession 的「防御性清空」由此消失），经分派器启动绑定。
    //
    // S8d 起分两阶段：①参数默认值预绑定（声明点作用域，先于一切函数体
    // 绑定——调用点填充时 callee 的默认值必已就绪，前向引用安全）；
    // ②逐函数体绑定。
    internal sealed class BindingDriver
    {
        private readonly BindEnvironment env;
        private readonly List<BoundFunctionBody> bodies = new List<BoundFunctionBody>();

        public BindingDriver(BindEnvironment env)
        {
            this.env = env;
            env.SetDefaultValueBinder(BindOneParameterDefault);
        }

        public IReadOnlyList<BoundFunctionBody> Run()
        {
            // 阶段 1（S8d）：参数默认值绑定（SYNTAX §4.2 声明点作用域）。
            // GetParameterDefault 记忆化按需绑定——前向依赖（f(a = h())
            // 声明先于 h）由调用点查表递归触发，声明顺序不影响语义
            WalkSkeleton((fn, symbol, fileCtx, owner) =>
            {
                foreach (var parameter in symbol.Parameters)
                {
                    if (parameter.DefaultValue != null) env.GetParameterDefault(parameter);
                }
            });
            // 阶段 2：逐函数体绑定
            WalkSkeleton((fn, symbol, fileCtx, owner) =>
            {
                if (fn.Body == null) return;    // 抽象/接口方法无体
                BindBody(fn, symbol, fileCtx, owner);
            });
            return bodies;
        }

        // 声明骨架遍历（两阶段共用）：对每个可调用声明回调（符号/FileCtx/宿主齐备）
        private void WalkSkeleton(
            Action<CallableDeclarationASTNode, MethodSymbol, FileContext, TypeSymbol?> visit)
        {
            foreach (var file in env.Unit.SourceFiles)
            {
                var fileCtx = env.Declarations.FileContextOf(file);
                foreach (var decl in file.Declarations)
                {
                    WalkDeclaration(decl, fileCtx, declaringType: null, visit);
                }
            }
        }

        // 遍历声明骨架找可调用声明（不进函数体内部；全局字段初始化器 S5 跳过）
        private void WalkDeclaration(ASTNode node, FileContext fileCtx, TypeSymbol? declaringType,
            Action<CallableDeclarationASTNode, MethodSymbol, FileContext, TypeSymbol?> visit)
        {
            switch (node)
            {
                case CallableDeclarationASTNode fn:
                    var symbol = env.Declarations.SymbolOf(fn) as MethodSymbol
                        ?? throw new CompilerInternalException("P1 未登记函数符号: " + fn.Name);
                    visit(fn, symbol, fileCtx, declaringType);
                    return;
                case ClassDeclarationASTNode or StructDeclarationASTNode or InterfaceDeclarationASTNode
                    or EnumStructDeclarationASTNode or WrapperDeclarationASTNode:
                    var nested = env.Declarations.SymbolOf(node) as TypeSymbol
                        ?? throw new CompilerInternalException("P1 未登记类型符号");
                    foreach (var member in MembersOf(node))
                    {
                        WalkDeclaration(member, fileCtx, nested, visit);
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

        // 单参数默认值绑定（S8d，SYNTAX §4.2 声明点作用域）：阶段 1 遍历与
        // 调用点懒触发共用（env 记忆化保证每参数只绑一次——诊断不重复）。
        // 宿主上下文经 AST Parent 链找回（DefaultValue → Parameter →
        // ParameterList → Callable）；IsDefaultValueContext 隔离形参引用与
        // this（视同静态上下文）；含局部声明的默认值暂不支持（P4 无法物化
        // 跨函数局部）；绑定失败返回 null（诊断已落袋）
        private BoundExpression? BindOneParameterDefault(ParameterSymbol parameter)
        {
            if (parameter.DefaultValue?.Parent?.Parent?.Parent
                is not CallableDeclarationASTNode fn)
            {
                throw new CompilerInternalException("默认值表达式不在可调用声明内");
            }
            var symbol = env.Declarations.SymbolOf(fn) as MethodSymbol
                ?? throw new CompilerInternalException("P1 未登记函数符号: " + fn.Name);
            var file = env.Unit.SourceFiles.FirstOrDefault(
                f => f.Span?.sourceName == fn.Span?.sourceName)
                ?? throw new CompilerInternalException("找不到声明所在源文件: " + fn.Name);
            var fileCtx = env.Declarations.FileContextOf(file);
            // 声明宿主（语法嵌套位置；ext 方法声明在全局/他类型内时与
            // Method.Owner 不同，与 BindBody 的 declaringType 同语义）
            var owner = env.Declarations.SymbolOf(fn.Parent!) as TypeSymbol;
            var span = parameter.DefaultValue.Span ?? fn.Span;
            if (parameter.Type is GenericParameterSymbol)
            {
                env.Error(span, "P3: generic type parameters are not supported yet (S9)");
                return null;
            }
            if (parameter.Type is not TypeSymbol expectedType || expectedType is ErrorTypeSymbol)
            {
                return null;    // P2 毒化静默
            }
            // 独立上下文（Flow/Locals 隔离）；DeclaringType 保留——静态宿主
            // 成员可引用，实例成员经 HasThis 拦截。空词法作用域层（表达式
            // 绑定要求非空 scope）
            var ctx = new BindContext(symbol, fileCtx, owner, isDefaultValueContext: true);
            var value = ExpressionDispatcher.Visit(parameter.DefaultValue.Expression,
                new Scope(null), ctx, env, expectedType);
            if (value == null) return null;    // 表达式自身诊断已报
            if (ctx.Locals.Count > 0)
            {
                env.Error(span, "P3: default value expressions with local declarations " +
                    "are not supported yet (S8d)");
                return null;
            }
            if (!SymbolLookup.IsAssignable(value.Type, expectedType, env))
            {
                env.Error(span,
                    $"Default value of parameter '{parameter.Name}' must be of type " +
                    $"'{BoundAnalysis.TypeDisplay(expectedType)}', got " +
                    $"'{BoundAnalysis.TypeDisplay(value.Type)}'");
                return null;
            }
            return value;
        }

        private void BindBody(CallableDeclarationASTNode fn, MethodSymbol symbol,
            FileContext fileCtx, TypeSymbol? owner)
        {
            var ctx = new BindContext(symbol, fileCtx, owner);
            var body = BlockDispatcher.Visit(fn.Body!, null, ctx, env);
            // 所有路径显式返回（SYNTAX §4.1 无隐式返回）；返回类型为泛型
            // 参数时跳过——return 值绑定必然 S9 归口失败，此检查只产级联噪音
            if (symbol.ReturnType is TypeSymbol && !BoundAnalysis.GuaranteesReturn(body))
            {
                env.Error(fn.Span, $"Function '{symbol.Name}' must return a value on all code paths");
            }
            bodies.Add(new BoundFunctionBody(symbol, ctx.Locals.ToList(), body));
        }
    }
}
