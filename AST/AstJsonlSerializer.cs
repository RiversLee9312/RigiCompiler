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
    /// AST → JSONL 序列化（M28 基于 ASTVisitor 统一遍历重写）：深度优先，每节点一行：
    /// {"id":1,"parent":null,"via":null,"type":"RootASTNode","span":{...},"fields":{...}}
    ///
    /// id 从 1 自增；parent 引用父节点 id（根为 null）；via 为挂载成员名
    /// （如 "Left"、"Children[2]"、carrier 情形 "importedSymbols[0](ImportItem.symbolNode)"）。
    ///
    /// 子节点枚举走 ASTVisitor 的统一实现（[ChildAstNode] 反射下钻）。
    ///
    /// span 为节点的源码范围（M28）：{"source","startLine","startCol","startOffset",
    /// "endLine","endCol","endOffset"}；无 span 时为 null（解析产物必然有 span）。
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
            new JsonlVisitor(writer).Visit(root);
        }

        private sealed class JsonlVisitor : ASTVisitor
        {
            private readonly TextWriter writer;
            private readonly Dictionary<ASTNode, int> ids = new(ReferenceEqualityComparer.Instance);
            private int nextId = 1;

            public JsonlVisitor(TextWriter writer)
            {
                this.writer = writer;
            }

            protected override void OnNode(ASTNode node, ASTNode? parent, string? via)
            {
                var id = nextId++;
                ids[node] = id;
                var record = new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["parent"] = parent != null ? ids[parent] : (int?)null,
                    ["via"] = via,
                    ["type"] = node.GetType().Name,
                    ["span"] = RenderSpan(node.Span),
                    ["fields"] = CollectFields(node)
                };
                writer.WriteLine(JsonSerializer.Serialize(record));
            }

            // span：源码范围渲染为对象；null 表示无范围
            private static object? RenderSpan(CharRange? span)
            {
                if (span is not { } s) return null;
                return new Dictionary<string, object?>
                {
                    ["source"] = s.sourceName,
                    ["startLine"] = s.Start.line,
                    ["startCol"] = s.Start.column,
                    ["startOffset"] = s.Start.offset,
                    ["endLine"] = s.End.line,
                    ["endCol"] = s.End.column,
                    ["endOffset"] = s.End.offset
                };
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
}
