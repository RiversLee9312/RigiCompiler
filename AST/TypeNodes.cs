using System;

namespace RigiCompiler
{
    // 类型引用 AST 节点
    // 注意：rich/shared 不属于类型引用！它们是类型声明的修饰符
    public class TypeReferenceASTNode : ASTNode
    {
        [ChildAstNode] public SymbolASTNode TypeSymbol;    // 类型符号（如 String, i32, Container<T>）
        public bool IsNullable;             // 是否可空（T?）

        public TypeReferenceASTNode(ASTNode? parent) : base(parent)
        {
            TypeSymbol = new SymbolASTNode(this);
            IsNullable = false;
        }
    }
}
