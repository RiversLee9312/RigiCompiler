using System.Collections.Generic;
using System.Reflection;
using LatteCompiler.Bil;

namespace LatteCompiler
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
        // 本空间全局字段/函数裸条目（§8.4.1：不包裹在 .type 中）
        public static void EmitNamespace(NamespaceSymbol ns, EmitEnvironment env)
        {
            foreach (var type in ns.Types)
            {
                EmitTypeTree(type, env);
            }
            foreach (var child in ns.ChildNamespaces)
            {
                EmitNamespace(child, env);
            }
            foreach (var field in ns.Fields)
            {
                env.Module.LocalSymbols.Add(EmitFieldDeclaration(field));
                foreach (var accessor in EmitFieldAccessorDeclarations(field))
                {
                    env.Module.LocalSymbols.Add(accessor);
                }
            }
            foreach (var method in ns.Methods)
            {
                env.Module.LocalSymbols.Add(EmitMethodDeclaration(method));
            }
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
                if (property.GetValue(env.Unit.Symbols.Bootstrap) is not TypeSymbol
                    { IsBuiltin: true } builtinType) continue;
                foreach (var field in builtinType.Fields)
                {
                    if (field.ExtTargetPath == null) continue;
                    env.Module.LocalSymbols.Add(EmitFieldDeclaration(field));
                }
                foreach (var method in builtinType.Methods)
                {
                    if (method.ExtTargetPath == null) continue;
                    env.Module.LocalSymbols.Add(EmitMethodDeclaration(method));
                }
            }
        }

        private static void EmitTypeTree(TypeSymbol type, EmitEnvironment env)
        {
            // 内建 bootstrap 符号（基元/层级根）不声明：经 BIL 别名投影引用；
            // ErrorType 是毒化单例，同样不进符号段
            if (type.IsBuiltin || type is ErrorTypeSymbol) return;
            env.Module.LocalSymbols.Add(EmitTypeDeclaration(type, env));
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
            }
            // 修饰符（§8.2）：访问（全显式）→ open/abstract/singleton → rich/shared
            // （wrapper 恒 rich 也显式输出——BIL 是显式 IR，不做源码的隐含）
            declaration.Modifiers.Add(new BilAccessibilityModifier(MapAccessibility(type.Accessibility)));
            if (type.IsOpen) declaration.Modifiers.Add(new BilKeywordModifier(BilKeyword.Open));
            if (type.IsAbstract) declaration.Modifiers.Add(new BilKeywordModifier(BilKeyword.Abstract));
            if (type.IsSingleton) declaration.Modifiers.Add(new BilKeywordModifier(BilKeyword.Singleton));
            if (type.IsRich) declaration.Modifiers.Add(new BilKeywordModifier(BilKeyword.Rich));
            if (type.IsShared) declaration.Modifiers.Add(new BilKeywordModifier(BilKeyword.Shared));
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
                declaration.Members.Add(EmitFieldDeclaration(field));
                foreach (var accessor in EmitFieldAccessorDeclarations(field))
                {
                    declaration.Members.Add(accessor);
                }
            }
            foreach (var method in type.Methods)
            {
                declaration.Members.Add(EmitMethodDeclaration(method));
            }
            return declaration;
        }

        // 字段声明（§8.3）：类型成员/全局字段/内建 ext 字段共形态——访问级
        // 全显式；带访问器字段追加 §8.3 表序的形态标记（backing/computed →
        // readable → writable → compiler-generated）；无访问器字段输出与
        // 此前逐字节一致（仅访问级一个修饰符）
        private static BilSimpleMemberDeclaration EmitFieldDeclaration(FieldSymbol field)
        {
            var modifiers = new List<BilModifier>
                { new BilAccessibilityModifier(MapAccessibility(field.Accessibility)) };
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
            if (method.Kind == MethodKind.Init) modifiers.Add(new BilKeywordModifier(BilKeyword.Init));
            if (method.Kind == MethodKind.Operator) modifiers.Add(new BilOperatorModifier(method.Name));
            // native 三件套（§8.4：symbol/lib 必须与 native 同时出现且各恰好一次）
            if (method.IsNative)
            {
                modifiers.Add(new BilKeywordModifier(BilKeyword.Native));
                modifiers.Add(new BilNativeSymbolModifier(method.NativeSymbol!));
                modifiers.Add(new BilNativeLibraryModifier(method.NativeLibrary!));
            }
            // entrypoint：全局命名空间的裸 main（SYNTAX 程序入口）
            if (method.Owner == null && method.Namespace is { FullName: "" }
                && method.Name == "main")
            {
                modifiers.Add(new BilKeywordModifier(BilKeyword.Entrypoint));
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
