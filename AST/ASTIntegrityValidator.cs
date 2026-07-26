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
    /// AST 完整性验证器（大扫除 §12）
    ///
    /// Parser 成功后、进入语义分析前运行一次。验证失败抛出 CompilerInternalException
    /// （内部编译器错误，而非用户语法错误）。
    ///
    /// 检查项：
    /// 1. 所有 ExpressionRootASTNode 均已填充（必需 Root 必填；可选位置用 null Root
    ///    表示，因此「存在但未填充的 Root」即施工中节点，一律拒绝）；
    /// 2. Root 中表达式的 Parent 正确指向 Root；
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
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var nodes = new List<ASTNode>();
            Collect(root, visited, nodes);

            foreach (var node in nodes)
            {
                // 1/2. ExpressionRoot：必须已填充，且表达式的 Parent 指向 Root
                if (node is ExpressionRootASTNode exprRoot)
                {
                    if (!exprRoot.IsAttached)
                    {
                        throw new CompilerInternalException(
                            $"ExpressionRootASTNode is not filled (owner: {exprRoot.Parent?.GetType().Name ?? "<null>"})");
                    }
                    if (exprRoot.Expression.Parent != exprRoot)
                    {
                        throw new CompilerInternalException(
                            $"Expression's parent does not point to its ExpressionRootASTNode " +
                            $"(expression: {exprRoot.Expression.GetType().Name})");
                    }
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

        // 反射遍历整棵 AST（公共实例字段与属性，跳过 Parent 指针），
        // 收集所有 ASTNode；同一 ASTNode 对象被第二次引用即共享错误
        private static void Collect(object? obj, HashSet<object> visited, List<ASTNode> nodes)
        {
            if (obj == null) return;

            var type = obj.GetType();

            // 基础类型直接返回
            if (type.IsPrimitive || obj is string || type.IsEnum) return;

            // 已访问过的对象：ASTNode 重复引用即共享；其他对象（Symbol 等）跳过
            if (!visited.Add(obj))
            {
                if (obj is ASTNode shared)
                {
                    throw new CompilerInternalException(
                        $"AST node is shared by multiple parents: {shared.GetType().Name}");
                }
                return;
            }

            if (obj is ASTNode node)
            {
                nodes.Add(node);
            }

            // 集合：遍历元素（string 已在上方排除）
            if (obj is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    Collect(item, visited, nodes);
                }
                return;
            }

            // 复杂对象：遍历公共实例字段与属性（跳过 Parent，防止上溯成环）
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                Collect(field.GetValue(obj), visited, nodes);
            }

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.Name == nameof(ASTNode.Parent)) continue;
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;

                object? value;
                try
                {
                    value = prop.GetValue(obj);
                }
                catch
                {
                    // 未填充 Root 的 Expression getter 会抛异常——由 Root 检查统一报告
                    continue;
                }
                Collect(value, visited, nodes);
            }
        }
    }
}
