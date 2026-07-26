using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace LatteCompiler
{
    // 内部编译器错误：AST 完整性验证失败等「不可能发生」的编译器内部状态错误。
    // 与 ParserException（用户语法错误）严格区分——抛出它即编译器自身有 bug。
    public class CompilerInternalException : Exception
    {
        public CompilerInternalException(string message) : base(message) { }
    }

    /// <summary>
    /// AST 完整性验证器（大扫除 §12；Attribute 驱动重写）
    ///
    /// Parser 成功后、进入语义分析前运行一次。验证失败抛出 CompilerInternalException
    /// （内部编译器错误，而非用户语法错误）。
    ///
    /// 遍历方式：只走以 [ChildAstNode] 标注的字段/属性（反射）——
    /// 值为 ASTNode（子节点）、ASTNode 集合（元素为子节点）、或 [AstCarrier]
    /// 对象/集合（深入其公共实例字段，其中的 ASTNode 为子节点）。
    /// 未被标注的成员（Symbol、string 等纯数据）一律不进入。
    ///
    /// 检查项：
    /// 1. 父子指针一致：每个子节点的 Parent 必须指向持有该 [ChildAstNode] 成员的节点
    ///    （carrier 情形为持有集合的节点，如 ImportItem.symbolNode → ImportASTNode）；
    /// 2. 所有 ExpressionRootASTNode 均已填充（必需 Root 必填；可选位置用 null Root
    ///    表示，因此「存在但未填充的 Root」即施工中节点，一律拒绝）；
    /// 3. 所有 AST 节点最多被引用一次（无共享——同一表达式不可能属于两个 Root，
    ///    也不存在多父节点）；
    /// 4. AST 的 Parent 链不存在环；
    /// 5. switch 表达式满足 default 规则（表达式模式必须有 default 分支）。
    ///
    /// 类型引用等声明侧字段的完备性（如 init 映射参数的空 Type 属合法形态）
    /// 不在此处检查，留待语义分析阶段。
    /// </summary>
    public static class ASTIntegrityValidator
    {
        public static void Validate(RootASTNode root)
        {
            var visited = new HashSet<ASTNode>(ReferenceEqualityComparer.Instance);
            var nodes = new List<ASTNode>();
            CollectNode(root, expectedParent: null, viaMember: null, visited, nodes);

            foreach (var node in nodes)
            {
                // 2. ExpressionRoot：必须已填充
                if (node is ExpressionRootASTNode exprRoot && !exprRoot.IsAttached)
                {
                    throw new CompilerInternalException(
                        $"ExpressionRootASTNode is not filled (owner: {exprRoot.Parent?.GetType().Name ?? "<null>"})");
                }

                // 5. switch 表达式必须有 default 分支（SYNTAX §7.2）
                if (node is SwitchExpressionASTNode switchExpr && switchExpr.DefaultBody == null)
                {
                    throw new CompilerInternalException(
                        "Switch expression has no default branch (SYNTAX §7.2)");
                }
            }

            // 4. Parent 链无环
            foreach (var node in nodes)
            {
                var chain = new HashSet<ASTNode>(ReferenceEqualityComparer.Instance);
                ASTNode? current = node;
                while (current != null)
                {
                    if (!chain.Add(current))
                    {
                        throw new CompilerInternalException(
                            $"AST parent chain has a cycle at {current.GetType().Name}");
                    }
                    current = current.Parent;
                }
            }
        }

        // 递归收集一个 AST 节点：校验父子指针、登记共享检测，再按 [ChildAstNode] 成员下钻
        private static void CollectNode(
            ASTNode node, ASTNode? expectedParent, string? viaMember,
            HashSet<ASTNode> visited, List<ASTNode> nodes)
        {
            // 1. 父子指针一致
            if (!ReferenceEquals(node.Parent, expectedParent))
            {
                throw new CompilerInternalException(
                    $"AST node has an incorrect parent: {node.GetType().Name} " +
                    $"(expected: {expectedParent?.GetType().Name ?? "<null>"}, " +
                    $"actual: {node.Parent?.GetType().Name ?? "<null>"}" +
                    (viaMember != null ? $", via member: {viaMember}" : "") + ")");
            }

            // 3. 共享检测：同一节点第二次出现即共享错误
            if (!visited.Add(node))
            {
                throw new CompilerInternalException(
                    $"AST node is shared by multiple parents: {node.GetType().Name}");
            }
            nodes.Add(node);

            // 按 [ChildAstNode] 标注的成员遍历子节点
            var type = node.GetType();

            // 字段（含 private，如 ExpressionRootASTNode.expression）
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.GetCustomAttribute<ChildAstNodeAttribute>() == null) continue;
                CollectChildValue(field.GetValue(node), node, field.Name, visited, nodes);
            }

            // 属性（get-only 挂载点属性，如 BinaryExpressionASTNode.Left）
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetCustomAttribute<ChildAstNodeAttribute>() == null) continue;
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                CollectChildValue(prop.GetValue(node), node, prop.Name, visited, nodes);
            }
        }

        // 处理 [ChildAstNode] 成员的值：单节点 / 集合 / 单 carrier
        private static void CollectChildValue(
            object? value, ASTNode owner, string memberName,
            HashSet<ASTNode> visited, List<ASTNode> nodes)
        {
            if (value == null || value is string) return;

            if (value is ASTNode child)
            {
                CollectNode(child, owner, memberName, visited, nodes);
                return;
            }

            if (value is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    CollectChildItem(item, owner, memberName, visited, nodes);
                }
                return;
            }

            CollectChildItem(value, owner, memberName, visited, nodes);
        }

        // 集合元素或单值：ASTNode → 子节点；[AstCarrier] → 深入其公共字段找 ASTNode
        private static void CollectChildItem(
            object? item, ASTNode owner, string memberName,
            HashSet<ASTNode> visited, List<ASTNode> nodes)
        {
            if (item == null) return;

            if (item is ASTNode child)
            {
                CollectNode(child, owner, memberName, visited, nodes);
                return;
            }

            var itemType = item.GetType();
            if (itemType.GetCustomAttribute<AstCarrierAttribute>() != null)
            {
                // carrier（如 ImportItem）：其中的 ASTNode 视为 owner 的子节点
                foreach (var field in itemType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (field.GetValue(item) is ASTNode carried)
                    {
                        CollectNode(carried, owner, $"{memberName}({itemType.Name}.{field.Name})", visited, nodes);
                    }
                }
                return;
            }

            // [ChildAstNode] 成员装的不是节点/节点集合/carrier——标注本身有误
            throw new CompilerInternalException(
                $"[ChildAstNode] member '{memberName}' of {owner.GetType().Name} " +
                $"holds a non-AST value of type {itemType.Name}");
        }
    }
}
