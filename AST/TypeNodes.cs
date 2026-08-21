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

        // 深拷贝（g1）：符号数据与泛型实参子树递归复制，供 import 前缀展开等
        // 场景重建整棵类型引用。本节点 Parent 为 newParent；嵌套实参的父指针
        // 随新 TypeSymbol 的构造自动就位。Span 为 struct，赋值即拷贝。
        public TypeReferenceASTNode DeepClone(ASTNode newParent)
        {
            var copy = new TypeReferenceASTNode(newParent)
            {
                IsNullable = IsNullable,
                Span = Span,
            };
            copy.TypeSymbol.Span = TypeSymbol.Span;
            copy.TypeSymbol.symbol = TypeSymbol.symbol.DeepClone(copy.TypeSymbol);
            return copy;
        }
    }
}
