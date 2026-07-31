using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler
{
    // P2 声明解析（SEMANTIC_ARCHITECTURE §2，SEMANTIC_ROADMAP S3）：
    // 在 P1 符号壳上填充类型引用与继承图，并完成全部声明侧合法性检查。
    // 七个子任务（本文件内按依赖序执行，序号对应 ROADMAP）：
    //   1. 类型引用解析（字段/参数/返回/基类/接口/约束 Bound/注解名），
    //      含泛型实参递归、T? → Nullable\<T>；失败绑 ErrorTypeSymbol 毒化，
    //      后续用到它的检查一律静默跳过（抑制次生噪音，ARCHITECTURE §8）；
    //   2. 继承 / implements 图 + 循环继承诊断 + 种类与可继承性检查；
    //   3. 修饰符合法性（SYNTAX §3.1.1 / §9.2 / §10 / §14.9 / §16）；
    //   4. rich/shared 单向传染 + 字段闭包检查（§3.1.1 闭包表七行，递归）；
    //   5. 共享安全闸门：全局/静态字段类型必须共享安全（§3.1.1 闸门 1）；
    //   6. 泛型约束声明侧检查（Target 为泛型参数、with 边界为 wrapper）；
    //   7. ext 成员注册到目标类型；wrapper 适用性（@WrapperTarget 三分类 ×
    //      宿主可内嵌性 × shared 目标矩阵 A–D × interface 实现者传染）。
    //
    // 名字解析查找序（类型引用/注解名/import/ext 目标共用）：
    //   泛型参数（方法 → 宿主类型链）→ 宿主类型链 NestedTypes →
    //   文件命名空间及父链 → 全局命名空间 → import 列表（具名/通配）→
    //   core 命名空间（隐式可见：i32/String/Object 等裸名由此解析）。
    //
    // 明确不做（归后续里程碑）：访问控制使用点检查、重载签名级重复判定、
    // getter/setter 符号与 enum case（S8/S11）、无标注字段类型推断（P3，
    // 其闭包/闸门检查随推断结果在 P3 复核——见 PROGRESS_REPORT 技术债）。
    // P2 结束冻结符号图（SymbolGraph.Freeze）。
    public static class DeclarationResolver
    {
        public static void Resolve(CompilationUnit unit, DeclarationCollection declarations)
        {
            new ResolveSession(unit, declarations).Run();
        }

        // 声明条目：遍历 AST 骨架时收集的「节点 + 符号 + 名字解析上下文」。
        // InGraph = 符号进入了容器成员表（P1 重复声明的符号不在容器内，
        // 解析填充照做但检查阶段跳过，避免对同一声明重复报错）；
        // ext 成员（ExtTargetPath != null）恒为 true（P2 注册后进目标容器）。
        private sealed class DeclEntry
        {
            public ASTNode Node = null!;
            public SemanticSymbol Symbol = null!;
            public FileContext Context = null!;
            public TypeSymbol? DeclaringType;
            public bool InGraph;
        }

        private sealed class ResolveSession
        {
            private readonly CompilationUnit unit;
            private readonly DeclarationCollection declarations;
            private readonly List<DeclEntry> entries = new List<DeclEntry>();
            private readonly List<DeclEntry> typeEntries = new List<DeclEntry>();
            private readonly Dictionary<SemanticSymbol, DeclEntry> entryOfSymbol =
                new Dictionary<SemanticSymbol, DeclEntry>(ReferenceEqualityComparer.Instance);

            public ResolveSession(CompilationUnit unit, DeclarationCollection declarations)
            {
                this.unit = unit;
                this.declarations = declarations;
            }

            public void Run()
            {
                CollectEntries();
                ValidateImports();
                ResolveTypeReferences();
                ResolveInheritance();
                CheckModifiers();
                CheckContagion();
                CheckFieldClosures();
                CheckSharedSafetyGates();
                CheckGenericConstraints();
                ResolveWrapperTargets();
                RegisterExtensions();
                CheckWrapperApplications();
                // P2 结束冻结符号图（ARCHITECTURE §2：P3/P4 只读）
                unit.Symbols.Freeze();
            }

            private void Error(CharRange? span, string message)
            {
                unit.Diagnostics.Error(DiagnosticPhase.P2, span, message);
            }

            // ===== 声明条目收集（含 open/abstract/singleton 标记位写符号）=====

            private void CollectEntries()
            {
                foreach (var file in unit.SourceFiles)
                {
                    var ctx = declarations.FileContextOf(file);
                    foreach (var decl in file.Declarations)
                    {
                        WalkDeclaration(decl, ctx, declaringType: null);
                    }
                }
            }

            private void WalkDeclaration(ASTNode node, FileContext ctx, TypeSymbol? declaringType)
            {
                switch (node)
                {
                    case ClassDeclarationASTNode c:
                        WalkType(c, c.Members, ctx, declaringType);
                        break;
                    case InterfaceDeclarationASTNode i:
                        WalkType(i, i.Members, ctx, declaringType);
                        break;
                    case StructDeclarationASTNode s:
                        WalkType(s, s.Members, ctx, declaringType);
                        break;
                    case EnumStructDeclarationASTNode e:
                        WalkType(e, e.Members, ctx, declaringType);
                        break;
                    case WrapperDeclarationASTNode w:
                        WalkType(w, w.Members, ctx, declaringType);
                        break;
                    case VariableDeclarationASTNode v:
                        if (declarations.SymbolOf(v) is FieldSymbol field)
                        {
                            AddEntry(v, field, ctx, declaringType, InContainer(field, declaringType, ctx));
                        }
                        break;
                    case CallableDeclarationASTNode fn:
                        if (declarations.SymbolOf(fn) is MethodSymbol method)
                        {
                            AddEntry(fn, method, ctx, declaringType, InContainer(method, declaringType, ctx));
                        }
                        break;
                }
            }

            private void WalkType(ASTNode node, List<ASTNode> members, FileContext ctx, TypeSymbol? declaringType)
            {
                var type = (TypeSymbol)declarations.SymbolOf(node)!;
                // open/abstract/singleton 标记位写符号（供可继承性判定与后续 pass 消费）
                var modifiers = ModifiersOf(node);
                type.IsOpen = modifiers.Contains(Keywords.OPEN);
                type.IsAbstract = modifiers.Contains(Keywords.ABSTRACT);
                type.IsSingleton = modifiers.Contains(Keywords.SINGLETON);
                var inGraph = (declaringType?.NestedTypes ?? ctx.Namespace.Types).Contains(type);
                AddEntry(node, type, ctx, declaringType, inGraph);
                typeEntries.Add(entries[^1]);
                foreach (var member in members)
                {
                    WalkDeclaration(member, ctx, type);
                }
            }

            private void AddEntry(ASTNode node, SemanticSymbol symbol, FileContext ctx,
                TypeSymbol? declaringType, bool inGraph)
            {
                // ext 成员 P1 未进容器（待注册），但检查与注册照常进行
                if (symbol is FieldSymbol { ExtTargetPath: not null } ||
                    symbol is MethodSymbol { ExtTargetPath: not null })
                {
                    inGraph = true;
                }
                var entry = new DeclEntry
                {
                    Node = node,
                    Symbol = symbol,
                    Context = ctx,
                    DeclaringType = declaringType,
                    InGraph = inGraph,
                };
                entries.Add(entry);
                entryOfSymbol[symbol] = entry;
            }

            private static bool InContainer(FieldSymbol field, TypeSymbol? declaringType, FileContext ctx)
            {
                return field.ExtTargetPath != null ||
                    (declaringType?.Fields ?? ctx.Namespace.Fields).Contains(field);
            }

            private static bool InContainer(MethodSymbol method, TypeSymbol? declaringType, FileContext ctx)
            {
                return method.ExtTargetPath != null ||
                    (declaringType?.Methods ?? ctx.Namespace.Methods).Contains(method);
            }

            private static List<string> ModifiersOf(ASTNode node) => node switch
            {
                ClassDeclarationASTNode c => c.Modifiers,
                InterfaceDeclarationASTNode i => i.Modifiers,
                StructDeclarationASTNode s => s.Modifiers,
                EnumStructDeclarationASTNode e => e.Modifiers,
                WrapperDeclarationASTNode w => w.Modifiers,
                VariableDeclarationASTNode v => v.Modifiers,
                CallableDeclarationASTNode fn => fn.Modifiers,
                _ => new List<string>(),
            };

            // ===== import 可解析性校验（import/namespace 模块语义义务）=====

            private void ValidateImports()
            {
                foreach (var file in unit.SourceFiles)
                {
                    var ctx = declarations.FileContextOf(file);
                    foreach (var item in ctx.Imports)
                    {
                        var path = item.symbolNode.symbol;
                        // import 路径自身不带泛型实参；解析失败统一在此报一次
                        // （名字解析消费 import 时一律静默，避免二次噪音）
                        var resolved = ResolveSymbolPath(path, ctx, declaringType: null,
                            declaringMethod: null, allowImports: false, reportErrors: false, span: null);
                        if (resolved is ErrorTypeSymbol)
                        {
                            Error(item.symbolNode.Span ?? file.Span,
                                $"Unresolved import: '{PathText(path)}'");
                        }
                    }
                }
            }

            // ===== 子任务 1：类型引用解析（字段 → 方法，init 映射依赖字段类型）=====

            private void ResolveTypeReferences()
            {
                // 先字段（init `_ -> field` 省略类型时沿用字段类型，字段须先就绪）
                foreach (var entry in entries)
                {
                    if (entry.Node is VariableDeclarationASTNode { TypeAnnotation: not null } v)
                    {
                        ((FieldSymbol)entry.Symbol).FieldType = ResolveTypeReference(v.TypeAnnotation, entry);
                    }
                }
                foreach (var entry in entries)
                {
                    if (entry.Node is not CallableDeclarationASTNode fn) continue;
                    var method = (MethodSymbol)entry.Symbol;
                    if (fn.ReturnType != null)
                    {
                        method.ReturnType = ResolveTypeReference(fn.ReturnType, entry);
                    }
                    for (int i = 0; i < fn.Parameters.Parameters.Count; i++)
                    {
                        method.Parameters[i].Type = ResolveParameterType(fn.Parameters.Parameters[i], entry);
                    }
                }
            }

            private SemanticSymbol ResolveParameterType(ParameterASTNode p, DeclEntry entry)
            {
                if (p.Type.TypeSymbol.symbol.elements.Count > 0)
                {
                    return ResolveTypeReference(p.Type, entry);
                }
                // 空类型节点仅出现于 init 映射省略类型（§9.3：沿用字段类型）
                if (p.MappedFieldName == null)
                {
                    Error(p.Span ?? entry.Node.Span, $"Parameter '{p.Name}' is missing a type annotation");
                    return unit.Symbols.ErrorType;
                }
                var field = FindField(entry.DeclaringType, p.MappedFieldName, out var fieldType);
                if (field == null)
                {
                    Error(p.Span ?? entry.Node.Span,
                        $"Init parameter mapping targets unknown field: '{p.MappedFieldName}'");
                    return unit.Symbols.ErrorType;
                }
                if (fieldType == null)
                {
                    Error(p.Span ?? entry.Node.Span,
                        $"Init parameter mapping requires field '{p.MappedFieldName}' to have a type annotation");
                    return unit.Symbols.ErrorType;
                }
                return fieldType;
            }

            // 沿基类链查字段（含继承）；fieldType 按命中处的构造基类代入实参
            private FieldSymbol? FindField(TypeSymbol? type, string name, out SemanticSymbol? fieldType)
            {
                fieldType = null;
                for (var t = type; t != null; t = t.BaseType)
                {
                    var def = t.ConstructedFrom ?? t;
                    if (def.IsBuiltin) return null;
                    var field = def.Fields.FirstOrDefault(f => f.Name == name);
                    if (field != null)
                    {
                        fieldType = Substitute(field.FieldType, def, t);
                        return field;
                    }
                }
                return null;
            }

            // ===== 子任务 2：继承 / implements 图 + 循环继承 =====

            private void ResolveInheritance()
            {
                foreach (var entry in typeEntries)
                {
                    if (!entry.InGraph) continue;
                    var type = (TypeSymbol)entry.Symbol;
                    switch (entry.Node)
                    {
                        case ClassDeclarationASTNode c:
                            if (c.BaseClass != null)
                            {
                                ResolveBaseClass(type, c.BaseClass, entry);
                            }
                            ResolveInterfaces(type, c.Interfaces, entry, "class");
                            break;
                        case StructDeclarationASTNode s:
                            if (s.BaseStruct != null)
                            {
                                ResolveBaseStruct(type, s.BaseStruct, entry);
                            }
                            // §10：struct 只能继承 struct，不能实现接口
                            if (s.Interfaces.Count > 0)
                            {
                                Error(s.Interfaces[0].Span ?? entry.Node.Span,
                                    $"'{type.Name}': structs cannot implement interfaces");
                            }
                            break;
                        case InterfaceDeclarationASTNode i:
                            ResolveInterfaces(type, i.BaseInterfaces, entry, "interface");
                            break;
                        // enum struct / wrapper：固定继承链，无源码基类语法
                    }
                }
            }

            private void ResolveBaseClass(TypeSymbol type, TypeReferenceASTNode baseRef, DeclEntry entry)
            {
                var resolved = ResolveTypeReference(baseRef, entry);
                if (resolved is ErrorTypeSymbol) return;    // 毒化：保持默认基类
                if (resolved is not TypeSymbol baseType)
                {
                    Error(baseRef.Span ?? entry.Node.Span, $"'{type.Name}': base class must be a type");
                    return;
                }
                var def = baseType.ConstructedFrom ?? baseType;
                if (def.Kind != TypeKind.Class)
                {
                    Error(baseRef.Span ?? entry.Node.Span,
                        $"'{type.Name}': a class can only inherit from a class (use 'implements' for interfaces)");
                    return;
                }
                // 可继承性：基类必须 open/abstract；内建 Object 天然可继承
                if (!def.IsBuiltin && !def.IsOpen && !def.IsAbstract)
                {
                    Error(baseRef.Span ?? entry.Node.Span,
                        $"'{type.Name}': base class '{def.Name}' is not open or abstract");
                    return;
                }
                if (CreatesCycle(type, baseType))
                {
                    Error(baseRef.Span ?? entry.Node.Span,
                        $"Circular inheritance involving '{type.Name}'");
                    return;
                }
                type.BaseType = baseType;
            }

            private void ResolveBaseStruct(TypeSymbol type, TypeReferenceASTNode baseRef, DeclEntry entry)
            {
                var resolved = ResolveTypeReference(baseRef, entry);
                if (resolved is ErrorTypeSymbol) return;
                if (resolved is not TypeSymbol baseType)
                {
                    Error(baseRef.Span ?? entry.Node.Span, $"'{type.Name}': base struct must be a type");
                    return;
                }
                var def = baseType.ConstructedFrom ?? baseType;
                // §10：struct 只能继承 struct；可被继承的 struct 必须是 open 的 rich struct
                if (def.Kind != TypeKind.Struct)
                {
                    Error(baseRef.Span ?? entry.Node.Span,
                        $"'{type.Name}': a struct can only inherit from a struct");
                    return;
                }
                if (!def.IsBuiltin && !(def.IsRich && def.IsOpen))
                {
                    Error(baseRef.Span ?? entry.Node.Span,
                        $"'{type.Name}': base struct '{def.Name}' must be an open rich struct");
                    return;
                }
                if (CreatesCycle(type, baseType))
                {
                    Error(baseRef.Span ?? entry.Node.Span,
                        $"Circular inheritance involving '{type.Name}'");
                    return;
                }
                type.BaseType = baseType;
            }

            private void ResolveInterfaces(TypeSymbol type, List<TypeReferenceASTNode> interfaces,
                DeclEntry entry, string keyword)
            {
                foreach (var ifaceRef in interfaces)
                {
                    var resolved = ResolveTypeReference(ifaceRef, entry);
                    if (resolved is ErrorTypeSymbol) continue;
                    if (resolved is not TypeSymbol iface ||
                        (iface.ConstructedFrom ?? iface).Kind != TypeKind.Interface)
                    {
                        Error(ifaceRef.Span ?? entry.Node.Span,
                            $"'{type.Name}': '{keyword}' target must be an interface");
                        continue;
                    }
                    type.Interfaces.Add(iface);
                }
                // interface 继承图的环：DFS 能回到自身即环（报错但保留图，
                // 后续消费 Interfaces 的遍历均为一层，不会死循环）
                if (type.Interfaces.Count > 0 && HasInterfaceCycle(type))
                {
                    Error(entry.Node.Span, $"Circular interface inheritance involving '{type.Name}'");
                }
            }

            private static bool CreatesCycle(TypeSymbol type, TypeSymbol baseType)
            {
                for (var t = baseType; t != null; t = t.BaseType)
                {
                    if (ReferenceEquals(t, type)) return true;
                }
                return false;
            }

            private static bool HasInterfaceCycle(TypeSymbol type)
            {
                var visited = new HashSet<TypeSymbol>();
                var stack = new Stack<TypeSymbol>(type.Interfaces);
                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    if (ReferenceEquals(current, type)) return true;
                    if (!visited.Add(current)) continue;
                    foreach (var next in current.Interfaces)
                    {
                        stack.Push(next);
                    }
                }
                return false;
            }

            // ===== 子任务 3：修饰符合法性 =====

            private void CheckModifiers()
            {
                foreach (var entry in entries)
                {
                    if (!entry.InGraph) continue;
                    var modifiers = ModifiersOf(entry.Node);
                    CheckDuplicateModifiers(modifiers, entry);
                    CheckAccessModifierExclusivity(modifiers, entry);
                    if (entry.Symbol is TypeSymbol type)
                    {
                        CheckTypeModifiers(type, modifiers, entry);
                    }
                    else
                    {
                        CheckMemberModifiers(entry, modifiers);
                    }
                }
            }

            private void CheckDuplicateModifiers(List<string> modifiers, DeclEntry entry)
            {
                foreach (var group in modifiers.GroupBy(m => m))
                {
                    if (group.Count() > 1)
                    {
                        Error(entry.Node.Span, $"Duplicate modifier '{group.Key}'");
                    }
                }
            }

            private void CheckAccessModifierExclusivity(List<string> modifiers, DeclEntry entry)
            {
                var count = modifiers.Count(m =>
                    m == Keywords.PUB || m == Keywords.PRIV || m == Keywords.PROTECTED || m == Keywords.INTERNAL);
                if (count > 1)
                {
                    Error(entry.Node.Span, "Access modifiers are mutually exclusive (pub/protected/internal/priv)");
                }
            }

            private void CheckTypeModifiers(TypeSymbol type, List<string> modifiers, DeclEntry entry)
            {
                var span = entry.Node.Span;
                var rich = modifiers.Contains(Keywords.RICH);
                // rich 仅 struct/enum struct；wrapper 恒 rich 由声明形式隐含（§3.1.1/§14.9）
                if (rich && type.Kind == TypeKind.Class)
                {
                    Error(span, $"'{type.Name}': 'rich' can only be applied to struct/enum struct");
                }
                if (rich && type.Kind == TypeKind.Interface)
                {
                    Error(span, $"'{type.Name}': 'rich' cannot be applied to interface");
                }
                if (rich && type.Kind == TypeKind.Wrapper)
                {
                    Error(span, $"'{type.Name}': 'rich' is implied by the wrapper declaration and must not be written");
                }
                if (modifiers.Contains(Keywords.SHARED) && type.Kind == TypeKind.Interface)
                {
                    Error(span, $"'{type.Name}': 'shared' cannot be applied to interface");
                }
                // shared struct 必 rich（§3.1.1）
                if (type.IsShared && !type.IsRich &&
                    (type.Kind == TypeKind.Struct || type.Kind == TypeKind.EnumStruct))
                {
                    Error(span, $"'{type.Name}': 'shared' struct must also be 'rich'");
                }
                // 非 rich struct 不得 open/abstract（§3.1.1 封闭性）
                if ((type.Kind == TypeKind.Struct || type.Kind == TypeKind.EnumStruct) && !type.IsRich)
                {
                    if (type.IsOpen)
                    {
                        Error(span, $"'{type.Name}': non-rich struct cannot be 'open'");
                    }
                    if (type.IsAbstract)
                    {
                        Error(span, $"'{type.Name}': non-rich struct cannot be 'abstract'");
                    }
                }
                // enum struct 是封闭特例（§10）：不得 open；abstract 天然 open 同禁
                if (type.Kind == TypeKind.EnumStruct && type.IsOpen)
                {
                    Error(span, $"'{type.Name}': enum struct cannot be 'open'");
                }
                if (type.Kind == TypeKind.EnumStruct && type.IsAbstract)
                {
                    Error(span, $"'{type.Name}': enum struct cannot be 'abstract'");
                }
                // open 仅 class/struct（§9.2）；interface 不得 open（wrapper 见下）
                if (type.IsOpen && type.Kind == TypeKind.Interface)
                {
                    Error(span, $"'{type.Name}': 'open' cannot be applied to interface");
                }
                // open × abstract 互斥（§9.2：abstract 天然 open）
                if (type.IsOpen && type.IsAbstract)
                {
                    Error(span, $"'{type.Name}': 'open' and 'abstract' are mutually exclusive");
                }
                // singleton 仅 class、必须 shared（§3.1.1 闸门）、不得 abstract
                if (type.IsSingleton)
                {
                    if (type.Kind != TypeKind.Class)
                    {
                        Error(span, $"'{type.Name}': 'singleton' can only be applied to class");
                    }
                    if (!type.IsShared)
                    {
                        Error(span, $"'{type.Name}': singleton class must also be 'shared'");
                    }
                    if (type.IsAbstract)
                    {
                        Error(span, $"'{type.Name}': 'abstract' and 'singleton' are mutually exclusive");
                    }
                }
                // wrapper 不得 open/abstract/singleton（§14.9）
                if (type.Kind == TypeKind.Wrapper && (type.IsOpen || type.IsAbstract))
                {
                    Error(span, $"'{type.Name}': wrapper cannot be 'open' or 'abstract'");
                }
            }

            private void CheckMemberModifiers(DeclEntry entry, List<string> modifiers)
            {
                // async 仅适用于函数与 lambda（§9.2）
                if (entry.Symbol is FieldSymbol && modifiers.Contains(Keywords.ASYNC))
                {
                    Error(entry.Node.Span, "'async' can only be applied to functions");
                }
                // ext 必须是限定名（§4.4：TargetType.memberName）且只能用于全局声明
                if (modifiers.Contains(Keywords.EXT))
                {
                    var hasQualifiedName = entry.Symbol switch
                    {
                        FieldSymbol f => f.ExtTargetPath != null,
                        MethodSymbol m => m.ExtTargetPath != null,
                        _ => false,
                    };
                    if (!hasQualifiedName)
                    {
                        Error(entry.Node.Span, "'ext' declaration requires a qualified name (TargetType.memberName)");
                    }
                    if (entry.DeclaringType != null)
                    {
                        Error(entry.Node.Span, "'ext' can only be applied to global declarations");
                    }
                }
            }

        // ===== 子任务 4：rich/shared 单向传染 + 字段闭包 =====

        // 闭包表持有者行（SYNTAX §3.1.1 七行；None = 不查：interface、内建类型）
        private enum HolderCategory
        {
            None,
            PlainStruct,        // 非 rich struct / 非 rich enum struct
            RichStruct,
            SharedRichStruct,
            WrapperPlain,       // 非 shared wrapper
            SharedWrapper,
            LocalClass,
            SharedClass,
        }

        // 字段类型分类（闭包表的两列：持有的 Object / 内嵌的 ValueType）
        private enum FieldCategory
        {
            Unknown,            // 泛型参数 / 毒化 / 无标注（跳过检查）
            LocalObject,
            SharedObject,       // shared class 或 T 共享安全的 Nullable\<T\>
            NonRichValue,
            LocalRichValue,
            SharedRichValue,
        }

        private void CheckContagion()
        {
            foreach (var entry in typeEntries)
            {
                if (!entry.InGraph) continue;
                var type = (TypeSymbol)entry.Symbol;
                var baseType = type.BaseType;
                if (baseType == null || baseType.IsBuiltin) continue;
                // 单向传染（§3.1.1）：基类 rich/shared ⇒ 子类必须同标；反向可收紧，
                // 收紧后的合法性由字段闭包检查兜底（CheckFieldClosures 含继承字段）
                var def = baseType.ConstructedFrom ?? baseType;
                if (def.IsShared && !type.IsShared)
                {
                    Error(entry.Node.Span,
                        $"'{type.Name}': base type '{def.Name}' is 'shared', so the derived type must also be 'shared'");
                }
                if (def.IsRich && !type.IsRich)
                {
                    Error(entry.Node.Span,
                        $"'{type.Name}': base type '{def.Name}' is 'rich', so the derived type must also be 'rich'");
                }
            }
        }

        private void CheckFieldClosures()
        {
            foreach (var entry in typeEntries)
            {
                if (!entry.InGraph) continue;
                var type = (TypeSymbol)entry.Symbol;
                var holder = ClassifyHolder(type);
                if (holder == HolderCategory.None) continue;
                var visited = new HashSet<TypeSymbol>();
                foreach (var (name, fieldType) in ClosureFieldsOf(type))
                {
                    CheckClosureField(holder, type, name, fieldType, entry.Node.Span, visited);
                }
            }
        }

        private static HolderCategory ClassifyHolder(TypeSymbol type)
        {
            if (type.IsBuiltin) return HolderCategory.None;
            switch (type.Kind)
            {
                case TypeKind.Interface:
                    return HolderCategory.None;
                case TypeKind.Wrapper:
                    return type.IsShared ? HolderCategory.SharedWrapper : HolderCategory.WrapperPlain;
                case TypeKind.Class:
                    return type.IsShared ? HolderCategory.SharedClass : HolderCategory.LocalClass;
                default:    // Struct / EnumStruct
                    if (!type.IsRich) return HolderCategory.PlainStruct;
                    return type.IsShared ? HolderCategory.SharedRichStruct : HolderCategory.RichStruct;
            }
        }

        private static FieldCategory ClassifyFieldType(SemanticSymbol? fieldType)
        {
            if (fieldType is not TypeSymbol t) return FieldCategory.Unknown;
            if (t is ErrorTypeSymbol) return FieldCategory.Unknown;
            if (t.IsValueTypeBranch)
            {
                if (!t.IsRich) return FieldCategory.NonRichValue;
                return t.IsShared ? FieldCategory.SharedRichValue : FieldCategory.LocalRichValue;
            }
            // Nullable\<T> 按 T 推导（§3.1.2）；T 为泛型参数时安全性未知，跳过
            if (t.ConstructedFrom is { DerivesSharedSafetyFromTypeArgument: true }
                && t.TypeArguments![0] is not TypeSymbol)
            {
                return FieldCategory.Unknown;
            }
            return t.IsSharedSafe() ? FieldCategory.SharedObject : FieldCategory.LocalObject;
        }

        // 直接字段（声明类型原样）+ 沿基类链的继承字段（按构造基类代入实参）
        private IEnumerable<(string Name, SemanticSymbol? FieldType)> ClosureFieldsOf(TypeSymbol type)
        {
            foreach (var f in type.Fields)
            {
                yield return (f.Name, f.FieldType);
            }
            for (var b = type.BaseType; b != null; b = b.BaseType)
            {
                var def = b.ConstructedFrom ?? b;
                if (def.IsBuiltin) yield break;
                foreach (var f in def.Fields)
                {
                    yield return (f.Name, Substitute(f.FieldType, def, b));
                }
            }
        }

        private void CheckClosureField(HolderCategory holder, TypeSymbol holderType, string fieldName,
            SemanticSymbol? fieldType, CharRange? span, HashSet<TypeSymbol> visited)
        {
            // 直接分类违规即报且不再展开（同一字段一处违规报一条，避免直接分类与
            // 实参展开对同一事实重复诊断）；直接分类放行时才需展开实参拦截
            if (CheckDirectClosure(holder, holderType, fieldName, fieldType, span)) return;
            // 泛型实参所展开的字段同样受限（§3.1.1）：用户构造类型字段代入实参递归查
            if (fieldType is TypeSymbol { ConstructedFrom: not null } constructed)
            {
                ExpandConstructedField(holder, holderType, fieldName, constructed, span, visited);
            }
        }

        // 持有者行 × 字段类型分类的直接检查；违规报一条诊断并返回 true
        private bool CheckDirectClosure(HolderCategory holder, TypeSymbol holderType,
            string fieldName, SemanticSymbol? fieldType, CharRange? span)
        {
            return CheckDirectClosure(holder, holderType, fieldName, ClassifyFieldType(fieldType), span, null);
        }

        private bool CheckDirectClosure(HolderCategory holder, TypeSymbol holderType, string fieldName,
            FieldCategory category, CharRange? span, string? viaNote)
        {
            if (category == FieldCategory.Unknown) return false;
            switch (holder)
            {
                case HolderCategory.PlainStruct:
                    if (category == FieldCategory.LocalObject || category == FieldCategory.SharedObject)
                    {
                        Error(span, $"Non-rich struct '{holderType.Name}' cannot hold object field '{fieldName}'{viaNote}");
                        return true;
                    }
                    if (category != FieldCategory.NonRichValue)
                    {
                        Error(span, $"Non-rich struct '{holderType.Name}' cannot embed rich value type field '{fieldName}'{viaNote}");
                        return true;
                    }
                    return false;
                case HolderCategory.SharedClass:
                case HolderCategory.SharedRichStruct:
                case HolderCategory.SharedWrapper:
                    if (category == FieldCategory.LocalObject)
                    {
                        Error(span, $"'{holderType.Name}' is shared and cannot hold local object field '{fieldName}'{viaNote}");
                        return true;
                    }
                    if (category == FieldCategory.LocalRichValue)
                    {
                        Error(span, $"'{holderType.Name}' is shared and cannot embed non-shared rich value type field '{fieldName}'{viaNote}");
                        return true;
                    }
                    return false;
                default:
                    // LocalClass / RichStruct / WrapperPlain：闭包不受限
                    return false;
            }
        }

        private void ExpandConstructedField(HolderCategory holder, TypeSymbol holderType, string fieldName,
            TypeSymbol constructed, CharRange? span, HashSet<TypeSymbol> visited)
        {
            // 引用相等去重：Node\<T> 自嵌套等场景沿展开链收敛
            if (!visited.Add(constructed)) return;
            var def = constructed.ConstructedFrom!;
            // 内建构造（Nullable/Box/Span/Type）的闭包属性由 §3.1.2 特权规则
            // 在 ClassifyFieldType/IsSharedSafe 覆盖，不展开
            if (def.IsBuiltin) return;
            foreach (var f in def.Fields)
            {
                var fieldType = Substitute(f.FieldType, def, constructed);
                var category = ClassifyFieldType(fieldType);
                if (CheckDirectClosure(holder, holderType, fieldName, category, span,
                    $" (via generic argument of '{def.Name}')"))
                {
                    continue;
                }
                if (fieldType is TypeSymbol { ConstructedFrom: not null } inner)
                {
                    ExpandConstructedField(holder, holderType, fieldName, inner, span, visited);
                }
            }
        }

        // 泛型实参代入：字段类型中的泛型参数按构造类型的实参列表替换（递归）
        private SemanticSymbol? Substitute(SemanticSymbol? fieldType, TypeSymbol definition, TypeSymbol constructed)
        {
            if (fieldType == null || ReferenceEquals(definition, constructed)) return fieldType;
            if (fieldType is GenericParameterSymbol gp)
            {
                var index = definition.GenericParameters.IndexOf(gp);
                return index >= 0 ? constructed.TypeArguments![index] : fieldType;
            }
            if (fieldType is TypeSymbol { ConstructedFrom: not null } inner)
            {
                var innerDef = inner.ConstructedFrom!;
                var args = new SemanticSymbol[inner.TypeArguments!.Count];
                for (int i = 0; i < args.Length; i++)
                {
                    args[i] = Substitute(inner.TypeArguments[i], definition, constructed)!;
                }
                return unit.Symbols.GetConstructedType(innerDef, args);
            }
            return fieldType;
        }

        // ===== 子任务 5：共享安全闸门（全局/静态字段）=====

        private void CheckSharedSafetyGates()
        {
            foreach (var entry in entries)
            {
                if (!entry.InGraph || entry.Symbol is not FieldSymbol field) continue;
                // 闸门 1（§3.1.1）：全局变量/常量、静态字段的类型必须共享安全；
                // ext 实例成员注册后是目标类型的实例字段，不受闸门约束
                var gated = field.IsStatic || (field.Owner == null && field.ExtTargetPath == null);
                if (!gated) continue;
                if (field.FieldType is not TypeSymbol type || type is ErrorTypeSymbol) continue;
                // 泛型上下文中的构造类型（含泛型参数实参）安全性未知，跳过
                if (ContainsGenericParameter(type)) continue;
                if (!type.IsSharedSafe())
                {
                    Error(entry.Node.Span,
                        $"Global or static field '{field.Name}' must have a shared-safe type (SYNTAX §3.1.1)");
                }
            }
        }

        private static bool ContainsGenericParameter(SemanticSymbol symbol)
        {
            if (symbol is GenericParameterSymbol) return true;
            if (symbol is TypeSymbol { TypeArguments: not null } t)
            {
                return t.TypeArguments.Any(ContainsGenericParameter);
            }
            return false;
        }

        // ===== 子任务 6：泛型约束声明侧检查 =====

        private void CheckGenericConstraints()
        {
            foreach (var entry in entries)
            {
                if (!entry.InGraph) continue;
                var (list, parameters) = entry switch
                {
                    { Node: ClassDeclarationASTNode c } => (c.GenericParameters, ((TypeSymbol)entry.Symbol).GenericParameters),
                    { Node: InterfaceDeclarationASTNode i } => (i.GenericParameters, ((TypeSymbol)entry.Symbol).GenericParameters),
                    { Node: StructDeclarationASTNode s } => (s.GenericParameters, ((TypeSymbol)entry.Symbol).GenericParameters),
                    { Node: EnumStructDeclarationASTNode e } => (e.GenericParameters, ((TypeSymbol)entry.Symbol).GenericParameters),
                    { Node: WrapperDeclarationASTNode w } => (w.GenericParameters, ((TypeSymbol)entry.Symbol).GenericParameters),
                    { Node: CallableDeclarationASTNode fn } => (fn.GenericParameters, ((MethodSymbol)entry.Symbol).GenericParameters),
                    _ => (null, null),
                };
                if (list == null) continue;
                foreach (var constraint in list.Constraints)
                {
                    // Target 必须是本声明的泛型参数（§3.6：裸标识符即泛型参数）
                    var targetName = BareNameOf(constraint.Target);
                    var parameter = targetName == null ? null : parameters!.FirstOrDefault(p => p.Name == targetName);
                    if (parameter == null)
                    {
                        Error(constraint.Target.Span ?? constraint.Span ?? entry.Node.Span,
                            "Constraint target must be a generic parameter of this declaration");
                        continue;
                    }
                    var bound = ResolveTypeReference(constraint.Bound, entry);
                    if (bound is ErrorTypeSymbol) continue;    // 毒化静默
                    // with 约束的边界必须是 wrapper 类型（§3.6）
                    if (constraint.Kind == GenericConstraintKind.With &&
                        bound is not TypeSymbol { Kind: TypeKind.Wrapper })
                    {
                        Error(constraint.Bound.Span ?? constraint.Span ?? entry.Node.Span,
                            $"'with' constraint bound of '{parameter.Name}' must be a wrapper type");
                        continue;
                    }
                    parameter.Constraints.Add(new GenericConstraintInfo(constraint.Kind, bound));
                }
            }
        }

        private static string? BareNameOf(TypeReferenceASTNode typeRef)
        {
            var elements = typeRef.TypeSymbol.symbol.elements;
            return !typeRef.IsNullable && elements.Count == 1 && elements[0].generics.Count == 0
                ? elements[0].name
                : null;
        }


        // ===== 子任务 7a：wrapper 声明的 @WrapperTarget 解析 =====

        private void ResolveWrapperTargets()
        {
            foreach (var entry in typeEntries)
            {
                if (!entry.InGraph) continue;
                var type = (TypeSymbol)entry.Symbol;
                if (type.Kind != TypeKind.Wrapper || type.IsBuiltin) continue;
                var annotations = ((WrapperDeclarationASTNode)entry.Node).Annotations;
                var targetAnnotation = annotations.FirstOrDefault(IsWrapperTargetAnnotation);
                if (targetAnnotation == null)
                {
                    // §14 三分类是 wrapper 适用性的必备信息，规范示例均显式标注
                    // （推断规则：缺失即诊断，见 PROGRESS_REPORT 技术债）
                    Error(entry.Node.Span,
                        $"Wrapper '{type.Name}' requires @WrapperTarget(.Entity/.Value/.Method)");
                    continue;
                }
                var kind = WrapperTargetKindOf(targetAnnotation);
                if (kind == null)
                {
                    Error(targetAnnotation.Span ?? entry.Node.Span,
                        "@WrapperTarget expects .Entity, .Value or .Method");
                    continue;
                }
                type.WrapperTarget = kind;
            }
        }

        private static bool IsWrapperTargetAnnotation(AnnotationASTNode annotation)
        {
            var elements = annotation.Name.symbol.elements;
            return elements.Count > 0 && elements[^1].name == "WrapperTarget";
        }

        private static WrapperTargetKind? WrapperTargetKindOf(AnnotationASTNode annotation)
        {
            if (annotation.Arguments.Count == 0) return null;
            var value = annotation.Arguments[0].Value;
            if (!value.IsAttached || value.Expression is not EnumCaseExpressionASTNode caseRef)
            {
                return null;
            }
            return caseRef.CaseName switch
            {
                "Entity" => LatteCompiler.WrapperTargetKind.Entity,
                "Value" => LatteCompiler.WrapperTargetKind.Value,
                "Method" => LatteCompiler.WrapperTargetKind.Method,
                _ => null,
            };
        }

        // ===== 子任务 7b：ext 成员注册到目标类型 =====

        private void RegisterExtensions()
        {
            foreach (var symbol in declarations.PendingExtMembers)
            {
                var entry = entryOfSymbol[symbol];
                var extTargetPath = symbol switch
                {
                    FieldSymbol f => f.ExtTargetPath,
                    MethodSymbol m => m.ExtTargetPath,
                    _ => null,
                };
                // 成员位置的 ext（§4.4 只允许全局声明）：诊断已在 CheckMemberModifiers
                // 报过（DeclaringType != null），此处不再注册
                if (entry.DeclaringType != null) continue;
                var target = ResolveDottedPath(extTargetPath!.Split('.'), entry.Context,
                    allowImports: true, reportErrors: true, span: entry.Node.Span);
                if (target is ErrorTypeSymbol) continue;    // 毒化静默
                if (target is not TypeSymbol targetType)
                {
                    Error(entry.Node.Span, $"Extension target '{extTargetPath}' must be a type");
                    continue;
                }
                if (symbol is FieldSymbol field)
                {
                    field.AttachToExtTarget(targetType);
                    targetType.Fields.Add(field);
                }
                else if (symbol is MethodSymbol method)
                {
                    method.AttachToExtTarget(targetType);
                    targetType.Methods.Add(method);
                }
            }
        }

        // ===== 子任务 7c：wrapper 应用检查（类别匹配 + 目标矩阵 + interface 传染）=====

        private void CheckWrapperApplications()
        {
            foreach (var entry in entries)
            {
                if (!entry.InGraph || entry.Node is not IWrapperAttachable attachable) continue;
                foreach (var annotation in attachable.Annotations)
                {
                    if (IsWrapperTargetAnnotation(annotation))
                    {
                        // wrapper 声明上的已在 ResolveWrapperTargets 处理；挂别处即非法
                        if (entry.Symbol is not TypeSymbol { Kind: TypeKind.Wrapper })
                        {
                            Error(annotation.Span ?? entry.Node.Span,
                                "@WrapperTarget can only be applied to wrapper declarations");
                        }
                        continue;
                    }
                    var resolved = ResolveSymbolPath(annotation.Name.symbol, entry.Context,
                        entry.DeclaringType, declaringMethod: null,
                        allowImports: true, reportErrors: true,
                        span: annotation.Name.Span ?? annotation.Span ?? entry.Node.Span);
                    if (resolved is ErrorTypeSymbol) continue;    // 毒化静默
                    if (resolved is not TypeSymbol { Kind: TypeKind.Wrapper } wrapperType)
                    {
                        Error(annotation.Span ?? entry.Node.Span,
                            $"'{PathText(annotation.Name.symbol)}' is not a wrapper type");
                        continue;
                    }
                    // wrapper 自身的 @WrapperTarget 缺失/非法已在声明处报过，此处静默
                    if (wrapperType.WrapperTarget is { } targetKind)
                    {
                        CheckWrapperCategoryMatch(entry, wrapperType, targetKind, annotation);
                    }
                    AppliedWrappersOf(entry.Symbol)?.Add(wrapperType);
                }
            }
            CheckInterfaceWrapperContagion();
        }

        private static List<TypeSymbol>? AppliedWrappersOf(SemanticSymbol symbol) => symbol switch
        {
            TypeSymbol t => t.AppliedWrappers,
            FieldSymbol f => f.AppliedWrappers,
            MethodSymbol m => m.AppliedWrappers,
            _ => null,
        };

        // §14.9：@WrapperTarget 类别 × 目标声明分类 + 宿主可内嵌性 + shared 目标矩阵
        private void CheckWrapperCategoryMatch(DeclEntry entry, TypeSymbol wrapperType,
            WrapperTargetKind targetKind, AnnotationASTNode annotation)
        {
            var span = annotation.Span ?? entry.Node.Span;
            switch (targetKind)
            {
                case WrapperTargetKind.Entity:
                    if (entry.Symbol is not TypeSymbol targetType)
                    {
                        Error(span, $"Entity wrapper '{wrapperType.Name}' can only be applied to type declarations");
                        return;
                    }
                    // 宿主可内嵌性：非 rich struct/enum struct 不能被任何 wrapper 修饰
                    if ((targetType.Kind == TypeKind.Struct || targetType.Kind == TypeKind.EnumStruct)
                        && !targetType.IsRich)
                    {
                        Error(span, $"Non-rich struct '{targetType.Name}' cannot be wrapped (§14.9)");
                    }
                    // shared 矩阵 D：非 shared wrapper 仅修饰非 shared 类型
                    if (!wrapperType.IsShared && targetType.IsShared)
                    {
                        Error(span, $"Non-shared wrapper '{wrapperType.Name}' cannot wrap shared type '{targetType.Name}'");
                    }
                    return;
                case WrapperTargetKind.Value:
                    if (entry.Symbol is not FieldSymbol field)
                    {
                        Error(span, $"Value wrapper '{wrapperType.Name}' can only be applied to fields and variables");
                        return;
                    }
                    CheckValueMethodTarget(wrapperType, span, host: field.Owner,
                        isGlobalOrStatic: field.Owner == null || field.IsStatic,
                        targetDescription: $"field '{field.Name}'");
                    return;
                case WrapperTargetKind.Method:
                    if (entry.Symbol is not MethodSymbol method)
                    {
                        Error(span, $"Method wrapper '{wrapperType.Name}' can only be applied to methods");
                        return;
                    }
                    CheckValueMethodTarget(wrapperType, span, host: method.Owner,
                        isGlobalOrStatic: method.Owner == null || method.IsStatic,
                        targetDescription: $"method '{method.Name}'");
                    return;
            }
        }

        // Value/Method wrapper 的共享判定（矩阵 A/B 同构）：
        // 全局/静态目标只允许 shared wrapper；非 shared wrapper 还要求宿主类型非 shared；
        // 非 rich struct 的实例字段/方法不能挂 wrapper（宿主须能内嵌 rich struct）。
        // （栈上变量属矩阵 C 恒合法，但栈上声明不进 P1/P2，归 P3。）
        private void CheckValueMethodTarget(TypeSymbol wrapperType, CharRange? span,
            TypeSymbol? host, bool isGlobalOrStatic, string targetDescription)
        {
            if (isGlobalOrStatic)
            {
                if (!wrapperType.IsShared)
                {
                    Error(span, $"Non-shared wrapper '{wrapperType.Name}' cannot wrap global or static {targetDescription}");
                }
                return;
            }
            if (host != null &&
                (host.Kind == TypeKind.Struct || host.Kind == TypeKind.EnumStruct) && !host.IsRich)
            {
                Error(span, $"Instance {targetDescription} of non-rich struct '{host.Name}' cannot be wrapped (§14.9)");
            }
            if (!wrapperType.IsShared && host is { IsShared: true })
            {
                Error(span, $"Non-shared wrapper '{wrapperType.Name}' cannot wrap {targetDescription} of shared type '{host.Name}'");
            }
        }

        // §14.9 interface 实现者传染（在实现者声明处检查）：
        // 被非 shared wrapper 修饰的 interface 不得被 shared 类型实现
        private void CheckInterfaceWrapperContagion()
        {
            foreach (var entry in typeEntries)
            {
                if (!entry.InGraph) continue;
                var type = (TypeSymbol)entry.Symbol;
                foreach (var iface in type.Interfaces)
                {
                    var ifaceDef = iface.ConstructedFrom ?? iface;
                    foreach (var wrapper in ifaceDef.AppliedWrappers)
                    {
                        if (!wrapper.IsShared && type.IsShared)
                        {
                            Error(entry.Node.Span,
                                $"Shared type '{type.Name}' cannot implement interface '{ifaceDef.Name}' wrapped by non-shared wrapper '{wrapper.Name}' (§14.9)");
                        }
                    }
                }
            }
        }

        // ===== 名字解析核心（类型引用/注解名/import/ext 目标共用）=====

        private SemanticSymbol ResolveTypeReference(TypeReferenceASTNode typeRef, DeclEntry entry)
        {
            var resolved = ResolveSymbolPath(typeRef.TypeSymbol.symbol, entry.Context,
                entry.DeclaringType, DeclaringMethodOf(entry), allowImports: true,
                reportErrors: true, span: typeRef.Span ?? entry.Node.Span);
            // T? 即构造类型 Nullable\<T>（SYNTAX §3.4）
            if (typeRef.IsNullable && resolved is not ErrorTypeSymbol)
            {
                return unit.Symbols.GetConstructedType(unit.Symbols.Bootstrap.NullableDefinition, resolved);
            }
            return resolved;
        }

        private static MethodSymbol? DeclaringMethodOf(DeclEntry entry)
        {
            return entry.Symbol as MethodSymbol;
        }

        // 符号路径解析：首段按查找序定位，后续逐段下钻，末段应用泛型实参。
        // 失败一律返回 ErrorTypeSymbol；reportErrors 控制是否产出诊断
        // （import 消费路径静默，由 ValidateImports 统一报一次）。
        private SemanticSymbol ResolveSymbolPath(Symbol path, FileContext ctx,
            TypeSymbol? declaringType, MethodSymbol? declaringMethod,
            bool allowImports, bool reportErrors, CharRange? span)
        {
            var elements = path.elements;
            if (elements.Count == 0)
            {
                if (reportErrors) Error(span, "Empty type reference");
                return unit.Symbols.ErrorType;
            }
            SemanticSymbol? current;
            // 泛型参数仅接受单段裸名引用（方法 → 宿主类型链）
            if (elements.Count == 1 && elements[0].generics.Count == 0)
            {
                current = FindGenericParameter(elements[0].name, declaringType, declaringMethod);
                if (current != null) return current;
            }
            current = ResolveFirstSegment(elements[0].name, ctx, declaringType, allowImports,
                out var importResolved);
            if (current == null)
            {
                if (reportErrors && !importResolved)
                {
                    Error(span, $"Unresolved type or namespace: '{elements[0].name}'");
                }
                return unit.Symbols.ErrorType;
            }
            // 逐段下钻（命名空间 → 子命名空间/类型；类型 → 嵌套类型）
            for (int i = 1; i < elements.Count; i++)
            {
                var next = Descend(current, elements[i].name);
                if (next == null)
                {
                    if (reportErrors)
                    {
                        Error(span, $"Unresolved type or namespace: '{PathText(path)}'");
                    }
                    return unit.Symbols.ErrorType;
                }
                current = next;
            }
            // 末段泛型实参（递归解析后驻留构造）
            var last = elements[^1];
            if (last.generics.Count > 0)
            {
                return ApplyTypeArguments(current, last, ctx, declaringType, declaringMethod,
                    allowImports, reportErrors, span);
            }
            return current;
        }

        // ext 目标路径（字符串段，SYNTAX §4.4 原文无泛型）走同一查找序
        private SemanticSymbol ResolveDottedPath(string[] segments, FileContext ctx,
            bool allowImports, bool reportErrors, CharRange? span)
        {
            var current = ResolveFirstSegment(segments[0], ctx, declaringType: null,
                allowImports, out var importResolved);
            if (current == null)
            {
                if (reportErrors && !importResolved)
                {
                    Error(span, $"Unresolved extension target: '{string.Join(".", segments)}'");
                }
                return unit.Symbols.ErrorType;
            }
            for (int i = 1; i < segments.Length; i++)
            {
                var next = Descend(current, segments[i]);
                if (next == null)
                {
                    if (reportErrors)
                    {
                        Error(span, $"Unresolved extension target: '{string.Join(".", segments)}'");
                    }
                    return unit.Symbols.ErrorType;
                }
                current = next;
            }
            return current;
        }

        // 首段查找序：宿主类型链 NestedTypes → 文件命名空间及父链 → 全局命名空间
        // → import 列表（具名末段同名 / 通配容器内查）→ core 命名空间（隐式）。
        // importResolved：具名 import 命中但自身解析失败（已诊断过）时为 true——
        // 调用方静默毒化，不再报「未解析」。
        private SemanticSymbol? ResolveFirstSegment(string name, FileContext ctx,
            TypeSymbol? declaringType, bool allowImports, out bool importResolved)
        {
            importResolved = false;
            for (var t = declaringType; t != null; t = t.DeclaringType)
            {
                var nested = t.NestedTypes.FirstOrDefault(n => n.Name == name);
                if (nested != null) return nested;
            }
            for (var ns = ctx.Namespace; ns != null; ns = ns.Parent)
            {
                var hit = FindInNamespace(ns, name);
                if (hit != null) return hit;
            }
            var globalHit = FindInNamespace(unit.Symbols.GlobalNamespace, name);
            if (globalHit != null) return globalHit;
            if (allowImports)
            {
                foreach (var item in ctx.Imports)
                {
                    var importPath = item.symbolNode.symbol;
                    if (item.importAll)
                    {
                        var container = ResolveSymbolPath(importPath, ctx, declaringType: null,
                            declaringMethod: null, allowImports: false, reportErrors: false, span: null);
                        SemanticSymbol? hit = container switch
                        {
                            NamespaceSymbol ns => ns.Types.FirstOrDefault(t => t.Name == name),
                            TypeSymbol t => t.NestedTypes.FirstOrDefault(n => n.Name == name),
                            _ => null,
                        };
                        if (hit != null) return hit;
                    }
                    else if (importPath.elements.Count > 0 && importPath.elements[^1].name == name)
                    {
                        var resolved = ResolveSymbolPath(importPath, ctx, declaringType: null,
                            declaringMethod: null, allowImports: false, reportErrors: false, span: null);
                        if (resolved is ErrorTypeSymbol)
                        {
                            importResolved = true;
                            return null;
                        }
                        return resolved;
                    }
                }
            }
            return FindInNamespace(unit.Symbols.Bootstrap.Core, name);
        }

        private static SemanticSymbol? FindInNamespace(NamespaceSymbol ns, string name)
        {
            return (SemanticSymbol?)ns.Types.FirstOrDefault(t => t.Name == name)
                ?? ns.ChildNamespaces.FirstOrDefault(n => n.Name == name);
        }

        private static SemanticSymbol? Descend(SemanticSymbol current, string name) => current switch
        {
            NamespaceSymbol ns => (SemanticSymbol?)ns.ChildNamespaces.FirstOrDefault(n => n.Name == name)
                ?? ns.Types.FirstOrDefault(t => t.Name == name),
            TypeSymbol t => t.NestedTypes.FirstOrDefault(n => n.Name == name),
            _ => null,
        };

        private static GenericParameterSymbol? FindGenericParameter(
            string name, TypeSymbol? declaringType, MethodSymbol? declaringMethod)
        {
            if (declaringMethod != null)
            {
                var hit = declaringMethod.GenericParameters.FirstOrDefault(p => p.Name == name);
                if (hit != null) return hit;
            }
            for (var t = declaringType; t != null; t = t.DeclaringType)
            {
                var hit = t.GenericParameters.FirstOrDefault(p => p.Name == name);
                if (hit != null) return hit;
            }
            return null;
        }

        private SemanticSymbol ApplyTypeArguments(SemanticSymbol current, SymbolElement last,
            FileContext ctx, TypeSymbol? declaringType, MethodSymbol? declaringMethod,
            bool allowImports, bool reportErrors, CharRange? span)
        {
            if (current is not TypeSymbol definition)
            {
                if (reportErrors) Error(span, $"'{last.name}' is not a generic type");
                return unit.Symbols.ErrorType;
            }
            var required = definition.GenericParameters.Count(p => !p.IsVariadic && !p.IsNamedVariadic);
            var variadic = definition.GenericParameters.Any(p => p.IsVariadic || p.IsNamedVariadic);
            var countOk = variadic
                ? last.generics.Count >= required
                : last.generics.Count == definition.GenericParameters.Count;
            if (definition.GenericParameters.Count == 0 || !countOk)
            {
                if (reportErrors)
                {
                    Error(span, $"'{definition.Name}' expects {definition.GenericParameters.Count} " +
                        $"type argument(s), got {last.generics.Count}");
                }
                return unit.Symbols.ErrorType;
            }
            var args = new SemanticSymbol[last.generics.Count];
            for (int i = 0; i < args.Length; i++)
            {
                args[i] = ResolveSymbolPath(last.generics[i], ctx, declaringType, declaringMethod,
                    allowImports, reportErrors, span);
            }
            // 实参毒化传播（实参自身的诊断已报，此处静默）
            if (args.Any(a => a is ErrorTypeSymbol)) return unit.Symbols.ErrorType;
            return unit.Symbols.GetConstructedType(definition, args);
        }

        // 诊断消息中的路径原文（a.b.C）
        private static string PathText(Symbol path)
        {
            return string.Join(".", path.elements.Select(e => e.name));
        }
    }
}

}
