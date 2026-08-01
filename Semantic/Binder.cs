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
    // S7c-1 落地范围：while/do-while 循环（循环标签栈解析 break/continue
    // 标签，条件 bool 检查；for 报 not supported yet (S7c-2)）、值块内
    // break/continue 穿透（GuaranteesValueReturn 视其为路径终止）、
    // definite assignment 循环规则（while 后 = before，体可能零次执行；
    // do-while 后 = 体尾集合，体至少一次）。
    // S7c-2 落地范围：this（静态上下文诊断）、实例成员链上色（实例方法
    // 调用/实例字段访问，receiver 静态类型沿 BaseType 链查找，接口
    // receiver 查接口成员；ext 注册成员同路径）、裸名实例成员补 this
    // （宿主查找统一从 method.Owner 出发——ext 方法 Owner = 目标类型）、
    // for 双形态（范围循环 = EnumerateInRange 实例 operator 调用 + for-each
    // 协议判定：实现 core.collections::IEnumerable\<TItem\>；协议三方法
    // 符号 P3 挂好，P4 不做名字分析；循环变量 const——只读默认，规范未明）。
    // S7d 落地范围：switch 语句/表达式（case 分类显式记录——值匹配 =
    // 编译期常量且类型与 selector 严格相同，含 _ pattern = bool 表达式，
    // _ 经占位栈绑为 BoundSwitchPlaceholderExpression；表达式形态分支体
    // 复用值块机制，产值类型全分支统一；DA 合并 before ∪ (∩ 全部体)）、
    // throw（异常表达式与异常根 core.Exception 兼容检查；throw/switch
    // 计入 GuaranteesReturn/GuaranteesValueReturn 终止口径）。
    // S7e 落地范围：cast（as/as?——可转性不做静态拒绝，as 失败是运行时
    // core.CastException，as? 结果类型 Nullable<T>）、try-catch-finally
    // （catch 类型兼容 core.Exception 检查；catch/finally 变量 const——
    // 只读默认，规范未明，M50 登记；finally(e) 的 e 类型
    // Nullable<core.Exception>；DA 合并：有 catch 时 before ∪ (try ∩ 各
    // catch)，无 catch 时 try 直通——异常必穿透，finally 恒执行并集）、
    // seq 双形态（语句 = 块级直通不压值块栈——return@ 指向它报未定义
    // 标签，M50 登记；表达式 = 值块机制复用 + volatile 置位；using 绑定
    // 列表归 S13，P3 拦截）。
    // S8a 落地范围：is/supers/with 类型谓词（右侧双形态——类型引用静态形态
    // 不落袋试探优先，失败按值绑定且必须承载 Type\<T\>；with 静态目标
    // 必须 Kind == Wrapper；结果恒 bool；enum case 形态归 S11）与
    // typeOf（值形态定型 Type\<操作数静态类型\>，类型形态定型 Type\<T\>；
    // 单段裸名操作数先值后类型不落袋分类）。
    // 访问控制（priv/protected）检查不做（归 S8，命中即放行）。
    //
    // 值/调用的名字解析查找序：块作用域链 → 参数 → 宿主类型成员
    // （声明类型沿 BaseType 链；当前仅调用查找落地，字段裸名归后续里程碑）
    // → 命名空间链（文件命名空间及父链，顶端即全局命名空间）字段/函数
    // → 通配 import 容器成员；
    // 多段路径 = 容器（命名空间/类型，经 NameResolver）+ 末段成员。
    // 类型引用解析与 P2 共用 NameResolver（本类以 DiagnosticPhase.P3 实例化）。
    //
    // 明确不做（归后续里程碑，遇之一律 P3 诊断而非崩溃）：
    // 其余控制流（yield，S7 后续）、成员访问与实例
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
        // BoundCallExpression / BoundCallStatement（void 调用）；
        // Receiver 为 null = 静态/全局调用，非 null = 实例调用（S7c-2）
        private sealed class CallBinding
        {
            public MethodSymbol Method = null!;
            public IReadOnlyList<BoundExpression> Arguments = null!;
            public bool IsVoid;
            public BoundExpression? Receiver;
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
            // return@标签 沿栈从内向外查找命中（引用相等即身份）。
            // LoopDepth = 值块创建时的循环栈深度（S7c-1）：return@ 命中时
            // 若当前循环更深，说明 return@ 隔着循环边界——P4a 脱糖（写局部）
            // 无法表达「跳出中间循环」，P3 拦截为诊断（S7c 技术债）
            private readonly Stack<(BoundValueBlock Block, int LoopDepth)> valueBlocks =
                new Stack<(BoundValueBlock, int)>();
            // 循环标签栈（S7c-1）：绑定循环体前压入施工壳（Label 可空），
            // break/continue 沿栈从内向外查找命中（引用相等即身份）；
            // 穿透值块命中外层循环合法（BIL §16.5 动态结构作用域）
            private readonly Stack<BoundLoop> loops = new Stack<BoundLoop>();
            // switch pattern 占位栈（S7d）：绑定含 _ 的 case 匹配表达式期间
            // 压入所属 switch 的 selector 表达式，BindPath 单段名 _ 命中栈顶
            // （嵌套 switch 逐层向内命中）；仅匹配表达式绑定期间存活，
            // 分支体无 _ 语义（SYNTAX §7.2）
            private readonly Stack<BoundExpression> switchSelectors = new Stack<BoundExpression>();

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
                loops.Clear();
                switchSelectors.Clear();

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
                    // S7d：throw 终止本路径（不落到块尾）→ 与 return 同口径
                    BoundThrowStatement => true,
                    // S7d：switch 全部分支体（含 default，Parser 强制存在）
                    // 都保证返回 → 保证返回
                    BoundSwitchStatement switchStatement =>
                        switchStatement.Cases.All(c => GuaranteesReturn(c.Body))
                        && GuaranteesReturn(switchStatement.DefaultBody),
                    // S7e：try——finally 终止即整体终止（finally 恒执行，其终止
                    // 覆盖所有路径）；否则 try 与全部 catch 体都保证返回才成立
                    // （无 catch 时异常必穿透，只剩 try 正常完成路径——单块判定）
                    BoundTryStatement tryStatement =>
                        (tryStatement.FinallyBlock != null
                            && GuaranteesReturn(tryStatement.FinallyBlock))
                        || (GuaranteesReturn(tryStatement.TryBlock)
                            && tryStatement.Catches.All(c => GuaranteesReturn(c.Body))),
                    BoundBlock nested => GuaranteesReturn(nested),
                    // S7c-1：循环保守 false——`while (true)` 无 break 的恒循环
                    // 特例留口（体可能零次执行的一般情形无法判定，S7c 技术债）；
                    // BoundLoopControl/Break 终止的是循环路径，不算函数返回；
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
                    SwitchStatementASTNode switchStatement => BindSwitchStatement(switchStatement, scope),
                    LoopStatementASTNode loop => BindLoop(loop, scope),
                    LoopControlStatementASTNode loopControl => BindLoopControl(loopControl),
                    ThrowStatementASTNode throwStatement => BindThrow(throwStatement, scope),
                    TryCatchFinallyStatementASTNode tryStatement => BindTry(tryStatement, scope),
                    // seq 语句（S7e）：语句位置的 seq 是裸 SeqBlockExpressionASTNode
                    // 直接进块（CodeBlockParserLayer 施工形态，非表达式语句包装）
                    SeqBlockExpressionASTNode seqStatement => BindSeqStatement(seqStatement, scope),
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
                // 解构声明（S7f，SYNTAX §18）：与单名形态互斥
                if (node.DestructureNames != null)
                {
                    return BindDestructuring(node, scope);
                }
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

            // 解构声明（S7f，SYNTAX §18）：var (a, b) = pair——初始化器类型
            // 必须沿 BaseType 链达到 core.Pair\<TKey, TValue\> 构造；每个名字
            // 绑定为对应分量类型的局部（字段读取由 P4a 脱糖）。core.Pair 是
            // .bootstrap.latte 自举声明，编译器按 canonical 名硬编码参照
            // （同 M48 core.collections 协议先例）
            private BoundStatement? BindDestructuring(VariableDeclarationASTNode node, Scope scope)
            {
                // Parser 已强制 =；此处为防御性检查
                if (node.Initializer == null)
                {
                    Error(node.Span, "Destructuring declaration requires an initializer");
                    return null;
                }
                var init = BindExpression(node.Initializer.Expression, scope);
                if (init == null) return null;
                if (init.Type is ErrorTypeSymbol) return null;
                var pairDef = FindCorePairDefinition(node.Span);
                if (pairDef == null) return null;
                TypeSymbol? constructed = null;
                for (var t = init.Type; t != null; t = t.BaseType)
                {
                    if (ReferenceEquals(t.ConstructedFrom, pairDef))
                    {
                        constructed = t;
                        break;
                    }
                }
                if (constructed == null)
                {
                    Error(node.Span,
                        $"Destructuring requires a subtype of core.Pair\\<TKey, TValue\\> " +
                        $"(got '{TypeDisplay(init.Type)}')");
                    return null;
                }
                if (node.DestructureNames!.Count != 2)
                {
                    Error(node.Span,
                        $"Destructuring of core.Pair requires exactly 2 names " +
                        $"(got {node.DestructureNames.Count})");
                    return null;
                }
                var entries = new List<(LocalSymbol, FieldSymbol)>();
                var componentNames = new[] { "key", "value" };
                for (int i = 0; i < 2; i++)
                {
                    // 分量类型 = 构造实参（实参含未替换泛型参数归 S9）
                    if (constructed.TypeArguments![i] is not TypeSymbol componentType
                        || ContainsGenericParameter(componentType))
                    {
                        Error(node.Span,
                            "P3: generic type parameters are not supported yet (S9)");
                        return null;
                    }
                    var name = node.DestructureNames[i];
                    if (scope.DeclaresHere(name))
                    {
                        Error(node.Span, $"Duplicate local variable '{name}'");
                        return null;
                    }
                    var field = pairDef.Fields.First(f => f.Name == componentNames[i]);
                    var local = new LocalSymbol(name, componentType, node.IsConst);
                    scope.Declare(local);
                    locals.Add(local);
                    assigned.Add(local);
                    entries.Add((local, field));
                }
                return new BoundDestructuringDeclarationStatement(node, init, entries);
            }

            // core.Pair 定义查找（.bootstrap.latte 自举提供；缺席即诊断——
            // BindUnit 类不带 stdlib 的驱动触不到解构绑定）
            private TypeSymbol? FindCorePairDefinition(CharRange? span)
            {
                var core = unit.Symbols.GlobalNamespace.ChildNamespaces
                    .FirstOrDefault(n => n.Name == "core");
                var pair = core?.Types.FirstOrDefault(t => t.Name == "Pair"
                    && t.GenericParameters.Count == 2);
                if (pair == null)
                {
                    Error(span, "P3: core.Pair not found " +
                        "(required by destructuring declaration; stdlib missing)");
                }
                return pair;
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
                        return new BoundCallStatement(node, binding.Method, binding.Arguments,
                            binding.Receiver);
                    }
                    if (binding.Receiver != null)
                    {
                        return new BoundExpressionStatement(node,
                            new BoundInstanceCallExpression(path, binding.Receiver,
                                binding.Method, binding.Arguments,
                                (TypeSymbol)binding.Method.ReturnType!));
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
                    case BoundFieldAccessExpression:
                        // 实例字段（S7c-2）：const 判定同全局字段技术债
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
                    var targetLoopDepth = 0;
                    foreach (var (valueBlock, loopDepth) in valueBlocks)
                    {
                        if (valueBlock.Label == node.Label)
                        {
                            target = valueBlock;
                            targetLoopDepth = loopDepth;
                            break;
                        }
                    }
                    if (target == null)
                    {
                        Error(node.Span, $"Undefined value block label: '{node.Label}'");
                        return null;
                    }
                    // return@ 隔循环边界（S7c-1 拦截，S7c 技术债）：脱糖产物
                    // 只是「写值块局部」，无法表达「跳出中间循环」，P3 拒绝
                    if (loops.Count > targetLoopDepth)
                    {
                        Error(node.Span, $"P3: return@{node.Label} across a loop " +
                            "boundary not supported yet (S7c)");
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
                CheckBoolCondition(node.Condition, node.Span, condition, "if");
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
                CheckBoolCondition(node.Condition, node.Span, condition, "if");
                var label = node.Label ?? "_";
                var before = new HashSet<LocalSymbol>(assigned);
                var trueBranch = BindValueBlock(node.ThenBody, label, scope, "if expression");
                var trueAssigned = new HashSet<LocalSymbol>(assigned);
                RestoreAssigned(before);
                var falseBranch = BindValueBlock(node.ElseBody, label, scope, "if expression");
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

            // 条件必须 bool（if 语句/表达式、循环同规则；ErrorType 毒化静默）
            private void CheckBoolCondition(ExpressionRootASTNode conditionRoot,
                CharRange? fallbackSpan, BoundExpression? condition, string construct)
            {
                if (condition != null && condition.Type is not ErrorTypeSymbol
                    && !ReferenceEquals(condition.Type, B.Bool))
                {
                    Error(conditionRoot.Span ?? fallbackSpan,
                        $"{construct} condition must be bool " +
                        $"(got '{TypeDisplay(condition.Type)}')");
                }
            }

            // 值块绑定（if/switch 表达式分支体，SYNTAX §6.1/§7.1/§7.2）：
            // - 施工壳先于分支体绑定创建并压入标签栈（分支体内的 return@标签 经栈命中），
            //   分支体绑完后回填 Block/IsImplicitValue/ValueType；
            // - 语法上恰好一条纯表达式语句（赋值语句不算）→ 隐式取值，
            //   ValueType = 该表达式类型；
            // - 否则所有执行路径必须显式 return@（GuaranteesValueReturn 检查，
            //   穿透终止也算路径终止），ValueType = 命中本块的 return@ 值类型
            //   统一结果；无本块产值（纯穿透）→ ValueType = null。
            // construct 为诊断消息中的构造名（"if expression"/"switch expression"）
            private BoundValueBlock BindValueBlock(CodeBlockASTNode node, string label, Scope scope,
                string construct)
            {
                var shell = new BoundValueBlock(node, label);
                valueBlocks.Push((shell, loops.Count));
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
                        Error(node.Span, $"{construct} branch must produce a value " +
                            "(a void call has no result)");
                    }
                    return shell;
                }
                if (!GuaranteesValueReturn(block))
                {
                    var article = "aeiou".Contains(construct[0]) ? "an" : "a";
                    Error(node.Span, $"All code paths of {article} {construct} branch must " +
                        "explicitly return@ a value");
                }
                shell.ValueType = CollectBranchValueType(block, shell, construct);
                return shell;
            }

            // 「所有执行路径显式 return@」判定：末语句是 BoundReturnValueStatement
            // （命中任意值块，穿透终止也算路径终止）→ true；末语句是 BoundIfStatement
            // 且双分支 GuaranteesValueReturn → true；末语句是嵌套 BoundBlock → 递归；
            // 末语句是 BoundLoopControl（S7c-1）→ true——break/continue 落在值块
            // 语句层时目标必是值块外的循环（该语句不被值块内任何循环包含，且
            // 循环外 break/continue 已被 P3 拒绝），穿透值块终止本路径合法
            // （BIL §16.5 动态结构作用域：if/循环块内引用外层循环 breakid 合法）；
            // 末语句是 BoundThrowStatement（S7d）→ true——throw 终止本路径；
            // 末语句是 BoundSwitchStatement（S7d）且全部分支体（含 default）
            // GuaranteesValueReturn → true；末语句是 BoundTryStatement（S7e）→
            // finally 终止覆盖，否则 try 与全部 catch 体都终止；末语句是
            // BoundSeqStatement（S7e）→ 体穿透（return@ 穿透语句 seq 命中外层
            // 值块合法）；其余 false（裸 return 终止函数路径
            // 但不作为值块产值收尾，规则从简）
            private static bool GuaranteesValueReturn(BoundBlock block)
            {
                return block.Statements.Count > 0 && block.Statements[^1] switch
                {
                    BoundReturnValueStatement => true,
                    BoundLoopControl => true,
                    BoundThrowStatement => true,
                    BoundIfStatement ifStatement => ifStatement.FalseBlock != null
                        && GuaranteesValueReturn(ifStatement.TrueBlock)
                        && GuaranteesValueReturn(ifStatement.FalseBlock),
                    BoundSwitchStatement switchStatement =>
                        switchStatement.Cases.All(c => GuaranteesValueReturn(c.Body))
                        && GuaranteesValueReturn(switchStatement.DefaultBody),
                    BoundTryStatement tryStatement =>
                        (tryStatement.FinallyBlock != null
                            && GuaranteesValueReturn(tryStatement.FinallyBlock))
                        || (GuaranteesValueReturn(tryStatement.TryBlock)
                            && tryStatement.Catches.All(c => GuaranteesValueReturn(c.Body))),
                    BoundSeqStatement seqStatement => GuaranteesValueReturn(seqStatement.Body),
                    BoundBlock nested => GuaranteesValueReturn(nested),
                    _ => false,
                };
            }

            // 收集分支块内命中本块的 return@ 值类型（递归嵌套块、if 分支与
            // switch 分支体）；全部须类型一致（符号 ==，驻留保证；ErrorType
            // 毒化静默跳过），不一致诊断并以首个为准；无命中（纯穿透终止）→ null。
            // construct 为诊断消息中的构造名（同 BindValueBlock）
            private TypeSymbol? CollectBranchValueType(BoundBlock block, BoundValueBlock shell,
                string construct)
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
                                $"{construct} branch produces different types " +
                                $"('{TypeDisplay(collected)}' and " +
                                $"'{TypeDisplay(returnValue.Value.Type)}')");
                        }
                    }
                }
                return collected;
            }

            // 块内语句的平铺枚举（递归嵌套 BoundBlock、BoundIfStatement 两分支、
            // BoundSwitchStatement 全部分支体（S7d：switch 体内 return@ 可穿透
            // 命中外层值块，收集/终止判定须看得到）、BoundTryStatement 三个块与
            // BoundSeqStatement 体（S7e：同理穿透可见））
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
                        case BoundSwitchStatement switchStatement:
                            foreach (var switchCase in switchStatement.Cases)
                            {
                                foreach (var s in EnumerateStatements(switchCase.Body))
                                    yield return s;
                            }
                            foreach (var s in EnumerateStatements(switchStatement.DefaultBody))
                                yield return s;
                            break;
                        case BoundTryStatement tryStatement:
                            foreach (var s in EnumerateStatements(tryStatement.TryBlock))
                                yield return s;
                            foreach (var catchClause in tryStatement.Catches)
                            {
                                foreach (var s in EnumerateStatements(catchClause.Body))
                                    yield return s;
                            }
                            if (tryStatement.FinallyBlock != null)
                            {
                                foreach (var s in EnumerateStatements(tryStatement.FinallyBlock))
                                    yield return s;
                            }
                            break;
                        case BoundSeqStatement seqStatement:
                            foreach (var s in EnumerateStatements(seqStatement.Body))
                                yield return s;
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

            // ===== switch 语句/表达式 + throw（S7d，SYNTAX §7.2/§8）=====

            // switch 语句：selector 先绑（DA 效果保留——selector 必求值一次）；
            // 每 case（匹配表达式 + 分支体）与 default 体各自从 before 快照
            // 出发绑定（前一分支的赋值效果不泄入后一分支），DA 合并
            // before ∪ (∩ 全部分支尾集合)——default 恒存在（Parser 强制），
            // 规则即 if 双分支合并的推广
            private BoundStatement? BindSwitchStatement(SwitchStatementASTNode node, Scope scope)
            {
                var selector = BindExpression(node.Selector.Expression, scope);
                var before = new HashSet<LocalSymbol>(assigned);
                var cases = new List<BoundSwitchCase>();
                var branchAssigned = new List<HashSet<LocalSymbol>>();
                foreach (var caseNode in node.Cases)
                {
                    RestoreAssigned(before);
                    var match = BindSwitchMatch(caseNode, selector, scope, out var isPattern);
                    var body = BindBlock(caseNode.Body, scope);
                    branchAssigned.Add(new HashSet<LocalSymbol>(assigned));
                    if (match != null)
                    {
                        cases.Add(new BoundSwitchCase(caseNode, match, isPattern, body));
                    }
                }
                RestoreAssigned(before);
                var defaultBody = BindBlock(RequireSwitchDefault(node.DefaultBody), scope);
                branchAssigned.Add(new HashSet<LocalSymbol>(assigned));
                MergeBranches(before, branchAssigned);
                if (selector == null) return null;
                return new BoundSwitchStatement(node, selector, cases, defaultBody);
            }

            // switch 表达式：分支体（含 default）各绑一个值块（标签同源
            // Label ?? "_"——return@ 命中规则同 if 表达式），产值类型全分支
            // 统一（纯穿透分支不参与；全穿透即无产值；引用不等且非 ErrorType
            // 报不一致）；DA 合并同语句形态
            private BoundExpression? BindSwitchExpression(SwitchExpressionASTNode node, Scope scope)
            {
                var selector = BindExpression(node.Selector.Expression, scope);
                var label = node.Label ?? "_";
                var before = new HashSet<LocalSymbol>(assigned);
                var cases = new List<BoundSwitchExpressionCase>();
                var branchAssigned = new List<HashSet<LocalSymbol>>();
                foreach (var caseNode in node.Cases)
                {
                    RestoreAssigned(before);
                    var match = BindSwitchMatch(caseNode, selector, scope, out var isPattern);
                    var body = BindValueBlock(caseNode.Body, label, scope, "switch expression");
                    branchAssigned.Add(new HashSet<LocalSymbol>(assigned));
                    if (match != null)
                    {
                        cases.Add(new BoundSwitchExpressionCase(caseNode, match, isPattern, body));
                    }
                }
                RestoreAssigned(before);
                var defaultBody = BindValueBlock(RequireSwitchDefault(node.DefaultBody), label,
                    scope, "switch expression");
                branchAssigned.Add(new HashSet<LocalSymbol>(assigned));
                MergeBranches(before, branchAssigned);
                if (selector == null) return null;
                // 产值类型统一（规则同 if 表达式）：纯穿透分支（ValueType null）
                // 不参与；有产值分支符号须引用相等（ErrorType 毒化静默）
                TypeSymbol? type = null;
                foreach (var branch in cases.Select(c => c.Body).Append(defaultBody))
                {
                    if (branch.ValueType == null) continue;
                    if (type == null)
                    {
                        type = branch.ValueType;
                        continue;
                    }
                    if (!ReferenceEquals(type, branch.ValueType)
                        && type is not ErrorTypeSymbol && branch.ValueType is not ErrorTypeSymbol)
                    {
                        Error(node.Span,
                            $"switch expression branches produce different types " +
                            $"('{TypeDisplay(type)}' and '{TypeDisplay(branch.ValueType)}')");
                        return null;
                    }
                }
                if (type == null)
                {
                    Error(node.Span, "switch expression must produce a value " +
                        "(at least one branch must return@ a value)");
                    return null;
                }
                return new BoundSwitchExpression(node, selector, cases, defaultBody, type);
            }

            // case 匹配表达式绑定（两形态共用）：先分类——AST 子树含单段路径 _
            // 即 pattern（SYNTAX §7.2：_ 引用 selector 的值），否则值匹配。
            // 值匹配：编译期常量最小口径（BoundLiteralExpression；enum case
            // 归 S11），类型与 selector 严格相同；pattern：占位栈开启下绑定，
            // 结果须 bool。selector 已失败（null）时值匹配照常绑定（独立诊断），
            // pattern 静默跳过（_ 无所指，避免次生错误）；分类经 out 显式记录
            // （IsPattern——P4a 按 §16.6 把 pattern 分支降级为嵌套条件）
            private BoundExpression? BindSwitchMatch(SwitchCaseASTNode node,
                BoundExpression? selector, Scope scope, out bool isPattern)
            {
                isPattern = ContainsSwitchPlaceholder(node.Pattern.Expression);
                if (isPattern)
                {
                    if (selector == null) return null;
                    switchSelectors.Push(selector);
                    BoundExpression? match;
                    try
                    {
                        match = BindExpression(node.Pattern.Expression, scope, selector.Type);
                    }
                    finally
                    {
                        switchSelectors.Pop();
                    }
                    if (match != null && match.Type is not ErrorTypeSymbol
                        && !ReferenceEquals(match.Type, B.Bool))
                    {
                        Error(node.Pattern.Span ?? node.Span,
                            $"switch pattern case must be bool (got '{TypeDisplay(match.Type)}')");
                    }
                    return match;
                }
                var value = BindExpression(node.Pattern.Expression, scope, selector?.Type);
                if (value != null && selector != null
                    && value.Type is not ErrorTypeSymbol && selector.Type is not ErrorTypeSymbol)
                {
                    if (value is not BoundLiteralExpression)
                    {
                        Error(node.Pattern.Span ?? node.Span,
                            "switch value-match case requires a compile-time constant");
                    }
                    else if (!ReferenceEquals(value.Type, selector.Type))
                    {
                        Error(node.Pattern.Span ?? node.Span,
                            $"switch case constant type must equal the selector type " +
                            $"(got '{TypeDisplay(value.Type)}' and '{TypeDisplay(selector.Type)}')");
                    }
                }
                return value;
            }

            // pattern 分类判定：匹配表达式 AST 子树含单段路径 _（符号头名为 _
            // 且无后缀无段）即 pattern match。子树遍历统一走
            // AstStructureReflection（M28 唯一反射下钻）
            private static bool ContainsSwitchPlaceholder(ASTNode node)
            {
                if (node is PathExpressionASTNode path
                    && path.Head.Expression == null && path.Head.Name == "_"
                    && path.Head.Suffixes.Count == 0 && path.Segments.Count == 0)
                {
                    return true;
                }
                foreach (var (child, _) in AstStructureReflection.EnumerateChildren(node))
                {
                    if (ContainsSwitchPlaceholder(child)) return true;
                }
                return false;
            }

            // switch default 分支体（两形态 Parser 强制存在；缺失即 Parser 不变量破坏）
            private static CodeBlockASTNode RequireSwitchDefault(CodeBlockASTNode? defaultBody)
            {
                return defaultBody ?? throw new CompilerInternalException(
                    "switch 缺 default 分支（Parser 不变量破坏）");
            }

            // DA 分支合并（switch 专用，if 双分支规则的推广）：
            // 合并结果 = before ∪ (∩ 各分支尾集合)；无分支时保守恢复 before
            private void MergeBranches(HashSet<LocalSymbol> before,
                List<HashSet<LocalSymbol>> branchAssigned)
            {
                if (branchAssigned.Count == 0)
                {
                    RestoreAssigned(before);
                    return;
                }
                var merged = new HashSet<LocalSymbol>(branchAssigned[0]);
                for (int i = 1; i < branchAssigned.Count; i++)
                {
                    merged.IntersectWith(branchAssigned[i]);
                }
                merged.UnionWith(before);
                RestoreAssigned(merged);
            }

            // throw（SYNTAX §8）：异常表达式必须与异常根 core.Exception 兼容
            // （IsAssignable 沿 BaseType 链命中；ErrorType 毒化静默）
            private BoundStatement? BindThrow(ThrowStatementASTNode node, Scope scope)
            {
                var exception = BindExpression(node.Exception.Expression, scope, B.Exception);
                if (exception == null) return null;
                if (!IsAssignable(exception.Type, B.Exception))
                {
                    Error(node.Exception.Span ?? node.Span,
                        $"Cannot throw '{TypeDisplay(exception.Type)}' " +
                        "(not compatible with 'Exception')");
                    return null;
                }
                return new BoundThrowStatement(node, exception);
            }

            // ===== try-catch-finally / seq / cast（S7e，SYNTAX §8/§10/§3.5）=====

            // try：try 体、各 catch 体、finally 体各自从 before 快照出发绑定
            // （异常可在任意点穿透，异常路径的赋值效果不泄入正常继续路径）。
            // catch 类型必须兼容 core.Exception；catch 变量（_ 丢弃时无变量）
            // 建 const 局部（只读默认，规范未明，M50 登记），命中即视为已赋值。
            // finally(e) 的 e 类型 = Nullable<core.Exception>（无异常时为 null，
            // SYNTAX §8），const。
            // definite assignment 合并（保守规则，S7e 定稿）：
            // - 有 catch：merged = before ∪ (tryAssigned ∩ (∩ 各 catchAssigned))
            //   ——继续路径是 try 正常完成或任一 catch 命中完成的并集；
            //   catch 变量经交集自动剪除（不在 tryAssigned/before 中）；
            // - 无 catch：merged = tryAssigned——异常必穿透，继续路径只有
            //   try 正常完成；
            // - 有 finally：merged ∪= finallyAssigned（finally 恒执行），
            //   并移除 finallyVariable（作用域限 finally 体，不外泄）
            private BoundStatement? BindTry(TryCatchFinallyStatementASTNode node, Scope scope)
            {
                var before = new HashSet<LocalSymbol>(assigned);
                var tryBlock = BindBlock(node.TryBlock, scope);
                var tryAssigned = new HashSet<LocalSymbol>(assigned);

                var catches = new List<BoundCatchClause>();
                var catchAssigned = new List<HashSet<LocalSymbol>>();
                foreach (var catchNode in node.CatchClauses)
                {
                    RestoreAssigned(before);
                    var exceptionType = ResolveBodyTypeReference(catchNode.ExceptionType,
                        catchNode.Span);
                    if (exceptionType != null && !IsAssignable(exceptionType, B.Exception))
                    {
                        Error(catchNode.Span, $"catch type must be compatible with 'Exception' " +
                            $"(got '{TypeDisplay(exceptionType)}')");
                    }
                    // catch 变量：_ 即丢弃（VariableName 为 null）；const 局部，
                    // 命中即已赋值（作用域限 catch 体）
                    LocalSymbol? variable = null;
                    var catchScope = new Scope(scope);
                    if (catchNode.VariableName != null && exceptionType != null)
                    {
                        variable = new LocalSymbol(catchNode.VariableName, exceptionType,
                            isConst: true);
                        catchScope.Declare(variable);
                        locals.Add(variable);
                        assigned.Add(variable);
                    }
                    var body = BindBlock(catchNode.Body, catchScope);
                    catchAssigned.Add(new HashSet<LocalSymbol>(assigned));
                    if (exceptionType != null)
                    {
                        catches.Add(new BoundCatchClause(catchNode, variable, exceptionType, body));
                    }
                }

                BoundBlock? finallyBlock = null;
                LocalSymbol? finallyVariable = null;
                HashSet<LocalSymbol>? finallyAssigned = null;
                if (node.FinallyBlock != null)
                {
                    RestoreAssigned(before);
                    var finallyScope = new Scope(scope);
                    if (node.FinallyParameter != null)
                    {
                        finallyVariable = new LocalSymbol(node.FinallyParameter,
                            unit.Symbols.GetNullable(B.Exception), isConst: true);
                        finallyScope.Declare(finallyVariable);
                        locals.Add(finallyVariable);
                        assigned.Add(finallyVariable);
                    }
                    finallyBlock = BindBlock(node.FinallyBlock, finallyScope);
                    finallyAssigned = new HashSet<LocalSymbol>(assigned);
                }

                HashSet<LocalSymbol> merged;
                if (catchAssigned.Count > 0)
                {
                    merged = new HashSet<LocalSymbol>(tryAssigned);
                    foreach (var set in catchAssigned) merged.IntersectWith(set);
                    merged.UnionWith(before);
                }
                else
                {
                    merged = tryAssigned;
                }
                if (finallyAssigned != null)
                {
                    merged.UnionWith(finallyAssigned);
                    if (finallyVariable != null) merged.Remove(finallyVariable);
                }
                RestoreAssigned(merged);
                return new BoundTryStatement(node, tryBlock, catches, finallyBlock,
                    finallyVariable);
            }

            // seq 语句（SYNTAX §10.1）：块级顺序执行区——绑定直通 BindBlock
            // （作用域/assigned 语义与裸块相同）；不压值块标签栈（return@ 指向
            // 语句 seq 报未定义标签，规范未明，M50 登记）；using 归 S13（拦截）
            private BoundStatement? BindSeqStatement(SeqBlockExpressionASTNode node, Scope scope)
            {
                if (node.UsingBindings.Count > 0)
                {
                    Error(node.Span, "P3: using bindings are not supported yet (S13)");
                    return null;
                }
                return new BoundSeqStatement(node, BindBlock(node.Body, scope), node.IsVolatile);
            }

            // seq 表达式（SYNTAX §10.2）：体即值块（标签同源 Label ?? "_"，
            // 取值规则同 if 表达式分支体）；volatile 置位到值块（BIL §9.6
            // block 修饰符）；using 归 S13（拦截）；必须产值（至少一条路径
            // return@——无产值的 seq 块应写语句形态）
            private BoundExpression? BindSeqExpression(SeqBlockExpressionASTNode node, Scope scope)
            {
                if (node.UsingBindings.Count > 0)
                {
                    Error(node.Span, "P3: using bindings are not supported yet (S13)");
                    return null;
                }
                var body = BindValueBlock(node.Body, node.Label ?? "_", scope, "seq expression");
                body.IsVolatile = node.IsVolatile;
                if (body.ValueType == null)
                {
                    Error(node.Span, "seq expression must produce a value " +
                        "(at least one path must return@ a value)");
                    return null;
                }
                return new BoundSeqExpression(node, body, body.ValueType);
            }

            // cast（SYNTAX §3.5）：as / as?。as 结果类型即目标类型，as? 结果
            // 类型 = Nullable<目标类型>（P3 定型，P4 不再区分包装）。可转性
            // 不做静态拒绝（as 失败是运行时 core.CastException；castTo/castFrom
            // 名字分析归后续里程碑）；ErrorType 毒化静默（结果沿用 ErrorType）
            private BoundExpression? BindCast(CastExpressionASTNode node, Scope scope)
            {
                var source = BindExpression(node.Object.Expression, scope);
                var targetType = ResolveBodyTypeReference(node.TargetType, node.Span);
                if (source == null || targetType == null) return null;
                var resultType = targetType is ErrorTypeSymbol
                    ? targetType
                    : node.IsSafe ? unit.Symbols.GetNullable(targetType) : targetType;
                return new BoundCastExpression(node, source, targetType, node.IsSafe, resultType);
            }

            // ===== 类型谓词与 typeOf（S8a，SYNTAX §3.5/§3.7）=====

            // is / supers / with（BIL §12.3）：右侧双形态——类型引用（静态）
            // 或 Type\<T\> 值（动态）。解析顺序：先按类型引用解析（reportErrors:
            // false 不落袋试探），失败再按值绑定；结果恒 bool。不做静态不可能性
            // 拒绝（12 is String 不报错，运行时判定）
            private BoundExpression? BindTypeCheck(TypeCheckExpressionASTNode node, Scope scope)
            {
                var operand = BindExpression(node.Object.Expression, scope);
                // is .Case（前导点 enum case 匹配）归 S11
                if (node.TargetCase != null)
                {
                    Error(node.Span, "P3: enum case is pattern is not supported yet (S11)");
                    return null;
                }
                var kind = node.Operator switch
                {
                    "is" => BoundTypeCheckKind.Is,
                    "supers" => BoundTypeCheckKind.Supers,
                    "with" => BoundTypeCheckKind.With,
                    _ => throw new CompilerInternalException("未知类型检查运算符: " + node.Operator),
                };
                var typeRef = node.TargetType!;
                var span = typeRef.Span ?? node.Span;
                // 静态形态试探：按类型引用解析（不落袋；失败返回 ErrorType
                // 毒化符号——TypeSymbol 子类，必须显式排除才落入动态形态）。
                // T? 目标包装 Nullable\<T\>（与 NameResolver.ResolveTypeReference
                // 同规则）
                var probed = names.ResolveSymbolPath(typeRef.TypeSymbol.symbol, ctx,
                    declaringType, method, allowImports: true, reportErrors: false, span: null);
                if (probed is GenericParameterSymbol)
                {
                    Error(span, "P3: generic type parameters are not supported yet (S9)");
                    return null;
                }
                if (probed is TypeSymbol targetType && probed is not ErrorTypeSymbol)
                {
                    if (typeRef.IsNullable)
                    {
                        targetType = unit.Symbols.GetNullable(targetType);
                    }
                    // with 的静态目标必须声明为 wrapper（is/supers 任意类型）
                    if (kind == BoundTypeCheckKind.With && targetType.Kind != TypeKind.Wrapper)
                    {
                        Error(span, $"'with' target must be a wrapper type: " +
                            $"'{TypeDisplay(targetType)}'");
                        return null;
                    }
                    if (operand == null) return null;
                    return new BoundTypeCheckExpression(node, kind, operand, targetType,
                        null, B.Bool);
                }
                // 动态形态：右侧按值绑定，值必须承载 Type\<T\>（BIL §12.3
                // .indirect 指令的 TYPEID_VAR 操作数）；ErrorType 毒化静默放行
                var targetValue = BindTypeCheckTargetValue(node, typeRef, scope,
                    out var valueFound);
                if (targetValue == null)
                {
                    // 值符号命中但绑定失败时诊断已落袋，不重复报
                    if (!valueFound)
                    {
                        Error(span, TypeCheckTargetMessage(node));
                    }
                    return null;
                }
                if (targetValue.Type is not ErrorTypeSymbol
                    && !ReferenceEquals(targetValue.Type.ConstructedFrom, B.TypeDefinition))
                {
                    Error(span, TypeCheckTargetMessage(node));
                    return null;
                }
                if (operand == null) return null;
                return new BoundTypeCheckExpression(node, kind, operand, null, targetValue,
                    B.Bool);
            }

            // 动态形态右侧的值绑定（不落袋纯查找，命中后正常构造值引用 bound
            // 节点）：单段名 = 局部 → 参数 → 字段（FindField 全链）；多段路径 =
            // 容器（前 N-1 段静默解析）+ 末段字段。路径带泛型实参按未命中处理
            // （使用侧泛型归 S9）。valueFound = 是否有值符号命中（命中但绑定
            // 失败时诊断已落袋，调用方不再重复报）
            private BoundExpression? BindTypeCheckTargetValue(TypeCheckExpressionASTNode node,
                TypeReferenceASTNode typeRef, Scope scope, out bool valueFound)
            {
                valueFound = false;
                var elements = typeRef.TypeSymbol.symbol.elements;
                if (elements.Any(e => e.generics.Count > 0)) return null;
                var span = typeRef.Span ?? node.Span;
                if (elements.Count == 1)
                {
                    var name = elements[0].name;
                    var local = scope.Lookup(name);
                    if (local != null)
                    {
                        valueFound = true;
                        if (!assigned.Contains(local))
                        {
                            Error(span, $"Use of unassigned local variable '{name}'");
                        }
                        // 源码局部 Type 恒非空（同 BindPath 单段分支）
                        return new BoundValueReferenceExpression(typeRef, local, local.Type!);
                    }
                    var parameter = method.Parameters.FirstOrDefault(p => p.Name == name);
                    if (parameter != null)
                    {
                        valueFound = true;
                        if (parameter.Type is not TypeSymbol paramType)
                        {
                            Error(span, "P3: generic type parameters are not supported yet (S9)");
                            return null;
                        }
                        return new BoundValueReferenceExpression(typeRef, parameter, paramType);
                    }
                    var field = FindField(name);
                    if (field == null) return null;
                    valueFound = true;
                    return BindFieldReference(typeRef, field);
                }
                // 多段：前 N-1 段解析为容器（静默——失败由调用方统一诊断），
                // 末段查字段成员（实例字段命中由 BindFieldReference 补 this）
                var head = new Symbol();
                for (int i = 0; i < elements.Count - 1; i++)
                {
                    head.elements.Add(new SymbolElement { name = elements[i].name });
                }
                var container = names.ResolveSymbolPath(head, ctx, declaringType, method,
                    allowImports: true, reportErrors: false, span: null);
                if (container is ErrorTypeSymbol) return null;
                if (FindMember(container, elements[^1].name) is not FieldSymbol memberField)
                {
                    return null;
                }
                valueFound = true;
                return BindFieldReference(typeRef, memberField);
            }

            // 动态形态失败（非类型也非 Type\<T\> 值）的统一诊断消息（附路径原文）
            private static string TypeCheckTargetMessage(TypeCheckExpressionASTNode node)
            {
                return $"right side of '{node.Operator}' must be a type or " +
                    $"a Type\\<T\\> value: " +
                    $"'{NameResolver.PathText(node.TargetType!.TypeSymbol.symbol)}'";
            }

            // typeOf（BIL §12.5）：双形态——值形态取操作数的运行时实际类型，
            // 静态定型 Type\<操作数静态类型\>；类型形态取类型本身的 Type 值，
            // 静态定型 Type\<T\>。操作数解析顺序：先值后类型（单段裸名两可时
            // 值优先——局部/参数/字段遮蔽同名类型）
            private BoundExpression? BindTypeOf(TypeOfExpressionASTNode node, Scope scope)
            {
                // 单段裸名路径（无泛型/无后缀/无段，this 除外）做不落袋分类；
                // 其余形态一律值形态直通
                if (node.Operand.Expression is PathExpressionASTNode path
                    && path.Head.Name != null && path.Head.Name != "this"
                    && path.Head.GenericArguments.Count == 0 && path.Head.Suffixes.Count == 0
                    && path.Segments.Count == 0)
                {
                    var name = path.Head.Name;
                    bool isValue = scope.Lookup(name) != null
                        || method.Parameters.Any(p => p.Name == name)
                        || FindField(name) != null;
                    if (!isValue)
                    {
                        // 值未命中 → 试探类型解析（不落袋），命中即类型形态
                        var head = new Symbol();
                        head.elements.Add(new SymbolElement { name = name });
                        var probed = names.ResolveSymbolPath(head, ctx, declaringType,
                            method, allowImports: true, reportErrors: false, span: null);
                        if (probed is GenericParameterSymbol)
                        {
                            Error(path.Span ?? node.Span,
                                "P3: generic type parameters are not supported yet (S9)");
                            return null;
                        }
                        // 失败返回 ErrorType 毒化符号（TypeSymbol 子类，必须
                        // 显式排除才会落入下方 BindPath 的「未解析」诊断）
                        if (probed is TypeSymbol targetType && probed is not ErrorTypeSymbol)
                        {
                            return new BoundTypeOfExpression(node, null, targetType,
                                TypeOfResultType(targetType));
                        }
                        // 值/类型都未命中：落入下方 BindPath 自然报「未解析」
                    }
                    var bound = BindPath(path, scope);
                    if (bound == null) return null;
                    return new BoundTypeOfExpression(node, bound, null,
                        TypeOfResultType(bound.Type));
                }
                var value = BindExpression(node.Operand.Expression, scope);
                if (value == null) return null;
                return new BoundTypeOfExpression(node, value, null,
                    TypeOfResultType(value.Type));
            }

            // typeOf 结果类型：Type\<T\> 构造类型；ErrorType 毒化静默
            // （结果沿用 ErrorType，同 BindCast）
            private TypeSymbol TypeOfResultType(TypeSymbol element)
            {
                return element is ErrorTypeSymbol
                    ? element
                    : unit.Symbols.GetConstructedType(B.TypeDefinition, element);
            }

            // ===== 循环（S7c-1，SYNTAX §7.3/§7.4）=====

            // while/do-while 绑定：条件 bool 检查；循环体绑定前施工壳压入
            // 循环标签栈（体内 break/continue 经引用命中），体绑完弹栈回填。
            // definite assignment（保守规则，S7c-1 定稿）：
            // - while 后 = before（体可能零次执行；条件的赋值效果同样保守
            //   丢弃——条件求值在循环内，不纳入循环后状态）；
            // - do-while 后 = 体尾集合（体至少执行一次；条件在体后求值，
            //   其赋值效果保守丢弃）。
            // break/continue 后同块语句不做特殊流处理（按顺序继续绑定，
            // 不截断、不改 assigned；不精确处登记为 S7c 技术债）
            private BoundStatement? BindLoop(LoopStatementASTNode node, Scope scope)
            {
                if (node.Kind == LoopKind.For)
                {
                    return BindForLoop(node, scope);
                }
                // While/DoWhile 必有条件（Parser 不变量；For 已提前返回）
                if (node.Condition == null)
                {
                    throw new CompilerInternalException(
                        "while/do-while 循环缺条件（Parser 不变量破坏）");
                }
                var shell = new BoundLoop(node, node.Kind, node.Label);
                if (node.Kind == LoopKind.While)
                {
                    var before = new HashSet<LocalSymbol>(assigned);
                    var condition = BindExpression(node.Condition.Expression, scope);
                    CheckBoolCondition(node.Condition, node.Span, condition, "loop");
                    loops.Push(shell);
                    try
                    {
                        shell.Body = BindBlock(node.Body, scope);
                    }
                    finally
                    {
                        loops.Pop();
                    }
                    RestoreAssigned(before);
                    if (condition == null) return null;
                    shell.Condition = condition;
                    return shell;
                }
                // DoWhile：体先行（至少一次），条件在体后
                loops.Push(shell);
                try
                {
                    shell.Body = BindBlock(node.Body, scope);
                }
                finally
                {
                    loops.Pop();
                }
                var bodyAssigned = new HashSet<LocalSymbol>(assigned);
                var revCondition = BindExpression(node.Condition.Expression, scope);
                CheckBoolCondition(node.Condition, node.Span, revCondition, "loop");
                RestoreAssigned(bodyAssigned);
                if (revCondition == null) return null;
                shell.Condition = revCondition;
                return shell;
            }

            // break/continue：无标签命中循环标签栈栈顶（最内层），栈空即
            // 循环外使用（诊断）；有标签沿栈从内向外查 named 命中（穿透
            // 值块/嵌套块命中外层循环合法，BIL §16.5），未命中即未定义标签
            private BoundStatement? BindLoopControl(LoopControlStatementASTNode node)
            {
                BoundLoop? target = null;
                if (node.Label == null)
                {
                    if (loops.Count > 0) target = loops.Peek();
                }
                else
                {
                    foreach (var loop in loops)
                    {
                        if (loop.Label == node.Label)
                        {
                            target = loop;
                            break;
                        }
                    }
                }
                if (target == null)
                {
                    var keyword = node.IsBreak ? "break" : "continue";
                    Error(node.Span, node.Label == null
                        ? $"'{keyword}' outside of a loop"
                        : $"Undefined loop label: '{node.Label}'");
                    return null;
                }
                return new BoundLoopControl(node, node.IsBreak, target);
            }

            // ===== for 双形态（S7c-2，SYNTAX §7.3/§13.2）=====

            // - 范围循环 `for (i in a to b)`（RangeTo 非 null）：a、b 类型
            //   一致后在 a 的类型上解析实例 operator EnumerateInRange（含
            //   ext 注册——P2 已挂目标类型成员表），Iterable =
            //   BoundInstanceCallExpression{a, op, [b]}；
            // - for-each `for (item in collection)`：Iterable = 集合表达式；
            // 两形态汇合于 for-each 协议判定：Iterable 类型实现
            // core.collections::IEnumerable\<TItem\>（沿接口表找该定义的
            // 构造取实参），协议三方法符号挂到 BoundLoop（P4 不做名字分析，
            // ARCH §11.3 纪律）；循环变量 const 局部（只读默认，规范未明，
            // M48 登记）；DA：for 后 = before（体可能零次执行）
            private BoundStatement? BindForLoop(LoopStatementASTNode node, Scope scope)
            {
                if (node.VariableName == null || node.Iterable == null)
                {
                    throw new CompilerInternalException(
                        "for 循环缺循环变量或迭代源（Parser 不变量破坏）");
                }
                var before = new HashSet<LocalSymbol>(assigned);
                BoundExpression? iterable;
                if (node.RangeTo != null)
                {
                    var from = BindExpression(node.Iterable.Expression, scope);
                    var to = BindExpression(node.RangeTo.Expression, scope);
                    if (from == null || to == null) { RestoreAssigned(before); return null; }
                    // 毒化静默：任一侧已失败时不再报次生错误
                    if (from.Type is ErrorTypeSymbol || to.Type is ErrorTypeSymbol)
                    {
                        RestoreAssigned(before);
                        return null;
                    }
                    if (!ReferenceEquals(from.Type, to.Type))
                    {
                        Error(node.RangeTo.Span ?? node.Span,
                            $"Range bounds must have the same type " +
                            $"(got '{TypeDisplay(from.Type)}' and '{TypeDisplay(to.Type)}')");
                        RestoreAssigned(before);
                        return null;
                    }
                    var op = FindInstanceOperator(from.Type, "EnumerateInRange");
                    if (op == null)
                    {
                        Error(node.Span, $"Type '{TypeDisplay(from.Type)}' has no " +
                            "EnumerateInRange operator (required by range for loop)");
                        RestoreAssigned(before);
                        return null;
                    }
                    if (op.ReturnType is not TypeSymbol enumerableType
                        || ContainsGenericParameter(enumerableType))
                    {
                        Error(node.Span,
                            "P3: generic type parameters are not supported yet (S9)");
                        RestoreAssigned(before);
                        return null;
                    }
                    iterable = new BoundInstanceCallExpression(node.Iterable, from, op,
                        new List<BoundExpression> { to }, enumerableType);
                }
                else
                {
                    iterable = BindExpression(node.Iterable.Expression, scope);
                    if (iterable == null) { RestoreAssigned(before); return null; }
                }
                if (iterable.Type is ErrorTypeSymbol) { RestoreAssigned(before); return null; }
                // for-each 协议判定与三方法符号
                var enumerableDef = FindCollectionType("IEnumerable", node.Span);
                var enumeratorDef = FindCollectionType("IEnumerator", node.Span);
                if (enumerableDef == null || enumeratorDef == null)
                {
                    RestoreAssigned(before);
                    return null;
                }
                var itemType = ResolveEnumerableElement(iterable.Type, enumerableDef, node.Span);
                if (itemType == null) { RestoreAssigned(before); return null; }
                var iterate = enumerableDef.Methods.FirstOrDefault(m => m.Name == "iterate");
                var moveNext = enumeratorDef.Methods.FirstOrDefault(m => m.Name == "moveNext");
                var current = enumeratorDef.Methods.FirstOrDefault(m => m.Name == "current");
                if (iterate == null || moveNext == null || current == null)
                {
                    throw new CompilerInternalException(
                        "core.collections 迭代协议成员缺失（stdlib 不变量破坏）");
                }
                var loopVariable = new LocalSymbol(node.VariableName, itemType, isConst: true);
                locals.Add(loopVariable);
                assigned.Add(loopVariable);    // 体入口视为已赋值（每轮由枚举器赋）
                var shell = new BoundLoop(node, LoopKind.For, node.Label)
                {
                    Iterable = iterable,
                    LoopVariable = loopVariable,
                    IterateMethod = iterate,
                    MoveNextMethod = moveNext,
                    CurrentMethod = current,
                };
                loops.Push(shell);
                try
                {
                    shell.Body = BindForBody(node.Body, scope, loopVariable);
                }
                finally
                {
                    loops.Pop();
                }
                RestoreAssigned(before);
                return shell;
            }

            // for 循环体绑定：体块作用域预声明循环变量（仿 BindBlock，
            // 循环变量在体内的遮蔽检查经 DeclaresHere 自然生效）
            private BoundBlock BindForBody(CodeBlockASTNode node, Scope parentScope,
                LocalSymbol loopVariable)
            {
                var scope = new Scope(parentScope);
                scope.Declare(loopVariable);
                var statements = new List<BoundStatement>();
                foreach (var statement in node.Statements)
                {
                    var bound = BindStatement(statement, scope);
                    if (bound != null) statements.Add(bound);
                }
                return new BoundBlock(node, statements);
            }

            // 实例 operator 查找（for 头专用）：receiver 静态类型沿
            // BaseType 链（ext 注册 operator 已在目标类型成员表）
            private static MethodSymbol? FindInstanceOperator(TypeSymbol type, string name)
            {
                for (var t = type; t != null; t = t.BaseType)
                {
                    var hit = t.Methods.FirstOrDefault(m => m.Name == name
                        && !m.IsStatic && m.Kind == MethodKind.Operator
                        && m.Parameters.Count == 1);
                    if (hit != null) return hit;
                }
                return null;
            }

            // core.collections 协议定义查找（stdlib 内嵌源提供；缺席即诊断
            // ——BindUnit 类不带 stdlib 的驱动触不到 for 绑定）
            private TypeSymbol? FindCollectionType(string name, CharRange? span)
            {
                var core = unit.Symbols.GlobalNamespace.ChildNamespaces
                    .FirstOrDefault(n => n.Name == "core");
                var collections = core?.ChildNamespaces
                    .FirstOrDefault(n => n.Name == "collections");
                var type = collections?.Types.FirstOrDefault(t => t.Name == name);
                if (type == null)
                {
                    Error(span, $"P3: core.collections.{name} not found " +
                        "(required by for loop; stdlib missing)");
                }
                return type;
            }

            // for-each 协议判定：type 实现 core.collections::IEnumerable\<TItem\>
            // ——type 自身即该定义的构造（迭代源的静态类型就是接口，如
            // EnumerateInRange 的返回类型），或沿自身与 BaseType 链的接口
            // 表找该定义的构造；取实参 TItem（实参含未替换泛型参数归 S9）；
            // 未实现即诊断
            private TypeSymbol? ResolveEnumerableElement(TypeSymbol type,
                TypeSymbol enumerableDef, CharRange? span)
            {
                if (ReferenceEquals(type.ConstructedFrom, enumerableDef))
                {
                    if (type.TypeArguments![0] is TypeSymbol selfElement
                        && !ContainsGenericParameter(selfElement))
                    {
                        return selfElement;
                    }
                    Error(span, "P3: generic type parameters are not supported yet (S9)");
                    return null;
                }
                for (var t = type; t != null; t = t.BaseType)
                {
                    foreach (var iface in t.Interfaces)
                    {
                        if (!ReferenceEquals(iface.ConstructedFrom, enumerableDef)) continue;
                        if (iface.TypeArguments![0] is TypeSymbol element
                            && !ContainsGenericParameter(element))
                        {
                            return element;
                        }
                        Error(span, "P3: generic type parameters are not supported yet (S9)");
                        return null;
                    }
                }
                Error(span, $"Type '{TypeDisplay(type)}' does not implement " +
                    "core.collections.IEnumerable<T> (required by for loop)");
                return null;
            }

            // ===== 表达式绑定（null 返回 = 已诊断失败，调用方跳过）=====

            private BoundExpression? BindExpression(ASTNode node, Scope scope, TypeSymbol? expectedType = null)
            {
                return node switch
                {
                    LiteralExpressionASTNode literal => BindLiteral(literal, scope, expectedType),
                    PathExpressionASTNode path => BindPath(path, scope),
                    BinaryExpressionASTNode binary => BindBinary(binary, scope),
                    UnaryExpressionASTNode unary => BindUnary(unary, scope),
                    NewExpressionASTNode newExpr => BindNew(newExpr, scope),
                    IfExpressionASTNode ifExpression => BindIfExpression(ifExpression, scope),
                    SwitchExpressionASTNode switchExpression =>
                        BindSwitchExpression(switchExpression, scope),
                    CastExpressionASTNode cast => BindCast(cast, scope),
                    TypeCheckExpressionASTNode typeCheck => BindTypeCheck(typeCheck, scope),
                    TypeOfExpressionASTNode typeOf => BindTypeOf(typeOf, scope),
                    SeqBlockExpressionASTNode seqExpression => BindSeqExpression(seqExpression, scope),
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

            private BoundExpression? BindLiteral(LiteralExpressionASTNode node, Scope scope,
                TypeSymbol? expectedType)
            {
                var literal = node.Literal;
                // 字符串插值（S7f，SYNTAX §3.8）：Parser 已拆分插值段，
                // 绑定即规范化为 toString/拼接调用链
                if (literal is StringLiteralASTNode { InterpolationParts: not null } interpolated)
                {
                    return BindStringInterpolation(node, interpolated, scope);
                }
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
                return new BoundLiteralExpression(node, type);
            }

            // 字符串插值（S7f，SYNTAX §3.8）：段序列绑定为 toString/拼接链——
            // 字面量段按普通 String 字面量绑定（段级子结构，复用字面量机器）；
            // 表达式段非 String 时包 toString() 实例调用（Any 承诺，沿 BaseType
            // 链静态绑定最近声明，运行期虚派发——override 自然生效）；全 String
            // 段按源码顺序左结合折叠为 + 链（String.Add intrinsic，BIL §11.2）。
            // 每个插值段在 bound 树中恰出现一次（求值一次，§3.8）
            private BoundExpression? BindStringInterpolation(LiteralExpressionASTNode node,
                StringLiteralASTNode literal, Scope scope)
            {
                BoundExpression? chain = null;
                foreach (var part in literal.InterpolationParts!)
                {
                    BoundExpression? segment;
                    if (part.Text != null)
                    {
                        segment = BindLiteral(part.Text, scope, expectedType: null);
                        if (segment == null) return null;
                    }
                    else
                    {
                        var value = BindExpression(part.Expression!.Expression, scope);
                        if (value == null) return null;
                        // 毒化静默：段已失败时不再报次生错误
                        if (value.Type is ErrorTypeSymbol) return null;
                        segment = value;
                        if (!ReferenceEquals(value.Type, B.String))
                        {
                            // 非 String 段 → toString()（SYNTAX §3.8 全类型承诺；
                            // Any 恒在 BaseType 链顶，查找不可能落空）
                            var toString = FindInstanceMethods(value.Type, "toString")
                                .FirstOrDefault(m => m.Parameters.Count == 0);
                            if (toString == null)
                            {
                                throw new CompilerInternalException(
                                    $"类型 '{TypeDisplay(value.Type)}' 的 BaseType 链上未找到 toString");
                            }
                            segment = new BoundInstanceCallExpression(node, value, toString,
                                new List<BoundExpression>(), B.String);
                        }
                    }
                    if (chain == null)
                    {
                        chain = segment;
                        continue;
                    }
                    chain = new BoundBinaryExpression(node, BilIntrinsicOp.Add, chain, segment,
                        B.String);
                }
                // HasInterpolation ⇒ 至少一个表达式段，chain 恒非空
                return chain;
            }

            private TypeSymbol NullLiteralError(ASTNode node)
            {
                Error(node.Span, "null requires a nullable type context");
                return unit.Symbols.ErrorType;
            }

            // 路径表达式（M42 统一形态）的值位置绑定。
            // 形态分派：this 首段 → this 路径；纯调用形态 → 直接/实例调用；
            // 纯值路径（无后缀）→ 局部/参数/宿主与命名空间字段/容器成员；
            // 首段为值的多段 → 实例链上色（S7c-2）；其余（表达式底座/索引/
            // 安全访问/wrapper）报归口诊断。
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
                // this 首段（S7c-2）：值位置 this 或实例链起点
                if (node.Head.Name == "this")
                {
                    return BindThisPath(node, scope);
                }
                // 纯调用形态 → 直接调用（静态/全局）或实例调用（首段为值）
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
                    if (binding.Receiver != null)
                    {
                        return new BoundInstanceCallExpression(node, binding.Receiver,
                            binding.Method, binding.Arguments,
                            (TypeSymbol)binding.Method.ReturnType!);
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
                if (node.Segments.Any(s => s.Connector == PathConnector.Colon))
                {
                    Error(node.Span, "P3: wrapper access is not supported yet (S11)");
                    return null;
                }
                // S7f：含 SafeDot 段的路径走实例链（BindInstanceChain 的 SafeDot
                // 分派）；首段带后缀（foo()?.bar 等表达式结果底座）仍归 S8
                bool hasSafeDot = node.Segments.Any(s => s.Connector == PathConnector.SafeDot);
                if (hasSafeDot && node.Head.Suffixes.Count > 0)
                {
                    Error(node.Span, "P3: instance member access is not supported yet (S8)");
                    return null;
                }
                if (!hasSafeDot
                    && (node.Head.Suffixes.Count > 0 || node.Segments.Any(s => s.Suffixes.Count > 0)))
                {
                    Error(node.Span, "P3: instance member access is not supported yet (S8)");
                    return null;
                }
                // 纯值路径：单段查找序 局部 → 参数 → 命名空间链字段 → 通配 import；
                // 多段 = 容器 + 末段成员
                if (node.Segments.Count == 0)
                {
                    var name = node.Head.Name!;
                    // switch pattern 占位（S7d）：占位栈非空时 _ 命中栈顶
                    // selector（嵌套 switch 逐层向内；栈空 = _ 不在 pattern
                    // 上下文，落普通查找报未定义名）
                    if (name == "_" && switchSelectors.Count > 0)
                    {
                        var placeholderSelector = switchSelectors.Peek();
                        return new BoundSwitchPlaceholderExpression(node,
                            placeholderSelector, placeholderSelector.Type);
                    }
                    var local = scope.Lookup(name);
                    if (local != null)
                    {
                        if (!forAssignment && !assigned.Contains(local))
                        {
                            Error(node.Span, $"Use of unassigned local variable '{name}'");
                        }
                        // 源码局部 Type 恒非空（null 是 P4a 合成 .breakid
                        // 局部的特例，P3 不可能遇到）
                        return new BoundValueReferenceExpression(node, local, local.Type!);
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
                // 多段：首段命中局部/参数 → 实例链上色（S7c-2）；
                // 否则前 N-1 段解析为容器，末段查成员
                var segments = PathSegmentNames(node);
                var headLocal = scope.Lookup(segments[0]);
                var headParameter = headLocal == null
                    ? method.Parameters.FirstOrDefault(p => p.Name == segments[0]) : null;
                if (headLocal != null || headParameter != null)
                {
                    BoundExpression headReceiver;
                    if (headLocal != null)
                    {
                        if (!forAssignment && !assigned.Contains(headLocal))
                        {
                            Error(node.Span,
                                $"Use of unassigned local variable '{segments[0]}'");
                        }
                        // 源码局部 Type 恒非空（同单段分支）
                        headReceiver = new BoundValueReferenceExpression(node, headLocal,
                            headLocal.Type!);
                    }
                    else
                    {
                        if (headParameter!.Type is not TypeSymbol headParamType)
                        {
                            Error(node.Span,
                                "P3: generic type parameters are not supported yet (S9)");
                            return null;
                        }
                        headReceiver = new BoundValueReferenceExpression(node, headParameter,
                            headParamType);
                    }
                    return BindInstanceChain(node, headReceiver, node.Segments, scope);
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
                // 泛型字段类型最小替换（S7f）：实例字段以宿主（method.Owner）
                // 为 receiver 链取构造实参；全局/static 字段声明类型不含泛型
                // 参数（宿主非泛型或已具体），原样直通
                var fieldType = SubstituteFieldType(field, method.Owner);
                if (fieldType == null || ContainsGenericParameter(fieldType))
                {
                    Error(node.Span, "P3: generic type parameters are not supported yet (S9)");
                    return null;
                }
                if (field.Owner != null && !field.IsStatic)
                {
                    // 实例字段（S7c-2）：当前上下文有 this（实例方法/ext 方法
                    // 体内，method.Owner 统一承载宿主）→ this.field；静态
                    // 上下文（static 方法/全局函数）→ 诊断
                    if (method.Owner != null && !method.IsStatic)
                    {
                        return new BoundFieldAccessExpression(node,
                            new BoundThisExpression(node, method.Owner), field, fieldType);
                    }
                    Error(node.Span, $"P3: instance field '{field.Name}' requires a receiver" +
                        " ('this' is not available in a static context)");
                    return null;
                }
                return new BoundFieldReferenceExpression(node, field, fieldType);
            }

            private BoundExpression? BindBinary(BinaryExpressionASTNode node, Scope scope)
            {
                // if? 空值回退（S7f，SYNTAX §3.4）：不走 intrinsic 键查询
                if (node.Operator == "if?")
                {
                    return BindNullFallback(node, scope);
                }
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

            // if? 空值回退（S7f，SYNTAX §3.4）：左操作数必须 Nullable<T>，
            // 右操作数（回退值）必须可赋值到 T，结果类型 T；右操作数延迟求值
            // （P4a 以 if 结构保证）
            private BoundExpression? BindNullFallback(BinaryExpressionASTNode node, Scope scope)
            {
                var left = BindExpression(node.Left.Expression, scope);
                if (left == null) return null;
                if (left.Type is ErrorTypeSymbol) return null;
                if (left.Type.ConstructedFrom != B.NullableDefinition
                    || left.Type.TypeArguments![0] is not TypeSymbol element)
                {
                    Error(node.Left.Span ?? node.Span,
                        $"Operator 'if?' requires a nullable left operand " +
                        $"(got '{TypeDisplay(left.Type)}')");
                    return null;
                }
                var right = BindExpression(node.Right.Expression, scope, element);
                if (right == null) return null;
                // 毒化静默：任一侧已失败时不再报次生错误
                if (right.Type is ErrorTypeSymbol) return null;
                if (!IsAssignable(right.Type, element))
                {
                    Error(node.Right.Span ?? node.Span,
                        $"Null fallback must be assignable to '{TypeDisplay(element)}' " +
                        $"(got '{TypeDisplay(right.Type)}')");
                    return null;
                }
                return new BoundNullFallbackExpression(node, left, right, element);
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
                    case BoundFieldAccessExpression:
                        // 参数与全局/实例字段：同赋值的放行规则
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
                // 多段首段为值（局部/参数）→ 实例调用形态（S7c-2）
                if (calleeSegments.Count > 1
                    && (scope.Lookup(calleeSegments[0]) != null
                        || method.Parameters.Any(p => p.Name == calleeSegments[0])))
                {
                    return BindInstanceCallForm(node, calleeSegments, arguments, scope);
                }
                var callee = BindCallee(node, calleeSegments, arguments.Count, scope);
                if (callee == null) return null;
                var (calleeMethod, receiver) = callee.Value;
                if (calleeMethod.ReturnType != null && ContainsGenericParameter(calleeMethod.ReturnType))
                {
                    Error(node.Span, "P3: generic type parameters are not supported yet (S9)");
                    return null;
                }
                var boundArguments = BindArguments(calleeMethod, arguments, scope, node.Span);
                if (boundArguments == null) return null;
                return new CallBinding
                {
                    Method = calleeMethod,
                    Arguments = boundArguments,
                    IsVoid = calleeMethod.ReturnType == null,
                    Receiver = receiver,
                };
            }

            private (MethodSymbol Method, BoundExpression? Receiver)? BindCallee(
                ASTNode node, List<string> calleeSegments, int argumentCount, Scope scope)
            {
                var pathText = string.Join(".", calleeSegments);
                List<MethodSymbol> candidates;
                if (calleeSegments.Count == 1)
                {
                    candidates = FindMethods(calleeSegments[0]);
                }
                else
                {
                    // 首段为值的多段已由 BindCall 分流（实例调用形态）；
                    // 此处前 N-1 段必为容器
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
                var selected = MatchSingleCandidate(node, candidates, argumentCount);
                if (selected == null) return null;
                if (selected.Owner != null && !selected.IsStatic)
                {
                    // 实例方法（S7c-2）：当前上下文有 this（实例方法/ext 方法
                    // 体内）→ 补 this receiver；静态上下文 → 诊断
                    if (method.Owner != null && !method.IsStatic)
                    {
                        return (selected, new BoundThisExpression(node, method.Owner));
                    }
                    Error(node.Span, $"P3: instance method '{selected.Name}' requires a receiver" +
                        " ('this' is not available in a static context)");
                    return null;
                }
                return (selected, null);
            }

            // 候选方法的唯一匹配（无重载直接调用；S7c-2 提取共享——
            // 静态/全局路径与实例链路径同一规则）：泛型方法归 S9；
            // 按实参个数唯一匹配；多匹配归 S8 ranking
            private MethodSymbol? MatchSingleCandidate(ASTNode node,
                List<MethodSymbol> candidates, int argumentCount)
            {
                candidates = candidates.Where(m => m.GenericParameters.Count == 0).ToList();
                if (candidates.Count == 0)
                {
                    Error(node.Span, "P3: generic calls are not supported yet (S9)");
                    return null;
                }
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
                return matched[0];
            }

            // ===== 实例成员（S7c-2，SYNTAX §9）=====

            // this 路径：值位置 this（Type = 宿主类型，method.Owner 统一
            // 承载——普通成员为声明类型，ext 方法为目标类型）或实例链起点。
            // 静态上下文（static 方法/全局函数）不可用
            private BoundExpression? BindThisPath(PathExpressionASTNode node, Scope scope)
            {
                if (method.Owner == null || method.IsStatic)
                {
                    Error(node.Span, "P3: 'this' is not available in a static context");
                    return null;
                }
                // this 自身的后缀（this(...) / this[...]）：无意义形态
                if (node.Head.Suffixes.Count > 0)
                {
                    Error(node.Span, "P3: instance member access is not supported yet (S8)");
                    return null;
                }
                BoundExpression receiver = new BoundThisExpression(node, method.Owner);
                if (node.Segments.Count == 0)
                {
                    return receiver;
                }
                return BindInstanceChain(node, receiver, node.Segments, scope);
            }

            // 实例成员链上色：首段已绑出 receiver，段序列沿 receiver 静态
            // 类型逐段上色——段带恰好一个 Call 后缀 → 实例方法调用；无后缀
            // → 实例字段访问；其余（Index/多后缀）归口 S8。返回链末端表达式
            private BoundExpression? BindInstanceChain(ASTNode node, BoundExpression receiver,
                IReadOnlyList<PathSegmentASTNode> chainSegments, Scope scope)
            {
                foreach (var segment in chainSegments)
                {
                    // 毒化静默：receiver 已失败时不再报次生错误
                    if (receiver.Type is ErrorTypeSymbol) return null;
                    BoundExpression? next;
                    if (segment.Connector == PathConnector.SafeDot)
                    {
                        // 安全访问段（S7f，SYNTAX §3.4）
                        next = BindSafeSegment(segment, receiver, scope);
                    }
                    else if (segment.Connector == PathConnector.Colon)
                    {
                        Error(segment.Span, "P3: wrapper access is not supported yet (S11)");
                        return null;
                    }
                    else
                    {
                        // 普通段：可空值不提供隐式成员访问（须逐段标注 ?.，§3.4）
                        if (receiver.Type.ConstructedFrom == B.NullableDefinition)
                        {
                            Error(segment.Span,
                                $"Member '{segment.Name}' cannot be accessed on nullable type " +
                                $"'{TypeDisplay(receiver.Type)}'; use '?.' for safe access");
                            return null;
                        }
                        next = BindInstanceSegment(segment, receiver, scope);
                        if (next == null) return null;
                    }
                    if (next == null) return null;
                    receiver = next;
                }
                return receiver;
            }

            // 普通实例段（无后缀 → 字段；恰一个 Call 后缀 → 实例方法调用）
            private BoundExpression? BindInstanceSegment(PathSegmentASTNode segment,
                BoundExpression receiver, Scope scope)
            {
                if (segment.Suffixes.Count == 0)
                {
                    return BindInstanceFieldAccess(segment, receiver, segment.Name);
                }
                if (segment.Suffixes.Count == 1
                    && segment.Suffixes[0].Kind == PathSuffixKind.Call)
                {
                    var call = BindInstanceMethodCall(segment, receiver, segment.Name,
                        segment.Suffixes[0].Arguments!, scope);
                    if (call == null) return null;
                    if (call.IsVoid)
                    {
                        Error(segment.Span, $"Method '{call.Method.Name}' has no result " +
                            "(void) and cannot be used as a value");
                        return null;
                    }
                    return new BoundInstanceCallExpression(segment, receiver,
                        call.Method, call.Arguments, (TypeSymbol)call.Method.ReturnType!);
                }
                Error(segment.Span,
                    "P3: instance member access is not supported yet (S8)");
                return null;
            }

            // 安全访问段（S7f，SYNTAX §3.4）：receiver 必须 Nullable<T>；段在
            // 非空 T 上绑定（占位叶子承载 unwrap 后的 receiver，P4a 物化替换）；
            // 结果类型：成员类型已可空则原样（不二次包装），否则包 Nullable
            private BoundExpression? BindSafeSegment(PathSegmentASTNode segment,
                BoundExpression receiver, Scope scope)
            {
                if (receiver.Type.ConstructedFrom != B.NullableDefinition
                    || receiver.Type.TypeArguments![0] is not TypeSymbol element)
                {
                    Error(segment.Span, $"Safe access '?.' requires a nullable receiver " +
                        $"(got '{TypeDisplay(receiver.Type)}')");
                    return null;
                }
                var placeholder = new BoundSafeAccessReceiverExpression(segment, element);
                var access = BindInstanceSegment(segment, placeholder, scope);
                if (access == null) return null;
                var resultType = access.Type.ConstructedFrom == B.NullableDefinition
                    ? access.Type
                    : unit.Symbols.GetNullable(access.Type);
                return new BoundSafeAccessExpression(segment, receiver, placeholder, access,
                    resultType);
            }

            // 实例调用形态（首段为值的多段纯调用）：首段绑 receiver，中间段
            // 沿 receiver 类型上色（TryGetCallForm 保证中间段无后缀，只能是
            // 字段），末段实例方法查找匹配
            private CallBinding? BindInstanceCallForm(ASTNode node, List<string> calleeSegments,
                List<ArgumentASTNode> arguments, Scope scope)
            {
                BoundExpression receiver;
                var headLocal = scope.Lookup(calleeSegments[0]);
                if (headLocal != null)
                {
                    if (!assigned.Contains(headLocal))
                    {
                        Error(node.Span,
                            $"Use of unassigned local variable '{calleeSegments[0]}'");
                    }
                    receiver = new BoundValueReferenceExpression(node, headLocal, headLocal.Type!);
                }
                else
                {
                    var headParameter = method.Parameters.First(p => p.Name == calleeSegments[0]);
                    if (headParameter.Type is not TypeSymbol paramType)
                    {
                        Error(node.Span, "P3: generic type parameters are not supported yet (S9)");
                        return null;
                    }
                    receiver = new BoundValueReferenceExpression(node, headParameter, paramType);
                }
                for (int i = 1; i < calleeSegments.Count - 1; i++)
                {
                    var next = BindInstanceFieldAccess(node, receiver, calleeSegments[i]);
                    if (next == null) return null;
                    receiver = next;
                }
                return BindInstanceMethodCall(node, receiver, calleeSegments[^1],
                    arguments, scope);
            }

            // 实例方法调用：receiver 静态类型沿 BaseType 链查找（接口
            // receiver 查接口自身成员；ext 注册成员同路径；访问控制检查
            // 归 S8）。值位置 void 检查由调用方做
            private CallBinding? BindInstanceMethodCall(ASTNode node, BoundExpression receiver,
                string name, List<ArgumentASTNode> arguments, Scope scope)
            {
                var candidates = FindInstanceMethods(receiver.Type, name);
                if (candidates.Count == 0)
                {
                    Error(node.Span, FindInstanceField(receiver.Type, name) != null
                        ? $"'{name}' on type '{TypeDisplay(receiver.Type)}' is not a method"
                        : $"Undefined member '{name}' on type '{TypeDisplay(receiver.Type)}'");
                    return null;
                }
                var selected = MatchSingleCandidate(node, candidates, arguments.Count);
                if (selected == null) return null;
                // 返回类型含未替换泛型参数（泛型接口/泛型类型成员的使用归
                // S9；for 协议内部路径不经此检查——P3 已备好具体类型）
                if (selected.ReturnType != null && ContainsGenericParameter(selected.ReturnType))
                {
                    Error(node.Span, "P3: generic type parameters are not supported yet (S9)");
                    return null;
                }
                var boundArguments = BindArguments(selected, arguments, scope, node.Span);
                if (boundArguments == null) return null;
                return new CallBinding
                {
                    Method = selected,
                    Arguments = boundArguments,
                    IsVoid = selected.ReturnType == null,
                    Receiver = receiver,
                };
            }

            // 实例字段访问：receiver 静态类型沿 BaseType 链查找（接口无
            // 实例字段；ext 注册字段同路径；访问控制检查归 S8）
            private BoundExpression? BindInstanceFieldAccess(ASTNode node,
                BoundExpression receiver, string name)
            {
                var field = FindInstanceField(receiver.Type, name);
                if (field == null)
                {
                    Error(node.Span, FindInstanceMethods(receiver.Type, name).Count > 0
                        ? $"'{name}' on type '{TypeDisplay(receiver.Type)}' is not a field"
                        : $"Undefined member '{name}' on type '{TypeDisplay(receiver.Type)}'");
                    return null;
                }
                if (field.FieldType == null)
                {
                    Error(node.Span, $"P3: field '{field.Name}' has no type annotation " +
                        "(field type inference is not supported yet)");
                    return null;
                }
                // 泛型字段类型的最小替换（S7f 解构场景）：声明类型是宿主泛型
                // 参数时按 receiver 链上的构造类型取实参；取不到具体类型归 S9
                var fieldType = SubstituteFieldType(field, receiver.Type);
                if (fieldType == null || ContainsGenericParameter(fieldType))
                {
                    Error(node.Span, "P3: generic type parameters are not supported yet (S9)");
                    return null;
                }
                return new BoundFieldAccessExpression(node, receiver, field, fieldType);
            }

            // 泛型字段类型最小替换（S7f；通用泛型使用侧归 S9）：
            // 字段声明类型是泛型参数时，沿 receiver 静态类型的 BaseType 链找
            // 字段宿主的构造类型，取同位实参；实参是具体 TypeSymbol 才返回，
            // 否则 null（调用方报 S9）。非泛型参数声明类型原样返回
            private static TypeSymbol? SubstituteFieldType(FieldSymbol field, TypeSymbol? receiverType)
            {
                if (field.FieldType is not GenericParameterSymbol param)
                {
                    return field.FieldType as TypeSymbol;
                }
                for (var t = receiverType; t != null; t = t.BaseType)
                {
                    if (t.ConstructedFrom == null || field.Owner == null
                        || !ReferenceEquals(t.ConstructedFrom, field.Owner))
                    {
                        continue;
                    }
                    var index = t.ConstructedFrom.GenericParameters.IndexOf(param);
                    return index >= 0 && t.TypeArguments![index] is TypeSymbol concrete
                        ? concrete
                        : null;
                }
                return null;
            }

            // 实例方法查找：receiver 静态类型沿 BaseType 链（接口 receiver
            // 即查接口自身，BaseType 为 null 自然终止；ext 注册成员已在目标
            // 类型成员表）。仅 Regular 实例方法——operator 不经点号调用
            // （for 头专用解析），init/getter/setter 归各自里程碑。
            // 构造类型的成员表在其泛型定义上（构造器不复制成员列表，
            // S7f 起经 ConstructedFrom 回退——实参替换在使用侧特判）
            private static List<MethodSymbol> FindInstanceMethods(TypeSymbol type, string name)
            {
                var result = new List<MethodSymbol>();
                for (var t = type; t != null; t = t.BaseType)
                {
                    var owner = t.ConstructedFrom ?? t;
                    result.AddRange(owner.Methods.Where(m => m.Name == name
                        && !m.IsStatic && m.Kind == MethodKind.Regular));
                }
                return result;
            }

            // 实例字段查找：同链（仅实例字段；构造类型回退泛型定义，同 FindInstanceMethods）
            private static FieldSymbol? FindInstanceField(TypeSymbol type, string name)
            {
                for (var t = type; t != null; t = t.BaseType)
                {
                    var owner = t.ConstructedFrom ?? t;
                    var hit = owner.Fields.FirstOrDefault(f => f.Name == name && !f.IsStatic);
                    if (hit != null) return hit;
                }
                return null;
            }

            // 类型含未替换泛型参数（自身是泛型参数，或构造类型的实参递归
            // 含有）——泛型使用侧归 S9 的统一拦截点
            private static bool ContainsGenericParameter(SemanticSymbol type)
            {
                if (type is GenericParameterSymbol) return true;
                return type is TypeSymbol { TypeArguments: { } arguments }
                    && arguments.Any(ContainsGenericParameter);
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

            // 字段查找序：宿主类型成员（S7c-2 落地——method.Owner 沿
            // BaseType 链，ext 方法 Owner = 目标类型；实例字段命中后由
            // BindFieldReference 补 this，与 FindMethods 的宿主优先一致）
            // → 命名空间链（文件命名空间及父链，顶端即全局命名空间）
            // → 通配 import 容器字段
            private FieldSymbol? FindField(string name)
            {
                for (var host = method.Owner; host != null; host = host.BaseType)
                {
                    // 构造类型的成员表在其泛型定义上（S7f ConstructedFrom 回退）
                    var owner = host.ConstructedFrom ?? host;
                    var hostHit = owner.Fields.FirstOrDefault(f => f.Name == name);
                    if (hostHit != null) return hostHit;
                }
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

            // 方法查找序：宿主类型成员（method.Owner 沿 BaseType 链——ext
            // 方法 Owner = 目标类型，先于命名空间全局函数；实例方法命中后
            // 由 BindCallee 补 this 或静态性检查拦截）→ 命名空间链 →
            // 通配 import 容器方法
            private List<MethodSymbol> FindMethods(string name)
            {
                var result = new List<MethodSymbol>();
                for (var host = method.Owner; host != null; host = host.BaseType)
                {
                    var owner = host.ConstructedFrom ?? host;
                    result.AddRange(owner.Methods.Where(m => m.Name == name));
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
