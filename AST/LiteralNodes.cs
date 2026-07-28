using System;

namespace LatteCompiler
{
    // 字面量节点的公共基类：LiteralParserLayer 的施工目标类型
    // （LiteralExpressionASTNode.AttachLiteral 只接受 LiteralASTNode）
    public abstract class LiteralASTNode : ASTNode
    {
        protected LiteralASTNode(ASTNode? parent) : base(parent)
        {
        }
    }

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

    // 整数字面量进制（M31：替代原 IsHex 布尔；SYNTAX §3.3 的 0x/0b/0o 前缀）
    public enum LiteralIntBase
    {
        Decimal,
        Hex,
        Binary,
        Octal
    }

    // 整数字面量 AST 节点
    public class IntLiteralASTNode : LiteralASTNode
    {
        public long Value;
        public IntType IntType;
        public LiteralIntBase Base = LiteralIntBase.Decimal;  // 进制（0x/0b/0o 前缀）

        public IntLiteralASTNode(ASTNode? parent) : base(parent)
        {
        }
    }

    // 浮点数字面量 AST 节点
    public class FloatLiteralASTNode : LiteralASTNode
    {
        public double Value;
        public bool IsFloat;  // true = float, false = double

        public FloatLiteralASTNode(ASTNode? parent) : base(parent)
        {
        }
    }

    // 字符串字面量 AST 节点
    public class StringLiteralASTNode : LiteralASTNode
    {
        public string Value = "";
        public bool HasInterpolation;  // 是否包含字符串插值

        public StringLiteralASTNode(ASTNode? parent) : base(parent)
        {
        }
    }

    // 字符字面量 AST 节点（SYNTAX §3.3：单引号内恰好一个字符或一个转义序列，类型 char）
    public class CharLiteralASTNode : LiteralASTNode
    {
        public char Value;

        public CharLiteralASTNode(ASTNode? parent) : base(parent)
        {
        }
    }

    // 布尔字面量 AST 节点
    public class BoolLiteralASTNode : LiteralASTNode
    {
        public bool Value;

        public BoolLiteralASTNode(ASTNode? parent) : base(parent)
        {
        }
    }

    // null 字面量 AST 节点
    public class NullLiteralASTNode : LiteralASTNode
    {
        public NullLiteralASTNode(ASTNode? parent) : base(parent)
        {
        }
    }
}
