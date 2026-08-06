namespace LatteCompiler
{
    // P3 绑定驱动器（M55 visitor 化协议）：遍历编译单元的声明骨架，
    // 为每个函数体创建独立 BindContext（函数体互不嵌套；每体一个上下文
    // 实例——旧 BindSession 的「防御性清空」由此消失），经分派器启动绑定。
    //
    // 分三阶段：①参数默认值预绑定（S8d，声明点作用域，先于一切函数体
    // 绑定——调用点填充时 callee 的默认值必已就绪，前向引用安全）；
    // ①·5 enum case init 模板绑定（S11，SYNTAX §12.1，同为声明点
    // 作用域——函数体内的 .Case 引用消费符号的 ResolvedInit/
    // HoleParameters，必须先于函数体落定）；②逐函数体绑定（S8e 起
    // 含字段 getter/setter 访问器体），并落地 init 参数映射赋值合成
    //（SYNTAX §9.3：构造时映射参数赋给字段——无体 init 合成映射体/
    // 空体，有体 init 映射赋值前插用户体头部）。
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
            // 阶段 1.5（S11，SYNTAX §12.1）：enum case init 模板绑定
            // （声明点作用域，先于一切函数体绑定）
            BindEnumCaseTemplates();
            // 阶段 2：逐函数体绑定（含字段访问器体，S8e）。
            // S11b 分流：proxy 声明体（.proxy. 前缀名）不按普通 operator
            // 体绑定（self/inner/this 上色依赖 (proxy × 目标成员) 组合
            // 语境），收集归阶段 2.5 逐组合绑定；被拦截成员（S11a 链
            // 合成，WrapperChain 非空）的用户体改挂原始体符号
            //（.wrapped.——产物 Method 即 WrappedBodySymbol），本符号
            // 退化为转发壳（阶段 2.5 合成 invoke 链首）
            var proxyDeclarations =
                new Dictionary<MethodSymbol, (CallableDeclarationASTNode Node, FileContext FileCtx)>();
            var interceptedMembers = new List<(MethodSymbol Symbol, ASTNode Syntax)>();
            WalkSkeleton((fn, symbol, fileCtx, owner) =>
            {
                if (symbol.Name.StartsWith(".proxy."))
                {
                    proxyDeclarations[symbol] = (fn, fileCtx);
                    return;
                }
                if (fn.Body == null)
                {
                    // 无体 init（§9.3 映射形态天然无体，OverrideChecker 已
                    // 豁免「无体方法必须 abstract/native」）：合成映射赋值
                    // 体/空体——BilVerifier §21.2 要求非 native 本地方法
                    // 有 fn 定义。native init（P2 已诊断）与接口成员
                    //（无 body 是声明语义，§21.2 豁免）不合成
                    if (symbol.Kind == MethodKind.Init && !symbol.IsNative
                        && symbol.Owner?.Kind != TypeKind.Interface)
                    {
                        SynthesizeBodilessInitBody(fn, symbol);
                    }
                    return; // 其余抽象/接口方法无体
                }
                BindBody(fn, symbol.WrappedBodySymbol ?? symbol, fileCtx, owner);
                if (symbol.WrapperChain != null) interceptedMembers.Add((symbol, fn));
            }, (node, fileCtx, owner) => BindAccessorBodies(node, fileCtx, owner,
                interceptedMembers));
            // 阶段 2.5（S11b）：proxy 体逐组合绑定——转发壳/特化体/解包
            // shim 三件套（ROADMAP S11b）
            BindProxyBodies(proxyDeclarations, interceptedMembers);
            return bodies;
        }

        // 声明骨架遍历（两阶段共用）：对每个可调用声明回调（符号/FileCtx/宿主齐备）；
        // visitAccessors 非空时对带访问器的变量声明回调（S8e——仅阶段 2 传入：
        // 访问器符号的参数无默认值，阶段 1 无需触及）
        private void WalkSkeleton(
            Action<CallableDeclarationASTNode, MethodSymbol, FileContext, TypeSymbol?> visit,
            Action<VariableDeclarationASTNode, FileContext, TypeSymbol?>? visitAccessors = null)
        {
            foreach (var file in env.Unit.SourceFiles)
            {
                var fileCtx = env.Declarations.FileContextOf(file);
                foreach (var decl in file.Declarations)
                {
                    WalkDeclaration(decl, fileCtx, declaringType: null, visit, visitAccessors);
                }
            }
        }

        // 遍历声明骨架找可调用声明（不进函数体内部；全局字段初始化器 S5 跳过）
        private void WalkDeclaration(ASTNode node, FileContext fileCtx, TypeSymbol? declaringType,
            Action<CallableDeclarationASTNode, MethodSymbol, FileContext, TypeSymbol?> visit,
            Action<VariableDeclarationASTNode, FileContext, TypeSymbol?>? visitAccessors = null)
        {
            switch (node)
            {
                case CallableDeclarationASTNode fn:
                    var symbol = env.Declarations.SymbolOf(fn) as MethodSymbol
                        ?? throw new CompilerInternalException("P1 未登记函数符号: " + fn.Name);
                    visit(fn, symbol, fileCtx, declaringType);
                    return;
                case VariableDeclarationASTNode variable
                    when visitAccessors != null && (variable.Getter != null
                        || variable.Setter != null):
                    // 带访问器的字段/全局变量（S8e）：访问器体交阶段 2 回调
                    visitAccessors(variable, fileCtx, declaringType);
                    return;
                case ClassDeclarationASTNode or StructDeclarationASTNode or InterfaceDeclarationASTNode
                    or EnumStructDeclarationASTNode or WrapperDeclarationASTNode:
                    var nested = env.Declarations.SymbolOf(node) as TypeSymbol
                        ?? throw new CompilerInternalException("P1 未登记类型符号");
                    foreach (var member in MembersOf(node))
                    {
                        WalkDeclaration(member, fileCtx, nested, visit, visitAccessors);
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
                env.Error(span, "P3: default values for parameters of generic parameter " +
                    "type are not supported");
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
            // async 调用点闸门 1/2（S8f，SYNTAX §4.5）：对绑定产物的后置
            // 遍历——单一落点覆盖全部调用形态，与绑定点解耦
            AsyncGates.CheckFunctionBody(body, env);
            // 所有路径显式返回（SYNTAX §4.1 无隐式返回）；S9a 起返回类型
            // 为泛型参数同样检查（泛型函数体已放行，无级联噪音）
            if (symbol.ReturnType != null && !BoundAnalysis.GuaranteesReturn(body))
            {
                env.Error(fn.Span, $"Function '{symbol.Name}' must return a value on all code paths");
            }
            // init 参数映射（§9.3）：映射赋值序列前插到用户体头部——构造时
            // 映射参数赋给字段，无论 init 有无体；用户体内再写同字段 =
            // 覆盖，合法（init 内写字段自由）
            if (symbol.Kind == MethodKind.Init)
            {
                var mapped = SynthesizeInitMappingAssignments(fn, symbol);
                if (mapped.Count > 0)
                {
                    body = new BoundBlock(body.Syntax,
                        mapped.Concat(body.Statements).ToList());
                }
            }
            bodies.Add(new BoundFunctionBody(symbol, ctx.Locals.ToList(), body));
        }

        // ===== init 参数映射赋值合成（SYNTAX §9.3）=====

        // 无体 init 的体合成：逐映射参数（声明序）的赋值序列（无映射 =
        // 空体——接收参数不做事，合法；BilVerifier §21.2 要求非 native
        // 本地方法有 fn 定义，enum case 模板 init 同此落地）。直接构造
        // bound 节点（自动访问器体合成先例——合成代码无 DA/return 问题）；
        // 语法一律回指 init 声明节点；locals 空
        private void SynthesizeBodilessInitBody(CallableDeclarationASTNode fn,
            MethodSymbol symbol)
        {
            var body = new BoundBlock(fn, SynthesizeInitMappingAssignments(fn, symbol));
            bodies.Add(new BoundFunctionBody(symbol, Array.Empty<LocalSymbol>(), body));
        }

        // 映射赋值序列（声明序）：实例字段为 this.field = param，静态字段
        // 为字段引用直达。类型取定义级身份（字段引用 = MappedField.FieldType，
        // 与用户体写字段同规则；值引用 = 参数类型——P2 保证映射参数类型
        // 即字段类型或显式标注）。毒化跳过（参数/字段类型缺失或
        // ErrorType——P2 诊断已报）；无 this 上下文（static/全局 init
        // 映射实例字段——P2 未拦的历史怪胎）同样跳过，不合成崩溃形状
        private static List<BoundStatement> SynthesizeInitMappingAssignments(
            CallableDeclarationASTNode fn, MethodSymbol symbol)
        {
            var statements = new List<BoundStatement>();
            foreach (var parameter in symbol.Parameters)
            {
                var field = parameter.MappedField;
                if (field == null) continue;
                if (parameter.Type is not { } parameterType || parameterType is ErrorTypeSymbol)
                {
                    continue;
                }
                if (field.FieldType is not { } fieldType || fieldType is ErrorTypeSymbol)
                {
                    continue;
                }
                BoundExpression target;
                if (field.Owner != null && !field.IsStatic)
                {
                    if (symbol.Owner == null || symbol.IsStatic) continue;
                    target = new BoundFieldAccessExpression(fn,
                        new BoundThisExpression(fn, symbol.Owner), field, fieldType);
                }
                else
                {
                    target = new BoundFieldReferenceExpression(fn, field, fieldType);
                }
                statements.Add(new BoundAssignmentStatement(fn, target,
                    new BoundValueReferenceExpression(fn, parameter, parameterType)));
            }
            return statements;
        }

        // 访问器体绑定（S8e，SYNTAX §9.4/§9.4.1）：访问器符号挂字段三槽
        // （P1 建壳、P2 回填签名），节点是 PropertyAccessorASTNode 而非
        // CallableDeclaration——薄适配：逐访问器走与普通函数体同一通道
        // （独立 BindContext + 块分派 + return 全路径检查），产物进
        // bodies（P4 LoweringDriver 数据驱动自动捡起）
        private void BindAccessorBodies(VariableDeclarationASTNode node, FileContext fileCtx,
            TypeSymbol? owner, List<(MethodSymbol Symbol, ASTNode Syntax)> interceptedMembers)
        {
            var field = env.Declarations.SymbolOf(node) as FieldSymbol
                ?? throw new CompilerInternalException("P1 未登记字段符号: " + node.Name);
            if (node.Getter != null && field.Getter != null)
            {
                // S11b：被拦截访问器的用户体改挂原始体符号（.wrapped.get.x），
                // 本符号退化转发壳（阶段 2.5 合成）
                BindAccessorBody(node.Getter, field.Getter.WrappedBodySymbol ?? field.Getter,
                    field, isSetter: false, fileCtx, owner);
                if (field.Getter.WrapperChain != null)
                {
                    interceptedMembers.Add((field.Getter, node.Getter));
                }
            }
            if (node.Setter != null && field.Setter != null)
            {
                BindAccessorBody(node.Setter, field.Setter.WrappedBodySymbol ?? field.Setter,
                    field, isSetter: true, fileCtx, owner);
                if (field.Setter.WrapperChain != null)
                {
                    interceptedMembers.Add((field.Setter, node.Setter));
                }
            }
        }

        // 单访问器体绑定：backing 形态置上下文 value 别名标记（裸名
        // value 由 PathVisitors 拦截为 backing 直达）；backing setter
        // （显式/自动同）体首合成隐含赋值 backing = value（§9.4.1，
        // 直接构造 bound 节点，不接名称解析）；自动访问器（无体，
        // 仅 backing 形态——P2 已拒无体 computed）合成体——getter
        // 为 return value、setter 为空块（隐含赋值已足）。合成节点
        // Syntax 一律指访问器 AST 节点；字段类型毒化时合成部分跳过
        // （P2 诊断已报）
        private void BindAccessorBody(PropertyAccessorASTNode accessorNode, MethodSymbol symbol,
            FieldSymbol field, bool isSetter, FileContext fileCtx, TypeSymbol? owner)
        {
            var ctx = new BindContext(symbol, fileCtx, owner);
            if (field.HasBackingStorage) ctx.Accessor.Set(field, isSetter);
            BoundBlock body;
            if (accessorNode.Body != null)
            {
                body = BlockDispatcher.Visit(accessorNode.Body, null, ctx, env);
            }
            else if (isSetter)
            {
                body = new BoundBlock(accessorNode, new List<BoundStatement>());
            }
            else
            {
                // 自动 getter：return value（backing 读）。字段类型可为泛型
                // 参数（S9a 起值层类型契约 SemanticSymbol）——仅毒化时跳过
                // 合成（P2 诊断已报）
                var statements = new List<BoundStatement>();
                if (field.FieldType is { } getterType && getterType is not ErrorTypeSymbol)
                {
                    statements.Add(new BoundReturnStatement(accessorNode,
                        PathFacility.MakeBackingFieldReference(accessorNode, field, getterType,
                            ctx.Frame)));
                }
                body = new BoundBlock(accessorNode, statements);
            }
            // backing 形态 setter：体首隐含 backing = value（value 即新值参数）；
            // 字段类型可为泛型参数（同自动 getter）
            if (isSetter && field.HasBackingStorage
                && field.FieldType is { } backingType && backingType is not ErrorTypeSymbol)
            {
                var implicitAssign = new BoundAssignmentStatement(accessorNode,
                    PathFacility.MakeBackingFieldReference(accessorNode, field, backingType,
                        ctx.Frame),
                    new BoundValueReferenceExpression(accessorNode, symbol.Parameters[0],
                        backingType));
                body = new BoundBlock(body.Syntax,
                    new List<BoundStatement> { implicitAssign }.Concat(body.Statements).ToList());
            }
            // getter 同普通函数：return 全路径检查（符号名即字段名）——
            // 返回类型可为泛型参数，与 BindBody 同一口径（!= null；
            // S9a 起泛型函数体恢复全路径检查，访问器不豁免）
            if (symbol.ReturnType != null && !BoundAnalysis.GuaranteesReturn(body))
            {
                env.Error(accessorNode.Span,
                    $"Function '{symbol.Name}' must return a value on all code paths");
            }
            AsyncGates.CheckFunctionBody(body, env);
            bodies.Add(new BoundFunctionBody(symbol, ctx.Locals.ToList(), body));
        }

        // ===== 阶段 1.5：enum case init 模板绑定（S11，SYNTAX §12.1）=====

        // 遍历编译单元全部 enum struct 声明（含嵌套类型内的），逐 case
        // 绑定 init 调用模板。骨架遍历与 WalkSkeleton 同口径（重复声明
        // 的壳同样照绑——P1 已诊断，壳上成员表自给自足不崩溃）
        private void BindEnumCaseTemplates()
        {
            foreach (var file in env.Unit.SourceFiles)
            {
                var fileCtx = env.Declarations.FileContextOf(file);
                foreach (var decl in file.Declarations)
                {
                    WalkEnumDeclarations(decl, fileCtx);
                }
            }
        }

        private void WalkEnumDeclarations(ASTNode node, FileContext fileCtx)
        {
            switch (node)
            {
                case EnumStructDeclarationASTNode enumNode:
                    var symbol = env.Declarations.SymbolOf(node) as TypeSymbol
                        ?? throw new CompilerInternalException("P1 未登记类型符号");
                    BindCaseTemplates(enumNode, symbol, fileCtx);
                    // 嵌套类型递归（enum 可声明在类型内）
                    foreach (var member in enumNode.Members)
                    {
                        WalkEnumDeclarations(member, fileCtx);
                    }
                    return;
                case ClassDeclarationASTNode or StructDeclarationASTNode
                    or InterfaceDeclarationASTNode or WrapperDeclarationASTNode:
                    foreach (var member in MembersOf(node))
                    {
                        WalkEnumDeclarations(member, fileCtx);
                    }
                    return;
            }
        }

        // 单 enum 的 case 模板绑定：泛型 enum 归口（S11 范围决策——
        // 声明侧跳过模板绑定；使用侧 expectedType/操作数为构造 enum
        // 类型时同归口）
        private void BindCaseTemplates(EnumStructDeclarationASTNode node, TypeSymbol enumType,
            FileContext fileCtx)
        {
            if (enumType.GenericParameters.Count > 0)
            {
                env.Error(node.Span, "P3: generic enum cases are not supported yet (S11)");
                return;
            }
            var inits = enumType.Methods.Where(m => m.Kind == MethodKind.Init).ToList();
            foreach (var caseNode in node.Cases)
            {
                BindOneCaseTemplate(caseNode, inits, fileCtx, enumType);
            }
        }

        // 单 case 模板绑定（§12.1）：结构过滤（实参个数 == init 参数
        // 个数——每个 init 参数对应一个模板位置（固定值或洞）；具名
        // 模板实参的名字须匹配对应位置参数名）→ 固定实参绑定与类型
        // 适用性决胜 → 洞 pub 规则（§12.2）→ 符号两槽落定 + 固定实参
        // 产物缓存（BindEnvironment，备 P4 case 构造入口消费）。
        // 失败毒化：符号保持未落定（HoleParameters 为 null）——使用侧
        // 遇未落定静默（声明点诊断已报，不二次报）。无显式 init 的零
        // 实参 case 合法（默认零参构造，§9.3 先例）——ResolvedInit
        // 保持 null（无 init 可选），HoleParameters 落定空列表作成功标记
        private void BindOneCaseTemplate(EnumCaseASTNode caseNode, List<MethodSymbol> inits,
            FileContext fileCtx, TypeSymbol enumType)
        {
            if (env.Declarations.SymbolOf(caseNode) is not EnumCaseSymbol caseSymbol)
            {
                throw new CompilerInternalException("P1 未登记 enum case 符号: "
                    + caseNode.CaseName);
            }
            // 结构过滤（静默）：模板实参与 init 参数位置一一对应——个数
            // 相等；具名模板实参名须匹配对应位置参数名
            var candidates = inits.Where(init => StructureMatches(init, caseNode.Arguments))
                .ToList();
            if (candidates.Count == 0)
            {
                // 无显式 init 的零实参 case：默认零参构造（成功标记 =
                // HoleParameters 空列表；失败判定同为 HoleParameters == null，
                // 与 ResolvedInit 无交集歧义）
                if (caseNode.Arguments.Count == 0 && inits.Count == 0)
                {
                    caseSymbol.HoleParameters = new List<EnumCaseHoleParameter>();
                    env.SetEnumCaseFixedArguments(caseSymbol, Array.Empty<BoundExpression?>());
                    return;
                }
                env.Error(caseNode.Span,
                    $"Enum case '{caseNode.CaseName}' has no matching init template");
                return;
            }
            // 声明点隔离上下文（复用 S8d 默认值隔离语义：无 this/看不到
            // init 形参/无标签）；Frame.Method 取首个候选 init——只承载
            // 文件/宿主上下文（HasThis/CanAccess 与具体候选无关）
            var ctx = new BindContext(candidates[0], fileCtx, enumType,
                isDefaultValueContext: true);
            // 固定实参绑定与适用性决胜（OverloadResolution 先例：单候选
            // 带目标类型绑定——null 字面量与嵌套 .Case 固定实参可定型；
            // 多候选先无目标预绑，按 IsAssignable 静默过滤）
            MethodSymbol winner;
            BoundExpression?[]? fixedArgs;
            if (candidates.Count == 1)
            {
                winner = candidates[0];
                fixedArgs = BindFixedArguments(caseNode, winner, ctx, env);
                if (fixedArgs == null) return;   // 表达式自身诊断已报（静默失败）
                if (!FixedArgumentsApplicable(winner, caseNode.Arguments, fixedArgs, env))
                {
                    env.Error(caseNode.Span,
                        $"Enum case '{caseNode.CaseName}' has no matching init template");
                    return;
                }
            }
            else
            {
                var prebound = PrebindFixedArguments(caseNode, ctx, env);
                if (prebound == null) return;    // 同上（静默失败）
                var applicable = candidates.Where(init =>
                    FixedArgumentsApplicable(init, caseNode.Arguments, prebound, env)).ToList();
                if (applicable.Count == 0)
                {
                    env.Error(caseNode.Span,
                        $"Enum case '{caseNode.CaseName}' has no matching init template");
                    return;
                }
                if (applicable.Count > 1)
                {
                    var sigs = string.Join(", ", applicable.Select(SignatureOf));
                    env.Error(caseNode.Span, $"Enum case '{caseNode.CaseName}' matches " +
                        $"multiple init templates: {sigs}");
                    return;
                }
                winner = applicable[0];
                fixedArgs = MaterializeFixedArguments(caseNode, winner, prebound, ctx, env);
                if (fixedArgs == null) return;
            }
            // 局部声明拦截（S8d 默认值同口径：P4 无法物化跨函数局部）
            if (ctx.Locals.Count > 0)
            {
                env.Error(caseNode.Span, "P3: enum case template arguments with local " +
                    "declarations are not supported yet (S11)");
                return;
            }
            // 洞签名（名/类型/位置取自对应 init 参数，§12.1）与 pub
            // 规则（§12.2：绑定到非 pub init 的 case 必须是固定模板）
            var holes = new List<EnumCaseHoleParameter>();
            for (int i = 0; i < caseNode.Arguments.Count; i++)
            {
                if (!IsHoleArgument(caseNode.Arguments[i])) continue;
                var parameter = winner.Parameters[i];
                holes.Add(new EnumCaseHoleParameter(parameter.Name, parameter.Type!, i));
            }
            if (holes.Count > 0 && winner.Accessibility != Accessibility.Public)
            {
                env.Error(caseNode.Span, $"Parameterized enum case '{caseNode.CaseName}' " +
                    "requires a pub init (its template binds to a non-pub init)");
                return;
            }
            caseSymbol.ResolvedInit = winner;
            caseSymbol.HoleParameters = holes;
            env.SetEnumCaseFixedArguments(caseSymbol, fixedArgs);
        }

        // init 候选结构过滤（静默）：模板实参个数 == init 参数个数；
        // 具名模板实参的名字须匹配对应位置（同下标）init 参数名
        private static bool StructureMatches(MethodSymbol init, List<ArgumentASTNode> arguments)
        {
            if (init.Parameters.Count != arguments.Count) return false;
            for (int i = 0; i < arguments.Count; i++)
            {
                if (arguments[i].Name != null && arguments[i].Name != init.Parameters[i].Name)
                {
                    return false;
                }
            }
            return true;
        }

        // 参数洞识别（§12.1：`_` 独占一个实参位置，可具名 name = _）：
        // 实参根是单段 `_` 路径（无底座/无泛型/无后缀/无段）。镜像
        // EnumCaseResolver.IsHoleArgument（P2 洞独占性检查的同一形态；
        // Resolution/ 私有实现不可跨层复用，复写保持两份同步）
        private static bool IsHoleArgument(ArgumentASTNode argument)
        {
            return argument.Value.IsAttached
                && argument.Value.Expression is PathExpressionASTNode path
                && path.Head.Expression == null && path.Head.Name == "_"
                && path.Head.GenericArguments.Count == 0
                && path.Head.Suffixes.Count == 0 && path.Segments.Count == 0;
        }

        // 单候选路径固定实参绑定：洞位置跳过（保持 null 占位——结果按
        // 实参位置序对齐 init 参数序）；以 init 对应位置形参类型为期望
        // 类型绑定。任一实参失败返回 null（表达式自身诊断已报）
        private BoundExpression?[]? BindFixedArguments(EnumCaseASTNode caseNode,
            MethodSymbol init, BindContext ctx, BindEnvironment env)
        {
            var result = new BoundExpression?[caseNode.Arguments.Count];
            for (int i = 0; i < caseNode.Arguments.Count; i++)
            {
                var argument = caseNode.Arguments[i];
                if (IsHoleArgument(argument)) continue;
                var value = ExpressionDispatcher.Visit(argument.Value.Expression,
                    new Scope(null), ctx, env, init.Parameters[i].Type as TypeSymbol);
                if (value == null) return null;
                result[i] = value;
            }
            return result;
        }

        // 多候选路径固定实参预绑（无目标类型；null 字面量留 null 占位
        // 待胜者形参类型定型——OverloadResolution.PrebindArguments 先例）。
        // 任一实参失败返回 null（表达式自身诊断已报，不级联模板诊断）
        private BoundExpression?[]? PrebindFixedArguments(EnumCaseASTNode caseNode,
            BindContext ctx, BindEnvironment env)
        {
            var result = new BoundExpression?[caseNode.Arguments.Count];
            for (int i = 0; i < caseNode.Arguments.Count; i++)
            {
                var argument = caseNode.Arguments[i];
                if (IsHoleArgument(argument)) continue;
                if (argument.Value.Expression is LiteralExpressionASTNode
                    { Literal: NullLiteralASTNode })
                {
                    continue;
                }
                var value = ExpressionDispatcher.Visit(argument.Value.Expression,
                    new Scope(null), ctx, env);
                if (value == null) return null;
                result[i] = value;
            }
            return result;
        }

        // 类型适用性（静默）：固定实参类型可赋给对应位置 init 形参类型；
        // null 占位要求形参为 Nullable\<T\>（OverloadResolution.IsApplicable
        // 先例）；ErrorType 毒化形参静默放行（P2 诊断已报）
        private static bool FixedArgumentsApplicable(MethodSymbol init,
            List<ArgumentASTNode> arguments, BoundExpression?[] fixedArgs, BindEnvironment env)
        {
            for (int i = 0; i < arguments.Count; i++)
            {
                if (IsHoleArgument(arguments[i])) continue;
                var parameterType = init.Parameters[i].Type;
                if (parameterType is ErrorTypeSymbol) continue;
                if (fixedArgs[i] == null)
                {
                    // null 字面量占位：形参须 Nullable；形参类型不可判
                    //（泛型参数边缘场景）按不适用
                    if (parameterType is not TypeSymbol type
                        || type.ConstructedFrom != env.B.NullableDefinition)
                    {
                        return false;
                    }
                    continue;
                }
                if (parameterType is not TypeSymbol parameter
                    || !SymbolLookup.IsAssignable(fixedArgs[i]!.Type, parameter, env))
                {
                    return false;
                }
            }
            return true;
        }

        // 多候选路径落定：null 字面量占位以胜者形参类型重绑定型
        // （OverloadResolution.Materialize 先例；非 null 实参适用性
        // 已查，直接用预绑产物）
        private BoundExpression?[]? MaterializeFixedArguments(EnumCaseASTNode caseNode,
            MethodSymbol winner, BoundExpression?[] prebound, BindContext ctx, BindEnvironment env)
        {
            for (int i = 0; i < caseNode.Arguments.Count; i++)
            {
                if (IsHoleArgument(caseNode.Arguments[i]) || prebound[i] != null) continue;
                var value = LiteralVisitor.Visit(caseNode.Arguments[i].Value.Expression,
                    new Scope(null), ctx, env, winner.Parameters[i].Type as TypeSymbol);
                if (value == null) return null;
                prebound[i] = value;
            }
            return prebound;
        }

        // 歧义诊断用签名文本：name(T1, T2)（镜像 OverloadResolution
        // 私有实现——私有不可复用）
        private static string SignatureOf(MethodSymbol method)
        {
            var parts = method.Parameters.Select(p => p.Type is TypeSymbol t
                ? BoundAnalysis.TypeDisplay(t)
                : p.Type?.Name ?? "?");
            return $"{method.Name}({string.Join(", ", parts)})";
        }

        // ===== 阶段 2.5：proxy 体逐组合绑定（S11b，ROADMAP S11b）=====
        //
        // 对全部被拦截成员（S11a 链合成，WrapperChain 非空）：
        //   ① 转发壳（被修饰成员原名 fn）：body = invoke 链首（骑 vtable，
        //      RUNTIME §14 声明侧烘焙——调用点零改动）；
        //   ② 逐环特化 fn：绑定命中的 proxy 声明体（组合语境挂
        //      BindContext.Proxy——self = 宿主角色的 this、inner = 对下一环
        //      符号的普通调用、this 重写为 BoundWrapperAccessExpression）；
        //      wildcard 环的前奏物化三形参（symbol = canonical 字符串常量、
        //      namedArgs/unnamedArgs = 自 fn 形参打包，M81 定稿「wildcard
        //      特化签名 = 成员签名」），get 类别的 value 形参物化为 invoke
        //      下一环；
        //   ③ wildcard 解包 shim：body = 逐元素 cast 解包后 invoke 下一环
        //     （§14.7 同款 CastException 语义）。
        // 直接构造 bound 节点的部分（转发壳/shim/前奏）仿 M77 init 映射
        // 赋值合成先例——Syntax 回指声明节点、无 DA/return 问题；诊断按
        // (proxy, span, message) 去重（BindEnvironment.CurrentProxy）。
        private void BindProxyBodies(
            Dictionary<MethodSymbol, (CallableDeclarationASTNode Node, FileContext FileCtx)>
                proxyDeclarations,
            List<(MethodSymbol Symbol, ASTNode Syntax)> interceptedMembers)
        {
            foreach (var (member, syntax) in interceptedMembers)
            {
                var chain = member.WrapperChain!;
                SynthesizeRouterShell(member, syntax);
                for (var i = 0; i < chain.Count; i++)
                {
                    BindProxyLinkBody(chain[i], i, proxyDeclarations);
                    if (chain[i].ProxySpecialization!.UnwrapShim is { } shim)
                    {
                        SynthesizeUnwrapShimBody(shim, chain[i].ProxySpecialization, i, syntax);
                    }
                }
            }
        }

        // ① 转发壳：被修饰成员原名 fn 的 body = invoke 最外层特化（实参 =
        // 本 fn 形参逐一引用；固定泛型参数逐位转发——嵌套转发 P4b 物化
        // $.generic.T 零指令（M71 先例）；含可变泛型参数时不带显式实参，
        // 包转发归 S11g 复核）
        private void SynthesizeRouterShell(MethodSymbol member, ASTNode syntax)
        {
            var chainHead = member.WrapperChain![0];
            var thisExpr = new BoundThisExpression(syntax, member.Owner!);
            var typeArgs = member.GenericParameters.Count > 0
                && member.GenericParameters.All(p => !p.IsVariadic && !p.IsNamedVariadic)
                ? member.GenericParameters.Cast<SemanticSymbol>().ToList()
                : null;
            var args = member.Parameters
                .Select(p => (BoundExpression)new BoundValueReferenceExpression(syntax, p, p.Type!))
                .ToList();
            BoundBlock body;
            if (member.ReturnType is { } returnType)
            {
                body = new BoundBlock(syntax, new List<BoundStatement> {
                    new BoundReturnStatement(syntax, new BoundInstanceCallExpression(syntax,
                        thisExpr, chainHead, args, returnType, typeArgs)) });
            }
            else
            {
                body = new BoundBlock(syntax, new List<BoundStatement> {
                    new BoundCallStatement(syntax, chainHead, args, thisExpr, typeArgs) });
            }
            bodies.Add(new BoundFunctionBody(member, Array.Empty<LocalSymbol>(), body));
        }

        // ② 单环特化体绑定：组合语境（BindContext.Proxy）+ 前奏物化 +
        // proxy 声明 AST 体经块分派绑定。产物 Method = 特化符号（.args =
        // 成员签名拷贝——proxy 声明形参与成员同名的部分直通，同名查找
        // 命中特化符号形参；不同名部分（symbol/namedArgs/unnamedArgs/
        // get 的 value）由前奏物化局部承载，PathVisitors 裸名查找拦截）
        private void BindProxyLinkBody(MethodSymbol link, int linkIndex,
            Dictionary<MethodSymbol, (CallableDeclarationASTNode Node, FileContext FileCtx)>
                proxyDeclarations)
        {
            var spec = link.ProxySpecialization!;
            if (!proxyDeclarations.TryGetValue(spec.ProxyDeclaration, out var decl))
            {
                throw new CompilerInternalException("P3 未收集 proxy 声明体: "
                    + spec.ProxyDeclaration.Name);
            }
            if (decl.Node.Body == null)
            {
                // 无体 proxy 声明（P2 形状校验不拦体有无）——毒化：链已建，
                // 本环按空体处理（诊断落袋，不中断）
                env.Error(decl.Node.Span,
                    $"Proxy '{spec.ProxyDeclaration.Name}' has no body (§14.2)");
                return;
            }
            var chain = spec.TargetMember.WrapperChain!;
            var next = linkIndex + 1 < chain.Count ? chain[linkIndex + 1] : spec.OriginalBody;
            // self 类型 = TTarget 代入结果（应用记录 Wrapper 构造的实参）；
            // wrapper 零泛型参数时为 null——self 引用由 PathVisitors 拒绝
            //（§14.2 末条）
            var selfType = spec.Application.WrapperDefinition.GenericParameters.Count == 1
                ? spec.Application.Wrapper.TypeArguments![0] as TypeSymbol
                : null;
            var ctx = new BindContext(link, decl.FileCtx, link.Owner);
            ctx.Proxy.Set(spec, spec.UnwrapShim ?? next, selfType);
            env.CurrentProxy = spec.ProxyDeclaration;
            try
            {
                var prelude = SynthesizeProxyPrelude(ctx, spec, link, next, decl.Node);
                var body = BlockDispatcher.Visit(decl.Node.Body, null, ctx, env);
                if (prelude.Count > 0)
                {
                    body = new BoundBlock(body.Syntax,
                        prelude.Concat(body.Statements).ToList());
                }
                // return 全路径检查（与 BindBody 同口径；消息用 proxy 声明
                // 名——各组合消息一致，诊断去重天然只报一次）
                if (link.ReturnType != null && !BoundAnalysis.GuaranteesReturn(body))
                {
                    env.Error(decl.Node.Span, $"Function '{spec.ProxyDeclaration.Name}' " +
                        "must return a value on all code paths");
                }
                AsyncGates.CheckFunctionBody(body, env);
                bodies.Add(new BoundFunctionBody(link, ctx.Locals.ToList(), body));
            }
            finally
            {
                env.CurrentProxy = null;
            }
        }

        // 特化前奏物化：proxy 声明形参中「与特化 fn（成员签名拷贝）不同名」
        // 的部分物化为合成局部（const，登记 ctx.Locals 与 MaterializedLocals，
        // 初始化语句前插体首）——wildcard 的 symbol/namedArgs/unnamedArgs
        // 三形参与 get 类别的 value 形参（= invoke 下一环的无参调用结果；
        // setter 的 value 与成员形参同名直通，不在此列）
        private List<BoundStatement> SynthesizeProxyPrelude(BindContext ctx,
            ProxySpecializationInfo spec, MethodSymbol link, MethodSymbol next, ASTNode syntax)
        {
            var prelude = new List<BoundStatement>();
            var memberParamNames = new HashSet<string>(link.Parameters.Select(p => p.Name));
            foreach (var proxyParam in spec.ProxyDeclaration.Parameters)
            {
                if (memberParamNames.Contains(proxyParam.Name)) continue;   // 直通
                BoundExpression? initializer = proxyParam.Name switch
                {
                    // §14.8 canonical symbol（CanonicalSymbolPrinter 唯一
                    // canonical 来源，ARCH §4.4）
                    "symbol" => MakeStringLiteral(
                        CanonicalSymbolPrinter.PrintMethod(spec.TargetMember)),
                    // 包自 fn 形参打包（Type = Array\<Any\> 构造，与 M72 调用点
                    // 打包节点同口径）；具名包元素 = 具名可变参数（P2 已排除
                    // 可变参数成员建链，当前恒空包）
                    "namedArgs" => PackMemberArguments(link, named: true, syntax),
                    "unnamedArgs" => PackMemberArguments(link, named: false, syntax),
                    // get 类别：value = 内层结果（invoke 下一环的无参调用）
                    "value" when spec.TargetMember.Kind == MethodKind.Getter =>
                        new BoundInstanceCallExpression(syntax,
                            new BoundThisExpression(syntax, link.Owner!), next,
                            new List<BoundExpression>(), next.ReturnType!),
                    // 未识别形参名（P2 形状校验保证不出现）——防御跳过，
                    // 体内引用落 Undefined name 诊断（可恢复）
                    _ => null,
                };
                if (initializer == null) continue;
                var local = new LocalSymbol(proxyParam.Name, initializer.Type, isConst: true);
                ctx.Locals.Add(local);
                ctx.Proxy.MaterializedLocals[proxyParam.Name] = local;
                prelude.Add(new BoundLocalDeclarationStatement(syntax, local, initializer));
            }
            return prelude;
        }

        // 成员形参打包（wildcard 前奏）：实参引用特化 fn 形参（同名同序
        // 拷贝，BIL .args 一致）；具名包元素 = 具名可变参数（当前恒空——
        // P2 已排除可变参数成员建链），位置包 = 全形参声明序。
        // 包类型 = §14.7 胖值 ABI 元素形态：具名包 Array\<Pair\<String, Any\>\>、
        // 位置包 Array\<Any\>（与 P4b VarArgsEmitter 产物及 shim 形参同型——
        // S11d 发射对齐；core::Pair 缺席时具名包降级 Array\<Any\>，
        // 同 PathVisitors.VariadicParameterViewType 先例）
        private BoundExpression PackMemberArguments(MethodSymbol link, bool named, ASTNode syntax)
        {
            if (named)
            {
                var namedValues = link.Parameters
                    .Where(p => p.IsNamedVariadic)
                    .Select(p => (p.Name, (BoundExpression)new BoundValueReferenceExpression(
                        syntax, p, p.Type!)))
                    .ToList();
                return new BoundVarArgsArgument(syntax, isNamed: true,
                    Array.Empty<BoundExpression>(), namedValues, NamedPackType(env));
            }
            var packType = env.Unit.Symbols.GetConstructedType(env.B.ArrayDefinition, env.B.Any);
            var values = link.Parameters
                .Where(p => !p.IsNamedVariadic)
                .Select(p => (BoundExpression)new BoundValueReferenceExpression(syntax, p, p.Type!))
                .ToList();
            return new BoundVarArgsArgument(syntax, isNamed: false, values, null, packType);
        }

        // 具名包 ABI 类型（§14.7：Array\<Pair\<String, Any\>\>；core::Pair
        // 缺席——无 stdlib 的测试驱动——时降级 Array\<Any\>）。P2 侧
        // ProxyDispatchResolver.SynthesizeUnwrapShim 的 namedArgs 形参
        // 与本类型逐项一致（invoke 签名严格匹配）
        internal static TypeSymbol NamedPackType(BindEnvironment env)
        {
            var core = env.Unit.Symbols.GlobalNamespace.ChildNamespaces
                .FirstOrDefault(n => n.Name == "core");
            var pairDefinition = core?.Types.FirstOrDefault(t => t.Name == "Pair"
                && t.GenericParameters.Count == 2);
            var elementType = pairDefinition == null
                ? (TypeSymbol)env.B.Any
                : env.Unit.Symbols.GetConstructedType(pairDefinition, env.B.String, env.B.Any);
            return env.Unit.Symbols.GetConstructedType(env.B.ArrayDefinition, elementType);
        }

        // ③ wildcard 解包 shim body：形参（成员签名，经 shim 泛型拷贝代入）
        // 逐一从 unnamedArgs 索引取出并 cast（Any → 形参类型，拆箱——§14.7
        // 同款 CastException 语义；目标即 Any 时省 cast 直通），invoke
        // 下一环。namedArgs 恒空包（P2 已排除可变参数成员），不解包
        private void SynthesizeUnwrapShimBody(MethodSymbol shim, ProxySpecializationInfo spec,
            int linkIndex, ASTNode syntax)
        {
            var member = spec.TargetMember;
            var chain = member.WrapperChain!;
            var next = linkIndex + 1 < chain.Count ? chain[linkIndex + 1] : spec.OriginalBody;
            var packType = shim.Parameters[1].Type!;
            var unnamedRef = new BoundValueReferenceExpression(syntax, shim.Parameters[1], packType);
            var arrayGetAtIndex = env.B.ArrayDefinition.Methods
                .First(m => m.Name == "getAtIndex");
            var args = new List<BoundExpression>();
            for (var i = 0; i < member.Parameters.Count; i++)
            {
                var paramType = SubstituteMemberGenerics(member.Parameters[i].Type!, member, shim);
                BoundExpression element = new BoundIndexExpression(syntax, unnamedRef,
                    MakeIntLiteral(i), arrayGetAtIndex, env.B.Any);
                if (!ReferenceEquals(paramType, env.B.Any))
                {
                    element = new BoundCastExpression(syntax, element, paramType,
                        isSafe: false, paramType);
                }
                args.Add(element);
            }
            var typeArgs = next.GenericParameters.Count > 0
                && next.GenericParameters.All(p => !p.IsVariadic && !p.IsNamedVariadic)
                ? shim.GenericParameters.Cast<SemanticSymbol>().ToList()
                : null;
            var thisExpr = new BoundThisExpression(syntax, member.Owner!);
            BoundBlock body;
            if (shim.ReturnType is { } returnType)
            {
                body = new BoundBlock(syntax, new List<BoundStatement> {
                    new BoundReturnStatement(syntax, new BoundInstanceCallExpression(syntax,
                        thisExpr, next, args, returnType, typeArgs)) });
            }
            else
            {
                body = new BoundBlock(syntax, new List<BoundStatement> {
                    new BoundCallStatement(syntax, next, args, thisExpr, typeArgs) });
            }
            bodies.Add(new BoundFunctionBody(shim, Array.Empty<LocalSymbol>(), body));
        }

        // 成员签名类型中的成员泛型参数替换为目标符号的同位拷贝（shim
        // 与特化/原始体各自独立拷贝成员泛型参数——P2 SynthesizeBodySymbol
        // 同序）；构造类型逐实参递归
        private SemanticSymbol SubstituteMemberGenerics(SemanticSymbol type, MethodSymbol member,
            MethodSymbol target)
        {
            if (type is GenericParameterSymbol gp)
            {
                var index = member.GenericParameters.IndexOf(gp);
                return index >= 0 ? target.GenericParameters[index] : gp;
            }
            if (type is TypeSymbol { ConstructedFrom: not null } constructed)
            {
                var args = new SemanticSymbol[constructed.TypeArguments!.Count];
                for (var i = 0; i < args.Length; i++)
                {
                    args[i] = SubstituteMemberGenerics(constructed.TypeArguments[i], member, target);
                }
                return env.Unit.Symbols.GetConstructedType(constructed.ConstructedFrom!, args);
            }
            return type;
        }

        // 合成字面量（S11b proxy 前奏/shim 用）：BoundLiteralExpression 的
        // Syntax 契约是 LiteralExpressionASTNode（P4b 发射与 BoundDescribe
        // 强转取值）——程序化构造 AST 包装节点，Span 缺省（合成节点不入
        // AST 完整性验证范围）
        private BoundExpression MakeStringLiteral(string value)
        {
            var expr = new LiteralExpressionASTNode();
            expr.AttachLiteral(new StringLiteralASTNode(expr) { Value = value });
            return new BoundLiteralExpression(expr, env.B.String);
        }

        private BoundExpression MakeIntLiteral(long value)
        {
            var expr = new LiteralExpressionASTNode();
            expr.AttachLiteral(new IntLiteralASTNode(expr)
                { Value = value, IntType = IntType.I32 });
            return new BoundLiteralExpression(expr, env.B.Int32);
        }
    }
}
