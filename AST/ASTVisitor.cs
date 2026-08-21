using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace RigiCompiler
{
    /// <summary>
    /// 统一 AST 遍历基建（M28）：深度优先先序（父先于子）。
    ///
    /// 遍历逻辑可重载：VisitNode（遍历骨架）与 EnumerateChildren（子节点来源）
    /// 均为 virtual，实现方可整体替换遍历方式或仅替换子节点枚举；
    /// 默认实现即下述反射逻辑（AstStructureReflection）。
    ///
    /// 默认子节点枚举（ASTIntegrityValidator 与 AstJsonlSerializer 共用）：
    /// 只走以 [ChildAstNode] 标注的字段（Public+NonPublic 实例）与公共可读无参属性；
    /// 值形态：单 ASTNode / 集合（via 带 [i] 下标）/ [AstCarrier] 对象或集合
    /// （深入其公共实例字段，via 复合为 member[i](CarrierType.Field)）。
    /// null 与 string 跳过；标注成员装非节点值不在遍历层处理
    /// （由 ASTIntegrityValidator 的类型审计拒绝，职责分离）。
    /// </summary>
    public abstract class ASTVisitor
    {
        // 深度优先先序遍历整棵 AST（先父后子）
        public void Visit(ASTNode root) => VisitNode(root, parent: null, via: null);

        // 遍历骨架（virtual：实现方可整体重载遍历逻辑）。
        // 默认：深度优先先序——先 OnNode，再递归 EnumerateChildren 枚举出的子节点。
        // 重载时不调用 base.VisitNode 即不再下钻该子树（剪枝）；
        // 只想替换子节点来源/顺序时重载 EnumerateChildren 即可，不必动本方法。
        protected virtual void VisitNode(ASTNode node, ASTNode? parent, string? via)
        {
            OnNode(node, parent, via);
            foreach (var (child, childVia) in EnumerateChildren(node))
            {
                VisitNode(child, node, childVia);
            }
        }

        // 子节点枚举（virtual：实现方可替换子节点来源）。
        // 默认走 [ChildAstNode] 反射（AstStructureReflection.EnumerateChildren）；
        // 重载可手写枚举、调整顺序或剪枝，返回的 via 串原样传给子节点的 OnNode。
        protected virtual IEnumerable<(ASTNode Child, string Via)> EnumerateChildren(ASTNode node)
            => AstStructureReflection.EnumerateChildren(node);

        // 访问一个节点。via：根为 null；单节点成员 = 成员名（如 Left、expression）；
        // 集合元素 = member[i]；carrier 字段 = member[i](CarrierType.Field)
        protected abstract void OnNode(ASTNode node, ASTNode? parent, string? via);
    }

    /// <summary>
    /// AST 结构反射的共享实现（M28）：[ChildAstNode] 子节点枚举 +
    /// 「成员是否装 ASTNode」判定。ASTVisitor 遍历与 Validator 类型审计
    /// 共用同一套成员枚举，杜绝两份反射代码漂移。
    /// </summary>
    internal static class AstStructureReflection
    {
        // 枚举 node 的全部子节点（[ChildAstNode] 标注驱动）
        public static IEnumerable<(ASTNode Child, string Via)> EnumerateChildren(ASTNode node)
        {
            foreach (var member in GetChildMembers(node.GetType()))
            {
                object? value = member switch
                {
                    FieldInfo field => field.GetValue(node),
                    PropertyInfo prop => prop.GetValue(node),
                    _ => null
                };
                foreach (var found in EnumerateValue(value, member.Name))
                {
                    yield return found;
                }
            }

            // SymbolASTNode 特判（g1）：symbol 是纯数据、不经 [ChildAstNode] 标注，
            // 但其泛型实参已是完整 TypeReferenceASTNode（AST 节点）——作为该
            // SymbolASTNode 的子节点纳入遍历（Parent/Span/共享校验与表达式路径
            // 同口径）。嵌套实参经由实参节点自身的 TypeSymbol 递归到达，无需在此下钻
            if (node is SymbolASTNode symbolNode)
            {
                var elements = symbolNode.symbol.elements;
                for (int i = 0; i < elements.Count; i++)
                {
                    var generics = elements[i].generics;
                    for (int j = 0; j < generics.Count; j++)
                    {
                        yield return (generics[j], $"symbol.elements[{i}].generics[{j}]");
                    }
                }
            }
        }

        // 子单元枚举（M32，JSONL v2 序列化专用）：与 EnumerateChildren 相同的
        // 成员路径与顺序，但 [AstCarrier] 对象作为独立单元产出（不再下钻其
        // 内部节点），via 不做 (CarrierType.Field) 复合——单 carrier 成员为
        // 成员名，集合元素为 member[i]。carrier 携带的节点由消费方对该单元
        // 再调 EnumerateCarriedNodes 获得。
        // EnumerateChildren（Validator/ASTVisitor 默认遍历）行为不受本方法影响。
        public static IEnumerable<(object Unit, string Via)> EnumerateChildUnits(ASTNode node)
        {
            foreach (var member in GetChildMembers(node.GetType()))
            {
                object? value = member switch
                {
                    FieldInfo field => field.GetValue(node),
                    PropertyInfo prop => prop.GetValue(node),
                    _ => null
                };
                foreach (var found in EnumerateUnitValue(value, member.Name))
                {
                    yield return found;
                }
            }
        }

        // carrier 携带的节点：其公共实例字段中的 ASTNode（FieldName = carrier 内字段名）
        public static IEnumerable<(ASTNode Node, string FieldName)> EnumerateCarriedNodes(object carrier)
        {
            foreach (var field in carrier.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.GetValue(carrier) is ASTNode carried)
                {
                    yield return (carried, field.Name);
                }
            }
        }

        // 实例字段全集（沿基类链，DeclaredOnly 去重）：
        // Type.GetFields 不返回基类的 private 字段（M31 修复的反射盲区），
        // 需要完整字段视图的场景（类型审计、fields 收集）统一走这里
        public static IEnumerable<FieldInfo> GetAllInstanceFields(Type type)
        {
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var field in t.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    yield return field;
                }
            }
        }

        // [ChildAstNode] 标注的成员：字段（含 NonPublic，如 ExpressionRootASTNode.expression）、
        // 公共可读无参属性（get-only 挂载点，如 BinaryExpressionASTNode.Left）。
        // 字段沿基类链取（GetAllInstanceFields：基类 private 字段不再漏枚举）
        public static IEnumerable<MemberInfo> GetChildMembers(Type type)
        {
            foreach (var field in GetAllInstanceFields(type))
            {
                if (field.GetCustomAttribute<ChildAstNodeAttribute>() != null)
                {
                    yield return field;
                }
            }
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetCustomAttribute<ChildAstNodeAttribute>() == null) continue;
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                yield return prop;
            }
        }

        // 「该成员类型装着 ASTNode」：ASTNode 派生 / 元素为 ASTNode 派生的集合 /
        // [AstCarrier] 类型或其集合（Validator 类型审计用：此类成员必须带标注）
        public static bool MemberHoldsAstNodes(Type memberType)
        {
            if (typeof(ASTNode).IsAssignableFrom(memberType)) return true;
            if (memberType.GetCustomAttribute<AstCarrierAttribute>() != null) return true;
            var elementType = GetCollectionElementType(memberType);
            if (elementType != null)
            {
                if (typeof(ASTNode).IsAssignableFrom(elementType)) return true;
                if (elementType.GetCustomAttribute<AstCarrierAttribute>() != null) return true;
            }
            return false;
        }

        // 集合元素类型（单类型参数的 IEnumerable<T> 的 T 或数组元素；string 不算集合）
        internal static Type? GetCollectionElementType(Type type)
        {
            if (type == typeof(string)) return null;
            if (type.IsArray) return type.GetElementType();
            if (type.IsGenericType && type.GenericTypeArguments.Length == 1
                && typeof(IEnumerable).IsAssignableFrom(type))
            {
                return type.GenericTypeArguments[0];
            }
            return null;
        }

        // [ChildAstNode] 成员的值：单节点 / 集合（via 带下标）/ 单 carrier
        private static IEnumerable<(ASTNode, string)> EnumerateValue(object? value, string memberName)
        {
            if (value == null || value is string) yield break;

            if (value is ASTNode single)
            {
                yield return (single, memberName);
                yield break;
            }

            if (value is IEnumerable enumerable)
            {
                int index = 0;
                foreach (var item in enumerable)
                {
                    foreach (var found in EnumerateItem(item, $"{memberName}[{index}]"))
                    {
                        yield return found;
                    }
                    index++;
                }
                yield break;
            }

            foreach (var found in EnumerateItem(value, memberName))
            {
                yield return found;
            }
        }

        // 集合元素或单值：ASTNode → 子节点；[AstCarrier] → 深入其公共字段找 ASTNode；
        // 其余（标注成员装非节点值）跳过——由 Validator 类型审计拒绝，这里保持静默
        private static IEnumerable<(ASTNode, string)> EnumerateItem(object? item, string via)
        {
            if (item == null) yield break;

            if (item is ASTNode child)
            {
                yield return (child, via);
                yield break;
            }

            var itemType = item.GetType();
            if (itemType.GetCustomAttribute<AstCarrierAttribute>() != null)
            {
                // carrier（如 ImportItem）：其中的 ASTNode 视为持有节点的子节点
                foreach (var field in itemType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (field.GetValue(item) is ASTNode carried)
                    {
                        yield return (carried, $"{via}({itemType.Name}.{field.Name})");
                    }
                }
            }
        }

        // ===== EnumerateChildUnits 的私有实现（M32，JSONL v2）=====

        // [ChildAstNode] 成员的值：单节点 / 集合元素 / carrier（carrier 不再下钻，
        // 作为独立单元产出；标注成员装非节点值同样静默跳过，由 Validator 类型审计拒绝）
        private static IEnumerable<(object, string)> EnumerateUnitValue(object? value, string memberName)
        {
            if (value == null || value is string) yield break;

            if (value is ASTNode single)
            {
                yield return (single, memberName);
                yield break;
            }

            if (value is IEnumerable enumerable)
            {
                int index = 0;
                foreach (var item in enumerable)
                {
                    if (item is ASTNode || IsCarrierObject(item))
                    {
                        yield return (item, $"{memberName}[{index}]");
                    }
                    index++;
                }
                yield break;
            }

            // 单 carrier 成员（via 为成员名）
            if (IsCarrierObject(value))
            {
                yield return (value, memberName);
            }
        }

        private static bool IsCarrierObject(object? value)
        {
            return value != null && value.GetType().GetCustomAttribute<AstCarrierAttribute>() != null;
        }
    }
}
