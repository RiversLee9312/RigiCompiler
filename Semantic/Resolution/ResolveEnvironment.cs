namespace RigiCompiler
{
    // P2 只读环境（M55 visitor 化协议同款）：EntryCollector 产出平铺条目三表后
    // 冻结（IReadOnlyList 暴露），后续 12 个阶段 visitor 共用。诊断统一经
    // Error 落袋（P2 phase；可恢复诊断模型）。各阶段共用的名字解析薄包装与
    // 声明侧静态设施也集中在本环境。
    internal sealed class ResolveEnvironment
    {
        private readonly List<DeclEntry> entries = new List<DeclEntry>();
        private readonly List<DeclEntry> typeEntries = new List<DeclEntry>();
        private readonly Dictionary<SemanticSymbol, DeclEntry> entryOfSymbol =
            new Dictionary<SemanticSymbol, DeclEntry>(ReferenceEqualityComparer.Instance);

        public ResolveEnvironment(CompilationUnit unit, DeclarationCollection declarations)
        {
            Unit = unit;
            Declarations = declarations;
            // 名字解析共用设施（P3 Binder 以 DiagnosticPhase.P3 另建实例）
            Names = new NameResolver(unit, DiagnosticPhase.P2);
        }

        public CompilationUnit Unit { get; }

        public DeclarationCollection Declarations { get; }

        public NameResolver Names { get; }

        public IReadOnlyList<DeclEntry> Entries => entries;

        public IReadOnlyList<DeclEntry> TypeEntries => typeEntries;

        public IReadOnlyDictionary<SemanticSymbol, DeclEntry> EntryOfSymbol => entryOfSymbol;

        // 收集期填充（EntryCollector 专用；收集结束后三表冻结，阶段只读）
        internal void RegisterEntry(DeclEntry entry)
        {
            entries.Add(entry);
            entryOfSymbol[entry.Symbol] = entry;
        }

        internal void RegisterTypeEntry(DeclEntry entry)
        {
            typeEntries.Add(entry);
        }

        public void Error(CharRange? span, string message)
        {
            Unit.Diagnostics.Error(DiagnosticPhase.P2, span, message);
        }

        // ===== 名字解析（实现已提取到 NameResolver，此处为条目解包薄包装）=====

        public SemanticSymbol ResolveTypeReference(TypeReferenceASTNode typeRef, DeclEntry entry)
        {
            return Names.ResolveTypeReference(typeRef, entry.Context,
                entry.DeclaringType, DeclaringMethodOf(entry), typeRef.Span ?? entry.Node.Span);
        }

        private static MethodSymbol? DeclaringMethodOf(DeclEntry entry)
        {
            return entry.Symbol as MethodSymbol;
        }

        // 沿基类链查字段（含继承）；fieldType 按命中处的构造基类代入实参。
        // 内建类型同样查表：bootstrap Exception 程序化携带 message 字段
        // （S10，SYNTAX §8.1——子类 init 直接赋值继承字段）；直造基元的
        // Fields 表本为空，查找自然不中，无需短路
        public FieldSymbol? FindField(TypeSymbol? type, string name, out SemanticSymbol? fieldType)
        {
            fieldType = null;
            for (var t = type; t != null; t = t.BaseType)
            {
                var def = t.ConstructedFrom ?? t;
                var field = def.Fields.FirstOrDefault(f => f.Name == name);
                if (field != null)
                {
                    fieldType = Substitute(field.FieldType, def, t);
                    return field;
                }
            }
            return null;
        }

        // 泛型实参代入：字段类型中的泛型参数按构造类型的实参列表替换（递归）。
        // 实现单源在 SymbolGraph（构造类型 BaseType 代入同用），此处为薄包装
        public SemanticSymbol? Substitute(SemanticSymbol? fieldType, TypeSymbol definition, TypeSymbol constructed)
        {
            return Unit.Symbols.Substitute(fieldType, definition, constructed);
        }

        // ===== 声明侧静态设施（修饰符/注解/分类查询，各阶段共用）=====

        public static List<string> ModifiersOf(ASTNode node) => node switch
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

        // 访问级别解析（SYNTAX §16/§16.1；互斥由 CheckAccessModifierExclusivity 保证，
        // 无访问修饰符 = 默认 Private；接口成员默认 Public——接口即契约）
        public static Accessibility ParseAccessibility(List<string> modifiers,
            TypeSymbol? declaringType = null)
        {
            if (modifiers.Contains(Keywords.PUB)) return Accessibility.Public;
            if (modifiers.Contains(Keywords.PROTECTED)) return Accessibility.Protected;
            if (modifiers.Contains(Keywords.INTERNAL)) return Accessibility.Internal;
            if (modifiers.Contains(Keywords.PRIV)) return Accessibility.Private;
            return declaringType?.Kind == TypeKind.Interface
                ? Accessibility.Public
                : Accessibility.Private;
        }

        // 内建 native 注解（@NativeLibrary/@NativeSymbol）按末段名识别；非 native 注解返回 null
        public static string? NativeAnnotationNameOf(AnnotationASTNode annotation)
        {
            var elements = annotation.Name.symbol.elements;
            if (elements.Count == 0) return null;
            var last = elements[^1].name;
            return last == "NativeLibrary" || last == "NativeSymbol" ? last : null;
        }

        // 内建注解实参校验：必须恰好一个字符串字面量（§4.6）；非法报错并返回 null
        public string? NativeAnnotationStringArgument(AnnotationASTNode annotation, DeclEntry entry)
        {
            if (annotation.Arguments.Count == 1 &&
                annotation.Arguments[0].Value.IsAttached &&
                annotation.Arguments[0].Value.Expression is
                    LiteralExpressionASTNode { Literal: StringLiteralASTNode literal })
            {
                return literal.Value;
            }
            Error(annotation.Span ?? entry.Node.Span,
                $"@{NativeAnnotationNameOf(annotation)} expects exactly one string literal argument");
            return null;
        }

        public static bool IsWrapperTargetAnnotation(AnnotationASTNode annotation)
        {
            var elements = annotation.Name.symbol.elements;
            return elements.Count > 0 && elements[^1].name == "WrapperTarget";
        }

        public static WrapperTargetKind? WrapperTargetKindOf(AnnotationASTNode annotation)
        {
            if (annotation.Arguments.Count == 0) return null;
            var value = annotation.Arguments[0].Value;
            if (!value.IsAttached || value.Expression is not EnumCaseExpressionASTNode caseRef)
            {
                return null;
            }
            return caseRef.CaseName switch
            {
                "Entity" => RigiCompiler.WrapperTargetKind.Entity,
                "Value" => RigiCompiler.WrapperTargetKind.Value,
                "Method" => RigiCompiler.WrapperTargetKind.Method,
                _ => null,
            };
        }

        public static List<WrapperApplication>? AppliedWrappersOf(SemanticSymbol symbol) => symbol switch
        {
            TypeSymbol t => t.AppliedWrappers,
            FieldSymbol f => f.AppliedWrappers,
            MethodSymbol m => m.AppliedWrappers,
            _ => null,
        };

        public static bool ContainsGenericParameter(SemanticSymbol symbol)
        {
            if (symbol is GenericParameterSymbol) return true;
            if (symbol is TypeSymbol { TypeArguments: not null } t)
            {
                return t.TypeArguments.Any(ContainsGenericParameter);
            }
            return false;
        }

        public static string? BareNameOf(TypeReferenceASTNode typeRef)
        {
            var elements = typeRef.TypeSymbol.symbol.elements;
            return !typeRef.IsNullable && elements.Count == 1 && elements[0].generics.Count == 0
                ? elements[0].name
                : null;
        }
    }
}
