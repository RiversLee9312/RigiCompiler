namespace LatteCompiler
{
    // ===== 声明条目收集（含 open/abstract/singleton 标记位写符号）=====
    //
    // 唯一的 AST 骨架递归（P2 后续 12 个阶段全部在产出的平铺条目表上
    // 遍历，不再碰 AST 树形结构）。自旧 DeclarationResolver.ResolveSession.
    // CollectEntries/WalkDeclaration/WalkType/AddEntry/InContainer 迁移，行为不变。
    internal static class EntryCollector
    {
        public static ResolveEnvironment Collect(CompilationUnit unit, DeclarationCollection declarations)
        {
            var env = new ResolveEnvironment(unit, declarations);
            foreach (var file in unit.SourceFiles)
            {
                var ctx = declarations.FileContextOf(file);
                foreach (var decl in file.Declarations)
                {
                    WalkDeclaration(decl, ctx, declaringType: null, env);
                }
            }
            return env;
        }

        private static void WalkDeclaration(ASTNode node, FileContext ctx, TypeSymbol? declaringType,
            ResolveEnvironment env)
        {
            switch (node)
            {
                case ClassDeclarationASTNode c:
                    WalkType(c, c.Members, ctx, declaringType, env);
                    break;
                case InterfaceDeclarationASTNode i:
                    WalkType(i, i.Members, ctx, declaringType, env);
                    break;
                case StructDeclarationASTNode s:
                    WalkType(s, s.Members, ctx, declaringType, env);
                    break;
                case EnumStructDeclarationASTNode e:
                    WalkType(e, e.Members, ctx, declaringType, env);
                    break;
                case WrapperDeclarationASTNode w:
                    WalkType(w, w.Members, ctx, declaringType, env);
                    break;
                case VariableDeclarationASTNode v:
                    if (env.Declarations.SymbolOf(v) is FieldSymbol field)
                    {
                        field.Accessibility = ResolveEnvironment.ParseAccessibility(v.Modifiers);
                        AddEntry(v, field, ctx, declaringType, InContainer(field, declaringType, ctx), env);
                    }
                    break;
                case CallableDeclarationASTNode fn:
                    if (env.Declarations.SymbolOf(fn) is MethodSymbol method)
                    {
                        method.Accessibility = ResolveEnvironment.ParseAccessibility(fn.Modifiers);
                        AddEntry(fn, method, ctx, declaringType, InContainer(method, declaringType, ctx), env);
                    }
                    break;
            }
        }

        private static void WalkType(ASTNode node, List<ASTNode> members, FileContext ctx,
            TypeSymbol? declaringType, ResolveEnvironment env)
        {
            var type = (TypeSymbol)env.Declarations.SymbolOf(node)!;
            // open/abstract/singleton 标记位写符号（供可继承性判定与后续 pass 消费）
            var modifiers = ResolveEnvironment.ModifiersOf(node);
            type.IsOpen = modifiers.Contains(Keywords.OPEN);
            type.IsAbstract = modifiers.Contains(Keywords.ABSTRACT);
            type.IsSingleton = modifiers.Contains(Keywords.SINGLETON);
            // 访问级别写符号（SYNTAX §16；BIL 发射与 S8 使用点访问控制消费）
            type.Accessibility = ResolveEnvironment.ParseAccessibility(modifiers);
            var inGraph = (declaringType?.NestedTypes ?? ctx.Namespace.Types).Contains(type);
            var entry = AddEntry(node, type, ctx, declaringType, inGraph, env);
            env.RegisterTypeEntry(entry);
            foreach (var member in members)
            {
                WalkDeclaration(member, ctx, type, env);
            }
        }

        private static DeclEntry AddEntry(ASTNode node, SemanticSymbol symbol, FileContext ctx,
            TypeSymbol? declaringType, bool inGraph, ResolveEnvironment env)
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
            env.RegisterEntry(entry);
            return entry;
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
    }
}
