using System;
using System.Collections.Generic;
using System.Linq;
using LatteCompiler.Bil;

namespace LatteCompiler
{
    // M109b：`..init.wrapper` 合成（BIL §9.7 / §14.5）+ 静态 Method wrapper
    // companion（§8.7）。
    // 类型级：Entity/字段-Value/实例 Method 应用 → owner 上至多一个
    // priv+compiler-generated 实例 void 方法，体内按 outer→inner 发
    // new.wrapper.*；应用实参在本 fn 体内绑定（允许 this；无参）。
    // cell 级：value 字段 wrapped(W) 的应用实参提升为本方法参数（声明点
    // 已绑定的 BoundInitArguments 按序平铺）；构造点 new.wrapped 传入。
    // 静态 Method wrapper（M109b-2）：每方法合成 `..companion.UUID`
    // singleton + 实例方法（体 = 原静态体、wrapper 改挂）+ 原方法降壳体。
    internal static class WrapperInitSynthesis
    {
        // 类型级合成入口（BindingDriver 阶段 1.7）：遍历用户类型声明
        public static void SynthesizeForTypes(BindEnvironment env)
        {
            foreach (var file in env.Unit.SourceFiles)
            {
                var fileCtx = env.Declarations.FileContextOf(file);
                foreach (var decl in file.Declarations)
                {
                    WalkTypes(decl, fileCtx, env);
                }
            }
        }

        private static void WalkTypes(ASTNode node, FileContext fileCtx, BindEnvironment env)
        {
            switch (node)
            {
                case ClassDeclarationASTNode or StructDeclarationASTNode
                    or EnumStructDeclarationASTNode or WrapperDeclarationASTNode:
                    var type = env.Declarations.SymbolOf(node) as TypeSymbol
                        ?? throw new CompilerInternalException("P1 未登记类型符号");
                    TrySynthesizeForType(type, node, fileCtx, env);
                    foreach (var member in MembersOf(node))
                        WalkTypes(member, fileCtx, env);
                    return;
                case InterfaceDeclarationASTNode iface:
                    // interface 自身不合成实例 ..init.wrapper（无实例构造）
                    foreach (var member in iface.Members)
                        WalkTypes(member, fileCtx, env);
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
            _ => throw new CompilerInternalException("非类型声明: " + node.GetType().Name),
        };

        public static void TrySynthesizeForType(TypeSymbol type, ASTNode syntax,
            FileContext fileCtx, BindEnvironment env)
        {
            if (type.Methods.Any(m => m.Name == BilSpellings.InitWrapperMethodName))
                return;

            // 静态 Method wrapper → companion（§8.7；先于本实体 ..init.wrapper，
            // 以便 companion 上再合成其自身的 ..init.wrapper）
            foreach (var staticMethod in type.Methods.ToList())
            {
                if (!staticMethod.IsStatic || staticMethod.AppliedWrappers.Count == 0) continue;
                if (staticMethod.Companion != null) continue;
                SynthesizeCompanion(staticMethod, type, syntax, fileCtx, env);
            }

            var hasEntityOrFieldOrInstance = type.AppliedWrappers.Count > 0
                || type.Fields.Any(f => !f.IsStatic && f.AppliedWrappers.Count > 0)
                || type.Methods.Any(m => !m.IsStatic && m.AppliedWrappers.Count > 0
                    && m.Name != BilSpellings.InitWrapperMethodName);
            if (!hasEntityOrFieldOrInstance) return;

            // 类型级：..init.wrapper 无参数（实参在体内求值）
            var initWrapper = NewInitWrapperMethod(type, Array.Empty<ParameterSymbol>());
            type.Methods.Add(initWrapper);

            var ctx = new BindContext(initWrapper, fileCtx, type);
            var scope = new Scope(null);
            var statements = new List<BoundStatement>();

            // Entity 应用（outer → inner = 声明序）
            foreach (var app in type.AppliedWrappers)
            {
                var args = BindInitArgsInScope(app, scope, ctx, env, syntax);
                if (args == null) continue;
                statements.Add(new BoundNewWrapperStatement(app.Syntax ?? syntax,
                    BoundNewWrapperKind.Entity, app.Wrapper, null, args));
            }

            // 实例字段 Value 应用
            foreach (var field in type.Fields)
            {
                if (field.IsStatic) continue;
                foreach (var app in field.AppliedWrappers)
                {
                    var args = BindInitArgsInScope(app, scope, ctx, env, syntax);
                    if (args == null) continue;
                    statements.Add(new BoundNewWrapperStatement(app.Syntax ?? syntax,
                        BoundNewWrapperKind.Field, app.Wrapper, field, args));
                }
            }

            // 实例方法 Method 应用（含 companion 实例方法——其 Owner 为 companion，
            // 不在本 type.Methods；本循环只覆盖原宿主实例方法）
            foreach (var m in type.Methods)
            {
                if (m.IsStatic || m.Name == BilSpellings.InitWrapperMethodName) continue;
                foreach (var app in m.AppliedWrappers)
                {
                    var args = BindInitArgsInScope(app, scope, ctx, env, syntax);
                    if (args == null) continue;
                    statements.Add(new BoundNewWrapperStatement(app.Syntax ?? syntax,
                        BoundNewWrapperKind.Method, app.Wrapper, m, args));
                }
            }

            env.SyntheticCellBodies.Add(new BoundFunctionBody(initWrapper,
                Array.Empty<LocalSymbol>(), new BoundBlock(syntax, statements)));
        }

        // §8.7：静态方法 + Method wrapper → companion singleton + 实例方法 + 壳体
        private static void SynthesizeCompanion(MethodSymbol shell, TypeSymbol host,
            ASTNode syntax, FileContext fileCtx, BindEnvironment env)
        {
            var uuid = Guid.NewGuid().ToString("N");
            var companionName = BilSpellings.CompanionTypeNamePrefix + uuid;
            var objectType = env.Unit.Symbols.Bootstrap.Object;
            var companion = new TypeSymbol(companionName, TypeKind.Class,
                ns: fileCtx.Namespace, baseType: objectType, isShared: true)
            {
                Accessibility = Accessibility.Public,
                IsSingleton = true,
            };
            // 泛型宿主：companion 共享宿主类型链泛型参数（同 cell 上下文共享）
            var hostGenerics = new List<GenericParameterSymbol>();
            for (var t = host; t != null; t = t.DeclaringType)
            {
                hostGenerics.AddRange(t.GenericParameters);
            }
            foreach (var gp in hostGenerics)
            {
                companion.GenericParameters.Add(gp);
            }

            // 实例方法：简单名沿用；签名拷贝；wrapper 改挂；体稍后绑定
            var instance = new MethodSymbol(shell.Name, MethodKind.Regular, owner: companion,
                returnType: shell.ReturnType, isAsync: shell.IsAsync)
            {
                Accessibility = shell.Accessibility,
                HasBody = true,
                IsSynthetic = true,
                IsCompanionInstance = true,
            };
            foreach (var gp in shell.GenericParameters)
            {
                instance.GenericParameters.Add(gp);
            }
            foreach (var p in shell.Parameters)
            {
                instance.Parameters.Add(new ParameterSymbol(p.Name, p.Type,
                    isVariadic: p.IsVariadic, isNamedVariadic: p.IsNamedVariadic));
            }
            instance.AppliedWrappers.AddRange(shell.AppliedWrappers);
            shell.AppliedWrappers.Clear();
            companion.Methods.Add(instance);

            var info = new StaticMethodCompanionInfo(companion, instance, shell);
            companion.CompanionInfo = info;
            shell.Companion = info;

            // companion 自身 ..init.wrapper（方法 wrapper → new.wrapper.method）
            TrySynthesizeForType(companion, syntax, fileCtx, env);
        }

        // cell 子类：value 字段 wrapper 应用 → 有参/无参 ..init.wrapper
        // （CellClassFactory 在 value 字段填完 AppliedWrappers 后调用）
        public static void SynthesizeForCell(CellStorageInfo storage, ASTNode syntax,
            BindEnvironment env)
        {
            var valueField = storage.ValueField;
            if (valueField.AppliedWrappers.Count == 0) return;
            var cellClass = storage.CellClass;
            if (cellClass.Methods.Any(m => m.Name == BilSpellings.InitWrapperMethodName))
                return;

            // 参数 = 逐应用逐 init 实参平铺（应用须已在声明点绑定 BoundInitArguments）
            var parameters = new List<ParameterSymbol>();
            var wrapperArgExprs = new List<BoundExpression>();
            var paramIndex = 0;
            foreach (var app in valueField.AppliedWrappers)
            {
                var bound = app.BoundInitArguments;
                if (bound == null)
                {
                    // 声明点未绑定（静态字段 cell 路径可能延后）——此处补绑失败则跳过
                    continue;
                }
                for (var i = 0; i < bound.Count; i++)
                {
                    var argType = bound[i].Type;
                    var p = new ParameterSymbol("w" + paramIndex, argType);
                    parameters.Add(p);
                    wrapperArgExprs.Add(bound[i]);
                    paramIndex++;
                }
            }

            var method = NewInitWrapperMethod(cellClass, parameters);
            cellClass.Methods.Add(method);
            storage.InitWrapper = method;
            storage.WrapperInitArguments = wrapperArgExprs;

            // 体：按应用序发 new.wrapper.field，实参 = 对应参数切片
            // （BoundInitArguments == null = 声明点绑定失败，跳过——诊断已报）
            var statements = new List<BoundStatement>();
            var cursor = 0;
            foreach (var app in valueField.AppliedWrappers)
            {
                if (app.BoundInitArguments == null) continue;
                var bound = app.BoundInitArguments;
                var args = new List<BoundExpression>();
                for (var i = 0; i < bound.Count; i++)
                {
                    var p = parameters[cursor++];
                    args.Add(new BoundValueReferenceExpression(app.Syntax ?? syntax, p, p.Type!));
                }
                statements.Add(new BoundNewWrapperStatement(app.Syntax ?? syntax,
                    BoundNewWrapperKind.Field, app.Wrapper, valueField, args));
            }

            env.SyntheticCellBodies.Add(new BoundFunctionBody(method,
                Array.Empty<LocalSymbol>(), new BoundBlock(syntax, statements)));
        }

        // 在给定作用域绑定 wrapper 应用的 init 实参（§3.3 重载）
        public static List<BoundExpression>? BindInitArgsInScope(WrapperApplication app,
            Scope scope, BindContext ctx, BindEnvironment env, ASTNode fallbackSyntax)
        {
            if (app.BoundInitArguments != null)
                return app.BoundInitArguments.ToList();

            var syntax = app.Syntax;
            var span = syntax?.Span ?? fallbackSyntax.Span;
            var wrapperDef = app.WrapperDefinition;
            var inits = wrapperDef.Methods.Where(m => m.Kind == MethodKind.Init).ToList();
            var argNodes = syntax?.Arguments ?? new List<ArgumentASTNode>();

            // 无显式 init + 无实参 = 默认零参构造（空实参列表）
            if (inits.Count == 0)
            {
                if (argNodes.Count > 0)
                {
                    env.Error(span, $"Wrapper '{wrapperDef.Name}' has no constructor");
                    return null;
                }
                app.BoundInitArguments = Array.Empty<BoundExpression>();
                return new List<BoundExpression>();
            }

            // 用注解节点或 fallback 作为 Resolve 诊断锚点
            var anchor = (ASTNode?)syntax ?? fallbackSyntax;
            var resolved = OverloadResolution.Resolve(anchor, inits, argNodes, scope, ctx, env,
                receiverType: app.Wrapper);
            if (resolved == null) return null;
            app.BoundInitArguments = resolved.Value.Arguments;
            return resolved.Value.Arguments;
        }

        // 壳体静态方法体：new companion → invoke 实例方法（实参透传）→ ret
        public static BoundFunctionBody SynthesizeShellBody(ASTNode syntax, MethodSymbol shell,
            StaticMethodCompanionInfo info, BindEnvironment env)
        {
            var companion = info.CompanionType;
            var instance = info.InstanceMethod;
            // 泛型 companion：构造类型 = 定义 + 共享泛型参数（同 cell）
            TypeSymbol constructed = companion.GenericParameters.Count == 0
                ? companion
                : env.Unit.Symbols.GetConstructedType(companion,
                    companion.GenericParameters.ToArray());
            var receiver = new BoundNewExpression(syntax, constructed, null,
                Array.Empty<BoundExpression>());
            var args = new List<BoundExpression>();
            for (var i = 0; i < shell.Parameters.Count; i++)
            {
                var p = shell.Parameters[i];
                var type = p.Type ?? env.Unit.Symbols.ErrorType;
                args.Add(new BoundValueReferenceExpression(syntax, p, type));
            }
            // 方法级泛型：typeid 转发（TypeArguments = 同形 GenericParameter 列表）
            IReadOnlyList<SemanticSymbol>? typeArgs = shell.GenericParameters.Count == 0
                ? null
                : shell.GenericParameters.Cast<SemanticSymbol>().ToList();
            var statements = new List<BoundStatement>();
            if (shell.ReturnType == null)
            {
                statements.Add(new BoundCallStatement(syntax, instance, args, receiver,
                    typeArgs));
            }
            else
            {
                var call = new BoundInstanceCallExpression(syntax, receiver, instance, args,
                    shell.ReturnType, typeArgs);
                statements.Add(new BoundReturnStatement(syntax, call));
            }
            return new BoundFunctionBody(shell, Array.Empty<LocalSymbol>(),
                new BoundBlock(syntax, statements));
        }

        private static MethodSymbol NewInitWrapperMethod(TypeSymbol owner,
            IReadOnlyList<ParameterSymbol> parameters)
        {
            var method = new MethodSymbol(BilSpellings.InitWrapperMethodName, MethodKind.Regular,
                owner: owner, returnType: null)
            {
                Accessibility = Accessibility.Private,
                HasBody = true,
                IsSynthetic = true,
            };
            foreach (var p in parameters) method.Parameters.Add(p);
            return method;
        }
    }
}
