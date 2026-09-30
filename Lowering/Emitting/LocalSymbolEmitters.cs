using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using RigiCompiler.Bil;

namespace RigiCompiler
{
    // ===== LocalSymbols（§8）=====
    // 符号图遍历发射（命名空间平铺/类型树/成员声明/内建 ext 成员），
    // 不依赖 EmitContext——模块级静态方法，env 显式传。
    // 自旧 EmitSession 同名方法迁移，行为不变。
    // M57 起声明种类/修饰符为 Bil/ 强类型（拼写唯一定义在 BilSpellings
    // 与各 BilModifier.Render）；本文件只做 Semantic 枚举 → Bil 枚举映射。
    internal static class LocalSymbolEmitters
    {
        // 命名空间平铺：本空间类型（含 NestedTypes 递归）→ 子命名空间递归 →
        // 本空间全局字段/函数裸条目（§8.4.1：不包裹在 .type 中）。
        // §17 切分：进入每个命名空间时把 env.CurrentSliceNs 切到本空间，
        // 全部声明经 env.AddLocalSymbol 同步记录进 merged 与对应切片
        public static void EmitNamespace(NamespaceSymbol ns, EmitEnvironment env)
        {
            env.CurrentSliceNs = ns.FullName;
            foreach (var type in ns.Types)
            {
                EmitTypeTree(type, env);
            }
            foreach (var child in ns.ChildNamespaces)
            {
                EmitNamespace(child, env);
                // 子空间递归会改切 CurrentSliceNs——回到本空间
                env.CurrentSliceNs = ns.FullName;
            }
            foreach (var field in ns.Fields)
            {
                // 全局 wrapped 字段（裁定 1）：cell 子类即 singleton，BIL 不再
                // 发全局字段声明（访问经 cell 单例 getValue/setValue）
                if (field.CellStorage != null && field.Owner == null) continue;
                env.AddLocalSymbol(EmitFieldDeclaration(field));
                foreach (var accessor in EmitFieldAccessorDeclarations(field))
                {
                    env.AddLocalSymbol(accessor);
                }
            }
            foreach (var method in ns.Methods)
            {
                env.AddLocalSymbol(EmitMethodDeclaration(method));
            }
        }

        // 切片归属命名空间（§17 切分）：全局符号取自身 Namespace；成员取
        // 宿主类型根（嵌套链顶端）的命名空间；ext 成员（Namespace 已被 P2
        // AttachToExtTarget 清空）同归宿主类型根——内建类型上的 ext 成员
        // 因此落 core 切片（声明点命名空间在符号上无存；对合并运行无影响）
        internal static string SliceNsOf(MethodSymbol method) =>
            method.SliceNamespaceOverride
            ?? method.Namespace?.FullName
            ?? RootNsOf(method.Owner);

        internal static string SliceNsOf(FieldSymbol field) =>
            field.Namespace?.FullName ?? RootNsOf(field.Owner);

        internal static string RootNsOf(TypeSymbol? type)
        {
            while (type?.DeclaringType != null) type = type.DeclaringType;
            return type?.Namespace?.FullName ?? "";
        }

        public static BilSimpleMemberDeclaration EmitSyntheticMethodDeclaration(MethodSymbol method)
        {
            return EmitMethodDeclaration(method);
        }

        // lambda 隐藏类声明发射（SYNTAX §5.2 对象模型，EmittingDriver 调用）：
        // 与普通类型声明同路径——字段（.capture.* cell 字段）/init/$$call
        // 成员声明由 type.Fields/Methods 表驱动
        public static BilTypeDeclaration EmitSyntheticTypeDeclaration(TypeSymbol type,
            EmitEnvironment env)
        {
            return EmitTypeDeclaration(type, env);
        }

        // 内建类型的 ext 成员声明（S7c-2）：内建类型自身不声明
        // （EmitTypeTree 跳过 IsBuiltin——基元经 BIL 别名投影而非符号
        // 引用），但 P2 注册到其上的 ext 成员（如 .bootstrap 的
        // EnumerateInRange）必须声明，否则其 fn 定义引用了未声明符号——
        // 以 §8.4.1 裸条目形态输出（canonical 自带宿主前缀，段内位置
        // 任意）。枚举经 BootstrapSymbols 公共 TypeSymbol 属性反射——
        // 新内建类型自动覆盖，不维护手列清单
        public static void EmitBuiltinExtMembers(EmitEnvironment env)
        {
            foreach (var property in typeof(BootstrapSymbols).GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                // Exception 懒解析：无 stdlib 时 getter 抛；源码化后亦非 IsBuiltin
                if (property.Name == "Exception") continue;
                if (property.GetValue(env.Unit.Symbols.Bootstrap) is not TypeSymbol
                    { IsBuiltin: true } builtinType) continue;
                foreach (var field in builtinType.Fields)
                {
                    if (field.ExtTargetPath == null) continue;
                    env.CurrentSliceNs = RootNsOf(builtinType);
                    env.AddLocalSymbol(EmitFieldDeclaration(field));
                    // 访问器声明随字段槽驱动（同 EmitNamespace/EmitTypeDeclaration
                    // 形态）——缺了它访问器 fn 定义将被 §21.2 拒绝（M80 修复：
                    // SYNTAX §4.4 的 ext var + get/set 示例形态端到端必挂）
                    foreach (var accessor in EmitFieldAccessorDeclarations(field))
                    {
                        env.AddLocalSymbol(accessor);
                    }
                }
                foreach (var method in builtinType.Methods)
                {
                    if (method.ExtTargetPath == null) continue;
                    env.CurrentSliceNs = RootNsOf(builtinType);
                    env.AddLocalSymbol(EmitMethodDeclaration(method));
                }
            }
        }

        // 内建声明不改变固定类型布局；源码成员按普通函数体绑定与发射。
        public static void EmitBuiltinNativeMethods(EmitEnvironment env)
        {
            if (!env.Unit.SourceFiles.Any(f => f.IsIntrinsicDeclarations)) return;
            foreach (var builtin in env.Unit.Symbols.Bootstrap.SourceTypes.Values.Where(t => t.IsBuiltin))
            {
                // 外部声明只描述固定 ABI 类型的源码成员/能力，不定义物理布局。
                // 实现函数仍在本地段，外部描述接受与本地声明相同的签名验证。
                var description = new BilTypeDeclaration("core::" + builtin.Name, MapTypeKind(builtin.Kind));
                description.GenericParameters.AddRange(builtin.GenericParameters.Select(p => p.Name));
                foreach (var app in builtin.AppliedWrappers)
                    description.Modifiers.Add(new BilWrappedModifier(CanonicalSymbolPrinter.PrintType(app.Wrapper)));
                foreach (var iface in builtin.Interfaces)
                    description.ImplementsTypes.Add(CanonicalSymbolPrinter.PrintType(iface));
                foreach (var method in builtin.Methods)
                {
                    if (method.ExtTargetPath != null) continue;
                    env.CurrentSliceNs = RootNsOf(builtin);
                    var member = EmitMethodDeclaration(method);
                    if (!method.IsSynthetic) env.AddLocalSymbol(member);
                    description.Members.Add(member);
                }
                env.CurrentSliceNs = RootNsOf(builtin);
                if (builtin.AppliedWrappers.Count != 0) env.AddExternalSymbol(description);
            }
        }

        private static void EmitTypeTree(TypeSymbol type, EmitEnvironment env)
        {
            // 内建 bootstrap 符号（基元/层级根）不声明：经 BIL 别名投影引用；
            // ErrorType 是毒化单例，同样不进符号段
            if (type.IsBuiltin || type is ErrorTypeSymbol) return;
            if (type.SourceFile is { IsIntrinsicDeclarations: true } source
                && !env.Unit.SourceFiles.Contains(source)) return;
            if (type.BilAlias == ".handle")
            {
                // 仅内部存储固定布局；公开泛型 Handle 按普通类完整发射。
                var handle = new BilTypeDeclaration(".handle", BilTypeKind.Class);
                handle.Modifiers.Add(new BilAccessibilityModifier(BilAccessibility.Public));
                handle.Modifiers.Add(new BilKeywordModifier(BilKeyword.Shared));
                handle.Modifiers.Add(new BilKeywordModifier(BilKeyword.Unsafe));
                handle.Modifiers.Add(new BilKeywordModifier(BilKeyword.CompilerGenerated));
                env.AddLocalSymbol(handle);
                return;
            }
            env.AddLocalSymbol(EmitTypeDeclaration(type, env));
            foreach (var nested in type.NestedTypes)
            {
                EmitTypeTree(nested, env);
            }
        }

        private static BilTypeDeclaration EmitTypeDeclaration(TypeSymbol type, EmitEnvironment env)
        {
            var declaration = new BilTypeDeclaration(CanonicalSymbolPrinter.PrintType(type),
                MapTypeKind(type.Kind));
            // S9e：泛型参数名列表（§8.2 generic(...) 子句，声明序）
            foreach (var genericParameter in type.GenericParameters)
            {
                declaration.GenericParameters.Add(genericParameter.Name);
                declaration.GenericVariances.Add(genericParameter.Variance switch
                {
                    GenericVariance.Out => BilGenericVariance.Out,
                    GenericVariance.In => BilGenericVariance.In,
                    _ => BilGenericVariance.None,
                });
            }
            // 修饰符（§8.2）：访问（全显式）→ open/abstract/singleton → rich/shared
            // wrapper 与 struct 一样只输出源码显式声明的 rich。
            declaration.Modifiers.Add(new BilAccessibilityModifier(MapAccessibility(type.Accessibility)));
            if (type.IsOpen) declaration.Modifiers.Add(new BilKeywordModifier(BilKeyword.Open));
            if (type.IsAbstract) declaration.Modifiers.Add(new BilKeywordModifier(BilKeyword.Abstract));
            if (type.IsSingleton) declaration.Modifiers.Add(new BilKeywordModifier(BilKeyword.Singleton));
            if (type.IsRich) declaration.Modifiers.Add(new BilKeywordModifier(BilKeyword.Rich));
            if (type.IsShared) declaration.Modifiers.Add(new BilKeywordModifier(BilKeyword.Shared));
            if (type.IsUnsafe) declaration.Modifiers.Add(new BilKeywordModifier(BilKeyword.Unsafe));
            // M109b-2 §8.7：companion singleton 建议 compiler-generated（仅
            // companion 自身——宿主类持 CompanionInfo 作反向链接不投影）；
            // 全局字段 singleton cell（裁定 1）同样标记 compiler-generated
            if ((type.CompanionInfo != null
                    && ReferenceEquals(type.CompanionInfo.CompanionType, type))
                || (type.CellStorage != null && type.IsSingleton)
                || type.Name == BilSpellings.SerializableIfaceName
                || type.Name == BilSpellings.SerializableTokenName)
            {
                declaration.Modifiers.Add(new BilKeywordModifier(BilKeyword.CompilerGenerated));
            }
            // §8.3.1 wrapped(W)：应用标记 outer→inner = 列表序
            foreach (var application in type.AppliedWrappers)
            {
                declaration.Modifiers.Add(new BilWrappedModifier(
                    CanonicalSymbolPrinter.PrintType(application.Wrapper)));
            }
            // extends：与种类默认基类相同则省略（P1 建壳即填默认基类——
            // class→Object / struct→ValueType / enum struct→Enum /
            // wrapper→Wrapper；P2 仅在源码显式继承时覆盖），不同才输出
            if (!ReferenceEquals(type.BaseType, DefaultBaseOf(type, env)))
            {
                declaration.ExtendsType = CanonicalSymbolPrinter.PrintType(type.BaseType!);
            }
            foreach (var iface in type.Interfaces)
            {
                declaration.ImplementsTypes.Add(CanonicalSymbolPrinter.PrintType(iface));
            }
            foreach (var field in type.Fields)
            {
                // 静态字段的 cell 存储已落到 companion 实例字段（§8.7）——
                // 宿主不再发静态字段声明（访问经 companion 单例）
                if (field.CompanionCellField != null) continue;
                declaration.Members.Add(EmitFieldDeclaration(field));
                foreach (var accessor in EmitFieldAccessorDeclarations(field))
                {
                    declaration.Members.Add(accessor);
                }
            }
            foreach (var method in type.Methods)
            {
                // enum struct 的无体 init（case 模板，SYNTAX §12.1）同样
                // 发射声明——P3 起映射赋值体合成（§9.3）为其产出 fn 定义
                declaration.Members.Add(EmitMethodDeclaration(method));
            }
            // enum case 声明（§8.5，S11；非 enum struct 的 Cases 恒空）：
            // 模板绑定失败的 case（HoleParameters null——P3 已诊断/泛型
            // 归口）跳过；洞签名（名 + 类型投影）按洞签名序。显式判别值
            // （-> N）登记 i32 标量资源（§19.1 判别值注记）发
            // discriminant res(R)，auto（符号上 Discriminant null）发
            // discriminant auto——auto 编号归 VM/Middleware 按声明序推导
            foreach (var enumCase in type.Cases)
            {
                if (enumCase.HoleParameters == null) continue;
                var caseParameters = enumCase.HoleParameters
                    .Select(hole => new BilCaseParameter(hole.Name,
                        CanonicalSymbolPrinter.PrintType(hole.Type)))
                    .ToArray();
                var discriminantResource = enumCase.Discriminant is decimal discriminant
                    ? EmittingFacility.RegisterScalarResource(BilScalarType.I32,
                        discriminant.ToString(CultureInfo.InvariantCulture), env).Name
                    : null;
                declaration.Members.Add(new BilCaseDeclaration(
                    CanonicalSymbolPrinter.PrintCase(enumCase), caseParameters,
                    discriminantResource));
            }
            return declaration;
        }

        // 字段声明（§8.3）：类型成员/全局字段/内建 ext 字段共形态——访问级
        // 全显式；const/var 可变性标记必发（BilVerifier §21.8 已补 init
        // 豁免——init 方法体内写实例 const 字段合法，M56 P3 同规则，
        // ConstFieldRules）；ext 字段带 ext 修饰符；带访问器字段追加形态
        // 标记（backing/computed → readable → writable → compiler-generated）。
        // S11c：`.wrapper.` 隐藏字段（IsCompilerGenerated）发 §8.3.1 形态
        //（backing compiler-generated，无访问器标记）。
        // 修饰符按 §8.3 表序：访问 → const/var → ext → 访问器形态标记
        private static BilSimpleMemberDeclaration EmitFieldDeclaration(FieldSymbol field)
        {
            var modifiers = new List<BilModifier>
                { new BilAccessibilityModifier(MapAccessibility(field.Accessibility)) };
            modifiers.Add(new BilKeywordModifier(field.IsConst ? BilKeyword.Const : BilKeyword.Var));
            if (field.ExtTargetPath != null) modifiers.Add(new BilKeywordModifier(BilKeyword.Ext));
            if (field.Getter != null || field.Setter != null)
            {
                // §9.4：backing 形态 = 编译器生成存储（backing + compiler-generated），
                // 否则为计算属性（computed）；有 getter 则 readable、有 setter 则 writable
                modifiers.Add(new BilKeywordModifier(
                    field.HasBackingStorage ? BilKeyword.Backing : BilKeyword.Computed));
                if (field.Getter != null) modifiers.Add(new BilKeywordModifier(BilKeyword.Readable));
                if (field.Setter != null) modifiers.Add(new BilKeywordModifier(BilKeyword.Writable));
                if (field.HasBackingStorage)
                {
                    modifiers.Add(new BilKeywordModifier(BilKeyword.CompilerGenerated));
                }
            }
            // §8.3.1 wrapped(W)：字段应用标记 outer→inner = 列表序。
            // cell 化的静态/全局字段（统一 cell 存储）：wrapper 标记由 cell
            // 子类的 value 字段承载，字段自身不再投影（避免 Middleware 对
            // 同一应用合成两份隐藏存储）
            if (field.CellStorage == null)
            {
                foreach (var application in field.AppliedWrappers)
                {
                    modifiers.Add(new BilWrappedModifier(
                        CanonicalSymbolPrinter.PrintType(application.Wrapper)));
                }
            }
            return new BilSimpleMemberDeclaration(
                field.IsStatic ? BilMemberKind.StaticField : BilMemberKind.Field,
                CanonicalSymbolPrinter.PrintField(field),
                modifiers);
        }

        // 字段访问器声明（§8.4，S8e）：由字段槽驱动（访问器符号不在容器
        // Methods 表——P2 WrapperCheckers 注释同此约定），getter/setter 声明
        // 按 get→set 顺序紧跟字段声明之后；canonical 走 PrintMethod
        // （$[.static].get.名/$.set.名，无参数段），getter(FIELD)/
        // setter(FIELD) 修饰符引用逻辑字段 canonical（§8.3：get.field/
        // set.field 始终引用逻辑字段，故表达式/语句发射零改动）
        private static IEnumerable<BilSimpleMemberDeclaration> EmitFieldAccessorDeclarations(
            FieldSymbol field)
        {
            if (field.Getter != null) yield return EmitAccessorDeclaration(field.Getter, field);
            if (field.Setter != null) yield return EmitAccessorDeclaration(field.Setter, field);
        }

        private static BilSimpleMemberDeclaration EmitAccessorDeclaration(MethodSymbol accessor,
            FieldSymbol field)
        {
            var modifiers = new List<BilModifier>
                { new BilAccessibilityModifier(MapAccessibility(accessor.Accessibility)) };
            if (accessor.ExtTargetPath != null) modifiers.Add(new BilKeywordModifier(BilKeyword.Ext));
            modifiers.Add(new BilAccessorModifier(
                accessor.Kind == MethodKind.Getter ? BilAccessorKind.Getter : BilAccessorKind.Setter,
                CanonicalSymbolPrinter.PrintField(field)));
            return new BilSimpleMemberDeclaration(
                accessor.IsStatic ? BilMemberKind.StaticMethod : BilMemberKind.Method,
                CanonicalSymbolPrinter.PrintMethod(accessor),
                modifiers);
        }

        // proxy 模板类别 → BIL 修饰符类别（§8.4 wrapper-proxy(PROXY_KIND)）
        private static BilProxyKind MapProxyKind(ProxyTemplateKind kind)
        {
            return kind == ProxyTemplateKind.Wildcard
                ? BilProxyKind.Wildcard
                : BilProxyKind.Specific;
        }

        // 方法声明（§8.4）：类型成员与全局函数共形态。
        // S7c-2 开闸 init/operator 与实例方法：init 走普通 canonical
        // （$init...@.void）+ init 修饰符；operator 走 $$名 canonical +
        // operator(名) 修饰符；ext 成员带 ext 修饰符（P2 注册后
        // ExtTargetPath 保留为标记）；S8e 补 override/abstract 关键字投影；
        // getter/setter 不入容器 Methods 表，声明由字段槽驱动
        // （EmitFieldAccessorDeclarations）
        private static BilSimpleMemberDeclaration EmitMethodDeclaration(MethodSymbol method)
        {
            if (method.Kind is MethodKind.Getter or MethodKind.Setter)
            {
                // 内部不变量：访问器符号只挂在 FieldSymbol.Getter/Setter 槽，
                // 声明发射由字段槽驱动；径方法表到达此处即上游结构错误
                throw new CompilerInternalException("访问器声明不应经方法表发射: " +
                    CanonicalSymbolPrinter.PrintMethod(method));
            }
            var modifiers = new List<BilModifier>
                { new BilAccessibilityModifier(MapAccessibility(method.Accessibility)) };
            if (method.ExtTargetPath != null) modifiers.Add(new BilKeywordModifier(BilKeyword.Ext));
            if (method.IsOverride) modifiers.Add(new BilKeywordModifier(BilKeyword.Override));
            if (method.IsAbstract) modifiers.Add(new BilKeywordModifier(BilKeyword.Abstract));
            // S10（BIL §8.4/§15.2）：async 修饰符——调用点返回 Task 的语义
            // 标记（§15.2 结果形态由 verifier 据此校验）
            if (method.IsAsync) modifiers.Add(new BilKeywordModifier(BilKeyword.Async));
            if (method.IsUnsafe) modifiers.Add(new BilKeywordModifier(BilKeyword.Unsafe));
            if (method.Kind == MethodKind.Init) modifiers.Add(new BilKeywordModifier(BilKeyword.Init));
            if (method.Kind == MethodKind.Operator) modifiers.Add(new BilOperatorModifier(method.Name));
            // M109b §9.7/§8.7：..init.wrapper / ..init.field.* / companion
            // 实例方法 / 静态壳体 / ..globals.init 均 compiler-generated
            // （priv/pub 由 Accessibility）
            if (method.Name == BilSpellings.InitWrapperMethodName
                || method.Name.StartsWith(BilSpellings.InitFieldMethodPrefix,
                    StringComparison.Ordinal)
                || method.Name == BilSpellings.GlobalsInitFunctionName
                || method.Name == BilSpellings.ToParcelMethodName
                || method.Name == BilSpellings.FromParcelMethodName
                || method.Name == BilSpellings.InitSerializableMethodName
                || method.IsCompanionInstance
                || method.Companion != null)
            {
                modifiers.Add(new BilKeywordModifier(BilKeyword.CompilerGenerated));
            }
            // native 三件套（§8.4：symbol/lib 必须与 native 同时出现且各恰好一次）
            if (method.IsNative)
            {
                modifiers.Add(new BilKeywordModifier(BilKeyword.Native));
                modifiers.Add(new BilNativeSymbolModifier(method.NativeSymbol!));
                modifiers.Add(new BilNativeLibraryModifier(method.NativeLibrary!));
                // 3b-δ1：@NativeBorrow → native-borrow 修饰符（借用返回标记；
                // NativeDeclarationChecker 保证只随 native 声明出现）
                if (method.NativeBorrow)
                {
                    modifiers.Add(new BilKeywordModifier(BilKeyword.NativeBorrow));
                }
            }
            // entrypoint（SYNTAX §17）：@EntryPoint 内建注解标记的静态方法
            // （任意命名空间），或全局命名空间的裸 main 命名约定
            if (method.IsEntryPoint
                || (method.Owner == null && method.Namespace is { FullName: "" }
                    && method.Name == "main"))
            {
                modifiers.Add(new BilKeywordModifier(BilKeyword.Entrypoint));
            }
            // M88：wrapper 类型内的 proxy 声明模板投影 wrapper-proxy
            //（specific|wildcard）——类别取 P1 落定的符号属性，不查名字；
            // 烘焙特化/original/router 归 Middleware
            if (method.ProxyTemplate is { } proxyTemplateKind
                && method.Owner?.Kind == TypeKind.Wrapper)
            {
                modifiers.Add(new BilWrapperProxyModifier(MapProxyKind(proxyTemplateKind)));
            }
            return new BilSimpleMemberDeclaration(
                method.IsStatic ? BilMemberKind.StaticMethod : BilMemberKind.Method,
                CanonicalSymbolPrinter.PrintMethod(method),
                modifiers);
        }

        // 种类的默认基类（P1 建壳填充规则；interface 无基类）
        private static TypeSymbol? DefaultBaseOf(TypeSymbol type, EmitEnvironment env)
        {
            var bootstrap = env.Unit.Symbols.Bootstrap;
            return type.Kind switch
            {
                TypeKind.Class => bootstrap.Object,
                TypeKind.Struct => bootstrap.ValueType,
                TypeKind.EnumStruct => bootstrap.Enum,
                TypeKind.Wrapper => bootstrap.Wrapper,
                TypeKind.Interface => null,
                _ => throw new CompilerInternalException("未知 TypeKind: " + type.Kind),
            };
        }

        // Semantic TypeKind → Bil 类型种类（§8.2）
        private static BilTypeKind MapTypeKind(TypeKind kind)
        {
            return kind switch
            {
                TypeKind.Class => BilTypeKind.Class,
                TypeKind.Struct => BilTypeKind.Struct,
                TypeKind.EnumStruct => BilTypeKind.EnumStruct,
                TypeKind.Interface => BilTypeKind.Interface,
                TypeKind.Wrapper => BilTypeKind.Wrapper,
                _ => throw new CompilerInternalException("未知 TypeKind: " + kind),
            };
        }

        // Semantic Accessibility → Bil 访问修饰符（§8.2/§8.3/§8.4）
        private static BilAccessibility MapAccessibility(Accessibility accessibility)
        {
            return accessibility switch
            {
                Accessibility.Public => BilAccessibility.Public,
                Accessibility.Protected => BilAccessibility.Protected,
                Accessibility.Internal => BilAccessibility.Internal,
                Accessibility.Private => BilAccessibility.Private,
                _ => throw new CompilerInternalException("未知 Accessibility: " + accessibility),
            };
        }
    }
}
