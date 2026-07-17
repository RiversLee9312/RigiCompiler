using System;

namespace LatteCompiler
{
    // 整数类型枚举
    public enum IntType
    {
        I32,    // 默认
        I64,    // L 后缀
        I16,    // S 后缀
        I8,     // B 后缀
        U32,    // U 后缀
        U64,    // UL 后缀
        U16,    // US 后缀
        U8      // UB 后缀
    }

    // 整数字面量 AST 节点
    public class IntLiteralASTNode : ASTNode
    {
        public long Value;
        public IntType IntType;
        public bool IsHex;  // 是否为十六进制

        public IntLiteralASTNode(ASTNode? parent) : base(parent)
        {
        }

        public override ASTNodeType NodeType => ASTNodeType.NumberValue;
    }

    // 浮点数字面量 AST 节点
    public class FloatLiteralASTNode : ASTNode
    {
        public double Value;
        public bool IsFloat;  // true = float, false = double

        public FloatLiteralASTNode(ASTNode? parent) : base(parent)
        {
        }

        public override ASTNodeType NodeType => ASTNodeType.NumberValue;
    }

    // 字符串字面量 AST 节点
    public class StringLiteralASTNode : ASTNode
    {
        public string Value = "";
        public bool HasInterpolation;  // 是否包含字符串插值

        public StringLiteralASTNode(ASTNode? parent) : base(parent)
        {
        }

        public override ASTNodeType NodeType => ASTNodeType.StringValue;
    }

    // 字符字面量 AST 节点
    public class CharLiteralASTNode : ASTNode
    {
        public char Value;

        public CharLiteralASTNode(ASTNode? parent) : base(parent)
        {
        }

        public override ASTNodeType NodeType => ASTNodeType.StringValue;
    }

    // 布尔字面量 AST 节点
    public class BoolLiteralASTNode : ASTNode
    {
        public bool Value;

        public BoolLiteralASTNode(ASTNode? parent) : base(parent)
        {
        }

        public override ASTNodeType NodeType => ASTNodeType.BoolValue;
    }

    // null 字面量 AST 节点
    public class NullLiteralASTNode : ASTNode
    {
        public NullLiteralASTNode(ASTNode? parent) : base(parent)
        {
        }

        public override ASTNodeType NodeType => ASTNodeType.StringValue;
    }
}
