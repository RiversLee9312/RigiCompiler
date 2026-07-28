using System;

namespace LatteCompiler
{
    // AST 结构标注（大扫除 Validator 重写）：
    // 用 Attribute 显式声明节点间的父子结构关系，ASTIntegrityValidator
    // 以反射按标注遍历整棵 AST 并校验父子指针一致性。

    /// <summary>
    /// 标记「装子 AST 节点」的字段/属性。
    /// 被标成员的合法值形态：
    ///   - 单个 ASTNode（可空）；
    ///   - ASTNode 集合（如 List&lt;ASTNode&gt;、List&lt;ExpressionRootASTNode&gt;）；
    ///   - [AstCarrier] 对象或其集合（如 List&lt;ImportItem&gt;）——
    ///     Validator 会深入 carrier 的公共实例字段找其中的 ASTNode。
    /// Required = true（M31）表示该单节点成员为解析成功后的必需子节点
    /// （如 LiteralExpressionASTNode.literal、CatchClauseASTNode.ExceptionType），
    /// Validator 逐节点校验其非空。
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class ChildAstNodeAttribute : Attribute
    {
        public bool Required { get; set; }
    }

    /// <summary>
    /// 标记「装父节点」的字段/属性（ASTNode.Parent）。
    /// 仅作结构声明；Validator 遍历时不会沿父指针上溯。
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class ParentAstNodeAttribute : Attribute { }

    /// <summary>
    /// 标记「携带 ASTNode 的非节点对象」（如 ImportItem struct）。
    /// 单独标注无效果：须配合容纳它的成员上的 [ChildAstNode]，
    /// Validator 才会深入该对象的公共实例字段，把其中的 ASTNode
    /// 视为「持有 [ChildAstNode] 成员的节点」的子节点。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class AstCarrierAttribute : Attribute { }
}
