using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace LatteCompiler
{
    /// <summary>
    /// AST → JSONL 序列化：深度优先，每节点一行：
    /// {"id":1,"parent":null,"via":null,"type":"RootASTNode","fields":{...}}
    ///
    /// id 从 1 自增；parent 引用父节点 id（根为 null）；via 为挂载成员名
    /// （如 "Left"、"Children[2]"、carrier 情形 "importedSymbols[0](ImportItem.symbolNode)"）。
    ///
    /// 子节点枚举与 ASTIntegrityValidator 同一套 [ChildAstNode] 反射下钻
    /// （Public+NonPublic 实例字段、Public 可读无参属性、IEnumerable 逐元素、
    /// [AstCarrier] 深入公共字段、null 跳过）。
    ///
    /// fields 收集无标注成员中的标量值（primitive/string/decimal/enum/List<string>），
    /// Symbol 渲染为点分字符串；排除 Parent、[ChildAstNode] 成员与索引器，其余忽略。
    /// 读取成员值之前先按声明类型过滤——ExpressionRootASTNode.Expression 这类
    /// 未填充即抛异常的属性不是简单类型，天然被跳过。
    /// </summary>
    public static class AstJsonlSerializer
    {
        public static void Serialize(ASTNode root, TextWriter writer)
        {
            int nextId = 1;
            DumpNode(root, parentId: null, via: null, writer, ref nextId);
        }

        private static void DumpNode(ASTNode node, int? parentId, string? via, TextWriter writer, ref int nextId)
        {
            var id = nextId++;
            var record = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["parent"] = parentId,
                ["via"] = via,
                ["type"] = node.GetType().Name,
                ["fields"] = CollectFields(node)
            };
            writer.WriteLine(JsonSerializer.Serialize(record));

            // 按 [ChildAstNode] 标注的成员下钻子节点（与 Validator 同一套反射）
            var type = node.GetType();

            // 字段（含 private，如 ExpressionRootASTNode.expression）
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.GetCustomAttribute<ChildAstNodeAttribute>() == null) continue;
                DumpChildValue(field.GetValue(node), field.Name, id, writer, ref nextId);
            }

            // 属性（get-only 挂载点属性，如 BinaryExpressionASTNode.Left）
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetCustomAttribute<ChildAstNodeAttribute>() == null) continue;
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                DumpChildValue(prop.GetValue(node), prop.Name, id, writer, ref nextId);
            }
        }

        // [ChildAstNode] 成员的值：单节点 / 集合（逐元素，via 带下标）/ 单 carrier
        private static void DumpChildValue(object? value, string memberName, int ownerId, TextWriter writer, ref int nextId)
        {
            if (value == null || value is string) return;

            if (value is ASTNode child)
            {
                DumpNode(child, ownerId, memberName, writer, ref nextId);
                return;
            }

            if (value is IEnumerable enumerable)
            {
                int index = 0;
                foreach (var item in enumerable)
                {
                    DumpChildItem(item, $"{memberName}[{index}]", ownerId, writer, ref nextId);
                    index++;
                }
                return;
            }

            DumpChildItem(value, memberName, ownerId, writer, ref nextId);
        }

        // 集合元素或单值：ASTNode → 子节点；[AstCarrier] → 深入其公共字段找 ASTNode
        private static void DumpChildItem(object? item, string memberName, int ownerId, TextWriter writer, ref int nextId)
        {
            if (item == null) return;

            if (item is ASTNode child)
            {
                DumpNode(child, ownerId, memberName, writer, ref nextId);
                return;
            }

            var itemType = item.GetType();
            if (itemType.GetCustomAttribute<AstCarrierAttribute>() != null)
            {
                foreach (var field in itemType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (field.GetValue(item) is ASTNode carried)
                    {
                        DumpNode(carried, ownerId, $"{memberName}({itemType.Name}.{field.Name})", writer, ref nextId);
                    }
                }
                return;
            }
            // 非节点/非 carrier 的 [ChildAstNode] 值由 ASTIntegrityValidator 负责拒绝，这里忽略
        }

        // fields：无标注成员中的标量值。先按声明类型过滤再取值，
        // 避开 ExpressionRootASTNode.Expression 这种未填充会抛异常的属性
        private static Dictionary<string, object?> CollectFields(ASTNode node)
        {
            var result = new Dictionary<string, object?>();
            var type = node.GetType();

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.GetCustomAttribute<ChildAstNodeAttribute>() != null) continue;
                if (field.GetCustomAttribute<ParentAstNodeAttribute>() != null) continue;
                // 编译器生成的自动属性 backing 字段（<Name>k__BackingField）：以属性名为准，跳过
                if (field.Name.StartsWith("<")) continue;
                if (TryRender(field.FieldType, field.GetValue(node), out var rendered))
                {
                    result[field.Name] = rendered;
                }
            }

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetCustomAttribute<ChildAstNodeAttribute>() != null) continue;
                if (prop.GetCustomAttribute<ParentAstNodeAttribute>() != null) continue;
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                if (TryRender(prop.PropertyType, prop.GetValue(node), out var rendered))
                {
                    result[prop.Name] = rendered;
                }
            }

            return result;
        }

        // 简单类型（primitive/string/decimal/enum）原样收录（enum 渲染为名字）；
        // List<string> 序列化为数组；Symbol 渲染为点分字符串；其余类型忽略
        private static bool TryRender(Type memberType, object? value, out object? rendered)
        {
            rendered = null;
            if (memberType.IsEnum)
            {
                rendered = value?.ToString();
                return true;
            }
            if (memberType.IsPrimitive || memberType == typeof(string) || memberType == typeof(decimal))
            {
                rendered = value;
                return true;
            }
            if (memberType == typeof(List<string>))
            {
                rendered = value;
                return true;
            }
            if (value is Symbol symbol)
            {
                rendered = RenderSymbol(symbol);
                return true;
            }
            return false;
        }

        // Symbol → 点分字符串（如 "core.collections.List"；泛型参数递归渲染）
        private static string RenderSymbol(Symbol symbol)
        {
            return string.Join(".", symbol.elements.Select(RenderElement));
        }

        private static string RenderElement(SymbolElement element)
        {
            if (element.generics.Count == 0)
            {
                return element.name;
            }
            return element.name + "\\<" + string.Join(", ", element.generics.Select(RenderSymbol)) + ">";
        }
    }
}
