using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RigiCompiler
{
    // P1 声明收集（SEMANTIC_ARCHITECTURE §2）：遍历编译单元全部声明骨架
    // （不进函数体）建命名空间/类型/成员/全局符号壳。
    //
    // 职责边界：
    // - 建壳：TypeSymbol/FieldSymbol/MethodSymbol/ParameterSymbol/
    //   GenericParameterSymbol + NamespaceSymbol 逐段驻留（SymbolGraph.GetNamespace）
    // - 登记：每文件的命名空间与 import 列表（FileContext，P2/P3 名字解析的上下文）；
    //   ext 限定名拆出成员名与目标路径原文（SYNTAX §4.4，注册到目标类型归 P2）
    // - 重复声明诊断：同容器类型同名、变量同名、方法签名文本全同
    //   （累积不中断；重复符号不进容器表——保留第一个，但仍登记 AST→符号映射）
    //
    // 明确不做（归 P2/P3）：类型引用解析（ReturnType/FieldType/参数类型留空）、
    // 显式继承/implements 解析、修饰符合法性、rich/shared 闭包与传染检查、
    // enum case 的 init 模板绑定（S11，P3 声明点）。
    // 修饰符只读标记位建壳（rich/shared/static/native），合法性检查一律归 P2。
    //
    // 访问器壳（SYNTAX §9.4，S8e）：字段带 get/set 块时建 Kind=Getter/Setter 的
    // MethodSymbol 挂字段三槽（Name=字段名，宿主/静态同字段；setter 含唯一 value
    // 参数壳）。访问器符号不进容器 Methods 表（避免污染按名查找）——P3 体枚举经
    // 字段反查，P4b 声明发射由字段槽驱动；返回/参数类型回填与修饰符合法性归 P2。
    //
    // 类型默认基类在建壳时即定（class→Object / struct→ValueType /
    // enum struct→Enum / wrapper→Wrapper，wrapper 恒 rich §14.9）：
    // IsValueTypeBranch 构造期沿基类链传播，P2 覆盖显式基类不影响分支归属。
    public static class DeclarationCollector
    {
        public static DeclarationCollection Collect(CompilationUnit unit)
        {
            var result = new DeclarationCollection();
            foreach (var file in unit.SourceFiles)
            {
                CollectFile(unit, file, result);
            }
            return result;
        }

        // ===== 文件级：namespace 唯一性与位置、import 登记 =====

        private static void CollectFile(CompilationUnit unit, RootASTNode file, DeclarationCollection result)
        {
            var fileNamespace = unit.Symbols.GlobalNamespace;
            var imports = new List<ImportItem>();
            var namespaceSeen = false;
            var declarationSeen = false;    // 已出现类型/函数/变量声明
            // 容器视图全程复用（Scope.MethodKeys 依赖此生命周期，见 Scope 注释）
            var globalScope = ScopeOf(fileNamespace);

            foreach (var decl in file.Declarations)
            {
                switch (decl)
                {
                    case NamespaceDeclarationASTNode nsDecl:
                        // §15.1：每文件至多一个 namespace 声明，且须先于任何类型/成员声明
                        if (namespaceSeen)
                        {
                            unit.Diagnostics.Error(DiagnosticPhase.P1, nsDecl.Span,
                                "Multiple namespace declarations in one file");
                        }
                        else if (declarationSeen)
                        {
                            unit.Diagnostics.Error(DiagnosticPhase.P1, nsDecl.Span,
                                "Namespace declaration must precede all type and member declarations");
                        }
                        else
                        {
                            namespaceSeen = true;
                            fileNamespace = unit.Symbols.GetNamespace(SegmentsOf(nsDecl.Name));
                            globalScope = ScopeOf(fileNamespace);
                        }
                        break;
                    case ImportASTNode import:
                        imports.AddRange(import.importedSymbols);
                        break;
                    default:
                        declarationSeen = true;
                        CollectDeclaration(unit, decl, fileNamespace,
                            globalScope, declaringType: null, result);
                        break;
                    // 其余顶层条目（如测试驱动的表达式 Root）不是声明骨架，跳过
                }
            }
            result.RegisterFile(file, new FileContext(file, fileNamespace, imports));
        }

        // ===== 声明分派（类型成员递归复用；declaringType 非空即成员/嵌套）=====

        private static void CollectDeclaration(
            CompilationUnit unit, ASTNode node, NamespaceSymbol ns,
            Scope scope, TypeSymbol? declaringType, DeclarationCollection result)
        {
            switch (node)
            {
                case ClassDeclarationASTNode c:
                    CollectType(unit, c.ClassName, TypeKind.Class, unit.Symbols.Bootstrap.Object,
                        c.Modifiers, c.GenericParameters, c.Members, node, ns, declaringType, scope, result);
                    // like 委托目标字段名（§9.6）入符号，供 P2/P3 消费
                    ((TypeSymbol)result.SymbolOf(node)!).LikeTarget = c.LikeTarget;
                    break;
                case InterfaceDeclarationASTNode i:
                    // 接口无基类（BaseInterfaces 解析归 P2，不进 BaseType 链）
                    CollectType(unit, i.InterfaceName, TypeKind.Interface, null,
                        i.Modifiers, i.GenericParameters, i.Members, node, ns, declaringType, scope, result);
                    break;
                case StructDeclarationASTNode s:
                    CollectType(unit, s.StructName, TypeKind.Struct, unit.Symbols.Bootstrap.ValueType,
                        s.Modifiers, s.GenericParameters, s.Members, node, ns, declaringType, scope, result);
                    break;
                case EnumStructDeclarationASTNode e:
                    CollectType(unit, e.EnumName, TypeKind.EnumStruct, unit.Symbols.Bootstrap.Enum,
                        e.Modifiers, e.GenericParameters, e.Members, node, ns, declaringType, scope, result);
                    // enum case 建壳（SYNTAX §12）：挂宿主 Cases 表，判别值落定归 P2、
                    // init 模板绑定归 P3 声明点
                    CollectEnumCases(unit, e, (TypeSymbol)result.SymbolOf(node)!, result);
                    break;
                case WrapperDeclarationASTNode w:
                    // wrapper 恒 rich（§14.9），隐式基类 Wrapper；显式写 rich 的报错归 P2
                    CollectType(unit, w.WrapperName, TypeKind.Wrapper, unit.Symbols.Bootstrap.Wrapper,
                        w.Modifiers, w.GenericParameters, w.Members, node, ns, declaringType, scope, result);
                    break;
                case VariableDeclarationASTNode v:
                    CollectVariable(unit, v, ns, declaringType, scope, result);
                    break;
                case CallableDeclarationASTNode fn:
                    CollectCallable(unit, fn, ns, declaringType, scope, result);
                    break;
                default:
                    // 成员列表中的非声明条目（同上，不进函数体）
                    break;
            }
        }

        // ===== 类型壳 =====

        private static void CollectType(
            CompilationUnit unit, string name, TypeKind kind, TypeSymbol? defaultBase,
            List<string> modifiers, GenericParameterListASTNode? generics, List<ASTNode> members,
            ASTNode node, NamespaceSymbol ns, TypeSymbol? declaringType,
            Scope scope, DeclarationCollection result)
        {
            var symbol = new TypeSymbol(
                name, kind, ns, declaringType,
                baseType: defaultBase,
                isRich: kind == TypeKind.Wrapper || modifiers.Contains(Keywords.RICH),
                isShared: modifiers.Contains(Keywords.SHARED));
            CollectGenericParameters(symbol.GenericParameters, generics, result);
            result.Map(node, symbol);
            // 重复检测：同容器同名同元数类型只保留第一个（S10 定稿：类型名
            // 唯一性按「名 + 泛型参数个数」判定——Task 与 Task\<T\> 合法共存，
            // 见 SYNTAX §15.3；诊断累积，收集不中断）
            var arity = generics?.Parameters.Count ?? 0;
            if (scope.Types.Any(t => t.Name == name && t.GenericParameters.Count == arity))
            {
                unit.Diagnostics.Error(DiagnosticPhase.P1, node.Span,
                    $"Duplicate type declaration: '{name}'");
            }
            else
            {
                scope.Types.Add(symbol);
            }
            // 递归成员与嵌套类型（容器切换为新类型自身；Scope 全程复用）
            var memberScope = ScopeOf(symbol);
            foreach (var member in members)
            {
                CollectDeclaration(unit, member, ns, memberScope, symbol, result);
            }
        }

        // ===== 变量壳（字段 / 全局变量与常量）=====

        private static void CollectVariable(
            CompilationUnit unit, VariableDeclarationASTNode node, NamespaceSymbol ns,
            TypeSymbol? declaringType, Scope scope, DeclarationCollection result)
        {
            // ext 限定名（§4.4 "String.isEmpty"）：拆最后一段为成员名，前缀为目标路径原文
            var name = node.Name;
            string? extTarget = null;
            if (node.Modifiers.Contains(Keywords.EXT) && name.LastIndexOf('.') is var dot && dot > 0)
            {
                name = name[(dot + 1)..];
                extTarget = node.Name[..dot];
            }
            var symbol = new FieldSymbol(name,
                owner: declaringType,
                ns: declaringType == null ? ns : null,
                isStatic: node.Modifiers.Contains(Keywords.STATIC),
                extTargetPath: extTarget,
                isConst: node.IsConst);
            result.Map(node, symbol);
            // 访问器壳（SYNTAX §9.4）：backing 一致性 Parser 已校验，取任一方即可
            if (node.Getter != null || node.Setter != null)
            {
                symbol.HasBackingStorage = (node.Getter ?? node.Setter)!.HasBackingField;
                if (node.Getter != null)
                {
                    symbol.Getter = new MethodSymbol(name, MethodKind.Getter,
                        owner: declaringType, ns: declaringType == null ? ns : null,
                        isStatic: symbol.IsStatic, extTargetPath: extTarget);
                    result.Map(node.Getter, symbol.Getter);
                }
                if (node.Setter != null)
                {
                    symbol.Setter = new MethodSymbol(name, MethodKind.Setter,
                        owner: declaringType, ns: declaringType == null ? ns : null,
                        isStatic: symbol.IsStatic, extTargetPath: extTarget);
                    symbol.Setter.Parameters.Add(new ParameterSymbol("value"));
                    result.Map(node.Setter, symbol.Setter);
                }
            }
            if (extTarget != null)
            {
                // ext 成员不属于声明所在容器：登记待注册列表（P2 解析目标路径后挂到目标类型）
                result.AddPendingExt(symbol);
                return;
            }
            if (scope.Fields.Any(f => f.Name == name))
            {
                unit.Diagnostics.Error(DiagnosticPhase.P1, node.Span,
                    $"Duplicate variable declaration: '{name}'");
            }
            else
            {
                scope.Fields.Add(symbol);
            }
        }

        // ===== 可调用壳（函数 / 方法 / init / operator）=====

        private static void CollectCallable(
            CompilationUnit unit, CallableDeclarationASTNode node, NamespaceSymbol ns,
            TypeSymbol? declaringType, Scope scope, DeclarationCollection result)
        {
            var kind = node.Kind switch
            {
                CallableKind.Init => MethodKind.Init,
                CallableKind.Operator => MethodKind.Operator,
                _ => MethodKind.Regular,
            };
            var name = node.Name;
            string? extTarget = null;
            if (node.Modifiers.Contains(Keywords.EXT) && name.LastIndexOf('.') is var dot && dot > 0)
            {
                name = name[(dot + 1)..];
                extTarget = node.Name[..dot];
            }
            var symbol = new MethodSymbol(name, kind,
                owner: declaringType,
                ns: declaringType == null ? ns : null,
                isStatic: node.Modifiers.Contains(Keywords.STATIC),
                isNative: node.Modifiers.Contains(Keywords.NATIVE),
                extTargetPath: extTarget,
                isAsync: node.Modifiers.Contains(Keywords.ASYNC));
            // wrapper proxy 模板（SYNTAX §14.2）：声明名即语法——建壳时落定
            // 属性标记（Specific/Wildcard），后续 pass 只读属性不查名字
            if (name.StartsWith(".proxy.", StringComparison.Ordinal))
            {
                symbol.ProxyTemplate = name.EndsWith(".*", StringComparison.Ordinal)
                    ? ProxyTemplateKind.Wildcard
                    : ProxyTemplateKind.Specific;
            }
            symbol.HasBody = node.Body != null;
            CollectGenericParameters(symbol.GenericParameters, node.GenericParameters, result);
            foreach (var p in node.Parameters.Parameters)
            {
                // init 参数映射 `_ -> field`：参数名与字段名相同（SYNTAX §9.3，纯语法替换）
                var param = new ParameterSymbol(
                    p.Name == "_" && p.MappedFieldName != null ? p.MappedFieldName : p.Name,
                    defaultValue: p.DefaultValue,
                    isVariadic: p.IsVariadic,
                    isNamedVariadic: p.IsNamedVariadic);
                symbol.Parameters.Add(param);
                result.Map(p, param);
            }
            result.Map(node, symbol);
            if (extTarget != null)
            {
                result.AddPendingExt(symbol);
                return;
            }
            // 方法重复检测（P1 文本级粒度：同名 + 同泛型元数 + 同参数名序列 +
            // 同参数类型文本全同必为重复；重载合法——签名级精确判定依赖类型
            // 解析，归 P2）
            var key = MethodKey(symbol.Name, symbol.GenericParameters.Count, node.Parameters);
            if (scope.MethodKeys.Contains(key))
            {
                unit.Diagnostics.Error(DiagnosticPhase.P1, node.Span,
                    $"Duplicate method declaration: '{symbol.Name}'");
            }
            else
            {
                scope.MethodKeys.Add(key);
                scope.Methods.Add(symbol);
            }
        }

        // ===== enum case 壳（SYNTAX §12；P2 落定判别值，P3 声明点绑定 init 模板）=====

        private static void CollectEnumCases(
            CompilationUnit unit, EnumStructDeclarationASTNode node, TypeSymbol owner,
            DeclarationCollection result)
        {
            foreach (var caseNode in node.Cases)
            {
                var symbol = new EnumCaseSymbol(caseNode.CaseName, owner);
                result.Map(caseNode, symbol);
                // case 名唯一（§12；Parser FinishEnumCases 已拦，此处防御性复核）。
                // 与重复声明惯例一致：重复符号不进容器表，但仍登记 AST→符号映射
                if (owner.Cases.Any(c => c.Name == symbol.Name))
                {
                    unit.Diagnostics.Error(DiagnosticPhase.P1, caseNode.Span,
                        $"Duplicate enum case declaration: '{symbol.Name}'");
                }
                else
                {
                    owner.Cases.Add(symbol);
                }
            }
        }

        // ===== 通用小件 =====

        private static void CollectGenericParameters(
            List<GenericParameterSymbol> target, GenericParameterListASTNode? generics,
            DeclarationCollection result)
        {
            if (generics == null) return;
            foreach (var p in generics.Parameters)
            {
                var gp = new GenericParameterSymbol(p.Name, p.IsVariadic, p.IsNamedVariadic,
                    p.Variance);
                target.Add(gp);
                result.Map(p, gp);
            }
            // 约束子句（GenericConstraintASTNode）的类型解析归 P2
        }

        // 收集期容器视图：借用宿主符号（命名空间/类型）的三张成员表，
        // 方法与 P1 文本级签名 key 表平行（MethodKeys[i] 对应 Methods[i]）。
        // 生命周期 = 一个容器的成员收集全程（容器成员共享同一实例），
        // 禁止每声明新建——MethodKeys 随临时实例丢弃会使方法重复检测失效。
        private sealed class Scope
        {
            public readonly List<TypeSymbol> Types;
            public readonly List<FieldSymbol> Fields;
            public readonly List<MethodSymbol> Methods;
            public readonly List<string> MethodKeys = new List<string>();

            public Scope(List<TypeSymbol> types, List<FieldSymbol> fields, List<MethodSymbol> methods)
            {
                Types = types;
                Fields = fields;
                Methods = methods;
            }
        }

        private static Scope ScopeOf(NamespaceSymbol ns) => new Scope(ns.Types, ns.Fields, ns.Methods);

        private static Scope ScopeOf(TypeSymbol type) => new Scope(type.NestedTypes, type.Fields, type.Methods);

        // namespace 声明路径段（a.b.c → ["a","b","c"]）
        private static List<string> SegmentsOf(SymbolASTNode path)
        {
            var segments = new List<string>();
            foreach (var element in path.symbol.elements)
            {
                segments.Add(element.name);
            }
            return segments;
        }

        // P1 文本级方法签名键：名\<泛型元数>(参数名:参数类型键,...)。
        // 泛型元数在内（对齐 S10 类型判重口径「名 + 泛型元数」——foo(x: i32) 与
        // foo\<T\>(x: i32) 元数不同即不同派发契约，合法共存）；参数名在内——
        // 具名调用使参数名成为签名的一部分；类型键是源码文本（未解析），
        // 同名同元数同参数名同类型文本的声明必为重复。
        private static string MethodKey(string name, int genericArity,
            ParameterListASTNode parameters)
        {
            var sb = new StringBuilder(name).Append("\\<").Append(genericArity).Append('>').Append('(');
            for (int i = 0; i < parameters.Parameters.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var p = parameters.Parameters[i];
                sb.Append(p.Name).Append(':').Append(TypeRefKey(p.Type));
            }
            return sb.Append(')').ToString();
        }

        // 类型引用的 P1 文本键（init 映射省略类型时为空引用节点，键为空串）
        private static string TypeRefKey(TypeReferenceASTNode typeRef)
        {
            return SymbolKey(typeRef.TypeSymbol.symbol) + (typeRef.IsNullable ? "?" : "");
        }

        private static string SymbolKey(Symbol symbol)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < symbol.elements.Count; i++)
            {
                if (i > 0) sb.Append('.');
                var element = symbol.elements[i];
                sb.Append(element.name);
                if (element.generics.Count > 0)
                {
                    sb.Append("\\<");
                    for (int j = 0; j < element.generics.Count; j++)
                    {
                        if (j > 0) sb.Append(',');
                        sb.Append(SymbolKey(element.generics[j]));
                    }
                    sb.Append('>');
                }
            }
            return sb.ToString();
        }
    }

    // P1 产物：声明 AST 节点 → 符号映射、每文件命名空间与 import 上下文、
    // ext 待注册列表（P2 DeclarationResolver 消费）。
    public sealed class DeclarationCollection
    {
        private readonly Dictionary<ASTNode, SemanticSymbol> symbolOf = new Dictionary<ASTNode, SemanticSymbol>();
        private readonly Dictionary<RootASTNode, FileContext> fileContexts = new Dictionary<RootASTNode, FileContext>();
        private readonly List<SemanticSymbol> pendingExtMembers = new List<SemanticSymbol>();

        // ext 声明的符号壳（FieldSymbol/MethodSymbol），ExtTargetPath 待 P2 解析注册
        public IReadOnlyList<SemanticSymbol> PendingExtMembers => pendingExtMembers;

        // 声明节点 → 符号（类型/变量/可调用/参数/泛型参数；重复声明的符号同样登记）
        public SemanticSymbol? SymbolOf(ASTNode node)
        {
            return symbolOf.TryGetValue(node, out var symbol) ? symbol : null;
        }

        public FileContext FileContextOf(RootASTNode file) => fileContexts[file];

        internal void Map(ASTNode node, SemanticSymbol symbol) => symbolOf.Add(node, symbol);

        internal void RegisterFile(RootASTNode file, FileContext context) => fileContexts.Add(file, context);

        internal void AddPendingExt(SemanticSymbol symbol) => pendingExtMembers.Add(symbol);
    }

    // 单文件的名字解析上下文（P2/P3 消费）：文件命名空间 + import 列表
    // （ImportItem 为 AST 引用，中端只读）+ 文件身份（S8e 访问控制
    // 「同文件可见」判定，RootASTNode 引用相等即同文件）
    public sealed class FileContext
    {
        public RootASTNode File { get; }
        public NamespaceSymbol Namespace { get; }
        public IReadOnlyList<ImportItem> Imports { get; }

        public FileContext(RootASTNode file, NamespaceSymbol ns, IReadOnlyList<ImportItem> imports)
        {
            File = file;
            Namespace = ns;
            Imports = imports;
        }
    }
}
