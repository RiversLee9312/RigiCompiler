using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RigiCompiler
{
    /// <summary>
    /// JSONL → AST 反序列化（格式 v2，M32）：AstJsonlSerializer 的逆操作，
    /// 目标为往返无损（Parse → Serialize → Deserialize → Serialize 两次输出逐行一致）。
    ///
    /// 字段名键控契约：fields 按字段名匹配成员取值，不依赖键的出现顺序；
    /// 找不到的成员直接报错（格式与类型漂移时应当暴露，而非静默吞掉）。
    ///
    /// 输入行为约定：一行一条记录；空行跳过；多文件 dump 的分隔元记录
    /// {"file":"..."}（无 id 键）跳过；输入本身为单棵树（深度优先、父先于子）。
    ///
    /// 三阶段实现：
    /// 1. 逐行解析 JSON 为记录（id/parentId/via/type/span/fields），type 名
    ///    在本程序集 RigiCompiler 命名空间下定位（ASTNode 派生类或 [AstCarrier]
    ///    类型，缓存字典）；
    /// 2. 自根向下创建实例并挂接：
    ///    - parent=null → RootASTNode（唯一）；
    ///    - via 单节点成员：现值非 null 且类型匹配 → 复用（构造时预创建的子容器，
    ///      如 Left/TypeSymbol），为 null 才新建实例赋给成员；
    ///    - via member[i] 集合元素：新建实例 Add（下标必须连续）；
    ///    - carrier 行：new carrier 加入宿主 IList / 赋给单 carrier 成员，
    ///      记录 id → CarrierSlot（struct carrier 的后续修改必须写回宿主）；
    ///    - via 为 carrier 内字段名（parent 是 carrier 行）：新建子节点赋给
    ///      carrier 字段后写回宿主；子节点的 AST 父节点是 carrier 的宿主节点
    ///      （Validator 约定：ImportItem.symbolNode 的 Parent 是 ImportASTNode）；
    /// 3. 回填 fields（按声明类型转换：number→long/int 等、bool、string、
    ///    enum 名→Enum.Parse、List&lt;string&gt;、Symbol 点分串→Symbol 递归解析）
    ///    与 span（source→sourceName，start*/end*→Start/End）。
    ///
    /// 收尾跑 ASTIntegrityValidator.Validate——产物必须过完整性验证
    /// （Parent 链、Required、span 等）。格式非法（缺键、未知 type、via 无法
    /// 定位、id 引用悬空等）一律抛 CompilerInternalException（带行号与原因），
    /// 不接受部分成功。
    /// </summary>
    public static class AstJsonlDeserializer
    {
        public static RootASTNode Deserialize(TextReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);

            var records = ParseRecords(reader);                  // 阶段 1：行 → 记录
            var (root, byId) = BuildTree(records);               // 阶段 2：建实例 + 挂接
            FillFieldsAndSpans(records, byId);                   // 阶段 3：回填标量与 span

            // 产物完整性验证（Parent 链 / Root 已填充 / Required / span 合法等）
            ASTIntegrityValidator.Validate(root);
            return root;
        }

        // ===== 记录模型 =====

        private sealed class JsonlRecord
        {
            public required int LineNo { get; init; }
            public required int Id { get; init; }
            public required int? ParentId { get; init; }
            public required string? Via { get; init; }
            public required Type Type { get; init; }
            public required bool IsCarrier { get; init; }
            public required JsonElement? SpanJson { get; init; }   // null = JSON null（carrier 行）
            public required JsonElement FieldsJson { get; init; }
        }

        // carrier 行的施工槽：struct carrier（ImportItem）是值类型，字段修改
        // 作用于装箱副本 Box 后必须经 WriteBack 写回宿主（List 下标赋值或成员赋值）
        private sealed class CarrierSlot
        {
            public readonly object Box;
            public readonly ASTNode Host;
            private readonly Action<object> writeBack;

            public CarrierSlot(object box, ASTNode host, Action<object> writeBack)
            {
                Box = box;
                Host = host;
                this.writeBack = writeBack;
            }

            public void WriteBack() => writeBack(Box);
        }

        // ===== 阶段 1：逐行解析 =====

        private static List<JsonlRecord> ParseRecords(TextReader reader)
        {
            var records = new List<JsonlRecord>();
            string? line;
            int lineNo = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNo++;
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException ex)
                {
                    throw Error(lineNo, $"invalid JSON: {ex.Message}");
                }
                using (doc)
                {
                    var record = doc.RootElement;
                    if (record.ValueKind != JsonValueKind.Object)
                    {
                        throw Error(lineNo, "record must be a JSON object");
                    }
                    if (!record.TryGetProperty("id", out var idEl))
                    {
                        // {"file":...} 元记录（多文件 dump 分隔行）：跳过
                        if (record.TryGetProperty("file", out _)) continue;
                        throw Error(lineNo, "record is missing the 'id' key");
                    }
                    if (!record.TryGetProperty("parent", out var parentEl) ||
                        !record.TryGetProperty("via", out var viaEl) ||
                        !record.TryGetProperty("type", out var typeEl) ||
                        !record.TryGetProperty("span", out var spanEl) ||
                        !record.TryGetProperty("fields", out var fieldsEl))
                    {
                        throw Error(lineNo,
                            "record is missing required keys (id/parent/via/type/span/fields)");
                    }
                    if (idEl.ValueKind != JsonValueKind.Number)
                    {
                        throw Error(lineNo, "'id' must be a number");
                    }
                    if (parentEl.ValueKind != JsonValueKind.Null &&
                        parentEl.ValueKind != JsonValueKind.Number)
                    {
                        throw Error(lineNo, "'parent' must be a number or null");
                    }
                    if (viaEl.ValueKind != JsonValueKind.Null &&
                        viaEl.ValueKind != JsonValueKind.String)
                    {
                        throw Error(lineNo, "'via' must be a string or null");
                    }
                    if (fieldsEl.ValueKind != JsonValueKind.Object)
                    {
                        throw Error(lineNo, "'fields' must be an object");
                    }

                    var typeName = typeEl.GetString();
                    if (string.IsNullOrEmpty(typeName))
                    {
                        throw Error(lineNo, "'type' must be a non-empty string");
                    }
                    var type = ResolveType(typeName, lineNo);

                    records.Add(new JsonlRecord
                    {
                        LineNo = lineNo,
                        Id = idEl.GetInt32(),
                        ParentId = parentEl.ValueKind == JsonValueKind.Null
                            ? null : parentEl.GetInt32(),
                        Via = viaEl.ValueKind == JsonValueKind.Null
                            ? null : viaEl.GetString(),
                        Type = type,
                        IsCarrier = type.GetCustomAttribute<AstCarrierAttribute>() != null,
                        SpanJson = spanEl.ValueKind == JsonValueKind.Null
                            ? null : spanEl.Clone(),
                        FieldsJson = fieldsEl.Clone()
                    });
                }
            }
            if (records.Count == 0)
            {
                throw Error(0, "input contains no records");
            }
            return records;
        }

        // type 名 → 类型（本程序集 RigiCompiler 命名空间下的非抽象 ASTNode
        // 派生类或 [AstCarrier] 类型；缓存）
        private static readonly Dictionary<string, Type> TypeMap = BuildTypeMap();

        private static Dictionary<string, Type> BuildTypeMap()
        {
            var map = new Dictionary<string, Type>();
            foreach (var type in typeof(RootASTNode).Assembly.GetTypes())
            {
                if (type.Namespace != typeof(RootASTNode).Namespace) continue;
                if (type.IsAbstract) continue;
                if (typeof(ASTNode).IsAssignableFrom(type) ||
                    type.GetCustomAttribute<AstCarrierAttribute>() != null)
                {
                    map[type.Name] = type;
                }
            }
            return map;
        }

        private static Type ResolveType(string typeName, int lineNo)
        {
            if (!TypeMap.TryGetValue(typeName, out var type))
            {
                throw Error(lineNo, $"unknown AST node/carrier type: '{typeName}'");
            }
            return type;
        }

        // ===== 阶段 2：创建实例并挂接 =====

        private static (RootASTNode Root, Dictionary<int, object> ById) BuildTree(
            List<JsonlRecord> records)
        {
            RootASTNode? root = null;
            var byId = new Dictionary<int, object>();
            foreach (var rec in records)
            {
                if (byId.ContainsKey(rec.Id))
                {
                    throw Error(rec.LineNo, $"duplicate id {rec.Id}");
                }
                object instance;
                if (rec.ParentId == null)
                {
                    if (rec.Via != null)
                    {
                        throw Error(rec.LineNo, "root record must have null via");
                    }
                    if (root != null)
                    {
                        throw Error(rec.LineNo, "multiple root records (parent=null)");
                    }
                    if (rec.Type != typeof(RootASTNode))
                    {
                        throw Error(rec.LineNo,
                            $"root record type must be RootASTNode, got {rec.Type.Name}");
                    }
                    root = new RootASTNode();
                    instance = root;
                }
                else
                {
                    if (!byId.TryGetValue(rec.ParentId.Value, out var parent))
                    {
                        throw Error(rec.LineNo, $"dangling parent id {rec.ParentId.Value}");
                    }
                    instance = AttachChild(rec, parent);
                }
                byId[rec.Id] = instance;
            }
            if (root == null)
            {
                throw Error(records[0].LineNo, "no root record (parent=null) found");
            }
            return (root, byId);
        }

        // via 解析：成员名 + 可选 [i] 下标（如 "Left"、"Statements[2]"、"symbolNode"）
        private static readonly Regex ViaPattern =
            new(@"^([^\[]+)(?:\[(\d+)\])?$", RegexOptions.Compiled);

        private static object AttachChild(JsonlRecord rec, object parent)
        {
            if (rec.Via == null)
            {
                throw Error(rec.LineNo, "non-root record must have a non-null via");
            }
            var match = ViaPattern.Match(rec.Via);
            if (!match.Success)
            {
                throw Error(rec.LineNo, $"malformed via: '{rec.Via}'");
            }
            var memberName = match.Groups[1].Value;
            int? index = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : null;

            // 父行是 carrier：via 为 carrier 内字段名（如 "symbolNode"）。
            // 子节点的 AST 父节点是 carrier 的宿主节点（Validator 约定），
            // 字段赋值后写回宿主（struct carrier）
            if (parent is CarrierSlot slot)
            {
                if (rec.IsCarrier)
                {
                    throw Error(rec.LineNo, "carrier record cannot be a child of a carrier");
                }
                if (index != null)
                {
                    throw Error(rec.LineNo, "carrier field via must not have an index");
                }
                var carrierField = slot.Box.GetType().GetField(
                    memberName, BindingFlags.Public | BindingFlags.Instance);
                if (carrierField == null || !typeof(ASTNode).IsAssignableFrom(carrierField.FieldType))
                {
                    throw Error(rec.LineNo,
                        $"carrier {slot.Box.GetType().Name} has no AST node field '{memberName}'");
                }
                if (carrierField.GetValue(slot.Box) is ASTNode carried &&
                    carried.GetType() == rec.Type)
                {
                    return carried;   // 复用（理论情形：carrier 预创建子节点）
                }
                var node = CreateNode(rec.Type, slot.Host, rec.LineNo);
                carrierField.SetValue(slot.Box, node);
                slot.WriteBack();
                return node;
            }

            var host = (ASTNode)parent;
            var member = AstStructureReflection.GetChildMembers(host.GetType())
                .FirstOrDefault(m => m.Name == memberName);
            if (member == null)
            {
                throw Error(rec.LineNo,
                    $"{host.GetType().Name} has no [ChildAstNode] member '{memberName}'");
            }
            var memberValue = ReadMember(host, member);

            if (rec.IsCarrier)
            {
                // carrier 行：new carrier 加入宿主集合 / 赋给单 carrier 成员
                if (index != null)
                {
                    var list = GetOrCreateList(host, member, memberValue, rec.LineNo);
                    var carrier = Activator.CreateInstance(rec.Type)!;
                    list.Add(carrier);
                    if (list.Count - 1 != index.Value)
                    {
                        throw Error(rec.LineNo,
                            $"carrier index mismatch: expected {list.Count - 1}, got {index.Value}");
                    }
                    int slotIndex = index.Value;
                    return new CarrierSlot(carrier, host, boxed => list[slotIndex] = boxed);
                }
                var single = memberValue != null && memberValue.GetType() == rec.Type
                    ? memberValue
                    : Activator.CreateInstance(rec.Type)!;
                WriteMember(host, member, single, rec.LineNo);
                return new CarrierSlot(single, host,
                    boxed => WriteMember(host, member, boxed, rec.LineNo));
            }

            if (index != null)
            {
                // 集合元素：新建实例 Add（下标必须连续——成员列表构造时为空）
                var nodeList = GetOrCreateList(host, member, memberValue, rec.LineNo);
                var element = CreateNode(rec.Type, host, rec.LineNo);
                nodeList.Add(element);
                if (nodeList.Count - 1 != index.Value)
                {
                    throw Error(rec.LineNo,
                        $"element index mismatch: expected {nodeList.Count - 1}, got {index.Value}");
                }
                return element;
            }

            // 单节点成员落位前的类型校验：成员类型必须装得下记录类型——
            // 集合成员（via 缺 [i] 下标）或类型漂移在此以带行号的错误拒绝，
            // 而不是让 WriteMember 的 field.SetValue 抛裸 ArgumentException
            var memberType = member switch
            {
                FieldInfo field => field.FieldType,
                PropertyInfo prop => prop.PropertyType,
                _ => null
            };
            if (memberType == null || !memberType.IsAssignableFrom(rec.Type))
            {
                throw Error(rec.LineNo,
                    $"member '{memberName}' of {host.GetType().Name} cannot hold {rec.Type.Name} " +
                    $"(member type: {memberType?.Name ?? "unknown"}; collections require an [i] index)");
            }

            // 单节点成员：现值非 null 且类型匹配 → 复用（构造时预创建的子容器，
            // 如 BinaryExpressionASTNode.Left、TypeReferenceASTNode.TypeSymbol；
            // get-only 属性天然走这条路）；为 null 才新建实例赋给成员
            if (memberValue is ASTNode existing)
            {
                if (existing.GetType() != rec.Type)
                {
                    throw Error(rec.LineNo,
                        $"member '{memberName}' already holds {existing.GetType().Name}, " +
                        $"record type is {rec.Type.Name}");
                }
                return existing;
            }
            var created = CreateNode(rec.Type, host, rec.LineNo);
            WriteMember(host, member, created, rec.LineNo);
            return created;
        }

        // 节点实例创建：优先无参构造（表达式节点多为无参 + 预创建子容器，
        // Parent 随后由 AttachTo 设置）；否则取单 ASTNode 参数构造传入父节点
        private static ASTNode CreateNode(Type type, ASTNode? parent, int lineNo)
        {
            var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            ASTNode node;
            var parameterless = ctors.FirstOrDefault(c => c.GetParameters().Length == 0);
            if (parameterless != null)
            {
                node = (ASTNode)parameterless.Invoke(null);
            }
            else
            {
                var parentCtor = ctors.FirstOrDefault(c =>
                {
                    var ps = c.GetParameters();
                    return ps.Length == 1 && typeof(ASTNode).IsAssignableFrom(ps[0].ParameterType);
                });
                if (parentCtor == null)
                {
                    throw Error(lineNo, $"no usable constructor on AST node type {type.Name}");
                }
                node = (ASTNode)parentCtor.Invoke(new object?[] { parent });
            }
            // 无参构造的节点 Parent 为 null：挂到父节点（Parent 只能设置一次）；
            // 带 parent 构造的节点 Parent 已是目标父节点，无需再挂
            if (node.Parent == null && parent != null)
            {
                node.AttachTo(parent);
            }
            return node;
        }

        private static object? ReadMember(object target, MemberInfo member)
        {
            return member switch
            {
                FieldInfo field => field.GetValue(target),
                PropertyInfo prop => prop.GetValue(target),
                _ => null
            };
        }

        // null 列表成员按需创建并写回（如 StringLiteralASTNode.InterpolationParts：
        // null 是「无插值」语义、构造时不预初始化——反序列化遇元素/carrier 行时
        // 才物化列表）；非 null 非 IList 与不可创建类型维持原报错
        private static IList GetOrCreateList(ASTNode host, MemberInfo member,
            object? memberValue, int lineNo)
        {
            if (memberValue is IList list) return list;
            if (memberValue != null)
            {
                throw Error(lineNo, $"member '{member.Name}' is not a list");
            }
            var memberType = member switch
            {
                FieldInfo field => field.FieldType,
                PropertyInfo prop => prop.PropertyType,
                _ => null
            };
            if (memberType == null || !typeof(IList).IsAssignableFrom(memberType)
                || Activator.CreateInstance(memberType) is not IList created)
            {
                throw Error(lineNo, $"member '{member.Name}' is not a list");
            }
            WriteMember(host, member, created, lineNo);
            return created;
        }

        private static void WriteMember(object target, MemberInfo member, object? value, int lineNo)
        {
            switch (member)
            {
                case FieldInfo field:
                    field.SetValue(target, value);
                    break;
                case PropertyInfo prop when prop.CanWrite:
                    prop.SetValue(target, value);
                    break;
                default:
                    throw Error(lineNo,
                        $"member '{member.Name}' of {target.GetType().Name} is not assignable");
            }
        }

        // ===== 阶段 3：回填 fields 与 span =====

        private static void FillFieldsAndSpans(List<JsonlRecord> records, Dictionary<int, object> byId)
        {
            foreach (var rec in records)
            {
                var target = byId[rec.Id];
                if (target is CarrierSlot slot)
                {
                    // carrier 行无 span（恒为 null）
                    if (rec.SpanJson != null)
                    {
                        throw Error(rec.LineNo, "carrier record must have null span");
                    }
                    ApplyFields(slot.Box, rec);
                    slot.WriteBack();
                    continue;
                }
                var node = (ASTNode)target;
                if (rec.SpanJson is { } spanJson)
                {
                    node.Span = ParseSpan(spanJson, rec.LineNo);
                }
                ApplyFields(node, rec);
            }
        }

        // fields 按键名匹配字段/属性并赋值（字段名键控，不依赖顺序）；
        // 找不到成员即报错（格式与类型漂移必须暴露）
        private static void ApplyFields(object target, JsonlRecord rec)
        {
            var type = target.GetType();
            foreach (var jsonProp in rec.FieldsJson.EnumerateObject())
            {
                var member = FindDataMember(type, jsonProp.Name);
                if (member == null)
                {
                    throw Error(rec.LineNo,
                        $"{type.Name} has no field/property named '{jsonProp.Name}' " +
                        "(format/type drift)");
                }
                var memberType = member is FieldInfo field
                    ? field.FieldType : ((PropertyInfo)member).PropertyType;
                var value = ConvertValue(memberType, jsonProp.Value, rec.LineNo, jsonProp.Name, target);

                if (member is PropertyInfo prop && !prop.CanWrite)
                {
                    // get-only List<string> 成员：原地填充（构造时预创建的列表）；
                    // 其余只读属性是派生视图（如 ExpressionRootASTNode.IsAttached），
                    // 值由结构决定，无需也无法赋值——跳过
                    if (value is List<string> strings &&
                        prop.GetValue(target) is List<string> existing)
                    {
                        existing.Clear();
                        existing.AddRange(strings);
                    }
                    continue;
                }
                WriteMember(target, member, value, rec.LineNo);
            }
        }

        // 字段/属性按键名查找（沿基类链取字段；跳过自动属性 backing 字段与索引器）
        private static MemberInfo? FindDataMember(Type type, string name)
        {
            var field = AstStructureReflection.GetAllInstanceFields(type)
                .FirstOrDefault(f => !f.Name.StartsWith("<") && f.Name == name);
            if (field != null) return field;
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.Name == name && p.GetIndexParameters().Length == 0);
        }

        // JSON 值 → 声明类型的 CLR 值：enum 名→Enum.Parse、数字→long/int 等、
        // bool、string、List<string>、Symbol 点分串→Symbol 递归解析。
        // target：字段所属对象——Symbol 的泛型实参是 AST 节点（g1），
        // 需要宿主 SymbolASTNode 作为实参子树的父节点
        private static object? ConvertValue(Type memberType, JsonElement json,
            int lineNo, string name, object target)
        {
            try
            {
                if (json.ValueKind == JsonValueKind.Null) return null;
                var effective = Nullable.GetUnderlyingType(memberType) ?? memberType;
                if (effective.IsEnum)
                {
                    return Enum.Parse(effective, json.GetString()!);
                }
                if (effective == typeof(string)) return json.GetString();
                // char 序列化为单字符字符串（与 Serializer 的 RenderMember 配对）
                if (effective == typeof(char))
                {
                    var s = json.GetString()!;
                    if (s.Length != 1)
                        throw new FormatException($"expected single character, got '{s}'");
                    return s[0];
                }
                if (effective == typeof(bool)) return json.GetBoolean();
                if (effective == typeof(decimal)) return json.GetDecimal();
                if (effective == typeof(double)) return json.GetDouble();
                if (effective == typeof(float)) return json.GetSingle();
                if (effective.IsPrimitive)
                {
                    return Convert.ChangeType(json.GetInt64(), effective);
                }
                if (memberType == typeof(List<string>))
                {
                    return json.EnumerateArray().Select(e => e.GetString()!).ToList();
                }
                if (typeof(Symbol).IsAssignableFrom(memberType))
                {
                    if (target is not SymbolASTNode symbolHost)
                    {
                        throw Error(lineNo,
                            $"field '{name}': Symbol value requires a SymbolASTNode host " +
                            $"(generic arguments are AST nodes), got {target.GetType().Name}");
                    }
                    return ParseSymbol(json.GetString()!, lineNo, symbolHost);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException
                or OverflowException or ArgumentException)
            {
                throw Error(lineNo,
                    $"field '{name}': value does not match declared type {memberType.Name} " +
                    $"({ex.Message})");
            }
            throw Error(lineNo, $"field '{name}': unsupported declared type {memberType.Name}");
        }

        // span 对象 → CharRange（source→sourceName，start*/end*→Start/End）
        private static CharRange ParseSpan(JsonElement json, int lineNo)
        {
            try
            {
                return new CharRange
                {
                    sourceName = json.GetProperty("source").GetString() ?? "",
                    Start = new CharPosition
                    {
                        line = json.GetProperty("startLine").GetInt64(),
                        column = json.GetProperty("startCol").GetInt32(),
                        offset = json.GetProperty("startOffset").GetInt64()
                    },
                    End = new CharPosition
                    {
                        line = json.GetProperty("endLine").GetInt64(),
                        column = json.GetProperty("endCol").GetInt32(),
                        offset = json.GetProperty("endOffset").GetInt64()
                    }
                };
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
            {
                throw Error(lineNo, $"malformed span object ({ex.Message})");
            }
        }

        // ===== Symbol 点分串解析（与 AstJsonlSerializer.RenderSymbol 互逆）=====
        //   symbol  := element ('.' element)*
        //   element := name ('\<' typeArg (', ' typeArg)* '>')?
        //   typeArg := symbol '?'?
        // 泛型实参是完整类型引用节点（g1）：可空后缀 ? 随实参解析；
        // 嵌套闭合 ">>" 由内外两层各自消费一个 '>'。
        // owner：持有本符号的 SymbolASTNode——实参节点以其为父（Validator 校验
        // 父子指针一致），span 复用宿主 span（字符串形态不携带实参级 span）
        private static Symbol ParseSymbol(string text, int lineNo, SymbolASTNode owner)
        {
            var pos = 0;
            var symbol = ParseSymbolBody(text, ref pos, lineNo, owner);
            if (pos != text.Length)
            {
                throw Error(lineNo,
                    $"malformed Symbol string (trailing content at {pos}): '{text}'");
            }
            return symbol;
        }

        // 点分元素序列；在泛型实参列表内遇到 ',' 或 '>' 自然结束
        private static Symbol ParseSymbolBody(string text, ref int pos, int lineNo,
            SymbolASTNode owner)
        {
            var symbol = new Symbol();
            while (true)
            {
                symbol.elements.Add(ParseSymbolElement(text, ref pos, lineNo, owner));
                if (pos < text.Length && text[pos] == '.')
                {
                    pos++;
                    continue;
                }
                return symbol;
            }
        }

        private static SymbolElement ParseSymbolElement(string text, ref int pos, int lineNo,
            SymbolASTNode owner)
        {
            SkipSpaces(text, ref pos);
            var start = pos;
            while (pos < text.Length && text[pos] != '.' && text[pos] != ',' &&
                   text[pos] != '>' && text[pos] != '\\' && text[pos] != '?')
            {
                pos++;
            }
            var name = text[start..pos].TrimEnd();
            if (name.Length == 0)
            {
                throw Error(lineNo, $"malformed Symbol string (empty element name): '{text}'");
            }
            var element = new SymbolElement { name = name };

            // 泛型实参列表以 "\<" 开启、'>' 闭合（SYNTAX §3.6）
            if (pos + 1 < text.Length && text[pos] == '\\' && text[pos + 1] == '<')
            {
                pos += 2;
                while (true)
                {
                    var argument = new TypeReferenceASTNode(owner) { Span = owner.Span };
                    argument.TypeSymbol.Span = owner.Span;
                    argument.TypeSymbol.symbol = ParseSymbolBody(text, ref pos, lineNo,
                        argument.TypeSymbol);
                    // 实参可空后缀（g1：Holder\<i32?>）
                    if (pos < text.Length && text[pos] == '?')
                    {
                        argument.IsNullable = true;
                        pos++;
                    }
                    element.generics.Add(argument);
                    SkipSpaces(text, ref pos);
                    if (pos < text.Length && text[pos] == ',')
                    {
                        pos++;
                        continue;
                    }
                    if (pos < text.Length && text[pos] == '>')
                    {
                        pos++;
                        break;
                    }
                    throw Error(lineNo,
                        $"malformed Symbol string (unterminated generic list): '{text}'");
                }
            }
            return element;
        }

        private static void SkipSpaces(string text, ref int pos)
        {
            while (pos < text.Length && text[pos] == ' ') pos++;
        }

        private static CompilerInternalException Error(int lineNo, string reason)
        {
            return new CompilerInternalException($"Invalid AST JSONL (line {lineNo}): {reason}");
        }
    }
}
