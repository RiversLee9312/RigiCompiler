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
                env.Module.LocalSymbols.Add(new BilSimpleMemberDeclaration(
                    field.IsStatic ? BilMemberKind.StaticField : BilMemberKind.Field,
                    CanonicalSymbolPrinter.PrintField(field),
                    new BilModifier[] { new BilAccessibilityModifier(MapAccessibility(field.Accessibility)) }));
            }
            foreach (var method in ns.Methods)
            {
                var declaration = EmitMethodDeclaration(method, env);
                if (declaration != null) env.Module.LocalSymbols.Add(declaration);
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
                    env.Module.LocalSymbols.Add(new BilSimpleMemberDeclaration(
                        field.IsStatic ? BilMemberKind.StaticField : BilMemberKind.Field,
                        CanonicalSymbolPrinter.PrintField(field),
                        new BilModifier[] { new BilAccessibilityModifier(MapAccessibility(field.Accessibility)) }));
                }
                foreach (var method in builtinType.Methods)
                {
                    if (method.ExtTargetPath == null) continue;
                    var declaration = EmitMethodDeclaration(method, env);
                    if (declaration != null) env.Module.LocalSymbols.Add(declaration);
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
                declaration.Members.Add(new BilSimpleMemberDeclaration(
                    field.IsStatic ? BilMemberKind.StaticField : BilMemberKind.Field,
                    CanonicalSymbolPrinter.PrintField(field),
                    new BilModifier[] { new BilAccessibilityModifier(MapAccessibility(field.Accessibility)) }));
            }
            foreach (var method in type.Methods)
            {
                var member = EmitMethodDeclaration(method, env);
                if (member != null) declaration.Members.Add(member);
            }
            return declaration;
        }

        // 方法声明（§8.4）：类型成员与全局函数共形态；null 返回 = 已诊断跳过。
        // S7c-2 开闸 init/operator 与实例方法：init 走普通 canonical
        // （$init...@.void）+ init 修饰符；operator 走 $$名 canonical +
        // operator(名) 修饰符；ext 成员带 ext 修饰符（P2 注册后
        // ExtTargetPath 保留为标记）；getter/setter 归 S8/S11 仍跳过
        private static BilSimpleMemberDeclaration? EmitMethodDeclaration(MethodSymbol method,
            EmitEnvironment env)
        {
            if (method.Kind is MethodKind.Getter or MethodKind.Setter)
            {
                env.Error(null, $"P4: method kind not supported by minimal emission: " +
                    $"{method.Kind} ({CanonicalSymbolPrinter.PrintMethod(method)})");
                return null;
            }
            var modifiers = new List<BilModifier>
                { new BilAccessibilityModifier(MapAccessibility(method.Accessibility)) };
            if (method.ExtTargetPath != null) modifiers.Add(new BilKeywordModifier(BilKeyword.Ext));
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
