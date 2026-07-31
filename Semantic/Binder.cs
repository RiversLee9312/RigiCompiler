using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler
{
    // P3 函数体分析（SEMANTIC_ARCHITECTURE §5，SEMANTIC_ROADMAP S5 最小闭环）：
    // 以函数体为独立分析单位（函数间诊断互不阻断），AST 只读，产出 BoundTree。
    //
    // S5 落地范围：字面量定型、局部变量声明与引用（var 类型推断）、
    // 参数引用、全局字段引用、二元/一元 bootstrap intrinsic 运算（BIL §11 精确键）、
    // 无重载直接函数调用（具名实参按形参归位为规范参数序）、new 构造、
    // return（含「所有路径显式返回」检查，SYNTAX §4.1 无隐式返回）、
    // 赋值与 definite assignment 最小版。
    // S7b 落地范围：if 语句（else if 链包成单语句 BoundBlock）、if 表达式
    // （值块：隐式取值/显式 return@标签，值块标签栈解析）、definite assignment
    // 分支合并（before ∪ (setT ∩ setF)）、GuaranteesReturn 双分支 if 升级、
    // 复合赋值（SYNTAX §13.2，10 个基础运算符，表达式值为写回后值）。
    //
    // 值/调用的名字解析查找序：块作用域链 → 参数 → 宿主类型成员
    // （声明类型沿 BaseType 链；当前仅调用查找落地，字段裸名归后续里程碑）
    // → 命名空间链（文件命名空间及父链，顶端即全局命名空间）字段/函数
    // → 通配 import 容器成员；
    // 多段路径 = 容器（命名空间/类型，经 NameResolver）+ 末段成员。
    // 类型引用解析与 P2 共用 NameResolver（本类以 DiagnosticPhase.P3 实例化）。
    //
    // 明确不做（归后续里程碑，遇之一律 P3 诊断而非崩溃）：
    // 其余控制流（loop/switch/try/seq/throw/yield，S7 后续）、成员访问与实例
    // receiver（S8）、重载 ranking 与默认参数填充（S8）、getter/setter（S8）、
    // 泛型使用侧（S9）、enum case（S11）、字符串插值脱糖（S7）、await（S13）、
    // 全局字段初始化器与无标注字段类型推断（其闭包/闸门 P3 复核随之一并，
    // 见 PROGRESS_REPORT 技术债）。
    public static class Binder
    {
        public static IReadOnlyList<BoundFunctionBody> Bind(CompilationUnit unit, DeclarationCollection declarations)
        {
            return new BindSession(unit, declarations).Run();
        }

        // 块作用域：分析期结构，不落 BoundTree
        private sealed class Scope
        {
            private readonly Scope? parent;
            private readonly Dictionary<string, LocalSymbol> locals = new Dictionary<string, LocalSymbol>();

            public Scope(Scope? parent)
            {
                this.parent = parent;
            }

            public bool DeclaresHere(string name)
            {
                return locals.ContainsKey(name);
            }

            public void Declare(LocalSymbol local)
            {
                locals[local.Name] = local;
            }

            public LocalSymbol? Lookup(string name)
            {
                for (var scope = this; scope != null; scope = scope.parent)
                {
                    if (scope.locals.TryGetValue(name, out var local)) return local;
                }
                return null;
            }
        }

        // 调用绑定的中间产物：值位置与语句位置分别落成
        // BoundCallExpression / BoundCallStatement（void 调用）
        private sealed class CallBinding
        {
            public MethodSymbol Method = null!;
            public IReadOnlyList<BoundExpression> Arguments = null!;
            public bool IsVoid;
        }

        private sealed class BindSession
        {
            private readonly CompilationUnit unit;
            private readonly DeclarationCollection declarations;
            private readonly NameResolver names;
            private readonly List<BoundFunctionBody> bodies = new List<BoundFunctionBody>();

            // 当前函数上下文（BindBody 开始时设置；函数体互不嵌套，无重入）
            private MethodSymbol method = null!;
            private FileContext ctx = null!;
            private TypeSymbol? declaringType;
            private readonly List<LocalSymbol> locals = new List<LocalSymbol>();
            // definite assignment 最小版：已赋值局部变量集合（参数恒已赋值）
            private readonly HashSet<LocalSymbol> assigned = new HashSet<LocalSymbol>();
            // 值块标签栈（S7b）：绑定 if 表达式分支体时压入对应施工壳，
            // return@标签 沿栈从内向外查找命中（引用相等即身份）
            private readonly Stack<BoundValueBlock> valueBlocks = new Stack<BoundValueBlock>();

            public BindSession(CompilationUnit unit, DeclarationCollection declarations)
            {
                this.unit = unit;
                this.declarations = declarations;
                names = new NameResolver(unit, DiagnosticPhase.P3);
            }

            private BootstrapSymbols B => unit.Symbols.Bootstrap;

            private void Error(CharRange? span, string message)
            {
                unit.Diagnostics.Error(DiagnosticPhase.P3, span, message);
            }

            public IReadOnlyList<BoundFunctionBody> Run()
            {
                foreach (var file in unit.SourceFiles)
                {
                    var fileCtx = declarations.FileContextOf(file);
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
                        var symbol = declarations.SymbolOf(fn) as MethodSymbol
                            ?? throw new CompilerInternalException("P1 未登记函数符号: " + fn.Name);
                        BindBody(fn, symbol, fileCtx, declaringType);
                        return;
                    case ClassDeclarationASTNode or StructDeclarationASTNode or InterfaceDeclarationASTNode
                        or EnumStructDeclarationASTNode or WrapperDeclarationASTNode:
                        var nested = declarations.SymbolOf(node) as TypeSymbol
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
                method = symbol;
                ctx = fileCtx;
                declaringType = owner;
                locals.Clear();
                assigned.Clear();
                valueBlocks.Clear();    // 函数体互不嵌套，防御性清空

                var body = BindBlock(fn.Body!, new Scope(null));
                // 所有路径显式返回（SYNTAX §4.1 无隐式返回）
                if (symbol.ReturnType != null && !GuaranteesReturn(body))
                {
                    Error(fn.Span, $"Function '{symbol.Name}' must return a value on all code paths");
                }
                bodies.Add(new BoundFunctionBody(symbol, locals.ToList(), body));
            }

            private static bool GuaranteesReturn(BoundBlock block)
            {
                return block.Statements.Count > 0 && block.Statements[^1] switch
                {
                    BoundReturnStatement => true,
                    // S7b：末语句 if 双分支都保证返回 → 保证返回
                    BoundIfStatement ifStatement => ifStatement.FalseBlock != null
                        && GuaranteesReturn(ifStatement.TrueBlock)
                        && GuaranteesReturn(ifStatement.FalseBlock),
                    BoundBlock nested => GuaranteesReturn(nested),
                    // BoundReturnValueStatement 终止的是值块路径，不算函数返回
                    _ => false,
                };
            }

            // ===== 语句绑定 =====

            private BoundBlock BindBlock(CodeBlockASTNode node, Scope? parentScope)
            {
                var scope = new Scope(parentScope);
                var statements = new List<BoundStatement>();
                foreach (var statement in node.Statements)
                {
                    var bound = BindStatement(statement, scope);
                    if (bound != null) statements.Add(bound);
                }
                return new BoundBlock(node, statements);
            }

            private BoundStatement? BindStatement(ASTNode node, Scope scope)
            {
                return node switch
                {
                    VariableDeclarationASTNode decl => BindLocalDeclaration(decl, scope),
                    ExpressionStatementASTNode stmt => BindExpressionStatement(stmt, scope),
                    ReturnStatementASTNode ret => BindReturn(ret, scope),
                    IfStatementASTNode ifStatement => BindIfStatement(ifStatement, scope),
                    CodeBlockASTNode block => BindBlock(block, scope),
                    _ => Unsupported(node, "statement"),
                };
            }

            private BoundStatement? Unsupported(ASTNode node, string kind)
            {
                Error(node.Span, $"P3: {kind} kind not supported yet: {node.GetType().Name}");
                return null;
            }

            private BoundStatement? BindLocalDeclaration(VariableDeclarationASTNode node, Scope scope)
            {
                TypeSymbol? declaredType = null;
                if (node.TypeAnnotation != null)
                {
                    declaredType = ResolveBodyTypeReference(node.TypeAnnotation, node.Span);
                }
                // 初始化表达式先于变量入作用域绑定（var x = x 报未定义而非自引用）
                var init = node.Initializer == null
                    ? null
                    : BindExpression(node.Initializer.Expression, scope, declaredType);
                var type = declaredType ?? init?.Type;
                if (type == null)
                {
                    Error(node.Span, $"Variable '{node.Name}' requires a type annotation or an initializer");
                    return null;
                }
                if (node.IsConst && init == null)
                {
                    Error(node.Span, $"Const '{node.Name}' must have an initializer");
                    return null;
                }
                if (declaredType != null && init != null && !IsAssignable(init.Type, declaredType))
                {
                    Error(node.Initializer!.Span ?? node.Span,
                        $"Cannot assign '{TypeDisplay(init.Type)}' to '{TypeDisplay(declaredType)}'");
                }
                if (scope.DeclaresHere(node.Name))
                {
                    Error(node.Span, $"Duplicate local variable '{node.Name}'");
                    return null;
                }
                var local = new LocalSymbol(node.Name, type, node.IsConst);
                scope.Declare(local);
                locals.Add(local);
                if (init != null) assigned.Add(local);
                return new BoundLocalDeclarationStatement(node, local, init);
            }

            private BoundStatement? BindExpressionStatement(ExpressionStatementASTNode node, Scope scope)
            {
                if (node.AssignValue != null)
                {
                    return BindAssignment(node, scope);
                }
                // void 调用落成 BoundCallStatement，非 void 调用仍是表达式语句
                if (node.Expression.Expression is PathExpressionASTNode path
                    && TryGetCallForm(path, out var calleeSegments, out var callArguments))
                {
                    var binding = BindCall(node, calleeSegments, callArguments!, scope);
                    if (binding == null) return null;
                    if (binding.IsVoid)
                    {
                        return new BoundCallStatement(node, binding.Method, binding.Arguments);
                    }
                    return new BoundExpressionStatement(node, new BoundCallExpression(path,
                        binding.Method, binding.Arguments, (TypeSymbol)binding.Method.ReturnType!));
                }
                var expr = BindExpression(node.Expression.Expression, scope);
                return expr == null ? null : new BoundExpressionStatement(node, expr);
            }

            private BoundStatement? BindAssignment(ExpressionStatementASTNode node, Scope scope)
            {
                // 赋值目标是定义而非「使用」：符号引用不经 unassigned 检查
                var target = node.Expression.Expression is PathExpressionASTNode targetPath
                    ? BindPath(targetPath, scope, forAssignment: true)
                    : BindExpression(node.Expression.Expression, scope);
                var value = BindExpression(node.AssignValue!.Expression, scope, target?.Type);
                if (target == null || value == null) return null;
                switch (target)
                {
                    case BoundValueReferenceExpression { Symbol: LocalSymbol local }:
                        if (local.IsConst)
                        {
                            Error(node.Span, $"Cannot assign to const '{local.Name}'");
                            return null;
                        }
                        assigned.Add(local);
                        break;
                    case BoundValueReferenceExpression { Symbol: ParameterSymbol }:
                        break;
                    case BoundFieldReferenceExpression:
                        // 全局字段 const 判定需声明 AST（P3 技术债：反向映射缺失）
                        break;
                    default:
                        Error(node.Expression.Span ?? node.Span, "Assignment target must be a variable");
                        return null;
                }
                if (!IsAssignable(value.Type, target.Type))
                {
                    Error(node.AssignValue.Span ?? node.Span,
                        $"Cannot assign '{TypeDisplay(value.Type)}' to '{TypeDisplay(target.Type)}'");
                }
                return new BoundAssignmentStatement(node, target, value);
            }

            private BoundStatement? BindReturn(ReturnStatementASTNode node, Scope scope)
            {
                // return@标签（SYNTAX §6.1）：终止标签对应值块的路径并把值作为该块
                // 产值；沿值块标签栈从内向外查找，未命中即未定义标签
                if (node.Label != null)
                {
                    BoundValueBlock? target = null;
                    foreach (var valueBlock in valueBlocks)
                    {
                        if (valueBlock.Label == node.Label)
                        {
                            target = valueBlock;
                            break;
                        }
                    }
                    if (target == null)
                    {
                        Error(node.Span, $"Undefined value block label: '{node.Label}'");
                        return null;
                    }
                    if (node.Value == null)
                    {
                        Error(node.Span, $"return@{node.Label} requires a value");
                        return null;
                    }
                    var labelValue = BindExpression(node.Value.Expression, scope);
                    if (labelValue == null) return null;
                    return new BoundReturnValueStatement(node, target, labelValue);
                }
                if (node.Value == null)
                {
                    if (method.ReturnType != null)
                    {
                        Error(node.Span, $"Function '{method.Name}' must return a value");
                        return null;
                    }
                    return new BoundReturnStatement(node, null);
                }
                var value = BindExpression(node.Value.Expression, scope, method.ReturnType as TypeSymbol);
                if (value == null) return null;
                if (method.ReturnType == null)
                {
                    Error(node.Value.Span ?? node.Span, $"Void function '{method.Name}' cannot return a value");
                    return null;
                }
                // 返回类型为泛型参数时兼容判定归 S9
                if (method.ReturnType is TypeSymbol returnType && !IsAssignable(value.Type, returnType))
                {
                    Error(node.Value.Span ?? node.Span,
                        $"Cannot return '{TypeDisplay(value.Type)}' from function returning '{TypeDisplay(returnType)}'");
                }
                return new BoundReturnStatement(node, value);
            }

            // ===== if 语句 / if 表达式 / 值块（S7b，SYNTAX §7.1/§6.1）=====

            // if 语句：else if 链包成单语句 BoundBlock（Bound 层双分支形态）。
            // definite assignment 分支合并：before ∪ (setT ∩ setF)；无 else 合并为 before
            private BoundStatement? BindIfStatement(IfStatementASTNode node, Scope scope)
            {
                var condition = BindExpression(node.Condition.Expression, scope);
                CheckBoolCondition(node.Condition, node.Span, condition);
                var before = new HashSet<LocalSymbol>(assigned);
                var trueBlock = BindBlock(node.ThenBlock, scope);
                var trueAssigned = new HashSet<LocalSymbol>(assigned);
                BoundBlock? falseBlock = null;
                HashSet<LocalSymbol>? falseAssigned = null;
                RestoreAssigned(before);
                switch (node.ElseBranch)
                {
                    case null:
                        break;
                    case CodeBlockASTNode elseBlock:
                        falseBlock = BindBlock(elseBlock, scope);
                        falseAssigned = new HashSet<LocalSymbol>(assigned);
                        break;
                    case IfStatementASTNode elseIf:
                        // else if 链：递归绑定，包成单语句 BoundBlock
                        var nested = BindIfStatement(elseIf, scope);
                        var statements = new List<BoundStatement>();
                        if (nested != null) statements.Add(nested);
                        falseBlock = new BoundBlock(elseIf, statements);
                        falseAssigned = new HashSet<LocalSymbol>(assigned);
                        break;
                    default:
                        throw new CompilerInternalException(
                            "未知 else 分支节点: " + node.ElseBranch.GetType().Name);
                }
                // 合并：双分支取交集并回 before；无 else 保守恢复 before
                if (falseAssigned == null)
                {
                    RestoreAssigned(before);
                }
                else
                {
                    trueAssigned.IntersectWith(falseAssigned);
                    trueAssigned.UnionWith(before);
                    RestoreAssigned(trueAssigned);
                }
                if (condition == null) return null;
                return new BoundIfStatement(node, condition, trueBlock, falseBlock);
            }

            // if 表达式：必须有 else（前端保证）；两分支各绑一个值块（标签同源——
            // if 表达式的 named 标签或缺省 "_"），产值类型统一（符号 ==；ErrorType
            // 毒化静默），纯穿透分支（ValueType null）不参与统一；definite
            // assignment 合并规则同 if 语句
            private BoundExpression? BindIfExpression(IfExpressionASTNode node, Scope scope)
            {
                var condition = BindExpression(node.Condition.Expression, scope);
                CheckBoolCondition(node.Condition, node.Span, condition);
                var label = node.Label ?? "_";
                var before = new HashSet<LocalSymbol>(assigned);
                var trueBranch = BindValueBlock(node.ThenBody, label, scope);
                var trueAssigned = new HashSet<LocalSymbol>(assigned);
                RestoreAssigned(before);
                var falseBranch = BindValueBlock(node.ElseBody, label, scope);
                var falseAssigned = new HashSet<LocalSymbol>(assigned);
                trueAssigned.IntersectWith(falseAssigned);
                trueAssigned.UnionWith(before);
                RestoreAssigned(trueAssigned);
                if (condition == null) return null;
                // 产值类型统一：纯穿透分支（null）不参与；两分支都穿透即无产值
                var type = trueBranch.ValueType ?? falseBranch.ValueType;
                if (type == null)
                {
                    Error(node.Span, "if expression must produce a value " +
                        "(at least one branch must return@ a value)");
                    return null;
                }
                if (trueBranch.ValueType != null && falseBranch.ValueType != null
                    && !ReferenceEquals(trueBranch.ValueType, falseBranch.ValueType)
                    && trueBranch.ValueType is not ErrorTypeSymbol
                    && falseBranch.ValueType is not ErrorTypeSymbol)
                {
                    Error(node.Span,
                        $"if expression branches produce different types " +
                        $"('{TypeDisplay(trueBranch.ValueType)}' and " +
                        $"'{TypeDisplay(falseBranch.ValueType)}')");
                    return null;
                }
                return new BoundIfExpression(node, condition, trueBranch, falseBranch, type);
            }

            // if 条件必须 bool（语句/表达式两形态同规则；ErrorType 毒化静默）
            private void CheckBoolCondition(ExpressionRootASTNode conditionRoot,
                CharRange? fallbackSpan, BoundExpression? condition)
            {
                if (condition != null && condition.Type is not ErrorTypeSymbol
                    && !ReferenceEquals(condition.Type, B.Bool))
                {
                    Error(conditionRoot.Span ?? fallbackSpan,
                        $"if condition must be bool (got '{TypeDisplay(condition.Type)}')");
                }
            }

            // 值块绑定（if 表达式分支体，SYNTAX §6.1/§7.1）：
            // - 施工壳先于分支体绑定创建并压入标签栈（分支体内的 return@标签 经栈命中），
            //   分支体绑完后回填 Block/IsImplicitValue/ValueType；
            // - 语法上恰好一条纯表达式语句（赋值语句不算）→ 隐式取值，
            //   ValueType = 该表达式类型；
            // - 否则所有执行路径必须显式 return@（GuaranteesValueReturn 检查，
            //   穿透终止也算路径终止），ValueType = 命中本块的 return@ 值类型
            //   统一结果；无本块产值（纯穿透）→ ValueType = null
            private BoundValueBlock BindValueBlock(CodeBlockASTNode node, string label, Scope scope)
            {
                var shell = new BoundValueBlock(node, label);
                valueBlocks.Push(shell);
                BoundBlock block;
                try
                {
                    block = BindBlock(node, scope);
                }
                finally
                {
                    valueBlocks.Pop();
                }
                shell.Block = block;
                // M33 判定：语法上恰好一条纯表达式语句
                if (node.Statements.Count == 1
                    && node.Statements[0] is ExpressionStatementASTNode { AssignValue: null })
                {
                    shell.IsImplicitValue = true;
                    // 绑定失败（产物缺失）时诊断已发，静默留 null ValueType；
                    // void 调用落成 BoundCallStatement——无值可取
                    if (block.Statements.Count == 1
                        && block.Statements[0] is BoundExpressionStatement expressionStatement)
                    {
                        shell.ValueType = expressionStatement.Expression.Type;
                    }
                    else if (block.Statements.Count == 1
                        && block.Statements[0] is BoundCallStatement)
                    {
                        Error(node.Span, "if expression branch must produce a value " +
                            "(a void call has no result)");
                    }
                    return shell;
                }
                if (!GuaranteesValueReturn(block))
                {
                    Error(node.Span, "All code paths of an if expression branch must " +
                        "explicitly return@ a value");
                }
                shell.ValueType = CollectBranchValueType(block, shell);
                return shell;
            }

            // 「所有执行路径显式 return@」判定：末语句是 BoundReturnValueStatement
            // （命中任意值块，穿透终止也算路径终止）→ true；末语句是 BoundIfStatement
            // 且双分支 GuaranteesValueReturn → true；末语句是嵌套 BoundBlock → 递归；
            // 其余 false（裸 return 终止函数路径但不作为值块产值收尾，规则从简）
            private static bool GuaranteesValueReturn(BoundBlock block)
            {
                return block.Statements.Count > 0 && block.Statements[^1] switch
                {
                    BoundReturnValueStatement => true,
                    BoundIfStatement ifStatement => ifStatement.FalseBlock != null
                        && GuaranteesValueReturn(ifStatement.TrueBlock)
                        && GuaranteesValueReturn(ifStatement.FalseBlock),
                    BoundBlock nested => GuaranteesValueReturn(nested),
                    _ => false,
                };
            }

            // 收集分支块内命中本块的 return@ 值类型（递归嵌套块与 if 分支）；
            // 全部须类型一致（符号 ==，驻留保证；ErrorType 毒化静默跳过），
            // 不一致诊断并以首个为准；无命中（纯穿透终止）→ null
            private TypeSymbol? CollectBranchValueType(BoundBlock block, BoundValueBlock shell)
            {
                TypeSymbol? collected = null;
                foreach (var statement in EnumerateStatements(block))
                {
                    if (statement is BoundReturnValueStatement returnValue
                        && ReferenceEquals(returnValue.Target, shell)
                        && returnValue.Value.Type is not ErrorTypeSymbol)
                    {
                        if (collected == null)
                        {
                            collected = returnValue.Value.Type;
                        }
                        else if (!ReferenceEquals(collected, returnValue.Value.Type))
                        {
                            Error(statement.Syntax.Span,
                                $"if expression branch produces different types " +
                                $"('{TypeDisplay(collected)}' and " +
                                $"'{TypeDisplay(returnValue.Value.Type)}')");
                        }
                    }
                }
                return collected;
            }

            // 块内语句的平铺枚举（递归嵌套 BoundBlock 与 BoundIfStatement 两分支）
            private static IEnumerable<BoundStatement> EnumerateStatements(BoundBlock block)
            {
                foreach (var statement in block.Statements)
                {
                    yield return statement;
                    switch (statement)
                    {
                        case BoundBlock nested:
                            foreach (var s in EnumerateStatements(nested)) yield return s;
                            break;
                        case BoundIfStatement ifStatement:
                            foreach (var s in EnumerateStatements(ifStatement.TrueBlock))
                                yield return s;
                            if (ifStatement.FalseBlock != null)
                            {
                                foreach (var s in EnumerateStatements(ifStatement.FalseBlock))
                                    yield return s;
                            }
                            break;
                    }
                }
            }

            // definite assignment 快照恢复（分支合并用）
            private void RestoreAssigned(HashSet<LocalSymbol> snapshot)
            {
                assigned.Clear();
                assigned.UnionWith(snapshot);
            }

            // ===== 表达式绑定（null 返回 = 已诊断失败，调用方跳过）=====

            private BoundExpression? BindExpression(ASTNode node, Scope scope, TypeSymbol? expectedType = null)
            {
                return node switch
                {
                    LiteralExpressionASTNode literal => BindLiteral(literal, expectedType),
                    PathExpressionASTNode path => BindPath(path, scope),
                    BinaryExpressionASTNode binary => BindBinary(binary, scope),
                    UnaryExpressionASTNode unary => BindUnary(unary, scope),
                    NewExpressionASTNode newExpr => BindNew(newExpr, scope),
                    IfExpressionASTNode ifExpression => BindIfExpression(ifExpression, scope),
                    CompoundAssignmentExpressionASTNode compound =>
                        BindCompoundAssignment(compound, scope),
                    // 括号是透明分组（Latte 无优先级，括号只定结构），不落 bound 节点
                    GroupExpressionASTNode group =>
                        BindExpression(group.InnerExpression.Expression, scope, expectedType),
                    ExpressionRootASTNode => throw new CompilerInternalException(
                        "ExpressionRootASTNode 应在调用方解包"),
                    _ => UnsupportedExpression(node),
                };
            }

            private BoundExpression? UnsupportedExpression(ASTNode node)
            {
                Error(node.Span, $"P3: expression kind not supported yet: {node.GetType().Name}");
                return null;
            }

            private BoundExpression BindLiteral(LiteralExpressionASTNode node, TypeSymbol? expectedType)
            {
                var literal = node.Literal;
                TypeSymbol type = literal switch
                {
                    IntLiteralASTNode i => i.IntType switch
                    {
                        IntType.I8 => B.Int8,
                        IntType.I16 => B.Int16,
                        IntType.I32 => B.Int32,
                        IntType.I64 => B.Int64,
                        IntType.U8 => B.UInt8,
                        IntType.U16 => B.UInt16,
                        IntType.U32 => B.UInt32,
                        IntType.U64 => B.UInt64,
                        _ => throw new CompilerInternalException("未知 IntType: " + i.IntType),
                    },
                    FloatLiteralASTNode f => f.IsFloat ? B.Float : B.Double,
                    StringLiteralASTNode => B.String,
                    CharLiteralASTNode => B.Char,
                    BoolLiteralASTNode => B.Bool,
                    // null 的类型由上下文给出（var x: T? = null；实参/return/赋值同）
                    NullLiteralASTNode => expectedType ?? NullLiteralError(node),
                    _ => throw new CompilerInternalException("未知字面量节点: " + literal.GetType().Name),
                };
                if (literal is StringLiteralASTNode { HasInterpolation: true })
                {
                    Error(node.Span, "P3: string interpolation is not supported yet (S7)");
                }
                return new BoundLiteralExpression(node, type);
            }

            private TypeSymbol NullLiteralError(ASTNode node)
            {
                Error(node.Span, "null requires a nullable type context");
                return unit.Symbols.ErrorType;
            }

            // 路径表达式（M42 统一形态）的值位置绑定。
            // 形态分派：纯调用形态 → 直接调用；纯值路径（无后缀）→ 局部/参数/
            // 字段/容器成员；其余（实例链/索引/安全访问/wrapper）报归口诊断。
            // forAssignment：赋值目标绑定（定义而非使用），跳过 unassigned 检查
            private BoundExpression? BindPath(PathExpressionASTNode node, Scope scope,
                bool forAssignment = false)
            {
                // 表达式底座（(a).b / foo().b / 字面量.foo）：实例成员链归 S8
                if (node.Head.Expression != null)
                {
                    Error(node.Span, "P3: instance member access is not supported yet (S8)");
                    return null;
                }
                // 泛型实参（首段/段）：使用侧归 S9
                if (node.Head.GenericArguments.Count > 0
                    || node.Segments.Any(s => s.GenericArguments.Count > 0))
                {
                    Error(node.Span, "P3: generic type arguments are not supported yet (S9)");
                    return null;
                }
                // 纯调用形态 → 直接调用
                if (TryGetCallForm(node, out var calleeSegments, out var callArguments))
                {
                    var binding = BindCall(node, calleeSegments, callArguments!, scope);
                    if (binding == null) return null;
                    if (binding.IsVoid)
                    {
                        Error(node.Span, $"Method '{binding.Method.Name}' has no result (void) " +
                            "and cannot be used as a value");
                        return null;
                    }
                    return new BoundCallExpression(node, binding.Method, binding.Arguments,
                        (TypeSymbol)binding.Method.ReturnType!);
                }
                // 非调用形态的后缀与特殊连接符：逐一归口诊断
                if (node.Head.Suffixes.Any(s => s.Kind == PathSuffixKind.Index)
                    || node.Segments.Any(s => s.Suffixes.Any(x => x.Kind == PathSuffixKind.Index)))
                {
                    Error(node.Span, "P3: index access is not supported yet (S8)");
                    return null;
                }
                if (node.Segments.Any(s => s.Connector == PathConnector.SafeDot))
                {
                    Error(node.Span, "P3: safe member access is not supported yet (S7)");
                    return null;
                }
                if (node.Segments.Any(s => s.Connector == PathConnector.Colon))
                {
                    Error(node.Span, "P3: wrapper access is not supported yet (S11)");
                    return null;
                }
                if (node.Head.Suffixes.Count > 0 || node.Segments.Any(s => s.Suffixes.Count > 0))
                {
                    Error(node.Span, "P3: instance member access is not supported yet (S8)");
                    return null;
                }
                // 纯值路径：单段查找序 局部 → 参数 → 命名空间链字段 → 通配 import；
                // 多段 = 容器 + 末段成员
                if (node.Segments.Count == 0)
                {
                    var name = node.Head.Name!;
                    var local = scope.Lookup(name);
                    if (local != null)
                    {
                        if (!forAssignment && !assigned.Contains(local))
                        {
                            Error(node.Span, $"Use of unassigned local variable '{name}'");
                        }
                        return new BoundValueReferenceExpression(node, local, local.Type);
                    }
                    var parameter = method.Parameters.FirstOrDefault(p => p.Name == name);
                    if (parameter != null)
                    {
                        if (parameter.Type is not TypeSymbol paramType)
                        {
                            Error(node.Span, "P3: generic type parameters are not supported yet (S9)");
                            return null;
                        }
                        return new BoundValueReferenceExpression(node, parameter, paramType);
                    }
                    var field = FindField(name);
                    if (field != null)
                    {
                        return BindFieldReference(node, field);
                    }
                    Error(node.Span, $"Undefined name: '{name}'");
                    return null;
                }
                // 多段：首段命中局部/参数 → 实例成员路径（receiver 归 S8）；
                // 否则前 N-1 段解析为容器，末段查成员
                var segments = PathSegmentNames(node);
                if (scope.Lookup(segments[0]) != null
                    || method.Parameters.Any(p => p.Name == segments[0]))
                {
                    Error(node.Span, "P3: instance member access is not supported yet (S8)");
                    return null;
                }
                var container = ResolveContainer(segments, node.Span);
                if (container == null) return null;
                var member = FindMember(container, segments[^1]);
                var pathText = string.Join(".", segments);
                return member switch
                {
                    FieldSymbol field => BindFieldReference(node, field),
                    MethodSymbol => ErrorAndNull(node.Span,
                        $"Method '{pathText}' cannot be used as a value"),
                    null => ErrorAndNull(node.Span, $"Undefined name: '{pathText}'"),
                    _ => ErrorAndNull(node.Span, $"'{pathText}' cannot be used as a value"),
                };
            }

            // 路径的段名序列（首段名 + 各段名）；调用方保证无表达式底座
            private static List<string> PathSegmentNames(PathExpressionASTNode node)
            {
                var segments = new List<string> { node.Head.Name! };
                segments.AddRange(node.Segments.Select(s => s.Name));
                return segments;
            }

            // 纯调用形态判定：符号头 + 全 Dot 段（无中间后缀）+
            // 整条链恰好一个 Call 后缀（在首段或某段之后）。
            // 泛型/表达式底座已在此前各自归口，不在此判定内
            private static bool TryGetCallForm(PathExpressionASTNode node,
                out List<string> calleeSegments, out List<ArgumentASTNode>? callArguments)
            {
                calleeSegments = new List<string>();
                callArguments = null;
                if (node.Head.Name == null) return false;
                calleeSegments.Add(node.Head.Name);
                if (node.Head.Suffixes.Count > 1) return false;
                if (node.Head.Suffixes.Count == 1)
                {
                    if (node.Head.Suffixes[0].Kind != PathSuffixKind.Call) return false;
                    callArguments = node.Head.Suffixes[0].Arguments;
                }
                foreach (var segment in node.Segments)
                {
                    if (segment.Connector != PathConnector.Dot) return false;
                    if (segment.Suffixes.Count > 1) return false;
                    calleeSegments.Add(segment.Name);
                    if (segment.Suffixes.Count == 1)
                    {
                        if (segment.Suffixes[0].Kind != PathSuffixKind.Call || callArguments != null)
                        {
                            return false;
                        }
                        callArguments = segment.Suffixes[0].Arguments;
                    }
                }
                return callArguments != null;
            }

            private BoundExpression? BindFieldReference(ASTNode node, FieldSymbol field)
            {
                if (field.FieldType == null)
                {
                    Error(node.Span, $"P3: field '{field.Name}' has no type annotation " +
                        "(field type inference is not supported yet)");
                    return null;
                }
                if (field.FieldType is not TypeSymbol fieldType)
                {
                    Error(node.Span, "P3: generic type parameters are not supported yet (S9)");
                    return null;
                }
                if (field.Owner != null && !field.IsStatic)
                {
                    Error(node.Span, $"P3: instance field '{field.Name}' requires a receiver (S8)");
                    return null;
                }
                return new BoundFieldReferenceExpression(node, field, fieldType);
            }

            private BoundExpression? BindBinary(BinaryExpressionASTNode node, Scope scope)
            {
                var op = MapBinaryOperator(node.Operator);
                var left = BindExpression(node.Left.Expression, scope);
                var right = BindExpression(node.Right.Expression, scope);
                if (left == null || right == null) return null;
                // 毒化静默：操作数已失败时不再报次生错误
                if (left.Type is ErrorTypeSymbol || right.Type is ErrorTypeSymbol) return null;
                if (!ReferenceEquals(left.Type, right.Type))
                {
                    Error(node.Span, $"Binary operator '{node.Operator}' requires operands of the same type " +
                        $"(got '{TypeDisplay(left.Type)}' and '{TypeDisplay(right.Type)}')");
                    return null;
                }
                if (!left.Type.IntrinsicOps.Contains(op))
                {
                    Error(node.Span, $"Operator '{node.Operator}' is not defined for type " +
                        $"'{TypeDisplay(left.Type)}'");
                    return null;
                }
                // BIL §11：比较结果 bool；算术/位/逻辑结果同操作数类型
                var resultType = IsComparison(op) ? B.Bool : left.Type;
                return new BoundBinaryExpression(node, op, left, right, resultType);
            }

            private BoundExpression? BindUnary(UnaryExpressionASTNode node, Scope scope)
            {
                BilIntrinsicOp op;
                switch (node.Operator)
                {
                    case "await":
                        Error(node.Span, "P3: await is not supported yet (S13)");
                        return null;
                    case "-": op = BilIntrinsicOp.Opposite; break;
                    case "not": op = BilIntrinsicOp.Not; break;
                    case "!": op = BilIntrinsicOp.BinNot; break;
                    case "+":
                        // 一元正号：SYNTAX §13.2 无此运算符，按恒等处理
                        return BindExpression(node.Operand.Expression, scope);
                    default:
                        throw new CompilerInternalException("未知一元运算符: " + node.Operator);
                }
                var operand = BindExpression(node.Operand.Expression, scope);
                if (operand == null) return null;
                if (operand.Type is ErrorTypeSymbol) return null;
                if (!operand.Type.IntrinsicOps.Contains(op))
                {
                    Error(node.Span, $"Operator '{node.Operator}' is not defined for type " +
                        $"'{TypeDisplay(operand.Type)}'");
                    return null;
                }
                return new BoundUnaryExpression(node, op, operand, operand.Type);
            }

            // 复合赋值（SYNTAX §13.2）：a op= b 即 a = a op b 的语义糖，表达式值
            // 为写回后值。Target 规则同赋值（局部/参数/字段 place），但读前须已赋值
            // （读语义——普通路径绑定的 unassigned 检查，不做 forAssignment 特免）；
            // Op 复用二元映射（10 个基础运算符，Parser 保证不含 and/or）；
            // 类型一致与 intrinsic 存在检查同 BindBinary；Type = Target 类型；
            // 赋值后 Target 标记 assigned
            private BoundExpression? BindCompoundAssignment(
                CompoundAssignmentExpressionASTNode node, Scope scope)
            {
                var op = MapBinaryOperator(node.Operator);
                var target = BindExpression(node.Target.Expression, scope);
                var value = BindExpression(node.Value.Expression, scope, target?.Type);
                if (target == null || value == null) return null;
                switch (target)
                {
                    case BoundValueReferenceExpression { Symbol: LocalSymbol local }:
                        if (local.IsConst)
                        {
                            Error(node.Span, $"Cannot assign to const '{local.Name}'");
                            return null;
                        }
                        assigned.Add(local);
                        break;
                    case BoundValueReferenceExpression { Symbol: ParameterSymbol }:
                    case BoundFieldReferenceExpression:
                        // 参数与全局字段：同赋值的放行规则
                        break;
                    default:
                        Error(node.Target.Span ?? node.Span,
                            "Assignment target must be a variable");
                        return null;
                }
                // 毒化静默：任一侧已失败时不再报次生错误
                if (target.Type is ErrorTypeSymbol || value.Type is ErrorTypeSymbol) return null;
                if (!ReferenceEquals(target.Type, value.Type))
                {
                    Error(node.Span, $"Compound assignment requires operands of the same type " +
                        $"(got '{TypeDisplay(target.Type)}' and '{TypeDisplay(value.Type)}')");
                    return null;
                }
                if (!target.Type.IntrinsicOps.Contains(op))
                {
                    Error(node.Span, $"Operator '{node.Operator}=' is not defined for type " +
                        $"'{TypeDisplay(target.Type)}'");
                    return null;
                }
                return new BoundCompoundAssignmentExpression(node, target, op, value, target.Type);
            }

            // ===== 调用与构造 =====

            private CallBinding? BindCall(ASTNode node, List<string> calleeSegments,
                List<ArgumentASTNode> arguments, Scope scope)
            {
                var method = BindCallee(node, calleeSegments, arguments.Count, scope);
                if (method == null) return null;
                var boundArguments = BindArguments(method, arguments, scope, node.Span);
                if (boundArguments == null) return null;
                return new CallBinding
                {
                    Method = method,
                    Arguments = boundArguments,
                    IsVoid = method.ReturnType == null,
                };
            }

            private MethodSymbol? BindCallee(ASTNode node, List<string> calleeSegments,
                int argumentCount, Scope scope)
            {
                var pathText = string.Join(".", calleeSegments);
                List<MethodSymbol> candidates;
                if (calleeSegments.Count == 1)
                {
                    candidates = FindMethods(calleeSegments[0]);
                }
                else
                {
                    // 首段命中局部/参数 → 实例成员路径调用（receiver 归 S8）
                    if (scope.Lookup(calleeSegments[0]) != null
                        || method.Parameters.Any(p => p.Name == calleeSegments[0]))
                    {
                        Error(node.Span, "P3: instance member access is not supported yet (S8)");
                        return null;
                    }
                    var container = ResolveContainer(calleeSegments, node.Span);
                    if (container == null) return null;
                    candidates = container switch
                    {
                        NamespaceSymbol ns => ns.Methods.Where(m => m.Name == calleeSegments[^1]).ToList(),
                        TypeSymbol t => t.Methods.Where(m => m.Name == calleeSegments[^1]).ToList(),
                        _ => new List<MethodSymbol>(),
                    };
                    if (candidates.Count == 0 && FindMember(container, calleeSegments[^1]) != null)
                    {
                        Error(node.Span, $"'{pathText}' is not a method");
                        return null;
                    }
                }
                if (candidates.Count == 0)
                {
                    Error(node.Span, $"Undefined function: '{pathText}'");
                    return null;
                }
                // 泛型方法调用（推断/显式实参）归 S9
                candidates = candidates.Where(m => m.GenericParameters.Count == 0).ToList();
                if (candidates.Count == 0)
                {
                    Error(node.Span, "P3: generic calls are not supported yet (S9)");
                    return null;
                }
                // 无重载直接调用：按实参个数唯一匹配
                var matched = candidates.Where(m => m.Parameters.Count == argumentCount).ToList();
                if (matched.Count == 0)
                {
                    var counts = string.Join("/", candidates.Select(m => m.Parameters.Count).Distinct());
                    Error(node.Span, $"Function '{candidates[0].Name}' expects {counts} argument(s), " +
                        $"got {argumentCount}");
                    return null;
                }
                if (matched.Count > 1)
                {
                    Error(node.Span, $"P3: overload resolution for '{candidates[0].Name}' " +
                        "is not supported yet (S8)");
                    return null;
                }
                var selected = matched[0];
                if (selected.Owner != null && !selected.IsStatic)
                {
                    Error(node.Span, $"P3: instance method '{selected.Name}' requires a receiver (S8)");
                    return null;
                }
                return selected;
            }

            // 实参绑定：位置实参按序、具名实参按形参名归位——产物已是规范参数序
            // （ARCHITECTURE §2「BoundCall 已是规范参数序」；默认参数填充归 S8）
            private List<BoundExpression>? BindArguments(MethodSymbol target,
                List<ArgumentASTNode> arguments, Scope scope, CharRange? callSpan)
            {
                var parameters = target.Parameters;
                var bound = new BoundExpression?[parameters.Count];
                var failed = false;
                var nextPositional = 0;
                foreach (var argument in arguments)
                {
                    int index;
                    if (argument.Name == null)
                    {
                        if (nextPositional >= parameters.Count)
                        {
                            Error(argument.Span, $"Too many arguments for '{target.Name}'");
                            failed = true;
                            continue;
                        }
                        index = nextPositional++;
                    }
                    else
                    {
                        index = -1;
                        for (int i = 0; i < parameters.Count; i++)
                        {
                            if (parameters[i].Name == argument.Name) { index = i; break; }
                        }
                        if (index < 0)
                        {
                            Error(argument.Span, $"'{target.Name}' has no parameter named '{argument.Name}'");
                            failed = true;
                            continue;
                        }
                        if (bound[index] != null)
                        {
                            Error(argument.Span, $"Duplicate argument for parameter '{argument.Name}'");
                            failed = true;
                            continue;
                        }
                    }
                    var expected = parameters[index].Type as TypeSymbol;
                    var value = BindExpression(argument.Value.Expression, scope, expected);
                    if (value == null)
                    {
                        failed = true;
                        continue;
                    }
                    // 形参类型为泛型参数时兼容判定归 S9
                    if (expected != null && !IsAssignable(value.Type, expected))
                    {
                        Error(argument.Value.Span ?? argument.Span,
                            $"Cannot pass '{TypeDisplay(value.Type)}' as '{TypeDisplay(expected)}'");
                        failed = true;
                        continue;
                    }
                    bound[index] = value;
                }
                for (int i = 0; i < parameters.Count; i++)
                {
                    if (bound[i] == null)
                    {
                        Error(callSpan, $"Missing argument for parameter '{parameters[i].Name}'");
                        failed = true;
                    }
                }
                return failed ? null : bound.Select(b => b!).ToList();
            }

            private BoundExpression? BindNew(NewExpressionASTNode node, Scope scope)
            {
                var type = ResolveBodyTypeReference(node.Type, node.Type.Span ?? node.Span);
                if (type is ErrorTypeSymbol) return null;
                if (type == null) return null;  // 泛型参数（已诊断）
                if (type.ConstructedFrom == null && type.GenericParameters.Count > 0)
                {
                    Error(node.Type.Span ?? node.Span,
                        $"Cannot construct generic type definition '{type.Name}'");
                    return null;
                }
                switch (type.Kind)
                {
                    case TypeKind.Class:
                    case TypeKind.Struct:
                        break;
                    case TypeKind.Interface:
                        Error(node.Type.Span ?? node.Span, $"Cannot construct interface '{type.Name}'");
                        return null;
                    case TypeKind.EnumStruct:
                        Error(node.Type.Span ?? node.Span,
                            "P3: enum case construction is not supported yet (S11)");
                        return null;
                    default:
                        Error(node.Type.Span ?? node.Span,
                            $"Cannot construct wrapper '{type.Name}' (created by the compiler)");
                        return null;
                }
                var inits = type.Methods.Where(m => m.Kind == MethodKind.Init
                    && m.GenericParameters.Count == 0).ToList();
                if (inits.Count == 0)
                {
                    // 无显式 init 的零参构造（默认构造规则待规范明确，见技术债）
                    if (node.Arguments.Count == 0)
                    {
                        return new BoundNewExpression(node, type, null,
                            new List<BoundExpression>());
                    }
                    Error(node.Span, $"Type '{type.Name}' has no constructor");
                    return null;
                }
                var matched = inits.Where(m => m.Parameters.Count == node.Arguments.Count).ToList();
                if (matched.Count == 0)
                {
                    var counts = string.Join("/", inits.Select(m => m.Parameters.Count).Distinct());
                    Error(node.Span, $"Constructor of '{type.Name}' expects {counts} argument(s), " +
                        $"got {node.Arguments.Count}");
                    return null;
                }
                if (matched.Count > 1)
                {
                    Error(node.Span, $"P3: overload resolution for constructor of '{type.Name}' " +
                        "is not supported yet (S8)");
                    return null;
                }
                var arguments = BindArguments(matched[0], node.Arguments, scope, node.Span);
                if (arguments == null) return null;
                return new BoundNewExpression(node, type, matched[0], arguments);
            }

            // ===== 名字解析辅助 =====

            // 函数体内类型引用（局部声明标注、new）：与 P2 共用 NameResolver；
            // 泛型参数命中归 S9（诊断并返回 null），ErrorType 毒化照常返回
            private TypeSymbol? ResolveBodyTypeReference(TypeReferenceASTNode typeRef, CharRange? span)
            {
                var resolved = names.ResolveTypeReference(typeRef, ctx, declaringType, method, span);
                if (resolved is TypeSymbol type) return type;
                Error(span, "P3: generic type parameters are not supported yet (S9)");
                return null;
            }

            // 多段路径的前 N-1 段解析为容器（命名空间/类型）
            // 多段路径的前 N-1 段解析为容器（命名空间/类型）：
            // 段名序列还原为语法侧 Symbol 喂 NameResolver（保持 P2 同款诊断消息）
            private SemanticSymbol? ResolveContainer(IReadOnlyList<string> segments, CharRange? span)
            {
                var head = new Symbol();
                for (int i = 0; i < segments.Count - 1; i++)
                {
                    head.elements.Add(new SymbolElement { name = segments[i] });
                }
                var container = names.ResolveSymbolPath(head, ctx, declaringType, method,
                    allowImports: true, reportErrors: true, span: span);
                return container is ErrorTypeSymbol ? null : container;
            }

            private static SemanticSymbol? FindMember(SemanticSymbol container, string name)
            {
                return container switch
                {
                    NamespaceSymbol ns => (SemanticSymbol?)ns.Fields.FirstOrDefault(f => f.Name == name)
                        ?? ns.Methods.FirstOrDefault(m => m.Name == name)
                        ?? (SemanticSymbol?)ns.Types.FirstOrDefault(t => t.Name == name)
                        ?? ns.ChildNamespaces.FirstOrDefault(n => n.Name == name),
                    TypeSymbol t => (SemanticSymbol?)t.Fields.FirstOrDefault(f => f.Name == name)
                        ?? (SemanticSymbol?)t.Methods.FirstOrDefault(m => m.Name == name)
                        ?? t.NestedTypes.FirstOrDefault(n => n.Name == name),
                    _ => null,
                };
            }

            // 命名空间链字段查找（文件命名空间及父链，顶端即全局命名空间）
            // → 通配 import 容器字段
            private FieldSymbol? FindField(string name)
            {
                for (var ns = ctx.Namespace; ns != null; ns = ns.Parent)
                {
                    var hit = ns.Fields.FirstOrDefault(f => f.Name == name);
                    if (hit != null) return hit;
                }
                foreach (var container in WildcardImportContainers())
                {
                    if (container is NamespaceSymbol ns)
                    {
                        var hit = ns.Fields.FirstOrDefault(f => f.Name == name);
                        if (hit != null) return hit;
                    }
                }
                return null;
            }

            // 方法查找序：宿主类型成员（声明类型沿 BaseType 链，先于命名空间
            // 全局函数；实例方法命中后由 BindCallee 静态性检查拦截）
            // → 命名空间链 → 通配 import 容器方法
            private List<MethodSymbol> FindMethods(string name)
            {
                var result = new List<MethodSymbol>();
                for (var host = declaringType; host != null; host = host.BaseType)
                {
                    result.AddRange(host.Methods.Where(m => m.Name == name));
                }
                for (var ns = ctx.Namespace; ns != null; ns = ns.Parent)
                {
                    result.AddRange(ns.Methods.Where(m => m.Name == name));
                }
                foreach (var container in WildcardImportContainers())
                {
                    if (container is NamespaceSymbol ns)
                    {
                        result.AddRange(ns.Methods.Where(m => m.Name == name));
                    }
                }
                return result;
            }

            // 通配 import 的容器（具名 import 经 P2 语义只导类型/命名空间，
            // 对值/函数查找无贡献）；import 路径解析静默（P2 已统一诊断）
            private IEnumerable<SemanticSymbol> WildcardImportContainers()
            {
                foreach (var item in ctx.Imports)
                {
                    if (!item.importAll) continue;
                    var container = names.ResolveSymbolPath(item.symbolNode.symbol, ctx,
                        declaringType: null, declaringMethod: null,
                        allowImports: false, reportErrors: false, span: null);
                    if (container is not ErrorTypeSymbol) yield return container;
                }
            }

            // ===== 类型兼容与显示 =====

            // 赋值兼容（BIL §6.5 的 source-level 判定）：严格相同、可空提升
            // （T → Nullable\<T>）、BaseType 链子类型、直接 interface 实现；
            // 子类型的显式 cast 由 P4a 物化。ErrorType 任一侧静默通过（毒化）。
            private bool IsAssignable(TypeSymbol from, TypeSymbol to)
            {
                if (ReferenceEquals(from, to)) return true;
                if (from is ErrorTypeSymbol || to is ErrorTypeSymbol) return true;
                if (ReferenceEquals(to.ConstructedFrom, unit.Symbols.Bootstrap.NullableDefinition)
                    && to.TypeArguments![0] is TypeSymbol element
                    && IsAssignable(from, element))
                {
                    return true;
                }
                for (var t = from.BaseType; t != null; t = t.BaseType)
                {
                    if (ReferenceEquals(t, to)) return true;
                }
                return from.Interfaces.Any(i => ReferenceEquals(i, to));
            }

            private static bool IsComparison(BilIntrinsicOp op)
            {
                return op is BilIntrinsicOp.CmpEq or BilIntrinsicOp.CmpNe or BilIntrinsicOp.CmpLt
                    or BilIntrinsicOp.CmpLe or BilIntrinsicOp.CmpGt or BilIntrinsicOp.CmpGe;
            }

            private static BilIntrinsicOp MapBinaryOperator(string token)
            {
                return token switch
                {
                    "+" => BilIntrinsicOp.Add,
                    "-" => BilIntrinsicOp.Sub,
                    "*" => BilIntrinsicOp.Mul,
                    "/" => BilIntrinsicOp.Div,
                    "and" => BilIntrinsicOp.And,
                    "or" => BilIntrinsicOp.Or,
                    "&" => BilIntrinsicOp.BinAnd,
                    "|" => BilIntrinsicOp.BinOr,
                    "^" => BilIntrinsicOp.BinXor,
                    "<<" => BilIntrinsicOp.ShiftLeft,
                    ">>" => BilIntrinsicOp.ShiftRight,
                    ">>>" => BilIntrinsicOp.ShiftRightUnsigned,
                    "==" => BilIntrinsicOp.CmpEq,
                    "!=" => BilIntrinsicOp.CmpNe,
                    "<" => BilIntrinsicOp.CmpLt,
                    "<=" => BilIntrinsicOp.CmpLe,
                    ">" => BilIntrinsicOp.CmpGt,
                    ">=" => BilIntrinsicOp.CmpGe,
                    _ => throw new CompilerInternalException("未知二元运算符: " + token),
                };
            }

            // 诊断消息中的类型短名（构造类型带实参递归）
            private static string TypeDisplay(TypeSymbol type)
            {
                if (type.ConstructedFrom == null) return type.Name;
                return type.Name + "<" + string.Join(", ",
                    type.TypeArguments!.Select(a => a is TypeSymbol t ? TypeDisplay(t) : a.Name)) + ">";
            }

            private BoundExpression? ErrorAndNull(CharRange? span, string message)
            {
                Error(span, message);
                return null;
            }
        }
    }
}
