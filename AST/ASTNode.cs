using System;
using System.Collections.Generic;

namespace RigiCompiler
{
    // AST 节点基类。节点类型一律用 CLR 类型判断（is / GetType()），
    // 不再有 ASTNodeType 枚举。子节点成员以 [ChildAstNode] 标注、
    // 父指针以 [ParentAstNode] 标注，供 ASTIntegrityValidator 反射遍历校验。
    public abstract class ASTNode
    {
        // 父节点只能设置一次：构造函数传入，或通过 AttachTo（供 ExpressionRootASTNode.Attach
        // 挂载未挂载表达式子树、DeclarationParserLayer 延迟挂接注解）。
        // 二次设置直接抛异常；禁止任何形式的重挂 Parent。
        [ParentAstNode]
        public ASTNode? Parent { get; private set; }

        // 源码范围（M28）：诊断用。Parser 施工时填充（层目标由主循环按 token 流回填，
        // 层内自建节点由所在层显式设置）；null = 未设置。
        // Validator 校验每个节点均有合法 Span（ExpressionRootASTNode 可透明继承内容表达式）。
        public virtual CharRange? Span { get; set; }

        protected ASTNode(ASTNode? parent)
        {
            Parent = parent;
        }

        // 把一个尚未拥有父节点的节点挂载到 parent（仅限一次）
        internal void AttachTo(ASTNode parent)
        {
            ArgumentNullException.ThrowIfNull(parent);

            if (Parent is not null)
            {
                throw new InvalidOperationException(
                    "AST node already has a parent.");
            }

            Parent = parent;
        }
    }

    public class RootASTNode : ASTNode
    {
        // 只能由内嵌标准库加载器授予；文件名或用户声明不能伪造编译器特权。
        internal bool IsCompilerLibrary { get; set; }
        // 内建声明资源的身份，不按用户提供的 sourceName 推断。
        internal bool IsIntrinsicDeclarations { get; set; }

        // 顶层条目容器：全局声明、import、namespace（以及测试驱动的顶层字面量表达式）
        [ChildAstNode] public List<ASTNode> Declarations = new List<ASTNode>();

        public RootASTNode() : base(null){ }
    }
}
