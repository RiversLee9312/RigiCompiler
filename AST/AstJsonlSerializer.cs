using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace RigiCompiler
{
    /// <summary>
    /// AST → JSONL 序列化（格式 v2，M32：carrier 记录化）：深度优先，每节点一行：
    /// {"id":1,"parent":null,"via":null,"type":"RootASTNode","span":{...},"fields":{...}}
    ///
    /// id 从 1 自增；parent 引用父行 id（根为 null）；via 为挂载成员名
    /// （如 "Left"、"Statements[2]"）。
    ///
    /// carrier 行（v2 新增，解决 ImportItem.importAll 等标量字段往返必丢的问题）：
    /// [AstCarrier] 对象（如 ImportItem）作为独立行产出——
    /// {"id":N,"parent":&lt;宿主节点id&gt;,"via":"importedSymbols[0]"（单 carrier 成员为
    /// 成员名）,"type":"ImportItem","span":null,"fields":{"importAll":true}}；
    /// carrier 携带的 ASTNode 行的 parent 为 carrier 行 id，via 为 carrier 内字段名
    /// （如 "symbolNode"）。宿主节点对子单元的枚举走
    /// AstStructureReflection.EnumerateChildUnits（与 Validator 的
    /// EnumerateChildren 同成员路径、同顺序，仅 carrier 不再下钻）。
    ///
    /// 字段名键控契约：fields 的消费者按字段名取值，不依赖键的出现顺序。
    ///
    /// span 为节点的源码范围（M28）：{"source","startLine","startCol","startOffset",
    /// "endLine","endCol","endOffset"}；无 span 时为 null（解析产物必然有 span；
    /// carrier 行恒为 null）。span 统一为左闭右开 [Start, End)（M31 起）：
    /// End 指向最后一个字符的下一位置。
    ///
    /// fields 收集无标注成员中的标量值（primitive/string/decimal/enum 及其
    /// Nullable 包装/List&lt;string&gt;；M31 修复 int?/long? 字段被静默丢弃），
    /// Symbol 渲染为点分字符串；排除 Parent、[ChildAstNode] 成员与索引器，其余忽略。
    /// 严格先按声明类型过滤再取值——ExpressionRootASTNode.Expression 这类
    /// 未填充即抛异常的属性不是可序列化类型，天然被跳过（M31 修复过滤在取值之后）。
    /// </summary>
    public static class AstJsonlSerializer
    {
        public static void Serialize(ASTNode root, TextWriter writer)
        {
            new JsonlWalker(writer).Walk(root);
        }

        // 遍历器（组合而非继承 ASTVisitor，M32）：carrier 作为独立行产出，
        // 默认遍历（只产 ASTNode 行、carrier 下钻）已不适用；子单元枚举
        // 复用 AstStructureReflection.EnumerateChildUnits，顺序与 Validator 一致。
        private sealed class JsonlWalker
        {
            private readonly TextWriter writer;
            private readonly Dictionary<ASTNode, int> ids = new(ReferenceEqualityComparer.Instance);
            private int nextId = 1;

            public JsonlWalker(TextWriter writer)
            {
                this.writer = writer;
            }

            public void Walk(ASTNode root) => WalkNode(root, parentId: null, via: null);

            // 节点行；parentId 为父行 id（根为 null）。
            // 循环保护（M31）：序列化器自身防重——含环的手工构造 AST
            // （未过 Validator 的调用方）不会无限递归直至栈溢出
            private void WalkNode(ASTNode node, int? parentId, string? via)
            {
                if (ids.ContainsKey(node)) return;
                var id = nextId++;
                ids[node] = id;
                WriteRecord(id, parentId, via, node.GetType().Name,
                    RenderSpan(node.Span), CollectFields(node));

                foreach (var (unit, childVia) in AstStructureReflection.EnumerateChildUnits(node))
                {
                    if (unit is ASTNode child)
                    {
                        WalkNode(child, id, childVia);
                    }
                    else
                    {
                        // [AstCarrier] 单元：独立产行，其携带的节点挂到 carrier 行下
                        WalkCarrier(unit, id, childVia);
                    }
                }
            }

            // carrier 行（span 恒为 null，fields 收 carrier 的标量字段）；
            // 随后深度优先产出其携带的节点（parent = carrier 行 id，via = carrier 内字段名）
            private void WalkCarrier(object carrier, int parentId, string via)
            {
                var id = nextId++;
                WriteRecord(id, parentId, via, carrier.GetType().Name,
                    span: null, CollectFields(carrier));

                foreach (var (carried, fieldName) in AstStructureReflection.EnumerateCarriedNodes(carrier))
                {
                    WalkNode(carried, id, fieldName);
                }
            }

            private void WriteRecord(int id, int? parentId, string? via, string typeName,
                object? span, Dictionary<string, object?> fields)
            {
                var record = new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["parent"] = parentId,
                    ["via"] = via,
                    ["type"] = typeName,
                    ["span"] = span,
                    ["fields"] = fields
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

            // fields：无标注成员中的标量值（节点与 carrier 通用——carrier 的
            // ASTNode 字段同样不是可序列化类型，天然被过滤）。严格「先按声明
            // 类型过滤、通过才取值」（M31 修复：此前先 GetValue 后过滤，
            // ExpressionRootASTNode.Expression 这类未填充即抛异常的属性会在
            // 过滤前被求值）
            private static Dictionary<string, object?> CollectFields(object target)
            {
                var result = new Dictionary<string, object?>();
                var type = target.GetType();

                foreach (var field in AstStructureReflection.GetAllInstanceFields(type))
                {
                    if (field.GetCustomAttribute<ChildAstNodeAttribute>() != null) continue;
                    if (field.GetCustomAttribute<ParentAstNodeAttribute>() != null) continue;
                    // 编译器生成的自动属性 backing 字段（<Name>k__BackingField）：以属性名为准，跳过
                    if (field.Name.StartsWith("<")) continue;
                    if (!IsSerializableMember(field.FieldType)) continue;
                    result[field.Name] = RenderMember(field.FieldType, field.GetValue(target));
                }

                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (prop.GetCustomAttribute<ChildAstNodeAttribute>() != null) continue;
                    if (prop.GetCustomAttribute<ParentAstNodeAttribute>() != null) continue;
                    if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                    if (!IsSerializableMember(prop.PropertyType)) continue;
                    result[prop.Name] = RenderMember(prop.PropertyType, prop.GetValue(target));
                }

                return result;
            }

            // 「该声明类型可序列化」：标量（primitive/string/decimal/enum 及其
            // Nullable 包装——M31 修复 int?/long? 字段被静默丢弃）、List<string>、Symbol
            private static bool IsSerializableMember(Type memberType)
            {
                var effective = Nullable.GetUnderlyingType(memberType) ?? memberType;
                return effective.IsEnum || effective.IsPrimitive ||
                       effective == typeof(string) || effective == typeof(decimal) ||
                       memberType == typeof(List<string>) ||
                       typeof(Symbol).IsAssignableFrom(memberType);
            }

            // 渲染已通过过滤的成员值：enum 为名字、Symbol 为点分字符串、
            // char 为单字符字符串（与 Deserializer 的 char 分支配对——JSON 数字
            // 无法区分 char 与整数），其余原样
            private static object? RenderMember(Type memberType, object? value)
            {
                var effective = Nullable.GetUnderlyingType(memberType) ?? memberType;
                if (effective.IsEnum) return value?.ToString();
                if (effective == typeof(char)) return value?.ToString();
                if (value is Symbol symbol) return RenderSymbol(symbol);
                return value;
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
