using System;
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
    /// AST 完整性验证器（大扫除 §12；M28 基于 ASTVisitor 统一遍历重写）
    ///
    /// Parser 成功后、进入语义分析前运行一次。验证失败抛出 CompilerInternalException
    /// （内部编译器错误，而非用户语法错误）。
    ///
    /// 遍历方式：ASTVisitor——只走以 [ChildAstNode] 标注的字段/属性（反射），
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
    /// 5. switch 表达式满足 default 规则（表达式模式必须有 default 分支）；
    /// 6. 每个节点都有合法源码范围 Span（M28）：非空（ExpressionRoot 可透明继承
    ///    内容表达式）、sourceName 非空、End 不早于 Start；
    ///    Span 统一为左闭右开 [Start, End)（M31 起）：Start 指向首个字符，
    ///    End 指向最后一个字符的下一位置；
    /// 7. 类型审计（M28）：节点类型中「装着 ASTNode」的成员（ASTNode 派生、
    ///    元素为 ASTNode 派生的集合、[AstCarrier] 类型或其集合）必须带
    ///    [ChildAstNode] 或 [ParentAstNode] 标注——防止新增字段忘记标注、
    ///    悄悄逃出遍历与校验；[ChildAstNode] 标在不装 ASTNode 的成员上同样拒绝。
    ///    每种节点类型只审计一次；自动属性经 backing 字段识别（手动实现的
    ///    视图属性如 ExpressionRootASTNode.Expression 不参与审计）。
    ///    字段沿基类链取（M31：基类 private 字段不再漏审计）；[AstCarrier]
    ///    成员递归审计 carrier 类型（M31：carrier 装 ASTNode 的属性会逃出
    ///    遍历——carrier 下钻只读公共字段，必须拒绝）；
    /// 8. Required 子节点非空（M31）：[ChildAstNode(Required = true)] 的必需
    ///    单节点成员不得为 null（LiteralExpressionASTNode.literal、
    ///    CatchClauseASTNode.ExceptionType）。
    ///
    /// 类型引用等声明侧字段的完备性（如 init 映射参数的空 Type 属合法形态）
    /// 不在此处检查，留待语义分析阶段。
    /// </summary>
    public static class ASTIntegrityValidator
    {
        public static void Validate(RootASTNode root)
        {
            var visitor = new ValidatorVisitor();
            visitor.Visit(root);
            visitor.CheckParentChains();
        }

        private sealed class ValidatorVisitor : ASTVisitor
        {
            private readonly HashSet<ASTNode> visited = new(ReferenceEqualityComparer.Instance);
            private readonly List<ASTNode> nodes = new();
            // 类型审计缓存：每种节点类型只审计一次
            private readonly HashSet<Type> auditedTypes = new();

            protected override void OnNode(ASTNode node, ASTNode? parent, string? via)
            {
                // 1. 父子指针一致
                if (!ReferenceEquals(node.Parent, parent))
                {
                    throw new CompilerInternalException(
                        $"AST node has an incorrect parent: {node.GetType().Name} " +
                        $"(expected: {parent?.GetType().Name ?? "<null>"}, " +
                        $"actual: {node.Parent?.GetType().Name ?? "<null>"}" +
                        (via != null ? $", via member: {via}" : "") + ")");
                }

                // 3. 共享检测：同一节点第二次出现即共享错误
                if (!visited.Add(node))
                {
                    throw new CompilerInternalException(
                        $"AST node is shared by multiple parents: {node.GetType().Name}");
                }
                nodes.Add(node);

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

                // 6. Span 检查（M28）
                ValidateSpan(node);

                // 7. 类型审计（M28）
                AuditType(node.GetType());

                // 8. Required 子节点非空（M31）：[ChildAstNode(Required = true)]
                // 的必需单节点成员不得为 null（如 LiteralExpression 未附加字面量）
                foreach (var member in AstStructureReflection.GetChildMembers(node.GetType()))
                {
                    if (member.GetCustomAttribute<ChildAstNodeAttribute>() is not { Required: true })
                        continue;
                    var value = member switch
                    {
                        FieldInfo f => f.GetValue(node),
                        PropertyInfo p => p.GetValue(node),
                        _ => null
                    };
                    if (value == null)
                    {
                        throw new CompilerInternalException(
                            $"Required child node is null: {node.GetType().Name}.{member.Name}");
                    }
                }
            }

            // 4. Parent 链无环（遍历后基于收集到的节点检查）
            public void CheckParentChains()
            {
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

            // 6. 每个节点必须有源码范围：非空、sourceName 非空、End 不早于 Start
            private static void ValidateSpan(ASTNode node)
            {
                if (node.Span is not { } span)
                {
                    throw new CompilerInternalException(
                        $"AST node has no source span: {node.GetType().Name}");
                }
                if (string.IsNullOrEmpty(span.sourceName))
                {
                    throw new CompilerInternalException(
                        $"AST node span has no source name: {node.GetType().Name}");
                }
                if (span.End.line < span.Start.line ||
                    (span.End.line == span.Start.line && span.End.column < span.Start.column) ||
                    span.End.offset < span.Start.offset)
                {
                    throw new CompilerInternalException(
                        $"AST node span is inverted: {node.GetType().Name} " +
                        $"[Line {span.Start.line} Col {span.Start.column}]->[Line {span.End.line} Col {span.End.column}]");
                }
            }

            // 7. 类型审计：装 ASTNode 的成员必须带标注；标注成员必须真的装 ASTNode。
            // 字段沿基类链取（M31：基类 private 字段不再漏审计）；
            // [AstCarrier] 成员递归审计 carrier 类型（M31：carrier 装 ASTNode 的
            // **属性**会逃出遍历——carrier 下钻只读公共字段，必须拒绝）
            private void AuditType(Type type)
            {
                if (!auditedTypes.Add(type)) return;
                bool isCarrier = type.GetCustomAttribute<AstCarrierAttribute>() != null;

                foreach (var field in AstStructureReflection.GetAllInstanceFields(type))
                {
                    // 编译器生成的自动属性 backing 字段（<Name>k__BackingField）：以属性审计为准
                    if (field.Name.StartsWith("<")) continue;
                    AuditMember(type, field.Name, field.FieldType,
                        hasChildAttr: field.GetCustomAttribute<ChildAstNodeAttribute>() != null,
                        hasParentAttr: field.GetCustomAttribute<ParentAstNodeAttribute>() != null,
                        isCarrierMember: isCarrier);
                    AuditCarrierMember(field.FieldType);
                }

                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    // carrier 的属性装 ASTNode 会逃出遍历（carrier 下钻只读字段）：直接拒绝
                    if (isCarrier && AstStructureReflection.MemberHoldsAstNodes(prop.PropertyType))
                    {
                        throw new CompilerInternalException(
                            $"[AstCarrier] type {type.Name} holds AST nodes in property '{prop.Name}' " +
                            "(carrier traversal only reads fields)");
                    }
                    // 只审计自动属性（有编译器生成 backing 字段）：手动实现的视图属性
                    // （如 ExpressionRootASTNode.Expression，数据在已标注的字段上）不参与
                    if (type.GetField($"<{prop.Name}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance) == null) continue;
                    AuditMember(type, prop.Name, prop.PropertyType,
                        hasChildAttr: prop.GetCustomAttribute<ChildAstNodeAttribute>() != null,
                        hasParentAttr: prop.GetCustomAttribute<ParentAstNodeAttribute>() != null,
                        isCarrierMember: isCarrier);
                    AuditCarrierMember(prop.PropertyType);
                }
            }

            // carrier 类型成员递归审计（集合元素为 carrier 同样深入）
            private void AuditCarrierMember(Type memberType)
            {
                if (memberType.GetCustomAttribute<AstCarrierAttribute>() != null)
                {
                    AuditType(memberType);
                    return;
                }
                var elementType = AstStructureReflection.GetCollectionElementType(memberType);
                if (elementType?.GetCustomAttribute<AstCarrierAttribute>() != null)
                {
                    AuditType(elementType);
                }
            }

            private static void AuditMember(Type ownerType, string memberName, Type memberType,
                bool hasChildAttr, bool hasParentAttr, bool isCarrierMember)
            {
                bool holdsAstNodes = AstStructureReflection.MemberHoldsAstNodes(memberType);
                // carrier 的公共字段装 ASTNode 是 carrier 的工作方式本身（无需标注）
                if (holdsAstNodes && !hasChildAttr && !hasParentAttr && !isCarrierMember)
                {
                    throw new CompilerInternalException(
                        $"AST node type {ownerType.Name} has an AST-holding member without annotation: " +
                        $"{memberName} (missing [ChildAstNode] or [ParentAstNode])");
                }
                if (!holdsAstNodes && hasChildAttr)
                {
                    throw new CompilerInternalException(
                        $"[ChildAstNode] member '{memberName}' of {ownerType.Name} " +
                        $"does not hold AST nodes (type: {memberType.Name})");
                }
            }
        }
    }
}
