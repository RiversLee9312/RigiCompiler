using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;

namespace RigiCompiler
{
    // M109b：`..init.wrapper` 合成（BIL §9.7 / §14.5）+ 静态 companion
    // （§8.7）。新 init 原则（§9.3/§9.7 修订）：
    // - 每个带声明初始值的实例字段合成一个可覆写的 `..init.field.<名>`
    //   方法（class/struct/enum struct/wrapper 同规则不退化）；子类同名字段
    //   override 时生成同族 override 版（写同一基类槽），虚派发自动选中
    //   最高派生实现。
    // - `..init.wrapper` 本体 = 安装**继承闭包全部** wrapper（本类与基类
    //   的 Entity/Field/Method 应用，含基类未被 override 方法的 Method
    //   wrapper）+ 依次调用闭包全部 `..init.field.*`（基→本、声明序）。
    //   不再生成对基类的 super(...)——前端完成全部缝合；VM 只调实际类型
    //   的 ..init.wrapper。
    // 类型级：Entity/字段-Value/实例 Method 应用 → owner 上至多一个
    // priv+compiler-generated 实例 void 方法，体内按 outer→inner 发
    // new.wrapper.*；应用实参在本 fn 体内绑定（允许 this；无参）。
    // cell 级：value 字段 wrapped(W) 的应用实参提升为本方法参数（声明点
    // 已绑定的 BoundInitArguments 按序平铺）；构造点 new.wrapped 传入。
    // 静态 companion（§8.7）：每个声明类一个嵌套 singleton（..companion，
    // 无 UUID）；静态 Method wrapper 迁实例方法 + 原方法降壳体，静态
    // Value wrapper 字段的 cell 存储挂 companion 实例（init 里构造）。
    internal static class WrapperInitSynthesis
    {
        // ===== 全局函数 Method wrapper（§14.4/§14.9，bug 修复）=====

        // 全局函数（Owner == null）无宿主类型，此前 P3 不为它们合成任何
        // wrapper 安装——@W 被 P2 登记后静默忽略。修复与静态方法同一机制
        //（§8.7 companion）：每命名空间合成一个顶层 singleton 宿主类
        //（..globals.host——全局函数无宿主类可嵌套，CompanionInfo 自指，
        // 形态对齐 companion），带 wrapper 的全局函数迁移为宿主实例方法
        // （wrapper 应用随迁），原符号降壳体（P3 阶段 2 经 symbol.Companion
        // 统一走壳体绑定）；安装在宿主自身的 ..init.wrapper 里，由 VM 在
        // main 前的 singleton 急切初始化完成。native/无体函数防御跳过
        // （旧行为同为忽略；壳体需要可迁的源体）
        public static void SynthesizeForGlobalFunctions(BindEnvironment env)
        {
            var hosts = new Dictionary<NamespaceSymbol,
                (TypeSymbol Host, ASTNode Syntax, FileContext FileCtx)>();
            foreach (var file in env.Unit.SourceFiles)
            {
                var fileCtx = env.Declarations.FileContextOf(file);
                foreach (var decl in file.Declarations)
                {
                    if (decl is not CallableDeclarationASTNode fn) continue;
                    if (env.Declarations.SymbolOf(fn) is not MethodSymbol method) continue;
                    if (method.Owner != null || method.AppliedWrappers.Count == 0
                        || method.Companion != null || method.IsNative || fn.Body == null)
                    {
                        continue;
                    }
                    if (!hosts.TryGetValue(fileCtx.Namespace, out var hostEntry))
                    {
                        hostEntry = (CreateGlobalMethodHost(fileCtx.Namespace, env), fn, fileCtx);
                        hosts.Add(fileCtx.Namespace, hostEntry);
                    }
                    SynthesizeCompanionMethod(hostEntry.Host.CompanionInfo!, method);
                }
            }
            // 宿主的 ..init.wrapper：安装迁入实例方法的 Method wrapper
            //（companion 自身即走 SynthesizeInitWrapper 同一路径）
            foreach (var (host, syntax, fileCtx) in hosts.Values)
            {
                SynthesizeInitWrapper(host, syntax, fileCtx, env);
            }
        }

        // 全局函数 wrapper 的宿主 singleton：顶层合成类（每命名空间一个），
        // singleton+shared；CompanionInfo 自指——P4b 的 companion 声明收集
        // 与 compiler-generated 投影与静态 companion 同通道
        private static TypeSymbol CreateGlobalMethodHost(NamespaceSymbol ns, BindEnvironment env)
        {
            var host = new TypeSymbol(BilSpellings.GlobalMethodHostTypeName, TypeKind.Class,
                ns: ns, baseType: env.Unit.Symbols.Bootstrap.Object, isShared: true)
            {
                // 这是每模块的实现宿主，不是源 API；独立模块必须隔离链接身份。
                Accessibility = env.Unit.IsModuleCompilation ? Accessibility.Private : Accessibility.Public,
                IsSingleton = true,
            };
            host.CompanionInfo = new StaticCompanionInfo(host);
            return host;
        }

        // 类型级合成入口（BindingDriver 阶段 1.7）：两阶段——先为全部类型
        // 合成 ..init.field.*（基类先于派生类，闭包缝合与 wrapper 实参的
        // 声明点绑定都依赖基类产物已就位），再逐类型合成 ..init.wrapper
        public static void SynthesizeForTypes(BindEnvironment env)
        {
            var declarations = new Dictionary<TypeSymbol, (ASTNode Syntax, FileContext FileCtx)>();
            foreach (var file in env.Unit.SourceFiles)
            {
                var fileCtx = env.Declarations.FileContextOf(file);
                foreach (var decl in file.Declarations)
                {
                    CollectTypes(decl, fileCtx, env, declarations);
                }
            }
            var processed = new HashSet<TypeSymbol>();
            foreach (var type in declarations.Keys.ToList())
            {
                ProcessType(type, declarations, processed, env);
            }
        }

        private static void CollectTypes(ASTNode node, FileContext fileCtx, BindEnvironment env,
            Dictionary<TypeSymbol, (ASTNode Syntax, FileContext FileCtx)> declarations)
        {
            switch (node)
            {
                case ClassDeclarationASTNode or StructDeclarationASTNode
                    or EnumStructDeclarationASTNode or WrapperDeclarationASTNode:
                    var type = env.Declarations.SymbolOf(node) as TypeSymbol
                        ?? throw new CompilerInternalException("P1 未登记类型符号");
                    declarations[type] = (node, fileCtx);
                    foreach (var member in MembersOf(node))
                        CollectTypes(member, fileCtx, env, declarations);
                    return;
                case InterfaceDeclarationASTNode iface:
                    // interface 自身不合成实例 ..init.wrapper（无实例构造）
                    foreach (var member in iface.Members)
                        CollectTypes(member, fileCtx, env, declarations);
                    return;
            }
        }

        // 单类型处理（基类优先递归 + 幂等）：companion（§8.7）→ 阶段 A
        // （..init.field.*）→ 阶段 B（..init.wrapper 闭包缝合）
        private static void ProcessType(TypeSymbol type,
            Dictionary<TypeSymbol, (ASTNode Syntax, FileContext FileCtx)> declarations,
            HashSet<TypeSymbol> processed, BindEnvironment env)
        {
            if (!processed.Add(type)) return;
            if (type.BaseType is { } baseType)
            {
                var baseDefinition = baseType.ConstructedFrom ?? baseType;
                if (declarations.TryGetValue(baseDefinition, out _)
                    && !processed.Contains(baseDefinition))
                {
                    ProcessType(baseDefinition, declarations, processed, env);
                }
            }
            var (syntax, fileCtx) = declarations[type];
            EnsureStaticCompanion(type, syntax, fileCtx, env, processed);
            SynthesizeInitFieldMethods(type, syntax, fileCtx, env);
            SynthesizeInitWrapper(type, syntax, fileCtx, env);
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

        // ===== 阶段 A：..init.field.<名> 合成（§9.7 字段初始化器方法）=====

        // 本类型每个带声明初始值的实例字段一个 priv+compiler-generated
        // 实例 void 方法，体 = this.<存储槽> = 初值（声明点隔离语境绑定，
        // 复用 BindFieldInitializer）。override 字段写基类槽
        // （OverriddenField）；其余字段存储槽即自身。幂等
        private static void SynthesizeInitFieldMethods(TypeSymbol type, ASTNode syntax,
            FileContext fileCtx, BindEnvironment env)
        {
            if (type.Methods.Any(m => m.Name.StartsWith(BilSpellings.InitFieldMethodPrefix,
                    StringComparison.Ordinal)))
            {
                return;
            }
            foreach (var (field, variable) in CollectInstanceFieldsWithInitializers(
                syntax, type, env))
            {
                var storage = field.OverriddenField ?? field;
                if (storage.FieldType is not { } fieldType || fieldType is ErrorTypeSymbol)
                {
                    continue;   // 毒化静默
                }
                var value = BindFieldInitializer(field, variable, fileCtx, env);
                if (value == null) continue;    // 绑定失败（诊断已报）
                var method = new MethodSymbol(
                    BilSpellings.InitFieldMethodPrefix + field.Name, MethodKind.Regular,
                    owner: type, returnType: null)
                {
                    Accessibility = Accessibility.Private,
                    HasBody = true,
                    IsSynthetic = true,
                };
                type.Methods.Add(method);
                var assignment = new BoundAssignmentStatement(variable,
                    new BoundFieldAccessExpression(variable,
                        new BoundThisExpression(variable,
                            SymbolLookup.AsSelfConstructed(type, env.Unit.Symbols)!),
                        storage, fieldType),
                    value);
                env.SyntheticCellBodies.Add(new BoundFunctionBody(method,
                    Array.Empty<LocalSymbol>(), new BoundBlock(variable,
                        new List<BoundStatement> { assignment })));
            }
        }

        // 当前类型声明处带初始化器的实例字段（声明序）。仅 get 无 set
        // 无法经 setter 应用（P2 已诊断，防御跳过）。class/struct/
        // enum struct/wrapper 同规则；interface 空表。
        // 自 BindingDriver 迁入（阶段 1.7/1.8 共用）
        public static List<(FieldSymbol Field, VariableDeclarationASTNode Variable)>
            CollectInstanceFieldsWithInitializers(ASTNode typeNode, TypeSymbol type,
                BindEnvironment env)
        {
            var fields = new List<(FieldSymbol, VariableDeclarationASTNode)>();
            if (typeNode is not (ClassDeclarationASTNode or StructDeclarationASTNode
                or EnumStructDeclarationASTNode or WrapperDeclarationASTNode))
            {
                return fields;
            }
            foreach (var member in MembersOf(typeNode))
            {
                if (member is VariableDeclarationASTNode { Initializer: not null } variable
                    && env.Declarations.SymbolOf(variable) is FieldSymbol field
                    && !field.IsStatic && ReferenceEquals(field.Owner, type)
                    && !(field.Getter != null && field.Setter == null))
                {
                    fields.Add((field, variable));
                }
            }
            return fields;
        }

        // ===== 阶段 B：..init.wrapper 闭包缝合（§9.7 修订）=====

        // 本体 = 继承闭包（基→本）全部 wrapper 安装（Entity 按定义去重、
        // 派生应用覆盖同名基类应用——§14.9 重申同实参）+ 闭包全部
        // ..init.field.* 调用（按字段名去重——同名字段只剩 override 一
        // 族（双侧带初始值的 hiding 已被 P2 拒绝），调基类最早声明符号、
        // 虚派发选中最高派生实现）。无 super(...)：基类字段初值与 wrapper
        // 已由本缝合覆盖。闭包无任何工作时（无 wrapper、无字段初始值）
        // 不合成。幂等
        private static void SynthesizeInitWrapper(TypeSymbol type, ASTNode syntax,
            FileContext fileCtx, BindEnvironment env)
        {
            if (type.Methods.Any(m => m.Name == BilSpellings.InitWrapperMethodName))
                return;

            var closure = InheritanceClosure(type);
            var statements = new List<BoundStatement>();
            var scope = new Scope(null);
            var initWrapper = NewInitWrapperMethod(type, Array.Empty<ParameterSymbol>());
            var ctx = new BindContext(initWrapper, fileCtx, type);

            // Entity 应用（基→本；同 wrapper 定义去重，派生应用原位替换）
            var entityApps = new List<WrapperApplication>();
            foreach (var definition in closure)
            {
                foreach (var app in definition.AppliedWrappers)
                {
                    var existing = entityApps.FindIndex(a =>
                        ReferenceEquals(a.WrapperDefinition, app.WrapperDefinition));
                    if (existing >= 0) entityApps[existing] = app;
                    else entityApps.Add(app);
                }
            }
            foreach (var app in entityApps)
            {
                var args = BindInitArgsInScope(app, scope, ctx, env, syntax);
                if (args == null) continue;
                statements.Add(new BoundNewWrapperStatement(app.Syntax ?? syntax,
                    BoundNewWrapperKind.Entity, app.Wrapper, null, args));
            }

            foreach (var definition in closure)
            {
                // 实例字段 Value 应用
                foreach (var field in definition.Fields)
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

                // 实例方法 Method 应用（含继承来的——基类未被 override 方法
                // 的 wrapper 同样安装到子类实例；companion 实例方法不在本类
                // Methods 表，由 companion 自身合成覆盖）
                foreach (var m in definition.Methods)
                {
                    if (m.IsStatic || m.Name == BilSpellings.InitWrapperMethodName
                        || m.Name.StartsWith(BilSpellings.InitFieldMethodPrefix,
                            StringComparison.Ordinal)) continue;
                    foreach (var app in m.AppliedWrappers)
                    {
                        var args = BindInitArgsInScope(app, scope, ctx, env, syntax);
                        if (args == null) continue;
                        statements.Add(new BoundNewWrapperStatement(app.Syntax ?? syntax,
                            BoundNewWrapperKind.Method, app.Wrapper, m, args));
                    }
                }
            }

            // 字段初值（基→本）：按名字段去重、调基类最早声明的
            // ..init.field.<名> 符号（虚派发选中最高派生 override）
            var initFieldCalls = new Dictionary<string, MethodSymbol>();
            foreach (var definition in closure)
            {
                foreach (var m in definition.Methods)
                {
                    if (!m.Name.StartsWith(BilSpellings.InitFieldMethodPrefix,
                            StringComparison.Ordinal)) continue;
                    var fieldName = m.Name.Substring(BilSpellings.InitFieldMethodPrefix.Length);
                    if (!initFieldCalls.ContainsKey(fieldName))
                    {
                        initFieldCalls[fieldName] = m;
                    }
                }
            }
            if (statements.Count == 0 && initFieldCalls.Count == 0) return;

            type.Methods.Add(initWrapper);
            foreach (var call in initFieldCalls.Values)
            {
                statements.Add(new BoundCallStatement(syntax, call,
                    new List<BoundExpression>(),
                    new BoundThisExpression(syntax,
                        SymbolLookup.AsSelfConstructed(type, env.Unit.Symbols)!)));
            }

            env.SyntheticCellBodies.Add(new BoundFunctionBody(initWrapper,
                Array.Empty<LocalSymbol>(), new BoundBlock(syntax, statements)));
        }

        // 继承闭包（基→本，定义级；内建根/毒化环防御截断）
        private static List<TypeSymbol> InheritanceClosure(TypeSymbol type)
        {
            var chain = new List<TypeSymbol>();
            var seen = new HashSet<TypeSymbol>();
            for (var t = type; t != null && t is not ErrorTypeSymbol; t = t.BaseType)
            {
                var definition = t.ConstructedFrom ?? t;
                if (definition.IsBuiltin || !seen.Add(definition)) break;
                chain.Add(definition);
            }
            chain.Reverse();
            return chain;
        }

        // §8.7：确保声明类有 companion（幂等）。含静态 Method wrapper 或
        // 静态 Value wrapper 字段时创建；迁入方法/字段并合成 companion 的
        // init（cell 构造）与 ..init.wrapper（Method wrapper 安装）。
        // companion 自身即时走完阶段 A/B（无基类链、无字段初始值）
        private static void EnsureStaticCompanion(TypeSymbol host, ASTNode syntax,
            FileContext fileCtx, BindEnvironment env, HashSet<TypeSymbol> processed)
        {
            var hasWrappedStaticMethod = host.Methods.Any(m => m.IsStatic
                && m.AppliedWrappers.Count > 0 && m.Companion == null);
            var hasWrappedStaticField = host.Fields.Any(f => f.IsStatic
                && f.CellStorage != null && f.CompanionCellField == null);
            if (!hasWrappedStaticMethod && !hasWrappedStaticField) return;

            var info = host.CompanionInfo;
            if (info == null)
            {
                info = new StaticCompanionInfo(CreateCompanionType(host, syntax, fileCtx, env));
                host.CompanionInfo = info;
                // companion 自身也持同一份（自指——识别 singleton 身份供 P4b
                // 声明收集与 compiler-generated 投影）
                info.CompanionType.CompanionInfo = info;
            }
            var companion = info.CompanionType;

            foreach (var staticMethod in host.Methods.ToList())
            {
                if (!staticMethod.IsStatic || staticMethod.AppliedWrappers.Count == 0
                    || staticMethod.Companion != null) continue;
                SynthesizeCompanionMethod(info, staticMethod);
            }
            // 静态字段需要 AST 初始化器——遍历类型成员 AST 拿声明节点
            foreach (var member in MembersOf(syntax))
            {
                if (member is not VariableDeclarationASTNode variable) continue;
                if (env.Declarations.SymbolOf(variable) is not FieldSymbol field) continue;
                if (!field.IsStatic || field.CellStorage == null
                    || field.CompanionCellField != null) continue;
                SynthesizeCompanionField(info, field, variable, syntax, fileCtx, env);
            }

            // companion 自身 init（cell 构造）与 ..init.wrapper（Method wrapper 安装）
            SynthesizeCompanionInit(info, syntax, env);
            if (processed.Add(companion))
            {
                SynthesizeInitFieldMethods(companion, syntax, fileCtx, env);
                SynthesizeInitWrapper(companion, syntax, fileCtx, env);
            }
        }

        // companion 类型：宿主类的嵌套类（canonical 命名空间::外层...companion），
        // 无 UUID；singleton+shared+compiler-generated；共享宿主类型链泛型参数
        private static TypeSymbol CreateCompanionType(TypeSymbol host, ASTNode syntax,
            FileContext fileCtx, BindEnvironment env)
        {
            var objectType = env.Unit.Symbols.Bootstrap.Object;
            var companion = new TypeSymbol(BilSpellings.CompanionTypeName, TypeKind.Class,
                ns: fileCtx.Namespace, declaringType: host, baseType: objectType, isShared: true)
            {
                Accessibility = Accessibility.Public,
                IsSingleton = true,
            };
            var hostGenerics = new List<GenericParameterSymbol>();
            for (var t = host; t != null; t = t.DeclaringType)
            {
                hostGenerics.AddRange(t.GenericParameters);
            }
            foreach (var gp in hostGenerics)
            {
                companion.GenericParameters.Add(gp);
            }
            return companion;
        }

        // 静态方法迁入 companion：实例方法沿用简单名、签名拷贝、wrapper 改挂；
        // 体稍后绑定（BindingDriver.BindCompanionInstanceBody）
        private static void SynthesizeCompanionMethod(StaticCompanionInfo info,
            MethodSymbol shell)
        {
            var companion = info.CompanionType;
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
            shell.Companion = new StaticMethodCompanionInfo(companion, instance, shell);
        }

        // 静态字段的 cell 落地 companion：cell 对象成为 companion 实例字段；
        // 字段初始化器在声明点静态语境绑定（仿参数默认值/字段 wrapper 实参）
        private static void SynthesizeCompanionField(StaticCompanionInfo info,
            FieldSymbol field, VariableDeclarationASTNode variable, ASTNode syntax,
            FileContext fileCtx, BindEnvironment env)
        {
            var storage = field.CellStorage!;
            var companion = info.CompanionType;
            var cellField = new FieldSymbol(field.Name, owner: companion,
                fieldType: storage.CellType)
            {
                Accessibility = Accessibility.Public,
            };
            companion.Fields.Add(cellField);
            var entry = new StaticFieldCompanionEntry(field, cellField, storage);
            info.Fields.Add(entry);
            field.CompanionCellField = cellField;
            entry.InitValue = BindFieldInitializer(field, variable, fileCtx, env);
        }

        // 字段初值在声明点隔离上下文绑定：无 this/无形参。实例字段可见
        // 宿主泛型参数；真正静态/全局字段仍受静态泛型限制。公开供 cell
        // 及普通实例字段的真实初始化器复用。
        public static BoundExpression? BindFieldInitializer(FieldSymbol field,
            VariableDeclarationASTNode variable, FileContext fileCtx, BindEnvironment env)
        {
            if (variable.Initializer == null) return null;
            if (field.FieldType is not { } expectedType || expectedType is ErrorTypeSymbol)
            {
                return null;
            }
            var host = new MethodSymbol(".field.init.bind", MethodKind.Regular,
                owner: field.Owner, ns: field.Namespace,
                isStatic: field.Owner == null || field.IsStatic)
            {
                HasBody = false,
                IsSynthetic = true,
            };
            var ctx = new BindContext(host, fileCtx, field.Owner, isDefaultValueContext: true);
            var scope = new Scope(null);
            var value = ExpressionDispatcher.Visit(variable.Initializer.Expression,
                scope, ctx, env, expectedType as TypeSymbol);
            if (value == null) return null;
            if (ctx.Locals.Count > 0)
            {
                env.Error(variable.Initializer.Span ?? variable.Span,
                    "P3: field initializer with local declarations is not supported");
                return null;
            }
            if (!SymbolLookup.IsAssignable(value.Type, expectedType, env))
            {
                env.Error(variable.Initializer.Span ?? variable.Span,
                    $"Field '{field.Name}' initializer must be of type " +
                    $"'{BoundAnalysis.TypeDisplay(expectedType)}', got " +
                    $"'{BoundAnalysis.TypeDisplay(value.Type)}'");
                return null;
            }
            // W5（SYNTAX §9.3）：全局/静态字段初值不得直接引用其它全局/
            // 静态字段（含 const、含自身）。frontend 不对 const 做编译期
            // 折叠，口径一律禁止。函数/方法调用与 lambda 体是逃逸口，
            // 不静态追踪。实例字段初值不在此列。
            CheckStaticFieldInitializerReferences(field, value, env);
            return value;
        }

        // 全局（Owner == null）或 static 字段的初值表达式：扫到
        // BoundFieldReferenceExpression 即直接引用，落诊断。lambda 体
        // 不下行（与顶层函数同属逃逸口）。
        private static void CheckStaticFieldInitializerReferences(FieldSymbol field,
            BoundExpression value, BindEnvironment env)
        {
            if (field.Owner != null && !field.IsStatic) return;
            ScanExpression(value, field, env);
        }

        private static void ScanExpression(BoundExpression expression, FieldSymbol initializing,
            BindEnvironment env)
        {
            switch (expression)
            {
                case BoundFieldReferenceExpression fieldRef:
                    env.Error(fieldRef.Syntax.Span,
                        $"Initializer of global/static field '{FieldDisplay(initializing)}' " +
                        $"cannot reference global/static field '{FieldDisplay(fieldRef.Field)}'");
                    return;
                case BoundLambdaExpression:
                    return;
                case BoundLiteralExpression:
                case BoundValueReferenceExpression:
                case BoundThisExpression:
                case BoundSelfExpression:
                case BoundSafeAccessReceiverExpression:
                    return;
                case BoundBinaryExpression binary:
                    ScanExpression(binary.Left, initializing, env);
                    ScanExpression(binary.Right, initializing, env);
                    return;
                case BoundUnaryExpression unary:
                    ScanExpression(unary.Operand, initializing, env);
                    return;
                case BoundCallExpression call:
                    foreach (var argument in call.Arguments)
                        ScanExpression(argument, initializing, env);
                    if (call.IndirectTarget != null)
                        ScanExpression(call.IndirectTarget, initializing, env);
                    return;
                case BoundNewExpression newExpression:
                    foreach (var argument in newExpression.Arguments)
                        ScanExpression(argument, initializing, env);
                    return;
                case BoundDynamicNewExpression dynamicNew:
                    if (dynamicNew.TypeValue != null)
                        ScanExpression(dynamicNew.TypeValue, initializing, env);
                    foreach (var argument in dynamicNew.Arguments)
                        ScanExpression(argument, initializing, env);
                    return;
                case BoundIfExpression ifExpression:
                    ScanExpression(ifExpression.Condition, initializing, env);
                    ScanBlock(ifExpression.TrueBranch.Block, initializing, env);
                    ScanBlock(ifExpression.FalseBranch.Block, initializing, env);
                    return;
                case BoundCompoundAssignmentExpression compound:
                    ScanExpression(compound.Target, initializing, env);
                    ScanExpression(compound.Value, initializing, env);
                    return;
                case BoundInstanceCallExpression instanceCall:
                    ScanExpression(instanceCall.Receiver, initializing, env);
                    foreach (var argument in instanceCall.Arguments)
                        ScanExpression(argument, initializing, env);
                    return;
                case BoundFieldAccessExpression fieldAccess:
                    ScanExpression(fieldAccess.Receiver, initializing, env);
                    return;
                case BoundIndexExpression index:
                    ScanExpression(index.Receiver, initializing, env);
                    ScanExpression(index.Index, initializing, env);
                    return;
                case BoundEnumCaseExpression enumCase:
                    foreach (var argument in enumCase.Arguments)
                        ScanExpression(argument, initializing, env);
                    return;
                case BoundWrapperAccessExpression wrapperAccess:
                    ScanExpression(wrapperAccess.Receiver, initializing, env);
                    return;
                case BoundInnerCallExpression innerCall:
                    foreach (var argument in innerCall.Arguments)
                        ScanExpression(argument, initializing, env);
                    return;
                case BoundAwaitExpression awaitExpression:
                    ScanExpression(awaitExpression.Operand, initializing, env);
                    return;
                case BoundSuperCallExpression superCall:
                    foreach (var argument in superCall.Arguments)
                        ScanExpression(argument, initializing, env);
                    return;
                case BoundSwitchExpression switchExpression:
                    ScanExpression(switchExpression.Selector, initializing, env);
                    foreach (var switchCase in switchExpression.Cases)
                    {
                        ScanExpression(switchCase.Match, initializing, env);
                        ScanBlock(switchCase.Body.Block, initializing, env);
                    }
                    ScanBlock(switchExpression.DefaultBody.Block, initializing, env);
                    return;
                case BoundSwitchPlaceholderExpression placeholder:
                    ScanExpression(placeholder.Selector, initializing, env);
                    return;
                case BoundCastExpression cast:
                    ScanExpression(cast.Source, initializing, env);
                    return;
                case BoundSmartCastExpression smartCast:
                    ScanExpression(smartCast.Operand, initializing, env);
                    return;
                case BoundSeqExpression seqExpression:
                    foreach (var binding in seqExpression.UsingBindings)
                        ScanExpression(binding.Initializer, initializing, env);
                    ScanBlock(seqExpression.Body.Block, initializing, env);
                    return;
                case BoundSafeAccessExpression safeAccess:
                    ScanExpression(safeAccess.Receiver, initializing, env);
                    ScanExpression(safeAccess.Access, initializing, env);
                    return;
                case BoundNullFallbackExpression nullFallback:
                    ScanExpression(nullFallback.Left, initializing, env);
                    ScanExpression(nullFallback.Right, initializing, env);
                    return;
                case BoundTypeCheckExpression typeCheck:
                    ScanExpression(typeCheck.Operand, initializing, env);
                    if (typeCheck.TargetValue != null)
                        ScanExpression(typeCheck.TargetValue, initializing, env);
                    return;
                case BoundTypeOfExpression typeOf:
                    if (typeOf.Operand != null)
                        ScanExpression(typeOf.Operand, initializing, env);
                    return;
                case BoundPlaceOfExpression placeOf:
                    ScanExpression(placeOf.Operand, initializing, env);
                    return;
                case BoundVarArgsArgument varArgs:
                    foreach (var value in varArgs.Values)
                        ScanExpression(value, initializing, env);
                    foreach (var (_, named) in varArgs.NamedValues)
                        ScanExpression(named, initializing, env);
                    return;
                default:
                    throw new CompilerInternalException(
                        "全局/静态字段初值禁令遍历遇未知 Bound 表达式节点: " +
                        expression.GetType().Name);
            }
        }

        private static void ScanBlock(BoundBlock? block, FieldSymbol initializing,
            BindEnvironment env)
        {
            if (block == null) return;
            foreach (var statement in block.Statements)
                ScanStatement(statement, initializing, env);
        }

        private static void ScanStatement(BoundStatement statement, FieldSymbol initializing,
            BindEnvironment env)
        {
            switch (statement)
            {
                case BoundBlock nested:
                    ScanBlock(nested, initializing, env);
                    return;
                case BoundLocalDeclarationStatement declaration:
                    if (declaration.Initializer != null)
                        ScanExpression(declaration.Initializer, initializing, env);
                    return;
                case BoundDestructuringDeclarationStatement destructuring:
                    ScanExpression(destructuring.Initializer, initializing, env);
                    return;
                case BoundExpressionStatement expressionStatement:
                    ScanExpression(expressionStatement.Expression, initializing, env);
                    return;
                case BoundYieldStatement yield:
                    if (yield.Alarm != null)
                        ScanExpression(yield.Alarm, initializing, env);
                    return;
                case BoundCallStatement call:
                    foreach (var argument in call.Arguments)
                        ScanExpression(argument, initializing, env);
                    if (call.Receiver != null)
                        ScanExpression(call.Receiver, initializing, env);
                    if (call.IndirectTarget != null)
                        ScanExpression(call.IndirectTarget, initializing, env);
                    return;
                case BoundAssignmentStatement assignment:
                    ScanExpression(assignment.Target, initializing, env);
                    ScanExpression(assignment.Value, initializing, env);
                    return;
                case BoundReturnStatement returnStatement:
                    if (returnStatement.Value != null)
                        ScanExpression(returnStatement.Value, initializing, env);
                    return;
                case BoundIfStatement ifStatement:
                    ScanExpression(ifStatement.Condition, initializing, env);
                    ScanBlock(ifStatement.TrueBlock, initializing, env);
                    if (ifStatement.FalseBlock != null)
                        ScanBlock(ifStatement.FalseBlock, initializing, env);
                    return;
                case BoundReturnValueStatement returnValue:
                    ScanExpression(returnValue.Value, initializing, env);
                    return;
                case BoundLoop loop:
                    if (loop.Condition != null)
                        ScanExpression(loop.Condition, initializing, env);
                    if (loop.Iterable != null)
                        ScanExpression(loop.Iterable, initializing, env);
                    ScanBlock(loop.Body, initializing, env);
                    return;
                case BoundLoopControl:
                    return;
                case BoundSwitchStatement switchStatement:
                    ScanExpression(switchStatement.Selector, initializing, env);
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        ScanExpression(switchCase.Match, initializing, env);
                        ScanBlock(switchCase.Body, initializing, env);
                    }
                    ScanBlock(switchStatement.DefaultBody, initializing, env);
                    return;
                case BoundThrowStatement throwStatement:
                    ScanExpression(throwStatement.Exception, initializing, env);
                    return;
                case BoundTryStatement tryStatement:
                    ScanBlock(tryStatement.TryBlock, initializing, env);
                    foreach (var catchClause in tryStatement.Catches)
                        ScanBlock(catchClause.Body, initializing, env);
                    if (tryStatement.FinallyBlock != null)
                        ScanBlock(tryStatement.FinallyBlock, initializing, env);
                    return;
                case BoundSeqStatement seqStatement:
                    foreach (var binding in seqStatement.UsingBindings)
                        ScanExpression(binding.Initializer, initializing, env);
                    ScanBlock(seqStatement.Body, initializing, env);
                    return;
                case BoundSeqExitStatement:
                    return;
                case BoundNewWrapperStatement newWrapper:
                    foreach (var argument in newWrapper.Arguments)
                        ScanExpression(argument, initializing, env);
                    return;
                default:
                    throw new CompilerInternalException(
                        "全局/静态字段初值禁令遍历遇未知 Bound 语句节点: " +
                        statement.GetType().Name);
            }
        }

        private static string FieldDisplay(FieldSymbol field)
        {
            return field.Owner != null ? field.Owner.Name + "." + field.Name : field.Name;
        }

        // companion 的 init：求值各静态字段初始化器并构造 cell（赋值到
        // companion 的 cell 实例字段）；cell 构造走 BoundNewExpression
        // （P4 NewExpressionRewriter 按 cell 存储补 new.wrapped 前缀）。
        // 无初始化器的 var 字段用空构造 init()（DefaultInit）
        private static void SynthesizeCompanionInit(StaticCompanionInfo info, ASTNode syntax,
            BindEnvironment env)
        {
            if (info.Fields.Count == 0) return;
            var companion = info.CompanionType;
            if (companion.Methods.Any(m => m.Kind == MethodKind.Init && m.Parameters.Count == 0))
            {
                return;
            }
            var init = new MethodSymbol("init", MethodKind.Init, owner: companion, returnType: null)
            {
                Accessibility = Accessibility.Public,
                HasBody = true,
                IsSynthetic = true,
            };
            companion.Methods.Add(init);
            var statements = new List<BoundStatement>();
            foreach (var entry in info.Fields)
            {
                var storage = entry.Storage;
                BoundExpression? cellNew = null;
                if (entry.InitValue != null)
                {
                    cellNew = new BoundNewExpression(syntax, storage.CellType,
                        storage.ValueInit, new List<BoundExpression> { entry.InitValue });
                }
                else if (storage.DefaultInit != null)
                {
                    cellNew = new BoundNewExpression(syntax, storage.CellType,
                        storage.DefaultInit, new List<BoundExpression>());
                }
                if (cellNew == null) continue;
                statements.Add(new BoundAssignmentStatement(syntax,
                    new BoundFieldAccessExpression(syntax,
                        new BoundThisExpression(syntax, companion), entry.CellField,
                        storage.CellType),
                    cellNew));
            }
            env.SyntheticCellBodies.Add(new BoundFunctionBody(init,
                Array.Empty<LocalSymbol>(), new BoundBlock(syntax, statements)));
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

        // 生成 cell value 字段 wrapper 应用安装语句（绑定实参直接求值形态）：
        // 供 singleton cell（全局字段，裁定 1）的无参 ..init.wrapper 安装
        // wrapper——与 SynthesizeForCell 的 ..init.wrapper 参数引用形态相对。
        // 声明点绑定失败的 app（BoundInitArguments == null，诊断已报）跳过
        private static List<BoundStatement> SynthesizeCellWrapperInstallStatements(
            FieldSymbol valueField, ASTNode syntax)
        {
            var statements = new List<BoundStatement>();
            foreach (var app in valueField.AppliedWrappers)
            {
                if (app.BoundInitArguments == null) continue;
                statements.Add(new BoundNewWrapperStatement(app.Syntax ?? syntax,
                    BoundNewWrapperKind.Field, app.Wrapper, valueField,
                    app.BoundInitArguments.ToList()));
            }
            return statements;
        }

        // singleton cell（全局字段，裁定 1）的 ..init.wrapper 合成：value 字段
        // 带 wrapped(W) 时合成无参 ..init.wrapper（priv + compiler-generated
        // 实例 void 方法），体内按 outer→inner 发 new.wrapper.field。wrapper
        // 实参不依赖函数局部，已在声明点按全局作用域绑定（BoundInitArguments），
        // 此处直接在 ..init.wrapper 体内求值（与类型级/静态 companion 的
        // ..init.wrapper 同形态）——与 SynthesizeForCell 的有参 new.wrapped
        // 传参形态相对（局部场景实参依赖函数局部，须经构造点传入）。无参
        // init() 只求值字段初值、写 value 字段，不内联 wrapper 安装（§14.5）
        public static void SynthesizeForSingletonCell(CellStorageInfo storage, ASTNode syntax,
            BindEnvironment env)
        {
            var valueField = storage.ValueField;
            if (valueField.AppliedWrappers.Count == 0) return;
            var cellClass = storage.CellClass;
            if (cellClass.Methods.Any(m => m.Name == BilSpellings.InitWrapperMethodName))
            {
                return;
            }

            var method = NewInitWrapperMethod(cellClass, Array.Empty<ParameterSymbol>());
            cellClass.Methods.Add(method);
            storage.InitWrapper = method;

            var statements = SynthesizeCellWrapperInstallStatements(valueField, syntax);
            env.SyntheticCellBodies.Add(new BoundFunctionBody(method,
                Array.Empty<LocalSymbol>(), new BoundBlock(syntax, statements)));
        }

        // 在给定作用域绑定 wrapper 应用的 init 实参（§3.3 重载）
        public static List<BoundExpression>? BindInitArgsInScope(WrapperApplication app,
            Scope scope, BindContext ctx, BindEnvironment env, ASTNode fallbackSyntax)
        {
            if (env.WrapperArguments(app) is { } cached)
                return cached.ToList();

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
                env.DefaultWrapperConstructions.TryAdd(app, span);
                env.StoreWrapperArguments(app, Array.Empty<BoundExpression>());
                return new List<BoundExpression>();
            }

            // 用注解节点或 fallback 作为 Resolve 诊断锚点
            var anchor = (ASTNode?)syntax ?? fallbackSyntax;
            var resolved = OverloadResolution.Resolve(anchor, inits, argNodes, scope, ctx, env,
                receiverType: app.Wrapper);
            if (resolved == null) return null;
            env.StoreWrapperArguments(app, resolved.Value.Arguments);
            return resolved.Value.Arguments;
        }

        // 默认 wrapper 安装也属于构造使用点，不能绕过普通实体的字段 DA。
        // 此时全部真实初始化方法已合成；有 init 的路径由 CheckInitBody 担保。
        public static void CheckDefaultWrapperConstructions(BindEnvironment env)
        {
            foreach (var (app, span) in env.DefaultWrapperConstructions)
            {
                if (InitFieldDa.HasCheckedDefaultInit(app.Wrapper, env)) continue;
                var missing = InitFieldDa.RequiredFields(app.Wrapper, env);
                if (missing.Count == 0) continue;
                env.Error(span,
                    $"Wrapper '{app.WrapperDefinition.Name}' has no constructor that assigns " +
                    $"non-nullable field '{missing[0].Name}' (§9.3: declare an init that " +
                    "assigns it, add a declaration initializer, or make the field Nullable)");
            }
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

        // P18（字段定值赋值分析 DA）预留口径：字段是否带声明初始值 ==
        // 其声明类型的 Methods 上存在 ..init.field.<名>（字段 override 时
        // 沿继承闭包同族可查——最高派生实现即实际写入者）；
        // ..init.wrapper 体内对 ..init.field.* 的调用即该字段在 wrapper
        // 阶段（任何 init 体之前）的赋值点，DA 可据此把带初始值字段判定
        // 为「构造进入时已赋值」
        public static bool HasFieldInitializerMethod(TypeSymbol declaringType, string fieldName)
        {
            var wanted = BilSpellings.InitFieldMethodPrefix + fieldName;
            for (var t = declaringType; t != null && t is not ErrorTypeSymbol; t = t.BaseType)
            {
                var definition = t.ConstructedFrom ?? t;
                if (definition.IsBuiltin) break;
                if (definition.Methods.Any(m => m.Name == wanted)) return true;
            }
            return false;
        }

        internal static MethodSymbol NewInitWrapperMethod(TypeSymbol owner,
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
