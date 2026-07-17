using System;
using System.Collections.Generic;

namespace LatteCompiler
{
    // 表达式基类
    public abstract class ExpressionASTNode : ASTNode
    {
        protected ExpressionASTNode(ASTNode? parent) : base(parent) { }
    }

    // 二元运算表达式
    public class BinaryExpressionASTNode : ExpressionASTNode
    {
        public ExpressionASTNode Left;
        public ExpressionASTNode Right;
        public string Operator;  // +, -, *, /, and, or, ==, !=, etc.

        public BinaryExpressionASTNode(ASTNode? parent) : base(parent)
        {
            Left = null!;
            Right = null!;
            Operator = "";
        }

        public override ASTNodeType NodeType => ASTNodeType.TwoValueExpression;
    }

    // 一元运算表达式
    public class UnaryExpressionASTNode : ExpressionASTNode
    {
        public ExpressionASTNode Operand;
        public string Operator;  // -, not, await
        public bool IsPrefix;    // true = 前缀, false = 后缀

        public UnaryExpressionASTNode(ASTNode? parent) : base(parent)
        {
            Operand = null!;
            Operator = "";
            IsPrefix = true;
        }

        public override ASTNodeType NodeType => ASTNodeType.OneValueExpression;
    }

    // 字面量表达式（包装已有的字面量节点）
    public class LiteralExpressionASTNode : ExpressionASTNode
    {
        public ASTNode LiteralNode;  // IntLiteralASTNode, StringLiteralASTNode, etc.

        public LiteralExpressionASTNode(ASTNode? parent, ASTNode literalNode) : base(parent)
        {
            LiteralNode = literalNode;
        }

        public override ASTNodeType NodeType => LiteralNode.NodeType;
    }

    // 符号引用表达式（变量、函数调用等）
    public class SymbolReferenceASTNode : ExpressionASTNode
    {
        public SymbolASTNode Symbol;

        public SymbolReferenceASTNode(ASTNode? parent) : base(parent)
        {
            Symbol = new SymbolASTNode(this);
        }

        public override ASTNodeType NodeType => ASTNodeType.Symbol;
    }

    // 括号分组表达式
    public class GroupExpressionASTNode : ExpressionASTNode
    {
        public ExpressionASTNode InnerExpression;

        public GroupExpressionASTNode(ASTNode? parent) : base(parent)
        {
            InnerExpression = null!;
        }

        public override ASTNodeType NodeType => ASTNodeType.ValueExpressionRoot;
    }

    // new 表达式
    public class NewExpressionASTNode : ExpressionASTNode
    {
        public TypeReferenceASTNode Type;
        public List<ArgumentASTNode> Arguments;

        public NewExpressionASTNode(ASTNode? parent) : base(parent)
        {
            Type = new TypeReferenceASTNode(this);
            Arguments = new List<ArgumentASTNode>();
        }

        public override ASTNodeType NodeType => ASTNodeType.NewExpression;
    }

    // 调用/索引/构造实参（可具名，如 foo(name = 42)）
    public class ArgumentASTNode : ASTNode
    {
        public string? Name;           // 具名实参名；位置实参为 null
        public ExpressionASTNode Value;

        public ArgumentASTNode(ASTNode? parent, ExpressionASTNode value, string? name = null) : base(parent)
        {
            Name = name;
            Value = value;
        }

        public override ASTNodeType NodeType => ASTNodeType.Argument;
    }

    // 函数调用表达式
    public class CallExpressionASTNode : ExpressionASTNode
    {
        public ExpressionASTNode Callee;  // 被调用的表达式
        public List<ArgumentASTNode> Arguments;

        public CallExpressionASTNode(ASTNode? parent) : base(parent)
        {
            Callee = null!;
            Arguments = new List<ArgumentASTNode>();
        }

        public override ASTNodeType NodeType => ASTNodeType.InvokeStatemnt;
    }

    // 索引访问表达式
    public class IndexExpressionASTNode : ExpressionASTNode
    {
        public ExpressionASTNode Object;
        public List<ArgumentASTNode> Indices;

        public IndexExpressionASTNode(ASTNode? parent) : base(parent)
        {
            Object = null!;
            Indices = new List<ArgumentASTNode>();
        }

        public override ASTNodeType NodeType => ASTNodeType.AcquisitionExpression;
    }

    // 成员访问表达式
    public class MemberAccessASTNode : ExpressionASTNode
    {
        public ExpressionASTNode Object;
        public string MemberName;
        public bool IsSafeAccess;  // ?. 安全访问
        public List<TypeReferenceASTNode> GenericArguments;  // 泛型实参（foo().bar\<i32>）

        public MemberAccessASTNode(ASTNode? parent) : base(parent)
        {
            Object = null!;
            MemberName = "";
            IsSafeAccess = false;
            GenericArguments = new List<TypeReferenceASTNode>();
        }

        public override ASTNodeType NodeType => ASTNodeType.AcquisitionExpression;
    }
}
